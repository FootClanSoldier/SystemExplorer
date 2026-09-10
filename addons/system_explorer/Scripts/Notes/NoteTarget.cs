#if TOOLS
using System;

namespace SystemExplorer.Notes;

internal sealed class NoteTarget
{
	internal string SystemName { get; }
	internal string FolderPath { get; }
	internal bool IsFolder => FolderPath.Length != 0;

	internal NoteTarget(string systemName, string folderPath = "")
	{
		SystemName = systemName ?? throw new ArgumentNullException(nameof(systemName));
		FolderPath = folderPath ?? throw new ArgumentNullException(nameof(folderPath));
	}
}
#endif
