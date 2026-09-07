#if TOOLS
using Godot;
using System;

namespace SystemExplorer.CodeService.Autocomplete;

internal readonly record struct AutocompletePrefixCapture(
	string Prefix,
	int Line,
	int GodotCaretColumn,
	int LspCharacter,
	int PrefixStartColumn
);

internal sealed class AutocompletePrefixExtractor
{
	internal bool TryExtract(CodeEdit codeEdit, out AutocompletePrefixCapture capture)
	{
		capture = default;

		if (!IsValidGodotObject(codeEdit) || codeEdit.HasSelection(0))
			return false;

		int lineIndex = codeEdit.GetCaretLine();
		int godotCaretColumn = codeEdit.GetCaretColumn();
		int lineCount = codeEdit.GetLineCount();

		if (lineIndex < 0 || lineIndex >= lineCount || godotCaretColumn < 0)
			return false;

		string line = codeEdit.GetLine(lineIndex) ?? "";
		if (!AutocompleteTextPositionConverter.TryGodotColumnToUtf16Index(line, godotCaretColumn, out int caretUtf16Index))
			return false;

		int prefixStartUtf16 = caretUtf16Index;
		int prefixStartColumn = godotCaretColumn;
		while (prefixStartUtf16 > 0)
		{
			if (!TryGetPreviousScalarStart(line, prefixStartUtf16, out int scalarStart))
				return false;
			if (!IsIdentifierScalar(line, scalarStart))
				break;

			prefixStartUtf16 = scalarStart;
			prefixStartColumn--;
		}

		capture = new AutocompletePrefixCapture(
			line.Substring(prefixStartUtf16, caretUtf16Index - prefixStartUtf16),
			lineIndex,
			godotCaretColumn,
			caretUtf16Index,
			prefixStartColumn
		);
		return true;
	}

	private static bool TryGetPreviousScalarStart(
		string line,
		int exclusiveUtf16Index,
		out int scalarStart
	)
	{
		scalarStart = exclusiveUtf16Index - 1;
		if (scalarStart < 0)
			return false;

		char current = line[scalarStart];
		if (char.IsLowSurrogate(current))
		{
			if (scalarStart == 0 || !char.IsHighSurrogate(line[scalarStart - 1]))
				return false;
			scalarStart--;
			return true;
		}
		return !char.IsHighSurrogate(current);
	}

	private static bool IsIdentifierScalar(string line, int scalarStart)
	{
		if (line[scalarStart] == '_')
			return true;
		return char.IsLetter(line, scalarStart) || char.IsDigit(line, scalarStart);
	}

	private static bool IsValidGodotObject(GodotObject source)
	{
		return source != null && GodotObject.IsInstanceValid(source);
	}
}
#endif
