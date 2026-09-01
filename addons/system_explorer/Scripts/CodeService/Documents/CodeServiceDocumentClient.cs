#if TOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SystemExplorer.CodeService.Client;

namespace SystemExplorer.CodeService.Documents;

internal sealed class CodeServiceDocumentClient
{
	private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);
	private HttpClient _httpClient;
	private CodeServiceClientCredentials _credentials;
	private readonly string _sessionId;

	internal CodeServiceDocumentClient(
		HttpClient httpClient,
		CodeServiceClientCredentials credentials,
		string sessionId
	)
	{
		_httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
		_credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
		_sessionId = string.IsNullOrWhiteSpace(sessionId)
			? throw new ArgumentException("Session id is required.", nameof(sessionId))
			: sessionId;
	}

	internal void CloseAdmission()
	{
		Interlocked.Exchange(ref _httpClient, null);
		Interlocked.Exchange(ref _credentials, null);
	}

	internal async Task<CodeServiceDocumentEpochResult> ReconcileEpochAsync(
		long clientGeneration,
		string epochId,
		IReadOnlyList<string> openDocumentPaths,
		CancellationToken cancellationToken
	)
	{
		if (clientGeneration <= 0 || !TryGetCanonicalGuid(epochId, out _))
			return CodeServiceDocumentEpochResult.Failure(CodeServiceDocumentOutcome.LocalInvalidRequest, "Invalid document authority.");
		if (openDocumentPaths == null)
			return CodeServiceDocumentEpochResult.Failure(CodeServiceDocumentOutcome.LocalInvalidRequest, "Open document paths are required.");
		if (openDocumentPaths.Count > CodeServiceDocumentSynchronizationLimits.MaxTrackedOpenDocuments)
			return CodeServiceDocumentEpochResult.Failure(CodeServiceDocumentOutcome.LocalCapacityExceeded, "Open document count exceeds the local bound.");

		HashSet<string> unique = new(CodeServiceDocumentPath.PlatformComparer);
		foreach (string path in openDocumentPaths)
		{
			if (!CodeServiceDocumentPath.TryValidateWirePath(path, out string pathDetail))
				return CodeServiceDocumentEpochResult.Failure(CodeServiceDocumentOutcome.LocalInvalidRequest, pathDetail);
			if (!unique.Add(path))
				return CodeServiceDocumentEpochResult.Failure(CodeServiceDocumentOutcome.LocalInvalidRequest, "Open document paths must be unique for the platform comparer.");
		}

		byte[] body;
		try
		{
			using MemoryStream stream = new();
			using (Utf8JsonWriter writer = new(stream))
			{
				writer.WriteStartObject();
				writer.WriteNumber("schemaVersion", CodeServiceClientProtocol.DocumentSynchronizationSchemaVersion);
				writer.WriteNumber("clientGeneration", clientGeneration);
				writer.WriteString("epochId", epochId);
				writer.WriteStartArray("openDocumentPaths");
				foreach (string path in openDocumentPaths)
					writer.WriteStringValue(path);
				writer.WriteEndArray();
				writer.WriteEndObject();
			}
			body = stream.ToArray();
		}
		catch (Exception exception)
		{
			return CodeServiceDocumentEpochResult.Failure(CodeServiceDocumentOutcome.LocalInvalidRequest, "Epoch request serialization failed: " + ToSingleLine(exception.Message));
		}

		if (body.Length > CodeServiceDocumentSynchronizationLimits.MaxEpochRequestBodySizeBytes)
			return CodeServiceDocumentEpochResult.Failure(CodeServiceDocumentOutcome.LocalCapacityExceeded, "Epoch request body exceeds the local endpoint bound.");

		string requestId = Guid.NewGuid().ToString("D");
		TransportResponse transport = await SendAsync(
			CodeServiceClientProtocol.DocumentEpochPath,
			requestId,
			body,
			cancellationToken
		).ConfigureAwait(false);

		if (!transport.HasHttpResponse)
			return CodeServiceDocumentEpochResult.Failure(transport.Outcome, transport.Detail);
		if (transport.StatusCode == (int)HttpStatusCode.ServiceUnavailable && transport.Body.Length == 0)
			return CodeServiceDocumentEpochResult.Failure(CodeServiceDocumentOutcome.DocumentSynchronizationUnavailableForSession, "Document synchronization endpoint is unavailable for this logical service session.");
		if (transport.StatusCode == (int)HttpStatusCode.Unauthorized && transport.Body.Length == 0)
			return CodeServiceDocumentEpochResult.Failure(CodeServiceDocumentOutcome.AuthenticationFailed, "Document epoch authentication failed.");
		if (transport.Body.Length == 0)
			return CodeServiceDocumentEpochResult.Failure(CodeServiceDocumentOutcome.MalformedResponse, $"Document epoch returned HTTP {transport.StatusCode} with an unexpected zero-length body.");

		return ParseEpochResponse(
			transport.StatusCode,
			transport.Body,
			requestId,
			clientGeneration,
			epochId,
			openDocumentPaths.Count
		);
	}

	internal async Task<CodeServiceDocumentSnapshotResult> SynchronizeSnapshotAsync(
		long clientGeneration,
		string epochId,
		CodeServiceDocumentSnapshot snapshot,
		CancellationToken cancellationToken
	)
	{
		if (clientGeneration <= 0 || !TryGetCanonicalGuid(epochId, out _))
			return CodeServiceDocumentSnapshotResult.Failure(CodeServiceDocumentOutcome.LocalInvalidRequest, "Invalid document authority.");
		if (!CodeServiceDocumentPath.TryValidateWirePath(snapshot.DocumentPath, out string pathDetail))
			return CodeServiceDocumentSnapshotResult.Failure(CodeServiceDocumentOutcome.LocalInvalidRequest, pathDetail);
		if (snapshot.ClientVersion <= 0 || snapshot.Text == null || snapshot.Utf8Bytes < 0)
			return CodeServiceDocumentSnapshotResult.Failure(CodeServiceDocumentOutcome.LocalInvalidRequest, "Invalid document snapshot identity or text.");
		if (snapshot.Utf8Bytes > CodeServiceDocumentSynchronizationLimits.MaxDocumentTextUtf8Bytes)
			return CodeServiceDocumentSnapshotResult.Failure(CodeServiceDocumentOutcome.LocalCapacityExceeded, "Document text exceeds the local UTF-8 bound.");
		if (Encoding.UTF8.GetByteCount(snapshot.Text) != snapshot.Utf8Bytes)
			return CodeServiceDocumentSnapshotResult.Failure(CodeServiceDocumentOutcome.LocalInvalidRequest, "Document snapshot UTF-8 byte accounting is inconsistent.");

		byte[] body;
		try
		{
			using MemoryStream stream = new();
			using (Utf8JsonWriter writer = new(stream))
			{
				writer.WriteStartObject();
				writer.WriteNumber("schemaVersion", CodeServiceClientProtocol.DocumentSynchronizationSchemaVersion);
				writer.WriteNumber("clientGeneration", clientGeneration);
				writer.WriteString("epochId", epochId);
				writer.WriteString("documentPath", snapshot.DocumentPath);
				writer.WriteNumber("clientVersion", snapshot.ClientVersion);
				writer.WriteString("text", snapshot.Text);
				writer.WriteEndObject();
			}
			body = stream.ToArray();
		}
		catch (Exception exception)
		{
			return CodeServiceDocumentSnapshotResult.Failure(CodeServiceDocumentOutcome.LocalInvalidRequest, "Snapshot request serialization failed: " + ToSingleLine(exception.Message));
		}

		if (body.Length > CodeServiceDocumentSynchronizationLimits.MaxSnapshotRequestBodySizeBytes)
			return CodeServiceDocumentSnapshotResult.Failure(CodeServiceDocumentOutcome.LocalCapacityExceeded, "Snapshot request body exceeds the local endpoint bound.");

		string requestId = Guid.NewGuid().ToString("D");
		TransportResponse transport = await SendAsync(
			CodeServiceClientProtocol.DocumentSnapshotPath,
			requestId,
			body,
			cancellationToken
		).ConfigureAwait(false);

		if (!transport.HasHttpResponse)
			return CodeServiceDocumentSnapshotResult.Failure(transport.Outcome, transport.Detail);
		if (transport.StatusCode == (int)HttpStatusCode.ServiceUnavailable && transport.Body.Length == 0)
			return CodeServiceDocumentSnapshotResult.Failure(CodeServiceDocumentOutcome.DocumentSynchronizationUnavailableForSession, "Document synchronization endpoint is unavailable for this logical service session.");
		if (transport.StatusCode == (int)HttpStatusCode.Unauthorized && transport.Body.Length == 0)
			return CodeServiceDocumentSnapshotResult.Failure(CodeServiceDocumentOutcome.AuthenticationFailed, "Document snapshot authentication failed.");
		if (transport.Body.Length == 0)
			return CodeServiceDocumentSnapshotResult.Failure(CodeServiceDocumentOutcome.MalformedResponse, $"Document snapshot returned HTTP {transport.StatusCode} with an unexpected zero-length body.");

		return ParseSnapshotResponse(
			transport.StatusCode,
			transport.Body,
			requestId,
			clientGeneration,
			epochId,
			snapshot
		);
	}

	private async Task<TransportResponse> SendAsync(
		string relativePath,
		string requestId,
		byte[] body,
		CancellationToken cancellationToken
	)
	{
		HttpClient httpClient = Volatile.Read(ref _httpClient);
		CodeServiceClientCredentials credentials = Volatile.Read(ref _credentials);
		if (httpClient == null || credentials == null)
			return TransportResponse.Failure(CodeServiceDocumentOutcome.Disposed, "Document client admission is closed.");

		using HttpRequestMessage request = new(HttpMethod.Post, relativePath);
		try
		{
			request.Headers.TryAddWithoutValidation("Authorization", credentials.CreateBearerAuthorizationValue());
			request.Headers.TryAddWithoutValidation(
				CodeServiceClientProtocol.ProtocolVersionHeaderName,
				CodeServiceClientProtocol.ProtocolVersion.ToString(CultureInfo.InvariantCulture)
			);
			request.Headers.TryAddWithoutValidation(CodeServiceClientProtocol.SessionIdHeaderName, _sessionId);
			request.Headers.TryAddWithoutValidation(CodeServiceClientProtocol.RequestIdHeaderName, requestId);
			request.Content = new ByteArrayContent(body);
			request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
		}
		catch (Exception exception)
		{
			return TransportResponse.Failure(CodeServiceDocumentOutcome.TransportUnavailable, "Document request preparation failed: " + ToSingleLine(exception.Message));
		}

		using CancellationTokenSource requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		requestCancellation.CancelAfter(RequestTimeout);
		HttpResponseMessage response;
		try
		{
			response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestCancellation.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (OperationCanceledException)
		{
			return TransportResponse.Failure(CodeServiceDocumentOutcome.TransportUnavailable, "Document request exceeded the bounded request deadline.");
		}
		catch (HttpRequestException exception)
		{
			return TransportResponse.Failure(CodeServiceDocumentOutcome.TransportUnavailable, "Document transport failed: " + ToSingleLine(exception.Message));
		}
		catch (ObjectDisposedException exception)
		{
			return TransportResponse.Failure(CodeServiceDocumentOutcome.TransportUnavailable, "Document transport was disposed: " + ToSingleLine(exception.Message));
		}
		catch (Exception exception)
		{
			return TransportResponse.Failure(CodeServiceDocumentOutcome.TransportUnavailable, "Document transport failed: " + ToSingleLine(exception.Message));
		}

		using (response)
		{
			byte[] responseBody;
			try
			{
				responseBody = await ReadBoundedBodyAsync(response, requestCancellation.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (OperationCanceledException)
			{
				return TransportResponse.Failure(CodeServiceDocumentOutcome.TransportUnavailable, "Document response body exceeded the bounded request deadline.");
			}
			catch (InvalidDataException exception)
			{
				return TransportResponse.Failure(CodeServiceDocumentOutcome.MalformedResponse, "Document response violated the bounded response contract: " + ToSingleLine(exception.Message));
			}
			catch (HttpRequestException exception)
			{
				return TransportResponse.Failure(CodeServiceDocumentOutcome.TransportUnavailable, "Document response transport failed: " + ToSingleLine(exception.Message));
			}
			catch (ObjectDisposedException exception)
			{
				return TransportResponse.Failure(CodeServiceDocumentOutcome.TransportUnavailable, "Document response transport was disposed: " + ToSingleLine(exception.Message));
			}
			catch (IOException exception)
			{
				return TransportResponse.Failure(CodeServiceDocumentOutcome.TransportUnavailable, "Document response transport failed: " + ToSingleLine(exception.Message));
			}
			catch (Exception exception)
			{
				return TransportResponse.Failure(CodeServiceDocumentOutcome.MalformedResponse, "Document response body could not be read safely: " + ToSingleLine(exception.Message));
			}

			return TransportResponse.Http((int)response.StatusCode, responseBody);
		}
	}

	private static async Task<byte[]> ReadBoundedBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		if (response.Content == null)
			return Array.Empty<byte>();
		if (response.Content.Headers.ContentLength is long length && length > CodeServiceDocumentSynchronizationLimits.MaxDocumentResponseSizeBytes)
			throw new InvalidDataException("Document response exceeds the configured response bound.");

		await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		using MemoryStream buffer = new();
		byte[] chunk = new byte[4096];
		while (true)
		{
			int read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false);
			if (read == 0)
				break;
			if (buffer.Length + read > CodeServiceDocumentSynchronizationLimits.MaxDocumentResponseSizeBytes)
				throw new InvalidDataException("Document response exceeds the configured response bound.");
			buffer.Write(chunk, 0, read);
		}
		return buffer.ToArray();
	}

	private static CodeServiceDocumentEpochResult ParseEpochResponse(
		int statusCode,
		byte[] bytes,
		string expectedRequestId,
		long expectedClientGeneration,
		string expectedEpochId,
		int requestedOpenCount
	)
	{
		try
		{
			using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 16 });
			JsonElement root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
				return CodeServiceDocumentEpochResult.Failure(CodeServiceDocumentOutcome.MalformedResponse, "Document epoch response root must be an object.");

			int schemaCount = 0, outcomeCount = 0, requestCount = 0, generationCount = 0, epochCount = 0;
			int workspaceGenerationCount = 0, publicationCount = 0, roslynCount = 0;
			int declaredCount = 0, retainedCount = 0, closedCount = 0;
			int schema = 0, declared = 0, retained = 0, closed = 0;
			string outcomeText = null, requestId = null, epochId = null;
			long? clientGeneration = null, workspaceGeneration = null, publicationVersion = null, roslynGeneration = null;

			foreach (JsonProperty property in root.EnumerateObject())
			{
				switch (property.Name)
				{
					case "schemaVersion": if (++schemaCount != 1 || property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out schema)) return EpochMalformed("Invalid schemaVersion."); break;
					case "outcome": if (++outcomeCount != 1 || property.Value.ValueKind != JsonValueKind.String) return EpochMalformed("Invalid outcome."); outcomeText = property.Value.GetString(); break;
					case "requestId": if (++requestCount != 1 || !TryReadNullableString(property.Value, out requestId)) return EpochMalformed("Invalid requestId."); break;
					case "clientGeneration": if (++generationCount != 1 || !TryReadNullablePositiveInt64(property.Value, out clientGeneration)) return EpochMalformed("Invalid clientGeneration."); break;
					case "epochId": if (++epochCount != 1 || !TryReadNullableCanonicalGuidString(property.Value, out epochId)) return EpochMalformed("Invalid epochId."); break;
					case "workspaceGeneration": if (++workspaceGenerationCount != 1 || !TryReadNullablePositiveInt64(property.Value, out workspaceGeneration)) return EpochMalformed("Invalid workspaceGeneration."); break;
					case "workspacePublicationVersion": if (++publicationCount != 1 || !TryReadNullablePositiveInt64(property.Value, out publicationVersion)) return EpochMalformed("Invalid workspacePublicationVersion."); break;
					case "roslynGeneration": if (++roslynCount != 1 || !TryReadNullablePositiveInt64(property.Value, out roslynGeneration)) return EpochMalformed("Invalid roslynGeneration."); break;
					case "declaredOpenDocumentCount": if (++declaredCount != 1 || property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out declared) || declared < 0) return EpochMalformed("Invalid declaredOpenDocumentCount."); break;
					case "retainedDocumentCount": if (++retainedCount != 1 || property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out retained) || retained < 0) return EpochMalformed("Invalid retainedDocumentCount."); break;
					case "closedDocumentCount": if (++closedCount != 1 || property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out closed) || closed < 0) return EpochMalformed("Invalid closedDocumentCount."); break;
					default: return EpochMalformed("Document epoch response contained an unknown property.");
				}
			}

			if (schemaCount != 1 || outcomeCount != 1 || requestCount != 1 || generationCount != 1 || epochCount != 1 || workspaceGenerationCount != 1 || publicationCount != 1 || roslynCount != 1 || declaredCount != 1 || retainedCount != 1 || closedCount != 1)
				return EpochMalformed("Document epoch response omitted one or more required properties.");
			if (schema != CodeServiceClientProtocol.DocumentSynchronizationSchemaVersion)
				return EpochMalformed("Document epoch response schemaVersion mismatch.");
			if (!TryMapServiceOutcome(outcomeText, out CodeServiceDocumentOutcome outcome) || !StatusMatchesOutcome(statusCode, outcome))
				return EpochMalformed("Document epoch response outcome/status combination is invalid.");
			if (!string.Equals(requestId, expectedRequestId, StringComparison.Ordinal))
				return EpochMalformed("Document epoch response requestId mismatch.");
			if (clientGeneration.HasValue && clientGeneration.Value != expectedClientGeneration)
				return EpochMalformed("Document epoch response clientGeneration echo mismatch.");
			if (epochId != null && !string.Equals(epochId, expectedEpochId, StringComparison.Ordinal))
				return EpochMalformed("Document epoch response epochId echo mismatch.");

			if (outcome is CodeServiceDocumentOutcome.Success or CodeServiceDocumentOutcome.AlreadyCurrent)
			{
				if (clientGeneration != expectedClientGeneration || !string.Equals(epochId, expectedEpochId, StringComparison.Ordinal) || !workspaceGeneration.HasValue || !publicationVersion.HasValue || !roslynGeneration.HasValue || declared != requestedOpenCount)
					return EpochMalformed("Accepted document epoch response did not strictly match the request/publication identity.");
			}

			return new CodeServiceDocumentEpochResult(outcome, requestId, clientGeneration, epochId ?? "", workspaceGeneration, publicationVersion, roslynGeneration, declared, retained, closed, "");
		}
		catch (JsonException exception)
		{
			return EpochMalformed("Document epoch response JSON was invalid: " + ToSingleLine(exception.Message));
		}
	}

	private static CodeServiceDocumentSnapshotResult ParseSnapshotResponse(
		int statusCode,
		byte[] bytes,
		string expectedRequestId,
		long expectedClientGeneration,
		string expectedEpochId,
		CodeServiceDocumentSnapshot expectedSnapshot
	)
	{
		try
		{
			using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 16 });
			JsonElement root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
				return SnapshotMalformed("Document snapshot response root must be an object.");

			int schemaCount = 0, outcomeCount = 0, requestCount = 0, generationCount = 0, epochCount = 0, pathCount = 0, versionCount = 0;
			int workspaceGenerationCount = 0, publicationCount = 0, roslynCount = 0, roslynDocumentCount = 0;
			int schema = 0;
			string outcomeText = null, requestId = null, epochId = null, documentPath = null;
			long? clientGeneration = null, acceptedVersion = null, workspaceGeneration = null, publicationVersion = null, roslynGeneration = null;
			int? roslynDocumentVersion = null;

			foreach (JsonProperty property in root.EnumerateObject())
			{
				switch (property.Name)
				{
					case "schemaVersion": if (++schemaCount != 1 || property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out schema)) return SnapshotMalformed("Invalid schemaVersion."); break;
					case "outcome": if (++outcomeCount != 1 || property.Value.ValueKind != JsonValueKind.String) return SnapshotMalformed("Invalid outcome."); outcomeText = property.Value.GetString(); break;
					case "requestId": if (++requestCount != 1 || !TryReadNullableString(property.Value, out requestId)) return SnapshotMalformed("Invalid requestId."); break;
					case "clientGeneration": if (++generationCount != 1 || !TryReadNullablePositiveInt64(property.Value, out clientGeneration)) return SnapshotMalformed("Invalid clientGeneration."); break;
					case "epochId": if (++epochCount != 1 || !TryReadNullableCanonicalGuidString(property.Value, out epochId)) return SnapshotMalformed("Invalid epochId."); break;
					case "documentPath": if (++pathCount != 1 || !TryReadNullableString(property.Value, out documentPath)) return SnapshotMalformed("Invalid documentPath."); break;
					case "acceptedClientVersion": if (++versionCount != 1 || !TryReadNullableNonNegativeInt64(property.Value, out acceptedVersion)) return SnapshotMalformed("Invalid acceptedClientVersion."); break;
					case "workspaceGeneration": if (++workspaceGenerationCount != 1 || !TryReadNullablePositiveInt64(property.Value, out workspaceGeneration)) return SnapshotMalformed("Invalid workspaceGeneration."); break;
					case "workspacePublicationVersion": if (++publicationCount != 1 || !TryReadNullablePositiveInt64(property.Value, out publicationVersion)) return SnapshotMalformed("Invalid workspacePublicationVersion."); break;
					case "roslynGeneration": if (++roslynCount != 1 || !TryReadNullablePositiveInt64(property.Value, out roslynGeneration)) return SnapshotMalformed("Invalid roslynGeneration."); break;
					case "roslynDocumentVersion": if (++roslynDocumentCount != 1 || !TryReadNullablePositiveInt32(property.Value, out roslynDocumentVersion)) return SnapshotMalformed("Invalid roslynDocumentVersion."); break;
					default: return SnapshotMalformed("Document snapshot response contained an unknown property.");
				}
			}

			if (schemaCount != 1 || outcomeCount != 1 || requestCount != 1 || generationCount != 1 || epochCount != 1 || pathCount != 1 || versionCount != 1 || workspaceGenerationCount != 1 || publicationCount != 1 || roslynCount != 1 || roslynDocumentCount != 1)
				return SnapshotMalformed("Document snapshot response omitted one or more required properties.");
			if (schema != CodeServiceClientProtocol.DocumentSynchronizationSchemaVersion)
				return SnapshotMalformed("Document snapshot response schemaVersion mismatch.");
			if (!TryMapServiceOutcome(outcomeText, out CodeServiceDocumentOutcome outcome) || !StatusMatchesOutcome(statusCode, outcome))
				return SnapshotMalformed("Document snapshot response outcome/status combination is invalid.");
			if (!string.Equals(requestId, expectedRequestId, StringComparison.Ordinal))
				return SnapshotMalformed("Document snapshot response requestId mismatch.");
			if (clientGeneration.HasValue && clientGeneration.Value != expectedClientGeneration)
				return SnapshotMalformed("Document snapshot response clientGeneration echo mismatch.");
			if (epochId != null && !string.Equals(epochId, expectedEpochId, StringComparison.Ordinal))
				return SnapshotMalformed("Document snapshot response epochId echo mismatch.");
			if (documentPath != null && !CodeServiceDocumentPath.Equals(documentPath, expectedSnapshot.DocumentPath))
				return SnapshotMalformed("Document snapshot response documentPath echo mismatch.");

			if (outcome is CodeServiceDocumentOutcome.Success or CodeServiceDocumentOutcome.AlreadyCurrent)
			{
				if (clientGeneration != expectedClientGeneration || !string.Equals(epochId, expectedEpochId, StringComparison.Ordinal) || !CodeServiceDocumentPath.Equals(documentPath, expectedSnapshot.DocumentPath) || acceptedVersion != expectedSnapshot.ClientVersion || !workspaceGeneration.HasValue || !publicationVersion.HasValue || !roslynGeneration.HasValue || !roslynDocumentVersion.HasValue)
					return SnapshotMalformed("Accepted document snapshot response did not strictly match the request/publication identity.");
			}
			else if (outcome == CodeServiceDocumentOutcome.StaleVersion)
			{
				if (!CodeServiceDocumentPath.Equals(documentPath, expectedSnapshot.DocumentPath)
					|| !acceptedVersion.HasValue
					|| acceptedVersion.Value <= expectedSnapshot.ClientVersion)
				{
					return SnapshotMalformed("StaleVersion response did not expose the server's strictly newer accepted client version.");
				}
			}
			else if (outcome == CodeServiceDocumentOutcome.VersionConflict)
			{
				if (!CodeServiceDocumentPath.Equals(documentPath, expectedSnapshot.DocumentPath)
					|| acceptedVersion != expectedSnapshot.ClientVersion)
				{
					return SnapshotMalformed("VersionConflict response did not match the conflicting request version.");
				}
			}

			return new CodeServiceDocumentSnapshotResult(outcome, requestId, clientGeneration, epochId ?? "", documentPath ?? "", acceptedVersion, workspaceGeneration, publicationVersion, roslynGeneration, roslynDocumentVersion, "");
		}
		catch (JsonException exception)
		{
			return SnapshotMalformed("Document snapshot response JSON was invalid: " + ToSingleLine(exception.Message));
		}
	}

	private static bool TryMapServiceOutcome(string value, out CodeServiceDocumentOutcome outcome)
	{
		outcome = value switch
		{
			CodeServiceClientProtocol.DocumentSuccessOutcome => CodeServiceDocumentOutcome.Success,
			CodeServiceClientProtocol.DocumentAlreadyCurrentOutcome => CodeServiceDocumentOutcome.AlreadyCurrent,
			CodeServiceClientProtocol.DocumentInvalidRequestOutcome => CodeServiceDocumentOutcome.InvalidRequest,
			CodeServiceClientProtocol.DocumentVersionMismatchOutcome => CodeServiceDocumentOutcome.VersionMismatch,
			CodeServiceClientProtocol.DocumentBusyOutcome => CodeServiceDocumentOutcome.Busy,
			CodeServiceClientProtocol.DocumentWorkspaceUnavailableOutcome => CodeServiceDocumentOutcome.WorkspaceUnavailable,
			CodeServiceClientProtocol.DocumentRoslynUnavailableOutcome => CodeServiceDocumentOutcome.RoslynUnavailable,
			CodeServiceClientProtocol.DocumentStaleEpochOutcome => CodeServiceDocumentOutcome.StaleEpoch,
			CodeServiceClientProtocol.DocumentEpochConflictOutcome => CodeServiceDocumentOutcome.EpochConflict,
			CodeServiceClientProtocol.DocumentStaleVersionOutcome => CodeServiceDocumentOutcome.StaleVersion,
			CodeServiceClientProtocol.DocumentVersionConflictOutcome => CodeServiceDocumentOutcome.VersionConflict,
			CodeServiceClientProtocol.DocumentNotOpenOutcome => CodeServiceDocumentOutcome.DocumentNotOpen,
			CodeServiceClientProtocol.DocumentNotInWorkspaceOutcome => CodeServiceDocumentOutcome.DocumentNotInWorkspace,
			CodeServiceClientProtocol.DocumentCapacityExceededOutcome => CodeServiceDocumentOutcome.CapacityExceeded,
			CodeServiceClientProtocol.DocumentUnavailableOutcome => CodeServiceDocumentOutcome.Unavailable,
			_ => (CodeServiceDocumentOutcome)(-1),
		};
		return (int)outcome >= 0;
	}

	private static bool StatusMatchesOutcome(int statusCode, CodeServiceDocumentOutcome outcome) => outcome switch
	{
		CodeServiceDocumentOutcome.Success or CodeServiceDocumentOutcome.AlreadyCurrent => statusCode == 200,
		CodeServiceDocumentOutcome.InvalidRequest => statusCode == 400,
		CodeServiceDocumentOutcome.VersionMismatch or CodeServiceDocumentOutcome.StaleEpoch or CodeServiceDocumentOutcome.EpochConflict or CodeServiceDocumentOutcome.StaleVersion or CodeServiceDocumentOutcome.VersionConflict or CodeServiceDocumentOutcome.DocumentNotOpen or CodeServiceDocumentOutcome.DocumentNotInWorkspace => statusCode == 409,
		CodeServiceDocumentOutcome.CapacityExceeded => statusCode == 413,
		CodeServiceDocumentOutcome.Busy or CodeServiceDocumentOutcome.WorkspaceUnavailable or CodeServiceDocumentOutcome.RoslynUnavailable or CodeServiceDocumentOutcome.Unavailable => statusCode == 503,
		_ => false,
	};

	private static bool TryReadNullableString(JsonElement element, out string value)
	{
		value = null;
		if (element.ValueKind == JsonValueKind.Null) return true;
		if (element.ValueKind != JsonValueKind.String) return false;
		value = element.GetString();
		return value != null;
	}

	private static bool TryReadNullableCanonicalGuidString(JsonElement element, out string value)
	{
		value = null;
		if (element.ValueKind == JsonValueKind.Null) return true;
		if (element.ValueKind != JsonValueKind.String) return false;
		string text = element.GetString();
		if (!TryGetCanonicalGuid(text, out _)) return false;
		value = text;
		return true;
	}

	private static bool TryReadNullableNonNegativeInt64(JsonElement element, out long? value)
	{
		value = null;
		if (element.ValueKind == JsonValueKind.Null) return true;
		if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out long parsed) || parsed < 0) return false;
		value = parsed;
		return true;
	}

	private static bool TryReadNullablePositiveInt64(JsonElement element, out long? value)
	{
		value = null;
		if (element.ValueKind == JsonValueKind.Null) return true;
		if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out long parsed) || parsed <= 0) return false;
		value = parsed;
		return true;
	}

	private static bool TryReadNullablePositiveInt32(JsonElement element, out int? value)
	{
		value = null;
		if (element.ValueKind == JsonValueKind.Null) return true;
		if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out int parsed) || parsed <= 0) return false;
		value = parsed;
		return true;
	}

	internal static bool TryGetCanonicalGuid(string text, out Guid guid)
	{
		guid = default;
		return !string.IsNullOrEmpty(text)
			&& Guid.TryParseExact(text, "D", out guid)
			&& string.Equals(guid.ToString("D"), text, StringComparison.Ordinal);
	}

	private static CodeServiceDocumentEpochResult EpochMalformed(string detail) =>
		CodeServiceDocumentEpochResult.Failure(CodeServiceDocumentOutcome.MalformedResponse, detail);
	private static CodeServiceDocumentSnapshotResult SnapshotMalformed(string detail) =>
		CodeServiceDocumentSnapshotResult.Failure(CodeServiceDocumentOutcome.MalformedResponse, detail);
	private static string ToSingleLine(string value) => (value ?? "").Replace('\r', ' ').Replace('\n', ' ');

	private readonly record struct TransportResponse(
		bool HasHttpResponse,
		int StatusCode,
		byte[] Body,
		CodeServiceDocumentOutcome Outcome,
		string Detail)
	{
		internal static TransportResponse Http(int statusCode, byte[] body) => new(true, statusCode, body ?? Array.Empty<byte>(), default, "");
		internal static TransportResponse Failure(CodeServiceDocumentOutcome outcome, string detail) => new(false, 0, Array.Empty<byte>(), outcome, detail ?? "");
	}
}
#endif
