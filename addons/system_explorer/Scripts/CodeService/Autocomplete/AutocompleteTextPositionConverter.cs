#if TOOLS
using System;

namespace SystemExplorer.CodeService.Autocomplete;

internal static class AutocompleteTextPositionConverter
{
	internal static bool TryGodotColumnToUtf16Index(string line, int godotColumn, out int utf16Index)
	{
		utf16Index = 0;
		if (line == null || godotColumn < 0)
			return false;

		int currentColumn = 0;
		while (utf16Index < line.Length && currentColumn < godotColumn)
		{
			if (!TryAdvanceScalar(line, ref utf16Index))
				return false;
			currentColumn++;
		}
		if (currentColumn != godotColumn)
			return false;
		return ValidateSuffix(line, utf16Index);
	}

	internal static bool TryUtf16IndexToGodotColumn(string line, int utf16Index, out int godotColumn)
	{
		godotColumn = 0;
		if (line == null || utf16Index < 0 || utf16Index > line.Length)
			return false;

		int index = 0;
		while (index < line.Length)
		{
			if (index == utf16Index)
			{
				return ValidateSuffix(line, index);
			}

			int scalarStart = index;
			if (!TryAdvanceScalar(line, ref index))
				return false;
			if (utf16Index > scalarStart && utf16Index < index)
				return false;
			godotColumn++;
		}
		return index == utf16Index;
	}

	internal static bool TryComputeInsertedTextEnd(
		int startLine,
		int startGodotColumn,
		string text,
		out int endLine,
		out int endGodotColumn)
	{
		endLine = startLine;
		endGodotColumn = startGodotColumn;
		if (startLine < 0 || startGodotColumn < 0 || text == null)
			return false;

		int index = 0;
		while (index < text.Length)
		{
			char current = text[index];
			if (current == '\r' || current == '\n')
			{
				if (current == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
					index += 2;
				else
					index++;
				endLine++;
				endGodotColumn = 0;
				continue;
			}
			if (!TryAdvanceScalar(text, ref index))
				return false;
			endGodotColumn++;
		}
		return true;
	}

	private static bool ValidateSuffix(string text, int startIndex)
	{
		int index = startIndex;
		while (index < text.Length)
		{
			if (!TryAdvanceScalar(text, ref index))
				return false;
		}
		return true;
	}

	private static bool TryAdvanceScalar(string text, ref int index)
	{
		if (index < 0 || index >= text.Length)
			return false;
		char current = text[index];
		if (char.IsHighSurrogate(current))
		{
			if (index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1]))
				return false;
			index += 2;
			return true;
		}
		if (char.IsLowSurrogate(current))
			return false;
		index++;
		return true;
	}
}
#endif
