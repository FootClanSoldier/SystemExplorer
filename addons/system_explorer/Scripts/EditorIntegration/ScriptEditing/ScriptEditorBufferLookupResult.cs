#if TOOLS
using System;
using System.Collections.Generic;

namespace SystemExplorer.EditorIntegration.ScriptEditing;

internal enum ScriptEditorBufferLookupFailure
{
    None,
    RequiredEditorUnavailable,
    RequiredEditorAlreadyUsed,
    DuplicateEditorForPath,
    UnsafeIndexedPair,
    SavedEditorDiskMismatch,
    UnmatchedRequiredOpenScripts,
}

internal sealed class ScriptEditorBufferLookupResult
{
    internal Dictionary<string, OpenScriptEditorBuffer> OpenEditorsByPath { get; }
    internal IReadOnlyList<string> UnsafeOpenScriptPaths { get; }
    internal IReadOnlyList<string> UnmatchedRequiredPaths { get; }
    internal ScriptEditorBufferLookupFailure Failure { get; }
    internal string FailurePath { get; }
    internal bool Success => Failure == ScriptEditorBufferLookupFailure.None;

    internal ScriptEditorBufferLookupResult(
        Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath,
        ScriptEditorBufferLookupFailure failure = ScriptEditorBufferLookupFailure.None,
        string failurePath = "",
        IEnumerable<string> unsafeOpenScriptPaths = null,
        IEnumerable<string> unmatchedRequiredPaths = null
    )
    {
        OpenEditorsByPath =
            openEditorsByPath
            ?? new Dictionary<string, OpenScriptEditorBuffer>(StringComparer.OrdinalIgnoreCase);
        UnsafeOpenScriptPaths = CreateReadOnlyList(unsafeOpenScriptPaths);
        UnmatchedRequiredPaths = CreateReadOnlyList(unmatchedRequiredPaths);
        Failure = failure;
        FailurePath = failurePath ?? "";
    }

    private static IReadOnlyList<string> CreateReadOnlyList(IEnumerable<string> source)
    {
        List<string> copy = source == null ? new List<string>() : new List<string>(source);
        return copy.AsReadOnly();
    }
}
#endif
