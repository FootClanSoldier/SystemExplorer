#if TOOLS
using System;
using System.Collections.Generic;
using Godot;
using SystemExplorer.EditorIntegration.ScriptEditing;

public partial class SystemExplorerPlugin
{
	#region Quick Actions - Beautify Editor State
	private readonly List<BeautifyEditorViewState> _pendingBeautifyEditorViewStates = new();
	private int _pendingBeautifyEditorViewStateRestorePasses;

	private readonly struct BeautifyEditorViewState
	{
		public BeautifyEditorViewState(
			string path,
			TextEdit textEditor,
			int firstVisibleLine,
			int scrollHorizontal,
			double scrollVertical,
			int caretLine,
			int caretColumn,
			bool hadFocus
		)
		{
			Path = NormalizeScriptPath(path);
			TextEditor = textEditor;
			FirstVisibleLine = firstVisibleLine;
			ScrollHorizontal = scrollHorizontal;
			ScrollVertical = scrollVertical;
			CaretLine = caretLine;
			CaretColumn = caretColumn;
			HadFocus = hadFocus;
		}

		public string Path { get; }
		public TextEdit TextEditor { get; }
		public int FirstVisibleLine { get; }
		public int ScrollHorizontal { get; }
		public double ScrollVertical { get; }
		public int CaretLine { get; }
		public int CaretColumn { get; }
		public bool HadFocus { get; }
		public bool IsValid => TextEditor != null;
	}

	private static bool TryAutosaveBeautifyOpenEditorIfNeeded(
		string scriptPath,
		string diskText,
		Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath,
		out string originalText,
		out bool didAutosaveOpenEditor,
		out string failureMessage
	)
	{
		originalText = diskText ?? "";
		didAutosaveOpenEditor = false;
		failureMessage = "";

		if (
			openEditorsByPath == null
			|| !openEditorsByPath.TryGetValue(scriptPath, out OpenScriptEditorBuffer openEditor)
			|| openEditor.TextEditor == null
		)
		{
			return true;
		}

		TextEdit textEditor = openEditor.TextEditor;
		bool isUnsaved = textEditor.GetVersion() != textEditor.GetSavedVersion();

		if (!isUnsaved)
		{
			if (textEditor.Text != originalText)
			{
				failureMessage =
					$"Beautify Script cancelled: the open editor buffer for '{scriptPath}' does not match the file on disk. Reload/save the script before formatting.";
				return false;
			}

			return true;
		}

		string editorText = textEditor.Text ?? "";

		if (!WriteTextFile(scriptPath, editorText))
		{
			failureMessage =
				$"Beautify Script cancelled: could not autosave the selected script before formatting '{scriptPath}'.";
			return false;
		}

		string savedEditorText = ReadTextFile(scriptPath);

		if (savedEditorText != editorText)
		{
			failureMessage =
				$"Beautify Script cancelled: autosaved text for '{scriptPath}' did not match the open editor buffer. The script was not formatted.";
			return false;
		}

		textEditor.TagSavedVersion();
		originalText = savedEditorText;
		didAutosaveOpenEditor = true;
		return true;
	}

	private static bool ValidateBeautifyOpenEditorStillMatchesDisk(
		string scriptPath,
		string originalText,
		Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath,
		out string failureMessage
	)
	{
		failureMessage = "";

		if (
			openEditorsByPath == null
			|| !openEditorsByPath.TryGetValue(scriptPath, out OpenScriptEditorBuffer openEditor)
			|| openEditor.TextEditor == null
		)
		{
			return true;
		}

		TextEdit textEditor = openEditor.TextEditor;

		if (textEditor.GetVersion() != textEditor.GetSavedVersion())
		{
			failureMessage =
				$"Beautify Script cancelled: the selected script became unsaved while CSharpier was running. Try again after saving/retrying:\n{scriptPath}";
			return false;
		}

		if (textEditor.Text != originalText)
		{
			failureMessage =
				$"Beautify Script cancelled: the open editor buffer for '{scriptPath}' changed while CSharpier was running. Try again.";
			return false;
		}

		return true;
	}

	private static bool TryApplyBeautifyTextToEditorBeforeDiskWrite(
		string scriptPath,
		string originalText,
		string formattedText,
		Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath,
		out string failureMessage
	)
	{
		failureMessage = "";

		if (
			openEditorsByPath == null
			|| !openEditorsByPath.TryGetValue(scriptPath, out OpenScriptEditorBuffer openEditor)
			|| openEditor.TextEditor == null
		)
		{
			return true;
		}

		TextEdit textEditor = openEditor.TextEditor;

		if (textEditor.GetVersion() != textEditor.GetSavedVersion())
		{
			failureMessage =
				$"Beautify Script cancelled: the selected script is unsaved again before applying formatted text:\n{scriptPath}";
			return false;
		}

		if (textEditor.Text != originalText)
		{
			failureMessage =
				$"Beautify Script cancelled: the open editor buffer for '{scriptPath}' no longer matches the saved text. Try again.";
			return false;
		}

		if (textEditor.Text != formattedText)
			textEditor.Text = formattedText;

		return true;
	}

	private BeautifyEditorViewState CaptureBeautifyEditorViewState(
		string scriptPath,
		Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath
	)
	{
		if (
			openEditorsByPath == null
			|| !openEditorsByPath.TryGetValue(scriptPath, out OpenScriptEditorBuffer openEditor)
			|| !IsBeautifyTextEditorAvailable(openEditor.TextEditor)
		)
		{
			return default;
		}

		TextEdit textEditor = openEditor.TextEditor;

		try
		{
			return new BeautifyEditorViewState(
				scriptPath,
				textEditor,
				Math.Max(0, textEditor.GetFirstVisibleLine()),
				Math.Max(0, textEditor.ScrollHorizontal),
				Math.Max(0.0, textEditor.ScrollVertical),
				Math.Max(0, textEditor.GetCaretLine()),
				Math.Max(0, textEditor.GetCaretColumn()),
				textEditor.HasFocus()
			);
		}
		catch (Exception exception)
		{
			DebugPrintBeautify(
				$"Beautify Script view-state capture skipped for '{scriptPath}': {exception.Message}"
			);
			return default;
		}
	}

	private void RestoreBeautifyEditorViewStateNowAndDeferred(
		BeautifyEditorViewState editorViewState
	)
	{
		if (!editorViewState.IsValid)
			return;

		RestoreBeautifyEditorViewState(editorViewState);

		_pendingBeautifyEditorViewStates.Add(editorViewState);
		_pendingBeautifyEditorViewStateRestorePasses = Math.Max(
			_pendingBeautifyEditorViewStateRestorePasses,
			2
		);
		CallDeferred(nameof(RestorePendingBeautifyEditorViewStates));
	}

	private void RestorePendingBeautifyEditorViewStates()
	{
		if (_pendingBeautifyEditorViewStates.Count == 0)
		{
			_pendingBeautifyEditorViewStateRestorePasses = 0;
			return;
		}

		BeautifyEditorViewState[] pendingStates = _pendingBeautifyEditorViewStates.ToArray();

		foreach (BeautifyEditorViewState editorViewState in pendingStates)
			RestoreBeautifyEditorViewState(editorViewState);

		_pendingBeautifyEditorViewStateRestorePasses--;

		if (_pendingBeautifyEditorViewStateRestorePasses > 0)
		{
			CallDeferred(nameof(RestorePendingBeautifyEditorViewStates));
			return;
		}

		_pendingBeautifyEditorViewStates.Clear();
	}

	private void RestoreBeautifyEditorViewState(BeautifyEditorViewState editorViewState)
	{
		if (!editorViewState.IsValid || !IsBeautifyTextEditorAvailable(editorViewState.TextEditor))
			return;

		TextEdit textEditor = editorViewState.TextEditor;

		try
		{
			int lineCount = Math.Max(1, textEditor.GetLineCount());
			int firstVisibleLine = ClampBeautifyEditorViewStateValue(
				editorViewState.FirstVisibleLine,
				0,
				lineCount - 1
			);
			int caretLine = ClampBeautifyEditorViewStateValue(
				editorViewState.CaretLine,
				0,
				lineCount - 1
			);
			int caretColumn = ClampBeautifyEditorViewStateValue(
				editorViewState.CaretColumn,
				0,
				GetBeautifyEditorLineLength(textEditor, caretLine)
			);
			int scrollHorizontal = Math.Max(0, editorViewState.ScrollHorizontal);
			double scrollVertical = Math.Min(
				Math.Max(0.0, editorViewState.ScrollVertical),
				Math.Max(0.0, lineCount - 1)
			);

			textEditor.SetCaretLine(caretLine, false, true, -1);
			textEditor.SetCaretColumn(caretColumn, false);
			textEditor.SetLineAsFirstVisible(firstVisibleLine);
			textEditor.ScrollVertical = scrollVertical;
			textEditor.ScrollHorizontal = scrollHorizontal;

			if (editorViewState.HadFocus && textEditor.IsVisibleInTree())
				textEditor.GrabFocus();
		}
		catch (Exception exception)
		{
			DebugPrintBeautify(
				$"Beautify Script view-state restore skipped for '{editorViewState.Path}': {exception.Message}"
			);
		}
	}

	private static bool IsBeautifyTextEditorAvailable(TextEdit textEditor)
	{
		return textEditor != null
			&& GodotObject.IsInstanceValid(textEditor)
			&& !textEditor.IsQueuedForDeletion();
	}

	private static int GetBeautifyEditorLineLength(TextEdit textEditor, int line)
	{
		if (textEditor == null)
			return 0;

		try
		{
			if (line < 0 || line >= textEditor.GetLineCount())
				return 0;

			return (textEditor.GetLine(line) ?? "").Length;
		}
		catch
		{
			return 0;
		}
	}

	private static int ClampBeautifyEditorViewStateValue(int value, int min, int max)
	{
		if (max < min)
			return min;

		return Math.Min(Math.Max(value, min), max);
	}

	private static void RestoreBeautifyOpenEditorAfterFailedWrite(
		string scriptPath,
		string originalText,
		Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath
	)
	{
		if (
			openEditorsByPath == null
			|| !openEditorsByPath.TryGetValue(scriptPath, out OpenScriptEditorBuffer openEditor)
			|| openEditor.TextEditor == null
		)
		{
			return;
		}

		ApplyTextToOpenScriptEditor(openEditor.TextEditor, originalText);
	}

	#endregion
}
#endif
