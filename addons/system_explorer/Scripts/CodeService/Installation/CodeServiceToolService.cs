#if TOOLS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using SystemExplorer.EditorIntegration.Operations;

namespace SystemExplorer.CodeService.Installation;

internal sealed class CodeServiceToolService
{
	internal const string PackageId = "SystemExplorer.CodeService";
	internal const string ToolCommand = "system-explorer-code";
	internal const string RequiredVersion = "0.1.0";

	private const int DefaultProbeTimeoutMilliseconds = 3000;
	private const int DefaultInstallationTimeoutMilliseconds = 120000;
	private const int MaximumUserDetailLength = 1200;

	private readonly Func<string> _workingDirectoryProvider;
	private readonly Action<string, string> _logOperation;
	private readonly CodeServiceProcessRunner _processRunner;
	private readonly int _probeTimeoutMilliseconds;
	private readonly int _installationTimeoutMilliseconds;

	internal CodeServiceToolService(
		Func<string> workingDirectoryProvider,
		Action<string, string> logOperation,
		CodeServiceProcessRunner processRunner,
		int probeTimeoutMilliseconds = DefaultProbeTimeoutMilliseconds,
		int installationTimeoutMilliseconds = DefaultInstallationTimeoutMilliseconds
	)
	{
		_workingDirectoryProvider =
			workingDirectoryProvider
			?? throw new ArgumentNullException(nameof(workingDirectoryProvider));
		_logOperation = logOperation ?? throw new ArgumentNullException(nameof(logOperation));
		_processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
		if (probeTimeoutMilliseconds <= 0)
			throw new ArgumentOutOfRangeException(nameof(probeTimeoutMilliseconds));
		if (installationTimeoutMilliseconds <= 0)
			throw new ArgumentOutOfRangeException(nameof(installationTimeoutMilliseconds));

		_probeTimeoutMilliseconds = probeTimeoutMilliseconds;
		_installationTimeoutMilliseconds = installationTimeoutMilliseconds;
	}

	internal bool IsGlobalToolPresentForMenu()
		=> TryResolveLaunchExecutable(out _);

	internal bool TryResolveLaunchExecutable(out string executable)
	{
		string globalToolShimPath = GetGlobalToolShimPath();
		if (!string.IsNullOrWhiteSpace(globalToolShimPath) && File.Exists(globalToolShimPath))
		{
			executable = globalToolShimPath;
			return true;
		}

		return TryResolveExecutableOnPath(ToolCommand, out executable);
	}

	internal async Task<CodeServiceToolProbeResult> ProbeAsync(EditorOperationLease operation)
	{
		if (operation == null)
			throw new ArgumentNullException(nameof(operation));

		operation.CancellationToken.ThrowIfCancellationRequested();

		ProcessStartInfo startInfo = CreateProcessStartInfo("dotnet");
		startInfo.ArgumentList.Add("tool");
		startInfo.ArgumentList.Add("list");
		startInfo.ArgumentList.Add("--global");

		CodeServiceProcessResult processResult = await _processRunner.RunAsync(
			startInfo,
			_probeTimeoutMilliseconds,
			operation
		);

		operation.CancellationToken.ThrowIfCancellationRequested();

		if (!processResult.Started)
		{
			return new CodeServiceToolProbeResult(
				CodeServiceToolProbeStatus.Failed,
				"",
				SelectTechnicalDetail(processResult)
			);
		}

		if (processResult.TimedOut)
		{
			return new CodeServiceToolProbeResult(
				CodeServiceToolProbeStatus.TimedOut,
				"",
				"Global .NET Tool list probe timed out."
			);
		}

		if (processResult.ExitCode != 0)
		{
			return new CodeServiceToolProbeResult(
				CodeServiceToolProbeStatus.Failed,
				"",
				SelectTechnicalDetail(processResult)
			);
		}

		return ParseGlobalToolList(processResult.StandardOutput);
	}

	internal async Task<CodeServiceInstallationResult> InstallRequiredVersionAsync(
		EditorOperationLease operation
	)
	{
		if (operation == null)
			throw new ArgumentNullException(nameof(operation));

		CodeServiceToolProbeResult preInstallProbe = await ProbeAsync(operation);
		operation.CancellationToken.ThrowIfCancellationRequested();

		if (preInstallProbe.RequiredVersionVerified)
			return CreateReadyResult(preInstallProbe.InstalledVersion);

		if (preInstallProbe.Status == CodeServiceToolProbeStatus.VersionMismatch)
			return CreateVersionMismatchResult(preInstallProbe.InstalledVersion);

		ProcessStartInfo installStartInfo = CreateProcessStartInfo("dotnet");
		installStartInfo.ArgumentList.Add("tool");
		installStartInfo.ArgumentList.Add("install");
		installStartInfo.ArgumentList.Add("--global");
		installStartInfo.ArgumentList.Add(PackageId);
		installStartInfo.ArgumentList.Add("--version");
		installStartInfo.ArgumentList.Add(RequiredVersion);

		_logOperation(
			"CodeService Installation Started",
			$"Package='{PackageId}', RequiredVersion='{RequiredVersion}'"
		);

		CodeServiceProcessResult installResult = await _processRunner.RunAsync(
			installStartInfo,
			_installationTimeoutMilliseconds,
			operation
		);

		operation.CancellationToken.ThrowIfCancellationRequested();

		if (!installResult.Started)
		{
			string detail = SelectTechnicalDetail(installResult);
			_logOperation("CodeService Installation Could Not Start", detail);
			return new CodeServiceInstallationResult(
				false,
				"",
				"Failed to install C# Code Intelligence.\n\n"
					+ "The installation process could not be started. A compatible .NET SDK is required."
			);
		}

		if (installResult.TimedOut)
		{
			string detail = SelectTechnicalDetail(installResult);
			_logOperation("CodeService Installation Timed Out", detail);
			return new CodeServiceInstallationResult(
				false,
				"",
				"Failed to install C# Code Intelligence.\n\n"
					+ "SystemExplorer.CodeService installation timed out."
			);
		}

		if (installResult.ExitCode != 0)
		{
			string detail = SelectTechnicalDetail(installResult);
			_logOperation(
				"CodeService Installation Command Failed",
				$"ExitCode='{installResult.ExitCode}', Detail='{detail}'"
			);

			CodeServiceToolProbeResult raceProbe = await ProbeAsync(operation);
			operation.CancellationToken.ThrowIfCancellationRequested();
			if (raceProbe.RequiredVersionVerified)
				return CreateReadyResult(raceProbe.InstalledVersion);
			if (raceProbe.Status == CodeServiceToolProbeStatus.VersionMismatch)
				return CreateVersionMismatchResult(raceProbe.InstalledVersion);

			return new CodeServiceInstallationResult(false, "", BuildInstallFailureMessage(detail));
		}

		_logOperation(
			"CodeService Installation Command Completed",
			$"ExitCode='{installResult.ExitCode}'"
		);

		CodeServiceToolProbeResult postInstallProbe = await ProbeAsync(operation);
		operation.CancellationToken.ThrowIfCancellationRequested();
		if (postInstallProbe.RequiredVersionVerified)
			return CreateReadyResult(postInstallProbe.InstalledVersion);

		string verificationDetail = BuildProbeTechnicalDetail(postInstallProbe);
		_logOperation("CodeService Post-Install Verification Failed", verificationDetail);
		return new CodeServiceInstallationResult(
			false,
			postInstallProbe.InstalledVersion,
			"Failed to install C# Code Intelligence.\n\n"
				+ $"SystemExplorer.CodeService installation completed, but version {RequiredVersion} could not be verified."
		);
	}

	private ProcessStartInfo CreateProcessStartInfo(string executable)
	{
		return new ProcessStartInfo
		{
			FileName = executable,
			WorkingDirectory = GetWorkingDirectory(),
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
		};
	}

	private string GetWorkingDirectory()
	{
		string workingDirectory = _workingDirectoryProvider();
		return string.IsNullOrWhiteSpace(workingDirectory)
			? Environment.CurrentDirectory
			: workingDirectory;
	}

	private static string GetGlobalToolShimPath()
	{
		string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if (string.IsNullOrWhiteSpace(profile))
			return "";

		return Path.Combine(
			profile,
			".dotnet",
			"tools",
			OperatingSystem.IsWindows() ? ToolCommand + ".exe" : ToolCommand
		);
	}

	private static bool TryResolveExecutableOnPath(string command, out string resolvedPath)
	{
		resolvedPath = "";
		if (string.IsNullOrWhiteSpace(command))
			return false;

		string path = Environment.GetEnvironmentVariable("PATH") ?? "";
		if (string.IsNullOrWhiteSpace(path))
			return false;

		string[] extensions = GetExecutableExtensions(command);
		foreach (
			string directory in path.Split(
				Path.PathSeparator,
				StringSplitOptions.RemoveEmptyEntries
			)
		)
		{
			string trimmedDirectory = directory.Trim().Trim('"');
			if (string.IsNullOrWhiteSpace(trimmedDirectory))
				continue;

			foreach (string extension in extensions)
			{
				string candidate;
				try
				{
					candidate = Path.Combine(trimmedDirectory, command + extension);
				}
				catch
				{
					continue;
				}

				if (!File.Exists(candidate))
					continue;

				resolvedPath = candidate;
				return true;
			}
		}

		return false;
	}

	private static string[] GetExecutableExtensions(string command)
	{
		if (!OperatingSystem.IsWindows())
			return new[] { "" };

		if (Path.HasExtension(command))
			return new[] { "" };

		string pathExtensions =
			Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD";
		string[] rawExtensions = pathExtensions.Split(';', StringSplitOptions.RemoveEmptyEntries);
		if (rawExtensions.Length == 0)
			return new[] { ".exe", "" };

		var normalizedExtensions = new List<string>(rawExtensions.Length + 1);
		foreach (string extension in rawExtensions)
		{
			string trimmed = extension.Trim();
			if (string.IsNullOrWhiteSpace(trimmed))
				continue;
			normalizedExtensions.Add(trimmed.StartsWith('.') ? trimmed : "." + trimmed);
		}
		normalizedExtensions.Add("");
		return normalizedExtensions.ToArray();
	}

	private static CodeServiceToolProbeResult ParseGlobalToolList(string stdout)
	{
		if (string.IsNullOrWhiteSpace(stdout))
		{
			return new CodeServiceToolProbeResult(
				CodeServiceToolProbeStatus.Failed,
				"",
				"'dotnet tool list --global' returned empty stdout."
			);
		}

		string matchedVersion = "";
		string[] lines = stdout
			.Replace("\r", "", StringComparison.Ordinal)
			.Split('\n', StringSplitOptions.RemoveEmptyEntries);

		foreach (string rawLine in lines)
		{
			string line = rawLine.Trim();
			if (string.IsNullOrWhiteSpace(line))
				continue;

			string[] tokens = line.Split(
				new[] { ' ', '\t' },
				StringSplitOptions.RemoveEmptyEntries
			);
			if (
				tokens.Length == 0
				|| !string.Equals(tokens[0], PackageId, StringComparison.OrdinalIgnoreCase)
			)
			{
				continue;
			}

			if (tokens.Length < 2 || !Version.TryParse(tokens[1], out _))
			{
				return new CodeServiceToolProbeResult(
					CodeServiceToolProbeStatus.Failed,
					"",
					$"Global tool row for '{PackageId}' did not contain a parseable version."
				);
			}

			if (
				!string.IsNullOrWhiteSpace(matchedVersion)
				&& !string.Equals(matchedVersion, tokens[1], StringComparison.Ordinal)
			)
			{
				return new CodeServiceToolProbeResult(
					CodeServiceToolProbeStatus.Failed,
					"",
					$"Global tool list contained conflicting rows for '{PackageId}'."
				);
			}

			matchedVersion = tokens[1];
		}

		if (string.IsNullOrWhiteSpace(matchedVersion))
		{
			return new CodeServiceToolProbeResult(
				CodeServiceToolProbeStatus.NotFound,
				""
			);
		}

		if (string.Equals(matchedVersion, RequiredVersion, StringComparison.Ordinal))
		{
			return new CodeServiceToolProbeResult(
				CodeServiceToolProbeStatus.RequiredVersionVerified,
				matchedVersion
			);
		}

		return new CodeServiceToolProbeResult(
			CodeServiceToolProbeStatus.VersionMismatch,
			matchedVersion,
			$"Required version is {RequiredVersion}."
		);
	}

	private static CodeServiceInstallationResult CreateReadyResult(string installedVersion)
	{
		return new CodeServiceInstallationResult(
			true,
			installedVersion,
			"C# Code Intelligence was installed successfully."
		);
	}

	private static CodeServiceInstallationResult CreateVersionMismatchResult(
		string installedVersion
	)
	{
		string installed = string.IsNullOrWhiteSpace(installedVersion)
			? "another version"
			: $"version {installedVersion}";
		return new CodeServiceInstallationResult(
			false,
			installedVersion,
			"Failed to install C# Code Intelligence.\n\n"
				+ $"SystemExplorer.CodeService {installed} is already installed, but version {RequiredVersion} is required. "
				+ "Updating an existing installation is not available yet."
		);
	}

	private static string BuildInstallFailureMessage(string detail)
	{
		if (string.IsNullOrWhiteSpace(detail))
			return "Failed to install C# Code Intelligence.\n\nSystemExplorer.CodeService could not be installed.";

		return "Failed to install C# Code Intelligence.\n\n" + TruncateUserDetail(detail);
	}

	private static string BuildProbeTechnicalDetail(CodeServiceToolProbeResult probe)
	{
		return $"Status='{probe.Status}', InstalledVersion='{probe.InstalledVersion}', Detail='{probe.Detail}'";
	}

	private static string SelectTechnicalDetail(CodeServiceProcessResult result)
	{
		string error = result.ErrorOutput?.Trim() ?? "";
		if (!string.IsNullOrWhiteSpace(error))
			return error;

		return result.StandardOutput?.Trim() ?? "";
	}

	private static string TruncateUserDetail(string detail)
	{
		string trimmed = detail?.Trim() ?? "";
		return trimmed.Length <= MaximumUserDetailLength
			? trimmed
			: trimmed[..MaximumUserDetailLength] + "...";
	}
}
#endif
