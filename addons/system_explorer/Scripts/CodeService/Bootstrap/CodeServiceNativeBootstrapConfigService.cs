#if TOOLS
using System;
using System.IO;
using System.Text;

namespace SystemExplorer.CodeService.Bootstrap;

internal enum CodeServiceNativeBootstrapDiagnosticSynchronizationResult
{
	Updated,
	Unchanged,
	NoValidExistingConfig,
	Failed,
}

internal sealed class CodeServiceNativeBootstrapConfigService
{
	internal const int FormatVersion = 1;
	internal const int MaximumConfigBytes = 4096;

	private const int MaximumServiceVersionLength = 128;
	private const int MaximumFailureDetailLength = 512;
	private static readonly UTF8Encoding Utf8NoBom = new(false, true);

	private readonly string _configPath;

	private readonly struct ExistingConfig
	{
		internal ExistingConfig(bool enabled, string verifiedServiceVersion)
		{
			Enabled = enabled;
			VerifiedServiceVersion = verifiedServiceVersion;
		}

		internal bool Enabled { get; }
		internal string VerifiedServiceVersion { get; }
	}

	private enum ExistingConfigReadResult
	{
		Valid,
		NoValidExistingConfig,
		Failed,
	}

	internal CodeServiceNativeBootstrapConfigService(string absoluteConfigPath)
	{
		if (string.IsNullOrWhiteSpace(absoluteConfigPath))
			throw new ArgumentException("Bootstrap config path is required.", nameof(absoluteConfigPath));
		if (!Path.IsPathFullyQualified(absoluteConfigPath))
			throw new ArgumentException("Bootstrap config path must be absolute.", nameof(absoluteConfigPath));

		_configPath = Path.GetFullPath(absoluteConfigPath);
	}

	internal bool TryPublishEnabled(
		string verifiedServiceVersion,
		bool diagnosticLogging,
		out bool changed,
		out string detail
	)
	{
		changed = false;
		detail = "";

		if (!IsSafeServiceVersion(verifiedServiceVersion, allowEmpty: false))
		{
			detail = "Verified Service version is not safe for native bootstrap serialization.";
			return false;
		}

		return TryPublish(
			Serialize(
				enabled: true,
				verifiedServiceVersion,
				diagnosticLogging
			),
			out changed,
			out detail
		);
	}

	internal bool TryPublishDisabled(
		bool diagnosticLogging,
		out bool changed,
		out string detail
	)
	{
		return TryPublish(
			Serialize(
				enabled: false,
				verifiedServiceVersion: "",
				diagnosticLogging
			),
			out changed,
			out detail
		);
	}

	internal CodeServiceNativeBootstrapDiagnosticSynchronizationResult SynchronizeExistingDiagnosticLogging(
		bool diagnosticLogging,
		out string detail
	)
	{
		detail = "";

		ExistingConfigReadResult readResult = TryReadExistingConfig(
			out ExistingConfig existingConfig,
			out detail
		);
		if (readResult == ExistingConfigReadResult.NoValidExistingConfig)
			return CodeServiceNativeBootstrapDiagnosticSynchronizationResult.NoValidExistingConfig;
		if (readResult == ExistingConfigReadResult.Failed)
			return CodeServiceNativeBootstrapDiagnosticSynchronizationResult.Failed;

		string canonicalContent = Serialize(
			existingConfig.Enabled,
			existingConfig.VerifiedServiceVersion,
			diagnosticLogging
		);

		if (
			!TryPublishExisting(
				canonicalContent,
				out bool changed,
				out bool targetMissing,
				out detail
			)
		)
		{
			return targetMissing
				? CodeServiceNativeBootstrapDiagnosticSynchronizationResult.NoValidExistingConfig
				: CodeServiceNativeBootstrapDiagnosticSynchronizationResult.Failed;
		}

		return changed
			? CodeServiceNativeBootstrapDiagnosticSynchronizationResult.Updated
			: CodeServiceNativeBootstrapDiagnosticSynchronizationResult.Unchanged;
	}

	private ExistingConfigReadResult TryReadExistingConfig(
		out ExistingConfig config,
		out string detail
	)
	{
		config = default;
		detail = "";

		try
		{
			using FileStream stream = new(
				_configPath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete
			);

			byte[] payload = new byte[MaximumConfigBytes + 1];
			int totalRead = 0;
			while (totalRead < payload.Length)
			{
				int read = stream.Read(payload, totalRead, payload.Length - totalRead);
				if (read <= 0)
					break;
				totalRead += read;
			}

			if (totalRead == 0 || totalRead > MaximumConfigBytes)
				return ExistingConfigReadResult.NoValidExistingConfig;

			string content;
			try
			{
				content = Utf8NoBom.GetString(payload, 0, totalRead);
			}
			catch (DecoderFallbackException)
			{
				return ExistingConfigReadResult.NoValidExistingConfig;
			}

			if (!TryParseExistingConfig(content, out config))
				return ExistingConfigReadResult.NoValidExistingConfig;

			return ExistingConfigReadResult.Valid;
		}
		catch (FileNotFoundException)
		{
			return ExistingConfigReadResult.NoValidExistingConfig;
		}
		catch (DirectoryNotFoundException)
		{
			return ExistingConfigReadResult.NoValidExistingConfig;
		}
		catch (Exception exception)
		{
			detail = BoundSynchronizationFailureDetail(
				"read",
				exception
			);
			return ExistingConfigReadResult.Failed;
		}
	}

	private static bool TryParseExistingConfig(string content, out ExistingConfig config)
	{
		config = default;
		if (string.IsNullOrEmpty(content))
			return false;

		bool hasFormatVersion = false;
		bool hasEnabled = false;
		bool enabled = false;
		bool hasVerifiedServiceVersion = false;
		string verifiedServiceVersion = "";
		bool hasDiagnosticLogging = false;
		bool diagnosticLogging = false;

		int offset = 0;
		while (offset < content.Length)
		{
			int lineFeedIndex = content.IndexOf('\n', offset);
			int lineEnd = lineFeedIndex >= 0 ? lineFeedIndex : content.Length;
			int lineLength = lineEnd - offset;

			if (
				lineFeedIndex >= 0
				&& lineLength > 0
				&& content[offset + lineLength - 1] == '\r'
			)
			{
				lineLength--;
			}
			if (lineLength == 0)
				return false;

			ReadOnlySpan<char> line = content.AsSpan(offset, lineLength);
			int equalsIndex = line.IndexOf('=');
			if (equalsIndex <= 0)
				return false;

			ReadOnlySpan<char> key = line[..equalsIndex];
			ReadOnlySpan<char> value = line[(equalsIndex + 1)..];

			if (key.SequenceEqual("format_version".AsSpan()))
			{
				if (hasFormatVersion || !value.SequenceEqual("1".AsSpan()))
					return false;
				hasFormatVersion = true;
			}
			else if (key.SequenceEqual("enabled".AsSpan()))
			{
				if (hasEnabled || !TryParseLowercaseBoolean(value, out enabled))
					return false;
				hasEnabled = true;
			}
			else if (key.SequenceEqual("verified_service_version".AsSpan()))
			{
				if (hasVerifiedServiceVersion)
					return false;

				verifiedServiceVersion = value.ToString();
				if (!IsSafeServiceVersion(verifiedServiceVersion, allowEmpty: true))
					return false;
				hasVerifiedServiceVersion = true;
			}
			else if (key.SequenceEqual("diagnostic_logging".AsSpan()))
			{
				if (
					hasDiagnosticLogging
					|| !TryParseLowercaseBoolean(value, out diagnosticLogging)
				)
				{
					return false;
				}
				hasDiagnosticLogging = true;
			}
			else
			{
				return false;
			}

			offset = lineFeedIndex >= 0 ? lineFeedIndex + 1 : content.Length;
		}

		if (
			!hasFormatVersion
			|| !hasEnabled
			|| !hasVerifiedServiceVersion
			|| !hasDiagnosticLogging
		)
		{
			return false;
		}

		config = new ExistingConfig(enabled, verifiedServiceVersion);
		return true;
	}

	private static bool TryParseLowercaseBoolean(ReadOnlySpan<char> value, out bool result)
	{
		if (value.SequenceEqual("true".AsSpan()))
		{
			result = true;
			return true;
		}

		if (value.SequenceEqual("false".AsSpan()))
		{
			result = false;
			return true;
		}

		result = false;
		return false;
	}

	private bool TryPublish(string canonicalContent, out bool changed, out string detail)
	{
		return TryPublishCore(
			canonicalContent,
			requireExistingTarget: false,
			out changed,
			out _,
			out detail
		);
	}

	private bool TryPublishExisting(
		string canonicalContent,
		out bool changed,
		out bool targetMissing,
		out string detail
	)
	{
		return TryPublishCore(
			canonicalContent,
			requireExistingTarget: true,
			out changed,
			out targetMissing,
			out detail
		);
	}

	private bool TryPublishCore(
		string canonicalContent,
		bool requireExistingTarget,
		out bool changed,
		out bool targetMissing,
		out string detail
	)
	{
		changed = false;
		targetMissing = false;
		detail = "";
		string temporaryPath = "";

		try
		{
			byte[] payload = Utf8NoBom.GetBytes(canonicalContent);
			if (payload.Length == 0 || payload.Length > MaximumConfigBytes)
			{
				detail = $"Serialized bootstrap config exceeded the {MaximumConfigBytes}-byte bound.";
				return false;
			}

			string directory = Path.GetDirectoryName(_configPath) ?? "";
			if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
			{
				if (requireExistingTarget)
				{
					targetMissing = true;
					return false;
				}

				detail = "Native bootstrap config directory does not exist.";
				return false;
			}

			if (requireExistingTarget && !File.Exists(_configPath))
			{
				targetMissing = true;
				return false;
			}

			if (TargetMatches(payload))
				return true;

			temporaryPath = Path.Combine(
				directory,
				Path.GetFileName(_configPath)
					+ "."
					+ Environment.ProcessId
					+ "."
					+ Guid.NewGuid().ToString("N")
					+ ".tmp"
			);

			using (
				FileStream stream = new(
					temporaryPath,
					FileMode.CreateNew,
					FileAccess.Write,
					FileShare.None,
					bufferSize: MaximumConfigBytes,
					FileOptions.WriteThrough
				)
			)
			{
				stream.Write(payload, 0, payload.Length);
				stream.Flush(flushToDisk: true);
			}

			if (File.Exists(_configPath))
			{
				File.Replace(
					temporaryPath,
					_configPath,
					destinationBackupFileName: null,
					ignoreMetadataErrors: true
				);
			}
			else if (requireExistingTarget)
			{
				targetMissing = true;
				return false;
			}
			else
			{
				File.Move(temporaryPath, _configPath);
			}

			temporaryPath = "";
			changed = true;
			return true;
		}
		catch (FileNotFoundException) when (requireExistingTarget)
		{
			targetMissing = true;
			return false;
		}
		catch (DirectoryNotFoundException) when (requireExistingTarget)
		{
			targetMissing = true;
			return false;
		}
		catch (Exception exception)
		{
			detail = requireExistingTarget
				? BoundSynchronizationFailureDetail("publication", exception)
				: BoundFailureDetail(exception.Message);
			return false;
		}
		finally
		{
			if (!string.IsNullOrWhiteSpace(temporaryPath))
			{
				try
				{
					File.Delete(temporaryPath);
				}
				catch
				{
					// Best-effort cleanup must not replace the original publication result.
				}
			}
		}
	}

	private static string Serialize(
		bool enabled,
		string verifiedServiceVersion,
		bool diagnosticLogging
	)
	{
		return "format_version="
			+ FormatVersion
			+ "\n"
			+ "enabled="
			+ (enabled ? "true" : "false")
			+ "\n"
			+ "verified_service_version="
			+ verifiedServiceVersion
			+ "\n"
			+ "diagnostic_logging="
			+ (diagnosticLogging ? "true" : "false")
			+ "\n";
	}

	private bool TargetMatches(byte[] payload)
	{
		if (!File.Exists(_configPath))
			return false;

		using FileStream stream = new(
			_configPath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.ReadWrite | FileShare.Delete
		);
		if (stream.Length != payload.Length || stream.Length > MaximumConfigBytes)
			return false;

		byte[] existing = new byte[payload.Length];
		int totalRead = 0;
		while (totalRead < existing.Length)
		{
			int read = stream.Read(existing, totalRead, existing.Length - totalRead);
			if (read <= 0)
				return false;
			totalRead += read;
		}

		return existing.AsSpan().SequenceEqual(payload);
	}

	private static bool IsSafeServiceVersion(string value, bool allowEmpty)
	{
		if (string.IsNullOrEmpty(value))
			return allowEmpty;
		if (value.Length > MaximumServiceVersionLength)
			return false;

		foreach (char character in value)
		{
			if (
				character < 0x21
				|| character > 0x7e
				|| character == '='
			)
			{
				return false;
			}
		}

		return true;
	}

	private static string BoundSynchronizationFailureDetail(
		string operation,
		Exception exception
	)
	{
		string exceptionName = exception?.GetType().Name ?? "Exception";
		int hResult = exception?.HResult ?? 0;
		return BoundFailureDetail(
			$"Existing native bootstrap config {operation} failed: {exceptionName}, HResult=0x{hResult:X8}."
		);
	}

	private static string BoundFailureDetail(string detail)
	{
		string bounded = (detail ?? "")
			.Replace('\r', ' ')
			.Replace('\n', ' ')
			.Trim();
		if (string.IsNullOrEmpty(bounded))
			bounded = "Native bootstrap config publication failed.";
		return bounded.Length <= MaximumFailureDetailLength
			? bounded
			: bounded[..MaximumFailureDetailLength];
	}
}
#endif
