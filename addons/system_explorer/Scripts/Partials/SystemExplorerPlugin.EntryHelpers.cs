#if TOOLS
using Godot;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SystemExplorer.EditorIntegration.ScriptEditing;

public partial class SystemExplorerPlugin
{
	private enum FolderMutationResult
	{
		Success,
		NameConflict,
		Failed,
	}

	#region Selection and Entry Mutation Helpers
	private bool DoesAnySystemContainEntry(string entry)
	{
		return _systems.Values.Any(entries => entries.Contains(entry));
	}

	private void UpdateSelectedScriptEntryFromFilter(string oldEntry, string newEntry)
	{
		if (!_isFilteringScripts)
			return;

		if (_selectedScriptEntryFromFilter != oldEntry)
			return;

		_selectedScriptEntryFromFilter = newEntry;
	}

	private void ClearSelectedScriptEntryFromFilter(string removedEntry)
	{
		if (!_isFilteringScripts)
			return;

		if (_selectedScriptEntryFromFilter == removedEntry)
			_selectedScriptEntryFromFilter = "";
	}

	private bool EnsureEntryAvailableForReversibleMutation(
		string entry,
		string reason,
		string systemName = ""
	)
	{
		if (DoesEntryExistForReversibleMutation(entry, systemName))
			return true;

		if (!TryRecoverSystemsFromDisk(reason, systemName))
			return false;

		return DoesEntryExistForReversibleMutation(entry, systemName);
	}

	private bool DoesEntryExistForReversibleMutation(string entry, string systemName)
	{
		if (string.IsNullOrWhiteSpace(entry))
			return false;

		if (!string.IsNullOrWhiteSpace(systemName))
		{
			return _systems.TryGetValue(systemName, out List<string> systemEntries)
				&& systemEntries != null
				&& FindEntryIndex(systemEntries, entry) >= 0;
		}

		return _systems.Values.Any(entries =>
			entries != null && FindEntryIndex(entries, entry) >= 0
		);
	}

	private bool RemoveEntry(string entry)
	{
		if (!EnsureSystemsLoadedForTreeOperation("Remove Entry"))
			return false;

		foreach (string systemName in _systems.Keys.ToList())
		{
			if (_systems[systemName].Remove(entry))
			{
				ClearSelectedScriptEntryFromFilter(entry);
				return true;
			}
		}

		if (TryRecoverSystemsFromDisk("Remove Entry"))
		{
			foreach (string systemName in _systems.Keys.ToList())
			{
				if (_systems[systemName].Remove(entry))
				{
					ClearSelectedScriptEntryFromFilter(entry);
					return true;
				}
			}
		}

		return false;
	}

	private bool RemoveFolder(string metadata)
	{
		if (!EnsureSystemsLoadedForTreeOperation("Remove Folder"))
			return false;

		string[] parts = metadata.Split("::");

		if (parts.Length < 3)
			return false;

		string systemName = parts[1];
		string folderPath = parts[2];

		if (!EnsureSystemAvailable(systemName, "Remove Folder"))
			return false;

		int removedFolderEntries = _systems[systemName]
			.RemoveAll(entry =>
				entry.StartsWith("folder::") && GetFolderPathFromFolderEntry(entry) == folderPath
			);

		int removedChildEntries = _systems[systemName]
			.RemoveAll(entry =>
				entry.StartsWith($"{folderPath}|")
				|| entry.StartsWith($"{folderPath}/")
				|| entry.StartsWith($"folder::{folderPath}/")
			);

		return removedFolderEntries > 0 || removedChildEntries > 0;
	}

	#endregion

	#region Metadata and Entry Path Helpers
	private string GetSelectedSystemName()
	{
		TreeItem item = _tree.GetSelected();

		while (item != null)
		{
			string metadata = item.GetMetadata(0).AsString();

			if (metadata.StartsWith("system::"))
				return metadata.Replace("system::", "");

			if (metadata.StartsWith("folder::"))
				return metadata.Split("::")[1];

			if (metadata.StartsWith("script::"))
			{
				string entry = GetEntryFromMetadata(metadata);
				string systemName = FindSystemNameForEntry(entry);

				if (!string.IsNullOrWhiteSpace(systemName))
					return systemName;
			}

			if (metadata.StartsWith("sceneLink::"))
			{
				string entry = metadata.Substring("sceneLink::".Length);
				string systemName = FindSystemNameForEntry(entry);

				if (!string.IsNullOrWhiteSpace(systemName))
					return systemName;
			}

			item = item.GetParent();
		}

		return "";
	}

	private string GetSelectedFolderPath()
	{
		TreeItem item = _tree.GetSelected();

		while (item != null)
		{
			string metadata = item.GetMetadata(0).AsString();

			if (metadata.StartsWith("folder::"))
				return metadata.Split("::")[2];

			if (metadata.StartsWith("script::"))
			{
				string entry = GetEntryFromMetadata(metadata);
				return GetFolderPathFromEntry(entry);
			}

			if (metadata.StartsWith("sceneLink::"))
			{
				string entry = metadata.Substring("sceneLink::".Length);
				return GetFolderPathFromEntry(entry);
			}

			item = item.GetParent();
		}

		return "";
	}

	private static string GetSystemNameFromMetadata(string metadata)
	{
		if (metadata.StartsWith("system::"))
			return metadata.Replace("system::", "");

		if (metadata.StartsWith("folder::"))
		{
			string[] parts = metadata.Split("::");
			return parts.Length >= 2 ? parts[1] : "";
		}

		return "";
	}

	private static string GetFolderPathFromMetadata(string metadata)
	{
		if (!metadata.StartsWith("folder::"))
			return "";

		string[] parts = metadata.Split("::");
		return parts.Length >= 3 ? parts[2] : "";
	}

	private static string BuildFolderEntry(string folderPath, bool locked = false)
	{
		string entry = $"folder::{folderPath}";
		return locked ? AddLockMarker(entry) : entry;
	}

	private static string GetFolderPathFromFolderEntry(string entry)
	{
		if (string.IsNullOrWhiteSpace(entry) || !entry.StartsWith("folder::"))
			return "";

		return RemoveLockMarker(entry).Substring("folder::".Length);
	}

	private bool DoesFolderPathExistInSystem(
		string systemName,
		string folderPath,
		string excludedFolderPath = ""
	)
	{
		if (
			string.IsNullOrWhiteSpace(systemName)
			|| string.IsNullOrWhiteSpace(folderPath)
			|| !_systems.ContainsKey(systemName)
		)
			return false;

		foreach (string entry in _systems[systemName])
		{
			if (!entry.StartsWith("folder::", System.StringComparison.Ordinal))
				continue;

			string existingFolderPath = GetFolderPathFromFolderEntry(entry);

			if (
				!string.IsNullOrWhiteSpace(excludedFolderPath)
				&& string.Equals(
					existingFolderPath,
					excludedFolderPath,
					System.StringComparison.Ordinal
				)
			)
				continue;

			if (
				string.Equals(
					existingFolderPath,
					folderPath,
					System.StringComparison.Ordinal
				)
			)
				return true;
		}

		return false;
	}

	private static string GetFolderPathFromEntry(string entry)
	{
		string entryWithoutLinkedScene = GetEntryWithoutLinkedScene(entry);
		return entryWithoutLinkedScene.Contains("|") ? entryWithoutLinkedScene.Split("|")[0] : "";
	}

	private static bool ContainsReservedSystemNameSeparator(string value)
	{
		return !string.IsNullOrEmpty(value)
			&& value.Contains("::", System.StringComparison.Ordinal);
	}

	private static bool ContainsReservedVirtualFolderSeparator(string value)
	{
		return !string.IsNullOrEmpty(value)
			&& (
				value.Contains("::", System.StringComparison.Ordinal)
				|| value.Contains('|')
			);
	}

	private static bool IsPrimaryResourcePathRepresentable(string resourcePath)
	{
		return !string.IsNullOrWhiteSpace(resourcePath) && !resourcePath.Contains('|');
	}

	private static bool TryGetUnambiguousPhysicalResourcePath(
		string entry,
		bool expectScene,
		out string resourcePath
	)
	{
		resourcePath = "";

		if (string.IsNullOrWhiteSpace(entry))
			return false;

		string primaryEntry = RemoveLockMarker(entry);
		int linkedSceneMarkerIndex = primaryEntry.IndexOf(
			LinkedSceneMarker,
			System.StringComparison.Ordinal
		);

		if (linkedSceneMarkerIndex >= 0)
			primaryEntry = primaryEntry.Substring(0, linkedSceneMarkerIndex);

		if (string.IsNullOrWhiteSpace(primaryEntry))
			return false;

		int firstFolderSeparatorIndex = primaryEntry.IndexOf('|');
		string payload;

		if (firstFolderSeparatorIndex < 0)
		{
			payload = primaryEntry;
		}
		else
		{
			if (primaryEntry.IndexOf('|', firstFolderSeparatorIndex + 1) >= 0)
				return false;

			string folderPath = primaryEntry.Substring(0, firstFolderSeparatorIndex);
			payload = primaryEntry.Substring(firstFolderSeparatorIndex + 1);

			if (
				string.IsNullOrWhiteSpace(folderPath)
				|| ContainsReservedSystemNameSeparator(folderPath)
				|| string.IsNullOrWhiteSpace(payload)
			)
			{
				return false;
			}
		}

		if (expectScene)
		{
			if (!payload.StartsWith(SceneEntryMarker, System.StringComparison.Ordinal))
				return false;

			resourcePath = payload.Substring(SceneEntryMarker.Length);
		}
		else
		{
			if (payload.StartsWith(SceneEntryMarker, System.StringComparison.Ordinal))
				return false;

			resourcePath = payload;
		}

		if (
			!resourcePath.StartsWith("res://", System.StringComparison.Ordinal)
			|| resourcePath.Contains('|')
		)
		{
			resourcePath = "";
			return false;
		}

		return true;
	}

	private static string GetScriptPathFromEntry(string entry)
	{
		string entryWithoutLinkedScene = GetEntryWithoutLinkedScene(entry);
		return entryWithoutLinkedScene.Contains("|")
			? entryWithoutLinkedScene.Split("|")[1]
			: entryWithoutLinkedScene;
	}

	private static string GetScenePathFromEntry(string entry)
	{
		string entryWithoutLinkedScene = GetEntryWithoutLinkedScene(entry);
		string pathPart = entryWithoutLinkedScene.Contains("|")
			? entryWithoutLinkedScene.Split("|")[1]
			: entryWithoutLinkedScene;

		return pathPart.StartsWith(SceneEntryMarker)
			? pathPart.Substring(SceneEntryMarker.Length)
			: pathPart;
	}

	private static string GetPathFromEntry(string entry)
	{
		return IsSceneEntry(entry) ? GetScenePathFromEntry(entry) : GetScriptPathFromEntry(entry);
	}

	private static bool IsSceneEntry(string entry)
	{
		string entryWithoutLinkedScene = GetEntryWithoutLinkedScene(entry);
		string pathPart = entryWithoutLinkedScene.Contains("|")
			? entryWithoutLinkedScene.Split("|")[1]
			: entryWithoutLinkedScene;

		return pathPart.StartsWith(SceneEntryMarker);
	}

	private static string BuildSceneEntry(string folderPath, string scenePath, bool locked = false)
	{
		string sceneEntry = $"{SceneEntryMarker}{scenePath}";
		string entry = string.IsNullOrWhiteSpace(folderPath)
			? sceneEntry
			: $"{folderPath}|{sceneEntry}";
		return locked ? AddLockMarker(entry) : entry;
	}

	private static string GetLinkedScenePathFromEntry(string entry)
	{
		string entryWithoutLock = RemoveLockMarker(entry);

		if (
			string.IsNullOrWhiteSpace(entryWithoutLock)
			|| !entryWithoutLock.Contains(LinkedSceneMarker)
		)
			return "";

		string[] parts = entryWithoutLock.Split(LinkedSceneMarker, System.StringSplitOptions.None);
		return parts.Length >= 2 ? parts[1] : "";
	}

	private static string GetEntryWithoutLinkedScene(string entry)
	{
		string entryWithoutLock = RemoveLockMarker(entry);

		if (
			string.IsNullOrWhiteSpace(entryWithoutLock)
			|| !entryWithoutLock.Contains(LinkedSceneMarker)
		)
			return entryWithoutLock;

		return entryWithoutLock.Split(LinkedSceneMarker, System.StringSplitOptions.None)[0];
	}

	private static string BuildScriptEntry(
		string folderPath,
		string scriptPath,
		string linkedScenePath = "",
		bool locked = false
	)
	{
		string entry = string.IsNullOrWhiteSpace(folderPath)
			? scriptPath
			: $"{folderPath}|{scriptPath}";

		if (!string.IsNullOrWhiteSpace(linkedScenePath))
			entry += $"{LinkedSceneMarker}{linkedScenePath}";

		return locked ? AddLockMarker(entry) : entry;
	}

	#endregion

	#region Entry Normalization
	private void NormalizeAllSystemEntries()
	{
		foreach (string systemName in _systems.Keys.ToList())
			_systems[systemName] = NormalizeSystemEntries(_systems[systemName]);
	}

	private static List<string> NormalizeSystemEntries(List<string> entries)
	{
		if (entries == null)
			return new List<string>();

		List<string> explicitFolders = entries
			.Where(entry => entry.StartsWith("folder::"))
			.GroupBy(GetFolderPathFromFolderEntry)
			.Select(group => BuildFolderEntry(group.Key, group.Any(IsEntryLocked)))
			.ToList();

		List<string> systemMarkers = entries.Where(IsSystemLockEntry).Distinct().ToList();

		List<string> scripts = entries
			.Where(IsScriptOrSceneEntry)
			.Select(NormalizeScriptOrSceneResourcePaths)
			.Distinct()
			.ToList();

		List<string> requiredFolders = new();

		foreach (string scriptEntry in scripts)
		{
			string folderPath = GetFolderPathFromEntry(scriptEntry);

			if (string.IsNullOrWhiteSpace(folderPath))
				continue;

			foreach (string folderEntry in GetFolderEntriesForPath(folderPath))
			{
				if (!requiredFolders.Contains(folderEntry))
					requiredFolders.Add(folderEntry);
			}
		}

		List<string> normalized = new();

		normalized.AddRange(systemMarkers);

		foreach (string folderEntry in explicitFolders)
		{
			if (
				!normalized.Any(existing =>
					GetFolderPathFromFolderEntry(existing)
					== GetFolderPathFromFolderEntry(folderEntry)
				)
			)
				normalized.Add(folderEntry);
		}

		foreach (string folderEntry in requiredFolders)
		{
			if (
				!normalized.Any(existing =>
					GetFolderPathFromFolderEntry(existing)
					== GetFolderPathFromFolderEntry(folderEntry)
				)
			)
				normalized.Add(folderEntry);
		}

		normalized.AddRange(scripts);

		return normalized;
	}

	private static string NormalizeScriptOrSceneResourcePaths(string entry)
	{
		if (string.IsNullOrWhiteSpace(entry) || !IsScriptOrSceneEntry(entry))
			return entry;

		bool isSceneEntry = IsSceneEntry(entry);
		if (
			!TryGetUnambiguousPhysicalResourcePath(
				entry,
				isSceneEntry,
				out string primaryResourcePath
			)
		)
		{
			return entry;
		}

		string folderPath = GetFolderPathFromEntry(entry);
		bool isLocked = IsEntryLocked(entry);

		if (isSceneEntry)
		{
			string reconstructedEntry = BuildSceneEntry(
				folderPath,
				primaryResourcePath,
				isLocked
			);

			if (!string.Equals(reconstructedEntry, entry, System.StringComparison.Ordinal))
				return entry;

			string normalizedScenePath = ScriptPathUtility.Normalize(primaryResourcePath);
			return string.Equals(
				normalizedScenePath,
				primaryResourcePath,
				System.StringComparison.Ordinal
			)
				? entry
				: BuildSceneEntry(folderPath, normalizedScenePath, isLocked);
		}

		string linkedScenePath = GetLinkedScenePathFromEntry(entry);
		string reconstructedScriptEntry = BuildScriptEntry(
			folderPath,
			primaryResourcePath,
			linkedScenePath,
			isLocked
		);

		if (!string.Equals(reconstructedScriptEntry, entry, System.StringComparison.Ordinal))
			return entry;

		string normalizedScriptPath = ScriptPathUtility.Normalize(primaryResourcePath);
		string normalizedLinkedScenePath = linkedScenePath;

		if (
			linkedScenePath.StartsWith("res://", System.StringComparison.Ordinal)
			&& IsPrimaryResourcePathRepresentable(linkedScenePath)
		)
		{
			normalizedLinkedScenePath = ScriptPathUtility.Normalize(linkedScenePath);
		}

		if (
			string.Equals(
				normalizedScriptPath,
				primaryResourcePath,
				System.StringComparison.Ordinal
			)
			&& string.Equals(
				normalizedLinkedScenePath,
				linkedScenePath,
				System.StringComparison.Ordinal
			)
		)
		{
			return entry;
		}

		return BuildScriptEntry(
			folderPath,
			normalizedScriptPath,
			normalizedLinkedScenePath,
			isLocked
		);
	}

	private static List<string> GetFolderEntriesForPath(string folderPath)
	{
		List<string> folderEntries = new();
		string[] parts = folderPath.Split("/", System.StringSplitOptions.RemoveEmptyEntries);
		string currentPath = "";

		foreach (string part in parts)
		{
			currentPath = string.IsNullOrWhiteSpace(currentPath) ? part : $"{currentPath}/{part}";
			folderEntries.Add($"folder::{currentPath}");
		}

		return folderEntries;
	}

	#endregion
}
#endif
