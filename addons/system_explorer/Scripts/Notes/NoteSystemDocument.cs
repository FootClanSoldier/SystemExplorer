#if TOOLS
using System;
using System.Collections.Generic;

namespace SystemExplorer.Notes;

internal sealed class NoteSystemDocument
{
	internal string SystemName { get; }
	internal string Note { get; set; } = "";
	internal Dictionary<string, string> Folders { get; } =
		new(StringComparer.Ordinal);
	internal NoteViewState SystemViewState { get; set; }
	internal Dictionary<string, NoteViewState> FolderViewStates { get; } =
		new(StringComparer.Ordinal);

	internal NoteSystemDocument(string systemName)
	{
		SystemName = systemName;
	}
}
#endif
