#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

public partial class SystemExplorerPlugin
{
	private enum AddTreeMutationResult
	{
		Success,
		NoChange,
		Failed,
	}

	#region Add and Create Tree Items
	private void OnAddSystemPressed()
	{
		string systemName = _systemNameInput.Text.Trim();

		DebugLogger.LogOperation("Add System Requested", systemName);

		if (string.IsNullOrWhiteSpace(systemName))
		{
			DebugLogger.Log("Add System cancelled: empty name.");
			return;
		}

		if (ContainsReservedSystemNameSeparator(systemName))
		{
			_systemNameInput.Text = "";
			UpdateSystemNameEnterIconVisibility(_systemNameInput.Text);
			ShowAddSystemInputWarning(
				"Invalid System Name",
				"System names cannot contain \"::\" because it is reserved by System Explorer."
			);
			DebugLogger.LogOperation(
				"Add System cancelled: reserved separator",
				systemName
			);
			return;
		}

		using TreeOperationDialogScope operationScope =
			BeginTreeOperationDialogScope("Add System Failed");

		if (FileAccess.FileExists(SavePath) && !EnsureSystemsLoadedForTreeOperation("Add System"))
			return;

		if (_systems.ContainsKey(systemName))
		{
			DebugLogger.LogOperation("Add System failed: name conflict", systemName);
			_systemNameInput.Text = "";
			UpdateSystemNameEnterIconVisibility(_systemNameInput.Text);
			ShowAddSystemInputWarning(
				"System Already Exists",
				"A system with this name already exists."
			);
			return;
		}

		SystemsAndFolderBindingsSnapshot snapshot =
			CaptureSystemsAndFolderBindingsSnapshot();

		_systems[systemName] = new List<string>();
		DebugLogger.LogOperation("Add System Mutated", systemName);

		_systemNameInput.Text = "";
		UpdateSystemNameEnterIconVisibility(_systemNameInput.Text);

		if (
			!TryPersistReversibleSystemsAndFolderBindingsMutation(
				snapshot,
				systemsChanged: true,
				folderBindingsChanged: false,
				operationName: "Add System"
			)
		)
		{
			return;
		}

		ForceExpandSystem(systemName);
		BuildTree();
	}

	private bool TryOpenAddFolderDialogForSelectedItem()
	{
		if (
			_isFilteringScripts
			|| _tree == null
			|| !GodotObject.IsInstanceValid(_tree)
			|| _addFolderDialog == null
			|| !GodotObject.IsInstanceValid(_addFolderDialog)
			|| _addFolderInput == null
			|| !GodotObject.IsInstanceValid(_addFolderInput)
		)
		{
			return false;
		}

		TreeItem selectedItem = _tree.GetSelected();

		if (
			selectedItem == null
			|| !GodotObject.IsInstanceValid(selectedItem)
			|| !TryResolveAddFolderTargetMetadata(
				selectedItem,
				out string targetMetadata
			)
		)
		{
			return false;
		}

		_pendingAddFolderMetadata = targetMetadata;

		if (OpenAddFolderDialog())
			return true;

		_pendingAddFolderMetadata = "";
		return false;
	}

	private bool TryResolveAddFolderTargetMetadata(
		TreeItem selectedItem,
		out string targetMetadata
	)
	{
		targetMetadata = "";

		if (selectedItem == null || !GodotObject.IsInstanceValid(selectedItem))
			return false;

		string metadata = selectedItem.GetMetadata(0).AsString();
		bool isSystem = metadata.StartsWith("system::", StringComparison.Ordinal);
		bool isFolder = metadata.StartsWith("folder::", StringComparison.Ordinal);
		bool isScript = metadata.StartsWith("script::", StringComparison.Ordinal);
		bool isScene = metadata.StartsWith("sceneLink::", StringComparison.Ordinal);

		if (!isSystem && !isFolder && !isScript && !isScene)
			return false;

		string systemName = GetSystemNameFromTreeItem(selectedItem);

		if (string.IsNullOrWhiteSpace(systemName))
			return false;

		if (isSystem)
		{
			targetMetadata = $"system::{systemName}";
			return true;
		}

		string folderPath = GetFolderPathFromTreeItem(selectedItem);

		if (isFolder && string.IsNullOrWhiteSpace(folderPath))
			return false;

		targetMetadata = string.IsNullOrWhiteSpace(folderPath)
			? $"system::{systemName}"
			: $"folder::{systemName}::{folderPath}";
		return true;
	}

	private bool OpenAddFolderDialog()
	{
		if (
			string.IsNullOrWhiteSpace(_pendingAddFolderMetadata)
			|| (
				!_pendingAddFolderMetadata.StartsWith(
					"system::",
					StringComparison.Ordinal
				)
				&& !_pendingAddFolderMetadata.StartsWith(
					"folder::",
					StringComparison.Ordinal
				)
			)
			|| string.IsNullOrWhiteSpace(
				GetSystemNameFromMetadata(_pendingAddFolderMetadata)
			)
			|| (
				_pendingAddFolderMetadata.StartsWith(
					"folder::",
					StringComparison.Ordinal
				)
				&& string.IsNullOrWhiteSpace(
					GetFolderPathFromMetadata(_pendingAddFolderMetadata)
				)
			)
			|| _addFolderDialog == null
			|| !GodotObject.IsInstanceValid(_addFolderDialog)
			|| _addFolderInput == null
			|| !GodotObject.IsInstanceValid(_addFolderInput)
		)
		{
			return false;
		}

		_addFolderInput.Text = "";
		_addFolderDialog.PopupCentered();
		_addFolderInput.Edit(true);
		return true;
	}

	private void OnAddFolderConfirmed()
	{
		string folderName = _addFolderInput.Text.Trim().Trim('/');
		DebugLogger.LogOperation("Add Folder Confirmed", folderName);

		if (string.IsNullOrWhiteSpace(folderName))
		{
			DebugLogger.Log("Add Folder cancelled: empty name.");
			return;
		}

		if (ContainsReservedVirtualFolderSeparator(folderName))
		{
			_addFolderInput.Text = "";
			ShowAddFolderInputWarning(
				"Invalid Folder Name",
				"Folder names cannot contain \"::\" or \"|\" because those characters are reserved by System Explorer."
			);
			DebugLogger.LogOperation(
				"Add Folder cancelled: reserved separator",
				folderName
			);
			return;
		}

		string targetSystemName = GetSystemNameFromMetadata(_pendingAddFolderMetadata);
		string parentFolderPath = GetFolderPathFromMetadata(_pendingAddFolderMetadata);
		string addedFolderPath = string.IsNullOrWhiteSpace(parentFolderPath)
			? folderName
			: $"{parentFolderPath}/{folderName}";

		using TreeOperationDialogScope operationScope =
			BeginTreeOperationDialogScope(
				"Add Folder Failed",
				closeOriginatingUi: CloseAddFolderUiAfterFailure
			);

		if (!EnsureSystemsLoadedForTreeOperation("Add Folder"))
			return;

		if (!EnsureSystemAvailable(targetSystemName, "Add Folder"))
			return;

		if (DoesFolderPathExistInSystem(targetSystemName, addedFolderPath))
		{
			DebugLogger.LogOperation(
				"Add Folder failed: name conflict",
				$"{targetSystemName}/{addedFolderPath}"
			);
			_addFolderInput.Text = "";
			ShowAddFolderInputWarning(
				"Folder Already Exists",
				"A folder with this name already exists."
			);
			return;
		}

		SystemsAndFolderBindingsSnapshot snapshot =
			CaptureSystemsAndFolderBindingsSnapshot();

		FolderMutationResult result = AddFolderToPendingLocation();

		if (result == FolderMutationResult.NameConflict)
		{
			_addFolderInput.Text = "";
			ShowAddFolderInputWarning(
				"Folder Already Exists",
				"A folder with this name already exists."
			);
			return;
		}

		if (result == FolderMutationResult.Failed)
		{
			if (!HasActiveTreeOperationFailure)
			{
				ReportTreeOperationFailure(
					"System Explorer could not add the folder because the selected tree location could no longer be updated."
				);
			}

			DebugLogger.Log("Add Folder cancelled: mutation failed.");
			return;
		}

		if (
			!TryPersistReversibleSystemsAndFolderBindingsMutation(
				snapshot,
				systemsChanged: true,
				folderBindingsChanged: false,
				operationName: "Add Folder"
			)
		)
		{
			return;
		}

		ForceExpandFolderPath(targetSystemName, addedFolderPath);
		_addFolderDialog.Hide();
		_pendingAddFolderMetadata = "";
		_addFolderInput.Text = "";
		BuildTree();
		RestoreAddedFolderSelectionAfterRebuild(targetSystemName, addedFolderPath);
	}

	private void RestoreAddedFolderSelectionAfterRebuild(
		string targetSystemName,
		string addedFolderPath
	)
	{
		var addedFolderSelection = new PersistentTreeSelection(
			targetSystemName,
			$"folder::{targetSystemName}::{addedFolderPath}"
		);

		try
		{
			if (
				TryRestoreTreeSelectionByIdentity(
					addedFolderSelection,
					"Add Folder"
				)
			)
			{
				return;
			}

			DebugLogger.LogOperation(
				"Add Folder selection restore warning: exact folder not found",
				$"system='{targetSystemName}', folder='{addedFolderPath}', metadata='{addedFolderSelection.Metadata}'"
			);
			ClearPersistentTreeSelectionAndTreeSelection();
		}
		finally
		{
			CallDeferred(nameof(ReleaseTreeFocusAfterNavigation));
		}
	}

	private FolderMutationResult AddFolderToPendingLocation()
	{
		string systemName = GetSystemNameFromMetadata(_pendingAddFolderMetadata);
		string parentFolderPath = GetFolderPathFromMetadata(_pendingAddFolderMetadata);
		string folderName = _addFolderInput.Text.Trim().Trim('/');

		DebugLogger.LogOperation(
			"Add Folder Target",
			$"System='{systemName}', Parent='{parentFolderPath}', Folder='{folderName}'"
		);

		if (string.IsNullOrWhiteSpace(systemName) || string.IsNullOrWhiteSpace(folderName))
		{
			ReportTreeOperationFailure(
				"System Explorer could not identify the selected location for the new folder.",
				$"PendingMetadata='{_pendingAddFolderMetadata}', System='{systemName}', Folder='{folderName}'"
			);
			return FolderMutationResult.Failed;
		}

		if (!EnsureSystemsLoadedForTreeOperation("Add Folder"))
			return FolderMutationResult.Failed;

		if (!EnsureSystemAvailable(systemName, "Add Folder"))
			return FolderMutationResult.Failed;

		List<string> entries = _systems[systemName];

		string folderPath = string.IsNullOrWhiteSpace(parentFolderPath)
			? folderName
			: $"{parentFolderPath}/{folderName}";

		if (DoesFolderPathExistInSystem(systemName, folderPath))
		{
			DebugLogger.LogOperation(
				"Add Folder failed: name conflict",
				$"{systemName}/{folderPath}"
			);
			return FolderMutationResult.NameConflict;
		}

		entries.Add(BuildFolderEntry(folderPath));
		DebugLogger.LogOperation("Add Folder Mutated", $"{systemName}/{folderPath}");

		return FolderMutationResult.Success;
	}

	private bool TryOpenAddExistingScriptsDialogForSelectedItem()
	{
		if (
			_isFilteringScripts
			|| _tree == null
			|| !GodotObject.IsInstanceValid(_tree)
			|| _tree.IsQueuedForDeletion()
		)
		{
			return false;
		}

		TreeItem selectedItem = _tree.GetSelected();

		if (selectedItem == null || !GodotObject.IsInstanceValid(selectedItem))
			return false;

		string metadata = selectedItem.GetMetadata(0).AsString();
		bool isSupportedTarget =
			metadata.StartsWith("system::", StringComparison.Ordinal)
			|| metadata.StartsWith("folder::", StringComparison.Ordinal)
			|| metadata.StartsWith("script::", StringComparison.Ordinal)
			|| metadata.StartsWith("sceneLink::", StringComparison.Ordinal);

		if (
			!isSupportedTarget
			|| string.IsNullOrWhiteSpace(GetSelectedSystemName())
			|| _fileDialog == null
			|| !GodotObject.IsInstanceValid(_fileDialog)
			|| _fileDialog.IsQueuedForDeletion()
		)
		{
			return false;
		}

		_fileDialog.PopupCenteredRatio(0.8f);
		return true;
	}

	private void OnScriptFilesSelected(string[] paths)
	{
		DebugLogger.LogOperation("Add Existing Scripts Selected", string.Join(", ", paths));

		if (string.IsNullOrWhiteSpace(GetSelectedSystemName()))
		{
			GD.PushWarning("Select a system or folder before adding a script.");
			DebugLogger.LogOperation(
				"Add Existing Scripts cancelled: no selected destination",
				string.Join(", ", paths)
			);
			return;
		}

		string targetSystemName = GetSelectedSystemName();
		string targetFolderPath = GetSelectedFolderPath();

		using TreeOperationDialogScope operationScope =
			BeginTreeOperationDialogScope(
				"Add Scripts Failed",
				closeOriginatingUi: CloseAddScriptUiAfterFailure
			);

		if (!EnsureSystemsLoadedForTreeOperation("Add Script"))
			return;

		if (!EnsureSystemAvailable(targetSystemName, "Add Script"))
			return;

		if (
			!WouldAddScriptsToSelectedTreeLocation(
				paths,
				targetSystemName,
				targetFolderPath
			)
		)
		{
			AddScriptsToSelectedTreeLocation(paths);
			return;
		}

		SystemsAndFolderBindingsSnapshot snapshot =
			CaptureSystemsAndFolderBindingsSnapshot();

		AddTreeMutationResult result = AddScriptsToSelectedTreeLocation(paths);

		if (result == AddTreeMutationResult.NoChange)
			return;

		if (result == AddTreeMutationResult.Failed)
		{
			if (!HasActiveTreeOperationFailure)
			{
				ReportTreeOperationFailure(
					"System Explorer could not add the selected scripts to the verified tree location."
				);
			}

			DebugLogger.LogOperation(
				"Add Existing Scripts cancelled: mutation failed",
				string.Join(", ", paths)
			);
			return;
		}

		if (
			!TryPersistReversibleSystemsAndFolderBindingsMutation(
				snapshot,
				systemsChanged: true,
				folderBindingsChanged: false,
				operationName: "Add Scripts"
			)
		)
		{
			return;
		}

		ForceExpandTreeLocation(targetSystemName, targetFolderPath);
		BuildTree();
	}

	private bool WouldAddScriptsToSelectedTreeLocation(
		IEnumerable<string> paths,
		string systemName,
		string folderPath
	)
	{
		if (
			string.IsNullOrWhiteSpace(systemName)
			|| !_systems.TryGetValue(systemName, out List<string> entries)
			|| entries == null
		)
		{
			return false;
		}

		return paths
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Distinct()
			.Any(path =>
			{
				string linkedScenePath = GetExistingLinkedScenePathForScript(path);
				string entry = BuildScriptEntry(folderPath, path, linkedScenePath);
				return !entries.Contains(entry);
			});
	}

	private AddTreeMutationResult AddScriptToSelectedTreeLocation(string path)
	{
		return AddScriptsToSelectedTreeLocation(new[] { path });
	}

	private AddTreeMutationResult AddScriptsToSelectedTreeLocation(IEnumerable<string> paths)
	{
		List<string> scriptPaths = paths
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Distinct()
			.ToList();

		if (scriptPaths.Count == 0)
			return AddTreeMutationResult.NoChange;

		string unrepresentablePath = scriptPaths.FirstOrDefault(path =>
			!IsPrimaryResourcePathRepresentable(path)
		);

		if (!string.IsNullOrWhiteSpace(unrepresentablePath))
		{
			ReportTreeOperationFailureOrWarning(
				$"System Explorer cannot add this file because its path contains \"|\" which is reserved by the current entry format:\n{unrepresentablePath}",
				$"Path='{unrepresentablePath}'"
			);
			DebugLogger.LogOperation(
				"Add Scripts cancelled: unrepresentable primary resource path",
				unrepresentablePath
			);
			return AddTreeMutationResult.Failed;
		}

		string systemName = GetSelectedSystemName();
		string folderPath = GetSelectedFolderPath();

		DebugLogger.LogOperation(
			"Add Script Target",
			$"Paths='{string.Join(", ", scriptPaths)}', System='{systemName}', Folder='{folderPath}'"
		);

		if (string.IsNullOrWhiteSpace(systemName))
			return AddTreeMutationResult.Failed;

		if (DebugState)
		{
			foreach (string path in scriptPaths)
				PrintScriptCreationDebugInfo(path, systemName, folderPath);
		}

		if (!EnsureSystemsLoadedForTreeOperation("Add Script"))
			return AddTreeMutationResult.Failed;

		if (!EnsureSystemAvailable(systemName, "Add Script"))
			return AddTreeMutationResult.Failed;

		List<string> entries = _systems[systemName];
		bool mutated = false;

		foreach (string path in scriptPaths)
		{
			string linkedScenePath = GetExistingLinkedScenePathForScript(path);
			string entry = BuildScriptEntry(folderPath, path, linkedScenePath);

			if (!entries.Contains(entry))
			{
				entries.Add(entry);
				mutated = true;
				DebugLogger.LogOperation("Add Script Mutated", entry);
			}
			else
			{
				DebugLogger.LogOperation("Add Script skipped: already exists", entry);
			}
		}

		if (!mutated)
			return AddTreeMutationResult.NoChange;

		return AddTreeMutationResult.Success;
	}

	private bool TryOpenAddExistingScenesDialogForSelectedItem()
	{
		if (
			_isFilteringScripts
			|| _tree == null
			|| !GodotObject.IsInstanceValid(_tree)
			|| _tree.IsQueuedForDeletion()
		)
		{
			return false;
		}

		TreeItem selectedItem = _tree.GetSelected();

		if (selectedItem == null || !GodotObject.IsInstanceValid(selectedItem))
			return false;

		string metadata = selectedItem.GetMetadata(0).AsString();
		bool isSupportedTarget =
			metadata.StartsWith("system::", StringComparison.Ordinal)
			|| metadata.StartsWith("folder::", StringComparison.Ordinal)
			|| metadata.StartsWith("script::", StringComparison.Ordinal)
			|| metadata.StartsWith("sceneLink::", StringComparison.Ordinal);

		if (
			!isSupportedTarget
			|| string.IsNullOrWhiteSpace(GetSelectedSystemName())
			|| _addSceneDialog == null
			|| !GodotObject.IsInstanceValid(_addSceneDialog)
			|| _addSceneDialog.IsQueuedForDeletion()
		)
		{
			return false;
		}

		_addSceneDialog.PopupCenteredRatio(0.8f);
		return true;
	}

	private void OnSceneFilesSelected(string[] paths)
	{
		DebugLogger.LogOperation("Add Existing Scenes Selected", string.Join(", ", paths));

		if (string.IsNullOrWhiteSpace(GetSelectedSystemName()))
		{
			GD.PushWarning("Select a system or folder before adding a scene.");
			DebugLogger.LogOperation(
				"Add Existing Scenes cancelled: no selected destination",
				string.Join(", ", paths)
			);
			return;
		}

		string targetSystemName = GetSelectedSystemName();
		string targetFolderPath = GetSelectedFolderPath();

		using TreeOperationDialogScope operationScope =
			BeginTreeOperationDialogScope(
				"Add Scenes Failed",
				closeOriginatingUi: CloseAddSceneUiAfterFailure
			);

		if (!EnsureSystemsLoadedForTreeOperation("Add Scene"))
			return;

		if (!EnsureSystemAvailable(targetSystemName, "Add Scene"))
			return;

		if (
			!WouldAddScenesToSelectedTreeLocation(
				paths,
				targetSystemName,
				targetFolderPath
			)
		)
		{
			AddScenesToSelectedTreeLocation(paths);
			return;
		}

		SystemsAndFolderBindingsSnapshot snapshot =
			CaptureSystemsAndFolderBindingsSnapshot();

		AddTreeMutationResult result = AddScenesToSelectedTreeLocation(paths);

		if (result == AddTreeMutationResult.NoChange)
			return;

		if (result == AddTreeMutationResult.Failed)
		{
			if (!HasActiveTreeOperationFailure)
			{
				ReportTreeOperationFailure(
					"System Explorer could not add the selected scenes to the verified tree location."
				);
			}

			DebugLogger.LogOperation(
				"Add Existing Scenes cancelled: mutation failed",
				string.Join(", ", paths)
			);
			return;
		}

		if (
			!TryPersistReversibleSystemsAndFolderBindingsMutation(
				snapshot,
				systemsChanged: true,
				folderBindingsChanged: false,
				operationName: "Add Scenes"
			)
		)
		{
			return;
		}

		ForceExpandTreeLocation(targetSystemName, targetFolderPath);
		BuildTree();
	}

	private bool WouldAddScenesToSelectedTreeLocation(
		IEnumerable<string> paths,
		string systemName,
		string folderPath
	)
	{
		if (
			string.IsNullOrWhiteSpace(systemName)
			|| !_systems.TryGetValue(systemName, out List<string> entries)
			|| entries == null
		)
		{
			return false;
		}

		return paths
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Distinct()
			.Any(path => !entries.Contains(BuildSceneEntry(folderPath, path)));
	}

	private AddTreeMutationResult AddSceneToSelectedTreeLocation(string path)
	{
		return AddScenesToSelectedTreeLocation(new[] { path });
	}

	private AddTreeMutationResult AddScenesToSelectedTreeLocation(IEnumerable<string> paths)
	{
		List<string> scenePaths = paths
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Distinct()
			.ToList();

		if (scenePaths.Count == 0)
			return AddTreeMutationResult.NoChange;

		string unrepresentablePath = scenePaths.FirstOrDefault(path =>
			!IsPrimaryResourcePathRepresentable(path)
		);

		if (!string.IsNullOrWhiteSpace(unrepresentablePath))
		{
			ReportTreeOperationFailureOrWarning(
				$"System Explorer cannot add this file because its path contains \"|\" which is reserved by the current entry format:\n{unrepresentablePath}",
				$"Path='{unrepresentablePath}'"
			);
			DebugLogger.LogOperation(
				"Add Scenes cancelled: unrepresentable primary resource path",
				unrepresentablePath
			);
			return AddTreeMutationResult.Failed;
		}

		string systemName = GetSelectedSystemName();
		string folderPath = GetSelectedFolderPath();

		DebugLogger.LogOperation(
			"Add Scene Target",
			$"Paths='{string.Join(", ", scenePaths)}', System='{systemName}', Folder='{folderPath}'"
		);

		if (string.IsNullOrWhiteSpace(systemName))
			return AddTreeMutationResult.Failed;

		if (!EnsureSystemsLoadedForTreeOperation("Add Scene"))
			return AddTreeMutationResult.Failed;

		if (!EnsureSystemAvailable(systemName, "Add Scene"))
			return AddTreeMutationResult.Failed;

		List<string> entries = _systems[systemName];
		bool mutated = false;

		foreach (string path in scenePaths)
		{
			string entry = BuildSceneEntry(folderPath, path);

			if (!entries.Contains(entry))
			{
				entries.Add(entry);
				mutated = true;
				DebugLogger.LogOperation("Add Scene Mutated", entry);
			}
			else
			{
				DebugLogger.LogOperation("Add Scene skipped: already exists", entry);
			}
		}

		if (!mutated)
			return AddTreeMutationResult.NoChange;

		return AddTreeMutationResult.Success;
	}

	private void ForceExpandTreeLocation(string systemName, string folderPath)
	{
		if (string.IsNullOrWhiteSpace(systemName))
			return;

		if (string.IsNullOrWhiteSpace(folderPath))
			ForceExpandSystem(systemName);
		else
			ForceExpandFolderPath(systemName, folderPath);
	}

	#endregion

	#region Script Creation
	private bool TryOpenCreateScriptDialogForSelectedItem()
	{
		if (
			_isFilteringScripts
			|| _tree == null
			|| !GodotObject.IsInstanceValid(_tree)
			|| _createScriptDialog == null
			|| !GodotObject.IsInstanceValid(_createScriptDialog)
		)
		{
			return false;
		}

		TreeItem selectedItem = _tree.GetSelected();

		if (selectedItem == null || !GodotObject.IsInstanceValid(selectedItem))
			return false;

		string metadata = selectedItem.GetMetadata(0).AsString();
		bool isSupportedTarget =
			metadata.StartsWith("system::", StringComparison.Ordinal)
			|| metadata.StartsWith("folder::", StringComparison.Ordinal)
			|| metadata.StartsWith("script::", StringComparison.Ordinal)
			|| metadata.StartsWith("sceneLink::", StringComparison.Ordinal);

		if (
			!isSupportedTarget
			|| string.IsNullOrWhiteSpace(GetSelectedSystemName())
		)
		{
			return false;
		}

		_createScriptDialog.CurrentFile = "";
		_createScriptDialog.PopupCenteredRatio(0.8f);
		return true;
	}

	private void OnCreateScriptFileSelected(string path)
	{
		DebugLogger.LogOperation("Create Script Selected", path);

		if (!path.EndsWith(".cs"))
			path += ".cs";

		if (!IsPrimaryResourcePathRepresentable(path))
		{
			ClearCreateScriptFileNameInputBestEffort();
			ShowCreateScriptInputWarning(
				"Invalid Script Path",
				"System Explorer cannot create this script because its path contains \"|\" which is reserved by the current entry format.\n\nNo script file was created."
			);
			DebugLogger.LogOperation(
				"Create Script cancelled: unrepresentable primary resource path",
				path
			);
			return;
		}

		if (FileAccess.FileExists(path))
		{
			GD.PushWarning($"File already exists: {path}");
			DebugLogger.LogOperation("Create Script cancelled: file exists", path);
			return;
		}

		if (string.IsNullOrWhiteSpace(GetSelectedSystemName()))
		{
			GD.PushWarning("Select a system or folder before creating a script.");
			DebugLogger.LogOperation("Create Script cancelled: no selected destination", path);
			return;
		}

		string targetSystemName = GetSelectedSystemName();
		string targetFolderPath = GetSelectedFolderPath();

		using TreeOperationDialogScope operationScope =
			BeginTreeOperationDialogScope(
				"Create Script Failed",
				closeOriginatingUi: CloseCreateScriptUiAfterFailure
			);

		if (!EnsureSystemsLoadedForTreeOperation("Create Script"))
			return;

		if (!EnsureSystemAvailable(targetSystemName, "Create Script"))
			return;

		if (
			!WouldAddScriptsToSelectedTreeLocation(
				new[] { path },
				targetSystemName,
				targetFolderPath
			)
		)
		{
			DebugLogger.LogOperation("Create Script cancelled: metadata addition was a no-op", path);
			return;
		}

		string className = path.GetFile().GetBaseName();

		if (!TryBuildScriptContent(className, out string content))
			return;

		if (
			!TryPreflightMetadataPersistenceForPhysicalMutation(
				"Create Script",
				systemsRequired: true,
				folderBindingsRequired: false,
				physicalConsequence: "No script file was created."
			)
		)
		{
			return;
		}

		if (!TryWriteCreatedScript(path, content))
			return;

		DebugLogger.LogOperation("Create Script File Written", path);

		SystemsAndFolderBindingsSnapshot snapshot =
			WouldAddScriptsToSelectedTreeLocation(
				new[] { path },
				targetSystemName,
				targetFolderPath
			)
				? CaptureSystemsAndFolderBindingsSnapshot()
				: null;

		AddTreeMutationResult addResult = AddScriptToSelectedTreeLocation(path);

		if (addResult == AddTreeMutationResult.Failed)
		{
			ReportTreeOperationFailure(
				"The script file was created, but System Explorer could not add it to the selected tree location.",
				$"Path='{path}'",
				TreeOperationOutcomeSeverity.Incomplete
			);
			EditorInterface.Singleton.GetResourceFilesystem().Scan();
			return;
		}

		if (
			addResult == AddTreeMutationResult.Success
			&& !TryPersistReversibleSystemsAndFolderBindingsMutation(
				snapshot,
				systemsChanged: true,
				folderBindingsChanged: false,
				operationName: "Create Script"
			)
		)
		{
			bool metadataStateUnclear = IsActiveTreeOperationFinalStateUnclear;
			ReportTreeOperationFailure(
				metadataStateUnclear
					? "The script file was created and left in the FileSystem. The local in-memory tree metadata was restored, but the final state of systems.json on disk could not be verified. Restart Godot and inspect systems.json before continuing."
					: "The script file was created, but System Explorer could not save the updated tree metadata. The in-memory tree metadata was restored, and the created script file was left in the FileSystem.",
				$"Path='{path}', InMemoryMetadataRestored=true, CreatedFileRetained=true, MetadataStateUnclear={metadataStateUnclear}",
				metadataStateUnclear
					? TreeOperationOutcomeSeverity.FinalStateUnclear
					: TreeOperationOutcomeSeverity.Incomplete,
				replaceExistingReport: true
			);
			EditorInterface.Singleton.GetResourceFilesystem().Scan();
			return;
		}

		if (addResult == AddTreeMutationResult.Success)
		{
			ForceExpandTreeLocation(targetSystemName, targetFolderPath);
			BuildTree();
		}

		EditorInterface.Singleton.GetResourceFilesystem().Scan();
		CallDeferred(nameof(OpenCreatedScript), path);
	}

	private bool TryBuildScriptContent(string className, out string content)
	{
		content = "";

		if (!FileAccess.FileExists(ScriptTemplatePath) && !EnsureScriptTemplateExists())
			return false;

		FileAccess file;

		try
		{
			file = FileAccess.Open(ScriptTemplatePath, FileAccess.ModeFlags.Read);
		}
		catch (Exception exception)
		{
			ReportTreeOperationFailure(
				"System Explorer could not read the script template, so the script was not created."
			);
			DebugLogger.LogOperation(
				"Create Script cancelled: template open threw",
				$"Path='{ScriptTemplatePath}', Exception='{exception}'"
			);
			return false;
		}

		if (file == null)
		{
			Error openError = FileAccess.GetOpenError();

			ReportTreeOperationFailure(
				"System Explorer could not read the script template, so the script was not created."
			);
			DebugLogger.LogOperation(
				"Create Script cancelled: template open returned null",
				$"Path='{ScriptTemplatePath}', Error='{openError}'"
			);
			return false;
		}

		string template = "";
		string readFailureDetail = "";

		try
		{
			using (file)
			{
				var templateLength = file.GetLength();
				Error lengthError = file.GetError();

				if (lengthError != Error.Ok)
				{
					readFailureDetail =
						$"Path='{ScriptTemplatePath}', Phase='length', Error='{lengthError}'";
				}
				else
				{
					template = file.GetAsText();
					Error readError = file.GetError();

					if (
						readError != Error.Ok
						|| (templateLength > 0 && string.IsNullOrEmpty(template))
					)
					{
						readFailureDetail =
							$"Path='{ScriptTemplatePath}', Phase='read', Error='{readError}', ExpectedBytes='{templateLength}', ActualChars='{template?.Length ?? 0}'";
					}
				}
			}
		}
		catch (Exception exception)
		{
			readFailureDetail =
				$"Path='{ScriptTemplatePath}', Exception='{exception}'";
		}

		if (!string.IsNullOrWhiteSpace(readFailureDetail))
		{
			ReportTreeOperationFailure(
				"System Explorer could not read the script template, so the script was not created."
			);
			DebugLogger.LogOperation(
				"Create Script cancelled: template read",
				readFailureDetail
			);
			return false;
		}

		content = (template ?? "").Replace("{{CLASS_NAME}}", className);
		return true;
	}

	private bool TryWriteCreatedScript(string path, string content)
	{
		FileAccess file;

		try
		{
			file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
		}
		catch (Exception exception)
		{
			ReportTreeOperationFailure($"System Explorer could not create the script file at '{path}'.");
			DebugLogger.LogOperation(
				"Create Script failed: target open threw",
				$"Path='{path}', Exception='{exception}'"
			);
			return false;
		}

		if (file == null)
		{
			Error openError = FileAccess.GetOpenError();

			ReportTreeOperationFailure($"System Explorer could not create the script file at '{path}'.");
			DebugLogger.LogOperation(
				"Create Script failed: target open returned null",
				$"Path='{path}', Error='{openError}'"
			);
			return false;
		}

		string writeFailureDetail = "";

		try
		{
			using (file)
			{
				bool stored = file.StoreString(content ?? "");
				file.Flush();
				Error writeError = file.GetError();

				if (!stored || writeError != Error.Ok)
				{
					writeFailureDetail =
						$"Path='{path}', StoreSucceeded='{stored}', Error='{writeError}'";
				}
			}
		}
		catch (Exception exception)
		{
			writeFailureDetail = $"Path='{path}', Exception='{exception}'";
		}

		if (!string.IsNullOrWhiteSpace(writeFailureDetail))
		{
			ReportTreeOperationFailure($"System Explorer could not create the script file at '{path}'.");
			DebugLogger.LogOperation("Create Script failed: target write", writeFailureDetail);
			return false;
		}

		if (!FileAccess.FileExists(path))
		{
			ReportTreeOperationFailure($"System Explorer could not create the script file at '{path}'.");
			DebugLogger.LogOperation(
				"Create Script failed: target missing after write",
				path
			);
			return false;
		}

		return true;
	}

	private void OpenCreatedScript(string path)
	{
		if (!FileAccess.FileExists(path))
		{
			QueueStandaloneTreeOperationDialog(
				"Create Script Incomplete",
				"The script file was created, but it could not be found when Godot tried to open it.",
				$"Path='{path}'"
			);
			return;
		}

		Script script = ResourceLoader.Load<Script>(path);

		if (script == null)
		{
			QueueStandaloneTreeOperationDialog(
				"Create Script Incomplete",
				"The script file was created, but Godot could not load it in the Script Editor.",
				$"Path='{path}'"
			);
			return;
		}

		OpenScriptFromSystemExplorer(script, path, false);
	}

	#endregion
}
#endif
