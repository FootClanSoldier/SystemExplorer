#if TOOLS
using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SystemExplorer.CodeService.Runtime;

namespace SystemExplorer.CodeService.Client;

internal sealed class CodeServiceHandshakeClient : IDisposable
{
	private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(2);

	private HttpClient _httpClient;
	private HttpClientHandler _handler;
	private readonly TimeSpan _requestTimeout;

	internal CodeServiceHandshakeClient(string address, int port, TimeSpan? requestTimeout = null)
	{
		if (!string.Equals(address, CodeServiceClientProtocol.Address, StringComparison.Ordinal))
			throw new ArgumentException("CodeService client requires the exact loopback address.", nameof(address));
		if (port < 1 || port > 65535)
			throw new ArgumentOutOfRangeException(nameof(port));

		_requestTimeout = requestTimeout ?? DefaultRequestTimeout;
		if (_requestTimeout <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(requestTimeout));

		_handler = new HttpClientHandler
		{
			UseProxy = false,
			AllowAutoRedirect = false,
			UseCookies = false,
		};
		_httpClient = new HttpClient(_handler, disposeHandler: false)
		{
			BaseAddress = new Uri($"http://{CodeServiceClientProtocol.Address}:{port}/", UriKind.Absolute),
			Timeout = Timeout.InfiniteTimeSpan,
		};
	}

	internal HttpClient HttpClient
	{
		get
		{
			return _httpClient ?? throw new ObjectDisposedException(nameof(CodeServiceHandshakeClient));
		}
	}

	internal async Task<CodeServiceHandshakeResult> HandshakeAsync(
		CodeServiceSessionDescriptor descriptor,
		CodeServiceProcessIdentity currentOwnerIdentity,
		string expectedServiceVersion,
		CodeServiceClientCredentials credentials,
		CancellationToken cancellationToken
	)
	{
		if (descriptor == null)
			throw new ArgumentNullException(nameof(descriptor));
		if (credentials == null)
			throw new ArgumentNullException(nameof(credentials));

		string requestId = Guid.NewGuid().ToString("D");
		using HttpRequestMessage request = new(
			HttpMethod.Post,
			CodeServiceClientProtocol.HandshakePath
		);

		string authorizationValue = credentials.CreateBearerAuthorizationValue();
		request.Headers.TryAddWithoutValidation("Authorization", authorizationValue);
		request.Headers.TryAddWithoutValidation(
			CodeServiceClientProtocol.ProtocolVersionHeaderName,
			CodeServiceClientProtocol.ProtocolVersion.ToString(CultureInfo.InvariantCulture)
		);
		request.Headers.TryAddWithoutValidation(
			CodeServiceClientProtocol.SessionIdHeaderName,
			descriptor.SessionId
		);
		request.Headers.TryAddWithoutValidation(
			CodeServiceClientProtocol.RequestIdHeaderName,
			requestId
		);

		using CancellationTokenSource requestCancellation =
			CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		requestCancellation.CancelAfter(_requestTimeout);

		HttpResponseMessage response;
		try
		{
			response = await HttpClient
				.SendAsync(
					request,
					HttpCompletionOption.ResponseHeadersRead,
					requestCancellation.Token
				)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (OperationCanceledException)
		{
			return CodeServiceHandshakeResult.TransportUnavailable(
				$"handshake exceeded the {_requestTimeout.TotalSeconds:0.###} second deadline."
			);
		}
		catch (HttpRequestException exception)
		{
			return CodeServiceHandshakeResult.TransportUnavailable(
				"handshake transport failed: " + ToSingleLine(exception.Message)
			);
		}
		catch (Exception exception)
		{
			return CodeServiceHandshakeResult.TransportUnavailable(
				"handshake transport failed: " + ToSingleLine(exception.Message)
			);
		}

		using (response)
		{
			BoundedResponseBodyResult bodyResult;
			try
			{
				bodyResult = await ReadBoundedBodyAsync(
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
				return CodeServiceHandshakeResult.TransportUnavailable(
					"handshake response body exceeded the request deadline."
				);
			}
			catch (Exception exception)
			{
				return CodeServiceHandshakeResult.MalformedResponse(
					"handshake response body could not be read safely: "
					+ ToSingleLine(exception.Message)
				);
			}

			if (!bodyResult.IsSuccess)
				return CodeServiceHandshakeResult.MalformedResponse(bodyResult.Detail);

			int statusCode = (int)response.StatusCode;
			if (statusCode == (int)HttpStatusCode.OK)
			{
				if (bodyResult.Bytes.Length == 0)
					return CodeServiceHandshakeResult.MalformedResponse("successful handshake response was empty.");

				return ValidateSuccessResponse(
					bodyResult.Bytes,
					requestId,
					descriptor,
					currentOwnerIdentity,
					expectedServiceVersion
				);
			}

			if (statusCode == (int)HttpStatusCode.Unauthorized)
			{
				return bodyResult.Bytes.Length == 0
					? CodeServiceHandshakeResult.AuthenticationFailed()
					: CodeServiceHandshakeResult.MalformedResponse("HTTP 401 handshake response was expected to have zero body bytes.");
			}

			if (statusCode == (int)HttpStatusCode.ServiceUnavailable)
			{
				return bodyResult.Bytes.Length == 0
					? CodeServiceHandshakeResult.ControlPlaneUnavailable()
					: CodeServiceHandshakeResult.MalformedResponse("HTTP 503 handshake response was expected to have zero body bytes.");
			}

			if (statusCode == (int)HttpStatusCode.MethodNotAllowed)
			{
				return bodyResult.Bytes.Length == 0
					? CodeServiceHandshakeResult.InvalidRequest("handshake endpoint rejected the HTTP method.")
					: CodeServiceHandshakeResult.MalformedResponse("HTTP 405 handshake response was expected to have zero body bytes.");
			}

			if (statusCode == (int)HttpStatusCode.BadRequest)
			{
				return ValidateFailureResponse(
					bodyResult.Bytes,
					requestId,
					CodeServiceClientProtocol.HandshakeInvalidRequestOutcome,
					CodeServiceHandshakeOutcome.InvalidRequest
				);
			}

			if (statusCode == (int)HttpStatusCode.Conflict)
			{
				return ValidateFailureResponse(
					bodyResult.Bytes,
					requestId,
					CodeServiceClientProtocol.HandshakeVersionMismatchOutcome,
					CodeServiceHandshakeOutcome.VersionMismatch
				);
			}

			return CodeServiceHandshakeResult.MalformedResponse(
				$"handshake returned unexpected HTTP status {statusCode}."
			);
		}
	}

	public void Dispose()
	{
		HttpClient client = Interlocked.Exchange(ref _httpClient, null);
		client?.Dispose();
		HttpClientHandler handler = Interlocked.Exchange(ref _handler, null);
		handler?.Dispose();
	}

	private static async Task<BoundedResponseBodyResult> ReadBoundedBodyAsync(
		HttpResponseMessage response,
		CancellationToken cancellationToken
	)
	{
		long? contentLength = response.Content?.Headers.ContentLength;
		if (contentLength.HasValue && contentLength.Value > CodeServiceClientProtocol.MaxHandshakeResponseSizeBytes)
			return BoundedResponseBodyResult.Failure("handshake response exceeded the 16 KiB size boundary.");

		if (response.Content == null)
			return BoundedResponseBodyResult.Success(Array.Empty<byte>());

		using Stream stream = await response.Content
			.ReadAsStreamAsync(cancellationToken)
			.ConfigureAwait(false);
		byte[] buffer = new byte[CodeServiceClientProtocol.MaxHandshakeResponseSizeBytes + 1];
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

		if (totalRead > CodeServiceClientProtocol.MaxHandshakeResponseSizeBytes)
			return BoundedResponseBodyResult.Failure("handshake response exceeded the 16 KiB size boundary.");

		if (totalRead == 0)
			return BoundedResponseBodyResult.Success(Array.Empty<byte>());

		byte[] exact = new byte[totalRead];
		Buffer.BlockCopy(buffer, 0, exact, 0, totalRead);
		return BoundedResponseBodyResult.Success(exact);
	}

	private static CodeServiceHandshakeResult ValidateSuccessResponse(
		byte[] bytes,
		string sentRequestId,
		CodeServiceSessionDescriptor descriptor,
		CodeServiceProcessIdentity currentOwnerIdentity,
		string expectedServiceVersion
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
					MaxDepth = 8,
				}
			);

			JsonElement root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
				return Malformed("successful handshake root was not an object.");

			int schemaVersion = 0;
			string outcome = null;
			string requestId = null;
			int protocolVersion = 0;
			string serviceVersion = null;
			string sessionId = null;
			int godotPid = 0;
			long godotStart = 0;
			int servicePid = 0;
			long serviceStart = 0;
			string transport = null;
			string address = null;
			int port = 0;

			bool[] seen = new bool[13];
			foreach (JsonProperty property in root.EnumerateObject())
			{
				switch (property.Name)
				{
					case "schemaVersion":
						if (seen[0] || !property.Value.TryGetInt32(out schemaVersion)) return Malformed("handshake schemaVersion was duplicate or invalid.");
						seen[0] = true;
						break;
					case "outcome":
						if (seen[1] || !TryString(property.Value, 64, out outcome)) return Malformed("handshake outcome was duplicate or invalid.");
						seen[1] = true;
						break;
					case "requestId":
						if (seen[2] || !TryString(property.Value, 64, out requestId)) return Malformed("handshake requestId was duplicate or invalid.");
						seen[2] = true;
						break;
					case "protocolVersion":
						if (seen[3] || !property.Value.TryGetInt32(out protocolVersion)) return Malformed("handshake protocolVersion was duplicate or invalid.");
						seen[3] = true;
						break;
					case "serviceVersion":
						if (seen[4] || !TryString(property.Value, 128, out serviceVersion)) return Malformed("handshake serviceVersion was duplicate or invalid.");
						seen[4] = true;
						break;
					case "sessionId":
						if (seen[5] || !TryString(property.Value, 32, out sessionId)) return Malformed("handshake sessionId was duplicate or invalid.");
						seen[5] = true;
						break;
					case "godotPid":
						if (seen[6] || !property.Value.TryGetInt32(out godotPid)) return Malformed("handshake godotPid was duplicate or invalid.");
						seen[6] = true;
						break;
					case "godotStartTimeUtcTicks":
						if (seen[7] || !property.Value.TryGetInt64(out godotStart)) return Malformed("handshake Godot start identity was duplicate or invalid.");
						seen[7] = true;
						break;
					case "servicePid":
						if (seen[8] || !property.Value.TryGetInt32(out servicePid)) return Malformed("handshake servicePid was duplicate or invalid.");
						seen[8] = true;
						break;
					case "serviceStartTimeUtcTicks":
						if (seen[9] || !property.Value.TryGetInt64(out serviceStart)) return Malformed("handshake service start identity was duplicate or invalid.");
						seen[9] = true;
						break;
					case "transport":
						if (seen[10] || !TryString(property.Value, 16, out transport)) return Malformed("handshake transport was duplicate or invalid.");
						seen[10] = true;
						break;
					case "address":
						if (seen[11] || !TryString(property.Value, 64, out address)) return Malformed("handshake address was duplicate or invalid.");
						seen[11] = true;
						break;
					case "port":
						if (seen[12] || !property.Value.TryGetInt32(out port)) return Malformed("handshake port was duplicate or invalid.");
						seen[12] = true;
						break;
				}
			}

			foreach (bool fieldSeen in seen)
			{
				if (!fieldSeen)
					return Malformed("successful handshake response was missing required fields.");
			}

			if (schemaVersion != CodeServiceClientProtocol.HandshakeSchemaVersion)
				return Malformed("handshake schemaVersion was not supported.");
			if (!string.Equals(outcome, CodeServiceClientProtocol.HandshakeSuccessOutcome, StringComparison.Ordinal))
				return Malformed("HTTP 200 handshake outcome was not Success.");
			if (!string.Equals(requestId, sentRequestId, StringComparison.Ordinal))
				return Malformed("handshake requestId did not echo the exact request identity.");
			if (
				protocolVersion != CodeServiceClientProtocol.ProtocolVersion
				|| protocolVersion != descriptor.ProtocolVersion
			)
				return Malformed("handshake protocolVersion identity did not match the descriptor/client.");
			if (
				!string.Equals(serviceVersion, descriptor.ServiceVersion, StringComparison.Ordinal)
				|| !string.Equals(serviceVersion, expectedServiceVersion, StringComparison.Ordinal)
			)
				return Malformed("handshake serviceVersion identity did not match the descriptor/client.");
			if (!string.Equals(sessionId, descriptor.SessionId, StringComparison.Ordinal))
				return Malformed("handshake sessionId did not match the descriptor.");
			if (
				godotPid != currentOwnerIdentity.ProcessId
				|| godotStart != currentOwnerIdentity.StartTimeUtcTicks
				|| godotPid != descriptor.GodotOwnerIdentity.ProcessId
				|| godotStart != descriptor.GodotOwnerIdentity.StartTimeUtcTicks
			)
				return Malformed("handshake Godot owner identity did not match the descriptor/current editor.");
			if (
				servicePid != descriptor.ServiceProcessIdentity.ProcessId
				|| serviceStart != descriptor.ServiceProcessIdentity.StartTimeUtcTicks
			)
				return Malformed("handshake service process identity did not match the descriptor.");
			if (
				!string.Equals(transport, CodeServiceClientProtocol.Transport, StringComparison.Ordinal)
				|| !string.Equals(transport, descriptor.Transport, StringComparison.Ordinal)
				|| !string.Equals(address, CodeServiceClientProtocol.Address, StringComparison.Ordinal)
				|| !string.Equals(address, descriptor.Address, StringComparison.Ordinal)
				|| port != descriptor.Port
			)
				return Malformed("handshake endpoint identity did not match the descriptor/client.");

			return CodeServiceHandshakeResult.Success(sentRequestId);
		}
		catch (JsonException)
		{
			return Malformed("handshake success JSON was malformed.");
		}
		catch (Exception exception)
		{
			return Malformed("handshake success validation failed: " + ToSingleLine(exception.Message));
		}
	}

	private static CodeServiceHandshakeResult ValidateFailureResponse(
		byte[] bytes,
		string sentRequestId,
		string expectedOutcome,
		CodeServiceHandshakeOutcome resultOutcome
	)
	{
		if (bytes.Length == 0)
			return Malformed("authenticated handshake failure response was empty.");

		try
		{
			using JsonDocument document = JsonDocument.Parse(
				bytes,
				new JsonDocumentOptions
				{
					AllowTrailingCommas = false,
					CommentHandling = JsonCommentHandling.Disallow,
					MaxDepth = 8,
				}
			);
			JsonElement root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
				return Malformed("handshake failure root was not an object.");

			int schemaVersion = 0;
			string outcome = null;
			string requestId = null;
			bool requestIdWasNull = false;
			bool schemaSeen = false;
			bool outcomeSeen = false;
			bool requestIdSeen = false;

			foreach (JsonProperty property in root.EnumerateObject())
			{
				switch (property.Name)
				{
					case "schemaVersion":
						if (schemaSeen || !property.Value.TryGetInt32(out schemaVersion)) return Malformed("failure schemaVersion was duplicate or invalid.");
						schemaSeen = true;
						break;
					case "outcome":
						if (outcomeSeen || !TryString(property.Value, 64, out outcome)) return Malformed("failure outcome was duplicate or invalid.");
						outcomeSeen = true;
						break;
					case "requestId":
						if (requestIdSeen) return Malformed("failure requestId was duplicate.");
						requestIdSeen = true;
						if (property.Value.ValueKind == JsonValueKind.Null)
						{
							requestIdWasNull = true;
						}
						else if (!TryString(property.Value, 64, out requestId))
						{
							return Malformed("failure requestId was invalid.");
						}
						break;
				}
			}

			if (!schemaSeen || !outcomeSeen || !requestIdSeen)
				return Malformed("handshake failure response was missing required fields.");
			if (schemaVersion != CodeServiceClientProtocol.HandshakeSchemaVersion)
				return Malformed("handshake failure schemaVersion was not supported.");
			if (!string.Equals(outcome, expectedOutcome, StringComparison.Ordinal))
				return Malformed("handshake failure outcome did not match the HTTP status.");
			if (resultOutcome == CodeServiceHandshakeOutcome.VersionMismatch)
			{
				if (requestIdWasNull || !string.Equals(requestId, sentRequestId, StringComparison.Ordinal))
					return Malformed("version-mismatch response did not echo the requestId.");
			}
			else if (!requestIdWasNull && !string.Equals(requestId, sentRequestId, StringComparison.Ordinal))
			{
				return Malformed("invalid-request response carried a mismatched requestId.");
			}

			return resultOutcome == CodeServiceHandshakeOutcome.VersionMismatch
				? CodeServiceHandshakeResult.VersionMismatch()
				: CodeServiceHandshakeResult.InvalidRequest("authenticated handshake request was rejected.");
		}
		catch (JsonException)
		{
			return Malformed("handshake failure JSON was malformed.");
		}
	}

	private static bool TryString(JsonElement element, int maxLength, out string value)
	{
		value = null;
		if (element.ValueKind != JsonValueKind.String)
			return false;
		value = element.GetString();
		return value != null && value.Length > 0 && value.Length <= maxLength;
	}

	private static CodeServiceHandshakeResult Malformed(string detail)
	{
		return CodeServiceHandshakeResult.MalformedResponse(detail);
	}

	private static string ToSingleLine(string message)
	{
		return (message ?? "").Replace('\r', ' ').Replace('\n', ' ');
	}

	private readonly struct BoundedResponseBodyResult
	{
		private BoundedResponseBodyResult(bool isSuccess, byte[] bytes, string detail)
		{
			IsSuccess = isSuccess;
			Bytes = bytes ?? Array.Empty<byte>();
			Detail = detail ?? "";
		}

		internal bool IsSuccess { get; }
		internal byte[] Bytes { get; }
		internal string Detail { get; }

		internal static BoundedResponseBodyResult Success(byte[] bytes)
		{
			return new BoundedResponseBodyResult(true, bytes, "");
		}

		internal static BoundedResponseBodyResult Failure(string detail)
		{
			return new BoundedResponseBodyResult(false, Array.Empty<byte>(), detail);
		}
	}
}

internal enum CodeServiceHandshakeOutcome
{
	Success,
	ControlPlaneUnavailable,
	AuthenticationFailed,
	VersionMismatch,
	InvalidRequest,
	TransportUnavailable,
	MalformedResponse,
}

internal readonly struct CodeServiceHandshakeResult
{
	private CodeServiceHandshakeResult(
		CodeServiceHandshakeOutcome outcome,
		string requestId,
		string detail
	)
	{
		Outcome = outcome;
		RequestId = requestId ?? "";
		Detail = detail ?? "";
	}

	internal CodeServiceHandshakeOutcome Outcome { get; }
	internal string RequestId { get; }
	internal string Detail { get; }
	internal bool IsSuccess => Outcome == CodeServiceHandshakeOutcome.Success;
	internal bool IsTransient =>
		Outcome == CodeServiceHandshakeOutcome.ControlPlaneUnavailable
		|| Outcome == CodeServiceHandshakeOutcome.TransportUnavailable;

	internal static CodeServiceHandshakeResult Success(string requestId)
	{
		return new CodeServiceHandshakeResult(CodeServiceHandshakeOutcome.Success, requestId, "");
	}

	internal static CodeServiceHandshakeResult ControlPlaneUnavailable()
	{
		return new CodeServiceHandshakeResult(CodeServiceHandshakeOutcome.ControlPlaneUnavailable, "", "control plane is not currently available.");
	}

	internal static CodeServiceHandshakeResult AuthenticationFailed()
	{
		return new CodeServiceHandshakeResult(CodeServiceHandshakeOutcome.AuthenticationFailed, "", "handshake authentication failed.");
	}

	internal static CodeServiceHandshakeResult VersionMismatch()
	{
		return new CodeServiceHandshakeResult(CodeServiceHandshakeOutcome.VersionMismatch, "", "handshake protocol version was rejected.");
	}

	internal static CodeServiceHandshakeResult InvalidRequest(string detail)
	{
		return new CodeServiceHandshakeResult(CodeServiceHandshakeOutcome.InvalidRequest, "", detail);
	}

	internal static CodeServiceHandshakeResult TransportUnavailable(string detail)
	{
		return new CodeServiceHandshakeResult(CodeServiceHandshakeOutcome.TransportUnavailable, "", detail);
	}

	internal static CodeServiceHandshakeResult MalformedResponse(string detail)
	{
		return new CodeServiceHandshakeResult(CodeServiceHandshakeOutcome.MalformedResponse, "", detail);
	}
}
#endif
