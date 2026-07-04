#if TOOLS
using Godot;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

[Tool]
public partial class SystemExplorerPlugin : EditorPlugin
{
	#region Constants and Fields
	private const string PluginFolderPath = "res://addons/system_explorer";
	private const string ResourcesFolderPath = PluginFolderPath + "/Resources";
	private const string SavePath = ResourcesFolderPath + "/systems.json";
	private const string ScriptTemplatePath = ResourcesFolderPath + "/script_template.txt";
	
	
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
	private const int ContextRefactorNamespace = 9;
	private const string LinkedSceneMarker = "||linkedScene::";
	private const string SceneEntryMarker = "scene::";
	private const string LockedEntryMarker = "||locked";
	private const string SystemLockEntry = "systemLock::locked";
	private const float ClickOpenDragThreshold = 6.0f;
	private const float RightIconClickablePadding = 12.0f;
	private static readonly Color DragDropTargetHighlightColor = new(1.0f, 1.0f, 1.0f, 0.16f);

	private EditorDock _editorDock;
	private VBoxContainer _dock;
	private LineEdit _systemNameInput;
	private LineEdit _scriptFilterInput;
	private Tree _tree;
	private Control _focusReleaseTarget;
	private EditorFileDialog _fileDialog;
	private PopupMenu _contextMenu;
	private PopupMenu _contextNewSubmenu;
	private PopupMenu _contextAddSubmenu;
	private PopupMenu _contextQuickActionsSubmenu;
	private ConfirmationDialog _removeDialog;
	private CheckBox _removeFromFilesystemCheckBox;
	private AcceptDialog _renameDialog;
	private LineEdit _renameInput;
	private AcceptDialog _addFolderDialog;
	private LineEdit _addFolderInput;
	private AcceptDialog _refactorNamespaceDialog;
	private LineEdit _oldNamespaceInput;
	private LineEdit _newNamespaceInput;
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
	private string _pendingRefactorNamespaceMetadata = "";
	private string _draggedMetadata = "";
	private string _draggedSourceSystemName = "";
	private string _draggedSourceFolderPath = "";
	private TreeItem _dragDropHighlightedItem;
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
	private Texture2D _contextCategoryAddIcon;
	private Texture2D _contextCategoryArrowLeftIcon;
	private Texture2D _contextQuickActionsIcon;
	private Texture2D _contextRefactorNamespaceIcon;
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

		EnsureProjectSettings();
		LoadEditorIcons();
		EnsureResourcesFolderExists();
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
	_contextCategoryAddIcon = GetEditorIcon(editorTheme, "Add");
	_contextCategoryArrowLeftIcon = GetEditorIcon(editorTheme, "ArrowLeft");
	_contextQuickActionsIcon = GetEditorIcon(editorTheme, "Tools");
	_contextRefactorNamespaceIcon = GetEditorIcon(editorTheme, "Rename");
}

	private bool EnsureResourcesFolderExists()
	{
		if (DirAccess.DirExistsAbsolute(ResourcesFolderPath))
			return true;

		using DirAccess pluginDirectory = DirAccess.Open(PluginFolderPath);

		if (pluginDirectory == null)
		{
			GD.PushWarning($"System Explorer could not open plugin folder '{PluginFolderPath}' to create the Resources folder.");
			return false;
		}

		Error error = pluginDirectory.MakeDir("Resources");

		if (error != Error.Ok && !DirAccess.DirExistsAbsolute(ResourcesFolderPath))
		{
			GD.PushWarning($"System Explorer could not create Resources folder at '{ResourcesFolderPath}'. Error: {error}.");
			return false;
		}

		EditorInterface.Singleton.GetResourceFilesystem().Scan();
		DebugLogOperation("Resources Folder Created", ResourcesFolderPath);
		return true;
	}

	private void EnsureScriptTemplateExists()
	{
		if (!EnsureResourcesFolderExists())
			return;

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
