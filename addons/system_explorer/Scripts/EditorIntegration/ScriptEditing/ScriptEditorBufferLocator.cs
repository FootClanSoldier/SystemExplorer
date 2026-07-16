#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SystemExplorer.EditorIntegration.ScriptEditing;

internal sealed class ScriptEditorBufferLocator
{
    private readonly Func<string, string> _normalizePath;
    private readonly Func<string, string> _readTextFile;
    private readonly Func<string, string, bool> _scriptTextsMatchForDiskVerification;
    private readonly Func<string, bool> _fileExists;

    internal ScriptEditorBufferLocator(
        Func<string, string> normalizePath,
        Func<string, string> readTextFile,
        Func<string, string, bool> scriptTextsMatchForDiskVerification,
        Func<string, bool> fileExists
    )
    {
        _normalizePath = normalizePath ?? throw new ArgumentNullException(nameof(normalizePath));
        _readTextFile = readTextFile ?? throw new ArgumentNullException(nameof(readTextFile));
        _scriptTextsMatchForDiskVerification =
            scriptTextsMatchForDiskVerification
            ?? throw new ArgumentNullException(nameof(scriptTextsMatchForDiskVerification));
        _fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
    }

    internal ScriptEditorBufferLookupResult LocateByScriptTextsWithoutActivation(
        ScriptEditor scriptEditor,
        Dictionary<string, string> originalTextsByPath,
        Dictionary<string, string> updatedTextsByPath
    )
    {
        Dictionary<string, OpenScriptEditorBuffer> result = new(StringComparer.OrdinalIgnoreCase);

        if (
            originalTextsByPath == null
            || updatedTextsByPath == null
            || updatedTextsByPath.Count == 0
        )
        {
            return new ScriptEditorBufferLookupResult(result);
        }

        HashSet<string> targetPaths = updatedTextsByPath
            .Keys.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(_normalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (targetPaths.Count == 0 || scriptEditor == null)
            return new ScriptEditorBufferLookupResult(result);

        HashSet<string> openTargetPaths = GetOpenScriptPaths(scriptEditor, targetPaths);
        HashSet<TextEdit> usedTextEditors = new();

        Script currentScript = scriptEditor.GetCurrentScript();
        string currentScriptPath = _normalizePath(currentScript?.ResourcePath);

        if (targetPaths.Contains(currentScriptPath))
        {
            ScriptEditorBase currentEditor = scriptEditor.GetCurrentEditor();

            if (currentEditor?.GetBaseEditor() is TextEdit currentTextEditor)
                AddOpenScriptEditorBuffer(
                    result,
                    usedTextEditors,
                    currentScriptPath,
                    currentTextEditor
                );
        }

        List<TextEdit> openTextEditors = GetOpenScriptTextEditors(scriptEditor);
        List<string> unsafeOpenScripts = new();

        List<string> pathsToMatch = openTargetPaths
            .Concat(targetPaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (string targetPath in pathsToMatch)
        {
            if (result.ContainsKey(targetPath))
                continue;

            if (
                !originalTextsByPath.TryGetValue(targetPath, out string originalText)
                || !updatedTextsByPath.TryGetValue(targetPath, out string updatedText)
            )
            {
                if (openTargetPaths.Contains(targetPath))
                    unsafeOpenScripts.Add(targetPath);

                continue;
            }

            List<TextEdit> matchingEditors = openTextEditors
                .Where(textEditor =>
                    textEditor != null
                    && !usedTextEditors.Contains(textEditor)
                    && TextEditorMatchesScriptTexts(
                        textEditor,
                        targetPath,
                        originalTextsByPath,
                        updatedTextsByPath
                    )
                )
                .ToList();

            if (matchingEditors.Count == 1)
            {
                TextEdit matchingEditor = matchingEditors[0];
                int matchingPathCount = pathsToMatch.Count(path =>
                    !result.ContainsKey(path)
                    && TextEditorMatchesScriptTexts(
                        matchingEditor,
                        path,
                        originalTextsByPath,
                        updatedTextsByPath
                    )
                );

                if (matchingPathCount == 1)
                {
                    AddOpenScriptEditorBuffer(result, usedTextEditors, targetPath, matchingEditor);
                    continue;
                }
            }

            if (matchingEditors.Count > 0 || openTargetPaths.Contains(targetPath))
                unsafeOpenScripts.Add(targetPath);
        }

        return new ScriptEditorBufferLookupResult(
            result,
            unsafeOpenScriptPaths: unsafeOpenScripts.Distinct(StringComparer.OrdinalIgnoreCase)
        );
    }

    internal ScriptEditorBufferLookupResult LocateByIndexedScriptEditorPathsWithoutActivation(
        ScriptEditor scriptEditor,
        HashSet<string> targetPaths,
        HashSet<string> requiredPaths = null
    )
    {
        Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath = new(
            StringComparer.OrdinalIgnoreCase
        );

        if (scriptEditor == null || targetPaths == null || targetPaths.Count == 0)
            return new ScriptEditorBufferLookupResult(openEditorsByPath);

        Godot.Collections.Array<Script> openScripts = scriptEditor.GetOpenScripts();
        Godot.Collections.Array<ScriptEditorBase> openScriptEditors =
            scriptEditor.GetOpenScriptEditors();

        HashSet<TextEdit> usedTextEditors = new();

        AddCurrentOpenScriptEditorBufferIfTarget(
            scriptEditor,
            targetPaths,
            openEditorsByPath,
            usedTextEditors
        );

        if (openScripts.Count == openScriptEditors.Count)
        {
            for (int i = 0; i < openScripts.Count; i++)
            {
                Script openScript = openScripts[i];

                if (openScript == null)
                    continue;

                string scriptPath = _normalizePath(openScript.ResourcePath);

                if (!targetPaths.Contains(scriptPath))
                    continue;

                if (!scriptPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    continue;

                Control baseEditor = openScriptEditors[i]?.GetBaseEditor();

                bool isRequiredScript = requiredPaths?.Contains(scriptPath) == true;

                if (baseEditor is not TextEdit textEditor)
                {
                    if (isRequiredScript)
                    {
                        return new ScriptEditorBufferLookupResult(
                            openEditorsByPath,
                            ScriptEditorBufferLookupFailure.RequiredEditorUnavailable,
                            scriptPath
                        );
                    }

                    continue;
                }

                bool hasExistingOpenEditor = openEditorsByPath.TryGetValue(
                    scriptPath,
                    out OpenScriptEditorBuffer existingOpenEditor
                );

                if (hasExistingOpenEditor && existingOpenEditor.TextEditor != textEditor)
                {
                    if (isRequiredScript)
                    {
                        return new ScriptEditorBufferLookupResult(
                            openEditorsByPath,
                            ScriptEditorBufferLookupFailure.DuplicateEditorForPath,
                            scriptPath
                        );
                    }

                    continue;
                }

                if (!hasExistingOpenEditor && usedTextEditors.Contains(textEditor))
                {
                    if (isRequiredScript)
                    {
                        return new ScriptEditorBufferLookupResult(
                            openEditorsByPath,
                            ScriptEditorBufferLookupFailure.RequiredEditorAlreadyUsed,
                            scriptPath
                        );
                    }

                    continue;
                }

                ScriptEditorBufferLookupFailure pairFailure = IndexedScriptEditorPairLooksSafe(
                    openScript,
                    scriptPath,
                    textEditor
                );

                if (pairFailure != ScriptEditorBufferLookupFailure.None)
                {
                    if (isRequiredScript)
                    {
                        return new ScriptEditorBufferLookupResult(
                            openEditorsByPath,
                            pairFailure,
                            scriptPath
                        );
                    }

                    continue;
                }

                if (hasExistingOpenEditor)
                    continue;

                openEditorsByPath[scriptPath] = new OpenScriptEditorBuffer(scriptPath, textEditor);
                usedTextEditors.Add(textEditor);
            }

            return new ScriptEditorBufferLookupResult(openEditorsByPath);
        }

        AddOpenScriptEditorBuffersByDiskText(
            scriptEditor,
            targetPaths,
            openEditorsByPath,
            usedTextEditors
        );

        List<string> unmatchedRequiredOpenScripts = GetUnmatchedOpenScriptPaths(
            scriptEditor,
            requiredPaths,
            openEditorsByPath
        );

        if (unmatchedRequiredOpenScripts.Count > 0)
        {
            return new ScriptEditorBufferLookupResult(
                openEditorsByPath,
                ScriptEditorBufferLookupFailure.UnmatchedRequiredOpenScripts,
                unmatchedRequiredPaths: unmatchedRequiredOpenScripts
            );
        }

        return new ScriptEditorBufferLookupResult(openEditorsByPath);
    }

    private bool TextEditorMatchesScriptTexts(
        TextEdit textEditor,
        string scriptPath,
        Dictionary<string, string> originalTextsByPath,
        Dictionary<string, string> updatedTextsByPath
    )
    {
        if (textEditor == null || string.IsNullOrWhiteSpace(scriptPath))
            return false;

        if (
            !originalTextsByPath.TryGetValue(scriptPath, out string originalText)
            || !updatedTextsByPath.TryGetValue(scriptPath, out string updatedText)
        )
        {
            return false;
        }

        string editorText = textEditor.Text ?? "";
        return _scriptTextsMatchForDiskVerification(editorText, originalText)
            || _scriptTextsMatchForDiskVerification(editorText, updatedText);
    }

    private void AddCurrentOpenScriptEditorBufferIfTarget(
        ScriptEditor scriptEditor,
        HashSet<string> targetPaths,
        Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath,
        HashSet<TextEdit> usedTextEditors
    )
    {
        if (
            scriptEditor == null
            || targetPaths == null
            || targetPaths.Count == 0
            || openEditorsByPath == null
            || usedTextEditors == null
        )
        {
            return;
        }

        Script currentScript = scriptEditor.GetCurrentScript();
        string currentScriptPath = _normalizePath(currentScript?.ResourcePath);

        if (
            string.IsNullOrWhiteSpace(currentScriptPath)
            || !targetPaths.Contains(currentScriptPath)
            || openEditorsByPath.ContainsKey(currentScriptPath)
        )
        {
            return;
        }

        ScriptEditorBase currentEditor = scriptEditor.GetCurrentEditor();
        Control baseEditor = currentEditor?.GetBaseEditor();

        if (
            baseEditor is TextEdit currentTextEditor
            && !usedTextEditors.Contains(currentTextEditor)
        )
        {
            AddOpenScriptEditorBuffer(
                openEditorsByPath,
                usedTextEditors,
                currentScriptPath,
                currentTextEditor
            );
        }
    }

    private void AddOpenScriptEditorBuffersByDiskText(
        ScriptEditor scriptEditor,
        HashSet<string> targetPaths,
        Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath,
        HashSet<TextEdit> usedTextEditors
    )
    {
        if (
            scriptEditor == null
            || targetPaths == null
            || openEditorsByPath == null
            || usedTextEditors == null
        )
        {
            return;
        }

        List<TextEdit> openTextEditors = GetOpenScriptTextEditors(scriptEditor);
        Dictionary<string, string> diskTextsByPath = targetPaths
            .Where(path =>
                !string.IsNullOrWhiteSpace(path)
                && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                && _fileExists(path)
            )
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(_normalizePath, _readTextFile, StringComparer.OrdinalIgnoreCase);

        foreach (string targetPath in diskTextsByPath.Keys)
        {
            if (openEditorsByPath.ContainsKey(targetPath))
                continue;

            string diskText = diskTextsByPath[targetPath];

            List<TextEdit> matchingEditors = openTextEditors
                .Where(textEditor =>
                    textEditor != null
                    && !usedTextEditors.Contains(textEditor)
                    && _scriptTextsMatchForDiskVerification(textEditor.Text ?? "", diskText)
                )
                .ToList();

            if (matchingEditors.Count != 1)
                continue;

            TextEdit matchingEditor = matchingEditors[0];
            int matchingPathCount = diskTextsByPath.Count(pair =>
                !openEditorsByPath.ContainsKey(pair.Key)
                && _scriptTextsMatchForDiskVerification(matchingEditor.Text ?? "", pair.Value)
            );

            if (matchingPathCount != 1)
                continue;

            AddOpenScriptEditorBuffer(
                openEditorsByPath,
                usedTextEditors,
                targetPath,
                matchingEditor
            );
        }
    }

    private List<string> GetUnmatchedOpenScriptPaths(
        ScriptEditor scriptEditor,
        HashSet<string> targetPaths,
        Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath
    )
    {
        if (scriptEditor == null || targetPaths == null || targetPaths.Count == 0)
            return new List<string>();

        return GetOpenScriptPaths(scriptEditor, targetPaths)
            .Where(path => openEditorsByPath == null || !openEditorsByPath.ContainsKey(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private ScriptEditorBufferLookupFailure IndexedScriptEditorPairLooksSafe(
        Script openScript,
        string scriptPath,
        TextEdit textEditor
    )
    {
        if (openScript == null || textEditor == null || string.IsNullOrWhiteSpace(scriptPath))
            return ScriptEditorBufferLookupFailure.UnsafeIndexedPair;

        string editorText = textEditor.Text ?? "";
        string diskText = _readTextFile(scriptPath);

        if (!ScriptEditorBufferStateService.IsUnsaved(textEditor))
        {
            if (_scriptTextsMatchForDiskVerification(editorText, diskText))
                return ScriptEditorBufferLookupFailure.None;

            return ScriptEditorBufferLookupFailure.SavedEditorDiskMismatch;
        }

        return ScriptEditorBufferLookupFailure.None;
    }

    private HashSet<string> GetOpenScriptPaths(
        ScriptEditor scriptEditor,
        HashSet<string> targetPaths
    )
    {
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);

        if (scriptEditor == null || targetPaths == null || targetPaths.Count == 0)
            return result;

        foreach (Script openScript in scriptEditor.GetOpenScripts())
        {
            if (openScript == null)
                continue;

            string scriptPath = _normalizePath(openScript.ResourcePath);

            if (targetPaths.Contains(scriptPath))
                result.Add(scriptPath);
        }

        return result;
    }

    private static List<TextEdit> GetOpenScriptTextEditors(ScriptEditor scriptEditor)
    {
        List<TextEdit> result = new();

        if (scriptEditor == null)
            return result;

        foreach (ScriptEditorBase scriptEditorBase in scriptEditor.GetOpenScriptEditors())
        {
            Control baseEditor = scriptEditorBase?.GetBaseEditor();

            if (baseEditor is TextEdit textEditor && !result.Contains(textEditor))
                result.Add(textEditor);
        }

        return result;
    }

    private void AddOpenScriptEditorBuffer(
        Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath,
        HashSet<TextEdit> usedTextEditors,
        string scriptPath,
        TextEdit textEditor
    )
    {
        if (openEditorsByPath == null || usedTextEditors == null || textEditor == null)
            return;

        string normalizedPath = _normalizePath(scriptPath);

        if (string.IsNullOrWhiteSpace(normalizedPath))
            return;

        openEditorsByPath[normalizedPath] = new OpenScriptEditorBuffer(normalizedPath, textEditor);
        usedTextEditors.Add(textEditor);
    }
}
#endif
