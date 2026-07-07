#if TOOLS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using IOPath = System.IO.Path;
using System.Text;
using System.Threading.Tasks;
using Godot;

public partial class SystemExplorerPlugin
{
	#region Quick Actions - CSharpier
	private const int CSharpierDetectionTimeoutMilliseconds = 3000;
	private const int CSharpierWarmUpTimeoutMilliseconds = 10000;
	private const int CSharpierFormatTimeoutMilliseconds = 30000;
	private const int CSharpierInstallTimeoutMilliseconds = 120000;
	private const int CSharpierDebugPreviewLength = 500;

	private bool _isInstallingCSharpier;
	private bool _isDebugUninstallingCSharpier;
	private bool _isWarmingUpCSharpierCommandCache;
	private CSharpierCommand _cachedCSharpierCommand;

	// Dev-only debug switch for testing the CSharpier install flow.
	// Keep false for normal use and releases.
	private const bool DebugUninstallCSharpierOnStartup = false;

	private readonly struct CSharpierInstallResult
	{
		public CSharpierInstallResult(
			bool success,
			string message,
			CSharpierCommand command = default
		)
		{
			Success = success;
			Message = message;
			Command = command;
		}

		public bool Success { get; }
		public string Message { get; }
		public CSharpierCommand Command { get; }
	}

	private enum CSharpierProbeStatus
	{
		Failed,
		Succeeded,
		TimedOut,
	}

	private readonly struct CSharpierCommand
	{
		public CSharpierCommand(string executable, params string[] baseArguments)
		{
			Executable = executable;
			BaseArguments = baseArguments ?? Array.Empty<string>();
		}

		public string Executable { get; }
		public string[] BaseArguments { get; }
		public bool IsValid => !string.IsNullOrWhiteSpace(Executable);
	}

	private readonly struct CSharpierProbeResult
	{
		public CSharpierProbeResult(bool success, CSharpierCommand command, bool timedOut)
		{
			Success = success;
			Command = command;
			TimedOut = timedOut;
		}

		public bool Success { get; }
		public CSharpierCommand Command { get; }
		public bool TimedOut { get; }
	}

	private readonly struct CSharpierFormatResult
	{
		public CSharpierFormatResult(
			bool success,
			string formattedText,
			string message,
			bool shouldInvalidateCachedCommand = false
		)
		{
			Success = success;
			FormattedText = formattedText;
			Message = message;
			ShouldInvalidateCachedCommand = shouldInvalidateCachedCommand;
		}

		public bool Success { get; }
		public string FormattedText { get; }
		public string Message { get; }
		public bool ShouldInvalidateCachedCommand { get; }
	}


	private void OnCSharpierInstallConfirmed()
	{
		if (_isInstallingCSharpier)
			return;

		_ = InstallCSharpierAsync();
	}

	private async Task InstallCSharpierAsync()
	{
		_isInstallingCSharpier = true;
		SetCSharpierInstallButtonDisabled(true);

		CSharpierInstallResult installResult = await Task.Run(InstallCSharpierGlobalTool);

		_isInstallingCSharpier = false;
		SetCSharpierInstallButtonDisabled(false);

		if (!installResult.Success)
		{
			ClearPendingBeautifyAfterCSharpierInstall("CSharpier install failed");
			ShowCSharpierInstallResultDialog(installResult);
			return;
		}

		if (installResult.Command.IsValid)
			CacheCSharpierCommand(installResult.Command, "install");

		if (await TryRunPendingBeautifyAfterCSharpierInstall(installResult.Command))
			return;

		ShowCSharpierInstallResultDialog(installResult);
	}

	private void ShowCSharpierInstallResultDialog(CSharpierInstallResult installResult)
	{
		if (_csharpierInstallResultDialog == null)
		{
			DebugPrintBeautify(
				$"CSharpier install result: success={installResult.Success}, message='{GetDebugTextPreview(installResult.Message)}'"
			);
			return;
		}

		_csharpierInstallResultDialog.Title = installResult.Success
			? "CSharpier Installed"
			: "CSharpier Install Failed";
		_csharpierInstallResultDialog.DialogText = installResult.Message;
		_csharpierInstallResultDialog.PopupCentered();
	}

	private void StartCSharpierStartupWarmUp()
	{
		if (DebugState && DebugUninstallCSharpierOnStartup)
		{
			CallDeferred(nameof(DebugUninstallCSharpierOnStartupThenWarmUp));
		}
		else
		{
			CallDeferred(nameof(WarmUpCSharpierCommandCache));
		}
	}

	private void DebugUninstallCSharpierOnStartupThenWarmUp()
	{
		_ = DebugUninstallCSharpierOnStartupThenWarmUpAsync();
	}

	private async Task DebugUninstallCSharpierOnStartupThenWarmUpAsync()
	{
		if (_isDebugUninstallingCSharpier)
			return;

		_isDebugUninstallingCSharpier = true;
		ClearCachedCSharpierCommand("startup debug uninstall started");
		DebugPrintBeautify("Startup debug uninstall of CSharpier started.");

		try
		{
			CSharpierInstallResult uninstallResult = await Task.Run(
				ExecuteCSharpierUninstallCommandForDebug
			);

			DebugPrintBeautify(
				$"Startup debug uninstall of CSharpier finished: success={uninstallResult.Success}, message='{GetDebugTextPreview(uninstallResult.Message)}'"
			);
		}
		finally
		{
			ClearCachedCSharpierCommand("startup debug uninstall finished");
			_isDebugUninstallingCSharpier = false;
		}

		WarmUpCSharpierCommandCache();
	}

	private async void WarmUpCSharpierCommandCache()
	{
		if (_isWarmingUpCSharpierCommandCache || _cachedCSharpierCommand.IsValid)
			return;

		_isWarmingUpCSharpierCommandCache = true;
		DebugLogOperation("CSharpier Warm-up Started");

		try
		{
			string workingDirectory = GetProjectWorkingDirectory();
			CSharpierProbeResult probeResult = await Task.Run(() =>
				ProbeCSharpierCommand(CSharpierWarmUpTimeoutMilliseconds, workingDirectory)
			);

			if (probeResult.Success)
			{
				CacheCSharpierCommand(probeResult.Command, "warm-up");
				DebugLogOperation(
					"CSharpier Warm-up Completed",
					GetCSharpierCommandDisplayName(probeResult.Command)
				);
				return;
			}

			DebugLogOperation(
				"CSharpier Warm-up Failed",
				probeResult.TimedOut ? "probe timed out" : "command not found"
			);
		}
		finally
		{
			_isWarmingUpCSharpierCommandCache = false;
		}
	}

	private bool IsCSharpierInstalled()
	{
		return TryGetCSharpierCommand(out _);
	}

	private bool TryGetCSharpierCommand(
		out CSharpierCommand command,
		bool allowCachedCommand = true
	)
	{
		if (allowCachedCommand && _cachedCSharpierCommand.IsValid)
		{
			command = _cachedCSharpierCommand;
			DebugLogOperation(
				"CSharpier Command Cache Hit",
				GetCSharpierCommandDisplayName(command)
			);
			return true;
		}

		CSharpierProbeResult probeResult = ProbeCSharpierCommand(
			CSharpierDetectionTimeoutMilliseconds,
			GetProjectWorkingDirectory()
		);

		if (probeResult.Success)
		{
			command = probeResult.Command;
			CacheCSharpierCommand(command, "manual probe");
			return true;
		}

		DebugLogOperation(
			"CSharpier Detection Failed",
			probeResult.TimedOut ? "probe timed out" : "command not found"
		);

		command = default;
		return false;
	}

	private static CSharpierProbeResult ProbeCSharpierCommand(
		int timeoutMilliseconds,
		string workingDirectory
	)
	{
		bool timedOut = false;

		foreach (CSharpierCommand candidate in GetCSharpierCommandCandidates())
		{
			CSharpierProbeStatus status = CanExecuteCSharpierCommand(
				candidate.Executable,
				candidate.BaseArguments.Concat(new[] { "--version" }),
				timeoutMilliseconds,
				workingDirectory
			);

			if (status == CSharpierProbeStatus.Succeeded)
				return new CSharpierProbeResult(true, candidate, false);

			if (status == CSharpierProbeStatus.TimedOut)
				timedOut = true;
		}

		return new CSharpierProbeResult(false, default, timedOut);
	}

	private void CacheCSharpierCommand(CSharpierCommand command, string source)
	{
		if (!command.IsValid)
			return;

		_cachedCSharpierCommand = command;
		DebugLogOperation(
			"CSharpier Command Cached",
			$"{source}: {GetCSharpierCommandDisplayName(command)}"
		);
	}

	private bool IsCachedCSharpierCommand(CSharpierCommand command)
	{
		return _cachedCSharpierCommand.IsValid
			&& string.Equals(
				_cachedCSharpierCommand.Executable,
				command.Executable,
				StringComparison.OrdinalIgnoreCase
			)
			&& _cachedCSharpierCommand.BaseArguments.SequenceEqual(
				command.BaseArguments ?? Array.Empty<string>()
			);
	}

	private void ClearCachedCSharpierCommand(string reason)
	{
		if (!_cachedCSharpierCommand.IsValid)
			return;

		DebugLogOperation(
			"CSharpier Command Cache Cleared",
			$"{GetCSharpierCommandDisplayName(_cachedCSharpierCommand)} ({reason})"
		);

		_cachedCSharpierCommand = default;
	}

	private static string GetCSharpierCommandDisplayName(CSharpierCommand command)
	{
		if (!command.IsValid)
			return "<invalid>";

		string[] baseArguments = command.BaseArguments ?? Array.Empty<string>();

		return baseArguments.Length == 0
			? command.Executable
			: $"{command.Executable} {string.Join(" ", baseArguments)}";
	}

	private static IEnumerable<CSharpierCommand> GetCSharpierCommandCandidates()
	{
		yield return new CSharpierCommand("dotnet", "csharpier");
		yield return new CSharpierCommand("csharpier");

		string globalToolPath = GetGlobalCSharpierToolPath();

		if (!string.IsNullOrWhiteSpace(globalToolPath))
			yield return new CSharpierCommand(globalToolPath);
	}

	private async Task<CSharpierFormatResult> FormatScriptWithCSharpierUsingCachedCommandFallback(
		CSharpierCommand command,
		string scriptPath,
		string operationName
	)
	{
		bool usedCachedCommand = IsCachedCSharpierCommand(command);
		bool debugState = DebugState;
		CSharpierFormatResult formatResult = await Task.Run(() =>
			FormatScriptWithCSharpier(command, scriptPath, operationName, debugState)
		);

		if (
			formatResult.Success
			|| !formatResult.ShouldInvalidateCachedCommand
			|| !usedCachedCommand
		)
			return formatResult;

		ClearCachedCSharpierCommand("cached command failed during format");

		if (
			!TryGetCSharpierCommand(out CSharpierCommand fallbackCommand, allowCachedCommand: false)
		)
			return formatResult;

		DebugLogOperation(
			"CSharpier Command Retry",
			$"{GetCSharpierCommandDisplayName(command)} -> {GetCSharpierCommandDisplayName(fallbackCommand)}"
		);

		return await Task.Run(() =>
			FormatScriptWithCSharpier(fallbackCommand, scriptPath, operationName, debugState)
		);
	}

	private static CSharpierFormatResult FormatScriptWithCSharpier(
		CSharpierCommand command,
		string scriptPath,
		string operationName,
		bool debugState
	)
	{
		if (!command.IsValid)
			return new CSharpierFormatResult(
				false,
				"",
				"Beautify Script failed: CSharpier command is invalid.",
				shouldInvalidateCachedCommand: true
			);

		string globalScriptPath = ProjectSettings.GlobalizePath(scriptPath);

		if (string.IsNullOrWhiteSpace(globalScriptPath))
			return new CSharpierFormatResult(
				false,
				"",
				$"Beautify Script failed: could not resolve '{scriptPath}'."
			);

		string workingDirectory = GetProjectWorkingDirectory();
		DebugPrintBeautify(
			debugState,
			$"{operationName} CSharpier start: command='{GetCSharpierCommandDisplayName(command)}', scriptPath='{scriptPath}', globalPath='{globalScriptPath}', globalExists={System.IO.File.Exists(globalScriptPath)}, workingDirectory='{workingDirectory}'"
		);

		try
		{
			using Process process = new()
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = command.Executable,
					WorkingDirectory = workingDirectory,
					UseShellExecute = false,
					CreateNoWindow = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					StandardOutputEncoding = Encoding.UTF8,
					StandardErrorEncoding = Encoding.UTF8,
				},
			};

			foreach (string argument in command.BaseArguments)
				process.StartInfo.ArgumentList.Add(argument);

			process.StartInfo.ArgumentList.Add("format");
			process.StartInfo.ArgumentList.Add(globalScriptPath);
			process.StartInfo.ArgumentList.Add("--write-stdout");
			process.StartInfo.ArgumentList.Add("--log-level");
			process.StartInfo.ArgumentList.Add("None");

			DebugPrintBeautify(
				debugState,
				$"{operationName} CSharpier args: {GetDebugProcessArguments(process.StartInfo.ArgumentList)}"
			);

			if (!process.Start())
				return new CSharpierFormatResult(
					false,
					"",
					"Beautify Script failed: could not start CSharpier.",
					shouldInvalidateCachedCommand: true
				);

			Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
			Task<string> errorOutputTask = process.StandardError.ReadToEndAsync();

			if (!process.WaitForExit(CSharpierFormatTimeoutMilliseconds))
			{
				DebugPrintBeautify(
					debugState,
					$"{operationName} CSharpier timed out after {CSharpierFormatTimeoutMilliseconds} ms."
				);
				TryKillProcess(process);
				return new CSharpierFormatResult(
					false,
					"",
                    "Beautify Script failed: CSharpier timed out."
				);
			}

			string standardOutput = standardOutputTask.Result;
			string errorOutput = errorOutputTask.Result.Trim();

			DebugPrintBeautify(
				debugState,
				$"{operationName} CSharpier exit: exitCode={process.ExitCode}, stdoutLength={GetDebugLength(standardOutput)}, stderrLength={GetDebugLength(errorOutput)}, stdoutPreview='{GetDebugTextPreview(standardOutput)}', stderrPreview='{GetDebugTextPreview(errorOutput)}'"
			);

			if (process.ExitCode == 0)
				return new CSharpierFormatResult(true, standardOutput, "");

			string details = !string.IsNullOrWhiteSpace(errorOutput)
				? errorOutput
				: standardOutput.Trim();

			return new CSharpierFormatResult(
				false,
				"",
				$"Beautify Script failed: CSharpier could not format '{scriptPath}'.",
				shouldInvalidateCachedCommand: LooksLikeUnavailableCSharpierCommandDetails(details)
			);
		}
		catch (Exception exception)
		{
			DebugPrintBeautify(debugState, $"{operationName} CSharpier exception: {exception}");

			return new CSharpierFormatResult(
				false,
				"",
				"Beautify Script failed: CSharpier could not be started.",
				shouldInvalidateCachedCommand: true
			);
		}
	}

	private CSharpierInstallResult InstallCSharpierGlobalTool()
	{
		CSharpierInstallResult installResult = ExecuteCSharpierInstallCommand();

		if (!installResult.Success)
			return installResult;

		CSharpierCommand installedCommand = GetCSharpierCommandAfterSuccessfulGlobalInstall();

		return new CSharpierInstallResult(true, "CSharpier is now installed.", installedCommand);
	}

	private static CSharpierCommand GetCSharpierCommandAfterSuccessfulGlobalInstall()
	{
		return new CSharpierCommand("dotnet", "csharpier");
	}

	private static CSharpierInstallResult ExecuteCSharpierInstallCommand()
	{
		try
		{
			using Process process = new()
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = "dotnet",
					UseShellExecute = false,
					CreateNoWindow = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					StandardOutputEncoding = Encoding.UTF8,
					StandardErrorEncoding = Encoding.UTF8,
				},
			};

			process.StartInfo.ArgumentList.Add("tool");
			process.StartInfo.ArgumentList.Add("install");
			process.StartInfo.ArgumentList.Add("csharpier");
			process.StartInfo.ArgumentList.Add("-g");

			if (!process.Start())
				return new CSharpierInstallResult(
					false,
                    "Could not start dotnet to install CSharpier."
				);

			Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
			Task<string> errorOutputTask = process.StandardError.ReadToEndAsync();

			if (!process.WaitForExit(CSharpierInstallTimeoutMilliseconds))
			{
				TryKillProcess(process);
				return new CSharpierInstallResult(false, "CSharpier installation timed out.");
			}

			string errorOutput = errorOutputTask.Result.Trim();
			string standardOutput = standardOutputTask.Result.Trim();

			if (process.ExitCode == 0)
				return new CSharpierInstallResult(true, "CSharpier is now installed.");

			string details = !string.IsNullOrWhiteSpace(errorOutput) ? errorOutput : standardOutput;

			return new CSharpierInstallResult(
				false,
				string.IsNullOrWhiteSpace(details)
					? "CSharpier could not be installed. Make sure the .NET SDK is installed and try again."
					: $"CSharpier could not be installed:\n{TruncateDialogText(details)}"
			);
		}
		catch
		{
			return new CSharpierInstallResult(
				false,
                "CSharpier could not be installed. Make sure the .NET SDK is installed and try again."
			);
		}
	}

	private static CSharpierInstallResult ExecuteCSharpierUninstallCommandForDebug()
	{
		try
		{
			using Process process = new()
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = "dotnet",
					UseShellExecute = false,
					CreateNoWindow = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					StandardOutputEncoding = Encoding.UTF8,
					StandardErrorEncoding = Encoding.UTF8,
				},
			};

			process.StartInfo.ArgumentList.Add("tool");
			process.StartInfo.ArgumentList.Add("uninstall");
			process.StartInfo.ArgumentList.Add("csharpier");
			process.StartInfo.ArgumentList.Add("-g");

			if (!process.Start())
				return new CSharpierInstallResult(
					false,
                    "Could not start dotnet to uninstall CSharpier."
				);

			Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
			Task<string> errorOutputTask = process.StandardError.ReadToEndAsync();

			if (!process.WaitForExit(CSharpierInstallTimeoutMilliseconds))
			{
				TryKillProcess(process);
				return new CSharpierInstallResult(false, "CSharpier uninstall timed out.");
			}

			string errorOutput = errorOutputTask.Result.Trim();
			string standardOutput = standardOutputTask.Result.Trim();
			string details = !string.IsNullOrWhiteSpace(errorOutput) ? errorOutput : standardOutput;

			if (process.ExitCode == 0)
				return new CSharpierInstallResult(true, "CSharpier was uninstalled.");

			if (LooksLikeCSharpierAlreadyUninstalledDetails(details))
				return new CSharpierInstallResult(true, "CSharpier was already not installed.");

			return new CSharpierInstallResult(
				false,
				string.IsNullOrWhiteSpace(details)
					? "CSharpier could not be uninstalled."
					: $"CSharpier could not be uninstalled:\n{TruncateDialogText(details)}"
			);
		}
		catch
		{
			return new CSharpierInstallResult(
				false,
                "CSharpier could not be uninstalled. Make sure the .NET SDK is installed and try again."
			);
		}
	}

	private static bool LooksLikeCSharpierAlreadyUninstalledDetails(string details)
	{
		if (string.IsNullOrWhiteSpace(details))
			return false;

		string normalizedDetails = details.ToLowerInvariant();

		return normalizedDetails.Contains("not currently installed", StringComparison.Ordinal)
			|| normalizedDetails.Contains("is not installed", StringComparison.Ordinal)
			|| normalizedDetails.Contains(
				"package 'csharpier' is not found",
				StringComparison.Ordinal
			)
			|| normalizedDetails.Contains("tool 'csharpier'", StringComparison.Ordinal)
				&& normalizedDetails.Contains("not found", StringComparison.Ordinal);
	}

	private static bool LooksLikeUnavailableCSharpierCommandDetails(string details)
	{
		if (string.IsNullOrWhiteSpace(details))
			return false;

		string normalizedDetails = details.ToLowerInvariant();

		return normalizedDetails.Contains(
				"could not execute because the specified command or file was not found",
				StringComparison.Ordinal
			)
			|| normalizedDetails.Contains(
				"no executable found matching command",
				StringComparison.Ordinal
			)
			|| normalizedDetails.Contains("not recognized", StringComparison.Ordinal)
			|| (
				normalizedDetails.Contains("csharpier", StringComparison.Ordinal)
				&& (
					normalizedDetails.Contains("not found", StringComparison.Ordinal)
					|| normalizedDetails.Contains("not installed", StringComparison.Ordinal)
					|| normalizedDetails.Contains("does not exist", StringComparison.Ordinal)
				)
			);
	}

	private static CSharpierProbeStatus CanExecuteCSharpierCommand(
		string executable,
		IEnumerable<string> arguments,
		int timeoutMilliseconds,
		string workingDirectory
	)
	{
		if (string.IsNullOrWhiteSpace(executable))
			return CSharpierProbeStatus.Failed;

		try
		{
			using Process process = new()
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = executable,
					WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
						? System.Environment.CurrentDirectory
						: workingDirectory,
					UseShellExecute = false,
					CreateNoWindow = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					StandardOutputEncoding = Encoding.UTF8,
					StandardErrorEncoding = Encoding.UTF8,
				},
			};

			foreach (string argument in arguments ?? Array.Empty<string>())
				process.StartInfo.ArgumentList.Add(argument);

			if (!process.Start())
				return CSharpierProbeStatus.Failed;

			if (!process.WaitForExit(timeoutMilliseconds))
			{
				TryKillProcess(process);
				return CSharpierProbeStatus.TimedOut;
			}

			return process.ExitCode == 0
				? CSharpierProbeStatus.Succeeded
				: CSharpierProbeStatus.Failed;
		}
		catch
		{
			return CSharpierProbeStatus.Failed;
		}
	}

	private void SetCSharpierInstallButtonDisabled(bool disabled)
	{
		Button installButton = _csharpierNotInstalledDialog?.GetOkButton();

		if (installButton != null)
			installButton.Disabled = disabled;
	}

	private void DebugPrintBeautify(string message)
	{
		DebugPrintBeautify(DebugState, message);
	}

	private static void DebugPrintBeautify(bool debugState, string message)
	{
		if (!debugState)
			return;

		GD.Print($"System Explorer Beautify: {message}");
	}

	private static int GetDebugLength(string text)
	{
		return text?.Length ?? -1;
	}

	private static string GetDebugTextPreview(string text)
	{
		if (string.IsNullOrEmpty(text))
			return "";

		string normalizedText = text.Replace("\r", "\\r", StringComparison.Ordinal)
			.Replace("\n", "\\n", StringComparison.Ordinal)
			.Replace("\t", "\\t", StringComparison.Ordinal);

		return normalizedText.Length <= CSharpierDebugPreviewLength
			? normalizedText
			: normalizedText[..CSharpierDebugPreviewLength] + "...";
	}

	private static string GetDebugProcessArguments(
		System.Collections.ObjectModel.Collection<string> arguments
	)
	{
		if (arguments == null || arguments.Count == 0)
			return "<none>";

		return string.Join(" ", arguments.Select(GetDebugQuotedArgument));
	}

	private static string GetDebugQuotedArgument(string argument)
	{
		if (argument == null)
			return "<null>";

		return argument.Contains(' ', StringComparison.Ordinal) ? $"\"{argument}\"" : argument;
	}

	private static string TruncateDialogText(string text)
	{
		const int maximumLength = 1200;

		if (string.IsNullOrWhiteSpace(text) || text.Length <= maximumLength)
			return text;

		return text[..maximumLength] + "...";
	}

	private static string GetGlobalCSharpierToolPath()
	{
		string userProfilePath = System.Environment.GetFolderPath(
			System.Environment.SpecialFolder.UserProfile
		);

		if (string.IsNullOrWhiteSpace(userProfilePath))
			return string.Empty;

		string executableName = OperatingSystem.IsWindows() ? "csharpier.exe" : "csharpier";

		return IOPath.Combine(userProfilePath, ".dotnet", "tools", executableName);
	}

	private static string GetProjectWorkingDirectory()
	{
		string projectPath = ProjectSettings.GlobalizePath("res://");

		return string.IsNullOrWhiteSpace(projectPath)
			? System.Environment.CurrentDirectory
			: projectPath;
	}

	private static void TryKillProcess(Process process)
	{
		try
		{
			process.Kill(entireProcessTree: true);
		}
		catch
		{
			// Best effort cleanup only.
		}
	}
	#endregion
}
#endif
