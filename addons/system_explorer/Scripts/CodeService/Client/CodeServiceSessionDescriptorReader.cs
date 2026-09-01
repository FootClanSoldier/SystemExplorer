#if TOOLS
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SystemExplorer.CodeService.Runtime;

namespace SystemExplorer.CodeService.Client;

internal sealed class CodeServiceSessionDescriptorReader
{
	internal async Task<CodeServiceSessionDescriptorReadResult> ReadAsync(
		CodeServiceProcessIdentity ownerIdentity,
		CancellationToken cancellationToken
	)
	{
		string descriptorPath;
		try
		{
			descriptorPath = CodeServiceSessionPathResolver.ResolveDescriptorPath(ownerIdentity);
		}
		catch (Exception exception)
		{
			return CodeServiceSessionDescriptorReadResult.Unavailable(
				"descriptor path could not be resolved: " + ToSingleLine(exception.Message)
			);
		}

		try
		{
			using FileStream stream = new(
				descriptorPath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete,
				4096,
				FileOptions.Asynchronous | FileOptions.SequentialScan
			);

			if (stream.Length <= 0)
			{
				return CodeServiceSessionDescriptorReadResult.Invalid(
					descriptorPath,
					"descriptor was empty."
				);
			}

			if (stream.Length > CodeServiceClientProtocol.MaxDescriptorSizeBytes)
			{
				return CodeServiceSessionDescriptorReadResult.Invalid(
					descriptorPath,
					"descriptor exceeded the 16 KiB size boundary."
				);
			}

			byte[] buffer = new byte[CodeServiceClientProtocol.MaxDescriptorSizeBytes + 1];
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

			if (totalRead <= 0)
			{
				return CodeServiceSessionDescriptorReadResult.Invalid(
					descriptorPath,
					"descriptor was empty."
				);
			}

			if (totalRead > CodeServiceClientProtocol.MaxDescriptorSizeBytes)
			{
				return CodeServiceSessionDescriptorReadResult.Invalid(
					descriptorPath,
					"descriptor exceeded the 16 KiB size boundary."
				);
			}

			try
			{
				return ParseDescriptor(
					descriptorPath,
					buffer.AsMemory(0, totalRead),
					ownerIdentity
				);
			}
			finally
			{
				CryptographicOperations.ZeroMemory(buffer);
			}
		}
		catch (FileNotFoundException)
		{
			return CodeServiceSessionDescriptorReadResult.NotFound(descriptorPath);
		}
		catch (DirectoryNotFoundException)
		{
			return CodeServiceSessionDescriptorReadResult.NotFound(descriptorPath);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception)
		{
			return CodeServiceSessionDescriptorReadResult.Unavailable(
				descriptorPath,
				"descriptor could not be read safely: " + ToSingleLine(exception.Message)
			);
		}
	}

	private static CodeServiceSessionDescriptorReadResult ParseDescriptor(
		string descriptorPath,
		ReadOnlyMemory<byte> bytes,
		CodeServiceProcessIdentity ownerIdentity
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
				return Invalid(descriptorPath, "descriptor root was not a JSON object.");

			int schemaVersion = 0;
			int protocolVersion = 0;
			string serviceVersion = null;
			string sessionId = null;
			int godotPid = 0;
			long godotStartTimeUtcTicks = 0;
			int servicePid = 0;
			long serviceStartTimeUtcTicks = 0;
			string transport = null;
			string address = null;
			int port = 0;
			string authenticationToken = null;

			bool hasSchemaVersion = false;
			bool hasProtocolVersion = false;
			bool hasServiceVersion = false;
			bool hasSessionId = false;
			bool hasGodotPid = false;
			bool hasGodotStart = false;
			bool hasServicePid = false;
			bool hasServiceStart = false;
			bool hasTransport = false;
			bool hasAddress = false;
			bool hasPort = false;
			bool hasAuthenticationToken = false;

			foreach (JsonProperty property in root.EnumerateObject())
			{
				switch (property.Name)
				{
					case "schemaVersion":
						if (hasSchemaVersion || !property.Value.TryGetInt32(out schemaVersion))
							return Invalid(descriptorPath, "descriptor schemaVersion was duplicate or invalid.");
						hasSchemaVersion = true;
						break;
					case "protocolVersion":
						if (hasProtocolVersion || !property.Value.TryGetInt32(out protocolVersion))
							return Invalid(descriptorPath, "descriptor protocolVersion was duplicate or invalid.");
						hasProtocolVersion = true;
						break;
					case "serviceVersion":
						if (hasServiceVersion || !TryGetBoundedString(property.Value, 128, out serviceVersion))
							return Invalid(descriptorPath, "descriptor serviceVersion was duplicate or invalid.");
						hasServiceVersion = true;
						break;
					case "sessionId":
						if (hasSessionId || !TryGetBoundedString(property.Value, 32, out sessionId))
							return Invalid(descriptorPath, "descriptor sessionId was duplicate or invalid.");
						hasSessionId = true;
						break;
					case "godotPid":
						if (hasGodotPid || !property.Value.TryGetInt32(out godotPid))
							return Invalid(descriptorPath, "descriptor godotPid was duplicate or invalid.");
						hasGodotPid = true;
						break;
					case "godotStartTimeUtcTicks":
						if (hasGodotStart || !property.Value.TryGetInt64(out godotStartTimeUtcTicks))
							return Invalid(descriptorPath, "descriptor Godot start identity was duplicate or invalid.");
						hasGodotStart = true;
						break;
					case "servicePid":
						if (hasServicePid || !property.Value.TryGetInt32(out servicePid))
							return Invalid(descriptorPath, "descriptor servicePid was duplicate or invalid.");
						hasServicePid = true;
						break;
					case "serviceStartTimeUtcTicks":
						if (hasServiceStart || !property.Value.TryGetInt64(out serviceStartTimeUtcTicks))
							return Invalid(descriptorPath, "descriptor service start identity was duplicate or invalid.");
						hasServiceStart = true;
						break;
					case "transport":
						if (hasTransport || !TryGetBoundedString(property.Value, 16, out transport))
							return Invalid(descriptorPath, "descriptor transport was duplicate or invalid.");
						hasTransport = true;
						break;
					case "address":
						if (hasAddress || !TryGetBoundedString(property.Value, 64, out address))
							return Invalid(descriptorPath, "descriptor address was duplicate or invalid.");
						hasAddress = true;
						break;
					case "port":
						if (hasPort || !property.Value.TryGetInt32(out port))
							return Invalid(descriptorPath, "descriptor port was duplicate or invalid.");
						hasPort = true;
						break;
					case "authenticationToken":
						if (
							hasAuthenticationToken
							|| !TryGetBoundedString(
								property.Value,
								CodeServiceClientProtocol.AuthenticationTokenBase64Length,
								out authenticationToken
							)
						)
						{
							return Invalid(descriptorPath, "descriptor authentication token was duplicate or invalid.");
						}
						hasAuthenticationToken = true;
						break;
				}
			}

			if (
				!hasSchemaVersion
				|| !hasProtocolVersion
				|| !hasServiceVersion
				|| !hasSessionId
				|| !hasGodotPid
				|| !hasGodotStart
				|| !hasServicePid
				|| !hasServiceStart
				|| !hasTransport
				|| !hasAddress
				|| !hasPort
				|| !hasAuthenticationToken
			)
			{
				return Invalid(descriptorPath, "descriptor was missing one or more required fields.");
			}

			if (schemaVersion != CodeServiceClientProtocol.DescriptorSchemaVersion)
				return Invalid(descriptorPath, "descriptor schema version is not supported.");
			if (protocolVersion <= 0)
				return Invalid(descriptorPath, "descriptor protocolVersion must be positive.");
			if (!IsUsableServiceVersion(serviceVersion))
				return Invalid(descriptorPath, "descriptor serviceVersion was not usable.");
			if (!IsValidSessionId(sessionId))
				return Invalid(descriptorPath, "descriptor sessionId did not match the current format.");
			if (godotPid <= 0 || godotStartTimeUtcTicks <= 0)
				return Invalid(descriptorPath, "descriptor Godot owner identity was invalid.");
			if (
				godotPid != ownerIdentity.ProcessId
				|| godotStartTimeUtcTicks != ownerIdentity.StartTimeUtcTicks
			)
			{
				return Invalid(descriptorPath, "descriptor Godot owner identity did not match the current editor process.");
			}
			if (servicePid <= 0 || serviceStartTimeUtcTicks <= 0)
				return Invalid(descriptorPath, "descriptor service process identity was invalid.");
			if (servicePid == ownerIdentity.ProcessId)
				return Invalid(descriptorPath, "descriptor service process pointed at the Godot owner process.");
			if (!string.Equals(transport, CodeServiceClientProtocol.Transport, StringComparison.Ordinal))
				return Invalid(descriptorPath, "descriptor transport was not http.");
			if (!string.Equals(address, CodeServiceClientProtocol.Address, StringComparison.Ordinal))
				return Invalid(descriptorPath, "descriptor address was not the exact loopback address.");
			if (port < 1 || port > 65535)
				return Invalid(descriptorPath, "descriptor port was outside the valid TCP range.");
			if (
				authenticationToken.Length
				!= CodeServiceClientProtocol.AuthenticationTokenBase64Length
			)
			{
				return Invalid(descriptorPath, "descriptor authentication token length was invalid.");
			}

			if (
				!CodeServiceClientCredentials.TryCreateFromBase64(
					authenticationToken,
					out CodeServiceClientCredentials credentials
				)
			)
			{
				return Invalid(descriptorPath, "descriptor authentication token encoding was invalid.");
			}

			CodeServiceSessionDescriptor descriptor = new(
				descriptorPath,
				schemaVersion,
				protocolVersion,
				serviceVersion,
				sessionId,
				new CodeServiceProcessIdentity(godotPid, godotStartTimeUtcTicks),
				new CodeServiceProcessIdentity(servicePid, serviceStartTimeUtcTicks),
				transport,
				address,
				port,
				credentials
			);
			return CodeServiceSessionDescriptorReadResult.Success(descriptor);
		}
		catch (JsonException)
		{
			return Invalid(descriptorPath, "descriptor JSON was malformed.");
		}
		catch (Exception exception)
		{
			return Invalid(
				descriptorPath,
				"descriptor validation failed: " + ToSingleLine(exception.Message)
			);
		}
	}

	private static bool TryGetBoundedString(JsonElement element, int maxLength, out string value)
	{
		value = null;
		if (element.ValueKind != JsonValueKind.String)
			return false;
		value = element.GetString();
		return value != null && value.Length > 0 && value.Length <= maxLength;
	}

	private static bool IsUsableServiceVersion(string value)
	{
		if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
			return false;
		foreach (char character in value)
		{
			if (char.IsControl(character))
				return false;
		}
		return true;
	}

	internal static bool IsValidSessionId(string value)
	{
		if (value == null || value.Length != 32)
			return false;
		foreach (char character in value)
		{
			bool digit = character >= '0' && character <= '9';
			bool lowerHex = character >= 'a' && character <= 'f';
			if (!digit && !lowerHex)
				return false;
		}
		return true;
	}

	private static CodeServiceSessionDescriptorReadResult Invalid(
		string descriptorPath,
		string detail
	)
	{
		return CodeServiceSessionDescriptorReadResult.Invalid(descriptorPath, detail);
	}

	private static string ToSingleLine(string message)
	{
		return (message ?? "").Replace('\r', ' ').Replace('\n', ' ');
	}
}

internal sealed class CodeServiceSessionDescriptor : IDisposable
{
	private CodeServiceClientCredentials _credentials;

	internal CodeServiceSessionDescriptor(
		string descriptorPath,
		int schemaVersion,
		int protocolVersion,
		string serviceVersion,
		string sessionId,
		CodeServiceProcessIdentity godotOwnerIdentity,
		CodeServiceProcessIdentity serviceProcessIdentity,
		string transport,
		string address,
		int port,
		CodeServiceClientCredentials credentials
	)
	{
		DescriptorPath = descriptorPath;
		SchemaVersion = schemaVersion;
		ProtocolVersion = protocolVersion;
		ServiceVersion = serviceVersion;
		SessionId = sessionId;
		GodotOwnerIdentity = godotOwnerIdentity;
		ServiceProcessIdentity = serviceProcessIdentity;
		Transport = transport;
		Address = address;
		Port = port;
		_credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
	}

	internal string DescriptorPath { get; }
	internal int SchemaVersion { get; }
	internal int ProtocolVersion { get; }
	internal string ServiceVersion { get; }
	internal string SessionId { get; }
	internal CodeServiceProcessIdentity GodotOwnerIdentity { get; }
	internal CodeServiceProcessIdentity ServiceProcessIdentity { get; }
	internal string Transport { get; }
	internal string Address { get; }
	internal int Port { get; }

	internal CodeServiceClientCredentials TakeCredentials()
	{
		CodeServiceClientCredentials credentials = System.Threading.Interlocked.Exchange(
			ref _credentials,
			null
		);
		return credentials ?? throw new ObjectDisposedException(nameof(CodeServiceSessionDescriptor));
	}

	public void Dispose()
	{
		CodeServiceClientCredentials credentials = System.Threading.Interlocked.Exchange(
			ref _credentials,
			null
		);
		credentials?.Dispose();
	}
}

internal enum CodeServiceSessionDescriptorReadStatus
{
	Success,
	NotFound,
	Invalid,
	Unavailable,
}

internal readonly struct CodeServiceSessionDescriptorReadResult
{
	private CodeServiceSessionDescriptorReadResult(
		CodeServiceSessionDescriptorReadStatus status,
		CodeServiceSessionDescriptor descriptor,
		string descriptorPath,
		string detail
	)
	{
		Status = status;
		Descriptor = descriptor;
		DescriptorPath = descriptorPath ?? "";
		Detail = detail ?? "";
	}

	internal CodeServiceSessionDescriptorReadStatus Status { get; }
	internal CodeServiceSessionDescriptor Descriptor { get; }
	internal string DescriptorPath { get; }
	internal string Detail { get; }
	internal bool IsSuccess => Status == CodeServiceSessionDescriptorReadStatus.Success;

	internal static CodeServiceSessionDescriptorReadResult Success(
		CodeServiceSessionDescriptor descriptor
	)
	{
		return new CodeServiceSessionDescriptorReadResult(
			CodeServiceSessionDescriptorReadStatus.Success,
			descriptor,
			descriptor.DescriptorPath,
			""
		);
	}

	internal static CodeServiceSessionDescriptorReadResult NotFound(string path)
	{
		return new CodeServiceSessionDescriptorReadResult(
			CodeServiceSessionDescriptorReadStatus.NotFound,
			null,
			path,
			"descriptor was not present."
		);
	}

	internal static CodeServiceSessionDescriptorReadResult Invalid(string path, string detail)
	{
		return new CodeServiceSessionDescriptorReadResult(
			CodeServiceSessionDescriptorReadStatus.Invalid,
			null,
			path,
			detail
		);
	}

	internal static CodeServiceSessionDescriptorReadResult Unavailable(string detail)
	{
		return Unavailable("", detail);
	}

	internal static CodeServiceSessionDescriptorReadResult Unavailable(string path, string detail)
	{
		return new CodeServiceSessionDescriptorReadResult(
			CodeServiceSessionDescriptorReadStatus.Unavailable,
			null,
			path,
			detail
		);
	}
}
#endif
