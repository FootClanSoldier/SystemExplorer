#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

public partial class SystemExplorerPlugin
{
	// Presence invariant: systems.json/_systems is a cheap presentation index only.
	// Resources/Notes remains authority for actual Note existence/content.
	private bool HasSystemNotePresence(string systemName)
	{
		return !string.IsNullOrWhiteSpace(systemName)
			&& _systems.TryGetValue(systemName, out List<string> entries)
			&& entries != null
			&& entries.Any(IsSystemNotePresenceEntry);
	}

	private bool HasFolderNotePresence(string systemName, string folderPath)
	{
		if (
			string.IsNullOrWhiteSpace(systemName)
			|| string.IsNullOrWhiteSpace(folderPath)
			|| !_systems.TryGetValue(systemName, out List<string> entries)
			|| entries == null
		)
		{
			return false;
		}

		return entries.Any(entry =>
			entry.StartsWith("folder::", StringComparison.Ordinal)
			&& string.Equals(
				GetFolderPathFromFolderEntry(entry),
				folderPath,
				StringComparison.Ordinal
			)
			&& HasFolderNotePresenceMarker(entry)
		);
	}

	private bool HasNotePresenceForMetadata(string metadata)
	{
		if (string.IsNullOrWhiteSpace(metadata))
			return false;

		if (metadata.StartsWith("system::", StringComparison.Ordinal))
			return HasSystemNotePresence(GetSystemNameFromMetadata(metadata));

		if (metadata.StartsWith("folder::", StringComparison.Ordinal))
		{
			return HasFolderNotePresence(
				GetSystemNameFromMetadata(metadata),
				GetFolderPathFromMetadata(metadata)
			);
		}

		return false;
	}

	private HashSet<string> GetNotedFolderPathsForSystem(string systemName)
	{
		HashSet<string> notedFolderPaths = new(StringComparer.Ordinal);

		if (
			string.IsNullOrWhiteSpace(systemName)
			|| !_systems.TryGetValue(systemName, out List<string> entries)
			|| entries == null
		)
		{
			return notedFolderPaths;
		}

		foreach (string entry in entries)
		{
			if (!HasFolderNotePresenceMarker(entry))
				continue;

			string folderPath = GetFolderPathFromFolderEntry(entry);

			if (!string.IsNullOrWhiteSpace(folderPath))
				notedFolderPaths.Add(folderPath);
		}

		return notedFolderPaths;
	}

	private bool TrySynchronizeNotePresenceForMetadata(
		string metadata,
		bool hasNote,
		out bool changed
	)
	{
		changed = false;

		bool isSystemTarget = metadata?.StartsWith("system::", StringComparison.Ordinal) == true;
		bool isFolderTarget = metadata?.StartsWith("folder::", StringComparison.Ordinal) == true;

		if (!isSystemTarget && !isFolderTarget)
		{
			ReportTreeOperationFailureOrWarning(
				"System Explorer saved the Note, but could not update its tree presence metadata.",
				$"Operation='Update Note Presence', Metadata='{metadata ?? ""}', Reason='Unsupported metadata target.'"
			);
			return false;
		}

		if (!EnsureSystemsLoadedForTreeOperation("Update Note Presence"))
		{
			ReportTreeOperationFailureOrWarning(
				"System Explorer saved the Note, but could not update its tree presence metadata.",
				$"Operation='Update Note Presence', Metadata='{metadata}', Reason='Systems metadata was unavailable.'"
			);
			return false;
		}

		string systemName = GetSystemNameFromMetadata(metadata);

		if (
			string.IsNullOrWhiteSpace(systemName)
			|| !_systems.TryGetValue(systemName, out List<string> entries)
			|| entries == null
		)
		{
			ReportTreeOperationFailureOrWarning(
				"System Explorer saved the Note, but could not update its tree presence metadata.",
				$"Operation='Update Note Presence', Metadata='{metadata}', System='{systemName}', Reason='System entry was unavailable.'"
			);
			return false;
		}

		SystemsAndFolderBindingsSnapshot snapshot = null;

		if (isSystemTarget)
		{
			int markerCount = entries.Count(IsSystemNotePresenceEntry);

			if ((hasNote && markerCount == 1) || (!hasNote && markerCount == 0))
				return true;

			snapshot = CaptureSystemsAndFolderBindingsSnapshot();
			entries.RemoveAll(IsSystemNotePresenceEntry);

			if (hasNote)
			{
				int systemLockIndex = entries.FindIndex(IsSystemLockEntry);
				int insertIndex = systemLockIndex >= 0 ? systemLockIndex + 1 : 0;
				entries.Insert(insertIndex, SystemNotePresenceEntry);
			}
		}
		else
		{
			string folderPath = GetFolderPathFromMetadata(metadata);

			if (string.IsNullOrWhiteSpace(folderPath))
			{
				ReportTreeOperationFailureOrWarning(
					"System Explorer saved the Note, but could not update its tree presence metadata.",
					$"Operation='Update Note Presence', Metadata='{metadata}', Reason='Folder path was unavailable.'"
				);
				return false;
			}

			List<int> matchingIndexes = new();
			bool locked = false;

			for (int index = 0; index < entries.Count; index++)
			{
				string entry = entries[index];

				if (
					!entry.StartsWith("folder::", StringComparison.Ordinal)
					|| !string.Equals(
						GetFolderPathFromFolderEntry(entry),
						folderPath,
						StringComparison.Ordinal
					)
				)
				{
					continue;
				}

				matchingIndexes.Add(index);
				locked |= IsEntryLocked(entry);
			}

			if (matchingIndexes.Count == 0)
			{
				ReportTreeOperationFailureOrWarning(
					"System Explorer saved the Note, but could not update its tree presence metadata.",
					$"Operation='Update Note Presence', Metadata='{metadata}', System='{systemName}', Folder='{folderPath}', Reason='Exact folder entry was unavailable.'"
				);
				return false;
			}

			string canonicalEntry = BuildFolderEntry(folderPath, locked, hasNote);

			if (
				matchingIndexes.Count == 1
				&& string.Equals(
					entries[matchingIndexes[0]],
					canonicalEntry,
					StringComparison.Ordinal
				)
			)
			{
				return true;
			}

			snapshot = CaptureSystemsAndFolderBindingsSnapshot();
			int insertionIndex = matchingIndexes[0];

			for (int index = matchingIndexes.Count - 1; index >= 0; index--)
				entries.RemoveAt(matchingIndexes[index]);

			entries.Insert(Math.Min(insertionIndex, entries.Count), canonicalEntry);
		}

		if (
			!TryPersistReversibleSystemsAndFolderBindingsMutation(
				snapshot,
				systemsChanged: true,
				folderBindingsChanged: false,
				operationName: "Update Note Presence"
			)
		)
		{
			return false;
		}

		changed = true;
		RefreshNotePresenceTreeRow(metadata);
		return true;
	}

	private void RefreshNotePresenceTreeRow(string metadata)
	{
		if (
			string.IsNullOrWhiteSpace(metadata)
			|| _tree == null
			|| !GodotObject.IsInstanceValid(_tree)
			|| !_tree.IsInsideTree()
		)
		{
			return;
		}

		TreeItem root = _tree.GetRoot();

		if (root == null)
			return;

		TreeItem item = FindTreeItemByMetadataWithinSubtree(root, metadata);

		if (item == null)
		{
			DebugLogger.LogOperation(
				"Note presence tree refresh skipped: target row unavailable",
				metadata
			);
			return;
		}

		UpdateTreeItemLockIconVisibility(item);
	}
}
#endif
