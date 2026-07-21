#if TOOLS
using System;

namespace SystemExplorer.QuickActions.Beautify.CSharpier;

internal readonly struct CSharpierCommand
{
    internal CSharpierCommand(string executable, params string[] baseArguments)
    {
        Executable = executable;
        BaseArguments = baseArguments ?? Array.Empty<string>();
    }

    internal string Executable { get; }
    internal string[] BaseArguments { get; }
    internal bool IsValid => !string.IsNullOrWhiteSpace(Executable);
}
#endif
