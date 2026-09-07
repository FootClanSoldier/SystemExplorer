#if TOOLS
using Godot;
using System;
using SystemExplorer.CodeService.Completion;

namespace SystemExplorer.CodeService.Autocomplete;

internal readonly record struct AutocompleteResolvedEditApplyResult(
	bool SourceApplied,
	bool CaretRestored,
	string Detail);

internal sealed class AutocompleteResolvedEditApplier
{
	internal AutocompleteResolvedEditApplyResult TryApply(
		CodeEdit codeEdit,
		CodeServiceCompletionTextEdit edit)
	{
		if (!IsValidGodotObject(codeEdit))
			return new(false, false, "Resolved edit requires a valid CodeEdit.");
		if (edit == null || string.IsNullOrEmpty(edit.NewText))
			return new(false, false, "Resolved edit newText is empty.");

		int lineCount;
		try { lineCount = codeEdit.GetLineCount(); }
		catch (Exception exception) { return new(false, false, "Could not read CodeEdit line count: " + ToSingleLine(exception.Message)); }

		CodeServiceCompletionTextPosition start = edit.Range.Start;
		CodeServiceCompletionTextPosition end = edit.Range.End;
		if (start.Line < 0 || end.Line < 0 || start.Line >= lineCount || end.Line >= lineCount)
			return new(false, false, "Resolved edit line range is outside the current CodeEdit buffer.");

		string startLineText;
		string endLineText;
		try
		{
			startLineText = codeEdit.GetLine(start.Line) ?? "";
			endLineText = codeEdit.GetLine(end.Line) ?? "";
		}
		catch (Exception exception)
		{
			return new(false, false, "Could not read resolved edit boundary lines: " + ToSingleLine(exception.Message));
		}

		if (!AutocompleteTextPositionConverter.TryUtf16IndexToGodotColumn(startLineText, start.Character, out int startColumn)
			|| !AutocompleteTextPositionConverter.TryUtf16IndexToGodotColumn(endLineText, end.Character, out int endColumn))
		{
			return new(false, false, "Resolved edit UTF-16 boundary is not a valid Godot character-column boundary.");
		}
		if (start.Line > end.Line || (start.Line == end.Line && startColumn > endColumn))
			return new(false, false, "Resolved edit range is not ordered after UTF-16 conversion.");
		if (!AutocompleteTextPositionConverter.TryComputeInsertedTextEnd(start.Line, startColumn, edit.NewText, out int caretLine, out int caretColumn))
			return new(false, false, "Resolved edit newText contains malformed UTF-16.");

		bool complexOperationStarted = false;
		try
		{
			codeEdit.BeginComplexOperation();
			complexOperationStarted = true;
			codeEdit.RemoveText(start.Line, startColumn, end.Line, endColumn);
			codeEdit.InsertText(edit.NewText, start.Line, startColumn);
		}
		catch (Exception exception)
		{
			return new(false, false, "Resolved edit source mutation failed: " + ToSingleLine(exception.Message));
		}
		finally
		{
			if (complexOperationStarted)
			{
				try { codeEdit.EndComplexOperation(); } catch { }
			}
		}

		try
		{
			codeEdit.SetCaretLine(caretLine, false, true, -1, 0);
			codeEdit.SetCaretColumn(caretColumn, false, 0);
			return new(true, true, "");
		}
		catch (Exception exception)
		{
			return new(true, false, "Source edit applied but caret restoration failed: " + ToSingleLine(exception.Message));
		}
	}

	private static string ToSingleLine(string value) => (value ?? "").Replace('\r', ' ').Replace('\n', ' ');
	private static bool IsValidGodotObject(GodotObject source) => source != null && GodotObject.IsInstanceValid(source);
}
#endif
