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
using SystemExplorer.CodeService.Documents;

namespace SystemExplorer.CodeService.Completion;

internal sealed class CodeServiceCompletionResolveClient
{
	private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
	private HttpClient _httpClient;
	private CodeServiceClientCredentials _credentials;
	private readonly string _sessionId;

	internal CodeServiceCompletionResolveClient(
		HttpClient httpClient,
		CodeServiceClientCredentials credentials,
		string sessionId)
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

	internal async Task<CodeServiceCompletionResolveResult> ResolveAsync(
		CodeServiceCompletionResolveRequest request,
		CancellationToken cancellationToken)
	{
		if (!TryValidateRequest(request, out string requestDetail))
			return CodeServiceCompletionResolveResult.Failure(CodeServiceCompletionResolveOutcome.LocalInvalidRequest, requestDetail);

		byte[] body;
		try
		{
			using MemoryStream stream = new();
			using (Utf8JsonWriter writer = new(stream))
			{
				writer.WriteStartObject();
				writer.WriteNumber("schemaVersion", CodeServiceClientProtocol.CompletionResolveSchemaVersion);
				writer.WriteNumber("clientGeneration", request.ClientGeneration);
				writer.WriteString("epochId", request.EpochId);
				writer.WriteString("documentPath", request.DocumentPath);
				writer.WriteNumber("clientVersion", request.ClientVersion);
				writer.WriteString("completionHandle", request.CompletionHandle.ToString("D"));
				writer.WriteEndObject();
			}
			body = stream.ToArray();
		}
		catch (Exception exception)
		{
			return CodeServiceCompletionResolveResult.Failure(
				CodeServiceCompletionResolveOutcome.LocalInvalidRequest,
				"Completion resolve request serialization failed: " + ToSingleLine(exception.Message));
		}

		if (body.Length > CodeServiceCompletionLimits.MaxCompletionResolveRequestBodySizeBytes)
			return CodeServiceCompletionResolveResult.Failure(CodeServiceCompletionResolveOutcome.LocalInvalidRequest, "Completion resolve request body exceeds the local endpoint bound.");

		string requestId = Guid.NewGuid().ToString("D");
		TransportResponse transport = await SendAsync(requestId, body, cancellationToken).ConfigureAwait(false);
		if (!transport.HasHttpResponse)
			return CodeServiceCompletionResolveResult.Failure(transport.Outcome, transport.Detail);
		if (transport.StatusCode == (int)HttpStatusCode.ServiceUnavailable && transport.Body.Length == 0)
			return CodeServiceCompletionResolveResult.Failure(CodeServiceCompletionResolveOutcome.CompletionResolveUnavailableForSession, "Completion resolve endpoint is unavailable for this logical service session.");
		if (transport.StatusCode == (int)HttpStatusCode.Unauthorized && transport.Body.Length == 0)
			return CodeServiceCompletionResolveResult.Failure(CodeServiceCompletionResolveOutcome.AuthenticationFailed, "Completion resolve authentication failed.");
		if (transport.Body.Length == 0)
			return CodeServiceCompletionResolveResult.Failure(CodeServiceCompletionResolveOutcome.MalformedResponse, $"Completion resolve returned HTTP {transport.StatusCode} with an unexpected zero-length body.");

		return ParseResponse(transport.StatusCode, transport.Body, requestId, request);
	}

	private async Task<TransportResponse> SendAsync(string requestId, byte[] body, CancellationToken cancellationToken)
	{
		HttpClient httpClient = Volatile.Read(ref _httpClient);
		CodeServiceClientCredentials credentials = Volatile.Read(ref _credentials);
		if (httpClient == null || credentials == null)
			return TransportResponse.Failure(CodeServiceCompletionResolveOutcome.Disposed, "Completion resolve client admission is closed.");

		using HttpRequestMessage httpRequest = new(HttpMethod.Post, CodeServiceClientProtocol.CompletionResolvePath);
		try
		{
			httpRequest.Headers.TryAddWithoutValidation("Authorization", credentials.CreateBearerAuthorizationValue());
			httpRequest.Headers.TryAddWithoutValidation(CodeServiceClientProtocol.ProtocolVersionHeaderName, CodeServiceClientProtocol.ProtocolVersion.ToString(CultureInfo.InvariantCulture));
			httpRequest.Headers.TryAddWithoutValidation(CodeServiceClientProtocol.SessionIdHeaderName, _sessionId);
			httpRequest.Headers.TryAddWithoutValidation(CodeServiceClientProtocol.RequestIdHeaderName, requestId);
			httpRequest.Content = new ByteArrayContent(body);
			httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
		}
		catch (Exception exception)
		{
			return TransportResponse.Failure(CodeServiceCompletionResolveOutcome.TransportUnavailable, "Completion resolve request preparation failed: " + ToSingleLine(exception.Message));
		}

		using CancellationTokenSource requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		requestCancellation.CancelAfter(RequestTimeout);
		HttpResponseMessage response;
		try
		{
			response = await httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, requestCancellation.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
		catch (OperationCanceledException)
		{
			return TransportResponse.Failure(CodeServiceCompletionResolveOutcome.TransportUnavailable, "Completion resolve request exceeded the bounded request deadline.");
		}
		catch (HttpRequestException exception)
		{
			return TransportResponse.Failure(CodeServiceCompletionResolveOutcome.TransportUnavailable, "Completion resolve transport failed: " + ToSingleLine(exception.Message));
		}
		catch (ObjectDisposedException exception)
		{
			return TransportResponse.Failure(CodeServiceCompletionResolveOutcome.TransportUnavailable, "Completion resolve transport was disposed: " + ToSingleLine(exception.Message));
		}
		catch (Exception exception)
		{
			return TransportResponse.Failure(CodeServiceCompletionResolveOutcome.TransportUnavailable, "Completion resolve transport failed: " + ToSingleLine(exception.Message));
		}

		using (response)
		{
			try
			{
				byte[] responseBody = await ReadBoundedBodyAsync(response, requestCancellation.Token).ConfigureAwait(false);
				return TransportResponse.Http((int)response.StatusCode, responseBody);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
			catch (OperationCanceledException)
			{
				return TransportResponse.Failure(CodeServiceCompletionResolveOutcome.TransportUnavailable, "Completion resolve response body exceeded the bounded request deadline.");
			}
			catch (InvalidDataException exception)
			{
				return TransportResponse.Failure(CodeServiceCompletionResolveOutcome.MalformedResponse, "Completion resolve response violated the bounded response contract: " + ToSingleLine(exception.Message));
			}
			catch (HttpRequestException exception)
			{
				return TransportResponse.Failure(CodeServiceCompletionResolveOutcome.TransportUnavailable, "Completion resolve response transport failed: " + ToSingleLine(exception.Message));
			}
			catch (ObjectDisposedException exception)
			{
				return TransportResponse.Failure(CodeServiceCompletionResolveOutcome.TransportUnavailable, "Completion resolve response transport was disposed: " + ToSingleLine(exception.Message));
			}
			catch (IOException exception)
			{
				return TransportResponse.Failure(CodeServiceCompletionResolveOutcome.TransportUnavailable, "Completion resolve response transport failed: " + ToSingleLine(exception.Message));
			}
			catch (Exception exception)
			{
				return TransportResponse.Failure(CodeServiceCompletionResolveOutcome.MalformedResponse, "Completion resolve response body could not be read safely: " + ToSingleLine(exception.Message));
			}
		}
	}

	private static async Task<byte[]> ReadBoundedBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		if (response.Content == null)
			return Array.Empty<byte>();
		if (response.Content.Headers.ContentLength is long length && length > CodeServiceCompletionLimits.MaxCompletionResolveResponseBodySizeBytes)
			throw new InvalidDataException("Completion resolve response exceeds the configured response bound.");

		await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		using MemoryStream buffer = new();
		byte[] chunk = new byte[8192];
		while (true)
		{
			int read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false);
			if (read == 0) break;
			if (buffer.Length + read > CodeServiceCompletionLimits.MaxCompletionResolveResponseBodySizeBytes)
				throw new InvalidDataException("Completion resolve response exceeds the configured response bound.");
			buffer.Write(chunk, 0, read);
		}
		return buffer.ToArray();
	}

	private static CodeServiceCompletionResolveResult ParseResponse(
		int statusCode,
		byte[] bytes,
		string expectedRequestId,
		CodeServiceCompletionResolveRequest expectedRequest)
	{
		try
		{
			using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 24 });
			JsonElement root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
				return Malformed("Completion resolve response root must be an object.");

			int schemaCount=0,outcomeCount=0,requestIdCount=0,clientGenerationCount=0,epochIdCount=0,documentPathCount=0,acceptedClientVersionCount=0;
			int workspaceGenerationCount=0,workspacePublicationVersionCount=0,roslynGenerationCount=0,roslynDocumentVersionCount=0,roslynOverlayRevisionCount=0,editsCount=0;
			int schemaVersion=0; string outcomeText=null, requestId=null, epochId=null, documentPath=null;
			long? clientGeneration=null, acceptedClientVersion=null, workspaceGeneration=null, workspacePublicationVersion=null, roslynGeneration=null, roslynOverlayRevision=null;
			int? roslynDocumentVersion=null;
			IReadOnlyList<CodeServiceCompletionTextEdit> edits=Array.Empty<CodeServiceCompletionTextEdit>();

			foreach (JsonProperty property in root.EnumerateObject())
			{
				switch (property.Name)
				{
					case "schemaVersion": if (++schemaCount != 1 || property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out schemaVersion)) return Malformed("Invalid schemaVersion."); break;
					case "outcome": if (++outcomeCount != 1 || property.Value.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(outcomeText = property.Value.GetString())) return Malformed("Invalid outcome."); break;
					case "requestId":
						if (++requestIdCount != 1 || property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() is not string requestIdText || !TryGetCanonicalGuid(requestIdText, out Guid parsedRequestId) || parsedRequestId == Guid.Empty) return Malformed("Invalid requestId.");
						requestId = requestIdText; break;
					case "clientGeneration": if (++clientGenerationCount != 1 || !TryReadNullablePositiveInt64(property.Value, out clientGeneration)) return Malformed("Invalid clientGeneration."); break;
					case "epochId": if (++epochIdCount != 1 || !TryReadNullableCanonicalNonEmptyGuid(property.Value, out epochId)) return Malformed("Invalid epochId."); break;
					case "documentPath": if (++documentPathCount != 1 || !TryReadNullableString(property.Value, out documentPath)) return Malformed("Invalid documentPath."); break;
					case "acceptedClientVersion": if (++acceptedClientVersionCount != 1 || !TryReadNullablePositiveInt64(property.Value, out acceptedClientVersion)) return Malformed("Invalid acceptedClientVersion."); break;
					case "workspaceGeneration": if (++workspaceGenerationCount != 1 || !TryReadNullablePositiveInt64(property.Value, out workspaceGeneration)) return Malformed("Invalid workspaceGeneration."); break;
					case "workspacePublicationVersion": if (++workspacePublicationVersionCount != 1 || !TryReadNullablePositiveInt64(property.Value, out workspacePublicationVersion)) return Malformed("Invalid workspacePublicationVersion."); break;
					case "roslynGeneration": if (++roslynGenerationCount != 1 || !TryReadNullablePositiveInt64(property.Value, out roslynGeneration)) return Malformed("Invalid roslynGeneration."); break;
					case "roslynDocumentVersion": if (++roslynDocumentVersionCount != 1 || !TryReadNullablePositiveInt32(property.Value, out roslynDocumentVersion)) return Malformed("Invalid roslynDocumentVersion."); break;
					case "roslynOverlayRevision": if (++roslynOverlayRevisionCount != 1 || !TryReadNullablePositiveInt64(property.Value, out roslynOverlayRevision)) return Malformed("Invalid roslynOverlayRevision."); break;
					case "edits":
						if (++editsCount != 1)
							return Malformed("Invalid edits.");

						if (!TryParseEdits(property.Value, out edits, out string editDetail))
							return Malformed(editDetail);
						break;
					default: return Malformed("Completion resolve response contained an unknown property.");
				}
			}

			if (schemaCount!=1 || outcomeCount!=1 || requestIdCount!=1 || clientGenerationCount!=1 || epochIdCount!=1 || documentPathCount!=1 || acceptedClientVersionCount!=1
				|| workspaceGenerationCount!=1 || workspacePublicationVersionCount!=1 || roslynGenerationCount!=1 || roslynDocumentVersionCount!=1 || roslynOverlayRevisionCount!=1 || editsCount!=1)
				return Malformed("Completion resolve response omitted one or more required properties.");
			if (schemaVersion != CodeServiceClientProtocol.CompletionResolveSchemaVersion)
				return Malformed("Completion resolve response schemaVersion mismatch.");
			if (!TryMapServiceOutcome(outcomeText, out CodeServiceCompletionResolveOutcome outcome) || !StatusMatchesOutcome(statusCode, outcome))
				return Malformed("Completion resolve response outcome/status combination is invalid.");
			if (!string.Equals(requestId, expectedRequestId, StringComparison.Ordinal))
				return Malformed("Completion resolve response requestId mismatch.");
			if (clientGeneration.HasValue && clientGeneration.Value != expectedRequest.ClientGeneration)
				return Malformed("Completion resolve response clientGeneration echo mismatch.");
			if (epochId != null && !string.Equals(epochId, expectedRequest.EpochId, StringComparison.Ordinal))
				return Malformed("Completion resolve response epochId echo mismatch.");
			if (documentPath != null && !string.Equals(documentPath, expectedRequest.DocumentPath, StringComparison.Ordinal))
				return Malformed("Completion resolve response documentPath echo mismatch.");

			if (outcome == CodeServiceCompletionResolveOutcome.Success)
			{
				if (clientGeneration != expectedRequest.ClientGeneration
					|| !string.Equals(epochId, expectedRequest.EpochId, StringComparison.Ordinal)
					|| !string.Equals(documentPath, expectedRequest.DocumentPath, StringComparison.Ordinal)
					|| acceptedClientVersion != expectedRequest.ClientVersion
					|| !workspaceGeneration.HasValue || !workspacePublicationVersion.HasValue || !roslynGeneration.HasValue || !roslynDocumentVersion.HasValue || !roslynOverlayRevision.HasValue
					|| edits.Count != 1)
					return Malformed("Successful completion resolve response did not strictly match the request/publication identity.");
			}
			else if (edits.Count != 0)
			{
				return Malformed("Failed completion resolve response must contain zero edits.");
			}

			return new CodeServiceCompletionResolveResult(
				outcome, requestId, clientGeneration, epochId ?? "", documentPath ?? "", acceptedClientVersion,
				workspaceGeneration, workspacePublicationVersion, roslynGeneration, roslynDocumentVersion, roslynOverlayRevision,
				edits, "");
		}
		catch (JsonException exception)
		{
			return Malformed("Completion resolve response JSON was invalid: " + ToSingleLine(exception.Message));
		}
	}

	private static bool TryParseEdits(JsonElement element, out IReadOnlyList<CodeServiceCompletionTextEdit> edits, out string detail)
	{
		edits = Array.Empty<CodeServiceCompletionTextEdit>(); detail = "";
		if (element.ValueKind != JsonValueKind.Array) { detail = "Completion resolve edits must be an array."; return false; }
		List<CodeServiceCompletionTextEdit> parsed = new();
		foreach (JsonElement editElement in element.EnumerateArray())
		{
			if (parsed.Count >= 1) { detail = "Completion resolve returned more than one edit."; return false; }
			if (editElement.ValueKind != JsonValueKind.Object) { detail = "Completion resolve edit must be an object."; return false; }
			int rangeCount=0,newTextCount=0; CodeServiceCompletionTextRange range=default; string newText=null;
			foreach (JsonProperty property in editElement.EnumerateObject())
			{
				switch (property.Name)
				{
					case "range": if (++rangeCount != 1 || !TryParseRange(property.Value, out range)) { detail = "Completion resolve edit range is invalid."; return false; } break;
					case "newText":
						if (++newTextCount != 1 || property.Value.ValueKind != JsonValueKind.String || (newText = property.Value.GetString()) == null || newText.Length == 0
							|| !TryGetBoundedUtf8ByteCount(newText, CodeServiceCompletionLimits.MaxCompletionResolveEditNewTextUtf8Bytes))
						{ detail = "Completion resolve edit newText is invalid or exceeds the local UTF-8 bound."; return false; }
						break;
					default: detail = "Completion resolve edit contained an unknown property."; return false;
				}
			}
			if (rangeCount != 1 || newTextCount != 1) { detail = "Completion resolve edit omitted a required property."; return false; }
			parsed.Add(new CodeServiceCompletionTextEdit(range, newText));
		}
		edits = parsed.AsReadOnly(); return true;
	}

	private static bool TryParseRange(JsonElement element, out CodeServiceCompletionTextRange range)
	{
		range=default; if (element.ValueKind != JsonValueKind.Object) return false;
		int startCount=0,endCount=0; CodeServiceCompletionTextPosition start=default,end=default;
		foreach (JsonProperty property in element.EnumerateObject())
		{
			switch (property.Name)
			{
				case "start": if (++startCount != 1 || !TryParsePosition(property.Value, out start)) return false; break;
				case "end": if (++endCount != 1 || !TryParsePosition(property.Value, out end)) return false; break;
				default: return false;
			}
		}
		if (startCount != 1 || endCount != 1) return false;
		if (start.Line > end.Line || (start.Line == end.Line && start.Character > end.Character)) return false;
		range = new CodeServiceCompletionTextRange(start,end); return true;
	}

	private static bool TryParsePosition(JsonElement element, out CodeServiceCompletionTextPosition position)
	{
		position=default; if (element.ValueKind != JsonValueKind.Object) return false;
		int lineCount=0,characterCount=0,line=0,character=0;
		foreach (JsonProperty property in element.EnumerateObject())
		{
			switch (property.Name)
			{
				case "line": if (++lineCount != 1 || property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out line) || line < 0 || line > CodeServiceCompletionLimits.MaxCompletionLine) return false; break;
				case "character": if (++characterCount != 1 || property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out character) || character < 0 || character > CodeServiceCompletionLimits.MaxCompletionCharacter) return false; break;
				default: return false;
			}
		}
		if (lineCount != 1 || characterCount != 1) return false;
		position = new CodeServiceCompletionTextPosition(line,character); return true;
	}

	private static bool TryValidateRequest(CodeServiceCompletionResolveRequest request, out string detail)
	{
		detail="";
		if (request.ClientGeneration <= 0) { detail="Completion resolve clientGeneration must be positive."; return false; }
		if (!TryGetCanonicalGuid(request.EpochId, out Guid epochId) || epochId == Guid.Empty) { detail="Completion resolve epochId must be a non-empty canonical lower-case GUID D string."; return false; }
		if (!CodeServiceDocumentPath.TryValidateWirePath(request.DocumentPath, out detail)) return false;
		if (request.ClientVersion <= 0) { detail="Completion resolve clientVersion must be positive."; return false; }
		if (request.CompletionHandle == Guid.Empty) { detail="Completion resolve handle must be non-empty."; return false; }
		return true;
	}

	private static bool TryMapServiceOutcome(string value, out CodeServiceCompletionResolveOutcome outcome)
	{
		outcome = value switch
		{
			CodeServiceClientProtocol.DocumentSuccessOutcome => CodeServiceCompletionResolveOutcome.Success,
			CodeServiceClientProtocol.DocumentInvalidRequestOutcome => CodeServiceCompletionResolveOutcome.InvalidRequest,
			CodeServiceClientProtocol.DocumentVersionMismatchOutcome => CodeServiceCompletionResolveOutcome.VersionMismatch,
			CodeServiceClientProtocol.DocumentBusyOutcome => CodeServiceCompletionResolveOutcome.Busy,
			CodeServiceClientProtocol.DocumentWorkspaceUnavailableOutcome => CodeServiceCompletionResolveOutcome.WorkspaceUnavailable,
			CodeServiceClientProtocol.DocumentRoslynUnavailableOutcome => CodeServiceCompletionResolveOutcome.RoslynUnavailable,
			CodeServiceClientProtocol.CompletionUnavailableOutcome => CodeServiceCompletionResolveOutcome.CompletionUnavailable,
			CodeServiceClientProtocol.CompletionExpiredOutcome => CodeServiceCompletionResolveOutcome.CompletionExpired,
			CodeServiceClientProtocol.DocumentStaleEpochOutcome => CodeServiceCompletionResolveOutcome.StaleEpoch,
			CodeServiceClientProtocol.DocumentEpochConflictOutcome => CodeServiceCompletionResolveOutcome.EpochConflict,
			CodeServiceClientProtocol.DocumentStaleVersionOutcome => CodeServiceCompletionResolveOutcome.StaleVersion,
			CodeServiceClientProtocol.DocumentNotSynchronizedOutcome => CodeServiceCompletionResolveOutcome.DocumentNotSynchronized,
			CodeServiceClientProtocol.DocumentNotOpenOutcome => CodeServiceCompletionResolveOutcome.DocumentNotOpen,
			CodeServiceClientProtocol.DocumentNotInWorkspaceOutcome => CodeServiceCompletionResolveOutcome.DocumentNotInWorkspace,
			CodeServiceClientProtocol.DocumentUnavailableOutcome => CodeServiceCompletionResolveOutcome.Unavailable,
			_ => (CodeServiceCompletionResolveOutcome)(-1),
		};
		return (int)outcome >= 0;
	}

	private static bool StatusMatchesOutcome(int statusCode, CodeServiceCompletionResolveOutcome outcome) => outcome switch
	{
		CodeServiceCompletionResolveOutcome.Success => statusCode == 200,
		CodeServiceCompletionResolveOutcome.InvalidRequest => statusCode == 400,
		CodeServiceCompletionResolveOutcome.VersionMismatch or CodeServiceCompletionResolveOutcome.CompletionExpired
			or CodeServiceCompletionResolveOutcome.StaleEpoch or CodeServiceCompletionResolveOutcome.EpochConflict
			or CodeServiceCompletionResolveOutcome.StaleVersion or CodeServiceCompletionResolveOutcome.DocumentNotSynchronized
			or CodeServiceCompletionResolveOutcome.DocumentNotOpen or CodeServiceCompletionResolveOutcome.DocumentNotInWorkspace => statusCode == 409,
		CodeServiceCompletionResolveOutcome.Busy or CodeServiceCompletionResolveOutcome.WorkspaceUnavailable
			or CodeServiceCompletionResolveOutcome.RoslynUnavailable or CodeServiceCompletionResolveOutcome.CompletionUnavailable
			or CodeServiceCompletionResolveOutcome.Unavailable => statusCode == 503,
		_ => false,
	};

	private static bool TryReadNullableString(JsonElement element, out string value)
	{
		value=null; if (element.ValueKind == JsonValueKind.Null) return true;
		if (element.ValueKind != JsonValueKind.String) return false; value=element.GetString(); return value != null;
	}
	private static bool TryReadNullableCanonicalNonEmptyGuid(JsonElement element, out string value)
	{
		value=null; if (element.ValueKind == JsonValueKind.Null) return true;
		if (element.ValueKind != JsonValueKind.String || element.GetString() is not string text || !TryGetCanonicalGuid(text, out Guid guid) || guid == Guid.Empty) return false;
		value=text; return true;
	}
	private static bool TryReadNullablePositiveInt64(JsonElement element, out long? value)
	{
		value=null; if (element.ValueKind == JsonValueKind.Null) return true;
		if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out long parsed) || parsed <= 0) return false; value=parsed; return true;
	}
	private static bool TryReadNullablePositiveInt32(JsonElement element, out int? value)
	{
		value=null; if (element.ValueKind == JsonValueKind.Null) return true;
		if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out int parsed) || parsed <= 0) return false; value=parsed; return true;
	}
	private static bool TryGetBoundedUtf8ByteCount(string value, int maximum)
	{
		if (value == null) return false;
		try { return Encoding.UTF8.GetByteCount(value) <= maximum; } catch { return false; }
	}
	private static bool TryGetCanonicalGuid(string text, out Guid guid)
	{
		guid=Guid.Empty; return !string.IsNullOrEmpty(text) && Guid.TryParseExact(text,"D",out guid) && string.Equals(guid.ToString("D"),text,StringComparison.Ordinal);
	}
	private static CodeServiceCompletionResolveResult Malformed(string detail) => CodeServiceCompletionResolveResult.Failure(CodeServiceCompletionResolveOutcome.MalformedResponse, detail);
	private static string ToSingleLine(string value) => (value ?? "").Replace('\r',' ').Replace('\n',' ');

	private readonly record struct TransportResponse(
		bool HasHttpResponse,
		int StatusCode,
		byte[] Body,
		CodeServiceCompletionResolveOutcome Outcome,
		string Detail)
	{
		internal static TransportResponse Http(int statusCode, byte[] body) => new(true,statusCode,body ?? Array.Empty<byte>(),default,"");
		internal static TransportResponse Failure(CodeServiceCompletionResolveOutcome outcome, string detail) => new(false,0,Array.Empty<byte>(),outcome,detail ?? "");
	}
}
#endif
