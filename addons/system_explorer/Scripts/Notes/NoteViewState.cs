#if TOOLS
using System;

namespace SystemExplorer.Notes;

internal sealed class NoteViewState
{
	internal int CaretLine { get; }
	internal int CaretColumn { get; }

	internal NoteViewState(int caretLine, int caretColumn)
	{
		if (caretLine < 0)
			throw new ArgumentOutOfRangeException(nameof(caretLine));

		if (caretColumn < 0)
			throw new ArgumentOutOfRangeException(nameof(caretColumn));

		CaretLine = caretLine;
		CaretColumn = caretColumn;
	}
}
#endif
