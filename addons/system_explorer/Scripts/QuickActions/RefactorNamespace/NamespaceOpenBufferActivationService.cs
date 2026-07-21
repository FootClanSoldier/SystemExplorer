#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal sealed class NamespaceOpenBufferActivationService
{
    private readonly ScriptEditorBufferActivationService _activationService;

    internal NamespaceOpenBufferActivationService(
        ScriptEditorBufferActivationService activationService
    )
    {
        _activationService =
            activationService ?? throw new ArgumentNullException(nameof(activationService));
    }

    internal bool TryGetOpenScriptEditorsByActivatingPaths(
        EditorInterface editorInterface,
        ScriptEditor scriptEditor,
        IEnumerable<string> scriptPaths,
        bool failIfOpenEditorCannotBeMatched,
        Action<string> debugLog,
        out Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath,
        out string failureMessage
    )
    {
        openEditorsByPath = new Dictionary<string, OpenScriptEditorBuffer>(
            StringComparer.OrdinalIgnoreCase
        );
        failureMessage = "";

        if (scriptEditor == null || scriptPaths == null)
            return true;

        foreach (
            string scriptPath in scriptPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(ScriptPathUtility.Normalize)
                .Distinct(StringComparer.OrdinalIgnoreCase)
        )
        {
            if (!IsScriptOpen(scriptEditor, scriptPath))
                continue;

            if (
                TryGetOpenScriptEditorByActivatingPath(
                    editorInterface,
                    editorInterface?.GetScriptEditor(),
                    scriptPath,
                    debugLog,
                    out OpenScriptEditorBuffer openEditor,
                    out string editorFailureMessage
                )
            )
            {
                openEditorsByPath[scriptPath] = openEditor;
                continue;
            }

            if (failIfOpenEditorCannotBeMatched)
            {
                failureMessage = editorFailureMessage;
                return false;
            }

            debugLog?.Invoke(
                $"Refactor Namespace could not match open editor for '{scriptPath}': {editorFailureMessage}"
            );
        }

        return true;
    }

    internal bool TryGetOpenScriptEditorByActivatingPath(
        EditorInterface editorInterface,
        ScriptEditor scriptEditor,
        string scriptPath,
        Action<string> debugLog,
        out OpenScriptEditorBuffer openEditor,
        out string failureMessage
    )
    {
        ScriptEditorBufferActivationResult activationResult =
            _activationService.TryActivateOpenBuffer(editorInterface, scriptEditor, scriptPath);

        openEditor = activationResult.OpenEditor;
        failureMessage = "";

        if (activationResult.ScriptWasOpen)
        {
            debugLog?.Invoke(
                $"Refactor Namespace activate '{activationResult.NormalizedScriptPath}': current='{activationResult.CurrentScriptPath}', currentEditorId={GetGodotInstanceId(activationResult.CurrentEditor)}, baseType='{activationResult.BaseEditor?.GetType().Name ?? "<null>"}', baseId={GetGodotInstanceId(activationResult.BaseEditor)}, matches={activationResult.ActivatedScriptPathMatched}"
            );
        }

        if (activationResult.Success)
            return true;

        switch (activationResult.Failure)
        {
            case ScriptEditorBufferActivationFailure.ScriptEditorUnavailable:
            case ScriptEditorBufferActivationFailure.EditorInterfaceUnavailable:
            case ScriptEditorBufferActivationFailure.ScriptNotOpen:
                return true;
            case ScriptEditorBufferActivationFailure.InvalidScriptPath:
                failureMessage =
                    "Refactor Namespace cancelled: an empty script path could not be matched to an open editor buffer.";
                return false;
            case ScriptEditorBufferActivationFailure.ActivatedScriptPathMismatch:
                failureMessage =
                    $"Refactor Namespace cancelled: System Explorer could not safely activate the open editor buffer for '{activationResult.NormalizedScriptPath}' before refactoring.";
                return false;
            case ScriptEditorBufferActivationFailure.ActiveEditorUnavailable:
            case ScriptEditorBufferActivationFailure.ActiveEditorIsNotTextEdit:
                failureMessage =
                    $"Refactor Namespace cancelled: System Explorer could not access the open text editor buffer for '{activationResult.NormalizedScriptPath}' before refactoring.";
                return false;
            default:
                return true;
        }
    }

    internal bool IsScriptOpen(ScriptEditor scriptEditor, string scriptPath)
    {
        return _activationService.IsScriptOpen(scriptEditor, scriptPath);
    }

    private static string GetGodotInstanceId(GodotObject godotObject)
    {
        return godotObject == null ? "<null>" : godotObject.GetInstanceId().ToString();
    }
}
#endif
