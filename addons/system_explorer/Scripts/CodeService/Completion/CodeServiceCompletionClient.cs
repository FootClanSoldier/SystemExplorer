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

internal sealed class CodeServiceCompletionClient
{
	private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(25);
	private HttpClient _httpClient;
	private CodeServiceClientCredentials _credentials;
	private readonly string _sessionId;

	internal CodeServiceCompletionClient(
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

	internal async Task<CodeServiceCompletionResult> CompleteAsync(
		CodeServiceCompletionRequest request,
		CancellationToken cancellationToken
	)
	{
		if (!TryValidateRequest(request, out string requestDetail))
		{
			return CodeServiceCompletionResult.Failure(
				CodeServiceCompletionOutcome.LocalInvalidRequest,
				requestDetail
			);
		}

		byte[] body;
		try
		{
			using MemoryStream stream = new();
			using (Utf8JsonWriter writer = new(stream))
			{
				writer.WriteStartObject();
				writer.WriteNumber("schemaVersion", CodeServiceClientProtocol.CompletionSchemaVersion);
				writer.WriteNumber("clientGeneration", request.ClientGeneration);
				writer.WriteString("epochId", request.EpochId);
				writer.WriteString("documentPath", request.DocumentPath);
				writer.WriteNumber("clientVersion", request.ClientVersion);
				writer.WriteNumber("line", request.Line);
				writer.WriteNumber("character", request.Character);
				writer.WriteEndObject();
			}
			body = stream.ToArray();
		}
		catch (Exception exception)
		{
			return CodeServiceCompletionResult.Failure(
				CodeServiceCompletionOutcome.LocalInvalidRequest,
				"Completion request serialization failed: " + ToSingleLine(exception.Message)
			);
		}

		if (body.Length > CodeServiceCompletionLimits.MaxRequestBodySizeBytes)
		{
			return CodeServiceCompletionResult.Failure(
				CodeServiceCompletionOutcome.LocalInvalidRequest,
				"Completion request body exceeds the local endpoint bound."
			);
		}

		string requestId = Guid.NewGuid().ToString("D");
		TransportResponse transport = await SendAsync(
			requestId,
			body,
			cancellationToken
		).ConfigureAwait(false);

		if (!transport.HasHttpResponse)
			return CodeServiceCompletionResult.Failure(transport.Outcome, transport.Detail);
		if (transport.StatusCode == (int)HttpStatusCode.ServiceUnavailable && transport.Body.Length == 0)
		{
			return CodeServiceCompletionResult.Failure(
				CodeServiceCompletionOutcome.CompletionUnavailableForSession,
				"Completion endpoint is unavailable for this logical service session."
			);
		}
		if (transport.StatusCode == (int)HttpStatusCode.Unauthorized && transport.Body.Length == 0)
		{
			return CodeServiceCompletionResult.Failure(
				CodeServiceCompletionOutcome.AuthenticationFailed,
				"Completion authentication failed."
			);
		}
		if (transport.Body.Length == 0)
		{
			return CodeServiceCompletionResult.Failure(
				CodeServiceCompletionOutcome.MalformedResponse,
				$"Completion returned HTTP {transport.StatusCode} with an unexpected zero-length body."
			);
		}

		return ParseResponse(
			transport.StatusCode,
			transport.Body,
			requestId,
			request
		);
	}

	private async Task<TransportResponse> SendAsync(
		string requestId,
		byte[] body,
		CancellationToken cancellationToken
	)
	{
		HttpClient httpClient = Volatile.Read(ref _httpClient);
		CodeServiceClientCredentials credentials = Volatile.Read(ref _credentials);
		if (httpClient == null || credentials == null)
		{
			return TransportResponse.Failure(
				CodeServiceCompletionOutcome.Disposed,
				"Completion client admission is closed."
			);
		}

		using HttpRequestMessage httpRequest = new(
			HttpMethod.Post,
			CodeServiceClientProtocol.CompletionPath
		);
		try
		{
			httpRequest.Headers.TryAddWithoutValidation(
				"Authorization",
				credentials.CreateBearerAuthorizationValue()
			);
			httpRequest.Headers.TryAddWithoutValidation(
				CodeServiceClientProtocol.ProtocolVersionHeaderName,
				CodeServiceClientProtocol.ProtocolVersion.ToString(CultureInfo.InvariantCulture)
			);
			httpRequest.Headers.TryAddWithoutValidation(
				CodeServiceClientProtocol.SessionIdHeaderName,
				_sessionId
			);
			httpRequest.Headers.TryAddWithoutValidation(
				CodeServiceClientProtocol.RequestIdHeaderName,
				requestId
			);
			httpRequest.Content = new ByteArrayContent(body);
			httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
		}
		catch (Exception exception)
		{
			return TransportResponse.Failure(
				CodeServiceCompletionOutcome.TransportUnavailable,
				"Completion request preparation failed: " + ToSingleLine(exception.Message)
			);
		}

		using CancellationTokenSource requestCancellation =
			CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		requestCancellation.CancelAfter(RequestTimeout);

		HttpResponseMessage response;
		try
		{
			response = await httpClient.SendAsync(
				httpRequest,
				HttpCompletionOption.ResponseHeadersRead,
				requestCancellation.Token
			).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (OperationCanceledException)
		{
			return TransportResponse.Failure(
				CodeServiceCompletionOutcome.TransportUnavailable,
				"Completion request exceeded the bounded request deadline."
			);
		}
		catch (HttpRequestException exception)
		{
			return TransportResponse.Failure(
				CodeServiceCompletionOutcome.TransportUnavailable,
				"Completion transport failed: " + ToSingleLine(exception.Message)
			);
		}
		catch (ObjectDisposedException exception)
		{
			return TransportResponse.Failure(
				CodeServiceCompletionOutcome.TransportUnavailable,
				"Completion transport was disposed: " + ToSingleLine(exception.Message)
			);
		}
		catch (Exception exception)
		{
			return TransportResponse.Failure(
				CodeServiceCompletionOutcome.TransportUnavailable,
				"Completion transport failed: " + ToSingleLine(exception.Message)
			);
		}

		using (response)
		{
			byte[] responseBody;
			try
			{
				responseBody = await ReadBoundedBodyAsync(
					response,
					requestCancellation.Token
				).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (OperationCanceledException)
			{
				return TransportResponse.Failure(
					CodeServiceCompletionOutcome.TransportUnavailable,
					"Completion response body exceeded the bounded request deadline."
				);
			}
			catch (InvalidDataException exception)
			{
				return TransportResponse.Failure(
					CodeServiceCompletionOutcome.MalformedResponse,
					"Completion response violated the bounded response contract: "
					+ ToSingleLine(exception.Message)
				);
			}
			catch (HttpRequestException exception)
			{
				return TransportResponse.Failure(
					CodeServiceCompletionOutcome.TransportUnavailable,
					"Completion response transport failed: " + ToSingleLine(exception.Message)
				);
			}
			catch (ObjectDisposedException exception)
			{
				return TransportResponse.Failure(
					CodeServiceCompletionOutcome.TransportUnavailable,
					"Completion response transport was disposed: " + ToSingleLine(exception.Message)
				);
			}
			catch (IOException exception)
			{
				return TransportResponse.Failure(
					CodeServiceCompletionOutcome.TransportUnavailable,
					"Completion response transport failed: " + ToSingleLine(exception.Message)
				);
			}
			catch (Exception exception)
			{
				return TransportResponse.Failure(
					CodeServiceCompletionOutcome.MalformedResponse,
					"Completion response body could not be read safely: "
					+ ToSingleLine(exception.Message)
				);
			}

			return TransportResponse.Http((int)response.StatusCode, responseBody);
		}
	}

	private static async Task<byte[]> ReadBoundedBodyAsync(
		HttpResponseMessage response,
		CancellationToken cancellationToken
	)
	{
		if (response.Content == null)
			return Array.Empty<byte>();
		if (response.Content.Headers.ContentLength is long length
			&& length > CodeServiceCompletionLimits.MaxResponseBodySizeBytes)
		{
			throw new InvalidDataException("Completion response exceeds the configured response bound.");
		}

		await using Stream stream = await response.Content
			.ReadAsStreamAsync(cancellationToken)
			.ConfigureAwait(false);
		using MemoryStream buffer = new();
		byte[] chunk = new byte[8192];
		while (true)
		{
			int read = await stream.ReadAsync(
				chunk.AsMemory(0, chunk.Length),
				cancellationToken
			).ConfigureAwait(false);
			if (read == 0)
				break;
			if (buffer.Length + read > CodeServiceCompletionLimits.MaxResponseBodySizeBytes)
			{
				throw new InvalidDataException(
					"Completion response exceeds the configured response bound."
				);
			}
			buffer.Write(chunk, 0, read);
		}
		return buffer.ToArray();
	}

	private static CodeServiceCompletionResult ParseResponse(
		int statusCode,
		byte[] bytes,
		string expectedRequestId,
		CodeServiceCompletionRequest expectedRequest
	)
	{
		try
		{
			using JsonDocument document = JsonDocument.Parse(
				bytes,
				new JsonDocumentOptions
				{
					AllowTrailingCommas = false,
					CommentHandling = JsonCommentHandling.Disallow,
					MaxDepth = 16,
				}
			);
			JsonElement root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
				return Malformed("Completion response root must be an object.");

			int schemaCount = 0;
			int outcomeCount = 0;
			int requestIdCount = 0;
			int clientGenerationCount = 0;
			int epochIdCount = 0;
			int documentPathCount = 0;
			int acceptedClientVersionCount = 0;
			int workspaceGenerationCount = 0;
			int workspacePublicationVersionCount = 0;
			int roslynGenerationCount = 0;
			int roslynDocumentVersionCount = 0;
			int roslynOverlayRevisionCount = 0;
			int isIncompleteCount = 0;
			int itemsCount = 0;

			int schemaVersion = 0;
			string outcomeText = null;
			string requestId = null;
			long? clientGeneration = null;
			string epochId = null;
			string documentPath = null;
			long? acceptedClientVersion = null;
			long? workspaceGeneration = null;
			long? workspacePublicationVersion = null;
			long? roslynGeneration = null;
			int? roslynDocumentVersion = null;
			long? roslynOverlayRevision = null;
			bool isIncomplete = false;
			IReadOnlyList<CodeServiceCompletionItem> items = Array.Empty<CodeServiceCompletionItem>();

			foreach (JsonProperty property in root.EnumerateObject())
			{
				switch (property.Name)
				{
					case "schemaVersion":
						if (++schemaCount != 1
							|| property.Value.ValueKind != JsonValueKind.Number
							|| !property.Value.TryGetInt32(out schemaVersion))
							return Malformed("Invalid schemaVersion.");
						break;
					case "outcome":
						if (++outcomeCount != 1 || property.Value.ValueKind != JsonValueKind.String)
							return Malformed("Invalid outcome.");
						outcomeText = property.Value.GetString();
						if (string.IsNullOrEmpty(outcomeText))
							return Malformed("Invalid outcome.");
						break;
					case "requestId":
						if (++requestIdCount != 1
							|| property.Value.ValueKind != JsonValueKind.String)
							return Malformed("Invalid requestId.");
						requestId = property.Value.GetString();
						if (!TryGetCanonicalGuid(requestId, out _))
							return Malformed("Invalid requestId.");
						break;
					case "clientGeneration":
						if (++clientGenerationCount != 1
							|| !TryReadNullablePositiveInt64(property.Value, out clientGeneration))
							return Malformed("Invalid clientGeneration.");
						break;
					case "epochId":
						if (++epochIdCount != 1
							|| !TryReadNullableCanonicalGuid(property.Value, out epochId))
							return Malformed("Invalid epochId.");
						break;
					case "documentPath":
						if (++documentPathCount != 1
							|| !TryReadNullableString(property.Value, out documentPath))
							return Malformed("Invalid documentPath.");
						break;
					case "acceptedClientVersion":
						if (++acceptedClientVersionCount != 1
							|| !TryReadNullablePositiveInt64(property.Value, out acceptedClientVersion))
							return Malformed("Invalid acceptedClientVersion.");
						break;
					case "workspaceGeneration":
						if (++workspaceGenerationCount != 1
							|| !TryReadNullablePositiveInt64(property.Value, out workspaceGeneration))
							return Malformed("Invalid workspaceGeneration.");
						break;
					case "workspacePublicationVersion":
						if (++workspacePublicationVersionCount != 1
							|| !TryReadNullablePositiveInt64(property.Value, out workspacePublicationVersion))
							return Malformed("Invalid workspacePublicationVersion.");
						break;
					case "roslynGeneration":
						if (++roslynGenerationCount != 1
							|| !TryReadNullablePositiveInt64(property.Value, out roslynGeneration))
							return Malformed("Invalid roslynGeneration.");
						break;
					case "roslynDocumentVersion":
						if (++roslynDocumentVersionCount != 1
							|| !TryReadNullablePositiveInt32(property.Value, out roslynDocumentVersion))
							return Malformed("Invalid roslynDocumentVersion.");
						break;
					case "roslynOverlayRevision":
						if (++roslynOverlayRevisionCount != 1
							|| !TryReadNullablePositiveInt64(property.Value, out roslynOverlayRevision))
							return Malformed("Invalid roslynOverlayRevision.");
						break;
					case "isIncomplete":
						if (++isIncompleteCount != 1
							|| (property.Value.ValueKind != JsonValueKind.True
								&& property.Value.ValueKind != JsonValueKind.False))
							return Malformed("Invalid isIncomplete.");
						isIncomplete = property.Value.GetBoolean();
						break;
					case "items":
						if (++itemsCount != 1)
							return Malformed("Completion response contained duplicate items.");
						if (!TryParseItems(property.Value, out items, out string itemsDetail))
							return Malformed(itemsDetail);
						break;
					default:
						return Malformed("Completion response contained an unknown property.");
				}
			}

			if (schemaCount != 1
				|| outcomeCount != 1
				|| requestIdCount != 1
				|| clientGenerationCount != 1
				|| epochIdCount != 1
				|| documentPathCount != 1
				|| acceptedClientVersionCount != 1
				|| workspaceGenerationCount != 1
				|| workspacePublicationVersionCount != 1
				|| roslynGenerationCount != 1
				|| roslynDocumentVersionCount != 1
				|| roslynOverlayRevisionCount != 1
				|| isIncompleteCount != 1
				|| itemsCount != 1)
			{
				return Malformed("Completion response omitted one or more required properties.");
			}
			if (schemaVersion != CodeServiceClientProtocol.CompletionSchemaVersion)
				return Malformed("Completion response schemaVersion mismatch.");
			if (!TryMapServiceOutcome(outcomeText, out CodeServiceCompletionOutcome outcome)
				|| !StatusMatchesOutcome(statusCode, outcome))
				return Malformed("Completion response outcome/status combination is invalid.");
			if (!string.Equals(requestId, expectedRequestId, StringComparison.Ordinal))
				return Malformed("Completion response requestId mismatch.");

			if (clientGeneration.HasValue
				&& clientGeneration.Value != expectedRequest.ClientGeneration)
				return Malformed("Completion response clientGeneration echo mismatch.");
			if (epochId != null
				&& !string.Equals(epochId, expectedRequest.EpochId, StringComparison.Ordinal))
				return Malformed("Completion response epochId echo mismatch.");
			if (documentPath != null
				&& !string.Equals(documentPath, expectedRequest.DocumentPath, StringComparison.Ordinal))
				return Malformed("Completion response documentPath echo mismatch.");

			if (outcome == CodeServiceCompletionOutcome.Success)
			{
				if (clientGeneration != expectedRequest.ClientGeneration
					|| !string.Equals(epochId, expectedRequest.EpochId, StringComparison.Ordinal)
					|| !string.Equals(documentPath, expectedRequest.DocumentPath, StringComparison.Ordinal)
					|| acceptedClientVersion != expectedRequest.ClientVersion
					|| !workspaceGeneration.HasValue || workspaceGeneration.Value <= 0
					|| !workspacePublicationVersion.HasValue || workspacePublicationVersion.Value <= 0
					|| !roslynGeneration.HasValue || roslynGeneration.Value <= 0
					|| !roslynDocumentVersion.HasValue || roslynDocumentVersion.Value <= 0
					|| !roslynOverlayRevision.HasValue || roslynOverlayRevision.Value <= 0)
				{
					return Malformed(
						"Successful completion response did not strictly match the request/publication identity."
					);
				}
			}

			return new CodeServiceCompletionResult(
				outcome,
				requestId,
				clientGeneration,
				epochId ?? "",
				documentPath ?? "",
				acceptedClientVersion,
				workspaceGeneration,
				workspacePublicationVersion,
				roslynGeneration,
				roslynDocumentVersion,
				roslynOverlayRevision,
				isIncomplete,
				items,
				""
			);
		}
		catch (JsonException exception)
		{
			return Malformed(
				"Completion response JSON was invalid: " + ToSingleLine(exception.Message)
			);
		}
	}

	private static bool TryParseItems(
		JsonElement element,
		out IReadOnlyList<CodeServiceCompletionItem> items,
		out string detail
	)
	{
		items = Array.Empty<CodeServiceCompletionItem>();
		detail = "";
		if (element.ValueKind != JsonValueKind.Array)
		{
			detail = "Completion items must be an array.";
			return false;
		}

		List<CodeServiceCompletionItem> parsed = new();
		int normalizedUtf8Bytes = 0;
		foreach (JsonElement item in element.EnumerateArray())
		{
			if (parsed.Count >= CodeServiceCompletionLimits.MaxCompletionItems)
			{
				detail = "Completion item count exceeds the local bound.";
				return false;
			}
			if (item.ValueKind != JsonValueKind.Object)
			{
				detail = "Each completion item must be an object.";
				return false;
			}

			int kindCount = 0;
			int displayTextCount = 0;
			int insertTextCount = 0;
			int filterTextCount = 0;
			int sortTextCount = 0;
			int preselectCount = 0;
			int? kind = null;
			string displayText = null;
			string insertText = null;
			string filterText = null;
			string sortText = null;
			bool preselect = false;

			foreach (JsonProperty property in item.EnumerateObject())
			{
				switch (property.Name)
				{
					case "kind":
						if (++kindCount != 1
							|| !TryReadNullableInt32(property.Value, out kind))
						{
							detail = "Completion item kind is invalid.";
							return false;
						}
						break;
					case "displayText":
						if (++displayTextCount != 1
							|| property.Value.ValueKind != JsonValueKind.String)
						{
							detail = "Completion item displayText is invalid.";
							return false;
						}
						displayText = property.Value.GetString();
						if (displayText == null)
						{
							detail = "Completion item displayText is invalid.";
							return false;
						}
						break;
					case "insertText":
						if (++insertTextCount != 1
							|| property.Value.ValueKind != JsonValueKind.String)
						{
							detail = "Completion item insertText is invalid.";
							return false;
						}
						insertText = property.Value.GetString();
						if (insertText == null)
						{
							detail = "Completion item insertText is invalid.";
							return false;
						}
						break;
					case "filterText":
						if (++filterTextCount != 1
							|| property.Value.ValueKind != JsonValueKind.String)
						{
							detail = "Completion item filterText is invalid.";
							return false;
						}
						filterText = property.Value.GetString();
						if (filterText == null)
						{
							detail = "Completion item filterText is invalid.";
							return false;
						}
						break;
					case "sortText":
						if (++sortTextCount != 1
							|| property.Value.ValueKind != JsonValueKind.String)
						{
							detail = "Completion item sortText is invalid.";
							return false;
						}
						sortText = property.Value.GetString();
						if (sortText == null)
						{
							detail = "Completion item sortText is invalid.";
							return false;
						}
						break;
					case "preselect":
						if (++preselectCount != 1
							|| (property.Value.ValueKind != JsonValueKind.True
								&& property.Value.ValueKind != JsonValueKind.False))
						{
							detail = "Completion item preselect is invalid.";
							return false;
						}
						preselect = property.Value.GetBoolean();
						break;
					default:
						detail = "Completion item contained an unknown property.";
						return false;
				}
			}

			if (kindCount != 1
				|| displayTextCount != 1
				|| insertTextCount != 1
				|| filterTextCount != 1
				|| sortTextCount != 1
				|| preselectCount != 1)
			{
				detail = "Completion item omitted one or more required properties.";
				return false;
			}

			if (!TryGetBoundedUtf8ByteCount(
				displayText,
				CodeServiceCompletionLimits.MaxDisplayTextUtf8Bytes,
				out int displayTextBytes))
			{
				detail = "Completion item displayText exceeds the local UTF-8 bound.";
				return false;
			}
			if (!TryGetBoundedUtf8ByteCount(
				insertText,
				CodeServiceCompletionLimits.MaxInsertTextUtf8Bytes,
				out int insertTextBytes))
			{
				detail = "Completion item insertText exceeds the local UTF-8 bound.";
				return false;
			}
			if (!TryGetBoundedUtf8ByteCount(
				filterText,
				CodeServiceCompletionLimits.MaxFilterTextUtf8Bytes,
				out int filterTextBytes))
			{
				detail = "Completion item filterText exceeds the local UTF-8 bound.";
				return false;
			}
			if (!TryGetBoundedUtf8ByteCount(
				sortText,
				CodeServiceCompletionLimits.MaxSortTextUtf8Bytes,
				out int sortTextBytes))
			{
				detail = "Completion item sortText exceeds the local UTF-8 bound.";
				return false;
			}

			int itemBytes;
			try
			{
				itemBytes = checked(
					displayTextBytes
					+ insertTextBytes
					+ filterTextBytes
					+ sortTextBytes
				);
			}
			catch (OverflowException)
			{
				detail = "Completion item UTF-8 accounting overflowed.";
				return false;
			}
			if (normalizedUtf8Bytes
				> CodeServiceCompletionLimits.MaxNormalizedCompletionTextUtf8Bytes - itemBytes)
			{
				detail = "Completion item text exceeds the aggregate local UTF-8 bound.";
				return false;
			}

			normalizedUtf8Bytes += itemBytes;
			parsed.Add(new CodeServiceCompletionItem(
				kind,
				displayText,
				insertText,
				filterText,
				sortText,
				preselect
			));
		}

		items = parsed.AsReadOnly();
		return true;
	}

	private static bool TryValidateRequest(
		CodeServiceCompletionRequest request,
		out string detail
	)
	{
		detail = "";
		if (request.ClientGeneration <= 0)
		{
			detail = "Completion clientGeneration must be positive.";
			return false;
		}
		if (!TryGetCanonicalGuid(request.EpochId, out Guid epochId) || epochId == Guid.Empty)
		{
			detail = "Completion epochId must be a non-empty canonical lower-case GUID D string.";
			return false;
		}
		if (!CodeServiceDocumentPath.TryValidateWirePath(request.DocumentPath, out detail))
			return false;
		if (request.ClientVersion <= 0)
		{
			detail = "Completion clientVersion must be positive.";
			return false;
		}
		if (request.Line < 0 || request.Line > CodeServiceCompletionLimits.MaxCompletionLine)
		{
			detail = "Completion line is outside the local bound.";
			return false;
		}
		if (request.Character < 0
			|| request.Character > CodeServiceCompletionLimits.MaxCompletionCharacter)
		{
			detail = "Completion character is outside the local bound.";
			return false;
		}
		return true;
	}

	private static bool TryMapServiceOutcome(
		string value,
		out CodeServiceCompletionOutcome outcome
	)
	{
		outcome = value switch
		{
			CodeServiceClientProtocol.DocumentSuccessOutcome => CodeServiceCompletionOutcome.Success,
			CodeServiceClientProtocol.DocumentInvalidRequestOutcome => CodeServiceCompletionOutcome.InvalidRequest,
			CodeServiceClientProtocol.DocumentVersionMismatchOutcome => CodeServiceCompletionOutcome.VersionMismatch,
			CodeServiceClientProtocol.DocumentBusyOutcome => CodeServiceCompletionOutcome.Busy,
			CodeServiceClientProtocol.DocumentWorkspaceUnavailableOutcome => CodeServiceCompletionOutcome.WorkspaceUnavailable,
			CodeServiceClientProtocol.DocumentRoslynUnavailableOutcome => CodeServiceCompletionOutcome.RoslynUnavailable,
			CodeServiceClientProtocol.SemanticUnavailableOutcome => CodeServiceCompletionOutcome.SemanticUnavailable,
			CodeServiceClientProtocol.CompletionUnavailableOutcome => CodeServiceCompletionOutcome.CompletionUnavailable,
			CodeServiceClientProtocol.DocumentStaleEpochOutcome => CodeServiceCompletionOutcome.StaleEpoch,
			CodeServiceClientProtocol.DocumentEpochConflictOutcome => CodeServiceCompletionOutcome.EpochConflict,
			CodeServiceClientProtocol.DocumentStaleVersionOutcome => CodeServiceCompletionOutcome.StaleVersion,
			CodeServiceClientProtocol.DocumentNotSynchronizedOutcome => CodeServiceCompletionOutcome.DocumentNotSynchronized,
			CodeServiceClientProtocol.DocumentNotOpenOutcome => CodeServiceCompletionOutcome.DocumentNotOpen,
			CodeServiceClientProtocol.DocumentNotInWorkspaceOutcome => CodeServiceCompletionOutcome.DocumentNotInWorkspace,
			CodeServiceClientProtocol.DocumentUnavailableOutcome => CodeServiceCompletionOutcome.Unavailable,
			_ => (CodeServiceCompletionOutcome)(-1),
		};
		return (int)outcome >= 0;
	}

	private static bool StatusMatchesOutcome(
		int statusCode,
		CodeServiceCompletionOutcome outcome
	) => outcome switch
	{
		CodeServiceCompletionOutcome.Success => statusCode == 200,
		CodeServiceCompletionOutcome.InvalidRequest => statusCode == 400,
		CodeServiceCompletionOutcome.VersionMismatch
			or CodeServiceCompletionOutcome.StaleEpoch
			or CodeServiceCompletionOutcome.EpochConflict
			or CodeServiceCompletionOutcome.StaleVersion
			or CodeServiceCompletionOutcome.DocumentNotSynchronized
			or CodeServiceCompletionOutcome.DocumentNotOpen
			or CodeServiceCompletionOutcome.DocumentNotInWorkspace => statusCode == 409,
		CodeServiceCompletionOutcome.Busy
			or CodeServiceCompletionOutcome.WorkspaceUnavailable
			or CodeServiceCompletionOutcome.RoslynUnavailable
			or CodeServiceCompletionOutcome.SemanticUnavailable
			or CodeServiceCompletionOutcome.CompletionUnavailable
			or CodeServiceCompletionOutcome.Unavailable => statusCode == 503,
		_ => false,
	};

	private static bool TryReadNullableString(JsonElement element, out string value)
	{
		value = null;
		if (element.ValueKind == JsonValueKind.Null)
			return true;
		if (element.ValueKind != JsonValueKind.String)
			return false;
		value = element.GetString();
		return value != null;
	}

	private static bool TryReadNullableCanonicalGuid(JsonElement element, out string value)
	{
		value = null;
		if (element.ValueKind == JsonValueKind.Null)
			return true;
		if (element.ValueKind != JsonValueKind.String)
			return false;
		string text = element.GetString();
		if (!TryGetCanonicalGuid(text, out _))
			return false;
		value = text;
		return true;
	}

	private static bool TryReadNullablePositiveInt64(JsonElement element, out long? value)
	{
		value = null;
		if (element.ValueKind == JsonValueKind.Null)
			return true;
		if (element.ValueKind != JsonValueKind.Number
			|| !element.TryGetInt64(out long parsed)
			|| parsed <= 0)
			return false;
		value = parsed;
		return true;
	}

	private static bool TryReadNullablePositiveInt32(JsonElement element, out int? value)
	{
		value = null;
		if (element.ValueKind == JsonValueKind.Null)
			return true;
		if (element.ValueKind != JsonValueKind.Number
			|| !element.TryGetInt32(out int parsed)
			|| parsed <= 0)
			return false;
		value = parsed;
		return true;
	}

	private static bool TryReadNullableInt32(JsonElement element, out int? value)
	{
		value = null;
		if (element.ValueKind == JsonValueKind.Null)
			return true;
		if (element.ValueKind != JsonValueKind.Number
			|| !element.TryGetInt32(out int parsed))
			return false;
		value = parsed;
		return true;
	}

	private static bool TryGetBoundedUtf8ByteCount(
		string value,
		int maximum,
		out int byteCount
	)
	{
		byteCount = 0;
		try
		{
			byteCount = Encoding.UTF8.GetByteCount(value ?? "");
			return byteCount <= maximum;
		}
		catch
		{
			return false;
		}
	}

	private static bool TryGetCanonicalGuid(string text, out Guid guid)
	{
		guid = default;
		return !string.IsNullOrEmpty(text)
			&& Guid.TryParseExact(text, "D", out guid)
			&& string.Equals(guid.ToString("D"), text, StringComparison.Ordinal);
	}

	private static CodeServiceCompletionResult Malformed(string detail) =>
		CodeServiceCompletionResult.Failure(
			CodeServiceCompletionOutcome.MalformedResponse,
			detail
		);

	private static string ToSingleLine(string value) =>
		(value ?? "").Replace('\r', ' ').Replace('\n', ' ');

	private readonly record struct TransportResponse(
		bool HasHttpResponse,
		int StatusCode,
		byte[] Body,
		CodeServiceCompletionOutcome Outcome,
		string Detail)
	{
		internal static TransportResponse Http(int statusCode, byte[] body) =>
			new(true, statusCode, body ?? Array.Empty<byte>(), default, "");

		internal static TransportResponse Failure(
			CodeServiceCompletionOutcome outcome,
			string detail
		) => new(false, 0, Array.Empty<byte>(), outcome, detail ?? "");
	}
}
#endif
