#if TOOLS
using System.Diagnostics;

namespace SystemExplorer.QuickActions.Beautify.CSharpier;

internal static class CSharpierProcessUtility
{
    internal static void TryKillProcess(Process process)
    {
        if (process == null)
            return;

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort cleanup only.
        }
    }
}
#endif
