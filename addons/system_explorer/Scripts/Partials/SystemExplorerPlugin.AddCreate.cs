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

		using TreeOperationDialogScope operationScope =
			BeginTreeOperationDialogScope("Add System Failed");

		if (FileAccess.FileExists(SavePath) && !EnsureSystemsLoadedForTreeOperation("Add System"))
			return;

		if (_systems.ContainsKey(systemName))
		{
			DebugLogger.LogOperation("Add System failed: name conflict", systemName);
			_systemNameInput.Text = "";
			UpdateSystemNameEnterIconVisibility(_systemNameInput.Text);
			ShowAddSystemConflictWarning();
			return;
		}

		_systems[systemName] = new List<string>();
		DebugLogger.LogOperation("Add System Mutated", systemName);

		_systemNameInput.Text = "";
		UpdateSystemNameEnterIconVisibility(_systemNameInput.Text);

		if (!SaveSystems())
			return;

		ForceExpandSystem(systemName);
		BuildTree();
	}

	private void OpenAddFolderDialog()
	{
		if (string.IsNullOrWhiteSpace(_pendingAddFolderMetadata))
			return;

		_addFolderInput.Text = "";
		_addFolderDialog.PopupCentered();
		_addFolderInput.Edit(true);
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

		FolderMutationResult result = AddFolderToPendingLocation();

		if (result == FolderMutationResult.NameConflict)
		{
			_addFolderInput.Text = "";
			ShowAddFolderConflictWarning();
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

		if (!SaveSystems())
			return;

		ForceExpandFolderPath(targetSystemName, addedFolderPath);
		_addFolderDialog.Hide();
		_pendingAddFolderMetadata = "";
		_addFolderInput.Text = "";
		BuildTree();
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

	private void OnAddScriptPressed()
	{
		string systemName = GetSelectedSystemName();

		if (string.IsNullOrWhiteSpace(systemName))
		{
			GD.PushWarning("Select a system or folder before adding a script.");
			return;
		}

		_fileDialog.PopupCenteredRatio(0.8f);
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

		if (!SaveSystems())
			return;

		ForceExpandTreeLocation(targetSystemName, targetFolderPath);
		BuildTree();
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

	private void OnAddScenePressed()
	{
		string systemName = GetSelectedSystemName();

		if (string.IsNullOrWhiteSpace(systemName))
		{
			GD.PushWarning("Select a system or folder before adding a scene.");
			return;
		}

		_addSceneDialog.PopupCenteredRatio(0.8f);
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

		if (!SaveSystems())
			return;

		ForceExpandTreeLocation(targetSystemName, targetFolderPath);
		BuildTree();
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
	private void OnCreateScriptFileSelected(string path)
	{
		DebugLogger.LogOperation("Create Script Selected", path);

		if (!path.EndsWith(".cs"))
			path += ".cs";

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

		string className = path.GetFile().GetBaseName();

		if (!TryBuildScriptContent(className, out string content))
			return;

		if (!TryWriteCreatedScript(path, content))
			return;

		DebugLogger.LogOperation("Create Script File Written", path);

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

		if (addResult == AddTreeMutationResult.Success && !SaveSystems())
		{
			bool metadataStateUnclear = IsActiveTreeOperationFinalStateUnclear;
			ReportTreeOperationFailure(
				metadataStateUnclear
					? "The script file was created, but the final state of System Explorer's updated metadata could not be verified."
					: "The script file was created, but System Explorer could not save the updated tree metadata.",
				$"Path='{path}'",
				metadataStateUnclear
					? TreeOperationOutcomeSeverity.FinalStateUnclear
					: TreeOperationOutcomeSeverity.Incomplete
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
