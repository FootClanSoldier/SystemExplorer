#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal sealed class NamespaceOpenBufferLookupService
{
    private readonly ScriptEditorBufferLocator _bufferLocator;

    internal NamespaceOpenBufferLookupService(ScriptEditorBufferLocator bufferLocator)
    {
        _bufferLocator = bufferLocator ?? throw new ArgumentNullException(nameof(bufferLocator));
    }

    internal bool TryGetOpenScriptEditorsByIndexedScriptEditorPaths(
        ScriptEditor scriptEditor,
        HashSet<string> targetPaths,
        out Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath,
        out string failureMessage,
        HashSet<string> requiredPaths = null
    )
    {
        ScriptEditorBufferLookupResult lookupResult =
            _bufferLocator.LocateByIndexedScriptEditorPathsWithoutActivation(
                scriptEditor,
                targetPaths,
                requiredPaths
            );

        openEditorsByPath = lookupResult.OpenEditorsByPath;
        failureMessage = BuildScriptEditorBufferLookupFailureMessage(lookupResult);
        return lookupResult.Success;
    }

    private static string BuildScriptEditorBufferLookupFailureMessage(
        ScriptEditorBufferLookupResult lookupResult
    )
    {
        if (lookupResult == null)
            return "";

        string scriptPath = lookupResult.FailurePath;

        return lookupResult.Failure switch
        {
            ScriptEditorBufferLookupFailure.RequiredEditorUnavailable =>
                $"Refactor Namespace cancelled: System Explorer could not access the open script editor buffer for '{scriptPath}' before scanning namespace usages.",
            ScriptEditorBufferLookupFailure.RequiredEditorAlreadyUsed =>
                $"Refactor Namespace cancelled: System Explorer found the same open script editor buffer for more than one script while preparing namespace refactor '{scriptPath}'.",
            ScriptEditorBufferLookupFailure.DuplicateEditorForPath =>
                $"Refactor Namespace cancelled: System Explorer found duplicate open script editor buffers for '{scriptPath}'. Save/reopen it before refactoring.",
            ScriptEditorBufferLookupFailure.UnsafeIndexedPair =>
                "Refactor Namespace cancelled: an open script editor buffer could not be matched safely before scanning namespace usages.",
            ScriptEditorBufferLookupFailure.SavedEditorDiskMismatch =>
                $"Refactor Namespace cancelled: the open editor buffer for '{scriptPath}' does not match the file on disk before scanning namespace usages. Save/reopen it before refactoring.",
            ScriptEditorBufferLookupFailure.UnmatchedRequiredOpenScripts =>
                $"Refactor Namespace cancelled: System Explorer could not safely match required open script editor buffer(s) without changing the active editor tab. Save/reopen before refactoring:\n{string.Join("\n", lookupResult.UnmatchedRequiredPaths)}",
            _ => "",
        };
    }
}
#endif
