#if TOOLS
using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Godot;
using SystemExplorer.EditorIntegration.Operations;
using SystemExplorer.QuickActions.Beautify.CSharpier;

public partial class SystemExplorerPlugin
{
	#region Quick Actions - CSharpier
	private const int CSharpierDetectionTimeoutMilliseconds = 3000;
	private const int CSharpierWarmUpTimeoutMilliseconds = 10000;
	private const int CSharpierFormatTimeoutMilliseconds = 30000;
	private const int CSharpierInstallTimeoutMilliseconds = 120000;
	private const int CSharpierDebugPreviewLength = 500;
	private const bool DebugUninstallCSharpierOnStartup = false;

	private bool _isInstallingCSharpier;
	private bool _isDebugUninstallingCSharpier;
	private bool _isWarmingUpCSharpierCommandCache;
	private CSharpierCommandService _csharpierCommandService;
	private CSharpierProcessRunner _csharpierProcessRunner;
	private CSharpierProcessRunner CSharpierProcesses => _csharpierProcessRunner ??= new CSharpierProcessRunner();
	private CSharpierCommandService CSharpierCommands => _csharpierCommandService ??= new CSharpierCommandService(GetProjectWorkingDirectory, (operation, details) => DebugLogger.LogOperation(operation, details), CSharpierDetectionTimeoutMilliseconds, CSharpierProcesses);

	private readonly struct CSharpierInstallResult
	{
		public CSharpierInstallResult(bool success, string message, CSharpierCommand command = default) { Success = success; Message = message; Command = command; }
		public bool Success { get; }
		public string Message { get; }
		public CSharpierCommand Command { get; }
	}

	private readonly struct CSharpierFormatResult
	{
		public CSharpierFormatResult(bool success, string formattedText, string message, bool shouldInvalidateCachedCommand = false)
		{ Success = success; FormattedText = formattedText; Message = message; ShouldInvalidateCachedCommand = shouldInvalidateCachedCommand; }
		public bool Success { get; }
		public string FormattedText { get; }
		public string Message { get; }
		public bool ShouldInvalidateCachedCommand { get; }
	}

	private void OnCSharpierInstallConfirmed() => StartObservedEditorOperation("Install CSharpier", InstallCSharpierAsync);

	private async Task InstallCSharpierAsync(EditorOperationLease operation)
	{
		_isInstallingCSharpier = true;
		SetCSharpierInstallButtonDisabled(true);
		try
		{
			CSharpierInstallResult result = await InstallCSharpierGlobalToolAsync(operation);
			operation.CancellationToken.ThrowIfCancellationRequested();
			if (!IsEditorOperationAccessValid(operation)) return;
			if (!result.Success)
			{
				ClearPendingBeautifyAfterCSharpierInstall("CSharpier install failed");
				ShowCSharpierInstallResultDialog(result);
				return;
			}
			if (result.Command.IsValid && operation.IsCurrent) CSharpierCommands.CacheCommand(result.Command, "install");
			if (!IsEditorOperationAccessValid(operation)) return;
			if (await TryRunPendingBeautifyAfterCSharpierInstall(operation, result.Command)) return;
			if (IsEditorOperationAccessValid(operation)) ShowCSharpierInstallResultDialog(result);
		}
		finally
		{
			_isInstallingCSharpier = false;
			if (IsEditorOperationAccessValid(operation)) SetCSharpierInstallButtonDisabled(false);
		}
	}

	private void ShowCSharpierInstallResultDialog(CSharpierInstallResult result)
	{
		if (_csharpierInstallResultDialog == null) { DebugPrintBeautify($"CSharpier install result: success={result.Success}, message='{GetDebugTextPreview(result.Message)}'"); return; }
		_csharpierInstallResultDialog.Title = result.Success ? "CSharpier Installed" : "CSharpier Install Failed";
		_csharpierInstallResultDialog.DialogText = result.Message;
		_csharpierInstallResultDialog.PopupCentered();
	}

	private void StartCSharpierStartupWarmUp()
	{
		if (_editorOperationShutdownStarted) return;
		if (DebugState && DebugUninstallCSharpierOnStartup) CallDeferred(nameof(DebugUninstallCSharpierOnStartupThenWarmUp));
		else CallDeferred(nameof(WarmUpCSharpierCommandCache));
	}

	private void DebugUninstallCSharpierOnStartupThenWarmUp() => StartObservedEditorOperation("CSharpier Startup Debug Uninstall", DebugUninstallCSharpierOnStartupThenWarmUpAsync, backgroundOperation: true);

	private async Task DebugUninstallCSharpierOnStartupThenWarmUpAsync(EditorOperationLease operation)
	{
		_isDebugUninstallingCSharpier = true;
		CSharpierCommands.ClearCachedCommand("startup debug uninstall started");
		try
		{
			CSharpierInstallResult result = await ExecuteCSharpierUninstallCommandForDebugAsync(operation);
			operation.CancellationToken.ThrowIfCancellationRequested();
			if (IsEditorOperationAccessValid(operation)) DebugPrintBeautify($"Startup debug uninstall of CSharpier finished: success={result.Success}, message='{GetDebugTextPreview(result.Message)}'");
			if (operation.IsCurrent) CSharpierCommands.ClearCachedCommand("startup debug uninstall finished");
			await WarmUpCSharpierCommandCacheAsync(operation);
		}
		finally { _isDebugUninstallingCSharpier = false; }
	}

	private void WarmUpCSharpierCommandCache() => StartObservedEditorOperation("CSharpier Warm-up", WarmUpCSharpierCommandCacheAsync, backgroundOperation: true);

	private async Task WarmUpCSharpierCommandCacheAsync(EditorOperationLease operation)
	{
		if (CSharpierCommands.HasCachedCommand) return;
		_isWarmingUpCSharpierCommandCache = true;
		try
		{
			CSharpierCommandProbeResult probe = await CSharpierCommands.ProbeCommandAsync(operation, CSharpierWarmUpTimeoutMilliseconds);
			operation.CancellationToken.ThrowIfCancellationRequested();
			if (!operation.IsCurrent) return;
			if (probe.Success)
			{
				CSharpierCommands.CacheCommand(probe.Command, "warm-up");
				if (IsEditorOperationAccessValid(operation)) DebugLogger.LogOperation("CSharpier Warm-up Completed", CSharpierCommandService.GetCommandDisplayName(probe.Command));
			}
			else if (IsEditorOperationAccessValid(operation)) DebugLogger.LogOperation("CSharpier Warm-up Failed", probe.TimedOut ? "probe timed out" : "command not found");
		}
		finally { _isWarmingUpCSharpierCommandCache = false; }
	}

	private async Task<CSharpierCommand> GetCSharpierCommandAsync(EditorOperationLease operation)
	{
		if (CSharpierCommands.TryGetCachedCommand(out CSharpierCommand cached)) return cached;
		CSharpierCommandProbeResult probe = await CSharpierCommands.ProbeCommandAsync(operation);
		operation.CancellationToken.ThrowIfCancellationRequested();
		if (probe.Success && operation.IsCurrent) { CSharpierCommands.CacheCommand(probe.Command, "operation probe"); return probe.Command; }
		return default;
	}

	private async Task<CSharpierFormatResult> FormatScriptWithCSharpierUsingCachedCommandFallback(EditorOperationLease operation, CSharpierCommand command, string scriptPath, string operationName)
	{
		bool usedCached = CSharpierCommands.IsCachedCommand(command);
		CSharpierFormatResult result = await FormatScriptWithCSharpierAsync(operation, command, scriptPath, operationName, DebugState);
		operation.CancellationToken.ThrowIfCancellationRequested();
		if (result.Success || !result.ShouldInvalidateCachedCommand || !usedCached || !operation.IsCurrent) return result;
		CSharpierCommands.ClearCachedCommand("cached command failed during format");
		CSharpierCommandProbeResult probe = await CSharpierCommands.ProbeCommandAsync(operation);
		operation.CancellationToken.ThrowIfCancellationRequested();
		if (!probe.Success || !operation.IsCurrent) return result;
		CSharpierCommands.CacheCommand(probe.Command, "format fallback probe");
		if (IsEditorOperationAccessValid(operation)) DebugLogger.LogOperation("CSharpier Command Retry", $"{CSharpierCommandService.GetCommandDisplayName(command)} -> {CSharpierCommandService.GetCommandDisplayName(probe.Command)}");
		return await FormatScriptWithCSharpierAsync(operation, probe.Command, scriptPath, operationName, DebugState);
	}

	private async Task<CSharpierFormatResult> FormatScriptWithCSharpierAsync(EditorOperationLease operation, CSharpierCommand command, string scriptPath, string operationName, bool debugState)
	{
		if (!command.IsValid) return new CSharpierFormatResult(false, "", "Beautify Script failed: CSharpier command is invalid.", true);
		string globalPath = ProjectSettings.GlobalizePath(scriptPath);
		if (string.IsNullOrWhiteSpace(globalPath)) return new CSharpierFormatResult(false, "", $"Beautify Script failed: could not resolve '{scriptPath}'.");
		ProcessStartInfo info = CreateProcessStartInfo(command.Executable, GetProjectWorkingDirectory());
		foreach (string arg in command.BaseArguments) info.ArgumentList.Add(arg);
		info.ArgumentList.Add("format"); info.ArgumentList.Add(globalPath); info.ArgumentList.Add("--write-stdout"); info.ArgumentList.Add("--log-level"); info.ArgumentList.Add("None");
		try
		{
			CSharpierProcessResult process = await CSharpierProcesses.RunAsync(info, CSharpierFormatTimeoutMilliseconds, operation);
			if (process.TimedOut) return new CSharpierFormatResult(false, "", "Beautify Script failed: CSharpier timed out.");
			if (!process.Started) return new CSharpierFormatResult(false, "", "Beautify Script failed: could not start CSharpier.", true);
			if (process.ExitCode == 0) return new CSharpierFormatResult(true, process.StandardOutput, "");
			string details = !string.IsNullOrWhiteSpace(process.ErrorOutput) ? process.ErrorOutput.Trim() : process.StandardOutput.Trim();
			return new CSharpierFormatResult(false, "", $"Beautify Script failed: CSharpier could not format '{scriptPath}'.", LooksLikeUnavailableCSharpierCommandDetails(details));
		}
		catch (OperationCanceledException) { throw; }
		catch (Exception exception) { DebugPrintBeautify(debugState, $"{operationName} CSharpier exception: {exception}"); return new CSharpierFormatResult(false, "", "Beautify Script failed: CSharpier could not be started.", true); }
	}

	private async Task<CSharpierInstallResult> InstallCSharpierGlobalToolAsync(EditorOperationLease operation)
	{
		CSharpierInstallResult result = await ExecuteToolCommandAsync(operation, "install");
		return result.Success ? new CSharpierInstallResult(true, "CSharpier is now installed.", new CSharpierCommand("dotnet", "csharpier")) : result;
	}

	private Task<CSharpierInstallResult> ExecuteCSharpierUninstallCommandForDebugAsync(EditorOperationLease operation) => ExecuteToolCommandAsync(operation, "uninstall");

	private async Task<CSharpierInstallResult> ExecuteToolCommandAsync(EditorOperationLease operation, string verb)
	{
		ProcessStartInfo info = CreateProcessStartInfo("dotnet", GetProjectWorkingDirectory());
		info.ArgumentList.Add("tool"); info.ArgumentList.Add(verb); info.ArgumentList.Add("csharpier"); info.ArgumentList.Add("-g");
		try
		{
			CSharpierProcessResult process = await CSharpierProcesses.RunAsync(info, CSharpierInstallTimeoutMilliseconds, operation);
			if (process.TimedOut) return new CSharpierInstallResult(false, $"CSharpier {verb} timed out.");
			if (!process.Started) return new CSharpierInstallResult(false, $"Could not start dotnet to {verb} CSharpier.");
			string details = !string.IsNullOrWhiteSpace(process.ErrorOutput) ? process.ErrorOutput.Trim() : process.StandardOutput.Trim();
			if (process.ExitCode == 0) return new CSharpierInstallResult(true, verb == "install" ? "CSharpier is now installed." : "CSharpier was uninstalled.");
			if (verb == "uninstall" && LooksLikeCSharpierAlreadyUninstalledDetails(details)) return new CSharpierInstallResult(true, "CSharpier was already not installed.");
			string action = verb == "install" ? "installed" : "uninstalled";
			return new CSharpierInstallResult(false, string.IsNullOrWhiteSpace(details) ? $"CSharpier could not be {action}." : $"CSharpier could not be {action}:\n{TruncateDialogText(details)}");
		}
		catch (OperationCanceledException) { throw; }
		catch { return new CSharpierInstallResult(false, $"CSharpier could not be {verb}ed. Make sure the .NET SDK is installed and try again."); }
	}

	private static ProcessStartInfo CreateProcessStartInfo(string executable, string workingDirectory) => new()
	{
		FileName = executable, WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? System.Environment.CurrentDirectory : workingDirectory,
		UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
		StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
	};

	private static bool LooksLikeCSharpierAlreadyUninstalledDetails(string details)
	{
		if (string.IsNullOrWhiteSpace(details)) return false;
		string d = details.ToLowerInvariant();
		return d.Contains("not currently installed", StringComparison.Ordinal) || d.Contains("is not installed", StringComparison.Ordinal) || d.Contains("package 'csharpier' is not found", StringComparison.Ordinal) || (d.Contains("tool 'csharpier'", StringComparison.Ordinal) && d.Contains("not found", StringComparison.Ordinal));
	}

	private static bool LooksLikeUnavailableCSharpierCommandDetails(string details)
	{
		if (string.IsNullOrWhiteSpace(details)) return false;
		string d = details.ToLowerInvariant();
		return d.Contains("could not execute because the specified command or file was not found", StringComparison.Ordinal) || d.Contains("no executable found matching command", StringComparison.Ordinal) || d.Contains("not recognized", StringComparison.Ordinal) || (d.Contains("csharpier", StringComparison.Ordinal) && (d.Contains("not found", StringComparison.Ordinal) || d.Contains("not installed", StringComparison.Ordinal) || d.Contains("does not exist", StringComparison.Ordinal)));
	}

	private void SetCSharpierInstallButtonDisabled(bool disabled) { Button button = _csharpierNotInstalledDialog?.GetOkButton(); if (button != null) button.Disabled = disabled; }
	private void DebugPrintBeautify(string message) => DebugPrintBeautify(DebugState, message);
	private static void DebugPrintBeautify(bool debugState, string message) { if (debugState) GD.Print($"System Explorer Beautify: {message}"); }
	private static int GetDebugLength(string text) => text?.Length ?? -1;
	private static string GetDebugTextPreview(string text)
	{
		if (string.IsNullOrEmpty(text)) return "";
		string normalized = text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal);
		return normalized.Length <= CSharpierDebugPreviewLength ? normalized : normalized[..CSharpierDebugPreviewLength] + "...";
	}
	private static string TruncateDialogText(string text) { const int max = 1200; return string.IsNullOrWhiteSpace(text) || text.Length <= max ? text : text[..max] + "..."; }
	private static string GetProjectWorkingDirectory() { string path = ProjectSettings.GlobalizePath("res://"); return string.IsNullOrWhiteSpace(path) ? System.Environment.CurrentDirectory : path; }
	#endregion
}
#endif
