#if TOOLS
namespace SystemExplorer.QuickActions.Beautify.CSharpier;

internal readonly struct CSharpierCommandProbeResult
{
    internal CSharpierCommandProbeResult(
        bool success,
        CSharpierCommand command,
        bool timedOut
    )
    {
        Success = success;
        Command = command;
        TimedOut = timedOut;
    }

    internal bool Success { get; }
    internal CSharpierCommand Command { get; }
    internal bool TimedOut { get; }
}
#endif
