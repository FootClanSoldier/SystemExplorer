#if TOOLS
using Godot;

namespace SystemExplorer.Autocomplete;

internal sealed class AutocompletePrefixExtractor
{
	internal bool TryExtract(CodeEdit codeEdit, out string prefix)
	{
		return TryExtract(
			codeEdit,
			out prefix,
			out _,
			out _,
			out _,
			out _
		);
	}

	internal bool TryExtract(
		CodeEdit codeEdit,
		out string prefix,
		out int caretLine,
		out int caretColumn
	)
	{
		return TryExtract(
			codeEdit,
			out prefix,
			out caretLine,
			out caretColumn,
			out _,
			out _
		);
	}

	internal bool TryExtract(
		CodeEdit codeEdit,
		out string prefix,
		out int caretLine,
		out int caretColumn,
		out AutocompleteRequestKind kind,
		out int prefixStartColumn
	)
	{
		prefix = "";
		caretLine = -1;
		caretColumn = -1;
		kind = AutocompleteRequestKind.Identifier;
		prefixStartColumn = -1;

		if (!IsValidGodotObject(codeEdit) || codeEdit.HasSelection(0))
			return false;

		int lineIndex = codeEdit.GetCaretLine();
		int columnIndex = codeEdit.GetCaretColumn();
		int lineCount = codeEdit.GetLineCount();

		if (lineIndex < 0 || lineIndex >= lineCount)
			return false;

		string line = codeEdit.GetLine(lineIndex) ?? "";

		if (columnIndex < 0 || columnIndex > line.Length)
			return false;

		int prefixStart = columnIndex;

		while (prefixStart > 0)
		{
			char character = line[prefixStart - 1];

			if (!char.IsLetterOrDigit(character) && character != '_')
				break;

			prefixStart--;
		}

		if (prefixStart == columnIndex)
		{
			if (columnIndex <= 0 || line[columnIndex - 1] != '.')
				return false;

			caretLine = lineIndex;
			caretColumn = columnIndex;
			prefixStartColumn = columnIndex;
			kind = ClassifyRequestKind(line, columnIndex);
			return true;
		}

		prefix = line.Substring(prefixStart, columnIndex - prefixStart);
		caretLine = lineIndex;
		caretColumn = columnIndex;
		prefixStartColumn = prefixStart;
		kind = ClassifyRequestKind(line, prefixStart);
		return true;
	}

	private static AutocompleteRequestKind ClassifyRequestKind(
		string line,
		int prefixStart
	)
	{
		if (
			string.IsNullOrEmpty(line)
			|| prefixStart <= 0
			|| prefixStart > line.Length
			|| line[prefixStart - 1] != '.'
		)
		{
			return AutocompleteRequestKind.Identifier;
		}

		if (prefixStart >= 2 && line[prefixStart - 2] == '?')
			return AutocompleteRequestKind.Unsupported;

		return AutocompleteRequestKind.MemberAccess;
	}

	private static bool IsValidGodotObject(GodotObject source)
	{
		return source != null && GodotObject.IsInstanceValid(source);
	}
}
#endif
