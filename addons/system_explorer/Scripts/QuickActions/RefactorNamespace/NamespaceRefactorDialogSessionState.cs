#if TOOLS
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal enum NamespaceRefactorDialogMode
{
    None,
    SingleReplacement,
    SingleAdd,
    Batch,
}

internal sealed class NamespaceRefactorDialogSessionState
{
    private readonly List<string> _scriptPaths = new();
    private readonly List<string> _namespaces = new();
    private readonly ReadOnlyCollection<string> _readOnlyScriptPaths;
    private readonly ReadOnlyCollection<string> _readOnlyNamespaces;

    internal NamespaceRefactorDialogSessionState()
    {
        _readOnlyScriptPaths = _scriptPaths.AsReadOnly();
        _readOnlyNamespaces = _namespaces.AsReadOnly();
    }

    internal string Metadata { get; private set; } = "";

    internal NamespaceRefactorDialogMode Mode { get; private set; }

    internal IReadOnlyList<string> ScriptPaths => _readOnlyScriptPaths;

    internal IReadOnlyList<string> Namespaces => _readOnlyNamespaces;

    internal void BeginSingleReplacement(string metadata)
    {
        Metadata = metadata ?? "";
        Mode = NamespaceRefactorDialogMode.SingleReplacement;
    }

    internal void TransitionToSingleAdd(string scriptPath)
    {
        Mode = NamespaceRefactorDialogMode.SingleAdd;
        _scriptPaths.Clear();
        _scriptPaths.Add(scriptPath ?? "");
    }

    internal void BeginBatch(
        string metadata,
        IEnumerable<string> scriptPaths,
        IEnumerable<string> namespaces
    )
    {
        Metadata = metadata ?? "";
        Mode = NamespaceRefactorDialogMode.Batch;

        _scriptPaths.Clear();
        if (scriptPaths != null)
            _scriptPaths.AddRange(scriptPaths);

        _namespaces.Clear();
        if (namespaces != null)
            _namespaces.AddRange(namespaces);
    }

    internal void Clear()
    {
        Metadata = "";
        Mode = NamespaceRefactorDialogMode.None;
        _scriptPaths.Clear();
        _namespaces.Clear();
    }
}
#endif
