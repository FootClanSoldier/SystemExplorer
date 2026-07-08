#if TOOLS
using Godot;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

public partial class SystemExplorerPlugin
{
	#region Rename and Remove Operations
	private void OpenRenameDialog()
	{
		if (string.IsNullOrWhiteSpace(_pendingRenameMetadata))
			return;

		if (_pendingRenameMetadata.StartsWith("system::"))
		{
			_renameInput.Text = _pendingRenameMetadata.Replace("system::", "");
		}
		else if (_pendingRenameMetadata.StartsWith("folder::"))
		{
			string[] parts = _pendingRenameMetadata.Split("::");

			if (parts.Length < 3)
				return;

			_renameInput.Text = parts[2].Split("/").Last();
		}
		else if (_pendingRenameMetadata.StartsWith("script::"))
		{
			string entry = GetEntryFromMetadata(_pendingRenameMetadata);
			string scriptPath = GetScriptPathFromEntry(entry);

			_renameInput.Text = scriptPath.GetFile().GetBaseName();
		}
		else if (_pendingRenameMetadata.StartsWith("sceneLink::"))
		{
			string entry = _pendingRenameMetadata.Substring("sceneLink::".Length);
			string scenePath = GetScenePathFromEntry(entry);

			_renameInput.Text = scenePath.GetFile().GetBaseName();
		}
		else
		{
			return;
		}

		_renameDialog.PopupCentered();
		_renameInput.GrabFocus();
		_renameInput.SelectAll();
	}

	private void OnRemoveConfirmed()
	{
		DebugLogOperation("Remove Confirmed", _pendingRemoveMetadata);

		if (string.IsNullOrWhiteSpace(_pendingRemoveMetadata))
			return;

		bool removeFromFilesystem = _removeFromFilesystemCheckBox.ButtonPressed;
		List<string> filePathsToDelete = removeFromFilesystem
			? GetFilePathsForRemoveMetadata(_pendingRemoveMetadata)
			: new List<string>();

		if (!RemoveMetadata(_pendingRemoveMetadata))
		{
			DebugLogOperation("Remove cancelled: mutation failed", _pendingRemoveMetadata);
			return;
		}

		if (removeFromFilesystem)
			DeleteFiles(filePathsToDelete);

		_pendingRemoveMetadata = "";
		_removeFromFilesystemCheckBox.ButtonPressed = false;

		if (SaveSystems())
			BuildTree();

		if (removeFromFilesystem)
			EditorInterface.Singleton.GetResourceFilesystem().Scan();
	}

	private bool RemoveMetadata(string metadata)
	{
		DebugLogOperation("Remove Mutation Requested", metadata);

		if (!EnsureSystemsLoadedForTreeOperation("Remove Item"))
			return false;

		if (metadata.StartsWith("system::"))
		{
			string systemName = metadata.Replace("system::", "");
			bool removed = _systems.Remove(systemName);
			DebugLogOperation(
				removed ? "Remove System Mutated" : "Remove System failed",
				systemName
			);
			return removed;
		}

		if (metadata.StartsWith("script::"))
		{
			string entry = GetEntryFromMetadata(metadata);
			bool removed = RemoveEntry(entry);
			DebugLogOperation(removed ? "Remove Script Mutated" : "Remove Script failed", entry);
			return removed;
		}

		if (metadata.StartsWith("sceneLink::"))
		{
			string entry = metadata.Substring("sceneLink::".Length);
			bool removed = RemoveEntry(entry);
			DebugLogOperation(removed ? "Remove Scene Mutated" : "Remove Scene failed", entry);
			return removed;
		}

		if (metadata.StartsWith("folder::"))
		{
			bool removed = RemoveFolder(metadata);
			DebugLogOperation(removed ? "Remove Folder Mutated" : "Remove Folder failed", metadata);
			return removed;
		}

		return false;
	}

	private List<string> GetFilePathsForRemoveMetadata(string metadata)
	{
		if (metadata.StartsWith("script::"))
		{
			string entry = GetEntryFromMetadata(metadata);
			return new List<string> { GetScriptPathFromEntry(entry) };
		}

		if (metadata.StartsWith("sceneLink::"))
		{
			string entry = metadata.Substring("sceneLink::".Length);
			return new List<string> { GetScenePathFromEntry(entry) };
		}

		if (metadata.StartsWith("system::"))
		{
			string systemName = metadata.Replace("system::", "");

			if (!EnsureSystemAvailable(systemName, "Collect Remove Scripts"))
				return new List<string>();

			return _systems[systemName]
				.Where(IsScriptOrSceneEntry)
				.Select(GetPathFromEntry)
				.Distinct()
				.ToList();
		}

		if (metadata.StartsWith("folder::"))
		{
			string[] parts = metadata.Split("::");

			if (parts.Length < 3)
				return new List<string>();

			string systemName = parts[1];
			string folderPath = parts[2];

			if (!EnsureSystemAvailable(systemName, "Collect Remove Scripts"))
				return new List<string>();

			return _systems[systemName]
				.Where(entry =>
					IsScriptOrSceneEntry(entry)
					&& (entry.StartsWith($"{folderPath}|") || entry.StartsWith($"{folderPath}/"))
				)
				.Select(GetPathFromEntry)
				.Distinct()
				.ToList();
		}

		return new List<string>();
	}

	private void DeleteFiles(List<string> filePaths)
	{
		foreach (string filePath in filePaths.Distinct())
			DeleteFile(filePath);
	}

	private void DeleteFile(string scriptPath)
	{
		if (string.IsNullOrWhiteSpace(scriptPath))
			return;

		if (!FileAccess.FileExists(scriptPath))
		{
			GD.PushWarning($"File does not exist, skipped delete: {scriptPath}");
			return;
		}

		Error error = DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(scriptPath));

		if (error != Error.Ok)
		{
			GD.PushWarning($"Could not delete file: {scriptPath}");
			return;
		}

		TryDeleteUidSidecar(scriptPath, "Delete File");
	}

	private void TryDeleteUidSidecar(string resourcePath, string context)
	{
		if (string.IsNullOrWhiteSpace(resourcePath))
			return;

		string uidPath = $"{resourcePath}.uid";

		if (!FileAccess.FileExists(uidPath))
			return;

		Error error = DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(uidPath));

		if (error == Error.Ok)
		{
			DebugLogOperation($"{context}: removed uid sidecar", uidPath);
			return;
		}

		DebugLogOperation($"{context}: could not remove uid sidecar", $"{uidPath} ({error})");
	}

	private void OnRenameConfirmed()
	{
		string newName = _renameInput.Text.Trim().Trim('/');

		DebugLogOperation("Rename Confirmed", $"{_pendingRenameMetadata} -> {newName}");

		if (string.IsNullOrWhiteSpace(newName))
		{
			DebugLog("Rename cancelled: empty name.");
			return;
		}

		bool renamed = false;

		if (_pendingRenameMetadata.StartsWith("system::"))
		{
			string oldSystemName = _pendingRenameMetadata.Replace("system::", "");
			renamed = RenameSystem(oldSystemName, newName);

			if (renamed)
				ForceExpandAfterSystemRename(oldSystemName, newName);
		}
		else if (_pendingRenameMetadata.StartsWith("folder::"))
		{
			string oldMetadata = _pendingRenameMetadata;
			renamed = RenameFolder(_pendingRenameMetadata, newName, out string newFolderPath);

			if (renamed)
				ForceExpandAfterFolderRename(oldMetadata, newFolderPath);
		}
		else if (_pendingRenameMetadata.StartsWith("script::"))
		{
			renamed = RenameScript(_pendingRenameMetadata, newName);
		}
		else if (_pendingRenameMetadata.StartsWith("sceneLink::"))
		{
			renamed = RenameScene(_pendingRenameMetadata, newName);
		}

		if (!renamed)
		{
			DebugLogOperation(
				"Rename cancelled: mutation failed",
				$"{_pendingRenameMetadata} -> {newName}"
			);
			return;
		}

		_pendingRenameMetadata = "";

		if (SaveSystems())
			BuildTree();
	}

	private void ForceExpandAfterSystemRename(string oldSystemName, string newSystemName)
	{
		ForceExpandSystem(newSystemName);

		foreach (string metadata in _expandedItems.ToList())
		{
			if (metadata == $"system::{oldSystemName}")
			{
				_forcedExpandedItems.Add($"system::{newSystemName}");
				continue;
			}

			if (metadata.StartsWith($"folder::{oldSystemName}::"))
			{
				string folderPath = metadata.Replace($"folder::{oldSystemName}::", "");
				_forcedExpandedItems.Add($"folder::{newSystemName}::{folderPath}");
			}
		}
	}

	private void ForceExpandAfterFolderRename(string oldMetadata, string newFolderPath)
	{
		string systemName = GetSystemNameFromMetadata(oldMetadata);
		string oldFolderPath = GetFolderPathFromMetadata(oldMetadata);

		if (string.IsNullOrWhiteSpace(systemName) || string.IsNullOrWhiteSpace(newFolderPath))
			return;

		ForceExpandFolderPath(systemName, newFolderPath);

		foreach (string metadata in _expandedItems.ToList())
		{
			if (!metadata.StartsWith($"folder::{systemName}::"))
				continue;

			string folderPath = metadata.Replace($"folder::{systemName}::", "");

			if (folderPath == oldFolderPath)
			{
				_forcedExpandedItems.Add($"folder::{systemName}::{newFolderPath}");
				continue;
			}

			if (folderPath.StartsWith($"{oldFolderPath}/"))
			{
				string childPath = folderPath.Replace($"{oldFolderPath}/", $"{newFolderPath}/");
				_forcedExpandedItems.Add($"folder::{systemName}::{childPath}");
			}
		}
	}

	private bool RenameSystem(string oldName, string newName)
	{
		if (!EnsureSystemsLoadedForTreeOperation("Rename System"))
			return false;

		if (!EnsureSystemAvailable(oldName, "Rename System"))
			return false;

		if (_systems.ContainsKey(newName))
		{
			GD.PushWarning($"System already exists: {newName}");
			DebugLogOperation("Rename System failed: new system exists", newName);
			return false;
		}

		List<string> entries = _systems[oldName];
		_systems.Remove(oldName);
		_systems[newName] = entries;

		DebugLogOperation("Rename System Mutated", $"{oldName} -> {newName}");

		return true;
	}

	private bool RenameFolder(string metadata, string newFolderName, out string newFolderPath)
	{
		newFolderPath = "";

		string[] parts = metadata.Split("::");

		if (parts.Length < 3)
			return false;

		if (!EnsureSystemsLoadedForTreeOperation("Rename Folder"))
			return false;

		string systemName = parts[1];
		string oldFolderPath = parts[2];

		if (!EnsureSystemAvailable(systemName, "Rename Folder"))
			return false;

		string parentPath = "";

		if (oldFolderPath.Contains("/"))
			parentPath = oldFolderPath.Substring(0, oldFolderPath.LastIndexOf('/'));

		newFolderPath = string.IsNullOrWhiteSpace(parentPath)
			? newFolderName
			: $"{parentPath}/{newFolderName}";

		List<string> updatedEntries = new();

		foreach (string entry in _systems[systemName])
		{
			if (entry.StartsWith("folder::"))
			{
				string folderEntryPath = GetFolderPathFromFolderEntry(entry);

				if (folderEntryPath == oldFolderPath)
				{
					updatedEntries.Add(BuildFolderEntry(newFolderPath, IsEntryLocked(entry)));
					continue;
				}

				if (folderEntryPath.StartsWith($"{oldFolderPath}/"))
				{
					string childFolderPath = folderEntryPath.Replace(
						$"{oldFolderPath}/",
						$"{newFolderPath}/"
					);
					updatedEntries.Add(BuildFolderEntry(childFolderPath, IsEntryLocked(entry)));
					continue;
				}
			}

			if (entry.StartsWith($"{oldFolderPath}|"))
			{
				updatedEntries.Add(entry.Replace($"{oldFolderPath}|", $"{newFolderPath}|"));
				continue;
			}

			if (entry.StartsWith($"{oldFolderPath}/"))
			{
				updatedEntries.Add(entry.Replace($"{oldFolderPath}/", $"{newFolderPath}/"));
				continue;
			}

			updatedEntries.Add(entry);
		}

		_systems[systemName] = updatedEntries.Distinct().ToList();

		DebugLogOperation(
			"Rename Folder Mutated",
			$"{systemName}: {oldFolderPath} -> {newFolderPath}"
		);

		return true;
	}

	private bool RenameScript(string metadata, string newName)
	{
		if (!EnsureSystemsLoadedForTreeOperation("Rename Script"))
			return false;

		string entry = GetEntryFromMetadata(metadata);
		string oldScriptPath = GetScriptPathFromEntry(entry);

		if (!FileAccess.FileExists(oldScriptPath))
		{
			GD.PushWarning($"File does not exist: {oldScriptPath}");
			DebugLogOperation("Rename Script failed: file missing", oldScriptPath);
			return false;
		}

		if (newName.Contains("/") || newName.Contains("\\"))
		{
			GD.PushWarning(
                "Script rename only supports changing the file name, not the folder path."
			);
			DebugLogOperation("Rename Script failed: invalid name", newName);
			return false;
		}

		string newFileName = newName.EndsWith(".cs") ? newName : $"{newName}.cs";
		string folderPath = oldScriptPath.GetBaseDir();
		string newScriptPath = $"{folderPath}/{newFileName}";

		if (oldScriptPath == newScriptPath)
			return false;

		if (FileAccess.FileExists(newScriptPath))
		{
			GD.PushWarning($"File already exists: {newScriptPath}");
			DebugLogOperation("Rename Script failed: target exists", newScriptPath);
			return false;
		}

		Error error = DirAccess.RenameAbsolute(oldScriptPath, newScriptPath);

		if (error != Error.Ok)
		{
			GD.PushWarning($"Could not rename script: {oldScriptPath} -> {newScriptPath}");
			DebugLogOperation(
				"Rename Script failed: filesystem rename error",
				$"{oldScriptPath} -> {newScriptPath} ({error})"
			);
			return false;
		}

		TryDeleteUidSidecar(oldScriptPath, "Rename Script");

		if (!DoesAnySystemContainEntry(entry))
			TryRecoverSystemsFromDisk("Rename Script");

		UpdateScriptEntries(oldScriptPath, newScriptPath);

		EditorInterface.Singleton.GetResourceFilesystem().Scan();

		DebugLogOperation("Rename Script Mutated", $"{oldScriptPath} -> {newScriptPath}");

		return true;
	}

	private bool RenameScene(string metadata, string newName)
	{
		if (!EnsureSystemsLoadedForTreeOperation("Rename Scene"))
			return false;

		string entry = metadata.Substring("sceneLink::".Length);
		string oldScenePath = GetScenePathFromEntry(entry);

		if (!FileAccess.FileExists(oldScenePath))
		{
			GD.PushWarning($"File does not exist: {oldScenePath}");
			DebugLogOperation("Rename Scene failed: file missing", oldScenePath);
			return false;
		}

		if (newName.Contains("/") || newName.Contains("\\"))
		{
			GD.PushWarning(
                "Scene rename only supports changing the file name, not the folder path."
			);
			DebugLogOperation("Rename Scene failed: invalid name", newName);
			return false;
		}

		string newFileName = newName.EndsWith(".tscn") ? newName : $"{newName}.tscn";
		string folderPath = oldScenePath.GetBaseDir();
		string newScenePath = $"{folderPath}/{newFileName}";

		if (oldScenePath == newScenePath)
			return false;

		if (FileAccess.FileExists(newScenePath))
		{
			GD.PushWarning($"File already exists: {newScenePath}");
			DebugLogOperation("Rename Scene failed: target exists", newScenePath);
			return false;
		}

		Error error = DirAccess.RenameAbsolute(oldScenePath, newScenePath);

		if (error != Error.Ok)
		{
			GD.PushWarning($"Could not rename scene: {oldScenePath} -> {newScenePath}");
			DebugLogOperation(
				"Rename Scene failed: filesystem rename error",
				$"{oldScenePath} -> {newScenePath} ({error})"
			);
			return false;
		}

		TryDeleteUidSidecar(oldScenePath, "Rename Scene");

		if (!DoesAnySystemContainEntry(entry))
			TryRecoverSystemsFromDisk("Rename Scene");

		UpdateSceneEntries(oldScenePath, newScenePath);

		EditorInterface.Singleton.GetResourceFilesystem().Scan();

		DebugLogOperation("Rename Scene Mutated", $"{oldScenePath} -> {newScenePath}");

		return true;
	}

	private void UpdateSceneEntries(string oldScenePath, string newScenePath)
	{
		if (!EnsureSystemsLoadedForTreeOperation("Update Scene Entries"))
			return;

		foreach (string systemName in _systems.Keys.ToList())
		{
			List<string> updatedEntries = new();

			foreach (string entry in _systems[systemName])
			{
				if (entry.StartsWith("folder::"))
				{
					updatedEntries.Add(entry);
					continue;
				}

				if (!IsSceneEntry(entry) || GetScenePathFromEntry(entry) != oldScenePath)
				{
					updatedEntries.Add(entry);
					continue;
				}

				string folderPath = GetFolderPathFromEntry(entry);
				string updatedEntry = BuildSceneEntry(
					folderPath,
					newScenePath,
					IsEntryLocked(entry)
				);

				updatedEntries.Add(updatedEntry);
			}

			_systems[systemName] = updatedEntries.Distinct().ToList();
		}
	}

	private void UpdateScriptEntries(string oldScriptPath, string newScriptPath)
	{
		if (!EnsureSystemsLoadedForTreeOperation("Update Script Entries"))
			return;

		foreach (string systemName in _systems.Keys.ToList())
		{
			List<string> updatedEntries = new();

			foreach (string entry in _systems[systemName])
			{
				if (entry.StartsWith("folder::"))
				{
					updatedEntries.Add(entry);
					continue;
				}

				string scriptPath = GetScriptPathFromEntry(entry);

				if (scriptPath != oldScriptPath)
				{
					updatedEntries.Add(entry);
					continue;
				}

				string folderPath = GetFolderPathFromEntry(entry);

				string linkedScenePath = GetLinkedScenePathFromEntry(entry);
				string updatedEntry = BuildScriptEntry(
					folderPath,
					newScriptPath,
					linkedScenePath,
					IsEntryLocked(entry)
				);

				UpdateSelectedScriptEntryFromFilter(entry, updatedEntry);

				updatedEntries.Add(updatedEntry);
			}

			_systems[systemName] = updatedEntries.Distinct().ToList();
		}
	}

	#endregion
}
#endif
