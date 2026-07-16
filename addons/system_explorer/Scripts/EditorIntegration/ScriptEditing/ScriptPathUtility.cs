#if TOOLS
namespace SystemExplorer.EditorIntegration.ScriptEditing;

internal static class ScriptPathUtility
{
    internal static string Normalize(string path)
    {
        return path?.Trim().Replace('\\', '/') ?? "";
    }
}
#endif
