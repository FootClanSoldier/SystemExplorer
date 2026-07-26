#if TOOLS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SystemExplorer.EditorIntegration.Operations;
using IOPath = System.IO.Path;

namespace SystemExplorer.QuickActions.Beautify.CSharpier;

internal sealed class CSharpierCommandService
{
	private readonly Func<string> _workingDirectoryProvider;
	private readonly Action<string, string> _logOperation;
	private readonly int _detectionTimeoutMilliseconds;
	private readonly CSharpierProcessRunner _processRunner;
	private CSharpierCommand _cachedCommand;

	internal CSharpierCommandService(Func<string> workingDirectoryProvider, Action<string, string> logOperation, int detectionTimeoutMilliseconds, CSharpierProcessRunner processRunner)
	{
		_workingDirectoryProvider = workingDirectoryProvider ?? throw new ArgumentNullException(nameof(workingDirectoryProvider));
		_logOperation = logOperation ?? throw new ArgumentNullException(nameof(logOperation));
		if (detectionTimeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(detectionTimeoutMilliseconds));
		_detectionTimeoutMilliseconds = detectionTimeoutMilliseconds;
		_processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
	}

	internal bool HasCachedCommand => _cachedCommand.IsValid;
	internal bool TryGetCachedCommand(out CSharpierCommand command) { command = _cachedCommand; return command.IsValid; }

	internal async Task<CSharpierCommandProbeResult> ProbeCommandAsync(EditorOperationLease operation, int? timeoutMilliseconds = null)
	{
		bool timedOut = false;
		foreach (CSharpierCommand candidate in GetCommandCandidates())
		{
			operation.CancellationToken.ThrowIfCancellationRequested();
			ProcessStartInfo startInfo = CreateProbeStartInfo(candidate, _workingDirectoryProvider());
			CSharpierProcessResult result;
			try { result = await _processRunner.RunAsync(startInfo, timeoutMilliseconds ?? _detectionTimeoutMilliseconds, operation); }
			catch when (!operation.CancellationToken.IsCancellationRequested) { continue; }
			if (result.Success) return new CSharpierCommandProbeResult(true, candidate, false);
			if (result.TimedOut) timedOut = true;
		}
		return new CSharpierCommandProbeResult(false, default, timedOut);
	}

	internal void CacheCommand(CSharpierCommand command, string source)
	{
		if (!command.IsValid) return;
		_cachedCommand = command;
		_logOperation("CSharpier Command Cached", $"{source}: {GetCommandDisplayName(command)}");
	}

	internal bool IsCachedCommand(CSharpierCommand command) => _cachedCommand.IsValid && string.Equals(_cachedCommand.Executable, command.Executable, StringComparison.OrdinalIgnoreCase) && (_cachedCommand.BaseArguments ?? Array.Empty<string>()).SequenceEqual(command.BaseArguments ?? Array.Empty<string>());

	internal void ClearCachedCommand(string reason)
	{
		if (!_cachedCommand.IsValid) return;
		_logOperation("CSharpier Command Cache Cleared", $"{GetCommandDisplayName(_cachedCommand)} ({reason})");
		_cachedCommand = default;
	}

	internal static string GetCommandDisplayName(CSharpierCommand command)
	{
		if (!command.IsValid) return "<invalid>";
		string[] args = command.BaseArguments ?? Array.Empty<string>();
		return args.Length == 0 ? command.Executable : $"{command.Executable} {string.Join(" ", args)}";
	}

	private static ProcessStartInfo CreateProbeStartInfo(CSharpierCommand command, string workingDirectory)
	{
		ProcessStartInfo info = new()
		{
			FileName = command.Executable,
			WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
			UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
		};
		foreach (string arg in command.BaseArguments ?? Array.Empty<string>()) info.ArgumentList.Add(arg);
		info.ArgumentList.Add("--version");
		return info;
	}

	private static IEnumerable<CSharpierCommand> GetCommandCandidates()
	{
		yield return new CSharpierCommand("dotnet", "csharpier");
		yield return new CSharpierCommand("csharpier");
		string path = GetGlobalToolPath();
		if (!string.IsNullOrWhiteSpace(path)) yield return new CSharpierCommand(path);
	}

	private static string GetGlobalToolPath()
	{
		string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if (string.IsNullOrWhiteSpace(profile)) return string.Empty;
		return IOPath.Combine(profile, ".dotnet", "tools", OperatingSystem.IsWindows() ? "csharpier.exe" : "csharpier");
	}
}
#endif
