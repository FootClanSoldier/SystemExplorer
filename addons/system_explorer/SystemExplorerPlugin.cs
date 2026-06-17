#if TOOLS
using Godot;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

[Tool]
public partial class SystemExplorerPlugin : EditorPlugin
{
	#region Constants and Fields
	private const string SavePath = "res://addons/system_explorer/systems.json";
	private const string ScriptTemplatePath = "res://addons/system_explorer/script_template.txt";

	// Enable only when investigating editor state/save issues.
	private const bool DebugState = false;

	private const int ContextAddFolder = 0;
	private const int ContextAddScript = 1;
	private const int ContextNewScript = 2;
	private const int ContextRename = 3;
	private const int ContextRemove = 4;
	private const float ClickOpenDragThreshold = 6.0f;

	private EditorDock _editorDock;
	private VBoxContainer _dock;
	private LineEdit _systemNameInput;
	private Button _addSystemButton;
	private Tree _tree;
	private EditorFileDialog _fileDialog;
	private PopupMenu _contextMenu;
	private ConfirmationDialog _removeDialog;
	private CheckBox _removeFromFilesystemCheckBox;
	private AcceptDialog _renameDialog;
	private LineEdit _renameInput;
	private AcceptDialog _addFolderDialog;
	private LineEdit _addFolderInput;
	private EditorFileDialog _createScriptDialog;
	private EditorFileDialog _relinkScriptDialog;
	private ConfirmationDialog _missingScriptDialog;

	private string _pendingRemoveMetadata = "";
	private string _pendingRenameMetadata = "";
	private string _pendingAddFolderMetadata = "";
	private string _draggedMetadata = "";
	private string _draggedSourceSystemName = "";
	private string _draggedSourceFolderPath = "";
	private bool _leftMousePressedOnSelectedScript;
	private Vector2 _leftMousePressPosition;
	private string _leftMousePressedMetadata = "";
	private string _pendingMissingScriptEntry = "";
	private string _pendingMissingScriptPath = "";

	private Texture2D _scriptIcon;
	private Texture2D _systemIcon;
	private Texture2D _folderIcon;
	private Color _systemColor = Color.FromHtml("#6495ED");
	private Color _folderColor = Color.FromHtml("#F2C252");

	private readonly Dictionary<string, List<string>> _systems = new();
	private readonly HashSet<string> _expandedItems = new();
	private readonly HashSet<string> _forcedExpandedItems = new();

	#endregion

	#region Lifecycle and Dock Setup
	public override void _EnterTree()
	{
		DebugLogOperation("Enter Tree");

		var editorTheme = EditorInterface.Singleton.GetEditorTheme();
		_scriptIcon = editorTheme.GetIcon("CSharpScript", "EditorIcons");
		_systemIcon = editorTheme.GetIcon("Environment", "EditorIcons");
		_folderIcon = editorTheme.GetIcon("Folder", "EditorIcons");

		EnsureScriptTemplateExists();
		BuildDock();
		LoadSystems();

		_editorDock = new EditorDock
		{
			Title = "System Explorer",
			DefaultSlot = EditorDock.DockSlot.LeftBl
		};

		_editorDock.AddChild(_dock);
		AddDock(_editorDock);

		BuildTree();

		DebugLogStateSnapshot("Enter Tree Complete");
	}

	private void EnsureScriptTemplateExists()
	{
		if (FileAccess.FileExists(ScriptTemplatePath))
			return;

		string defaultTemplate =
            @"using Godot;

public sealed class {{CLASS_NAME}}
{
}
";

		using FileAccess file = FileAccess.Open(ScriptTemplatePath, FileAccess.ModeFlags.Write);
		file.StoreString(defaultTemplate);

		EditorInterface.Singleton.GetResourceFilesystem().Scan();
	}

	public override void _ExitTree()
	{
		DebugLogOperation("Exit Tree");

		if (_editorDock == null)
			return;

		RemoveDock(_editorDock);
		_editorDock.QueueFree();

		_editorDock = null;
		_dock = null;
	}

	private void BuildDock()
	{
		_dock = new VBoxContainer { Name = "System Explorer" };

		_systemNameInput = new LineEdit { PlaceholderText = "System name..." };
		_addSystemButton = new Button { Text = "Add System" };

		_tree = new Tree { HideRoot = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill };

		_fileDialog = new EditorFileDialog
		{
			FileMode = EditorFileDialog.FileModeEnum.OpenFile,
			Access = EditorFileDialog.AccessEnum.Resources,
			Title = "Select C# Script"
		};

		_createScriptDialog = new EditorFileDialog
		{
			FileMode = EditorFileDialog.FileModeEnum.SaveFile,
			Access = EditorFileDialog.AccessEnum.Resources,
			Title = "Create C# Script"
		};

		_relinkScriptDialog = new EditorFileDialog
		{
			FileMode = EditorFileDialog.FileModeEnum.OpenFile,
			Access = EditorFileDialog.AccessEnum.Resources,
			Title = "Relink C# Script"
		};

		_fileDialog.Filters = new[] { "*.cs ; C# Scripts" };
		_createScriptDialog.Filters = new[] { "*.cs ; C# Scripts" };
		_relinkScriptDialog.Filters = new[] { "*.cs ; C# Scripts" };

		_contextMenu = new PopupMenu();
		_contextMenu.AddItem("New Folder", ContextAddFolder);
		_contextMenu.AddItem("New Script", ContextNewScript);
		_contextMenu.AddItem("Add Script", ContextAddScript);
		_contextMenu.AddSeparator();
		_contextMenu.AddItem("Rename", ContextRename);
		_contextMenu.AddItem("Remove", ContextRemove);

		_removeDialog = new ConfirmationDialog
		{
			Title = "Remove Item",
			DialogText = "Remove selected item from System Explorer?",
			MinSize = new Vector2I(420, 220)
		};

		var removeDialogContainer = new VBoxContainer();

		removeDialogContainer.AddChild(new Control { CustomMinimumSize = new Vector2(0, 68) });

		_removeFromFilesystemCheckBox = new CheckBox
		{
			Text = "Also delete script file(s) from FileSystem",
			ButtonPressed = false
		};

		removeDialogContainer.AddChild(_removeFromFilesystemCheckBox);

		_removeDialog.AddChild(removeDialogContainer);

		_missingScriptDialog = new ConfirmationDialog
		{
			Title = "Script Not Found",
			DialogText = "The script file could not be found.",
			MinSize = new Vector2I(520, 220),
			OkButtonText = "Relink Script..."
		};

		_missingScriptDialog.AddButton("Remove from Plugin", false, "remove_from_plugin");

		_renameDialog = new AcceptDialog { Title = "Rename Item", MinSize = new Vector2I(350, 0) };

		_renameInput = new LineEdit { PlaceholderText = "New name..." };
		_renameDialog.AddChild(_renameInput);

		_addFolderDialog = new AcceptDialog
		{
			Title = "Add Folder",
			MinSize = new Vector2I(350, 0)
		};

		_addFolderInput = new LineEdit { PlaceholderText = "Folder name..." };
		_addFolderDialog.AddChild(_addFolderInput);

		_createScriptDialog.FileSelected += OnCreateScriptFileSelected;
		_relinkScriptDialog.FileSelected += OnRelinkScriptFileSelected;
		_missingScriptDialog.Confirmed += OnMissingScriptRelinkPressed;
		_missingScriptDialog.CustomAction += OnMissingScriptCustomAction;
		_addSystemButton.Pressed += OnAddSystemPressed;
		_tree.ItemSelected += OnItemSelected;
		_tree.GuiInput += OnTreeGuiInput;
		_fileDialog.FileSelected += OnScriptFileSelected;
		_contextMenu.IdPressed += OnContextMenuIdPressed;
		_removeDialog.Confirmed += OnRemoveConfirmed;
		_renameDialog.Confirmed += OnRenameConfirmed;
		_addFolderDialog.Confirmed += OnAddFolderConfirmed;

		_dock.AddChild(_systemNameInput);
		_dock.AddChild(_addSystemButton);
		_dock.AddChild(_tree);
		_dock.AddChild(_fileDialog);
		_dock.AddChild(_relinkScriptDialog);
		_dock.AddChild(_contextMenu);
		_dock.AddChild(_removeDialog);
		_dock.AddChild(_missingScriptDialog);
		_dock.AddChild(_renameDialog);
		_dock.AddChild(_addFolderDialog);
		_dock.AddChild(_createScriptDialog);
	}

	#endregion

	#region Tree Building and Expansion State
	private void BuildTree()
	{
		DebugLogOperation("Build Tree", $"{_systems.Count} systems");

		SaveExpansionState();
		MergeForcedExpansionState();
		NormalizeAllSystemEntries();

		_tree.Clear();

		TreeItem root = _tree.CreateItem();

		foreach (KeyValuePair<string, List<string>> system in _systems)
		{
			TreeItem systemItem = _tree.CreateItem(root);
			systemItem.SetText(0, system.Key);
			systemItem.SetIcon(0, _systemIcon);
			systemItem.SetIconModulate(0, _systemColor);
			systemItem.SetMetadata(0, $"system::{system.Key}");
			systemItem.Collapsed = true;

			Dictionary<string, TreeItem> folders = new();

			foreach (string entry in system.Value.Where(entry => entry.StartsWith("folder::")))
			{
				CreateFolderPath(systemItem, folders, system.Key, entry.Replace("folder::", ""));
			}

			foreach (string entry in system.Value.Where(entry => !entry.StartsWith("folder::")))
			{
				string folderPath = GetFolderPathFromEntry(entry);
				TreeItem parent = string.IsNullOrWhiteSpace(folderPath)
					? systemItem
					: CreateFolderPath(systemItem, folders, system.Key, folderPath);

				TreeItem scriptItem = _tree.CreateItem(parent);
				scriptItem.SetText(0, GetScriptPathFromEntry(entry).GetFile());
				scriptItem.SetIcon(0, _scriptIcon);
				scriptItem.SetMetadata(0, $"script::{entry}");
			}
		}

		RestoreExpansionState(root);
	}

	private void MergeForcedExpansionState()
	{
		foreach (string metadata in _forcedExpandedItems)
		{
			if (!string.IsNullOrWhiteSpace(metadata))
				_expandedItems.Add(metadata);
		}

		_forcedExpandedItems.Clear();
	}

	private void ForceExpandSystem(string systemName)
	{
		if (string.IsNullOrWhiteSpace(systemName))
			return;

		_forcedExpandedItems.Add($"system::{systemName}");
	}

	private void ForceExpandFolderPath(string systemName, string folderPath)
	{
		ForceExpandSystem(systemName);

		if (string.IsNullOrWhiteSpace(systemName) || string.IsNullOrWhiteSpace(folderPath))
			return;

		string[] parts = folderPath.Split("/", System.StringSplitOptions.RemoveEmptyEntries);
		string currentPath = "";

		foreach (string part in parts)
		{
			currentPath = string.IsNullOrWhiteSpace(currentPath) ? part : $"{currentPath}/{part}";
			_forcedExpandedItems.Add($"folder::{systemName}::{currentPath}");
		}
	}

	private void ForceExpandForSelectedTreeLocation()
	{
		string systemName = GetSelectedSystemName();
		string folderPath = GetSelectedFolderPath();

		if (string.IsNullOrWhiteSpace(systemName))
			return;

		if (string.IsNullOrWhiteSpace(folderPath))
			ForceExpandSystem(systemName);
		else
			ForceExpandFolderPath(systemName, folderPath);
	}

	private void SaveExpansionState()
	{
		_expandedItems.Clear();

		if (_tree == null)
			return;

		TreeItem root = _tree.GetRoot();

		if (root == null)
			return;

		SaveExpandedRecursive(root);
	}

	private void SaveExpandedRecursive(TreeItem item)
	{
		TreeItem current = item;

		while (current != null)
		{
			string metadata = current.GetMetadata(0).AsString();

			if (!string.IsNullOrWhiteSpace(metadata) && !current.Collapsed)
				_expandedItems.Add(metadata);

			TreeItem child = current.GetFirstChild();

			if (child != null)
				SaveExpandedRecursive(child);

			current = current.GetNext();
		}
	}

	private void RestoreExpansionState(TreeItem item)
	{
		TreeItem current = item;

		while (current != null)
		{
			string metadata = current.GetMetadata(0).AsString();

			if (_expandedItems.Contains(metadata))
				current.Collapsed = false;

			TreeItem child = current.GetFirstChild();

			if (child != null)
				RestoreExpansionState(child);

			current = current.GetNext();
		}
	}

	private TreeItem CreateFolderPath(
		TreeItem systemItem,
		Dictionary<string, TreeItem> folders,
		string systemName,
		string folderPath
	)
	{
		string[] parts = folderPath.Split("/", System.StringSplitOptions.RemoveEmptyEntries);

		TreeItem parent = systemItem;
		string currentPath = "";

		foreach (string part in parts)
		{
			currentPath = string.IsNullOrWhiteSpace(currentPath) ? part : $"{currentPath}/{part}";

			if (!folders.ContainsKey(currentPath))
			{
				TreeItem folderItem = _tree.CreateItem(parent);
				folderItem.SetText(0, part);
				folderItem.SetIcon(0, _folderIcon);
				folderItem.SetIconModulate(0, _folderColor);
				folderItem.SetMetadata(0, $"folder::{systemName}::{currentPath}");
				folderItem.Collapsed = true;
				folders[currentPath] = folderItem;
			}

			parent = folders[currentPath];
		}

		return parent;
	}

	#endregion

	#region Add Existing Items
	private void OnAddSystemPressed()
	{
		string systemName = _systemNameInput.Text.Trim();

		DebugLogOperation("Add System Requested", systemName);

		if (string.IsNullOrWhiteSpace(systemName))
		{
			DebugLog("Add System cancelled: empty name.");
			return;
		}

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

	private void OnItemSelected()
	{
		OpenScriptFromTreeItem(_tree.GetSelected());
	}

	private void OpenScriptFromTreeItem(TreeItem item)
	{
		if (item == null)
			return;

		string metadata = item.GetMetadata(0).AsString();

		if (!metadata.StartsWith("script::"))
			return;

		string entry = metadata.Replace("script::", "");
		string scriptPath = GetScriptPathFromEntry(entry);

		OpenScriptOrMissingDialog(entry, scriptPath);
	}

	private void OpenScriptOrMissingDialog(string entry, string scriptPath)
	{
		if (!FileAccess.FileExists(scriptPath))
		{
			OpenMissingScriptDialog(entry, scriptPath);
			return;
		}

		Script script = ResourceLoader.Load<Script>(scriptPath);

		if (script == null)
		{
			OpenMissingScriptDialog(entry, scriptPath);
			return;
		}

		EditorInterface.Singleton.EditScript(script);
	}

	private void OpenMissingScriptDialog(string entry, string scriptPath)
	{
		_pendingMissingScriptEntry = entry;
		_pendingMissingScriptPath = scriptPath;

		_missingScriptDialog.DialogText =
			$"The script file could not be found: {scriptPath}\n\nIt may have been moved, renamed, or deleted outside System Explorer.";

		_missingScriptDialog.PopupCentered();
		CallDeferred(nameof(ReleaseMissingDialogFocus));
	}
	private void ReleaseMissingDialogFocus()
	{
		ReleaseDialogOkButtonFocus(_missingScriptDialog);
	}

	private void OnMissingScriptRelinkPressed()
	{
		if (string.IsNullOrWhiteSpace(_pendingMissingScriptEntry))
			return;

		_relinkScriptDialog.PopupCenteredRatio(0.8f);
	}

	private void OnMissingScriptCustomAction(StringName action)
	{
		if (action != "remove_from_plugin")
			return;

		RemoveMissingScriptFromPlugin();
	}

	private void RemoveMissingScriptFromPlugin()
	{
		if (string.IsNullOrWhiteSpace(_pendingMissingScriptEntry))
			return;

		if (!RemoveEntry(_pendingMissingScriptEntry))
		{
			DebugLogOperation(
				"Remove Missing Script cancelled: mutation failed",
				_pendingMissingScriptEntry
			);
			return;
		}

		_missingScriptDialog.Hide();

		ClearMissingScriptState();

		if (SaveSystems())
			BuildTree();
	}

	private void OnRelinkScriptFileSelected(string newScriptPath)
	{
		if (string.IsNullOrWhiteSpace(_pendingMissingScriptEntry))
			return;

		string oldEntry = _pendingMissingScriptEntry;
		string folderPath = GetFolderPathFromEntry(oldEntry);
		string newEntry = string.IsNullOrWhiteSpace(folderPath)
			? newScriptPath
			: $"{folderPath}|{newScriptPath}";

		if (!ReplaceEntry(oldEntry, newEntry))
		{
			DebugLogOperation(
				"Relink Script cancelled: mutation failed",
				$"{oldEntry} -> {newEntry}"
			);
			return;
		}

		ClearMissingScriptState();

		if (SaveSystems())
			BuildTree();

		OpenScriptOrMissingDialog(newEntry, newScriptPath);
	}

	private bool ReplaceEntry(string oldEntry, string newEntry)
	{
		foreach (string systemName in _systems.Keys.ToList())
		{
			List<string> entries = _systems[systemName];
			int index = entries.IndexOf(oldEntry);

			if (index < 0)
				continue;

			entries.RemoveAt(index);

			if (!entries.Contains(newEntry))
				entries.Insert(index, newEntry);

			DebugLogOperation("Relink Script Mutated", $"{oldEntry} -> {newEntry}");

			return true;
		}

		if (TryRecoverSystemsFromDisk("Relink Script"))
		{
			foreach (string systemName in _systems.Keys.ToList())
			{
				List<string> entries = _systems[systemName];
				int index = entries.IndexOf(oldEntry);

				if (index < 0)
					continue;

				entries.RemoveAt(index);

				if (!entries.Contains(newEntry))
					entries.Insert(index, newEntry);

				DebugLogOperation(
					"Relink Script Mutated After Recovery",
					$"{oldEntry} -> {newEntry}"
				);

				return true;
			}
		}

		return false;
	}

	private void ClearMissingScriptState()
	{
		_pendingMissingScriptEntry = "";
		_pendingMissingScriptPath = "";
	}

	private void PrintScriptCreationDebugInfo(string path, string systemName, string folderPath)
	{
		DebugLog("=== Script Creation Debug ===");
		DebugLog($"Path: {path}");
		DebugLog($"Selected System: '{systemName}'");
		DebugLog($"Selected Folder: '{folderPath}'");
		DebugLog($"Systems Count: {_systems.Count}");

		TreeItem selectedItem = _tree.GetSelected();

		if (selectedItem != null)
		{
			DebugLog($"Selected Text: '{selectedItem.GetText(0)}'");
			DebugLog($"Selected Metadata: '{selectedItem.GetMetadata(0).AsString()}'");
		}
		else
		{
			DebugLog("Selected Item: <null>");
		}

		DebugLogSystems("Script Creation Systems Snapshot");
		DebugLog("=============================");
	}

	#endregion

	#region Tree Input and Context Menu
	private void OnTreeGuiInput(InputEvent inputEvent)
	{
		if (inputEvent is not InputEventMouseButton mouseButton)
			return;

		Vector2 mousePosition = _tree.GetLocalMousePosition();
		TreeItem item = _tree.GetItemAtPosition(mousePosition);

		if (mouseButton.ButtonIndex == MouseButton.Left)
		{
			if (IsShiftPressed(mouseButton))
			{
				ClearDragState();

				if (mouseButton.Pressed)
					ToggleExpandedIfSystemOrFolder(item);

				_tree.AcceptEvent();
				return;
			}

			if (mouseButton.Pressed)
			{
				_draggedMetadata = item?.GetMetadata(0).AsString() ?? "";
				_draggedSourceSystemName = item == null ? "" : GetSystemNameFromTreeItem(item);
				_draggedSourceFolderPath = item == null ? "" : GetFolderPathFromTreeItem(item);
				_leftMousePressPosition = mousePosition;
				_leftMousePressedMetadata = _draggedMetadata;
				_leftMousePressedOnSelectedScript = IsSelectedScriptItem(item);
			}
			else
			{
				if (string.IsNullOrWhiteSpace(_draggedMetadata) || item == null)
				{
					ClearDragState();
					return;
				}

				string releaseMetadata = item.GetMetadata(0).AsString();
				bool isClick =
					_leftMousePressedOnSelectedScript
					&& _leftMousePressedMetadata == releaseMetadata
					&& _leftMousePressPosition.DistanceTo(mousePosition) <= ClickOpenDragThreshold;

				if (isClick)
				{
					OpenScriptFromTreeItem(item);
					ClearDragState();
					return;
				}

				MoveDraggedItem(_draggedMetadata, item);

				ClearDragState();
			}

			return;
		}

		if (!mouseButton.Pressed || mouseButton.ButtonIndex != MouseButton.Right)
			return;

		if (item == null)
			return;

		item.Select(0);

		string rightClickMetadata = item.GetMetadata(0).AsString();

		_pendingRemoveMetadata = rightClickMetadata;
		_pendingRenameMetadata = rightClickMetadata;
		_pendingAddFolderMetadata = rightClickMetadata;

		bool canRename =
			rightClickMetadata.StartsWith("system::")
			|| rightClickMetadata.StartsWith("folder::")
			|| rightClickMetadata.StartsWith("script::");

		bool canAddFolder =
			rightClickMetadata.StartsWith("system::") || rightClickMetadata.StartsWith("folder::");

		SetContextMenuItemDisabled(ContextRename, !canRename);
		SetContextMenuItemDisabled(ContextAddFolder, !canAddFolder);

		_contextMenu.Position = DisplayServer.MouseGetPosition();
		_contextMenu.Popup();
	}

	private static bool IsShiftPressed(InputEventMouseButton mouseButton)
	{
		return mouseButton.ShiftPressed || Input.IsKeyPressed(Key.Shift);
	}

	private static void ToggleExpandedIfSystemOrFolder(TreeItem item)
	{
		if (item == null)
			return;

		string metadata = item.GetMetadata(0).AsString();

		if (!metadata.StartsWith("system::") && !metadata.StartsWith("folder::"))
			return;

		item.Collapsed = !item.Collapsed;
	}

	private bool IsSelectedScriptItem(TreeItem item)
	{
		if (item == null)
			return false;

		if (_tree.GetSelected() != item)
			return false;

		string metadata = item.GetMetadata(0).AsString();

		return metadata.StartsWith("script::");
	}

	private void SetContextMenuItemDisabled(int id, bool disabled)
	{
		int index = _contextMenu.GetItemIndex(id);

		if (index < 0)
			return;

		_contextMenu.SetItemDisabled(index, disabled);
	}

	private void OnContextMenuIdPressed(long id)
	{
		switch (id)
		{
			case ContextAddFolder:
				OpenAddFolderDialog();
				break;

			case ContextAddScript:
				OnAddScriptPressed();
				break;

			case ContextNewScript:
				_createScriptDialog.CurrentFile = "";
				_createScriptDialog.PopupCenteredRatio(0.8f);
				break;

			case ContextRename:
				OpenRenameDialog();
				break;

			case ContextRemove:
				OpenRemoveDialog();
				break;
		}
	}

	private void OpenRemoveDialog()
	{
		if (string.IsNullOrWhiteSpace(_pendingRemoveMetadata))
			return;

		_removeFromFilesystemCheckBox.ButtonPressed = false;
		_removeFromFilesystemCheckBox.Visible = true;

		if (_pendingRemoveMetadata.StartsWith("system::"))
		{
			_removeDialog.Title = "Remove System";
			_removeDialog.DialogText = "Remove selected system from System Explorer?";
			_removeFromFilesystemCheckBox.Text = "Also delete scripts from FileSystem";
		}
		else if (_pendingRemoveMetadata.StartsWith("folder::"))
		{
			_removeDialog.Title = "Remove Folder";
			_removeDialog.DialogText = "Remove selected folder from System Explorer?";
			_removeFromFilesystemCheckBox.Text = "Also delete scripts from FileSystem";
		}
		else if (_pendingRemoveMetadata.StartsWith("script::"))
		{
			_removeDialog.Title = "Remove Script";
			_removeDialog.DialogText = "Remove selected script from System Explorer?";
			_removeFromFilesystemCheckBox.Text = "Also delete script from FileSystem";
		}
		else
		{
			_removeDialog.DialogText = "Remove selected item from System Explorer?";
		}

		_removeDialog.PopupCentered();
		CallDeferred(nameof(ReleaseRemoveDialogFocus));
	}

	private void ReleaseRemoveDialogFocus()
	{
		ReleaseDialogOkButtonFocus(_removeDialog);
	}

	private static void ReleaseDialogOkButtonFocus(ConfirmationDialog dialog)
	{
		if (dialog == null)
			return;

		dialog.GetOkButton()?.ReleaseFocus();
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

	#region Drag and Drop Reordering
	private void MoveDraggedItem(string draggedMetadata, TreeItem targetItem)
	{
		string targetMetadata = targetItem?.GetMetadata(0).AsString() ?? "";

		DebugLogOperation(
			"Drag Move Requested",
			$"Dragged='{draggedMetadata}', Target='{targetMetadata}'"
		);

		if (string.IsNullOrWhiteSpace(draggedMetadata) || string.IsNullOrWhiteSpace(targetMetadata))
		{
			DebugLog("Drag Move cancelled: missing metadata.");
			return;
		}

		bool moved = false;

		if (draggedMetadata.StartsWith("system::") && targetMetadata.StartsWith("system::"))
		{
			if (draggedMetadata == targetMetadata)
			{
				DebugLog("Drag Move cancelled: dragged and target metadata are identical.");
				return;
			}

			moved = MoveSystem(draggedMetadata, targetMetadata);
		}
		else if (draggedMetadata.StartsWith("script::") && IsValidScriptDropTargetMetadata(targetMetadata))
		{
			moved = MoveScriptToDropTarget(draggedMetadata, targetItem);
		}
		else if (
			draggedMetadata != targetMetadata
			&& IsSystemEntryMetadata(draggedMetadata)
			&& IsSystemEntryMetadata(targetMetadata)
		)
		{
			moved = MoveSystemEntry(draggedMetadata, targetMetadata);
		}

		if (!moved)
		{
			DebugLogOperation(
				"Drag Move cancelled: mutation failed",
				$"Dragged='{draggedMetadata}', Target='{targetMetadata}'"
			);
			return;
		}

		if (SaveSystems())
			BuildTree();
	}

	private bool MoveSystem(string draggedMetadata, string targetMetadata)
	{
		string draggedSystemName = draggedMetadata.Replace("system::", "");
		string targetSystemName = targetMetadata.Replace("system::", "");

		if (!EnsureSystemsAvailable(new[] { draggedSystemName, targetSystemName }, "Move System"))
			return false;

		List<KeyValuePair<string, List<string>>> orderedSystems = _systems.ToList();

		int draggedIndex = orderedSystems.FindIndex(system => system.Key == draggedSystemName);
		int targetIndex = orderedSystems.FindIndex(system => system.Key == targetSystemName);

		if (draggedIndex < 0 || targetIndex < 0 || draggedIndex == targetIndex)
			return false;

		bool moveDown = draggedIndex < targetIndex;
		KeyValuePair<string, List<string>> draggedSystem = orderedSystems[draggedIndex];

		orderedSystems.RemoveAt(draggedIndex);

		targetIndex = orderedSystems.FindIndex(system => system.Key == targetSystemName);

		if (targetIndex < 0)
			return false;

		int insertIndex = moveDown ? targetIndex + 1 : targetIndex;

		orderedSystems.Insert(insertIndex, draggedSystem);

		_systems.Clear();

		foreach (KeyValuePair<string, List<string>> system in orderedSystems)
			_systems[system.Key] = system.Value;

		DebugLogOperation("Move System Mutated", $"{draggedSystemName} -> {targetSystemName}");

		return true;
	}

	private bool MoveScriptToDropTarget(string draggedMetadata, TreeItem targetItem)
	{
		if (targetItem == null)
			return false;

		string targetMetadata = targetItem.GetMetadata(0).AsString();

		if (!IsValidScriptDropTargetMetadata(targetMetadata))
			return false;

		string draggedEntry = GetEntryFromMetadata(draggedMetadata);
		string draggedScriptPath = GetScriptPathFromEntry(draggedEntry);

		if (string.IsNullOrWhiteSpace(draggedEntry) || string.IsNullOrWhiteSpace(draggedScriptPath))
			return false;

		string sourceSystemName = _draggedSourceSystemName;

		if (string.IsNullOrWhiteSpace(sourceSystemName))
			sourceSystemName = FindSystemNameForEntry(draggedEntry);

		string targetSystemName = GetSystemNameFromTreeItem(targetItem);
		string targetFolderPath = GetDropFolderPathFromTargetItem(targetItem);
		string targetEntry = targetMetadata.StartsWith("script::")
			? GetEntryFromMetadata(targetMetadata)
			: "";

		if (string.IsNullOrWhiteSpace(sourceSystemName) || string.IsNullOrWhiteSpace(targetSystemName))
		{
			TryRecoverSystemsFromDisk("Move Script Resolve Systems");

			if (string.IsNullOrWhiteSpace(sourceSystemName))
				sourceSystemName = FindSystemNameForEntry(draggedEntry);

			if (string.IsNullOrWhiteSpace(targetSystemName))
				targetSystemName = GetSystemNameFromTreeItem(targetItem);
		}

		if (
			!EnsureSystemsAvailable(
				new[] { sourceSystemName, targetSystemName },
				"Move Script"
			)
		)
		{
			return false;
		}

		List<string> sourceEntries = _systems[sourceSystemName];
		List<string> targetEntries = _systems[targetSystemName];

		int sourceIndex = sourceEntries.IndexOf(draggedEntry);

		if (sourceIndex < 0)
		{
			if (!TryRecoverSystemsFromDisk("Move Script Source Entry"))
				return false;

			if (!_systems.ContainsKey(sourceSystemName) || !_systems.ContainsKey(targetSystemName))
				return false;

			sourceEntries = _systems[sourceSystemName];
			targetEntries = _systems[targetSystemName];
			sourceIndex = sourceEntries.IndexOf(draggedEntry);

			if (sourceIndex < 0)
				return false;
		}

		string newEntry = string.IsNullOrWhiteSpace(targetFolderPath)
			? draggedScriptPath
			: $"{targetFolderPath}|{draggedScriptPath}";

		bool sameSystem = sourceSystemName == targetSystemName;
		bool sameLocation = sameSystem && draggedEntry == newEntry;

		if (sameLocation && !targetMetadata.StartsWith("script::"))
			return false;

		if (sameLocation && targetMetadata.StartsWith("script::") && draggedEntry == targetEntry)
			return false;

		int targetIndexBeforeRemove = targetMetadata.StartsWith("script::")
			? targetEntries.IndexOf(targetEntry)
			: -1;

		if (targetMetadata.StartsWith("script::") && targetIndexBeforeRemove < 0)
			return false;

		sourceEntries.RemoveAt(sourceIndex);

		if (targetMetadata.StartsWith("script::"))
		{
			if (sameSystem)
				targetEntries = sourceEntries;

			int targetIndexAfterRemove = targetEntries.IndexOf(targetEntry);

			if (targetIndexAfterRemove < 0)
				return false;

			if (!targetEntries.Contains(newEntry))
			{
				bool moveDownInSameList = sameSystem && sourceIndex < targetIndexBeforeRemove;
				int insertIndex = moveDownInSameList
					? targetIndexAfterRemove + 1
					: targetIndexAfterRemove;

				targetEntries.Insert(insertIndex, newEntry);
			}
		}
		else if (!targetEntries.Contains(newEntry))
		{
			int insertIndex = GetAppendIndexForScriptDrop(targetEntries, targetFolderPath);
			targetEntries.Insert(insertIndex, newEntry);
		}

		if (string.IsNullOrWhiteSpace(targetFolderPath))
			ForceExpandSystem(targetSystemName);
		else
			ForceExpandFolderPath(targetSystemName, targetFolderPath);

		DebugLogOperation(
			"Move Script Mutated",
			$"{sourceSystemName}:{draggedEntry} -> {targetSystemName}:{newEntry}"
		);

		return true;
	}

	private bool MoveSystemEntry(string draggedMetadata, string targetMetadata)
	{
		string draggedSystemName = GetSystemNameFromEntryMetadata(draggedMetadata);
		string targetSystemName = GetSystemNameFromEntryMetadata(targetMetadata);

		if (
			string.IsNullOrWhiteSpace(draggedSystemName)
			|| string.IsNullOrWhiteSpace(targetSystemName)
		)
		{
			TryRecoverSystemsFromDisk("Move Entry Resolve System");

			draggedSystemName = GetSystemNameFromEntryMetadata(draggedMetadata);
			targetSystemName = GetSystemNameFromEntryMetadata(targetMetadata);
		}

		if (string.IsNullOrWhiteSpace(draggedSystemName) || draggedSystemName != targetSystemName)
			return false;

		if (!EnsureSystemAvailable(draggedSystemName, "Move Entry"))
			return false;

		string draggedEntry = GetEntryFromMetadata(draggedMetadata);
		string targetEntry = GetEntryFromMetadata(targetMetadata);

		if (string.IsNullOrWhiteSpace(draggedEntry) || string.IsNullOrWhiteSpace(targetEntry))
			return false;

		List<string> entries = _systems[draggedSystemName];

		int draggedIndex = entries.IndexOf(draggedEntry);
		int targetIndex = entries.IndexOf(targetEntry);

		if (draggedIndex < 0 || targetIndex < 0 || draggedIndex == targetIndex)
			return false;

		string draggedParentPath = GetParentFolderPathFromEntryMetadata(draggedMetadata);
		string targetParentPath = GetParentFolderPathFromEntryMetadata(targetMetadata);

		if (draggedParentPath != targetParentPath)
			return false;

		bool moveDown = draggedIndex < targetIndex;

		entries.RemoveAt(draggedIndex);

		targetIndex = entries.IndexOf(targetEntry);

		if (targetIndex < 0)
			return false;

		int insertIndex = moveDown ? targetIndex + 1 : targetIndex;

		entries.Insert(insertIndex, draggedEntry);

		DebugLogOperation("Move Entry Mutated", $"{draggedEntry} -> {targetEntry}");

		return true;
	}

	private static bool IsSystemEntryMetadata(string metadata)
	{
		return metadata.StartsWith("folder::") || metadata.StartsWith("script::");
	}

	private static bool IsValidScriptDropTargetMetadata(string metadata)
	{
		return metadata.StartsWith("system::")
			|| metadata.StartsWith("folder::")
			|| metadata.StartsWith("script::");
	}

	private static int GetAppendIndexForScriptDrop(List<string> entries, string targetFolderPath)
	{
		for (int i = entries.Count - 1; i >= 0; i--)
		{
			string entry = entries[i];

			if (entry.StartsWith("folder::"))
				continue;

			if (GetFolderPathFromEntry(entry) == targetFolderPath)
				return i + 1;
		}

		if (!string.IsNullOrWhiteSpace(targetFolderPath))
		{
			int folderIndex = entries.IndexOf($"folder::{targetFolderPath}");

			if (folderIndex >= 0)
				return folderIndex + 1;
		}

		return entries.Count;
	}

	private string GetSystemNameFromEntryMetadata(string metadata)
	{
		if (metadata.StartsWith("folder::"))
			return GetSystemNameFromMetadata(metadata);

		if (metadata.StartsWith("script::"))
		{
			string entry = metadata.Replace("script::", "");
			return FindSystemNameForEntry(entry);
		}

		return "";
	}

	private string FindSystemNameForEntry(string entry)
	{
		foreach (KeyValuePair<string, List<string>> system in _systems)
		{
			if (system.Value.Contains(entry))
				return system.Key;
		}

		return "";
	}

	private static string GetEntryFromMetadata(string metadata)
	{
		if (metadata.StartsWith("script::"))
			return metadata.Replace("script::", "");

		if (metadata.StartsWith("folder::"))
		{
			string[] parts = metadata.Split("::");
			return parts.Length >= 3 ? $"folder::{parts[2]}" : "";
		}

		return "";
	}

	private static string GetParentFolderPathFromEntryMetadata(string metadata)
	{
		if (metadata.StartsWith("script::"))
		{
			string entry = metadata.Replace("script::", "");
			return GetFolderPathFromEntry(entry);
		}

		if (metadata.StartsWith("folder::"))
		{
			string folderPath = GetFolderPathFromMetadata(metadata);

			if (!folderPath.Contains("/"))
				return "";

			return folderPath.Substring(0, folderPath.LastIndexOf('/'));
		}

		return "";
	}

	private static string GetSystemNameFromTreeItem(TreeItem item)
	{
		TreeItem current = item;

		while (current != null)
		{
			string metadata = current.GetMetadata(0).AsString();

			if (metadata.StartsWith("system::"))
				return metadata.Replace("system::", "");

			if (metadata.StartsWith("folder::"))
				return GetSystemNameFromMetadata(metadata);

			current = current.GetParent();
		}

		return "";
	}

	private static string GetFolderPathFromTreeItem(TreeItem item)
	{
		TreeItem current = item;

		while (current != null)
		{
			string metadata = current.GetMetadata(0).AsString();

			if (metadata.StartsWith("folder::"))
				return GetFolderPathFromMetadata(metadata);

			current = current.GetParent();
		}

		return "";
	}

	private static string GetDropFolderPathFromTargetItem(TreeItem targetItem)
	{
		if (targetItem == null)
			return "";

		string targetMetadata = targetItem.GetMetadata(0).AsString();

		if (targetMetadata.StartsWith("folder::"))
			return GetFolderPathFromMetadata(targetMetadata);

		if (targetMetadata.StartsWith("script::"))
			return GetFolderPathFromEntry(GetEntryFromMetadata(targetMetadata));

		return "";
	}

	private void ClearDragState()
	{
		_draggedMetadata = "";
		_draggedSourceSystemName = "";
		_draggedSourceFolderPath = "";
		_leftMousePressedOnSelectedScript = false;
		_leftMousePressPosition = Vector2.Zero;
		_leftMousePressedMetadata = "";
	}

	#endregion

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
			string entry = _pendingRenameMetadata.Replace("script::", "");
			string scriptPath = GetScriptPathFromEntry(entry);

			_renameInput.Text = scriptPath.GetFile().GetBaseName();
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
		List<string> scriptPathsToDelete = removeFromFilesystem
			? GetScriptPathsForRemoveMetadata(_pendingRemoveMetadata)
			: new List<string>();

		if (!RemoveMetadata(_pendingRemoveMetadata))
		{
			DebugLogOperation("Remove cancelled: mutation failed", _pendingRemoveMetadata);
			return;
		}

		if (removeFromFilesystem)
			DeleteScriptFiles(scriptPathsToDelete);

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
			string entry = metadata.Replace("script::", "");
			bool removed = RemoveEntry(entry);
			DebugLogOperation(removed ? "Remove Script Mutated" : "Remove Script failed", entry);
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

	private List<string> GetScriptPathsForRemoveMetadata(string metadata)
	{
		if (metadata.StartsWith("script::"))
		{
			string entry = metadata.Replace("script::", "");
			return new List<string> { GetScriptPathFromEntry(entry) };
		}

		if (metadata.StartsWith("system::"))
		{
			string systemName = metadata.Replace("system::", "");

			if (!EnsureSystemAvailable(systemName, "Collect Remove Scripts"))
				return new List<string>();

			return _systems[systemName]
				.Where(entry => !entry.StartsWith("folder::"))
				.Select(GetScriptPathFromEntry)
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
				.Where(
					entry =>
						!entry.StartsWith("folder::")
						&& (
							entry.StartsWith($"{folderPath}|") || entry.StartsWith($"{folderPath}/")
						)
				)
				.Select(GetScriptPathFromEntry)
				.Distinct()
				.ToList();
		}

		return new List<string>();
	}

	private void DeleteScriptFiles(List<string> scriptPaths)
	{
		foreach (string scriptPath in scriptPaths.Distinct())
			DeleteScriptFile(scriptPath);
	}

	private void DeleteScriptFile(string scriptPath)
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
			GD.PushWarning($"Could not delete script file: {scriptPath}");
			return;
		}

		string uidPath = $"{scriptPath}.uid";

		if (FileAccess.FileExists(uidPath))
			DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(uidPath));
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
			if (entry == $"folder::{oldFolderPath}")
			{
				updatedEntries.Add($"folder::{newFolderPath}");
				continue;
			}

			if (entry.StartsWith($"folder::{oldFolderPath}/"))
			{
				updatedEntries.Add(
					entry.Replace($"folder::{oldFolderPath}/", $"folder::{newFolderPath}/")
				);
				continue;
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
		string entry = metadata.Replace("script::", "");
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

		if (!DoesAnySystemContainEntry(entry))
			TryRecoverSystemsFromDisk("Rename Script");

		UpdateScriptEntries(oldScriptPath, newScriptPath);

		EditorInterface.Singleton.GetResourceFilesystem().Scan();

		DebugLogOperation("Rename Script Mutated", $"{oldScriptPath} -> {newScriptPath}");

		return true;
	}

	private void UpdateScriptEntries(string oldScriptPath, string newScriptPath)
	{
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

				string updatedEntry = string.IsNullOrWhiteSpace(folderPath)
					? newScriptPath
					: $"{folderPath}|{newScriptPath}";

				updatedEntries.Add(updatedEntry);
			}

			_systems[systemName] = updatedEntries.Distinct().ToList();
		}
	}

	#endregion

	#region Selection, Metadata and Entry Helpers
	private bool DoesAnySystemContainEntry(string entry)
	{
		return _systems.Values.Any(entries => entries.Contains(entry));
	}

	private bool RemoveEntry(string entry)
	{
		foreach (string systemName in _systems.Keys.ToList())
		{
			if (_systems[systemName].Remove(entry))
				return true;
		}

		if (TryRecoverSystemsFromDisk("Remove Entry"))
		{
			foreach (string systemName in _systems.Keys.ToList())
			{
				if (_systems[systemName].Remove(entry))
					return true;
			}
		}

		return false;
	}

	private bool RemoveFolder(string metadata)
	{
		string[] parts = metadata.Split("::");

		if (parts.Length < 3)
			return false;

		string systemName = parts[1];
		string folderPath = parts[2];

		if (!EnsureSystemAvailable(systemName, "Remove Folder"))
			return false;

		bool removedFolderEntry = _systems[systemName].Remove($"folder::{folderPath}");

		int removedChildEntries = _systems[systemName].RemoveAll(
			entry =>
				entry.StartsWith($"{folderPath}|")
				|| entry.StartsWith($"{folderPath}/")
				|| entry.StartsWith($"folder::{folderPath}/")
		);

		return removedFolderEntry || removedChildEntries > 0;
	}

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

	private static string GetFolderPathFromEntry(string entry)
	{
		return entry.Contains("|") ? entry.Split("|")[0] : "";
	}

	private static string GetScriptPathFromEntry(string entry)
	{
		return entry.Contains("|") ? entry.Split("|")[1] : entry;
	}

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
			.Distinct()
			.ToList();

		List<string> scripts = entries
			.Where(entry => !entry.StartsWith("folder::"))
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

		foreach (string folderEntry in explicitFolders)
		{
			if (!normalized.Contains(folderEntry))
				normalized.Add(folderEntry);
		}

		foreach (string folderEntry in requiredFolders)
		{
			if (!normalized.Contains(folderEntry))
				normalized.Add(folderEntry);
		}

		normalized.AddRange(scripts);

		return normalized;
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

	#region Persistence and Save Guards
	private bool SaveSystems()
	{
		DebugLogOperation("Save Systems Requested", $"{_systems.Count} systems");

		if (WouldOverwriteExistingDataWithEmptySystems())
		{
			GD.PushWarning(
                "System Explorer blocked saving an empty systems file because existing data was found on disk."
			);
			DebugLog(
                "Save Systems blocked: empty in-memory systems would overwrite existing data."
			);
			DebugLogStateSnapshot("Blocked Save");
			return false;
		}

		if (WouldOverwriteExistingDataWithUnrelatedSystems())
		{
			GD.PushWarning(
                "System Explorer blocked saving because the in-memory systems do not match the existing systems file. Restart Godot to avoid data loss."
			);
			DebugLog(
                "Save Systems blocked: in-memory systems appear unrelated to existing disk data."
			);
			DebugLogStateSnapshot("Blocked Save");
			return false;
		}

		NormalizeAllSystemEntries();

		string json = JsonSerializer.Serialize(
			_systems,
			new JsonSerializerOptions { WriteIndented = true }
		);

		using FileAccess file = FileAccess.Open(SavePath, FileAccess.ModeFlags.Write);
		file.StoreString(json);

		DebugLogOperation(
			"Save Systems Completed",
			$"{_systems.Count} systems, {json.Length} chars"
		);

		return true;
	}

	private bool WouldOverwriteExistingDataWithEmptySystems()
	{
		if (_systems.Count > 0)
			return false;

		if (!FileAccess.FileExists(SavePath))
			return false;

		using FileAccess file = FileAccess.Open(SavePath, FileAccess.ModeFlags.Read);
		string existingJson = file.GetAsText();

		if (string.IsNullOrWhiteSpace(existingJson))
			return false;

		return existingJson.Trim() != "{}";
	}

	private bool WouldOverwriteExistingDataWithUnrelatedSystems()
	{
		Dictionary<string, List<string>> existingSystems = LoadSystemsFromDiskForSaveGuard();

		if (existingSystems == null || existingSystems.Count <= 1 || _systems.Count == 0)
			return false;

		return !_systems.Keys.Any(existingSystems.ContainsKey);
	}

	private Dictionary<string, List<string>> LoadSystemsFromDiskForSaveGuard()
	{
		if (!FileAccess.FileExists(SavePath))
			return null;

		using FileAccess file = FileAccess.Open(SavePath, FileAccess.ModeFlags.Read);
		string existingJson = file.GetAsText();

		if (string.IsNullOrWhiteSpace(existingJson))
			return null;

		try
		{
			return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(existingJson);
		}
		catch
		{
			return null;
		}
	}

	private bool EnsureSystemAvailable(string systemName, string reason)
	{
		if (string.IsNullOrWhiteSpace(systemName))
			return false;

		if (_systems.ContainsKey(systemName))
			return true;

		DebugLogOperation(
			"Recovery Guard: missing system",
			$"Reason='{reason}', System='{systemName}'"
		);

		if (TryRecoverSystemsFromDisk(reason, systemName) && _systems.ContainsKey(systemName))
		{
			DebugLogOperation("Recovery Guard: recovered system", systemName);
			return true;
		}

		GD.PushWarning(
			$"System Explorer could not find system '{systemName}' for '{reason}'. The tree will be rebuilt from the current in-memory state."
		);
		DebugLogOperation(
			"Recovery Guard failed: system still missing",
			$"Reason='{reason}', System='{systemName}'"
		);
		DebugLogStateSnapshot("Recovery Failed");
		BuildTree();
		return false;
	}

	private bool EnsureSystemsAvailable(IEnumerable<string> systemNames, string reason)
	{
		List<string> names = systemNames
			.Where(name => !string.IsNullOrWhiteSpace(name))
			.Distinct()
			.ToList();

		if (names.Count == 0)
			return false;

		List<string> missingNames = names.Where(name => !_systems.ContainsKey(name)).ToList();

		if (missingNames.Count == 0)
			return true;

		DebugLogOperation(
			"Recovery Guard: missing systems",
			$"Reason='{reason}', Systems='{string.Join(", ", missingNames)}'"
		);

		if (TryRecoverSystemsFromDisk(reason))
		{
			missingNames = names.Where(name => !_systems.ContainsKey(name)).ToList();

			if (missingNames.Count == 0)
			{
				DebugLogOperation("Recovery Guard: recovered systems", string.Join(", ", names));
				return true;
			}
		}

		GD.PushWarning(
			$"System Explorer could not find required system(s) for '{reason}': {string.Join(", ", missingNames)}. The tree will be rebuilt from the current in-memory state."
		);
		DebugLogOperation(
			"Recovery Guard failed: systems still missing",
			$"Reason='{reason}', Systems='{string.Join(", ", missingNames)}'"
		);
		DebugLogStateSnapshot("Recovery Failed");
		BuildTree();
		return false;
	}

	private bool TryRecoverSystemsFromDisk(string reason, string requiredSystemName = "")
	{
		DebugLogOperation(
			"Recovery From Disk Requested",
			$"Reason='{reason}', Required='{requiredSystemName}'"
		);

		Dictionary<string, List<string>> recoveredSystems = LoadSystemsFromDiskForRecovery();

		if (recoveredSystems == null || recoveredSystems.Count == 0)
		{
			DebugLog("Recovery From Disk failed: no usable systems found on disk.");

			if (DebugState)
				GD.PushError(
					$"[SystemExplorer] Recovery failed. Reason='{reason}', Required='{requiredSystemName}'."
				);

			return false;
		}

		if (
			!string.IsNullOrWhiteSpace(requiredSystemName)
			&& !recoveredSystems.ContainsKey(requiredSystemName)
		)
		{
			DebugLogOperation(
				"Recovery From Disk failed: required system missing on disk",
				requiredSystemName
			);

			if (DebugState)
				GD.PushError(
					$"[SystemExplorer] Recovery failed. Required system '{requiredSystemName}' was not found on disk. Reason='{reason}'."
				);

			return false;
		}

		_systems.Clear();

		foreach (KeyValuePair<string, List<string>> system in recoveredSystems)
			_systems[system.Key] = system.Value ?? new List<string>();

		NormalizeAllSystemEntries();

		DebugLogOperation("Recovery From Disk Completed", $"{_systems.Count} systems");

		if (DebugState)
		{
			GD.PushWarning(
				$"[SystemExplorer] Recovery successful. Reason='{reason}', Required='{requiredSystemName}', Recovered Systems={_systems.Count}"
			);
		}

		DebugLogStateSnapshot("Recovered From Disk");

		return true;
	}

	private Dictionary<string, List<string>> LoadSystemsFromDiskForRecovery()
	{
		if (!FileAccess.FileExists(SavePath))
			return null;

		using FileAccess file = FileAccess.Open(SavePath, FileAccess.ModeFlags.Read);
		string existingJson = file.GetAsText();

		if (string.IsNullOrWhiteSpace(existingJson))
			return null;

		try
		{
			return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(existingJson);
		}
		catch (System.Exception exception)
		{
			DebugLogOperation(
				"Recovery From Disk failed: JSON deserialize error",
				exception.Message
			);
			return null;
		}
	}

	private void LoadSystems()
	{
		DebugLogOperation("Load Systems Requested", SavePath);

		if (!FileAccess.FileExists(SavePath))
		{
			DebugLog("Load Systems skipped: save file does not exist.");
			return;
		}

		using FileAccess file = FileAccess.Open(SavePath, FileAccess.ModeFlags.Read);
		string json = file.GetAsText();

		if (string.IsNullOrWhiteSpace(json))
		{
			DebugLog("Load Systems skipped: save file is empty.");
			return;
		}

		Dictionary<string, List<string>> loaded = JsonSerializer.Deserialize<
			Dictionary<string, List<string>>
		>(json);

		if (loaded == null)
		{
			DebugLog("Load Systems skipped: deserialized data was null.");
			return;
		}

		_systems.Clear();

		foreach (KeyValuePair<string, List<string>> system in loaded)
			_systems[system.Key] = system.Value ?? new List<string>();

		NormalizeAllSystemEntries();

		DebugLogOperation("Load Systems Completed", $"{_systems.Count} systems");
		DebugLogStateSnapshot("Loaded Systems");
	}

	public override void _Input(InputEvent inputEvent)
	{
		if (inputEvent is not InputEventKey keyEvent)
			return;

		if (!keyEvent.Pressed || keyEvent.Echo)
			return;

		if (keyEvent.Keycode != Key.Delete)
			return;

		if (!keyEvent.CtrlPressed)
			return;

		OpenRemoveDialogForSelectedItem();

		GetViewport().SetInputAsHandled();
	}

	#endregion

	#region Keyboard Shortcuts
	private void OpenRemoveDialogForSelectedItem()
	{
		TreeItem selectedItem = _tree.GetSelected();

		if (selectedItem == null)
			return;

		string metadata = selectedItem.GetMetadata(0).AsString();

		if (string.IsNullOrWhiteSpace(metadata))
			return;

		_pendingRemoveMetadata = metadata;

		OpenRemoveDialog();
	}

	#endregion

	#region Debug Logging
	private void DebugLog(string message)
	{
		if (!DebugState)
			return;

		GD.Print($"[SystemExplorer] {message}");
	}

	private void DebugLogOperation(string operation, string details = "")
	{
		if (!DebugState)
			return;

		if (string.IsNullOrWhiteSpace(details))
			GD.Print($"[SystemExplorer] {operation}");
		else
			GD.Print($"[SystemExplorer] {operation} -> {details}");
	}

	private void DebugLogStateSnapshot(string label)
	{
		if (!DebugState)
			return;

		DebugLog($"--- {label} ---");
		DebugLog($"Systems Count: {_systems.Count}");
		DebugLog($"Pending Add Folder Metadata: '{_pendingAddFolderMetadata}'");
		DebugLog($"Pending Remove Metadata: '{_pendingRemoveMetadata}'");
		DebugLog($"Pending Rename Metadata: '{_pendingRenameMetadata}'");

		TreeItem selectedItem = _tree?.GetSelected();

		if (selectedItem == null)
		{
			DebugLog("Selected Tree Item: <null>");
		}
		else
		{
			DebugLog($"Selected Tree Text: '{selectedItem.GetText(0)}'");
			DebugLog($"Selected Tree Metadata: '{selectedItem.GetMetadata(0).AsString()}'");
		}

		DebugLogSystems("Systems Snapshot");
		DebugLog($"--- End {label} ---");
	}

	private void DebugLogSystems(string label)
	{
		if (!DebugState)
			return;

		DebugLog(label);

		if (_systems.Count == 0)
		{
			DebugLog(" - <no systems>");
			return;
		}

		foreach (KeyValuePair<string, List<string>> system in _systems)
			DebugLog($" - {system.Key}: {system.Value.Count} entries");
	}
	#endregion
}
#endif
