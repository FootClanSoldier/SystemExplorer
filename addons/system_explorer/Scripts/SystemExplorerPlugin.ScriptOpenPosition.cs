#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using SystemExplorer.EditorIntegration.ScriptEditing;

public partial class SystemExplorerPlugin
{
    #region Script Open Position

    private readonly HashSet<string> _scriptsOpenedFromSystemExplorerThisSession = new(
        StringComparer.OrdinalIgnoreCase
    );

    private void OpenScriptFromSystemExplorer(
        Script script,
        string scriptPath,
        bool releaseTreeFocusAfterNavigation = true
    )
    {
        if (script == null)
            return;

        string normalizedScriptPath = ScriptPathUtility.Normalize(scriptPath);

        if (string.IsNullOrWhiteSpace(normalizedScriptPath))
            normalizedScriptPath = ScriptPathUtility.Normalize(script.ResourcePath);

        EditorInterface.Singleton.EditScript(script);

        bool shouldMoveToTop =
            !string.IsNullOrWhiteSpace(normalizedScriptPath)
            && _scriptsOpenedFromSystemExplorerThisSession.Add(normalizedScriptPath);

        if (shouldMoveToTop)
            CallDeferred(nameof(TryMoveCurrentScriptEditorToTop), normalizedScriptPath);

        if (releaseTreeFocusAfterNavigation)
            CallDeferred(nameof(ReleaseTreeFocusAfterNavigation));
    }

    private void TryMoveCurrentScriptEditorToTop(string expectedScriptPath)
    {
        string normalizedExpectedPath = ScriptPathUtility.Normalize(expectedScriptPath);

        if (string.IsNullOrWhiteSpace(normalizedExpectedPath))
            return;

        ScriptEditor scriptEditor = EditorInterface.Singleton?.GetScriptEditor();

        if (scriptEditor == null)
            return;

        Script currentScript = scriptEditor.GetCurrentScript();
        string currentScriptPath = ScriptPathUtility.Normalize(currentScript?.ResourcePath);

        if (
            !string.Equals(
                currentScriptPath,
                normalizedExpectedPath,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            DebugLogger.Log(
                $"Script open top reset skipped: current script is '{currentScriptPath}', expected '{normalizedExpectedPath}'."
            );
            return;
        }

        ScriptEditorBase currentEditor = scriptEditor.GetCurrentEditor();
        Control baseEditor = currentEditor?.GetBaseEditor();

        if (baseEditor is not TextEdit textEditor)
        {
            DebugLogger.Log(
                $"Script open top reset skipped: current editor for '{normalizedExpectedPath}' is '{baseEditor?.GetType().Name ?? "<null>"}'."
            );
            return;
        }

        MoveTextEditorToTop(textEditor, normalizedExpectedPath);
    }

    private void MoveTextEditorToTop(TextEdit textEditor, string scriptPath)
    {
        if (textEditor == null)
            return;

        try
        {
            // Use Godot method/property names directly so the helper stays tolerant of
            // generated C# overload changes between Godot versions.
            textEditor.Call("set_caret_column", 0, false, 0);
            textEditor.Call("set_caret_line", 0, false, true, 0, 0);
            textEditor.Call("set_caret_column", 0, false, 0);
            textEditor.Set("scroll_horizontal", 0);
            textEditor.Set("scroll_vertical", 0.0);

            DebugLogger.Log($"Script open top reset applied to '{scriptPath}'.");
        }
        catch (Exception exception)
        {
            DebugLogger.Log($"Script open top reset failed for '{scriptPath}': {exception.Message}");
        }
    }

    #endregion
}
#endif
