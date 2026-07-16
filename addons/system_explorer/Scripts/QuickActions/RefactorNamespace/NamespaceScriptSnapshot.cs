#if TOOLS
namespace SystemExplorer.QuickActions.RefactorNamespace;

internal sealed class NamespaceScriptSnapshot
{
    internal string Path { get; }
    internal string Text { get; }

    internal NamespaceScriptSnapshot(string path, string text)
    {
        Path = path ?? "";
        Text = text ?? "";
    }
}
#endif
