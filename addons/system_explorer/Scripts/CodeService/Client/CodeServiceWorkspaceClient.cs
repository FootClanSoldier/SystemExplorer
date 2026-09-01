#if TOOLS
using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SystemExplorer.CodeService.Client;

internal sealed class CodeServiceWorkspaceClient
{
	private const int MaxJsonDepth = 8;
	private const int MaxOutcomeLength = 64;
	private const int MaxStateLength = 64;
	private const int MaxRequestIdLength = 64;
	private const int MaxFaultKindLength = 256;

	private readonly HttpClient _httpClient;
	private readonly CodeServiceClientCredentials _credentials;
	private readonly string _sessionId;

	internal CodeServiceWorkspaceClient(
		HttpClient httpClient,
		CodeServiceClientCredentials credentials,
		string sessionId
	)
	{
		_httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
		_credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
		if (string.IsNullOrWhiteSpace(sessionId))
			throw new ArgumentException("CodeService sessionId is required.", nameof(sessionId));
		_sessionId = sessionId;
	}

	internal async Task<CodeServiceWorkspaceInitializeResult> InitializeAsync(
		string projectRoot,
		CancellationToken cancellationToken
	)
	{
		if (
			!CodeServiceWorkspacePath.TryNormalize(
				projectRoot,
				out string normalizedProjectRoot,
				out string normalizationDetail
			)
		)
		{
			return CodeServiceWorkspaceInitializeResult.InvalidRequest(normalizationDetail);
		}

		byte[] bodyBytes;
		try
		{
			bodyBytes = CreateWorkspaceInitializeBody(normalizedProjectRoot);
		}
		catch (Exception exception)
		{
			return CodeServiceWorkspaceInitializeResult.InvalidRequest(
				"workspace initialize body could not be encoded safely: "
				+ ToSingleLine(exception.Message)
			);
		}

		if (bodyBytes.Length > CodeServiceClientProtocol.MaxWorkspaceInitializeBodySizeBytes)
		{
			return CodeServiceWorkspaceInitializeResult.InvalidRequest(
				$"workspace initialize body exceeded the {CodeServiceClientProtocol.MaxWorkspaceInitializeBodySizeBytes} byte boundary."
			);
		}

		string requestId = Guid.NewGuid().ToString("D");
		using HttpRequestMessage request = new(
			HttpMethod.Post,
			CodeServiceClientProtocol.WorkspaceInitializePath
		);
		try
		{
			AddAuthenticatedHeaders(request, requestId);
		}
		catch (ObjectDisposedException exception)
		{
			return CodeServiceWorkspaceInitializeResult.TransportUnavailable(
				"workspace initialize credentials were retired: " + ToSingleLine(exception.Message)
			);
		}
		catch (Exception exception)
		{
			return CodeServiceWorkspaceInitializeResult.TransportUnavailable(
				"workspace initialize authentication headers could not be prepared: "
				+ ToSingleLine(exception.Message)
			);
		}
		request.Content = new ByteArrayContent(bodyBytes);
		request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
		{
			CharSet = "utf-8",
		};

		HttpResponseMessage response;
		try
		{
			response = await _httpClient
				.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (HttpRequestException exception)
		{
			return CodeServiceWorkspaceInitializeResult.TransportUnavailable(
				"workspace initialize transport failed: " + ToSingleLine(exception.Message)
			);
		}
		catch (ObjectDisposedException exception)
		{
			return CodeServiceWorkspaceInitializeResult.TransportUnavailable(
				"workspace initialize transport was retired: " + ToSingleLine(exception.Message)
			);
		}
		catch (Exception exception)
		{
			return CodeServiceWorkspaceInitializeResult.TransportUnavailable(
				"workspace initialize transport failed: " + ToSingleLine(exception.Message)
			);
		}

		using (response)
		{
			BoundedWorkspaceBodyResult bodyResult;
			try
			{
				bodyResult = await ReadBoundedBodyAsync(response, cancellationToken)
					.ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception exception)
			{
				return CodeServiceWorkspaceInitializeResult.MalformedResponse(
					"workspace initialize response body could not be read safely: "
					+ ToSingleLine(exception.Message)
				);
			}

			if (!bodyResult.IsSuccess)
				return CodeServiceWorkspaceInitializeResult.MalformedResponse(bodyResult.Detail);

			return ValidateInitializeResponse(
				(int)response.StatusCode,
				bodyResult.Bytes,
				requestId,
				normalizedProjectRoot
			);
		}
	}

	internal async Task<CodeServiceWorkspaceStatusResult> GetStatusAsync(
		CancellationToken cancellationToken
	)
	{
		string requestId = Guid.NewGuid().ToString("D");
		using HttpRequestMessage request = new(
			HttpMethod.Post,
			CodeServiceClientProtocol.WorkspaceStatusPath
		);
		try
		{
			AddAuthenticatedHeaders(request, requestId);
		}
		catch (ObjectDisposedException exception)
		{
			return CodeServiceWorkspaceStatusResult.TransportUnavailable(
				"workspace status credentials were retired: " + ToSingleLine(exception.Message)
			);
		}
		catch (Exception exception)
		{
			return CodeServiceWorkspaceStatusResult.TransportUnavailable(
				"workspace status authentication headers could not be prepared: "
				+ ToSingleLine(exception.Message)
			);
		}

		HttpResponseMessage response;
		try
		{
			response = await _httpClient
				.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (HttpRequestException exception)
		{
			return CodeServiceWorkspaceStatusResult.TransportUnavailable(
				"workspace status transport failed: " + ToSingleLine(exception.Message)
			);
		}
		catch (ObjectDisposedException exception)
		{
			return CodeServiceWorkspaceStatusResult.TransportUnavailable(
				"workspace status transport was retired: " + ToSingleLine(exception.Message)
			);
		}
		catch (Exception exception)
		{
			return CodeServiceWorkspaceStatusResult.TransportUnavailable(
				"workspace status transport failed: " + ToSingleLine(exception.Message)
			);
		}

		using (response)
		{
			BoundedWorkspaceBodyResult bodyResult;
			try
			{
				bodyResult = await ReadBoundedBodyAsync(response, cancellationToken)
					.ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception exception)
			{
				return CodeServiceWorkspaceStatusResult.MalformedResponse(
					"workspace status response body could not be read safely: "
					+ ToSingleLine(exception.Message)
				);
			}

			if (!bodyResult.IsSuccess)
				return CodeServiceWorkspaceStatusResult.MalformedResponse(bodyResult.Detail);

			return ValidateStatusResponse(
				(int)response.StatusCode,
				bodyResult.Bytes,
				requestId
			);
		}
	}

	private static byte[] CreateWorkspaceInitializeBody(string normalizedProjectRoot)
	{
		// Keep outbound CodeService wire encoding independent of reflection metadata for reloadable plugin-defined types.
		using MemoryStream stream = new();
		using (Utf8JsonWriter writer = new(stream))
		{
			writer.WriteStartObject();
			writer.WriteNumber(
				"schemaVersion",
				CodeServiceClientProtocol.WorkspaceSchemaVersion
			);
			writer.WriteString("projectRoot", normalizedProjectRoot);
			writer.WriteEndObject();
		}

		return stream.ToArray();
	}

	private void AddAuthenticatedHeaders(HttpRequestMessage request, string requestId)
	{
		string authorizationValue = _credentials.CreateBearerAuthorizationValue();
		request.Headers.TryAddWithoutValidation("Authorization", authorizationValue);
		request.Headers.TryAddWithoutValidation(
			CodeServiceClientProtocol.ProtocolVersionHeaderName,
			CodeServiceClientProtocol.ProtocolVersion.ToString(CultureInfo.InvariantCulture)
		);
		request.Headers.TryAddWithoutValidation(
			CodeServiceClientProtocol.SessionIdHeaderName,
			_sessionId
		);
		request.Headers.TryAddWithoutValidation(
			CodeServiceClientProtocol.RequestIdHeaderName,
			requestId
		);
	}

	private static CodeServiceWorkspaceInitializeResult ValidateInitializeResponse(
		int statusCode,
		byte[] bytes,
		string sentRequestId,
		string normalizedProjectRoot
	)
	{
		if (statusCode == (int)HttpStatusCode.Unauthorized)
		{
			return bytes.Length == 0
				? CodeServiceWorkspaceInitializeResult.AuthenticationFailed()
				: CodeServiceWorkspaceInitializeResult.MalformedResponse(
					"HTTP 401 workspace initialize response was expected to have zero body bytes."
				);
		}

		if (statusCode == (int)HttpStatusCode.MethodNotAllowed)
		{
			return bytes.Length == 0
				? CodeServiceWorkspaceInitializeResult.InvalidRequest(
					"workspace initialize endpoint rejected the HTTP method."
				)
				: CodeServiceWorkspaceInitializeResult.MalformedResponse(
					"HTTP 405 workspace initialize response was expected to have zero body bytes."
				);
		}

		if (statusCode == (int)HttpStatusCode.ServiceUnavailable && bytes.Length == 0)
			return CodeServiceWorkspaceInitializeResult.ControlPlaneUnavailable();

		if (bytes.Length == 0)
		{
			return CodeServiceWorkspaceInitializeResult.MalformedResponse(
				$"workspace initialize returned empty HTTP {statusCode} response."
			);
		}

		if (!TryParseWireResponse(bytes, allowReusedExistingWorkspace: true, out WorkspaceWireResponse wire, out string parseDetail))
			return CodeServiceWorkspaceInitializeResult.MalformedResponse(parseDetail);

		if (!ValidateCommonWireResponse(wire, sentRequestId, out string commonDetail))
			return CodeServiceWorkspaceInitializeResult.MalformedResponse(commonDetail);

		if (statusCode == (int)HttpStatusCode.OK)
		{
			if (
				!string.Equals(wire.Outcome, CodeServiceClientProtocol.WorkspaceSuccessOutcome, StringComparison.Ordinal)
				|| wire.State != CodeServiceWorkspaceState.Ready
				|| !wire.HasReusedExistingWorkspace
				|| !wire.ReusedExistingWorkspace.HasValue
				|| string.IsNullOrEmpty(wire.ProjectRoot)
				|| !CodeServiceWorkspacePath.EqualsNormalized(wire.ProjectRoot, normalizedProjectRoot)
				|| !string.IsNullOrEmpty(wire.FaultKind)
			)
			{
				return CodeServiceWorkspaceInitializeResult.MalformedResponse(
					"HTTP 200 workspace initialize response did not satisfy the Success/Ready identity contract."
				);
			}

			return CodeServiceWorkspaceInitializeResult.Ready(
				wire.ProjectRoot,
				wire.ReusedExistingWorkspace.Value,
				wire.SourceFileCount,
				wire.ProjectFileCount,
				wire.SolutionFileCount
			);
		}

		if (statusCode == (int)HttpStatusCode.BadRequest)
		{
			if (!MatchesFailure(wire, CodeServiceClientProtocol.WorkspaceInvalidRequestOutcome))
			{
				return CodeServiceWorkspaceInitializeResult.MalformedResponse(
					"HTTP 400 workspace initialize response was not InvalidRequest."
				);
			}

			return CodeServiceWorkspaceInitializeResult.InvalidRequest(
				"CodeService rejected the workspace initialize request.",
				wire
			);
		}

		if (statusCode == (int)HttpStatusCode.Conflict)
		{
			if (string.Equals(wire.Outcome, CodeServiceClientProtocol.WorkspaceVersionMismatchOutcome, StringComparison.Ordinal))
			{
				if (!ValidateFailureReuseField(wire))
					return CodeServiceWorkspaceInitializeResult.MalformedResponse("VersionMismatch response carried an invalid reuse field.");
				return CodeServiceWorkspaceInitializeResult.VersionMismatch(wire);
			}

			if (string.Equals(wire.Outcome, CodeServiceClientProtocol.WorkspaceMismatchOutcome, StringComparison.Ordinal))
			{
				if (!wire.HasReusedExistingWorkspace || wire.ReusedExistingWorkspace != false)
					return CodeServiceWorkspaceInitializeResult.MalformedResponse("WorkspaceMismatch response did not use the WorkspaceInitializeResponse shape.");
				return CodeServiceWorkspaceInitializeResult.WorkspaceMismatch(wire);
			}

			return CodeServiceWorkspaceInitializeResult.MalformedResponse(
				"HTTP 409 workspace initialize response had an unexpected outcome."
			);
		}

		if (statusCode == (int)HttpStatusCode.InternalServerError)
		{
			if (
				!string.Equals(wire.Outcome, CodeServiceClientProtocol.WorkspaceFaultedOutcome, StringComparison.Ordinal)
				|| wire.State != CodeServiceWorkspaceState.Faulted
				|| !wire.HasReusedExistingWorkspace
				|| wire.ReusedExistingWorkspace != false
				|| string.IsNullOrWhiteSpace(wire.FaultKind)
			)
			{
				return CodeServiceWorkspaceInitializeResult.MalformedResponse(
					"HTTP 500 workspace initialize response did not satisfy the Faulted contract."
				);
			}

			return CodeServiceWorkspaceInitializeResult.Faulted(wire);
		}

		if (statusCode == (int)HttpStatusCode.ServiceUnavailable)
		{
			if (string.Equals(wire.Outcome, CodeServiceClientProtocol.WorkspaceBusyOutcome, StringComparison.Ordinal))
			{
				if (
					(wire.State != CodeServiceWorkspaceState.Initializing
						&& wire.State != CodeServiceWorkspaceState.Indexing)
					|| !wire.HasReusedExistingWorkspace
					|| wire.ReusedExistingWorkspace != false
					|| string.IsNullOrEmpty(wire.ProjectRoot)
					|| !CodeServiceWorkspacePath.EqualsNormalized(wire.ProjectRoot, normalizedProjectRoot)
				)
				{
					return CodeServiceWorkspaceInitializeResult.MalformedResponse(
						"HTTP 503 Busy workspace response did not satisfy the initializing/indexing identity contract."
					);
				}

				return CodeServiceWorkspaceInitializeResult.Busy(wire);
			}

			if (string.Equals(wire.Outcome, CodeServiceClientProtocol.WorkspaceUnavailableOutcome, StringComparison.Ordinal))
			{
				if (!wire.HasReusedExistingWorkspace || wire.ReusedExistingWorkspace != false)
					return CodeServiceWorkspaceInitializeResult.MalformedResponse("Unavailable initialize response did not use the WorkspaceInitializeResponse shape.");
				return CodeServiceWorkspaceInitializeResult.Unavailable(wire);
			}

			return CodeServiceWorkspaceInitializeResult.MalformedResponse(
				"HTTP 503 workspace initialize response had an unexpected JSON outcome."
			);
		}

		return CodeServiceWorkspaceInitializeResult.MalformedResponse(
			$"workspace initialize returned unexpected HTTP status {statusCode}."
		);
	}

	private static CodeServiceWorkspaceStatusResult ValidateStatusResponse(
		int statusCode,
		byte[] bytes,
		string sentRequestId
	)
	{
		if (statusCode == (int)HttpStatusCode.Unauthorized)
		{
			return bytes.Length == 0
				? CodeServiceWorkspaceStatusResult.AuthenticationFailed()
				: CodeServiceWorkspaceStatusResult.MalformedResponse(
					"HTTP 401 workspace status response was expected to have zero body bytes."
				);
		}

		if (statusCode == (int)HttpStatusCode.MethodNotAllowed)
		{
			return bytes.Length == 0
				? CodeServiceWorkspaceStatusResult.InvalidRequest(
					"workspace status endpoint rejected the HTTP method."
				)
				: CodeServiceWorkspaceStatusResult.MalformedResponse(
					"HTTP 405 workspace status response was expected to have zero body bytes."
				);
		}

		if (statusCode == (int)HttpStatusCode.ServiceUnavailable && bytes.Length == 0)
			return CodeServiceWorkspaceStatusResult.ControlPlaneUnavailable();

		if (bytes.Length == 0)
		{
			return CodeServiceWorkspaceStatusResult.MalformedResponse(
				$"workspace status returned empty HTTP {statusCode} response."
			);
		}

		if (!TryParseWireResponse(bytes, allowReusedExistingWorkspace: false, out WorkspaceWireResponse wire, out string parseDetail))
			return CodeServiceWorkspaceStatusResult.MalformedResponse(parseDetail);

		if (!ValidateCommonWireResponse(wire, sentRequestId, out string commonDetail))
			return CodeServiceWorkspaceStatusResult.MalformedResponse(commonDetail);

		if (statusCode == (int)HttpStatusCode.OK)
		{
			if (!string.Equals(wire.Outcome, CodeServiceClientProtocol.WorkspaceSuccessOutcome, StringComparison.Ordinal))
				return CodeServiceWorkspaceStatusResult.MalformedResponse("HTTP 200 workspace status response was not Success.");
			if (wire.State == CodeServiceWorkspaceState.ShuttingDown)
				return CodeServiceWorkspaceStatusResult.MalformedResponse("HTTP 200 workspace status cannot be ShuttingDown.");
			if (!ValidateStateMetadata(wire, out string stateDetail))
				return CodeServiceWorkspaceStatusResult.MalformedResponse(stateDetail);

			return CodeServiceWorkspaceStatusResult.Success(wire);
		}

		if (statusCode == (int)HttpStatusCode.BadRequest)
		{
			return string.Equals(wire.Outcome, CodeServiceClientProtocol.WorkspaceInvalidRequestOutcome, StringComparison.Ordinal)
				? CodeServiceWorkspaceStatusResult.InvalidRequest("CodeService rejected the workspace status request.", wire)
				: CodeServiceWorkspaceStatusResult.MalformedResponse("HTTP 400 workspace status response was not InvalidRequest.");
		}

		if (statusCode == (int)HttpStatusCode.Conflict)
		{
			return string.Equals(wire.Outcome, CodeServiceClientProtocol.WorkspaceVersionMismatchOutcome, StringComparison.Ordinal)
				? CodeServiceWorkspaceStatusResult.VersionMismatch(wire)
				: CodeServiceWorkspaceStatusResult.MalformedResponse("HTTP 409 workspace status response was not VersionMismatch.");
		}

		if (statusCode == (int)HttpStatusCode.ServiceUnavailable)
		{
			if (
				!string.Equals(wire.Outcome, CodeServiceClientProtocol.WorkspaceUnavailableOutcome, StringComparison.Ordinal)
				|| wire.State != CodeServiceWorkspaceState.ShuttingDown
			)
			{
				return CodeServiceWorkspaceStatusResult.MalformedResponse(
					"HTTP 503 workspace status response did not satisfy Unavailable/ShuttingDown contract."
				);
			}
			if (!ValidateStateMetadata(wire, out string stateDetail))
				return CodeServiceWorkspaceStatusResult.MalformedResponse(stateDetail);

			return CodeServiceWorkspaceStatusResult.Unavailable(wire);
		}

		return CodeServiceWorkspaceStatusResult.MalformedResponse(
			$"workspace status returned unexpected HTTP status {statusCode}."
		);
	}

	private static bool MatchesFailure(WorkspaceWireResponse wire, string expectedOutcome)
	{
		return string.Equals(wire.Outcome, expectedOutcome, StringComparison.Ordinal)
			&& ValidateFailureReuseField(wire);
	}

	private static bool ValidateFailureReuseField(WorkspaceWireResponse wire)
	{
		return !wire.HasReusedExistingWorkspace || wire.ReusedExistingWorkspace == false;
	}

	private static bool ValidateCommonWireResponse(
		WorkspaceWireResponse wire,
		string sentRequestId,
		out string detail
	)
	{
		if (wire.SchemaVersion != CodeServiceClientProtocol.WorkspaceSchemaVersion)
		{
			detail = "workspace response schemaVersion did not match WorkspaceSchemaVersion=1.";
			return false;
		}
		if (!string.Equals(wire.RequestId, sentRequestId, StringComparison.Ordinal))
		{
			detail = "workspace response requestId did not match the sent canonical requestId.";
			return false;
		}
		if (wire.SourceFileCount < 0 || wire.ProjectFileCount < 0 || wire.SolutionFileCount < 0)
		{
			detail = "workspace response contained a negative file count.";
			return false;
		}
		if (!ValidateStateMetadata(wire, out detail))
			return false;

		detail = "";
		return true;
	}

	private static bool ValidateStateMetadata(WorkspaceWireResponse wire, out string detail)
	{
		if (wire.State == CodeServiceWorkspaceState.Faulted)
		{
			if (string.IsNullOrWhiteSpace(wire.FaultKind))
			{
				detail = "Faulted workspace response did not contain faultKind.";
				return false;
			}
		}
		else if (!string.IsNullOrEmpty(wire.FaultKind))
		{
			detail = "non-Faulted workspace response unexpectedly contained faultKind.";
			return false;
		}

		if (wire.ProjectRoot != null)
		{
			if (
				!CodeServiceWorkspacePath.TryNormalize(
					wire.ProjectRoot,
					out string normalized,
					out _
				)
			)
			{
				detail = "workspace response projectRoot was not a valid bounded absolute path.";
				return false;
			}
			wire.ProjectRoot = normalized;
		}

		if (
			(
				wire.State is CodeServiceWorkspaceState.Initializing
					or CodeServiceWorkspaceState.Indexing
					or CodeServiceWorkspaceState.Ready
					or CodeServiceWorkspaceState.Faulted
			)
			&& string.IsNullOrEmpty(wire.ProjectRoot)
		)
		{
			detail = "workspace response state required a projectRoot but none was present.";
			return false;
		}

		detail = "";
		return true;
	}

	private static bool TryParseWireResponse(
		byte[] bytes,
		bool allowReusedExistingWorkspace,
		out WorkspaceWireResponse response,
		out string detail
	)
	{
		response = default;
		detail = "";
		try
		{
			using JsonDocument document = JsonDocument.Parse(
				bytes,
				new JsonDocumentOptions
				{
					AllowTrailingCommas = false,
					CommentHandling = JsonCommentHandling.Disallow,
					MaxDepth = MaxJsonDepth,
				}
			);
			JsonElement root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
			{
				detail = "workspace response root was not a JSON object.";
				return false;
			}

			int schemaVersionCount = 0;
			int outcomeCount = 0;
			int requestIdCount = 0;
			int stateCount = 0;
			int projectRootCount = 0;
			int reusedCount = 0;
			int sourceCount = 0;
			int projectCount = 0;
			int solutionCount = 0;
			int faultCount = 0;

			int schemaVersion = 0;
			string outcome = null;
			string requestId = null;
			CodeServiceWorkspaceState state = default;
			string projectRoot = null;
			bool? reused = null;
			int sourceFileCount = 0;
			int projectFileCount = 0;
			int solutionFileCount = 0;
			string faultKind = null;

			foreach (JsonProperty property in root.EnumerateObject())
			{
				switch (property.Name)
				{
					case "schemaVersion":
						schemaVersionCount++;
						if (schemaVersionCount != 1 || property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out schemaVersion))
							return ParseFailure("workspace response schemaVersion was duplicated or invalid.", out response, out detail);
						break;
					case "outcome":
						outcomeCount++;
						if (!TryReadBoundedString(property.Value, MaxOutcomeLength, allowNull: false, out outcome) || outcomeCount != 1)
							return ParseFailure("workspace response outcome was duplicated or invalid.", out response, out detail);
						break;
					case "requestId":
						requestIdCount++;
						if (!TryReadBoundedString(property.Value, MaxRequestIdLength, allowNull: true, out requestId) || requestIdCount != 1)
							return ParseFailure("workspace response requestId was duplicated or invalid.", out response, out detail);
						break;
					case "state":
						stateCount++;
						if (!TryReadWorkspaceState(property.Value, out state) || stateCount != 1)
							return ParseFailure("workspace response state was duplicated or unknown.", out response, out detail);
						break;
					case "projectRoot":
						projectRootCount++;
						if (!TryReadBoundedString(property.Value, CodeServiceClientProtocol.MaxWorkspaceProjectRootLength, allowNull: true, out projectRoot) || projectRootCount != 1)
							return ParseFailure("workspace response projectRoot was duplicated or invalid.", out response, out detail);
						break;
					case "reusedExistingWorkspace":
						reusedCount++;
						if (!allowReusedExistingWorkspace || reusedCount != 1 || (property.Value.ValueKind != JsonValueKind.True && property.Value.ValueKind != JsonValueKind.False))
							return ParseFailure("workspace response reusedExistingWorkspace was unexpected, duplicated or invalid.", out response, out detail);
						reused = property.Value.GetBoolean();
						break;
					case "sourceFileCount":
						sourceCount++;
						if (sourceCount != 1 || property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out sourceFileCount))
							return ParseFailure("workspace response sourceFileCount was duplicated or invalid.", out response, out detail);
						break;
					case "projectFileCount":
						projectCount++;
						if (projectCount != 1 || property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out projectFileCount))
							return ParseFailure("workspace response projectFileCount was duplicated or invalid.", out response, out detail);
						break;
					case "solutionFileCount":
						solutionCount++;
						if (solutionCount != 1 || property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out solutionFileCount))
							return ParseFailure("workspace response solutionFileCount was duplicated or invalid.", out response, out detail);
						break;
					case "faultKind":
						faultCount++;
						if (!TryReadBoundedString(property.Value, MaxFaultKindLength, allowNull: true, out faultKind) || faultCount != 1)
							return ParseFailure("workspace response faultKind was duplicated or invalid.", out response, out detail);
						break;
					default:
						return ParseFailure("workspace response contained an unknown property.", out response, out detail);
				}
			}

			if (
				schemaVersionCount != 1
				|| outcomeCount != 1
				|| requestIdCount != 1
				|| stateCount != 1
				|| projectRootCount != 1
				|| sourceCount != 1
				|| projectCount != 1
				|| solutionCount != 1
				|| faultCount != 1
			)
			{
				detail = "workspace response was missing one or more required properties.";
				return false;
			}

			response = new WorkspaceWireResponse
			{
				SchemaVersion = schemaVersion,
				Outcome = outcome,
				RequestId = requestId,
				State = state,
				ProjectRoot = projectRoot,
				HasReusedExistingWorkspace = reusedCount == 1,
				ReusedExistingWorkspace = reused,
				SourceFileCount = sourceFileCount,
				ProjectFileCount = projectFileCount,
				SolutionFileCount = solutionFileCount,
				FaultKind = faultKind,
			};
			return true;
		}
		catch (JsonException exception)
		{
			detail = "workspace response was invalid strict JSON: " + ToSingleLine(exception.Message);
			return false;
		}
		catch (Exception exception)
		{
			detail = "workspace response could not be parsed safely: " + ToSingleLine(exception.Message);
			return false;
		}
	}

	private static bool ParseFailure(
		string failureDetail,
		out WorkspaceWireResponse response,
		out string detail
	)
	{
		response = default;
		detail = failureDetail;
		return false;
	}

	private static bool TryReadBoundedString(
		JsonElement element,
		int maxLength,
		bool allowNull,
		out string value
	)
	{
		value = null;
		if (allowNull && element.ValueKind == JsonValueKind.Null)
			return true;
		if (element.ValueKind != JsonValueKind.String)
			return false;

		value = element.GetString();
		return value != null && value.Length <= maxLength;
	}

	private static bool TryReadWorkspaceState(
		JsonElement element,
		out CodeServiceWorkspaceState state
	)
	{
		state = default;
		if (!TryReadBoundedString(element, MaxStateLength, allowNull: false, out string value))
			return false;

		if (string.Equals(value, "Uninitialized", StringComparison.Ordinal))
			state = CodeServiceWorkspaceState.Uninitialized;
		else if (string.Equals(value, "Initializing", StringComparison.Ordinal))
			state = CodeServiceWorkspaceState.Initializing;
		else if (string.Equals(value, "Indexing", StringComparison.Ordinal))
			state = CodeServiceWorkspaceState.Indexing;
		else if (string.Equals(value, "Ready", StringComparison.Ordinal))
			state = CodeServiceWorkspaceState.Ready;
		else if (string.Equals(value, "Faulted", StringComparison.Ordinal))
			state = CodeServiceWorkspaceState.Faulted;
		else if (string.Equals(value, "ShuttingDown", StringComparison.Ordinal))
			state = CodeServiceWorkspaceState.ShuttingDown;
		else
			return false;

		return true;
	}

	private static async Task<BoundedWorkspaceBodyResult> ReadBoundedBodyAsync(
		HttpResponseMessage response,
		CancellationToken cancellationToken
	)
	{
		long? contentLength = response.Content?.Headers.ContentLength;
		if (contentLength.HasValue && contentLength.Value > CodeServiceClientProtocol.MaxWorkspaceResponseSizeBytes)
		{
			return BoundedWorkspaceBodyResult.Failure(
				$"workspace response exceeded the {CodeServiceClientProtocol.MaxWorkspaceResponseSizeBytes} byte boundary."
			);
		}

		if (response.Content == null)
			return BoundedWorkspaceBodyResult.Success(Array.Empty<byte>());

		using Stream stream = await response.Content
			.ReadAsStreamAsync(cancellationToken)
			.ConfigureAwait(false);
		byte[] buffer = new byte[CodeServiceClientProtocol.MaxWorkspaceResponseSizeBytes + 1];
		int totalRead = 0;
		while (totalRead < buffer.Length)
		{
			int read = await stream
				.ReadAsync(buffer, totalRead, buffer.Length - totalRead, cancellationToken)
				.ConfigureAwait(false);
			if (read == 0)
				break;
			totalRead += read;
		}

		if (totalRead > CodeServiceClientProtocol.MaxWorkspaceResponseSizeBytes)
		{
			return BoundedWorkspaceBodyResult.Failure(
				$"workspace response exceeded the {CodeServiceClientProtocol.MaxWorkspaceResponseSizeBytes} byte boundary."
			);
		}

		if (totalRead == 0)
			return BoundedWorkspaceBodyResult.Success(Array.Empty<byte>());

		byte[] exact = new byte[totalRead];
		Buffer.BlockCopy(buffer, 0, exact, 0, totalRead);
		return BoundedWorkspaceBodyResult.Success(exact);
	}

	private static string ToSingleLine(string message)
	{
		return (message ?? "").Replace('\r', ' ').Replace('\n', ' ');
	}

	internal sealed class WorkspaceWireResponse
	{
		internal int SchemaVersion { get; init; }
		internal string Outcome { get; init; }
		internal string RequestId { get; init; }
		internal CodeServiceWorkspaceState State { get; init; }
		internal string ProjectRoot { get; set; }
		internal bool HasReusedExistingWorkspace { get; init; }
		internal bool? ReusedExistingWorkspace { get; init; }
		internal int SourceFileCount { get; init; }
		internal int ProjectFileCount { get; init; }
		internal int SolutionFileCount { get; init; }
		internal string FaultKind { get; init; }
	}

	private readonly struct BoundedWorkspaceBodyResult
	{
		private BoundedWorkspaceBodyResult(bool isSuccess, byte[] bytes, string detail)
		{
			IsSuccess = isSuccess;
			Bytes = bytes ?? Array.Empty<byte>();
			Detail = detail ?? "";
		}

		internal bool IsSuccess { get; }
		internal byte[] Bytes { get; }
		internal string Detail { get; }
		internal static BoundedWorkspaceBodyResult Success(byte[] bytes) => new(true, bytes, "");
		internal static BoundedWorkspaceBodyResult Failure(string detail) => new(false, Array.Empty<byte>(), detail);
	}
}

internal enum CodeServiceWorkspaceState
{
	Uninitialized,
	Initializing,
	Indexing,
	Ready,
	Faulted,
	ShuttingDown,
}

internal enum CodeServiceWorkspaceInitializeOutcome
{
	Ready,
	Busy,
	WorkspaceMismatch,
	Faulted,
	InvalidRequest,
	VersionMismatch,
	Unavailable,
	AuthenticationFailed,
	ControlPlaneUnavailable,
	TransportUnavailable,
	MalformedResponse,
}

internal readonly struct CodeServiceWorkspaceInitializeResult
{
	private CodeServiceWorkspaceInitializeResult(
		CodeServiceWorkspaceInitializeOutcome outcome,
		CodeServiceWorkspaceState state,
		string projectRoot,
		bool? reusedExistingWorkspace,
		int sourceFileCount,
		int projectFileCount,
		int solutionFileCount,
		string faultKind,
		string detail
	)
	{
		Outcome = outcome;
		State = state;
		ProjectRoot = projectRoot ?? "";
		ReusedExistingWorkspace = reusedExistingWorkspace;
		SourceFileCount = sourceFileCount;
		ProjectFileCount = projectFileCount;
		SolutionFileCount = solutionFileCount;
		FaultKind = faultKind ?? "";
		Detail = detail ?? "";
	}

	internal CodeServiceWorkspaceInitializeOutcome Outcome { get; }
	internal CodeServiceWorkspaceState State { get; }
	internal string ProjectRoot { get; }
	internal bool? ReusedExistingWorkspace { get; }
	internal int SourceFileCount { get; }
	internal int ProjectFileCount { get; }
	internal int SolutionFileCount { get; }
	internal string FaultKind { get; }
	internal string Detail { get; }

	internal static CodeServiceWorkspaceInitializeResult Ready(string root, bool reused, int source, int project, int solution)
		=> new(CodeServiceWorkspaceInitializeOutcome.Ready, CodeServiceWorkspaceState.Ready, root, reused, source, project, solution, "", "");
	internal static CodeServiceWorkspaceInitializeResult Busy(CodeServiceWorkspaceClient.WorkspaceWireResponse wire)
		=> FromWire(CodeServiceWorkspaceInitializeOutcome.Busy, wire, "");
	internal static CodeServiceWorkspaceInitializeResult WorkspaceMismatch(CodeServiceWorkspaceClient.WorkspaceWireResponse wire)
		=> FromWire(CodeServiceWorkspaceInitializeOutcome.WorkspaceMismatch, wire, "");
	internal static CodeServiceWorkspaceInitializeResult Faulted(CodeServiceWorkspaceClient.WorkspaceWireResponse wire)
		=> FromWire(CodeServiceWorkspaceInitializeOutcome.Faulted, wire, "");
	internal static CodeServiceWorkspaceInitializeResult InvalidRequest(string detail)
		=> new(CodeServiceWorkspaceInitializeOutcome.InvalidRequest, CodeServiceWorkspaceState.Uninitialized, "", null, 0, 0, 0, "", detail);
	internal static CodeServiceWorkspaceInitializeResult InvalidRequest(string detail, CodeServiceWorkspaceClient.WorkspaceWireResponse wire)
		=> FromWire(CodeServiceWorkspaceInitializeOutcome.InvalidRequest, wire, detail);
	internal static CodeServiceWorkspaceInitializeResult VersionMismatch(CodeServiceWorkspaceClient.WorkspaceWireResponse wire)
		=> FromWire(CodeServiceWorkspaceInitializeOutcome.VersionMismatch, wire, "workspace schema/protocol version mismatch.");
	internal static CodeServiceWorkspaceInitializeResult Unavailable(CodeServiceWorkspaceClient.WorkspaceWireResponse wire)
		=> FromWire(CodeServiceWorkspaceInitializeOutcome.Unavailable, wire, "workspace is unavailable.");
	internal static CodeServiceWorkspaceInitializeResult AuthenticationFailed()
		=> new(CodeServiceWorkspaceInitializeOutcome.AuthenticationFailed, CodeServiceWorkspaceState.Uninitialized, "", null, 0, 0, 0, "", "workspace authentication failed.");
	internal static CodeServiceWorkspaceInitializeResult ControlPlaneUnavailable()
		=> new(CodeServiceWorkspaceInitializeOutcome.ControlPlaneUnavailable, CodeServiceWorkspaceState.Uninitialized, "", null, 0, 0, 0, "", "workspace control plane is unavailable.");
	internal static CodeServiceWorkspaceInitializeResult TransportUnavailable(string detail)
		=> new(CodeServiceWorkspaceInitializeOutcome.TransportUnavailable, CodeServiceWorkspaceState.Uninitialized, "", null, 0, 0, 0, "", detail);
	internal static CodeServiceWorkspaceInitializeResult MalformedResponse(string detail)
		=> new(CodeServiceWorkspaceInitializeOutcome.MalformedResponse, CodeServiceWorkspaceState.Uninitialized, "", null, 0, 0, 0, "", detail);

	private static CodeServiceWorkspaceInitializeResult FromWire(
		CodeServiceWorkspaceInitializeOutcome outcome,
		CodeServiceWorkspaceClient.WorkspaceWireResponse wire,
		string detail
	)
	{
		return new CodeServiceWorkspaceInitializeResult(
			outcome,
			wire.State,
			wire.ProjectRoot,
			wire.ReusedExistingWorkspace,
			wire.SourceFileCount,
			wire.ProjectFileCount,
			wire.SolutionFileCount,
			wire.FaultKind,
			detail
		);
	}
}

internal enum CodeServiceWorkspaceStatusOutcome
{
	Success,
	InvalidRequest,
	VersionMismatch,
	Unavailable,
	AuthenticationFailed,
	ControlPlaneUnavailable,
	TransportUnavailable,
	MalformedResponse,
}

internal readonly struct CodeServiceWorkspaceStatusResult
{
	private CodeServiceWorkspaceStatusResult(
		CodeServiceWorkspaceStatusOutcome outcome,
		CodeServiceWorkspaceState state,
		string projectRoot,
		int sourceFileCount,
		int projectFileCount,
		int solutionFileCount,
		string faultKind,
		string detail
	)
	{
		Outcome = outcome;
		State = state;
		ProjectRoot = projectRoot ?? "";
		SourceFileCount = sourceFileCount;
		ProjectFileCount = projectFileCount;
		SolutionFileCount = solutionFileCount;
		FaultKind = faultKind ?? "";
		Detail = detail ?? "";
	}

	internal CodeServiceWorkspaceStatusOutcome Outcome { get; }
	internal CodeServiceWorkspaceState State { get; }
	internal string ProjectRoot { get; }
	internal int SourceFileCount { get; }
	internal int ProjectFileCount { get; }
	internal int SolutionFileCount { get; }
	internal string FaultKind { get; }
	internal string Detail { get; }

	internal static CodeServiceWorkspaceStatusResult Success(CodeServiceWorkspaceClient.WorkspaceWireResponse wire)
		=> FromWire(CodeServiceWorkspaceStatusOutcome.Success, wire, "");
	internal static CodeServiceWorkspaceStatusResult InvalidRequest(string detail)
		=> new(CodeServiceWorkspaceStatusOutcome.InvalidRequest, CodeServiceWorkspaceState.Uninitialized, "", 0, 0, 0, "", detail);
	internal static CodeServiceWorkspaceStatusResult InvalidRequest(string detail, CodeServiceWorkspaceClient.WorkspaceWireResponse wire)
		=> FromWire(CodeServiceWorkspaceStatusOutcome.InvalidRequest, wire, detail);
	internal static CodeServiceWorkspaceStatusResult VersionMismatch(CodeServiceWorkspaceClient.WorkspaceWireResponse wire)
		=> FromWire(CodeServiceWorkspaceStatusOutcome.VersionMismatch, wire, "workspace schema/protocol version mismatch.");
	internal static CodeServiceWorkspaceStatusResult Unavailable(CodeServiceWorkspaceClient.WorkspaceWireResponse wire)
		=> FromWire(CodeServiceWorkspaceStatusOutcome.Unavailable, wire, "workspace is unavailable.");
	internal static CodeServiceWorkspaceStatusResult AuthenticationFailed()
		=> new(CodeServiceWorkspaceStatusOutcome.AuthenticationFailed, CodeServiceWorkspaceState.Uninitialized, "", 0, 0, 0, "", "workspace authentication failed.");
	internal static CodeServiceWorkspaceStatusResult ControlPlaneUnavailable()
		=> new(CodeServiceWorkspaceStatusOutcome.ControlPlaneUnavailable, CodeServiceWorkspaceState.Uninitialized, "", 0, 0, 0, "", "workspace control plane is unavailable.");
	internal static CodeServiceWorkspaceStatusResult TransportUnavailable(string detail)
		=> new(CodeServiceWorkspaceStatusOutcome.TransportUnavailable, CodeServiceWorkspaceState.Uninitialized, "", 0, 0, 0, "", detail);
	internal static CodeServiceWorkspaceStatusResult MalformedResponse(string detail)
		=> new(CodeServiceWorkspaceStatusOutcome.MalformedResponse, CodeServiceWorkspaceState.Uninitialized, "", 0, 0, 0, "", detail);

	private static CodeServiceWorkspaceStatusResult FromWire(
		CodeServiceWorkspaceStatusOutcome outcome,
		CodeServiceWorkspaceClient.WorkspaceWireResponse wire,
		string detail
	)
	{
		return new CodeServiceWorkspaceStatusResult(
			outcome,
			wire.State,
			wire.ProjectRoot,
			wire.SourceFileCount,
			wire.ProjectFileCount,
			wire.SolutionFileCount,
			wire.FaultKind,
			detail
		);
	}
}
#endif
