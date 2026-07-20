#if TOOLS
using Godot;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

public partial class SystemExplorerPlugin
{
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

		if (FileAccess.FileExists(SavePath) && !EnsureSystemsLoadedForTreeOperation("Add System"))
			return;

		if (!_systems.ContainsKey(systemName))
		{
			_systems[systemName] = new List<string>();
			DebugLogger.LogOperation("Add System Mutated", systemName);
		}
		else
		{
			DebugLogger.LogOperation("Add System skipped: already exists", systemName);
		}

		_systemNameInput.Text = "";
		UpdateSystemNameEnterIconVisibility(_systemNameInput.Text);
		ForceExpandSystem(systemName);

		if (SaveSystems())
			BuildTree();
	}

	private void OpenAddFolderDialog()
	{
		if (string.IsNullOrWhiteSpace(_pendingAddFolderMetadata))
			return;

		_addFolderInput.Text = "";
		_addFolderDialog.PopupCentered();
		_addFolderInput.GrabFocus();
	}

	private void OnAddFolderConfirmed()
	{
		DebugLogger.LogOperation("Add Folder Confirmed", _addFolderInput.Text.Trim());

		if (!AddFolderToPendingLocation())
		{
			DebugLogger.Log("Add Folder cancelled: mutation failed.");
			return;
		}

		_pendingAddFolderMetadata = "";
		_addFolderInput.Text = "";

		if (SaveSystems())
			BuildTree();
	}

	private bool AddFolderToPendingLocation()
	{
		string systemName = GetSystemNameFromMetadata(_pendingAddFolderMetadata);
		string parentFolderPath = GetFolderPathFromMetadata(_pendingAddFolderMetadata);
		string folderName = _addFolderInput.Text.Trim().Trim('/');

		DebugLogger.LogOperation(
			"Add Folder Target",
			$"System='{systemName}', Parent='{parentFolderPath}', Folder='{folderName}'"
		);

		if (string.IsNullOrWhiteSpace(systemName) || string.IsNullOrWhiteSpace(folderName))
			return false;

		if (!EnsureSystemsLoadedForTreeOperation("Add Folder"))
			return false;

		if (!EnsureSystemAvailable(systemName, "Add Folder"))
			return false;

		List<string> entries = _systems[systemName];

		string folderPath = string.IsNullOrWhiteSpace(parentFolderPath)
			? folderName
			: $"{parentFolderPath}/{folderName}";

		string entry = $"folder::{folderPath}";

		if (!entries.Contains(entry))
		{
			entries.Add(entry);
			DebugLogger.LogOperation("Add Folder Mutated", $"{systemName}/{folderPath}");
		}
		else
		{
			DebugLogger.LogOperation("Add Folder skipped: already exists", $"{systemName}/{folderPath}");
		}

		ForceExpandFolderPath(systemName, folderPath);

		return true;
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

		if (!AddScriptsToSelectedTreeLocation(paths))
		{
			DebugLogger.LogOperation(
				"Add Existing Scripts cancelled: mutation failed",
				string.Join(", ", paths)
			);
			return;
		}

		if (SaveSystems())
			BuildTree();
	}

	private bool AddScriptToSelectedTreeLocation(string path)
	{
		return AddScriptsToSelectedTreeLocation(new[] { path });
	}

	private bool AddScriptsToSelectedTreeLocation(IEnumerable<string> paths)
	{
		List<string> scriptPaths = paths
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Distinct()
			.ToList();

		if (scriptPaths.Count == 0)
			return false;

		string systemName = GetSelectedSystemName();
		string folderPath = GetSelectedFolderPath();

		DebugLogger.LogOperation(
			"Add Script Target",
			$"Paths='{string.Join(", ", scriptPaths)}', System='{systemName}', Folder='{folderPath}'"
		);

		if (string.IsNullOrWhiteSpace(systemName))
		{
			GD.PushWarning("Select a system or folder before adding a script.");
			DebugLogger.LogOperation(
				"Add Script failed: no selected system",
				string.Join(", ", scriptPaths)
			);
			return false;
		}

		if (DebugState)
		{
			foreach (string path in scriptPaths)
				PrintScriptCreationDebugInfo(path, systemName, folderPath);
		}

		if (!EnsureSystemsLoadedForTreeOperation("Add Script"))
			return false;

		if (!EnsureSystemAvailable(systemName, "Add Script"))
			return false;

		List<string> entries = _systems[systemName];
		bool mutated = false;

		foreach (string path in scriptPaths)
		{
			string entry = string.IsNullOrWhiteSpace(folderPath) ? path : $"{folderPath}|{path}";

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

		if (mutated)
			ForceExpandTreeLocation(systemName, folderPath);

		return mutated;
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

		if (!AddScenesToSelectedTreeLocation(paths))
		{
			DebugLogger.LogOperation(
				"Add Existing Scenes cancelled: mutation failed",
				string.Join(", ", paths)
			);
			return;
		}

		if (SaveSystems())
			BuildTree();
	}

	private bool AddSceneToSelectedTreeLocation(string path)
	{
		return AddScenesToSelectedTreeLocation(new[] { path });
	}

	private bool AddScenesToSelectedTreeLocation(IEnumerable<string> paths)
	{
		List<string> scenePaths = paths
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Distinct()
			.ToList();

		if (scenePaths.Count == 0)
			return false;

		if (!EnsureSystemsLoadedForTreeOperation("Add Scene"))
			return false;

		string systemName = GetSelectedSystemName();
		string folderPath = GetSelectedFolderPath();

		DebugLogger.LogOperation(
			"Add Scene Target",
			$"Paths='{string.Join(", ", scenePaths)}', System='{systemName}', Folder='{folderPath}'"
		);

		if (string.IsNullOrWhiteSpace(systemName))
		{
			GD.PushWarning("Select a system or folder before adding a scene.");
			DebugLogger.LogOperation(
				"Add Scene failed: no selected system",
				string.Join(", ", scenePaths)
			);
			return false;
		}

		if (!EnsureSystemAvailable(systemName, "Add Scene"))
			return false;

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

		if (mutated)
			ForceExpandTreeLocation(systemName, folderPath);

		return mutated;
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

		if (!EnsureSystemsLoadedForTreeOperation("Create Script"))
			return;

		string className = path.GetFile().GetBaseName();
		string content = BuildScriptContent(className);

		using FileAccess file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
		file.StoreString(content);

		DebugLogger.LogOperation("Create Script File Written", path);

		if (!AddScriptToSelectedTreeLocation(path))
		{
			DebugLogger.LogOperation("Create Script warning: file created but tree mutation failed", path);
			EditorInterface.Singleton.GetResourceFilesystem().Scan();
			return;
		}

		if (SaveSystems())
			BuildTree();

		EditorInterface.Singleton.GetResourceFilesystem().Scan();

		CallDeferred(nameof(OpenCreatedScript), path);
	}

	private string BuildScriptContent(string className)
	{
		if (!FileAccess.FileExists(ScriptTemplatePath))
			EnsureScriptTemplateExists();

		using FileAccess file = FileAccess.Open(ScriptTemplatePath, FileAccess.ModeFlags.Read);
		string template = file.GetAsText();

		return template.Replace("{{CLASS_NAME}}", className);
	}

	private void OpenCreatedScript(string path)
	{
		if (!FileAccess.FileExists(path))
		{
			GD.PushWarning($"Created script does not exist: {path}");
			return;
		}

		Script script = ResourceLoader.Load<Script>(path);

		if (script == null)
		{
			GD.PushWarning($"Could not load created script: {path}");
			return;
		}

		OpenScriptFromSystemExplorer(script, path, false);
	}

	#endregion
}
#endif
