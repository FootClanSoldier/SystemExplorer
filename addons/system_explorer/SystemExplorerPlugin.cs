#if TOOLS
using Godot;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

[Tool]
public partial class SystemExplorerPlugin : EditorPlugin
{
	#region Constants and Fields
	private const string SavePath = "res://addons/system_explorer/Resources/systems.json";
	private const string ScriptTemplatePath = "res://addons/system_explorer/Resources/script_template.txt";
	
	// Enable or disable icons in the context menu
	private const bool EnableContextMenuIcons = true;
	
	// Enable only when investigating editor state/save issues.
	private const bool DebugState = false;
	
	private const int ContextAddFolder = 0;
	private const int ContextAddScript = 1;
	private const int ContextNewScript = 2;
	private const int ContextRename = 3;
	private const int ContextRemove = 4;
	private const int ContextLinkScene = 5;
	private const int ContextUnlinkScene = 6;
	private const int ContextShowInFileManager = 7;
	private const int ContextAddScene = 8;
	private const string LinkedSceneMarker = "||linkedScene::";
	private const string SceneEntryMarker = "scene::";
	private const string LockedEntryMarker = "||locked";
	private const string SystemLockEntry = "systemLock::locked";
	private const float ClickOpenDragThreshold = 6.0f;
	private const float RightIconClickablePadding = 12.0f;

	private EditorDock _editorDock;
	private VBoxContainer _dock;
	private LineEdit _systemNameInput;
	private LineEdit _scriptFilterInput;
	private Tree _tree;
	private Control _focusReleaseTarget;
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
	private EditorFileDialog _linkSceneDialog;
	private EditorFileDialog _addSceneDialog;
	private EditorFileDialog _relinkSceneDialog;
	private ConfirmationDialog _missingScriptDialog;
	private ConfirmationDialog _missingSceneDialog;

	private string _pendingRemoveMetadata = "";
	private string _pendingRenameMetadata = "";
	private string _pendingAddFolderMetadata = "";
	private string _pendingShowInFileManagerMetadata = "";
	private string _draggedMetadata = "";
	private string _draggedSourceSystemName = "";
	private string _draggedSourceFolderPath = "";
	private bool _leftMousePressedOnSelectedScript;
	private Vector2 _leftMousePressPosition;
	private string _leftMousePressedMetadata = "";
	private string _pendingMissingScriptEntry = "";
	private string _pendingMissingScriptPath = "";
	private string _pendingSceneLinkEntry = "";
	private string _pendingMissingSceneEntry = "";
	private string _pendingMissingScenePath = "";
	private string _selectedScriptEntryFromFilter = "";
	private string _hoveredTreeItemMetadata = "";
	private bool _isFilteringScripts;
	private bool _ignoreNextScriptFilterReleaseOpen;

	private Texture2D _scriptIcon;
	private Texture2D _sceneIcon;
	private Texture2D _systemIcon;
	private Texture2D _folderIcon;
	private Texture2D _contextFolderIcon;
	private Texture2D _contextNewScriptIcon;
	private Texture2D _contextAddScriptIcon;
	private Texture2D _contextLinkSceneIcon;
	private Texture2D _contextUnlinkSceneIcon;
	private Texture2D _contextRenameIcon;
	private Texture2D _contextRemoveIcon;
	private Texture2D _contextShowInFileSystemIcon;
	private Texture2D _scriptFilterSearchIcon;
	private Texture2D _systemNameEnterIcon;
	private Texture2D _scriptFilterCloseIcon;
	private Color _systemColor = Color.FromHtml("#6495ED");
	private Color _folderColor = Color.FromHtml("#F2C252");

	private readonly Dictionary<string, List<string>> _systems = new();
	private readonly HashSet<string> _expandedItems = new();
	private readonly HashSet<string> _forcedExpandedItems = new();
	private readonly HashSet<string> _expandedItemsBeforeScriptFilter = new();

	#endregion

	#region Lifecycle and Dock Setup
	public override void _EnterTree()
	{
		
		DebugLogOperation("Enter Tree");

		LoadEditorIcons();
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
private void LoadEditorIcons()
{
	var editorTheme = EditorInterface.Singleton.GetEditorTheme();

	_scriptIcon = GetEditorIcon(editorTheme, "CSharpScript");
	_sceneIcon = GetEditorIcon(editorTheme, "PackedScene");
	_systemIcon = GetEditorIcon(editorTheme, "Environment");
	_folderIcon = GetEditorIcon(editorTheme, "Folder");
	_scriptFilterSearchIcon = GetEditorIcon(editorTheme, "Search");
	_scriptFilterCloseIcon = GetEditorIcon(editorTheme, "GuiClose");
	_systemNameEnterIcon = GetEditorIcon(editorTheme, "Add");

	_contextFolderIcon = GetEditorIcon(editorTheme, "Folder");
	_contextNewScriptIcon = GetEditorIcon(editorTheme, "Script");
	_contextAddScriptIcon = GetEditorIcon(editorTheme, "ScriptCreate");
	_contextLinkSceneIcon = GetEditorIcon(editorTheme, "PackedScene");
	_contextUnlinkSceneIcon = GetEditorIcon(editorTheme, "Unlinked");
	_contextRenameIcon = GetEditorIcon(editorTheme, "Rename");
	_contextRemoveIcon = GetEditorIcon(editorTheme, "Remove");
	_contextShowInFileSystemIcon = GetEditorIcon(editorTheme, "Filesystem");
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
private static Texture2D GetEditorIcon(Theme theme, string iconName)
{
	if (!theme.HasIcon(iconName, "EditorIcons"))
		return null;

	return theme.GetIcon(iconName, "EditorIcons");
}

	private void AddContextMenuIconItem(string label, int id, Texture2D icon)
	{
		if (!EnableContextMenuIcons || icon == null)
		{
			_contextMenu.AddItem(label, id);
			return;
		}

		_contextMenu.AddIconItem(icon, label, id);
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
