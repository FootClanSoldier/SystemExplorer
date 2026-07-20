#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SystemExplorer.EditorIntegration.ScriptEditing;
using SystemExplorer.FileOperations;

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

			_pendingScriptRenameTreeState ??= CaptureScriptRenameTreeState(entry);

			if (_pendingScriptRenameTreeState == null || !_pendingScriptRenameTreeState.IsValid)
			{
				GD.PushWarning(
                    "System Explorer could not identify the exact selected script entry before opening the rename dialog."
				);
				return;
			}

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
		DebugLogger.LogOperation("Remove Confirmed", _pendingRemoveMetadata);

		if (string.IsNullOrWhiteSpace(_pendingRemoveMetadata))
			return;

		bool removeFromFilesystem = _removeFromFilesystemCheckBox.ButtonPressed;

		if (removeFromFilesystem)
		{
			if (
				!TryPrepareScriptsForPhysicalRemove(
					_pendingRemoveMetadata,
					out List<string> filePathsToDelete,
					out string preparationFailureMessage
				)
			)
			{
				if (!string.IsNullOrWhiteSpace(preparationFailureMessage))
					GD.PushWarning(preparationFailureMessage);

				DebugLogger.LogOperation(
					"Physical remove cancelled during preparation",
					preparationFailureMessage
				);
				return;
			}

			PhysicalDeleteResult deleteResult = DeleteFiles(filePathsToDelete);

			if (!deleteResult.Success)
			{
				GD.PushWarning(deleteResult.FailureMessage);
				DebugLogger.LogOperation("Physical remove failed", deleteResult.FailureMessage);
				return;
			}
		}

		if (!RemoveMetadata(_pendingRemoveMetadata))
		{
			DebugLogger.LogOperation("Remove cancelled: mutation failed", _pendingRemoveMetadata);
			return;
		}

		_pendingRemoveMetadata = "";
		_removeFromFilesystemCheckBox.ButtonPressed = false;

		if (SaveSystems())
			BuildTree(keepCurrentExpansionState: true);

		if (removeFromFilesystem)
			EditorInterface.Singleton.GetResourceFilesystem().Scan();
	}

	private bool RemoveMetadata(string metadata)
	{
		DebugLogger.LogOperation("Remove Mutation Requested", metadata);

		if (!EnsureSystemsLoadedForTreeOperation("Remove Item"))
			return false;

		if (metadata.StartsWith("system::"))
		{
			string systemName = metadata.Replace("system::", "");
			bool removed = _systems.Remove(systemName);
			DebugLogger.LogOperation(
				removed ? "Remove System Mutated" : "Remove System failed",
				systemName
			);
			return removed;
		}

		if (metadata.StartsWith("script::"))
		{
			string entry = GetEntryFromMetadata(metadata);
			bool removed = RemoveEntry(entry);
			DebugLogger.LogOperation(removed ? "Remove Script Mutated" : "Remove Script failed", entry);
			return removed;
		}

		if (metadata.StartsWith("sceneLink::"))
		{
			string entry = metadata.Substring("sceneLink::".Length);
			bool removed = RemoveEntry(entry);
			DebugLogger.LogOperation(removed ? "Remove Scene Mutated" : "Remove Scene failed", entry);
			return removed;
		}

		if (metadata.StartsWith("folder::"))
		{
			bool removed = RemoveFolder(metadata);
			DebugLogger.LogOperation(removed ? "Remove Folder Mutated" : "Remove Folder failed", metadata);
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

	private bool TryPrepareScriptsForPhysicalRemove(
		string metadata,
		out List<string> filePaths,
		out string failureMessage
	)
	{
		filePaths = new List<string>();
		failureMessage = "";

		if (!EnsureSystemsLoadedForTreeOperation("Remove Item From Filesystem"))
		{
			failureMessage =
				"System Explorer could not load the current systems data before physical removal.";
			return false;
		}

		List<string> collectedPaths = GetFilePathsForRemoveMetadata(metadata);
		HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);

		foreach (string collectedPath in collectedPaths)
		{
			string normalizedPath = collectedPath?.Trim().Replace('\\', '/');

			if (
				!string.IsNullOrWhiteSpace(normalizedPath)
				&& normalizedPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
			)
			{
				normalizedPath = ScriptPathUtility.Normalize(normalizedPath);
			}

			if (
				string.IsNullOrWhiteSpace(normalizedPath)
				|| !normalizedPath.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
			)
			{
				failureMessage =
					"System Explorer cancelled physical removal because an invalid project file path was found.";
				return false;
			}

			if (seenPaths.Add(normalizedPath))
				filePaths.Add(normalizedPath);
		}

		if (filePaths.Count == 0)
		{
			failureMessage =
				"System Explorer could not identify any physical files for the selected remove operation.";
			return false;
		}

		foreach (string filePath in filePaths)
		{
			if (FileAccess.FileExists(filePath))
				continue;

			failureMessage =
				$"System Explorer cancelled physical removal because the file no longer exists:\n{filePath}";
			return false;
		}

		List<string> scriptPaths = filePaths
			.Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
			.ToList();

		if (scriptPaths.Count == 0)
			return true;

		EditorInterface editorInterface = EditorInterface.Singleton;
		ScriptEditor scriptEditor = editorInterface?.GetScriptEditor();

		if (editorInterface == null || scriptEditor == null)
		{
			failureMessage =
				"System Explorer could not access Godot's Script Editor before physical removal.";
			return false;
		}

		Dictionary<string, List<Script>> matchingScriptsByPath = new(
			StringComparer.OrdinalIgnoreCase
		);

		foreach (string scriptPath in scriptPaths)
			matchingScriptsByPath[scriptPath] = new List<Script>();

		foreach (Script openScript in scriptEditor.GetOpenScripts())
		{
			if (openScript == null)
				continue;

			string openPath = ScriptPathUtility.Normalize(openScript.ResourcePath);

			if (matchingScriptsByPath.TryGetValue(openPath, out List<Script> matches))
				matches.Add(openScript);
		}

		foreach ((string scriptPath, List<Script> matches) in matchingScriptsByPath)
		{
			int distinctResourceCount = matches
				.Where(script => script != null && GodotObject.IsInstanceValid(script))
				.Select(script => script.GetInstanceId())
				.Distinct()
				.Count();

			if (distinctResourceCount <= 1)
				continue;

			failureMessage =
				$"System Explorer cancelled physical removal because more than one open Script resource matched:\n{scriptPath}\n\nClose the duplicate script tabs/resources and try again.";
			return false;
		}

		Script activeScriptBeforeOperation = scriptEditor.GetCurrentScript();
		HashSet<string> targetScriptPaths = scriptPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);

		BeginScriptEditorSyncSuppression();

		try
		{
			foreach ((string scriptPath, List<Script> matches) in matchingScriptsByPath)
			{
				Script targetScript = matches.FirstOrDefault(script =>
					script != null && GodotObject.IsInstanceValid(script)
				);

				if (targetScript == null)
					continue;

				if (
					!TryActivateExactScriptEditorForFileOperation(
						editorInterface,
						scriptEditor,
						targetScript,
						scriptPath,
						"physical removal",
						out ScriptEditorBase scriptEditorBase,
						out TextEdit textEditor
					)
				)
				{
					failureMessage =
						$"System Explorer could not safely activate the exact Script Editor buffer before deleting:\n{scriptPath}\n\nNo files or System Explorer metadata were changed.";
					return false;
				}

				OpenScriptEditorBuffer openEditorBuffer = new(scriptPath, textEditor);

				ScriptEditorBufferAutosaveOperationResult autosaveResult =
					OpenScriptEditorBufferAutosaveCoordinator.TryAutosaveIfNeeded(
						openEditorBuffer,
						failOnSavedDiskMismatch: true
					);

				if (!autosaveResult.Success)
				{
					string autosaveFailureMessage =
						ScriptFileOperationAutosaveFailureMessageBuilder.Build(
							autosaveResult.FailedAutosave,
							"Remove Script"
						);

					failureMessage = autosaveFailureMessage;
					return false;
				}

				ScriptEditorTabCloseResult closeResult = _scriptEditorTabService.TryCloseScriptTab(
					scriptEditor,
					targetScript,
					scriptEditorBase,
					textEditor
				);

				if (!closeResult.Success)
				{
					failureMessage =
						$"System Explorer could not safely close the Script Editor tab before deleting:\n{scriptPath}\n\n{closeResult.FailureMessage}\n\nNo files or System Explorer metadata were changed.";
					return false;
				}

				if (DoesScriptEditorStillContainOldScript(scriptEditor, targetScript, scriptPath))
				{
					failureMessage =
						$"System Explorer cancelled physical removal because Godot still reported the script as open after closing its tab:\n{scriptPath}\n\nNo files or System Explorer metadata were changed.";
					return false;
				}
			}

			string activePathBeforeOperation = ScriptPathUtility.Normalize(
				activeScriptBeforeOperation?.ResourcePath
			);

			if (
				activeScriptBeforeOperation != null
				&& GodotObject.IsInstanceValid(activeScriptBeforeOperation)
				&& !targetScriptPaths.Contains(activePathBeforeOperation)
				&& FileAccess.FileExists(activePathBeforeOperation)
			)
			{
				editorInterface.EditScript(activeScriptBeforeOperation, -1, 0, false);
			}

			return true;
		}
		finally
		{
			EndScriptEditorSyncSuppression();
		}
	}

	private readonly record struct PhysicalDeleteResult(
		bool Success,
		string FailureMessage,
		IReadOnlyList<string> DeletedPaths
	)
	{
		internal static PhysicalDeleteResult Succeeded(IReadOnlyList<string> deletedPaths) =>
			new(true, "", deletedPaths ?? Array.Empty<string>());

		internal static PhysicalDeleteResult Failed(
			string failureMessage,
			IReadOnlyList<string> deletedPaths
		) => new(false, failureMessage ?? "", deletedPaths ?? Array.Empty<string>());
	}

	private PhysicalDeleteResult DeleteFiles(List<string> filePaths)
	{
		List<string> deletedPaths = new();

		foreach (string filePath in filePaths)
		{
			PhysicalDeleteResult result = DeleteFile(filePath, deletedPaths);

			if (!result.Success)
				return result;
		}

		return PhysicalDeleteResult.Succeeded(deletedPaths);
	}

	private PhysicalDeleteResult DeleteFile(string resourcePath, List<string> deletedPaths)
	{
		if (string.IsNullOrWhiteSpace(resourcePath))
		{
			return PhysicalDeleteResult.Failed(
				"System Explorer cancelled physical removal because an empty file path was encountered.",
				deletedPaths
			);
		}

		if (!FileAccess.FileExists(resourcePath))
		{
			return PhysicalDeleteResult.Failed(
				$"System Explorer cancelled physical removal because the file no longer exists:\n{resourcePath}",
				deletedPaths
			);
		}

		Error error = DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(resourcePath));

		if (error != Error.Ok)
		{
			return PhysicalDeleteResult.Failed(
				$"System Explorer could not delete:\n{resourcePath}\n\nGodot error: {error}\n\nNo System Explorer metadata was changed.",
				deletedPaths
			);
		}

		deletedPaths.Add(resourcePath);
		string uidPath = $"{resourcePath}.uid";

		if (FileAccess.FileExists(uidPath))
		{
			Error uidError = DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(uidPath));

			if (uidError != Error.Ok)
			{
				return PhysicalDeleteResult.Failed(
					$"System Explorer deleted the file but could not delete its UID sidecar:\n{resourcePath}\n{uidPath}\n\nGodot error: {uidError}\n\nNo System Explorer metadata was changed. Physical deletion was only partially completed.",
					deletedPaths
				);
			}

			deletedPaths.Add(uidPath);
			DebugLogger.LogOperation("Delete File: removed uid sidecar", uidPath);
		}

		return PhysicalDeleteResult.Succeeded(deletedPaths);
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
			DebugLogger.LogOperation($"{context}: removed uid sidecar", uidPath);
			return;
		}

		DebugLogger.LogOperation($"{context}: could not remove uid sidecar", $"{uidPath} ({error})");
	}

	private void OnRenameConfirmed()
	{
		string newName = _renameInput.Text.Trim().Trim('/');

		DebugLogger.LogOperation("Rename Confirmed", $"{_pendingRenameMetadata} -> {newName}");

		if (string.IsNullOrWhiteSpace(newName))
		{
			DebugLogger.Log("Rename cancelled: empty name.");
			return;
		}

		bool renamed = false;
		bool scriptRenameHandledPersistence = false;

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
			scriptRenameHandledPersistence = true;
			renamed = RenameScript(_pendingRenameMetadata, newName);
		}
		else if (_pendingRenameMetadata.StartsWith("sceneLink::"))
		{
			renamed = RenameScene(_pendingRenameMetadata, newName);
		}

		if (!renamed)
		{
			DebugLogger.LogOperation(
				"Rename cancelled: mutation failed",
				$"{_pendingRenameMetadata} -> {newName}"
			);
			return;
		}

		_pendingRenameMetadata = "";

		if (scriptRenameHandledPersistence)
			return;

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
			DebugLogger.LogOperation("Rename System failed: new system exists", newName);
			return false;
		}

		List<string> entries = _systems[oldName];
		_systems.Remove(oldName);
		_systems[newName] = entries;

		DebugLogger.LogOperation("Rename System Mutated", $"{oldName} -> {newName}");

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

		DebugLogger.LogOperation(
			"Rename Folder Mutated",
			$"{systemName}: {oldFolderPath} -> {newFolderPath}"
		);

		return true;
	}

	private sealed class ScriptRenameTreeState
	{
		public string SystemName { get; init; } = "";
		public string FolderPath { get; init; } = "";
		public string Entry { get; init; } = "";
		public string Metadata { get; init; } = "";
		public bool WasFiltering { get; init; }
		public string FilterText { get; init; } = "";
		public HashSet<string> ExpansionState { get; init; } =
			new(StringComparer.OrdinalIgnoreCase);
		public Control FocusOwnerBeforeDialog { get; init; }
		public bool TreeHadFocusBeforeDialog { get; init; }
		public bool IsValid =>
			!string.IsNullOrWhiteSpace(SystemName)
			&& !string.IsNullOrWhiteSpace(Entry)
			&& Metadata.StartsWith("script::", StringComparison.Ordinal);
	}

	private readonly record struct ScriptRenameEditorState(
		string OldScriptPath,
		ulong ScriptInstanceId,
		ulong ScriptEditorBaseInstanceId,
		ulong TextEditorInstanceId,
		bool WasUnsaved,
		string BufferText,
		int FirstVisibleLine,
		int ScrollHorizontal,
		double ScrollVertical,
		int CaretLine,
		int CaretColumn,
		bool HadSelection,
		int SelectionFromLine,
		int SelectionFromColumn,
		int SelectionToLine,
		int SelectionToColumn,
		int SelectionOriginLine,
		int SelectionOriginColumn,
		bool RenamedScriptWasActive,
		Script ActiveScriptBeforeOperation
	);

	private const int ScriptRenameEditorRestoreMaxDeferredAttempts = 3;

	private enum ScriptRenameEditorRestoreMode
	{
		SuccessfulRename,
		RestoreOriginalAfterRenameFailure,
		RestoreOriginalAfterCloseFailure,
	}

	private sealed class PendingScriptRenameEditorRestore
	{
		public string TargetScriptPath { get; init; } = "";
		public string OldPathToReject { get; init; } = "";
		public ScriptRenameEditorState EditorState { get; init; }
		public ScriptRenameTreeState TreeState { get; init; }
		public string SelectedEntry { get; init; } = "";
		public Script LoadedScript { get; init; }
		public ulong LoadedScriptInstanceId { get; init; }
		public int DeferredAttemptCount { get; set; }
		public bool IsCompleting { get; set; }
		public bool EndSyncSuppression { get; init; }
		public ScriptRenameEditorRestoreMode Mode { get; init; }
		public bool IsValid =>
			!string.IsNullOrWhiteSpace(TargetScriptPath)
			&& TreeState != null
			&& TreeState.IsValid
			&& LoadedScript != null
			&& LoadedScriptInstanceId != 0;
	}

	private readonly ScriptEditorTabService _scriptEditorTabService = new();
	private ScriptRenameTreeState _pendingScriptRenameTreeState;
	private ScriptRenameTreeState _deferredScriptRenameTreeState;
	private string _deferredScriptRenameSelectedEntry = "";
	private bool _deferredScriptRenameEndSyncSuppression;
	private PendingScriptRenameEditorRestore _pendingScriptRenameEditorRestore;

	private bool RenameScript(string metadata, string newName)
	{
		if (_pendingScriptRenameEditorRestore != null)
		{
			GD.PushWarning(
                "System Explorer is still restoring the previous renamed script in Godot's Script Editor. Try again in a moment."
			);
			DebugLogger.LogOperation("Rename Script blocked: previous editor restore pending");
			_pendingScriptRenameTreeState = null;
			return false;
		}

		string entry = GetEntryFromMetadata(metadata);
		string oldScriptPath = ScriptPathUtility.Normalize(GetScriptPathFromEntry(entry));
		ScriptRenameTreeState treeState = _pendingScriptRenameTreeState;
		_pendingScriptRenameTreeState = null;

		if (treeState == null || !treeState.IsValid || treeState.Entry != entry)
			treeState = CaptureScriptRenameTreeState(entry);

		if (treeState == null || !treeState.IsValid)
		{
			GD.PushWarning(
                "System Explorer could not identify the exact selected system/folder entry before renaming the script. The rename was cancelled."
			);
			DebugLogger.LogOperation("Rename Script failed: selected tree identity unavailable", entry);
			return false;
		}

		if (!FileAccess.FileExists(oldScriptPath))
		{
			GD.PushWarning($"File does not exist: {oldScriptPath}");
			DebugLogger.LogOperation("Rename Script failed: file missing", oldScriptPath);
			return false;
		}

		if (newName.Contains("/") || newName.Contains("\\"))
		{
			GD.PushWarning(
                "Script rename only supports changing the file name, not the folder path."
			);
			DebugLogger.LogOperation("Rename Script failed: invalid name", newName);
			return false;
		}

		string newFileName = newName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
			? newName
			: $"{newName}.cs";
		string folderPath = ScriptPathUtility.Normalize(oldScriptPath.GetBaseDir());
		string newScriptPath = ScriptPathUtility.Normalize($"{folderPath}/{newFileName}");
		bool isExactSamePath = string.Equals(
			oldScriptPath,
			newScriptPath,
			StringComparison.Ordinal
		);
		bool isCaseOnlyRename =
			!isExactSamePath
			&& string.Equals(oldScriptPath, newScriptPath, StringComparison.OrdinalIgnoreCase);

		if (isExactSamePath)
			return false;

		if (
			!TryGetMatchingOpenScriptResources(
				oldScriptPath,
				out EditorInterface editorInterface,
				out ScriptEditor scriptEditor,
				out List<Script> matchingOpenScripts
			)
		)
		{
			return false;
		}

		if (matchingOpenScripts.Count != 1)
		{
			if (matchingOpenScripts.Count > 1)
			{
				string distinctOpenPaths = string.Join(
					"\n",
					matchingOpenScripts
						.Select(script => ScriptPathUtility.Normalize(script?.ResourcePath))
						.Where(path => !string.IsNullOrWhiteSpace(path))
				);
				GD.PushWarning(
					$"System Explorer could not safely rename this script because Godot has multiple open script resources for the same path:\n\n{distinctOpenPaths}\n\nClose the duplicate tabs or reset the project's .godot editor state before trying again."
				);
				DebugLogger.LogOperation(
					"Rename Script blocked: duplicate open script resources",
					distinctOpenPaths
				);
				return false;
			}

			GD.PushWarning(
				$"System Explorer could not safely rename the script because its Script Editor tab was not open:\n{oldScriptPath}\n\nOpen the script through System Explorer and try again."
			);
			DebugLogger.LogOperation("Rename Script failed: target script tab not open", oldScriptPath);
			return false;
		}

		if (isCaseOnlyRename)
		{
			if (
				!TryCheckCaseOnlyRenameTargetConflict(
					oldScriptPath,
					newScriptPath,
					out bool hasTargetConflict
				)
			)
			{
				return false;
			}

			if (hasTargetConflict)
			{
				GD.PushWarning($"File already exists: {newScriptPath}");
				DebugLogger.LogOperation(
					"Rename Script failed: case-only target exists as a separate file",
					newScriptPath
				);
				return false;
			}
		}
		else if (FileAccess.FileExists(newScriptPath))
		{
			GD.PushWarning($"File already exists: {newScriptPath}");
			DebugLogger.LogOperation("Rename Script failed: target exists", newScriptPath);
			return false;
		}

		if (!TryCheckUidRenameTargetConflict(oldScriptPath, newScriptPath, isCaseOnlyRename))
			return false;

		if (!EnsureSystemsLoadedForTreeOperation("Rename Script"))
			return false;

		Script oldOpenScript = matchingOpenScripts[0];
		Script activeScriptBeforeOperation = scriptEditor.GetCurrentScript();
		bool renamedScriptWasActive = IsSameScriptResource(
			activeScriptBeforeOperation,
			oldOpenScript
		);
		bool syncSuppressionQueuedForDeferredEnd = false;

		BeginScriptEditorSyncSuppression();

		try
		{
			if (
				!TryActivateExactScriptEditorForFileOperation(
					editorInterface,
					scriptEditor,
					oldOpenScript,
					oldScriptPath,
					"rename",
					out ScriptEditorBase oldScriptEditorBase,
					out TextEdit oldTextEditor
				)
			)
			{
				TryRestorePreviouslyActiveScript(
					editorInterface,
					scriptEditor,
					activeScriptBeforeOperation,
					oldOpenScript
				);
				QueueScriptRenameTreeRestore(treeState, entry, endSyncSuppression: true);
				syncSuppressionQueuedForDeferredEnd = true;
				return false;
			}

			ScriptRenameEditorState editorState = CaptureScriptRenameEditorState(
				oldScriptPath,
				oldOpenScript,
				oldScriptEditorBase,
				oldTextEditor,
				renamedScriptWasActive,
				activeScriptBeforeOperation
			);

			OpenScriptEditorBuffer openEditorBuffer = new(oldScriptPath, oldTextEditor);

			ScriptEditorBufferAutosaveOperationResult autosaveResult =
				OpenScriptEditorBufferAutosaveCoordinator.TryAutosaveIfNeeded(
					openEditorBuffer,
					failOnSavedDiskMismatch: true
				);

			if (!autosaveResult.Success)
			{
				string autosaveFailureMessage =
					ScriptFileOperationAutosaveFailureMessageBuilder.Build(
						autosaveResult.FailedAutosave,
						"Rename Script"
					);

				GD.PushWarning(autosaveFailureMessage);
				DebugLogger.LogOperation(
					"Rename Script failed: autosave",
					autosaveFailureMessage
				);
				TryRestorePreviouslyActiveScript(
					editorInterface,
					scriptEditor,
					activeScriptBeforeOperation,
					oldOpenScript
				);
				QueueScriptRenameTreeRestore(treeState, entry, endSyncSuppression: true);
				syncSuppressionQueuedForDeferredEnd = true;
				return false;
			}

			ScriptEditorTabCloseResult closeResult = _scriptEditorTabService.TryCloseScriptTab(
				scriptEditor,
				oldOpenScript,
				oldScriptEditorBase,
				oldTextEditor
			);

			if (!closeResult.Success)
			{
				string closeFailureMessage = closeResult.FailureMessage;
				bool recoveryRestoreQueued = false;

				// A signal exception or an unexpected editor transition can leave the
				// close result uncertain. If the exact old tab is already gone, request
				// a deferred reopen of the unchanged original path before reporting the
				// safe cancellation.
				if (
					!DoesScriptEditorStillContainOldScript(
						scriptEditor,
						oldOpenScript,
						oldScriptPath
					)
				)
				{
					RestoreScriptRenameTreeState(treeState, entry, restoreFocus: false);

					if (
						TryRequestScriptRenameEditorRestore(
							editorInterface,
							scriptEditor,
							oldScriptPath,
							editorState,
							oldPathToReject: "",
							treeState,
							entry,
							ScriptRenameEditorRestoreMode.RestoreOriginalAfterCloseFailure,
							endSyncSuppression: true,
							out string closeRecoveryFailureMessage
						)
					)
					{
						recoveryRestoreQueued = true;
						syncSuppressionQueuedForDeferredEnd = true;
					}
					else
					{
						closeFailureMessage +=
							$"\n\nThe old tab was no longer open, and requesting a deferred reopen of the unchanged original path also failed: {closeRecoveryFailureMessage}";
					}
				}
				else
				{
					TryRestorePreviouslyActiveScript(
						editorInterface,
						scriptEditor,
						activeScriptBeforeOperation,
						oldOpenScript
					);
				}

				GD.PushWarning(
					$"System Explorer could not safely close the old script tab before renaming. The file was not changed.\n\n{closeFailureMessage}"
				);
				DebugLogger.LogOperation("Rename Script failed: close tab", closeFailureMessage);

				if (!recoveryRestoreQueued)
				{
					QueueScriptRenameTreeRestore(treeState, entry, endSyncSuppression: true);
					syncSuppressionQueuedForDeferredEnd = true;
				}

				return false;
			}

			if (DoesScriptEditorStillContainOldScript(scriptEditor, oldOpenScript, oldScriptPath))
			{
				GD.PushWarning(
					$"System Explorer closed the Script Editor command but Godot still reports the old script as open. The file was not renamed:\n{oldScriptPath}"
				);
				DebugLogger.LogOperation(
					"Rename Script failed: old script remained open after close verification",
					oldScriptPath
				);
				QueueScriptRenameTreeRestore(treeState, entry, endSyncSuppression: true);
				syncSuppressionQueuedForDeferredEnd = true;
				return false;
			}

			bool originalPathAvailableAfterFailure = true;
			string temporaryCaseRenamePath = "";
			bool filesystemRenameSucceeded = isCaseOnlyRename
				? TryRenameScriptCaseOnly(
					oldScriptPath,
					newScriptPath,
					out originalPathAvailableAfterFailure,
					out temporaryCaseRenamePath
				)
				: TryRenameScriptOnce(
					oldScriptPath,
					newScriptPath,
					out originalPathAvailableAfterFailure
				);

			if (!filesystemRenameSucceeded)
			{
				bool recoveryRestoreQueued = false;

				if (originalPathAvailableAfterFailure && FileAccess.FileExists(oldScriptPath))
				{
					editorInterface.GetResourceFilesystem().Scan();
					RestoreScriptRenameTreeState(treeState, entry, restoreFocus: false);

					if (
						TryRequestScriptRenameEditorRestore(
							editorInterface,
							scriptEditor,
							oldScriptPath,
							editorState,
							oldPathToReject: "",
							treeState,
							entry,
							ScriptRenameEditorRestoreMode.RestoreOriginalAfterRenameFailure,
							endSyncSuppression: true,
							out string reopenFailureMessage
						)
					)
					{
						recoveryRestoreQueued = true;
						syncSuppressionQueuedForDeferredEnd = true;
					}
					else
					{
						GD.PushWarning(
							$"The rename failed and System Explorer could not request a deferred restore of the original Script Editor tab:\n{oldScriptPath}\n\n{reopenFailureMessage}"
						);
						DebugLogger.LogOperation(
							"Rename Script recovery warning: original reopen request failed",
							reopenFailureMessage
						);
					}
				}
				else
				{
					DebugLogger.LogOperation(
						"Rename Script recovery required: file not at original path",
						$"old='{oldScriptPath}', temporary='{temporaryCaseRenamePath}', target='{newScriptPath}'"
					);
				}

				if (!recoveryRestoreQueued)
				{
					QueueScriptRenameTreeRestore(treeState, entry, endSyncSuppression: true);
					syncSuppressionQueuedForDeferredEnd = true;
				}

				return false;
			}

			if (!FileAccess.FileExists(newScriptPath))
			{
				GD.PushWarning(
					$"System Explorer completed the filesystem rename call, but the final script path could not be verified:\n{newScriptPath}\n\nThe System Explorer data was not updated."
				);
				DebugLogger.LogOperation(
					"Rename Script failed: final path missing after filesystem success",
					newScriptPath
				);
				QueueScriptRenameTreeRestore(treeState, entry, endSyncSuppression: true);
				syncSuppressionQueuedForDeferredEnd = true;
				return false;
			}

			editorInterface.GetResourceFilesystem().Scan();

			if (!DoesAnySystemContainEntry(entry))
				TryRecoverSystemsFromDisk("Rename Script");

			bool entriesUpdated = UpdateScriptEntries(oldScriptPath, newScriptPath);
			string updatedSelectedEntry = BuildScriptEntry(
				GetFolderPathFromEntry(treeState.Entry),
				newScriptPath,
				GetLinkedScenePathFromEntry(treeState.Entry),
				IsEntryLocked(treeState.Entry)
			);

			if (!entriesUpdated)
			{
				GD.PushWarning(
					$"The script file was renamed, but no matching System Explorer entry could be updated. Verify systems.json manually.\n\nOld: {oldScriptPath}\nNew: {newScriptPath}"
				);
				DebugLogger.LogOperation(
					"Rename Script warning: no System Explorer entries updated after filesystem success",
					$"{oldScriptPath} -> {newScriptPath}"
				);
			}
			else if (!SaveSystems())
			{
				GD.PushWarning(
                    "The script was renamed and System Explorer was updated in memory, but systems.json could not be saved."
				);
				DebugLogger.LogOperation(
					"Rename Script warning: systems save failed after filesystem success",
					newScriptPath
				);
			}

			BuildTree(keepCurrentExpansionState: true);
			string selectedEntryAfterRename = entriesUpdated ? updatedSelectedEntry : entry;
			RestoreScriptRenameTreeState(treeState, selectedEntryAfterRename, restoreFocus: false);

			if (
				TryRequestScriptRenameEditorRestore(
					editorInterface,
					scriptEditor,
					newScriptPath,
					editorState,
					oldScriptPath,
					treeState,
					selectedEntryAfterRename,
					ScriptRenameEditorRestoreMode.SuccessfulRename,
					endSyncSuppression: true,
					out string editorRestoreRequestFailureMessage
				)
			)
			{
				syncSuppressionQueuedForDeferredEnd = true;
			}
			else
			{
				GD.PushWarning(
					$"The script was renamed successfully, but System Explorer could not request reopening of its Script Editor buffer. The filesystem rename will not be rolled back.\n\n{editorRestoreRequestFailureMessage}"
				);
				DebugLogger.LogOperation(
					"Rename Script warning: filesystem success; editor reopen request failed",
					editorRestoreRequestFailureMessage
				);
				QueueScriptRenameTreeRestore(
					treeState,
					selectedEntryAfterRename,
					endSyncSuppression: true
				);
				syncSuppressionQueuedForDeferredEnd = true;
			}

			DebugLogger.LogOperation("Rename Script Mutated", $"{oldScriptPath} -> {newScriptPath}");
			return true;
		}
		finally
		{
			if (!syncSuppressionQueuedForDeferredEnd)
				EndScriptEditorSyncSuppression();
		}
	}

	private ScriptRenameTreeState CaptureScriptRenameTreeState(string entry)
	{
		if (_tree == null || string.IsNullOrWhiteSpace(entry))
			return null;

		TreeItem selectedItem = _tree.GetSelected();
		string metadata = selectedItem?.GetMetadata(0).AsString() ?? "";

		if (!metadata.StartsWith("script::", StringComparison.Ordinal))
			metadata = $"script::{entry}";

		bool wasFiltering = IsScriptFilterActive();
		string systemName = "";
		string folderPath = GetFolderPathFromEntry(entry);

		if (wasFiltering)
		{
			TreeItem root = _tree.GetRoot();
			TreeItem current = root?.GetFirstChild();
			int selectedIndex = 0;

			while (current != null && current != selectedItem)
			{
				selectedIndex++;
				current = current.GetNext();
			}

			List<ScriptFilterResult> results = GetFilteredScriptResults(
				(_scriptFilterInput?.Text ?? "").Trim().ToLowerInvariant()
			);

			if (current == selectedItem && selectedIndex >= 0 && selectedIndex < results.Count)
			{
				ScriptFilterResult result = results[selectedIndex];

				if (result.Entry == entry)
				{
					systemName = result.SystemName;
					folderPath = result.FolderPath;
				}
			}
		}
		else
		{
			TreeItem current = selectedItem;

			while (current != null)
			{
				string currentMetadata = current.GetMetadata(0).AsString();

				if (currentMetadata.StartsWith("system::", StringComparison.Ordinal))
				{
					systemName = GetSystemNameFromMetadata(currentMetadata);
					break;
				}

				current = current.GetParent();
			}
		}

		HashSet<string> expansionState = wasFiltering
			? new HashSet<string>(
				_expandedItemsBeforeScriptFilter,
				StringComparer.OrdinalIgnoreCase
			)
			: CaptureTreeExpansionStateSnapshot();

		return new ScriptRenameTreeState
		{
			SystemName = systemName,
			FolderPath = folderPath,
			Entry = entry,
			Metadata = metadata,
			WasFiltering = wasFiltering,
			FilterText = _scriptFilterInput?.Text ?? "",
			ExpansionState = expansionState,
			FocusOwnerBeforeDialog = GetViewport()?.GuiGetFocusOwner(),
			TreeHadFocusBeforeDialog = _tree.HasFocus(),
		};
	}

	private bool TryActivateExactScriptEditorForFileOperation(
		EditorInterface editorInterface,
		ScriptEditor scriptEditor,
		Script targetScript,
		string targetPath,
		string operationName,
		out ScriptEditorBase scriptEditorBase,
		out TextEdit textEditor
	)
	{
		scriptEditorBase = null;
		textEditor = null;

		if (editorInterface == null || scriptEditor == null || targetScript == null)
			return false;

		editorInterface.EditScript(targetScript, -1, 0, false);
		Script currentScript = scriptEditor.GetCurrentScript();
		scriptEditorBase = scriptEditor.GetCurrentEditor();
		Control baseEditor = scriptEditorBase?.GetBaseEditor();

		if (
			!IsSameScriptResource(currentScript, targetScript)
			|| baseEditor is not TextEdit currentTextEditor
		)
		{
			GD.PushWarning(
				$"System Explorer could not safely activate and match the exact Script Editor buffer before {operationName}:\n{targetPath}"
			);
			DebugLogger.LogOperation(
				$"File operation failed during exact editor activation ({operationName})",
				targetPath
			);
			return false;
		}

		textEditor = currentTextEditor;
		return true;
	}

	private static ScriptRenameEditorState CaptureScriptRenameEditorState(
		string oldScriptPath,
		Script script,
		ScriptEditorBase scriptEditorBase,
		TextEdit textEditor,
		bool renamedScriptWasActive,
		Script activeScriptBeforeOperation
	)
	{
		bool hadSelection = textEditor.HasSelection(0);

		return new ScriptRenameEditorState(
			ScriptPathUtility.Normalize(oldScriptPath),
			script.GetInstanceId(),
			scriptEditorBase.GetInstanceId(),
			textEditor.GetInstanceId(),
			ScriptEditorBufferStateService.IsUnsaved(textEditor),
			textEditor.Text ?? "",
			Math.Max(0, textEditor.GetFirstVisibleLine()),
			Math.Max(0, textEditor.ScrollHorizontal),
			Math.Max(0.0, textEditor.ScrollVertical),
			Math.Max(0, textEditor.GetCaretLine()),
			Math.Max(0, textEditor.GetCaretColumn()),
			hadSelection,
			hadSelection ? Math.Max(0, textEditor.GetSelectionFromLine(0)) : 0,
			hadSelection ? Math.Max(0, textEditor.GetSelectionFromColumn(0)) : 0,
			hadSelection ? Math.Max(0, textEditor.GetSelectionToLine(0)) : 0,
			hadSelection ? Math.Max(0, textEditor.GetSelectionToColumn(0)) : 0,
			hadSelection ? Math.Max(0, textEditor.GetSelectionOriginLine(0)) : 0,
			hadSelection ? Math.Max(0, textEditor.GetSelectionOriginColumn(0)) : 0,
			renamedScriptWasActive,
			activeScriptBeforeOperation
		);
	}

	private bool TryRequestScriptRenameEditorRestore(
		EditorInterface editorInterface,
		ScriptEditor scriptEditor,
		string scriptPath,
		ScriptRenameEditorState editorState,
		string oldPathToReject,
		ScriptRenameTreeState treeState,
		string selectedEntry,
		ScriptRenameEditorRestoreMode mode,
		bool endSyncSuppression,
		out string failureMessage
	)
	{
		failureMessage = "";

		if (_pendingScriptRenameEditorRestore != null)
		{
			failureMessage =
				"Another script-rename editor restore is already pending. Only one restore operation can run at a time.";
			return false;
		}

		if (editorInterface == null || scriptEditor == null)
		{
			failureMessage =
				"Godot's EditorInterface or ScriptEditor was unavailable when the reopen was requested.";
			return false;
		}

		if (treeState == null || !treeState.IsValid)
		{
			failureMessage =
				"The captured System Explorer tree state was invalid when the reopen was requested.";
			return false;
		}

		string normalizedPath = ScriptPathUtility.Normalize(scriptPath);

		if (!FileAccess.FileExists(normalizedPath))
		{
			failureMessage =
				$"The script file does not exist at the path to reopen: {normalizedPath}";
			return false;
		}

		Script loadedScript;

		try
		{
			loadedScript = ResourceLoader.Load<Script>(
				normalizedPath,
				"",
				ResourceLoader.CacheMode.Ignore
			);
		}
		catch (Exception exception)
		{
			failureMessage = $"Loading '{normalizedPath}' threw: {exception.Message}";
			return false;
		}

		if (loadedScript == null)
		{
			failureMessage = $"Godot did not load a Script resource from '{normalizedPath}'.";
			return false;
		}

		ulong loadedScriptInstanceId = loadedScript.GetInstanceId();

		if (loadedScriptInstanceId == editorState.ScriptInstanceId)
		{
			failureMessage =
				$"Godot reused the old Script resource instance instead of loading a fresh resource from '{normalizedPath}'.";
			return false;
		}

		string loadedScriptPath = ScriptPathUtility.Normalize(loadedScript.ResourcePath);

		if (!string.Equals(loadedScriptPath, normalizedPath, StringComparison.Ordinal))
		{
			failureMessage =
				$"Godot loaded a Script resource with an unexpected path. Expected '{normalizedPath}', got '{loadedScriptPath}'.";
			return false;
		}

		PendingScriptRenameEditorRestore pendingRestore = new()
		{
			TargetScriptPath = normalizedPath,
			OldPathToReject = ScriptPathUtility.Normalize(oldPathToReject),
			EditorState = editorState,
			TreeState = treeState,
			SelectedEntry = selectedEntry ?? "",
			LoadedScript = loadedScript,
			LoadedScriptInstanceId = loadedScriptInstanceId,
			DeferredAttemptCount = 0,
			EndSyncSuppression = endSyncSuppression,
			Mode = mode,
		};

		_pendingScriptRenameEditorRestore = pendingRestore;

		try
		{
			// EditScript is intentionally treated as a request. Godot may not expose
			// the resulting current Script/ScriptEditorBase/TextEdit until a later frame.
			editorInterface.EditScript(loadedScript, -1, 0, false);
		}
		catch (Exception exception)
		{
			_pendingScriptRenameEditorRestore = null;
			failureMessage =
				$"Requesting Godot to open '{normalizedPath}' threw: {exception.Message}";
			return false;
		}

		DebugLogger.LogOperation(
			"Rename Script editor reopen requested",
			$"mode={mode}, target='{normalizedPath}', loadedScriptId={loadedScriptInstanceId}"
		);

		try
		{
			CallDeferred(nameof(VerifyPendingScriptRenameEditorRestoreDeferred));
		}
		catch (Exception exception)
		{
			_pendingScriptRenameEditorRestore = null;
			failureMessage =
				$"Scheduling deferred reopen verification for '{normalizedPath}' threw: {exception.Message}";
			return false;
		}

		return true;
	}

	private void VerifyPendingScriptRenameEditorRestoreDeferred()
	{
		PendingScriptRenameEditorRestore pendingRestore = _pendingScriptRenameEditorRestore;

		if (pendingRestore == null || pendingRestore.IsCompleting)
			return;

		try
		{
			VerifyPendingScriptRenameEditorRestore(pendingRestore);
		}
		catch (Exception exception)
		{
			CompletePendingScriptRenameEditorRestore(
				succeeded: false,
				BuildScriptRenameEditorRestoreFailure(
					pendingRestore,
					$"Deferred reopen verification threw unexpectedly: {exception.Message}",
					currentScript: null,
					currentEditor: null,
					baseEditor: null,
					finalPathMatchCount: 0
				)
			);
		}
	}

	private void VerifyPendingScriptRenameEditorRestore(
		PendingScriptRenameEditorRestore pendingRestore
	)
	{
		if (!ReferenceEquals(_pendingScriptRenameEditorRestore, pendingRestore))
			return;

		if (!IsInsideTree())
		{
			CancelPendingScriptRenameEditorRestore();
			return;
		}

		pendingRestore.DeferredAttemptCount++;

		EditorInterface editorInterface = EditorInterface.Singleton;
		ScriptEditor scriptEditor = editorInterface?.GetScriptEditor();
		Script currentScript = scriptEditor?.GetCurrentScript();
		ScriptEditorBase currentEditor = scriptEditor?.GetCurrentEditor();

		if (currentScript != null && !GodotObject.IsInstanceValid(currentScript))
			currentScript = null;

		if (currentEditor != null && !GodotObject.IsInstanceValid(currentEditor))
			currentEditor = null;

		Control baseEditor = currentEditor?.GetBaseEditor();
		List<Script> finalPathMatches =
			scriptEditor == null
				? new List<Script>()
				: GetDistinctOpenScriptsByPath(scriptEditor, pendingRestore.TargetScriptPath);

		if (!pendingRestore.IsValid)
		{
			CompletePendingScriptRenameEditorRestore(
				succeeded: false,
				BuildScriptRenameEditorRestoreFailure(
					pendingRestore,
					"The pending script-rename editor restore state was inconsistent.",
					currentScript,
					currentEditor,
					baseEditor,
					finalPathMatches.Count
				)
			);
			return;
		}

		if (!FileAccess.FileExists(pendingRestore.TargetScriptPath))
		{
			CompletePendingScriptRenameEditorRestore(
				succeeded: false,
				BuildScriptRenameEditorRestoreFailure(
					pendingRestore,
					$"The target script file no longer exists: {pendingRestore.TargetScriptPath}",
					currentScript,
					currentEditor,
					baseEditor,
					finalPathMatches.Count
				)
			);
			return;
		}

		if (editorInterface == null || scriptEditor == null)
		{
			QueueNextPendingScriptRenameEditorRestoreAttempt(
				pendingRestore,
				editorInterface,
				finalOpenScript: null,
				"Godot's ScriptEditor is not available yet.",
				currentScript,
				currentEditor,
				baseEditor,
				finalPathMatches.Count
			);
			return;
		}

		if (
			DoesOpenScriptSetContainInstance(
				scriptEditor,
				pendingRestore.EditorState.ScriptInstanceId
			)
			|| (
				currentScript != null
				&& currentScript.GetInstanceId() == pendingRestore.EditorState.ScriptInstanceId
			)
		)
		{
			CompletePendingScriptRenameEditorRestore(
				succeeded: false,
				BuildScriptRenameEditorRestoreFailure(
					pendingRestore,
					"The old closed Script resource instance reappeared during reopen verification.",
					currentScript,
					currentEditor,
					baseEditor,
					finalPathMatches.Count
				)
			);
			return;
		}

		if (
			TryGetRejectedOldOpenScriptPath(
				scriptEditor,
				pendingRestore.OldPathToReject,
				pendingRestore.TargetScriptPath,
				out string rejectedOldOpenPath
			)
		)
		{
			CompletePendingScriptRenameEditorRestore(
				succeeded: false,
				BuildScriptRenameEditorRestoreFailure(
					pendingRestore,
					$"The old script path still appeared among open scripts: {rejectedOldOpenPath}",
					currentScript,
					currentEditor,
					baseEditor,
					finalPathMatches.Count
				)
			);
			return;
		}

		if (finalPathMatches.Count > 1)
		{
			CompletePendingScriptRenameEditorRestore(
				succeeded: false,
				BuildScriptRenameEditorRestoreFailure(
					pendingRestore,
					$"Godot reported {finalPathMatches.Count} open Script resources for the target path; exactly one was required.",
					currentScript,
					currentEditor,
					baseEditor,
					finalPathMatches.Count
				)
			);
			return;
		}

		if (finalPathMatches.Count == 0)
		{
			QueueNextPendingScriptRenameEditorRestoreAttempt(
				pendingRestore,
				editorInterface,
				finalOpenScript: null,
				"Godot has not exposed the requested target Script among GetOpenScripts() yet.",
				currentScript,
				currentEditor,
				baseEditor,
				finalPathMatches.Count
			);
			return;
		}

		Script finalOpenScript = finalPathMatches[0];
		string finalOpenPath = ScriptPathUtility.Normalize(finalOpenScript.ResourcePath);

		if (
			!string.Equals(finalOpenPath, pendingRestore.TargetScriptPath, StringComparison.Ordinal)
		)
		{
			CompletePendingScriptRenameEditorRestore(
				succeeded: false,
				BuildScriptRenameEditorRestoreFailure(
					pendingRestore,
					$"The unique target Script resource used unexpected path casing. Expected '{pendingRestore.TargetScriptPath}', got '{finalOpenPath}'.",
					currentScript,
					currentEditor,
					baseEditor,
					finalPathMatches.Count
				)
			);
			return;
		}

		if (finalOpenScript.GetInstanceId() == pendingRestore.EditorState.ScriptInstanceId)
		{
			CompletePendingScriptRenameEditorRestore(
				succeeded: false,
				BuildScriptRenameEditorRestoreFailure(
					pendingRestore,
					"The unique target Script resource was the old closed Script instance.",
					currentScript,
					currentEditor,
					baseEditor,
					finalPathMatches.Count
				)
			);
			return;
		}

		string currentPath = ScriptPathUtility.Normalize(currentScript?.ResourcePath);
		bool currentIsUniqueFinalScript =
			currentScript != null
			&& currentScript.GetInstanceId() == finalOpenScript.GetInstanceId()
			&& string.Equals(
				currentPath,
				pendingRestore.TargetScriptPath,
				StringComparison.Ordinal
			);

		if (!currentIsUniqueFinalScript)
		{
			QueueNextPendingScriptRenameEditorRestoreAttempt(
				pendingRestore,
				editorInterface,
				finalOpenScript,
				"The unique target Script is open, but Godot has not made it the exact current Script yet.",
				currentScript,
				currentEditor,
				baseEditor,
				finalPathMatches.Count
			);
			return;
		}

		if (currentEditor == null || !GodotObject.IsInstanceValid(currentEditor))
		{
			QueueNextPendingScriptRenameEditorRestoreAttempt(
				pendingRestore,
				editorInterface,
				finalOpenScript,
				"Godot has made the target Script current, but GetCurrentEditor() is still unavailable.",
				currentScript,
				currentEditor,
				baseEditor,
				finalPathMatches.Count
			);
			return;
		}

		if (
			baseEditor is not TextEdit currentTextEditor
			|| !GodotObject.IsInstanceValid(currentTextEditor)
		)
		{
			QueueNextPendingScriptRenameEditorRestoreAttempt(
				pendingRestore,
				editorInterface,
				finalOpenScript,
				"Godot has not exposed a valid TextEdit for the current target Script yet.",
				currentScript,
				currentEditor,
				baseEditor,
				finalPathMatches.Count
			);
			return;
		}

		if (
			!ScriptTextFileService.TextsMatchForDiskVerification(
				currentTextEditor.Text ?? "",
				pendingRestore.EditorState.BufferText
			)
		)
		{
			CompletePendingScriptRenameEditorRestore(
				succeeded: false,
				BuildScriptRenameEditorRestoreFailure(
					pendingRestore,
					$"The reopened editor buffer for '{pendingRestore.TargetScriptPath}' did not match the text captured before close.",
					currentScript,
					currentEditor,
					baseEditor,
					finalPathMatches.Count
				)
			);
			return;
		}

		RestoreScriptRenameEditorState(currentTextEditor, pendingRestore.EditorState);

		if (!pendingRestore.EditorState.RenamedScriptWasActive)
		{
			TryRestorePreviouslyActiveScript(
				editorInterface,
				scriptEditor,
				pendingRestore.EditorState.ActiveScriptBeforeOperation,
				finalOpenScript
			);
		}

		CompletePendingScriptRenameEditorRestore(succeeded: true, failureMessage: "");
	}

	private void QueueNextPendingScriptRenameEditorRestoreAttempt(
		PendingScriptRenameEditorRestore pendingRestore,
		EditorInterface editorInterface,
		Script finalOpenScript,
		string transientReason,
		Script currentScript,
		ScriptEditorBase currentEditor,
		Control baseEditor,
		int finalPathMatchCount
	)
	{
		if (
			pendingRestore == null
			|| !ReferenceEquals(_pendingScriptRenameEditorRestore, pendingRestore)
			|| pendingRestore.IsCompleting
		)
		{
			return;
		}

		if (pendingRestore.DeferredAttemptCount >= ScriptRenameEditorRestoreMaxDeferredAttempts)
		{
			CompletePendingScriptRenameEditorRestore(
				succeeded: false,
				BuildScriptRenameEditorRestoreFailure(
					pendingRestore,
					$"{transientReason} The retry limit was reached.",
					currentScript,
					currentEditor,
					baseEditor,
					finalPathMatchCount
				)
			);
			return;
		}

		if (editorInterface != null && finalOpenScript != null)
		{
			try
			{
				// Re-request only the unique Script resource Godot itself exposed.
				editorInterface.EditScript(finalOpenScript, -1, 0, false);
			}
			catch (Exception exception)
			{
				CompletePendingScriptRenameEditorRestore(
					succeeded: false,
					BuildScriptRenameEditorRestoreFailure(
						pendingRestore,
						$"Re-activating the unique target Script threw: {exception.Message}",
						currentScript,
						currentEditor,
						baseEditor,
						finalPathMatchCount
					)
				);
				return;
			}
		}

		DebugLogger.LogOperation(
			"Rename Script editor restore retry queued",
			$"attempt={pendingRestore.DeferredAttemptCount}/{ScriptRenameEditorRestoreMaxDeferredAttempts}, reason='{transientReason}'"
		);

		try
		{
			CallDeferred(nameof(VerifyPendingScriptRenameEditorRestoreDeferred));
		}
		catch (Exception exception)
		{
			CompletePendingScriptRenameEditorRestore(
				succeeded: false,
				BuildScriptRenameEditorRestoreFailure(
					pendingRestore,
					$"Scheduling the next deferred verification attempt threw: {exception.Message}",
					currentScript,
					currentEditor,
					baseEditor,
					finalPathMatchCount
				)
			);
		}
	}

	private void CompletePendingScriptRenameEditorRestore(bool succeeded, string failureMessage)
	{
		PendingScriptRenameEditorRestore pendingRestore = _pendingScriptRenameEditorRestore;

		if (pendingRestore == null || pendingRestore.IsCompleting)
			return;

		pendingRestore.IsCompleting = true;

		try
		{
			if (!succeeded)
			{
				string warningMessage = pendingRestore.Mode switch
				{
					ScriptRenameEditorRestoreMode.SuccessfulRename =>
						$"The script was renamed successfully, but Godot did not finish exposing a safe reopened Script Editor buffer after {pendingRestore.DeferredAttemptCount} deferred attempt(s).\n\nFinal path:\n{pendingRestore.TargetScriptPath}\n\nThe filesystem and System Explorer data remain on the new path.\n\n{failureMessage}",
					ScriptRenameEditorRestoreMode.RestoreOriginalAfterCloseFailure =>
						$"The rename was cancelled before filesystem mutation, but System Explorer could not fully restore the original Script Editor tab after {pendingRestore.DeferredAttemptCount} deferred attempt(s).\n\nOriginal path:\n{pendingRestore.TargetScriptPath}\n\n{failureMessage}",
					_ =>
						$"The filesystem rename failed, and System Explorer could not fully restore the original Script Editor tab after {pendingRestore.DeferredAttemptCount} deferred attempt(s).\n\nOriginal path:\n{pendingRestore.TargetScriptPath}\n\nSystem Explorer data remains unchanged.\n\n{failureMessage}",
				};

				GD.PushWarning(warningMessage);
				DebugLogger.LogOperation("Rename Script deferred editor restore failed", warningMessage);
			}
			else
			{
				DebugLogger.LogOperation(
					"Rename Script deferred editor restore completed",
					$"mode={pendingRestore.Mode}, target='{pendingRestore.TargetScriptPath}', attempts={pendingRestore.DeferredAttemptCount}"
				);
			}

			// Tree selection is restored immediately without final focus, then once more
			// on a later deferred pass after any previous-active-script request has settled.
			RestoreScriptRenameTreeState(
				pendingRestore.TreeState,
				pendingRestore.SelectedEntry,
				restoreFocus: false
			);
		}
		catch (Exception exception)
		{
			DebugLogger.LogOperation("Rename Script terminal restore warning", exception.Message);
		}

		try
		{
			CallDeferred(nameof(FinalizePendingScriptRenameTreeRestoreDeferred));
		}
		catch (Exception exception)
		{
			DebugLogger.LogOperation(
				"Rename Script final tree restore scheduling warning",
				exception.Message
			);
			FinalizePendingScriptRenameTreeRestoreDeferred();
		}
	}

	private void FinalizePendingScriptRenameTreeRestoreDeferred()
	{
		PendingScriptRenameEditorRestore pendingRestore = _pendingScriptRenameEditorRestore;

		if (pendingRestore == null || !pendingRestore.IsCompleting)
			return;

		try
		{
			RestoreScriptRenameTreeState(
				pendingRestore.TreeState,
				pendingRestore.SelectedEntry,
				restoreFocus: true
			);
		}
		finally
		{
			_pendingScriptRenameEditorRestore = null;

			if (pendingRestore.EndSyncSuppression)
				EndScriptEditorSyncSuppression();
		}
	}

	private void CancelPendingScriptRenameEditorRestore()
	{
		PendingScriptRenameEditorRestore pendingRestore = _pendingScriptRenameEditorRestore;
		_pendingScriptRenameEditorRestore = null;

		if (pendingRestore?.EndSyncSuppression == true)
			EndScriptEditorSyncSuppression();
	}

	private static bool DoesOpenScriptSetContainInstance(
		ScriptEditor scriptEditor,
		ulong scriptInstanceId
	)
	{
		if (scriptEditor == null || scriptInstanceId == 0)
			return false;

		foreach (Script openScript in scriptEditor.GetOpenScripts())
		{
			if (openScript != null && openScript.GetInstanceId() == scriptInstanceId)
				return true;
		}

		return false;
	}

	private static bool TryGetRejectedOldOpenScriptPath(
		ScriptEditor scriptEditor,
		string oldPathToReject,
		string targetScriptPath,
		out string rejectedOpenPath
	)
	{
		rejectedOpenPath = "";

		if (scriptEditor == null || string.IsNullOrWhiteSpace(oldPathToReject))
			return false;

		string normalizedOldPath = ScriptPathUtility.Normalize(oldPathToReject);
		string normalizedTargetPath = ScriptPathUtility.Normalize(targetScriptPath);
		bool isCaseOnlyTarget = string.Equals(
			normalizedOldPath,
			normalizedTargetPath,
			StringComparison.OrdinalIgnoreCase
		);

		foreach (Script openScript in scriptEditor.GetOpenScripts())
		{
			if (openScript == null)
				continue;

			string openPath = ScriptPathUtility.Normalize(openScript.ResourcePath);
			bool sameIgnoringCase = string.Equals(
				openPath,
				normalizedOldPath,
				StringComparison.OrdinalIgnoreCase
			);
			bool violatesPolicy =
				(!isCaseOnlyTarget && sameIgnoringCase)
				|| (
					isCaseOnlyTarget
					&& string.Equals(openPath, normalizedOldPath, StringComparison.Ordinal)
				);

			if (!violatesPolicy)
				continue;

			rejectedOpenPath = openPath;
			return true;
		}

		return false;
	}

	private static string BuildScriptRenameEditorRestoreFailure(
		PendingScriptRenameEditorRestore pendingRestore,
		string reason,
		Script currentScript,
		ScriptEditorBase currentEditor,
		Control baseEditor,
		int finalPathMatchCount
	)
	{
		TextEdit currentTextEditor = baseEditor as TextEdit;
		string currentScriptPath = ScriptPathUtility.Normalize(currentScript?.ResourcePath);
		ulong currentScriptInstanceId = currentScript?.GetInstanceId() ?? 0;
		ulong currentEditorInstanceId = currentEditor?.GetInstanceId() ?? 0;
		ulong currentTextEditorInstanceId = currentTextEditor?.GetInstanceId() ?? 0;
		bool reusedEditorControl =
			currentEditorInstanceId != 0
			&& currentEditorInstanceId == pendingRestore.EditorState.ScriptEditorBaseInstanceId;
		bool reusedTextControl =
			currentTextEditorInstanceId != 0
			&& currentTextEditorInstanceId == pendingRestore.EditorState.TextEditorInstanceId;

		return $"{reason}\n\n"
			+ $"Attempt: {pendingRestore.DeferredAttemptCount}/{ScriptRenameEditorRestoreMaxDeferredAttempts}\n"
			+ $"Target path: {pendingRestore.TargetScriptPath}\n"
			+ $"Old path to reject: {pendingRestore.OldPathToReject}\n"
			+ $"Current script path: {currentScriptPath}\n"
			+ $"Final-path resource count: {finalPathMatchCount}\n"
			+ $"Current editor was null: {currentEditor == null}\n"
			+ $"Current base editor was TextEdit: {currentTextEditor != null}\n"
			+ $"Old Script instance ID: {pendingRestore.EditorState.ScriptInstanceId}\n"
			+ $"Loaded Script instance ID: {pendingRestore.LoadedScriptInstanceId}\n"
			+ $"Current Script instance ID: {currentScriptInstanceId}\n"
			+ $"Old ScriptEditorBase instance ID: {pendingRestore.EditorState.ScriptEditorBaseInstanceId}\n"
			+ $"Current ScriptEditorBase instance ID: {currentEditorInstanceId}\n"
			+ $"ScriptEditorBase control reused: {reusedEditorControl}\n"
			+ $"Old TextEdit instance ID: {pendingRestore.EditorState.TextEditorInstanceId}\n"
			+ $"Current TextEdit instance ID: {currentTextEditorInstanceId}\n"
			+ $"TextEdit control reused: {reusedTextControl}";
	}

	private static void RestoreScriptRenameEditorState(
		TextEdit textEditor,
		ScriptRenameEditorState editorState
	)
	{
		if (textEditor == null || !GodotObject.IsInstanceValid(textEditor))
			return;

		int lineCount = Math.Max(1, textEditor.GetLineCount());
		int caretLine = Math.Clamp(editorState.CaretLine, 0, lineCount - 1);
		int caretColumn = Math.Clamp(
			editorState.CaretColumn,
			0,
			textEditor.GetLine(caretLine).Length
		);

		textEditor.RemoveSecondaryCarets();
		textEditor.Deselect();
		textEditor.SetCaretLine(caretLine, false);
		textEditor.SetCaretColumn(caretColumn, false);

		if (editorState.HadSelection)
		{
			int originLine = Math.Clamp(editorState.SelectionOriginLine, 0, lineCount - 1);
			int originColumn = Math.Clamp(
				editorState.SelectionOriginColumn,
				0,
				textEditor.GetLine(originLine).Length
			);
			textEditor.Select(originLine, originColumn, caretLine, caretColumn, 0);
		}

		int firstVisibleLine = Math.Clamp(editorState.FirstVisibleLine, 0, lineCount - 1);
		textEditor.SetLineAsFirstVisible(firstVisibleLine);
		textEditor.ScrollVertical = Math.Max(0.0, editorState.ScrollVertical);
		textEditor.ScrollHorizontal = Math.Max(0, editorState.ScrollHorizontal);
	}

	private static bool DoesScriptEditorStillContainOldScript(
		ScriptEditor scriptEditor,
		Script oldScript,
		string oldScriptPath
	)
	{
		if (scriptEditor == null)
			return true;

		string normalizedOldPath = ScriptPathUtility.Normalize(oldScriptPath);
		ulong oldScriptInstanceId = oldScript?.GetInstanceId() ?? 0;

		foreach (Script openScript in scriptEditor.GetOpenScripts())
		{
			if (openScript == null)
				continue;

			if (oldScriptInstanceId != 0 && openScript.GetInstanceId() == oldScriptInstanceId)
				return true;

			if (
				string.Equals(
					ScriptPathUtility.Normalize(openScript.ResourcePath),
					normalizedOldPath,
					StringComparison.OrdinalIgnoreCase
				)
			)
			{
				return true;
			}
		}

		return false;
	}

	private static void TryRestorePreviouslyActiveScript(
		EditorInterface editorInterface,
		ScriptEditor scriptEditor,
		Script activeScriptBeforeOperation,
		Script renamedScript
	)
	{
		if (
			editorInterface == null
			|| scriptEditor == null
			|| activeScriptBeforeOperation == null
			|| IsSameScriptResource(activeScriptBeforeOperation, renamedScript)
			|| !GodotObject.IsInstanceValid(activeScriptBeforeOperation)
		)
		{
			return;
		}

		editorInterface.EditScript(activeScriptBeforeOperation, -1, 0, false);
	}

	private void QueueScriptRenameTreeRestore(
		ScriptRenameTreeState treeState,
		string selectedEntry,
		bool endSyncSuppression
	)
	{
		_deferredScriptRenameTreeState = treeState;
		_deferredScriptRenameSelectedEntry = selectedEntry ?? "";
		_deferredScriptRenameEndSyncSuppression = endSyncSuppression;

		RestoreScriptRenameTreeState(
			treeState,
			_deferredScriptRenameSelectedEntry,
			restoreFocus: false
		);
		CallDeferred(nameof(RestoreScriptRenameTreeStateDeferred));
	}

	private void RestoreScriptRenameTreeStateDeferred()
	{
		try
		{
			RestoreScriptRenameTreeState(
				_deferredScriptRenameTreeState,
				_deferredScriptRenameSelectedEntry,
				restoreFocus: true
			);
		}
		finally
		{
			_deferredScriptRenameTreeState = null;
			_deferredScriptRenameSelectedEntry = "";

			if (_deferredScriptRenameEndSyncSuppression)
			{
				_deferredScriptRenameEndSyncSuppression = false;
				EndScriptEditorSyncSuppression();
			}
		}
	}

	private void RestoreScriptRenameTreeState(
		ScriptRenameTreeState treeState,
		string selectedEntry,
		bool restoreFocus
	)
	{
		if (treeState == null || _tree == null || !GodotObject.IsInstanceValid(_tree))
			return;

		if (!treeState.WasFiltering)
			RestoreTreeExpansionStateSnapshot(treeState.ExpansionState);

		if (!string.IsNullOrWhiteSpace(selectedEntry))
			TrySelectExactScriptRenameTreeItem(treeState, selectedEntry);

		// Keep the restored selection visible without leaving keyboard focus on the
		// whole tree. This matches normal script navigation and avoids the tree-wide
		// focus outline after the rename dialog and deferred editor restore complete.
		if (restoreFocus)
			ReleaseTreeFocusAfterNavigation();
	}

	private bool TrySelectExactScriptRenameTreeItem(
		ScriptRenameTreeState treeState,
		string selectedEntry
	)
	{
		TreeItem root = _tree?.GetRoot();

		if (root == null)
			return false;

		TreeItem targetItem = null;

		if (treeState.WasFiltering && IsScriptFilterActive())
		{
			List<ScriptFilterResult> results = GetFilteredScriptResults(
				(_scriptFilterInput?.Text ?? "").Trim().ToLowerInvariant()
			);
			TreeItem item = root.GetFirstChild();

			for (int index = 0; item != null && index < results.Count; index++)
			{
				ScriptFilterResult result = results[index];

				if (
					result.SystemName == treeState.SystemName
					&& result.FolderPath == treeState.FolderPath
					&& result.Entry == selectedEntry
				)
				{
					targetItem = item;
					break;
				}

				item = item.GetNext();
			}
		}
		else
		{
			TreeItem systemItem = root.GetFirstChild();

			while (systemItem != null)
			{
				if (systemItem.GetMetadata(0).AsString() == $"system::{treeState.SystemName}")
				{
					targetItem = FindTreeItemByMetadataWithinSubtree(
						systemItem,
						$"script::{selectedEntry}"
					);
					break;
				}

				systemItem = systemItem.GetNext();
			}
		}

		if (targetItem == null)
		{
			DebugLogger.LogOperation(
				"Rename Script tree restore warning: exact entry not found",
				$"system='{treeState.SystemName}', folder='{treeState.FolderPath}', entry='{selectedEntry}'"
			);
			return false;
		}

		targetItem.Select(0);
		_tree.ScrollToItem(targetItem);
		UpdateTreeLockIconVisibility();
		return _tree.GetSelected() == targetItem;
	}

	private static TreeItem FindTreeItemByMetadataWithinSubtree(TreeItem root, string metadata)
	{
		if (root == null)
			return null;

		if (root.GetMetadata(0).AsString() == metadata)
			return root;

		TreeItem child = root.GetFirstChild();

		while (child != null)
		{
			TreeItem found = FindTreeItemByMetadataWithinSubtree(child, metadata);

			if (found != null)
				return found;

			child = child.GetNext();
		}

		return null;
	}

	private bool TryGetMatchingOpenScriptResources(
		string scriptPath,
		out EditorInterface editorInterface,
		out ScriptEditor scriptEditor,
		out List<Script> matchingOpenScripts
	)
	{
		editorInterface = EditorInterface.Singleton;
		scriptEditor = editorInterface?.GetScriptEditor();
		matchingOpenScripts = new List<Script>();

		if (editorInterface == null || scriptEditor == null)
		{
			GD.PushWarning(
                "System Explorer could not safely inspect Godot's Script Editor before renaming the script. The rename was cancelled."
			);
			DebugLogger.LogOperation("Rename Script failed: Script Editor unavailable", scriptPath);
			return false;
		}

		string normalizedScriptPath = ScriptPathUtility.Normalize(scriptPath);
		HashSet<ulong> matchedInstanceIds = new();

		foreach (Script openScript in scriptEditor.GetOpenScripts())
		{
			if (openScript == null)
				continue;

			string openScriptPath = ScriptPathUtility.Normalize(openScript.ResourcePath);

			if (
				string.IsNullOrWhiteSpace(openScriptPath)
				|| !string.Equals(
					openScriptPath,
					normalizedScriptPath,
					StringComparison.OrdinalIgnoreCase
				)
			)
			{
				continue;
			}

			if (matchedInstanceIds.Add(openScript.GetInstanceId()))
				matchingOpenScripts.Add(openScript);
		}

		return true;
	}

	private static List<Script> GetDistinctOpenScriptsByPath(
		ScriptEditor scriptEditor,
		string scriptPath
	)
	{
		List<Script> matchingScripts = new();

		if (scriptEditor == null || string.IsNullOrWhiteSpace(scriptPath))
			return matchingScripts;

		string normalizedScriptPath = ScriptPathUtility.Normalize(scriptPath);
		HashSet<ulong> matchedInstanceIds = new();

		foreach (Script openScript in scriptEditor.GetOpenScripts())
		{
			if (openScript == null)
				continue;

			string openScriptPath = ScriptPathUtility.Normalize(openScript.ResourcePath);

			if (
				!string.Equals(
					openScriptPath,
					normalizedScriptPath,
					StringComparison.OrdinalIgnoreCase
				) || !matchedInstanceIds.Add(openScript.GetInstanceId())
			)
			{
				continue;
			}

			matchingScripts.Add(openScript);
		}

		return matchingScripts;
	}

	private static bool IsSameScriptResource(Script left, Script right)
	{
		return left != null && right != null && left.GetInstanceId() == right.GetInstanceId();
	}

	private bool TryCheckCaseOnlyRenameTargetConflict(
		string oldScriptPath,
		string newScriptPath,
		out bool hasTargetConflict
	)
	{
		hasTargetConflict = false;

		string folderPath = ScriptPathUtility.Normalize(oldScriptPath.GetBaseDir());
		string oldFileName = oldScriptPath.GetFile();
		string newFileName = newScriptPath.GetFile();

		using DirAccess directory = DirAccess.Open(folderPath);

		if (directory == null)
		{
			GD.PushWarning(
				$"System Explorer could not inspect the script folder before the case-only rename: {folderPath}"
			);
			DebugLogger.LogOperation(
				"Rename Script failed: could not inspect case-only target directory",
				folderPath
			);
			return false;
		}

		directory.IncludeHidden = true;

		bool hasExactOldEntry = false;
		bool hasExactNewEntry = false;

		foreach (string fileName in directory.GetFiles())
		{
			if (string.Equals(fileName, oldFileName, StringComparison.Ordinal))
				hasExactOldEntry = true;

			if (string.Equals(fileName, newFileName, StringComparison.Ordinal))
				hasExactNewEntry = true;
		}

		hasTargetConflict = hasExactOldEntry && hasExactNewEntry;
		return true;
	}

	private bool TryCheckUidRenameTargetConflict(
		string oldScriptPath,
		string newScriptPath,
		bool isCaseOnlyRename
	)
	{
		string oldUidPath = $"{oldScriptPath}.uid";

		if (!FileAccess.FileExists(oldUidPath))
			return true;

		string newUidPath = $"{newScriptPath}.uid";
		bool destinationUidExists;

		if (isCaseOnlyRename)
		{
			string folderPath = ScriptPathUtility.Normalize(oldScriptPath.GetBaseDir());
			using DirAccess directory = DirAccess.Open(folderPath);

			if (directory == null)
			{
				GD.PushWarning(
					$"System Explorer could not inspect the script folder before checking the UID sidecar rename: {folderPath}"
				);
				DebugLogger.LogOperation(
					"Rename Script failed: could not inspect UID target directory",
					folderPath
				);
				return false;
			}

			directory.IncludeHidden = true;
			string oldUidFileName = oldUidPath.GetFile();
			string newUidFileName = newUidPath.GetFile();
			bool hasExactOldUid = false;
			bool hasExactNewUid = false;

			foreach (string fileName in directory.GetFiles())
			{
				if (string.Equals(fileName, oldUidFileName, StringComparison.Ordinal))
					hasExactOldUid = true;

				if (string.Equals(fileName, newUidFileName, StringComparison.Ordinal))
					hasExactNewUid = true;
			}

			destinationUidExists = hasExactOldUid && hasExactNewUid;
		}
		else
		{
			destinationUidExists = FileAccess.FileExists(newUidPath);
		}

		if (!destinationUidExists)
			return true;

		GD.PushWarning(
			$"System Explorer could not rename the script because the destination UID sidecar already exists and will not be overwritten:\n{newUidPath}"
		);
		DebugLogger.LogOperation("Rename Script failed: destination UID sidecar exists", newUidPath);
		return false;
	}

	private bool TryRenameScriptOnce(
		string oldScriptPath,
		string newScriptPath,
		out bool originalPathAvailableAfterFailure
	)
	{
		originalPathAvailableAfterFailure = true;
		string oldUidPath = $"{oldScriptPath}.uid";
		string newUidPath = $"{newScriptPath}.uid";
		bool hasUidSidecar = FileAccess.FileExists(oldUidPath);

		Error scriptRenameError = DirAccess.RenameAbsolute(oldScriptPath, newScriptPath);

		if (scriptRenameError != Error.Ok)
		{
			GD.PushWarning($"Could not rename script: {oldScriptPath} -> {newScriptPath}");
			DebugLogger.LogOperation(
				"Rename Script failed: filesystem rename error",
				$"{oldScriptPath} -> {newScriptPath} ({scriptRenameError})"
			);
			return false;
		}

		if (!hasUidSidecar)
			return true;

		Error uidRenameError = DirAccess.RenameAbsolute(oldUidPath, newUidPath);

		if (uidRenameError == Error.Ok)
		{
			DebugLogger.LogOperation("Rename Script: moved uid sidecar", $"{oldUidPath} -> {newUidPath}");
			return true;
		}

		Error scriptRollbackError = DirAccess.RenameAbsolute(newScriptPath, oldScriptPath);
		originalPathAvailableAfterFailure = scriptRollbackError == Error.Ok;

		if (scriptRollbackError == Error.Ok)
		{
			GD.PushWarning(
				$"System Explorer could not move the script UID sidecar, so the script rename was rolled back:\n{oldScriptPath}"
			);
			DebugLogger.LogOperation(
				"Rename Script failed: UID move failed; script rollback succeeded",
				$"uid={uidRenameError}, rollback={scriptRollbackError}, old='{oldScriptPath}', new='{newScriptPath}'"
			);
			return false;
		}

		GD.PushWarning(
			$"System Explorer could not move the script UID sidecar and could not roll back the script rename. The script may remain at the target path while its UID remains at the original path.\n\nOriginal: {oldScriptPath}\nTarget: {newScriptPath}"
		);
		DebugLogger.LogOperation(
			"Rename Script failed: UID move and script rollback failed",
			$"uid={uidRenameError}, rollback={scriptRollbackError}, old='{oldScriptPath}', new='{newScriptPath}'"
		);
		return false;
	}

	private bool TryRenameScriptCaseOnly(
		string oldScriptPath,
		string newScriptPath,
		out bool originalPathAvailableAfterFailure,
		out string temporaryScriptPath
	)
	{
		originalPathAvailableAfterFailure = true;
		string folderPath = ScriptPathUtility.Normalize(oldScriptPath.GetBaseDir());
		temporaryScriptPath = CreateUniqueCaseRenameTemporaryPath(folderPath);

		if (string.IsNullOrWhiteSpace(temporaryScriptPath))
		{
			GD.PushWarning(
				$"System Explorer could not create a unique temporary path for the case-only rename: {oldScriptPath} -> {newScriptPath}"
			);
			DebugLogger.LogOperation(
				"Rename Script failed: temporary case-only path unavailable",
				$"{oldScriptPath} -> {newScriptPath}"
			);
			return false;
		}

		string oldUidPath = $"{oldScriptPath}.uid";
		string newUidPath = $"{newScriptPath}.uid";
		string temporaryUidPath = $"{temporaryScriptPath}.uid";
		bool hasUidSidecar = FileAccess.FileExists(oldUidPath);

		Error firstScriptRenameError = DirAccess.RenameAbsolute(oldScriptPath, temporaryScriptPath);

		if (firstScriptRenameError != Error.Ok)
		{
			GD.PushWarning(
				$"Could not begin case-only script rename: {oldScriptPath} -> {newScriptPath}"
			);
			DebugLogger.LogOperation(
				"Rename Script failed: first case-only rename step",
				$"{oldScriptPath} -> {temporaryScriptPath} ({firstScriptRenameError})"
			);
			return false;
		}

		if (hasUidSidecar)
		{
			Error firstUidRenameError = DirAccess.RenameAbsolute(oldUidPath, temporaryUidPath);

			if (firstUidRenameError != Error.Ok)
			{
				Error scriptRollbackError = DirAccess.RenameAbsolute(
					temporaryScriptPath,
					oldScriptPath
				);
				originalPathAvailableAfterFailure = scriptRollbackError == Error.Ok;
				GD.PushWarning(
					$"System Explorer could not begin the case-only UID sidecar rename. The script rename was {(scriptRollbackError == Error.Ok ? "rolled back" : "not fully rolled back")}.\n\nOriginal: {oldScriptPath}\nTemporary: {temporaryScriptPath}"
				);
				DebugLogger.LogOperation(
					"Rename Script failed: first case-only UID step",
					$"uid={firstUidRenameError}, scriptRollback={scriptRollbackError}, old='{oldScriptPath}', temporary='{temporaryScriptPath}'"
				);
				return false;
			}
		}

		Error secondScriptRenameError = DirAccess.RenameAbsolute(
			temporaryScriptPath,
			newScriptPath
		);

		if (secondScriptRenameError != Error.Ok)
		{
			Error temporaryUidRollbackError = hasUidSidecar
				? DirAccess.RenameAbsolute(temporaryUidPath, oldUidPath)
				: Error.Ok;
			Error scriptRollbackError = DirAccess.RenameAbsolute(
				temporaryScriptPath,
				oldScriptPath
			);
			originalPathAvailableAfterFailure =
				temporaryUidRollbackError == Error.Ok && scriptRollbackError == Error.Ok;

			GD.PushWarning(
				originalPathAvailableAfterFailure
					? $"System Explorer could not complete the case-only script rename, but the original script and UID sidecar were restored:\n{oldScriptPath}"
					: $"System Explorer could not complete or fully roll back the case-only script rename.\n\nOriginal: {oldScriptPath}\nTemporary: {temporaryScriptPath}\nTarget: {newScriptPath}"
			);
			DebugLogger.LogOperation(
				"Rename Script failed: second case-only script step",
				$"second={secondScriptRenameError}, uidRollback={temporaryUidRollbackError}, scriptRollback={scriptRollbackError}, old='{oldScriptPath}', temporary='{temporaryScriptPath}', new='{newScriptPath}'"
			);
			return false;
		}

		if (!hasUidSidecar)
			return true;

		Error secondUidRenameError = DirAccess.RenameAbsolute(temporaryUidPath, newUidPath);

		if (secondUidRenameError == Error.Ok)
		{
			DebugLogger.LogOperation("Rename Script: moved uid sidecar", $"{oldUidPath} -> {newUidPath}");
			return true;
		}

		Error scriptToTemporaryRollbackError = DirAccess.RenameAbsolute(
			newScriptPath,
			temporaryScriptPath
		);
		Error uidRollbackError = DirAccess.RenameAbsolute(temporaryUidPath, oldUidPath);
		Error scriptToOriginalRollbackError =
			scriptToTemporaryRollbackError == Error.Ok
				? DirAccess.RenameAbsolute(temporaryScriptPath, oldScriptPath)
				: scriptToTemporaryRollbackError;
		originalPathAvailableAfterFailure =
			scriptToTemporaryRollbackError == Error.Ok
			&& uidRollbackError == Error.Ok
			&& scriptToOriginalRollbackError == Error.Ok;

		GD.PushWarning(
			originalPathAvailableAfterFailure
				? $"System Explorer could not complete the case-only UID sidecar rename, so the script and UID were restored:\n{oldScriptPath}"
				: $"System Explorer could not complete or fully roll back the case-only UID sidecar rename.\n\nOriginal: {oldScriptPath}\nTemporary: {temporaryScriptPath}\nTarget: {newScriptPath}"
		);
		DebugLogger.LogOperation(
			"Rename Script failed: second case-only UID step",
			$"uid={secondUidRenameError}, scriptToTemporary={scriptToTemporaryRollbackError}, uidRollback={uidRollbackError}, scriptToOriginal={scriptToOriginalRollbackError}, old='{oldScriptPath}', temporary='{temporaryScriptPath}', new='{newScriptPath}'"
		);
		return false;
	}

	private static string CreateUniqueCaseRenameTemporaryPath(string folderPath)
	{
		for (int attempt = 0; attempt < 16; attempt++)
		{
			string temporaryFileName = $".__system_explorer_case_rename_{Guid.NewGuid():N}.cs";
			string temporaryScriptPath = ScriptPathUtility.Normalize(
				$"{folderPath}/{temporaryFileName}"
			);

			if (!FileAccess.FileExists(temporaryScriptPath))
				return temporaryScriptPath;
		}

		return "";
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
			DebugLogger.LogOperation("Rename Scene failed: file missing", oldScenePath);
			return false;
		}

		if (newName.Contains("/") || newName.Contains("\\"))
		{
			GD.PushWarning(
                "Scene rename only supports changing the file name, not the folder path."
			);
			DebugLogger.LogOperation("Rename Scene failed: invalid name", newName);
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
			DebugLogger.LogOperation("Rename Scene failed: target exists", newScenePath);
			return false;
		}

		Error error = DirAccess.RenameAbsolute(oldScenePath, newScenePath);

		if (error != Error.Ok)
		{
			GD.PushWarning($"Could not rename scene: {oldScenePath} -> {newScenePath}");
			DebugLogger.LogOperation(
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

		DebugLogger.LogOperation("Rename Scene Mutated", $"{oldScenePath} -> {newScenePath}");

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

	private bool UpdateScriptEntries(string oldScriptPath, string newScriptPath)
	{
		if (!EnsureSystemsLoadedForTreeOperation("Update Script Entries"))
			return false;

		string normalizedOldScriptPath = ScriptPathUtility.Normalize(oldScriptPath);
		string normalizedNewScriptPath = ScriptPathUtility.Normalize(newScriptPath);
		int updatedEntryCount = 0;

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

				string scriptPath = ScriptPathUtility.Normalize(GetScriptPathFromEntry(entry));

				if (
					!string.Equals(
						scriptPath,
						normalizedOldScriptPath,
						StringComparison.OrdinalIgnoreCase
					)
				)
				{
					updatedEntries.Add(entry);
					continue;
				}

				string folderPath = GetFolderPathFromEntry(entry);

				string linkedScenePath = GetLinkedScenePathFromEntry(entry);
				string updatedEntry = BuildScriptEntry(
					folderPath,
					normalizedNewScriptPath,
					linkedScenePath,
					IsEntryLocked(entry)
				);

				UpdateSelectedScriptEntryFromFilter(entry, updatedEntry);
				updatedEntryCount++;

				updatedEntries.Add(updatedEntry);
			}

			_systems[systemName] = updatedEntries.Distinct().ToList();
		}

		return updatedEntryCount > 0;
	}

	#endregion
}
#endif
