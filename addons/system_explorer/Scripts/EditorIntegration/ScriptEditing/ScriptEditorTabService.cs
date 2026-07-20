#if TOOLS
using Godot;
using System;
using System.Collections.Generic;

namespace SystemExplorer.EditorIntegration.ScriptEditing;

internal readonly record struct ScriptEditorTabCloseResult(bool Success, string FailureMessage)
{
	internal static ScriptEditorTabCloseResult Succeeded() => new(true, "");

	internal static ScriptEditorTabCloseResult Failed(string failureMessage) =>
		new(false, failureMessage ?? "");
}

/// <summary>
/// Godot 4.6-specific adapter for closing exactly the active Script Editor tab through
/// Script Editor's own File-menu command. The adapter never mutates files, resources,
/// editor-buffer text, System Explorer data, or Godot's editor cache files. It verifies
/// only the live open-tab/resource state; persistence and pruning of closed-script cache
/// sections remain owned by Godot.
/// </summary>
internal sealed class ScriptEditorTabService
{
	// Godot 4.6 ScriptEditor::MenuOptions values from script_editor_plugin.h.
	private const int FileMenuSave = 5;
	private const int FileMenuClose = 15;
	private const int FileMenuCloseAll = 16;
	private const int FileMenuCloseOtherTabs = 17;
	private const int FileMenuCloseTabsBelow = 18;
	private const int FileMenuRun = 20;

	internal ScriptEditorTabCloseResult TryCloseScriptTab(
		ScriptEditor scriptEditor,
		Script script,
		ScriptEditorBase scriptEditorBase,
		TextEdit textEditor
	)
	{
		if (
			scriptEditor == null
			|| script == null
			|| scriptEditorBase == null
			|| textEditor == null
			|| !GodotObject.IsInstanceValid(scriptEditor)
			|| !GodotObject.IsInstanceValid(script)
			|| !GodotObject.IsInstanceValid(scriptEditorBase)
			|| !GodotObject.IsInstanceValid(textEditor)
		)
		{
			return ScriptEditorTabCloseResult.Failed(
                "The target script tab or editor control was no longer valid."
			);
		}

		Script currentScript = scriptEditor.GetCurrentScript();
		ScriptEditorBase currentEditor = scriptEditor.GetCurrentEditor();
		Control currentBaseEditor = currentEditor?.GetBaseEditor();

		if (
			currentScript == null
			|| currentScript.GetInstanceId() != script.GetInstanceId()
			|| currentEditor == null
			|| currentEditor.GetInstanceId() != scriptEditorBase.GetInstanceId()
			|| currentBaseEditor is not TextEdit currentTextEditor
			|| currentTextEditor.GetInstanceId() != textEditor.GetInstanceId()
		)
		{
			return ScriptEditorTabCloseResult.Failed(
                "Godot's active Script Editor tab did not match the exact script and text buffer selected for the file operation."
			);
		}

		List<PopupMenu> matchingFileMenus = new();
		CollectMatchingGodot46FileMenus(scriptEditor, matchingFileMenus);

		if (matchingFileMenus.Count != 1)
		{
			return ScriptEditorTabCloseResult.Failed(
				$"System Explorer could not identify Godot 4.6's Script Editor File menu unambiguously (matches: {matchingFileMenus.Count})."
			);
		}

		PopupMenu fileMenu = matchingFileMenus[0];
		int closeItemIndex = fileMenu.GetItemIndex(FileMenuClose);

		if (closeItemIndex < 0 || fileMenu.IsItemDisabled(closeItemIndex))
		{
			return ScriptEditorTabCloseResult.Failed(
                "Godot 4.6's Script Editor Close command was unavailable or disabled."
			);
		}

		string normalizedPath = ScriptPathUtility.Normalize(script.ResourcePath);
		ulong targetScriptInstanceId = script.GetInstanceId();

		try
		{
			// This follows Godot's own Script Editor File-menu route to
            // ScriptEditor::_menu_option(FILE_MENU_CLOSE), which invokes
            // the full internal _close_current_tab/_close_tab lifecycle.
            Error emitError = fileMenu.EmitSignal(
                PopupMenu.SignalName.IdPressed,
                (long)FileMenuClose
            );

            if (emitError != Error.Ok)
            {
                return ScriptEditorTabCloseResult.Failed(
					$"Godot's Script Editor Close signal could not be emitted ({emitError})."
                );
            }
        }
        catch (Exception exception)
        {
            return ScriptEditorTabCloseResult.Failed(
				$"Godot's Script Editor Close command threw an exception: {exception.Message}"
            );
        }

        foreach (Script openScript in scriptEditor.GetOpenScripts())
        {
            if (openScript == null)
                continue;

            if (openScript.GetInstanceId() == targetScriptInstanceId)
            {
                return ScriptEditorTabCloseResult.Failed(
                    "Godot reported that the exact old Script resource was still open after the close command."
                );
            }

            string openPath = ScriptPathUtility.Normalize(openScript.ResourcePath);

            if (
                !string.IsNullOrWhiteSpace(normalizedPath)
                && string.Equals(openPath, normalizedPath, StringComparison.OrdinalIgnoreCase)
            )
            {
                return ScriptEditorTabCloseResult.Failed(
                    $"Godot reported that the old script path was still open after the close command: {openPath}"
                );
            }
        }

        return ScriptEditorTabCloseResult.Succeeded();
    }

    private static void CollectMatchingGodot46FileMenus(Node node, List<PopupMenu> matches)
    {
        if (node == null || matches == null)
            return;

        foreach (Node child in node.GetChildren())
        {
            if (child is MenuButton menuButton)
            {
                PopupMenu popup = menuButton.GetPopup();

                if (IsGodot46ScriptEditorFileMenu(popup))
                    matches.Add(popup);
            }

            CollectMatchingGodot46FileMenus(child, matches);
        }
    }

    private static bool IsGodot46ScriptEditorFileMenu(PopupMenu popup)
    {
        return popup != null
            && GodotObject.IsInstanceValid(popup)
            && popup.GetItemIndex(FileMenuSave) >= 0
            && popup.GetItemIndex(FileMenuClose) >= 0
            && popup.GetItemIndex(FileMenuCloseAll) >= 0
            && popup.GetItemIndex(FileMenuCloseOtherTabs) >= 0
            && popup.GetItemIndex(FileMenuCloseTabsBelow) >= 0
            && popup.GetItemIndex(FileMenuRun) >= 0;
    }
}
#endif
