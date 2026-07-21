#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.QuickActions.Beautify;

internal sealed class BeautifyEditorStateService
{
	private readonly Action<string> _debugLog;
	private readonly Action<Action> _scheduleDeferred;
	private readonly List<BeautifyEditorViewState> _pendingEditorViewStates = new();
	private int _pendingEditorViewStateRestorePasses;

	internal BeautifyEditorStateService(Action<string> debugLog, Action<Action> scheduleDeferred)
	{
		_debugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
		_scheduleDeferred =
			scheduleDeferred ?? throw new ArgumentNullException(nameof(scheduleDeferred));
	}

	internal bool TryAutosaveOpenEditorIfNeeded(
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
		bool isUnsaved = ScriptEditorBufferStateService.IsUnsaved(textEditor);

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

		if (!ScriptTextFileService.WriteText(scriptPath, editorText))
		{
			failureMessage =
				$"Beautify Script cancelled: could not autosave the selected script before formatting '{scriptPath}'.";
			return false;
		}

		string savedEditorText = ScriptTextFileService.ReadText(scriptPath);

		if (savedEditorText != editorText)
		{
			failureMessage =
				$"Beautify Script cancelled: autosaved text for '{scriptPath}' did not match the open editor buffer. The script was not formatted.";
			return false;
		}

		ScriptEditorBufferStateService.MarkCurrentVersionSaved(textEditor);
		originalText = savedEditorText;
		didAutosaveOpenEditor = true;
		return true;
	}

	internal bool ValidateOpenEditorStillMatchesDisk(
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

		if (ScriptEditorBufferStateService.IsUnsaved(textEditor))
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

	internal bool TryApplyTextToEditorBeforeDiskWrite(
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

		if (ScriptEditorBufferStateService.IsUnsaved(textEditor))
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

	internal BeautifyEditorViewState CaptureEditorViewState(
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
			_debugLog(
				$"Beautify Script view-state capture skipped for '{scriptPath}': {exception.Message}"
			);
			return default;
		}
	}

	internal void RestoreEditorViewStateNowAndDeferred(
		BeautifyEditorViewState editorViewState
	)
	{
		if (!editorViewState.IsValid)
			return;

		RestoreEditorViewState(editorViewState);

		_pendingEditorViewStates.Add(editorViewState);
		_pendingEditorViewStateRestorePasses = Math.Max(
			_pendingEditorViewStateRestorePasses,
			2
		);
		_scheduleDeferred(RestorePendingEditorViewStates);
	}

	internal void RestoreOpenEditorAfterFailedWrite(
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

		ScriptEditorBufferStateService.ApplyCommittedText(openEditor.TextEditor, originalText);
	}

	private void RestorePendingEditorViewStates()
	{
		if (_pendingEditorViewStates.Count == 0)
		{
			_pendingEditorViewStateRestorePasses = 0;
			return;
		}

		BeautifyEditorViewState[] pendingStates = _pendingEditorViewStates.ToArray();

		foreach (BeautifyEditorViewState editorViewState in pendingStates)
			RestoreEditorViewState(editorViewState);

		_pendingEditorViewStateRestorePasses--;

		if (_pendingEditorViewStateRestorePasses > 0)
		{
			_scheduleDeferred(RestorePendingEditorViewStates);
			return;
		}

		_pendingEditorViewStates.Clear();
	}

	private void RestoreEditorViewState(BeautifyEditorViewState editorViewState)
	{
		if (!editorViewState.IsValid || !IsBeautifyTextEditorAvailable(editorViewState.TextEditor))
			return;

		TextEdit textEditor = editorViewState.TextEditor;

		try
		{
			int lineCount = Math.Max(1, textEditor.GetLineCount());
			int firstVisibleLine = ClampEditorViewStateValue(
				editorViewState.FirstVisibleLine,
				0,
				lineCount - 1
			);
			int caretLine = ClampEditorViewStateValue(
				editorViewState.CaretLine,
				0,
				lineCount - 1
			);
			int caretColumn = ClampEditorViewStateValue(
				editorViewState.CaretColumn,
				0,
				GetEditorLineLength(textEditor, caretLine)
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
			_debugLog(
				$"Beautify Script view-state restore skipped for '{editorViewState.Path}': {exception.Message}"
			);
		}
	}

	internal static bool IsBeautifyTextEditorAvailable(TextEdit textEditor)
	{
		return textEditor != null
			&& GodotObject.IsInstanceValid(textEditor)
			&& !textEditor.IsQueuedForDeletion();
	}

	private static int GetEditorLineLength(TextEdit textEditor, int line)
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

	private static int ClampEditorViewStateValue(int value, int min, int max)
	{
		if (max < min)
			return min;

		return Math.Min(Math.Max(value, min), max);
	}
}
#endif
