#if TOOLS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using IOPath = System.IO.Path;

namespace SystemExplorer.QuickActions.Beautify.CSharpier;

internal sealed class CSharpierCommandService
{
    private readonly Func<string> _workingDirectoryProvider;
    private readonly Action<string, string> _logOperation;
    private readonly int _detectionTimeoutMilliseconds;
    private CSharpierCommand _cachedCommand;

    private enum CSharpierProbeStatus
    {
        Failed,
        Succeeded,
        TimedOut,
    }

    internal CSharpierCommandService(
        Func<string> workingDirectoryProvider,
        Action<string, string> logOperation,
        int detectionTimeoutMilliseconds
    )
    {
        _workingDirectoryProvider = workingDirectoryProvider
            ?? throw new ArgumentNullException(nameof(workingDirectoryProvider));
        _logOperation = logOperation ?? throw new ArgumentNullException(nameof(logOperation));

        if (detectionTimeoutMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(detectionTimeoutMilliseconds));

        _detectionTimeoutMilliseconds = detectionTimeoutMilliseconds;
    }

    internal bool HasCachedCommand => _cachedCommand.IsValid;

    internal bool TryGetCommand(
        out CSharpierCommand command,
        bool allowCachedCommand = true
    )
    {
        if (allowCachedCommand && _cachedCommand.IsValid)
        {
            command = _cachedCommand;
            _logOperation("CSharpier Command Cache Hit", GetCommandDisplayName(command));
            return true;
        }

        CSharpierCommandProbeResult probeResult = ProbeCommand(
            _detectionTimeoutMilliseconds,
            _workingDirectoryProvider()
        );

        if (probeResult.Success)
        {
            command = probeResult.Command;
            CacheCommand(command, "manual probe");
            return true;
        }

        _logOperation(
            "CSharpier Detection Failed",
            probeResult.TimedOut ? "probe timed out" : "command not found"
        );

        command = default;
        return false;
    }

    internal CSharpierCommandProbeResult ProbeCommand(
        int timeoutMilliseconds,
        string workingDirectory
    )
    {
        bool timedOut = false;

        foreach (CSharpierCommand candidate in GetCommandCandidates())
        {
            CSharpierProbeStatus status = CanExecuteCommand(
                candidate.Executable,
                candidate.BaseArguments.Concat(new[] { "--version" }),
                timeoutMilliseconds,
                workingDirectory
            );

            if (status == CSharpierProbeStatus.Succeeded)
                return new CSharpierCommandProbeResult(true, candidate, false);

            if (status == CSharpierProbeStatus.TimedOut)
                timedOut = true;
        }

        return new CSharpierCommandProbeResult(false, default, timedOut);
    }

    internal void CacheCommand(CSharpierCommand command, string source)
    {
        if (!command.IsValid)
            return;

        _cachedCommand = command;
        _logOperation(
            "CSharpier Command Cached",
            $"{source}: {GetCommandDisplayName(command)}"
        );
    }

    internal bool IsCachedCommand(CSharpierCommand command)
    {
        return _cachedCommand.IsValid
            && string.Equals(
                _cachedCommand.Executable,
                command.Executable,
                StringComparison.OrdinalIgnoreCase
            )
            && (_cachedCommand.BaseArguments ?? Array.Empty<string>()).SequenceEqual(
                command.BaseArguments ?? Array.Empty<string>()
            );
    }

    internal void ClearCachedCommand(string reason)
    {
        if (!_cachedCommand.IsValid)
            return;

        _logOperation(
            "CSharpier Command Cache Cleared",
            $"{GetCommandDisplayName(_cachedCommand)} ({reason})"
        );

        _cachedCommand = default;
    }

    internal static string GetCommandDisplayName(CSharpierCommand command)
    {
        if (!command.IsValid)
            return "<invalid>";

        string[] baseArguments = command.BaseArguments ?? Array.Empty<string>();

        return baseArguments.Length == 0
            ? command.Executable
            : $"{command.Executable} {string.Join(" ", baseArguments)}";
    }

    private static IEnumerable<CSharpierCommand> GetCommandCandidates()
    {
        yield return new CSharpierCommand("dotnet", "csharpier");
        yield return new CSharpierCommand("csharpier");

        string globalToolPath = GetGlobalToolPath();

        if (!string.IsNullOrWhiteSpace(globalToolPath))
            yield return new CSharpierCommand(globalToolPath);
    }

    private static CSharpierProbeStatus CanExecuteCommand(
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
                        ? Environment.CurrentDirectory
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
                CSharpierProcessUtility.TryKillProcess(process);
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

    private static string GetGlobalToolPath()
    {
        string userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (string.IsNullOrWhiteSpace(userProfilePath))
            return string.Empty;

        string executableName = OperatingSystem.IsWindows() ? "csharpier.exe" : "csharpier";

        return IOPath.Combine(userProfilePath, ".dotnet", "tools", executableName);
    }
}
#endif
