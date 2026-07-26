#if TOOLS
namespace SystemExplorer.QuickActions.Beautify.CSharpier;

internal readonly struct CSharpierProcessResult
{
	internal CSharpierProcessResult(bool started, bool timedOut, int exitCode, string standardOutput, string errorOutput)
	{
		Started = started;
		TimedOut = timedOut;
		ExitCode = exitCode;
		StandardOutput = standardOutput ?? "";
		ErrorOutput = errorOutput ?? "";
	}

	internal bool Started { get; }
	internal bool TimedOut { get; }
	internal int ExitCode { get; }
	internal string StandardOutput { get; }
	internal string ErrorOutput { get; }
	internal bool Success => Started && !TimedOut && ExitCode == 0;
}
#endif
