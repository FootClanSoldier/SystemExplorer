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

		DebugLogOperation("Add System Requested", systemName);

		if (string.IsNullOrWhiteSpace(systemName))
		{
			DebugLog("Add System cancelled: empty name.");
			return;
		}

		if (FileAccess.FileExists(SavePath) && !EnsureSystemsLoadedForTreeOperation("Add System"))
			return;

		if (!_systems.ContainsKey(systemName))
		{
			_systems[systemName] = new List<string>();
			DebugLogOperation("Add System Mutated", systemName);
		}
		else
		{
			DebugLogOperation("Add System skipped: already exists", systemName);
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
		DebugLogOperation("Add Folder Confirmed", _addFolderInput.Text.Trim());

		if (!AddFolderToPendingLocation())
		{
			DebugLog("Add Folder cancelled: mutation failed.");
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

		DebugLogOperation(
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
			DebugLogOperation("Add Folder Mutated", $"{systemName}/{folderPath}");
		}
		else
		{
			DebugLogOperation("Add Folder skipped: already exists", $"{systemName}/{folderPath}");
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

	private void OnScriptFileSelected(string path)
	{
		DebugLogOperation("Add Existing Script Selected", path);

		if (!AddScriptToSelectedTreeLocation(path))
		{
			DebugLogOperation("Add Existing Script cancelled: mutation failed", path);
			return;
		}

		if (SaveSystems())
			BuildTree();
	}

	private bool AddScriptToSelectedTreeLocation(string path)
	{
		string systemName = GetSelectedSystemName();
		string folderPath = GetSelectedFolderPath();

		DebugLogOperation(
			"Add Script Target",
			$"Path='{path}', System='{systemName}', Folder='{folderPath}'"
		);

		if (string.IsNullOrWhiteSpace(systemName))
		{
			GD.PushWarning("Select a system or folder before adding a script.");
			DebugLogOperation("Add Script failed: no selected system", path);
			return false;
		}

		if (DebugState)
			PrintScriptCreationDebugInfo(path, systemName, folderPath);

		if (!EnsureSystemsLoadedForTreeOperation("Add Script"))
			return false;

		if (!EnsureSystemAvailable(systemName, "Add Script"))
			return false;

		List<string> entries = _systems[systemName];

		string entry = string.IsNullOrWhiteSpace(folderPath) ? path : $"{folderPath}|{path}";

		if (!entries.Contains(entry))
		{
			entries.Add(entry);
			DebugLogOperation("Add Script Mutated", entry);
		}
		else
		{
			DebugLogOperation("Add Script skipped: already exists", entry);
		}

		ForceExpandForSelectedTreeLocation();

		return true;
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

	private void OnSceneFileSelected(string path)
	{
		DebugLogOperation("Add Existing Scene Selected", path);

		if (!AddSceneToSelectedTreeLocation(path))
		{
			DebugLogOperation("Add Existing Scene cancelled: mutation failed", path);
			return;
		}

		if (SaveSystems())
			BuildTree();
	}

	private bool AddSceneToSelectedTreeLocation(string path)
	{
		if (!EnsureSystemsLoadedForTreeOperation("Add Scene"))
			return false;

		string systemName = GetSelectedSystemName();
		string folderPath = GetSelectedFolderPath();

		DebugLogOperation(
			"Add Scene Target",
			$"Path='{path}', System='{systemName}', Folder='{folderPath}'"
		);

		if (string.IsNullOrWhiteSpace(systemName))
		{
			GD.PushWarning("Select a system or folder before adding a scene.");
			DebugLogOperation("Add Scene failed: no selected system", path);
			return false;
		}

		if (!EnsureSystemAvailable(systemName, "Add Scene"))
			return false;

		List<string> entries = _systems[systemName];
		string entry = BuildSceneEntry(folderPath, path);

		if (!entries.Contains(entry))
		{
			entries.Add(entry);
			DebugLogOperation("Add Scene Mutated", entry);
		}
		else
		{
			DebugLogOperation("Add Scene skipped: already exists", entry);
		}

		ForceExpandForSelectedTreeLocation();

		return true;
	}

	#endregion

	#region Script Creation
	private void OnCreateScriptFileSelected(string path)
	{
		DebugLogOperation("Create Script Selected", path);

		if (!path.EndsWith(".cs"))
			path += ".cs";

		if (FileAccess.FileExists(path))
		{
			GD.PushWarning($"File already exists: {path}");
			DebugLogOperation("Create Script cancelled: file exists", path);
			return;
		}

		if (!EnsureSystemsLoadedForTreeOperation("Create Script"))
			return;

		string className = path.GetFile().GetBaseName();
		string content = BuildScriptContent(className);

		using FileAccess file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
		file.StoreString(content);

		DebugLogOperation("Create Script File Written", path);

		if (!AddScriptToSelectedTreeLocation(path))
		{
			DebugLogOperation("Create Script warning: file created but tree mutation failed", path);
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

		EditorInterface.Singleton.EditScript(script);
	}

	#endregion
}
#endif
