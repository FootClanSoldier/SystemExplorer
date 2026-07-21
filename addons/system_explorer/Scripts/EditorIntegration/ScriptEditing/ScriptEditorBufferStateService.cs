#if TOOLS
using Godot;
using System;
using System.Collections.Generic;

namespace SystemExplorer.EditorIntegration.ScriptEditing;

internal static class ScriptEditorBufferStateService
{
	internal static bool IsUnsaved(TextEdit textEditor)
	{
		return textEditor != null && textEditor.GetVersion() != textEditor.GetSavedVersion();
	}

	internal static IReadOnlyList<string> GetUnsavedPaths(
		IEnumerable<OpenScriptEditorBuffer> openEditors
	)
	{
		List<string> unsavedPaths = new();

		if (openEditors == null)
			return unsavedPaths;

		HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);

		foreach (OpenScriptEditorBuffer openEditor in openEditors)
		{
			if (!IsUnsaved(openEditor.TextEditor) || !seenPaths.Add(openEditor.Path))
				continue;

			unsavedPaths.Add(openEditor.Path);
		}

		return unsavedPaths;
	}

	internal static void MarkCurrentVersionSaved(TextEdit textEditor)
	{
		textEditor.TagSavedVersion();
	}

	internal static void ApplyCommittedText(TextEdit textEditor, string updatedText)
	{
		if (textEditor == null)
			return;

		if (textEditor.Text != updatedText)
			textEditor.Text = updatedText;

		textEditor.ClearUndoHistory();
		textEditor.TagSavedVersion();
	}
}
#endif
