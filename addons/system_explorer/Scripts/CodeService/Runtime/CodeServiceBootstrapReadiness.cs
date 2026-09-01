#if TOOLS
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using SystemExplorer.CodeService.Client;

namespace SystemExplorer.CodeService.Runtime;

internal static class CodeServiceBootstrapReadiness
{
	internal static CodeServiceBootstrapReadinessParseResult TryParse(
		string line,
		CodeServiceProcessIdentity ownerIdentity,
		string expectedServiceVersion,
		string expectedDescriptorPath,
		CodeServiceProcessIdentity? launchedServiceIdentity
	)
	{
		if (string.IsNullOrWhiteSpace(line))
			return CodeServiceBootstrapReadinessParseResult.Invalid("readiness line was empty.");

		if (Encoding.UTF8.GetByteCount(line) > CodeServiceClientProtocol.MaxReadinessLineBytes)
			return CodeServiceBootstrapReadinessParseResult.Invalid("readiness line exceeded the 16 KiB boundary.");

		try
		{
			using JsonDocument document = JsonDocument.Parse(
				line,
				new JsonDocumentOptions
				{
					AllowTrailingCommas = false,
					CommentHandling = JsonCommentHandling.Disallow,
					MaxDepth = 8,
				}
			);

			JsonElement root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
				return CodeServiceBootstrapReadinessParseResult.Invalid("readiness root was not an object.");

			int schemaVersion = 0;
			string type = null;
			int protocolVersion = 0;
			string serviceVersion = null;
			string sessionId = null;
			int godotPid = 0;
			long godotStart = 0;
			int servicePid = 0;
			long serviceStart = 0;
			string descriptorPath = null;

			bool schemaSeen = false;
			bool typeSeen = false;
			bool protocolSeen = false;
			bool serviceVersionSeen = false;
			bool sessionSeen = false;
			bool godotPidSeen = false;
			bool godotStartSeen = false;
			bool servicePidSeen = false;
			bool serviceStartSeen = false;
			bool descriptorPathSeen = false;

			foreach (JsonProperty property in root.EnumerateObject())
			{
				switch (property.Name)
				{
					case "schemaVersion":
						if (schemaSeen || !property.Value.TryGetInt32(out schemaVersion))
							return Invalid("readiness schemaVersion was duplicate or invalid.");
						schemaSeen = true;
						break;
					case "type":
						if (typeSeen || !TryGetString(property.Value, 64, out type))
							return Invalid("readiness type was duplicate or invalid.");
						typeSeen = true;
						break;
					case "protocolVersion":
						if (protocolSeen || !property.Value.TryGetInt32(out protocolVersion))
							return Invalid("readiness protocolVersion was duplicate or invalid.");
						protocolSeen = true;
						break;
					case "serviceVersion":
						if (serviceVersionSeen || !TryGetString(property.Value, 128, out serviceVersion))
							return Invalid("readiness serviceVersion was duplicate or invalid.");
						serviceVersionSeen = true;
						break;
					case "sessionId":
						if (sessionSeen || !TryGetString(property.Value, 32, out sessionId))
							return Invalid("readiness sessionId was duplicate or invalid.");
						sessionSeen = true;
						break;
					case "godotPid":
						if (godotPidSeen || !property.Value.TryGetInt32(out godotPid))
							return Invalid("readiness godotPid was duplicate or invalid.");
						godotPidSeen = true;
						break;
					case "godotStartTimeUtcTicks":
						if (godotStartSeen || !property.Value.TryGetInt64(out godotStart))
							return Invalid("readiness Godot start identity was duplicate or invalid.");
						godotStartSeen = true;
						break;
					case "servicePid":
						if (servicePidSeen || !property.Value.TryGetInt32(out servicePid))
							return Invalid("readiness servicePid was duplicate or invalid.");
						servicePidSeen = true;
						break;
					case "serviceStartTimeUtcTicks":
						if (serviceStartSeen || !property.Value.TryGetInt64(out serviceStart))
							return Invalid("readiness service start identity was duplicate or invalid.");
						serviceStartSeen = true;
						break;
					case "descriptorPath":
						if (descriptorPathSeen || !TryGetString(property.Value, 4096, out descriptorPath))
							return Invalid("readiness descriptorPath was duplicate or invalid.");
						descriptorPathSeen = true;
						break;
				}
			}

			if (
				!schemaSeen
				|| !typeSeen
				|| !protocolSeen
				|| !serviceVersionSeen
				|| !sessionSeen
				|| !godotPidSeen
				|| !godotStartSeen
				|| !servicePidSeen
				|| !serviceStartSeen
				|| !descriptorPathSeen
			)
			{
				return Invalid("readiness record was missing required fields.");
			}

			if (schemaVersion != CodeServiceClientProtocol.ReadinessSchemaVersion)
				return Invalid("readiness schemaVersion was not supported.");
			if (!string.Equals(type, CodeServiceClientProtocol.ReadinessRecordType, StringComparison.Ordinal))
				return Invalid("readiness type was not codeservice.ready.");
			if (protocolVersion != CodeServiceClientProtocol.ProtocolVersion)
				return Invalid("readiness protocolVersion was incompatible.");
			if (!string.Equals(serviceVersion, expectedServiceVersion, StringComparison.Ordinal))
				return Invalid("readiness serviceVersion did not match the required version.");
			if (!CodeServiceSessionDescriptorReader.IsValidSessionId(sessionId))
				return Invalid("readiness sessionId did not match the current format.");
			if (godotPid != ownerIdentity.ProcessId || godotStart != ownerIdentity.StartTimeUtcTicks)
				return Invalid("readiness Godot owner identity did not match the current editor process.");
			if (servicePid <= 0 || serviceStart <= 0)
				return Invalid("readiness service process identity was invalid.");
			if (string.IsNullOrWhiteSpace(descriptorPath) || !Path.IsPathFullyQualified(descriptorPath))
				return Invalid("readiness descriptorPath was not an absolute path.");

			string normalizedExpected = Path.GetFullPath(expectedDescriptorPath);
			string normalizedActual = Path.GetFullPath(descriptorPath);
			StringComparison pathComparison = OperatingSystem.IsWindows()
				? StringComparison.OrdinalIgnoreCase
				: StringComparison.Ordinal;
			if (!string.Equals(normalizedExpected, normalizedActual, pathComparison))
				return Invalid("readiness descriptorPath did not match the deterministic owner path.");

			CodeServiceProcessIdentity serviceIdentity = new(servicePid, serviceStart);
			if (
				launchedServiceIdentity.HasValue
				&& (
					launchedServiceIdentity.Value.ProcessId != serviceIdentity.ProcessId
					|| launchedServiceIdentity.Value.StartTimeUtcTicks
						!= serviceIdentity.StartTimeUtcTicks
				)
			)
			{
				return Invalid("readiness service identity did not match the launched process identity.");
			}

			return CodeServiceBootstrapReadinessParseResult.Success(
				new CodeServiceBootstrapReadinessRecord(
					serviceVersion,
					sessionId,
					ownerIdentity,
					serviceIdentity,
					normalizedActual
				)
			);
		}
		catch (JsonException)
		{
			return Invalid("readiness JSON was malformed.");
		}
		catch (Exception exception)
		{
			return Invalid("readiness validation failed: " + ToSingleLine(exception.Message));
		}
	}

	private static bool TryGetString(JsonElement element, int maxLength, out string value)
	{
		value = null;
		if (element.ValueKind != JsonValueKind.String)
			return false;
		value = element.GetString();
		return value != null && value.Length > 0 && value.Length <= maxLength;
	}

	private static CodeServiceBootstrapReadinessParseResult Invalid(string detail)
	{
		return CodeServiceBootstrapReadinessParseResult.Invalid(detail);
	}

	private static string ToSingleLine(string message)
	{
		return (message ?? "").Replace('\r', ' ').Replace('\n', ' ');
	}
}

internal readonly struct CodeServiceBootstrapReadinessRecord
{
	internal CodeServiceBootstrapReadinessRecord(
		string serviceVersion,
		string sessionId,
		CodeServiceProcessIdentity godotOwnerIdentity,
		CodeServiceProcessIdentity serviceProcessIdentity,
		string descriptorPath
	)
	{
		ServiceVersion = serviceVersion;
		SessionId = sessionId;
		GodotOwnerIdentity = godotOwnerIdentity;
		ServiceProcessIdentity = serviceProcessIdentity;
		DescriptorPath = descriptorPath;
	}

	internal string ServiceVersion { get; }
	internal string SessionId { get; }
	internal CodeServiceProcessIdentity GodotOwnerIdentity { get; }
	internal CodeServiceProcessIdentity ServiceProcessIdentity { get; }
	internal string DescriptorPath { get; }
}

internal readonly struct CodeServiceBootstrapReadinessParseResult
{
	private CodeServiceBootstrapReadinessParseResult(
		bool success,
		CodeServiceBootstrapReadinessRecord record,
		string detail
	)
	{
		IsSuccess = success;
		Record = record;
		Detail = detail ?? "";
	}

	internal bool IsSuccess { get; }
	internal CodeServiceBootstrapReadinessRecord Record { get; }
	internal string Detail { get; }

	internal static CodeServiceBootstrapReadinessParseResult Success(
		CodeServiceBootstrapReadinessRecord record
	)
	{
		return new CodeServiceBootstrapReadinessParseResult(true, record, "");
	}

	internal static CodeServiceBootstrapReadinessParseResult Invalid(string detail)
	{
		return new CodeServiceBootstrapReadinessParseResult(false, default, detail);
	}
}
#endif
