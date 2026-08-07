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
		prefix = "";
		caretLine = -1;
		caretColumn = -1;

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
			return false;

		prefix = line.Substring(prefixStart, columnIndex - prefixStart);
		caretLine = lineIndex;
		caretColumn = columnIndex;
		return true;
	}

	private static bool IsValidGodotObject(GodotObject source)
	{
		return source != null && GodotObject.IsInstanceValid(source);
	}
}
#endif
