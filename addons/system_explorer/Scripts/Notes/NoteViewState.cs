#if TOOLS
using System;

namespace SystemExplorer.Notes;

internal sealed class NoteViewState
{
	internal int CaretLine { get; }
	internal int CaretColumn { get; }
	internal double? ScrollVertical { get; }

	internal NoteViewState(int caretLine, int caretColumn)
		: this(caretLine, caretColumn, null)
	{
	}

	internal NoteViewState(
		int caretLine,
		int caretColumn,
		double? scrollVertical
	)
	{
		if (caretLine < 0)
			throw new ArgumentOutOfRangeException(nameof(caretLine));

		if (caretColumn < 0)
			throw new ArgumentOutOfRangeException(nameof(caretColumn));

		if (
			scrollVertical.HasValue
			&& (!double.IsFinite(scrollVertical.Value) || scrollVertical.Value < 0.0)
		)
		{
			throw new ArgumentOutOfRangeException(nameof(scrollVertical));
		}

		CaretLine = caretLine;
		CaretColumn = caretColumn;
		ScrollVertical = scrollVertical;
	}
}
#endif
