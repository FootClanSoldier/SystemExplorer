#if TOOLS
using Godot;
using System.Collections.Generic;

public partial class SystemExplorerPlugin
{
	#region Context Menu File Manager
	private void ShowPendingItemInFileManager()
	{
		if (string.IsNullOrWhiteSpace(_pendingShowInFileManagerMetadata))
			return;

		if (
			!TryGetFileManagerTargetFromMetadata(
				_pendingShowInFileManagerMetadata,
				out string path,
				out string missingEntry,
				out bool isSceneTarget
			)
		)
			return;

		if (!FileAccess.FileExists(path))
		{
			if (isSceneTarget)
				OpenMissingSceneDialog(missingEntry, path);
			else
				OpenMissingScriptDialog(missingEntry, path);

			return;
		}

		string globalPath = ProjectSettings.GlobalizePath(path);

		if (string.IsNullOrWhiteSpace(globalPath))
		{
			GD.PushWarning($"Could not resolve file path: {path}");
			return;
		}

		OS.ShellShowInFileManager(globalPath, false);
	}

	private bool TryGetFileManagerTargetFromMetadata(
		string metadata,
		out string path,
		out string missingEntry,
		out bool isSceneTarget
	)
	{
		path = "";
		missingEntry = "";
		isSceneTarget = false;

		if (metadata.StartsWith("script::"))
		{
			string entry = GetEntryFromMetadata(metadata);
			path = GetScriptPathFromEntry(entry);
			missingEntry = entry;
			return !string.IsNullOrWhiteSpace(path);
		}

		if (metadata.StartsWith("sceneLink::"))
		{
			string entry = metadata.Substring("sceneLink::".Length);
			path = GetScenePathFromEntry(entry);
			missingEntry = entry;
			isSceneTarget = true;
			return !string.IsNullOrWhiteSpace(path);
		}

		if (metadata.StartsWith("folder::"))
			return TryGetFolderFileManagerTarget(
				metadata,
				out path,
				out missingEntry,
				out isSceneTarget
			);

		return false;
	}

	private bool HasFolderFileManagerTarget(string metadata)
	{
		if (!metadata.StartsWith("folder::"))
			return false;

		string systemName = GetSystemNameFromMetadata(metadata);
		string folderPath = GetFolderPathFromMetadata(metadata);

		if (string.IsNullOrWhiteSpace(systemName) || string.IsNullOrWhiteSpace(folderPath))
			return false;

		if (!_systems.TryGetValue(systemName, out var entries) || entries == null)
			return false;

		string entry = FindFirstFileManagerEntryInFolder(entries, folderPath);

		if (string.IsNullOrWhiteSpace(entry))
			return false;

		string path = IsSceneEntry(entry)
			? GetScenePathFromEntry(entry)
			: GetScriptPathFromEntry(entry);
		return !string.IsNullOrWhiteSpace(path);
	}

	private bool TryGetFolderFileManagerTarget(
		string metadata,
		out string path,
		out string missingEntry,
		out bool isSceneTarget
	)
	{
		path = "";
		missingEntry = "";
		isSceneTarget = false;

		string systemName = GetSystemNameFromMetadata(metadata);
		string folderPath = GetFolderPathFromMetadata(metadata);

		if (string.IsNullOrWhiteSpace(systemName) || string.IsNullOrWhiteSpace(folderPath))
			return false;

		if (!_systems.TryGetValue(systemName, out var entries) || entries == null)
			return false;

		string entry = FindFirstFileManagerEntryInFolder(entries, folderPath);

		if (string.IsNullOrWhiteSpace(entry))
		{
			GD.PushWarning(
				$"System Explorer could not find a script or scene in folder: {folderPath}"
			);
			return false;
		}

		missingEntry = entry;
		isSceneTarget = IsSceneEntry(entry);
		path = isSceneTarget ? GetScenePathFromEntry(entry) : GetScriptPathFromEntry(entry);

		return !string.IsNullOrWhiteSpace(path);
	}

	private static string FindFirstFileManagerEntryInFolder(List<string> entries, string folderPath)
	{
		string entry = FindFirstEntryInFolder(
			entries,
			folderPath,
			includeNestedFolders: false,
			sceneEntriesOnly: false
		);
		entry = string.IsNullOrWhiteSpace(entry)
			? FindFirstEntryInFolder(
				entries,
				folderPath,
				includeNestedFolders: true,
				sceneEntriesOnly: false
			)
			: entry;
		entry = string.IsNullOrWhiteSpace(entry)
			? FindFirstEntryInFolder(
				entries,
				folderPath,
				includeNestedFolders: false,
				sceneEntriesOnly: true
			)
			: entry;
		entry = string.IsNullOrWhiteSpace(entry)
			? FindFirstEntryInFolder(
				entries,
				folderPath,
				includeNestedFolders: true,
				sceneEntriesOnly: true
			)
			: entry;

		return entry;
	}

	private static string FindFirstEntryInFolder(
		List<string> entries,
		string folderPath,
		bool includeNestedFolders,
		bool sceneEntriesOnly
	)
	{
		foreach (string entry in entries)
		{
			if (!IsScriptOrSceneEntry(entry))
				continue;

			bool isSceneEntry = IsSceneEntry(entry);

			if (sceneEntriesOnly != isSceneEntry)
				continue;

			string entryFolderPath = GetFolderPathFromEntry(entry);

			if (entryFolderPath == folderPath)
				return entry;

			if (
				includeNestedFolders
				&& entryFolderPath.StartsWith($"{folderPath}/", System.StringComparison.Ordinal)
			)
				return entry;
		}

		return "";
	}
	#endregion
}
#endif
