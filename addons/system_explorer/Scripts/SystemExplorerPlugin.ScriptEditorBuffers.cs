#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using SystemExplorer.EditorIntegration.ScriptEditing;

public partial class SystemExplorerPlugin
{
    #region Shared Script Editor Buffers

    private static readonly ScriptEditorBufferLocator OpenScriptEditorBufferLocator = new(
        ScriptPathUtility.Normalize,
        ScriptTextFileService.ReadText,
        ScriptTextFileService.TextsMatchForDiskVerification,
        FileAccess.FileExists
    );

    private static readonly ScriptEditorBufferAutosaveService OpenScriptEditorBufferAutosaveService =
        new(
            ScriptTextFileService.ReadText,
            ScriptTextFileService.WriteText,
            ScriptTextFileService.TextsMatchForDiskVerification
        );

    private static string ReadTextFile(string path)
    {
        return ScriptTextFileService.ReadText(path);
    }

    private static bool WriteTextFile(string path, string text)
    {
        return ScriptTextFileService.WriteText(path, text);
    }

    private bool TryGetOpenScriptEditorsByActivatingPaths(
        IEnumerable<string> scriptPaths,
        bool failIfOpenEditorCannotBeMatched,
        out Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath,
        out string failureMessage
    )
    {
        openEditorsByPath = new Dictionary<string, OpenScriptEditorBuffer>(
            StringComparer.OrdinalIgnoreCase
        );
        failureMessage = "";

        ScriptEditor scriptEditor = EditorInterface.Singleton?.GetScriptEditor();

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
                    scriptPath,
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

            DebugLog(
                $"Refactor Namespace could not match open editor for '{scriptPath}': {editorFailureMessage}"
            );
        }

        return true;
    }

    private bool TryGetOpenScriptEditorByActivatingPath(
        string scriptPath,
        out OpenScriptEditorBuffer openEditor,
        out string failureMessage
    )
    {
        openEditor = default;
        failureMessage = "";

        string normalizedPath = ScriptPathUtility.Normalize(scriptPath);

        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            failureMessage =
                "Refactor Namespace cancelled: an empty script path could not be matched to an open editor buffer.";
            return false;
        }

        ScriptEditor scriptEditor = EditorInterface.Singleton?.GetScriptEditor();
        EditorInterface editorInterface = EditorInterface.Singleton;

        if (scriptEditor == null || editorInterface == null)
            return true;

        if (!TryGetOpenScript(scriptEditor, normalizedPath, out Script openScript))
            return true;

        editorInterface.EditScript(openScript);

        Script currentScript = scriptEditor.GetCurrentScript();
        string currentScriptPath = ScriptPathUtility.Normalize(currentScript?.ResourcePath);
        ScriptEditorBase currentEditor = scriptEditor.GetCurrentEditor();
        Control baseEditor = currentEditor?.GetBaseEditor();

        DebugLog(
            $"Refactor Namespace activate '{normalizedPath}': current='{currentScriptPath}', currentEditorId={GetGodotInstanceId(currentEditor)}, baseType='{baseEditor?.GetType().Name ?? "<null>"}', baseId={GetGodotInstanceId(baseEditor)}, matches={string.Equals(currentScriptPath, normalizedPath, StringComparison.OrdinalIgnoreCase)}"
        );

        if (!string.Equals(currentScriptPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            failureMessage =
                $"Refactor Namespace cancelled: System Explorer could not safely activate the open editor buffer for '{normalizedPath}' before refactoring.";
            return false;
        }

        if (baseEditor is not TextEdit textEditor)
        {
            failureMessage =
                $"Refactor Namespace cancelled: System Explorer could not access the open text editor buffer for '{normalizedPath}' before refactoring.";
            return false;
        }

        openEditor = new OpenScriptEditorBuffer(normalizedPath, textEditor);
        return true;
    }

    private static bool TryGetOpenScript(
        ScriptEditor scriptEditor,
        string scriptPath,
        out Script openScript
    )
    {
        openScript = null;

        if (scriptEditor == null || string.IsNullOrWhiteSpace(scriptPath))
            return false;

        foreach (Script candidateScript in scriptEditor.GetOpenScripts())
        {
            if (candidateScript == null)
                continue;

            string candidatePath = ScriptPathUtility.Normalize(candidateScript.ResourcePath);

            if (!string.Equals(candidatePath, scriptPath, StringComparison.OrdinalIgnoreCase))
                continue;

            openScript = candidateScript;
            return true;
        }

        return false;
    }

    private static bool IsScriptOpen(ScriptEditor scriptEditor, string scriptPath)
    {
        return TryGetOpenScript(scriptEditor, scriptPath, out _);
    }

    private static bool TryAutosaveOpenScriptEditorBufferIfNeeded(
        OpenScriptEditorBuffer openEditor,
        bool failOnSavedDiskMismatch,
        out bool didAutosaveOpenEditor,
        out string failureMessage,
        string operationName = "Refactor Namespace"
    )
    {
        ScriptEditorBufferAutosaveResult autosaveResult =
            OpenScriptEditorBufferAutosaveService.TryAutosaveIfNeeded(
                openEditor,
                failOnSavedDiskMismatch
            );

        didAutosaveOpenEditor = autosaveResult.DidAutosave;
        failureMessage = "";

        if (autosaveResult.Success)
            return true;

        string scriptPath = autosaveResult.ScriptPath;

        failureMessage = autosaveResult.Failure switch
        {
            ScriptEditorBufferAutosaveFailure.SavedBufferDiskMismatch => operationName
            == "Refactor Namespace"
                ? $"Refactor Namespace cancelled: the open editor buffer for '{scriptPath}' does not match the file on disk before scanning namespace usages. Save/reopen it before refactoring."
                : $"{operationName} cancelled: the open editor buffer for '{scriptPath}' does not match the file on disk before the operation. Save/reopen it and try again.",
            ScriptEditorBufferAutosaveFailure.WriteFailed => operationName == "Refactor Namespace"
                ? $"Refactor Namespace cancelled: could not autosave affected script before refactoring '{scriptPath}'. Some script buffers may already have been saved."
                : $"{operationName} cancelled: could not autosave the open script buffer before continuing '{scriptPath}'.",
            ScriptEditorBufferAutosaveFailure.AutosaveVerificationMismatch => operationName
            == "Refactor Namespace"
                ? $"Refactor Namespace cancelled: autosaved text for '{scriptPath}' did not match the open editor buffer. The namespace refactor was not applied."
                : $"{operationName} cancelled: autosaved text for '{scriptPath}' did not match the open editor buffer. The operation was not applied.",
            _ => "",
        };

        return false;
    }

    private static bool ScriptTextsMatchForDiskVerification(string left, string right)
    {
        return ScriptTextFileService.TextsMatchForDiskVerification(left, right);
    }

    private static string NormalizeScriptTextForDiskVerification(string text)
    {
        return ScriptTextFileService.NormalizeForDiskVerification(text);
    }

    private static string GetGodotInstanceId(GodotObject godotObject)
    {
        return godotObject == null ? "<null>" : godotObject.GetInstanceId().ToString();
    }

    private static Dictionary<string, OpenScriptEditorBuffer> GetOpenScriptEditorsByPath(
        Dictionary<string, string> originalTextsByPath,
        Dictionary<string, string> updatedTextsByPath,
        out string unsafeOpenScriptList
    )
    {
        ScriptEditorBufferLookupResult lookupResult =
            OpenScriptEditorBufferLocator.LocateByScriptTextsWithoutActivation(
                EditorInterface.Singleton?.GetScriptEditor(),
                originalTextsByPath,
                updatedTextsByPath
            );

        unsafeOpenScriptList = string.Join("\n", lookupResult.UnsafeOpenScriptPaths);
        return lookupResult.OpenEditorsByPath;
    }

    private static bool TryGetOpenScriptEditorsByIndexedScriptEditorPaths(
        ScriptEditor scriptEditor,
        HashSet<string> targetPaths,
        out Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath,
        out string failureMessage,
        HashSet<string> requiredPaths = null
    )
    {
        ScriptEditorBufferLookupResult lookupResult =
            OpenScriptEditorBufferLocator.LocateByIndexedScriptEditorPathsWithoutActivation(
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

    private static bool TryFindUnmatchedOpenScriptEditorUsingReference(
        ScriptEditor scriptEditor,
        Dictionary<string, OpenScriptEditorBuffer> matchedOpenEditorsByPath,
        string namespaceName,
        out string failureMessage
    )
    {
        failureMessage = "";

        if (scriptEditor == null || string.IsNullOrWhiteSpace(namespaceName))
            return false;

        HashSet<TextEdit> matchedTextEditors =
            matchedOpenEditorsByPath
                ?.Values.Where(openEditor => openEditor.TextEditor != null)
                .Select(openEditor => openEditor.TextEditor)
                .ToHashSet()
            ?? new HashSet<TextEdit>();

        foreach (ScriptEditorBase scriptEditorBase in scriptEditor.GetOpenScriptEditors())
        {
            Control baseEditor = scriptEditorBase?.GetBaseEditor();

            if (baseEditor is not TextEdit textEditor || matchedTextEditors.Contains(textEditor))
                continue;

            if (!ScriptTextContainsUsingStatement(textEditor.Text ?? "", namespaceName))
                continue;

            failureMessage =
                $"Refactor Namespace cancelled: an open script editor buffer contains 'using {namespaceName};', but System Explorer could not safely match that buffer to a script path without changing the active editor tab. Save/reopen open scripts before batch refactoring.";
            return true;
        }

        return false;
    }

    private static bool ScriptTextContainsUsingStatement(string scriptText, string namespaceName)
    {
        if (string.IsNullOrWhiteSpace(scriptText) || string.IsNullOrWhiteSpace(namespaceName))
            return false;

        string[] lines = NormalizeScriptTextForDiskVerification(scriptText).Split('\n');

        foreach (string line in lines)
        {
            string trimmedLine = line.Trim();

            if (!trimmedLine.StartsWith("using ", StringComparison.Ordinal))
                continue;

            if (!trimmedLine.EndsWith(";", StringComparison.Ordinal))
                continue;

            string usingNamespace = trimmedLine
                .Substring("using ".Length, trimmedLine.Length - "using ".Length - 1)
                .Trim();

            if (string.Equals(usingNamespace, namespaceName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool HasUnsavedOpenScriptEditorBuffers(
        Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath,
        out string unsavedScriptList
    )
    {
        unsavedScriptList = "";

        if (openEditorsByPath == null || openEditorsByPath.Count == 0)
            return false;

        IReadOnlyList<string> unsavedPaths = ScriptEditorBufferStateService.GetUnsavedPaths(
            openEditorsByPath.Values
        );

        if (unsavedPaths.Count == 0)
            return false;

        unsavedScriptList = string.Join("\n", unsavedPaths);
        return true;
    }

    private static bool TryAutosaveOpenScriptEditorBuffersIfNeeded(
        Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath,
        out bool didAutosaveOpenEditors,
        out string failureMessage
    )
    {
        didAutosaveOpenEditors = false;
        failureMessage = "";

        if (openEditorsByPath == null || openEditorsByPath.Count == 0)
            return true;

        foreach (OpenScriptEditorBuffer openEditor in openEditorsByPath.Values)
        {
            if (
                !TryAutosaveOpenScriptEditorBufferIfNeeded(
                    openEditor,
                    true,
                    out bool didAutosaveOpenEditor,
                    out string autosaveFailureMessage
                )
            )
            {
                failureMessage = autosaveFailureMessage;
                return false;
            }

            if (didAutosaveOpenEditor)
                didAutosaveOpenEditors = true;
        }

        return true;
    }

    private static void ApplyTextToOpenScriptEditors(
        Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath,
        Dictionary<string, string> updatedTextsByPath
    )
    {
        if (openEditorsByPath == null || updatedTextsByPath == null || openEditorsByPath.Count == 0)
            return;

        foreach (KeyValuePair<string, OpenScriptEditorBuffer> openEditorPair in openEditorsByPath)
        {
            if (!updatedTextsByPath.TryGetValue(openEditorPair.Key, out string updatedText))
                continue;

            ApplyTextToOpenScriptEditor(openEditorPair.Value.TextEditor, updatedText);
        }
    }

    private static void ApplyTextToOpenScriptEditor(TextEdit textEditor, string updatedText)
    {
        ScriptEditorBufferStateService.ApplyCommittedText(textEditor, updatedText);
    }

    private static string BuildScriptPathPayload(IEnumerable<string> scriptPaths)
    {
        if (scriptPaths == null)
            return "";

        return string.Join(
            "\n",
            scriptPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(ScriptPathUtility.Normalize)
                .Distinct(StringComparer.OrdinalIgnoreCase)
        );
    }

    private static string[] ParseScriptPathPayload(string scriptPathPayload)
    {
        if (string.IsNullOrWhiteSpace(scriptPathPayload))
            return Array.Empty<string>();

        return scriptPathPayload
            .Split('\n')
            .Select(ScriptPathUtility.Normalize)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    #endregion
}
#endif
