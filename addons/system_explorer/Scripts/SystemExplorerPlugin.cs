#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using SystemExplorer.Diagnostics;
using SystemExplorer.EditorIntegration.ScriptEditing;
using SystemExplorer.QuickActions.RefactorNamespace;

[Tool]
public partial class SystemExplorerPlugin : EditorPlugin
{
	#region Constants and Fields
	private const string PluginFolderPath = "res://addons/system_explorer";
	private const string ResourcesFolderPath = PluginFolderPath + "/Resources";
	private const string SavePath = ResourcesFolderPath + "/systems.json";
	private const string FolderBindingsPath = ResourcesFolderPath + "/folder_bindings.json";
	private const string ScriptTemplatePath = ResourcesFolderPath + "/script_template.txt";

	private const string LinkedSceneMarker = "||linkedScene::";
	private const string SceneEntryMarker = "scene::";
	private const string LockedEntryMarker = "||locked";
	private const string SystemLockEntry = "systemLock::locked";
	private const float ClickOpenDragThreshold = 6.0f;
	private const float RightIconClickablePadding = 12.0f;
	private static readonly Color DragDropTargetHighlightColor = new(1.0f, 1.0f, 1.0f, 0.16f);

	private SystemExplorerDebugLogger _debugLogger;

	private SystemExplorerDebugLogger DebugLogger =>
		_debugLogger ??= new SystemExplorerDebugLogger(() => DebugState);

	#region Shared Script Editing Services

	private static ScriptEditorBufferLocator OpenScriptEditorBufferLocator =>
		OpenScriptEditorBufferLocatorHolder.Instance;

	private static class OpenScriptEditorBufferLocatorHolder
	{
		internal static readonly ScriptEditorBufferLocator Instance = new(
			ScriptPathUtility.Normalize,
			ScriptTextFileService.ReadText,
			ScriptTextFileService.TextsMatchForDiskVerification,
			FileAccess.FileExists
		);
	}

	private static ScriptEditorBufferAutosaveService OpenScriptEditorBufferAutosaveService =>
		OpenScriptEditorBufferAutosaveCompositionHolder.AutosaveService;

	private static ScriptEditorBufferBatchService OpenScriptEditorBufferBatchService =>
		OpenScriptEditorBufferAutosaveCompositionHolder.BatchService;

	private static ScriptEditorBufferAutosaveCoordinator
		OpenScriptEditorBufferAutosaveCoordinator =>
			OpenScriptEditorBufferAutosaveCompositionHolder.Coordinator;

	private static class OpenScriptEditorBufferAutosaveCompositionHolder
	{
		internal static readonly ScriptEditorBufferAutosaveService AutosaveService =
			new(
				ScriptTextFileService.ReadText,
				ScriptTextFileService.WriteText,
				ScriptTextFileService.TextsMatchForDiskVerification
			);

		internal static readonly ScriptEditorBufferBatchService BatchService =
			new(AutosaveService);

		internal static readonly ScriptEditorBufferAutosaveCoordinator Coordinator =
			new(AutosaveService, BatchService);
	}

	#endregion

	private EditorDock _editorDock;
	private VBoxContainer _dock;
	private LineEdit _systemNameInput;
	private LineEdit _scriptFilterInput;
	private MarginContainer _firstRunWelcomeNote;
	private Tree _tree;
	private Control _focusReleaseTarget;
	private EditorFileDialog _fileDialog;
	private EditorFileDialog _folderBindingDialog;
	private PopupMenu _contextMenu;
	private PopupMenu _contextNewSubmenu;
	private PopupMenu _contextAddSubmenu;
	private PopupMenu _contextQuickActionsSubmenu;
	private ConfirmationDialog _removeDialog;
	private AcceptDialog _physicalRemoveIncompleteDialog;
	private CheckBox _removeFromFilesystemCheckBox;
	private AcceptDialog _renameDialog;
	private LineEdit _renameInput;
	private AcceptDialog _renameNameConflictDialog;
	private AcceptDialog _addFolderDialog;
	private LineEdit _addFolderInput;
	private AcceptDialog _addFolderConflictDialog;
	private AcceptDialog _addSystemConflictDialog;
	private AcceptDialog _namespaceRefactorDialog;
	private AcceptDialog _namespaceRefactorIncompleteWriteReportDialog;
	private Label _namespaceRefactorDescriptionLabel;
	private Label _namespaceRefactorNewNamespaceLabel;
	private LineEdit _namespaceRefactorNewNamespaceInput;
	private Label _namespaceRefactorOldNamespaceLabel;
	private LineEdit _namespaceRefactorOldNamespaceInput;
	private Label _namespaceRefactorApplyToLabel;
	private CheckBox _namespaceRefactorExistingNamespaceOption;
	private OptionButton _namespaceRefactorExistingNamespaceDropdown;
	private CheckBox _namespaceRefactorWithoutNamespaceOption;
	private NamespaceRefactorPluginHost _namespaceRefactorHost;
	private AcceptDialog _csharpierInstallResultDialog;
	private ConfirmationDialog _csharpierNotInstalledDialog;
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
	private string _pendingBeautifyScriptMetadata = "";
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
	private bool _isRenameNameConflictPopupPending;
	private bool _isAddFolderConflictPopupPending;
	private bool _isAddSystemConflictPopupPending;

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
	private Texture2D _contextBeautifyScriptIcon;
	private Texture2D _scriptFilterSearchIcon;
	private Texture2D _systemNameEnterIcon;
	private Texture2D _scriptFilterCloseIcon;
	private Color _systemColor = Color.FromHtml("#6495ED");
	private Color _folderColor = Color.FromHtml("#F2C252");
	private Color _boundFolderColor = Color.FromHtml("#D8C86A");

	private readonly Dictionary<string, List<string>> _systems = new();
	private readonly HashSet<string> _expandedItems = new();
	private readonly HashSet<string> _forcedExpandedItems = new();
	private readonly HashSet<string> _expandedItemsBeforeScriptFilter = new();

	#endregion

	#region Lifecycle and Dock Setup
	public override void _EnterTree()
	{
		DebugLogger.LogOperation("Enter Tree");

		EnsureProjectSettings();
		LoadEditorIcons();
		EnsureScriptTemplateExists();
		BuildDock();
		LoadSystems();
		LoadFolderBindings();
		SynchronizeBoundFoldersAtStartup();
		InitializeFolderBindingFilesystemLifecycle();

		_editorDock = new EditorDock
		{
			Title = "System Explorer",
			DefaultSlot = EditorDock.DockSlot.RightUl,
		};

		_editorDock.AddChild(_dock);
		AddDock(_editorDock);

		BuildTree();
		SchedulePendingTreeOperationDialogPresentation();
		InitializeScriptEditorSync();

		CallDeferred(nameof(MakeSystemExplorerDockVisible));

		DebugLogStateSnapshot("Enter Tree Complete");

		StartCSharpierStartupWarmUp();
	}

	private void MakeSystemExplorerDockVisible()
	{
		if (!GodotObject.IsInstanceValid(_editorDock))
		{
			return;
		}

		_editorDock.MakeVisible();
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
		_contextBeautifyScriptIcon = GetEditorIcon(editorTheme, "CodeHighlighter");
		_contextBeautifyScriptIcon ??= GetEditorIcon(editorTheme, "CSharpScript");
	}

	private bool EnsureResourcesFolderExists()
	{
		if (DirAccess.DirExistsAbsolute(ResourcesFolderPath))
			return true;

		using DirAccess pluginDirectory = DirAccess.Open(PluginFolderPath);

		if (pluginDirectory == null)
		{
			ReportTreeOperationFailureOrWarning(
				$"System Explorer could not open the plugin folder needed to save its metadata.",
				$"PluginFolderPath='{PluginFolderPath}', ResourcesFolderPath='{ResourcesFolderPath}'"
			);
			return false;
		}

		Error error = pluginDirectory.MakeDir("Resources");

		if (error != Error.Ok && !DirAccess.DirExistsAbsolute(ResourcesFolderPath))
		{
			ReportTreeOperationFailureOrWarning(
				$"System Explorer could not create the Resources folder needed to save its metadata.",
				$"ResourcesFolderPath='{ResourcesFolderPath}', Error='{error}'"
			);
			return false;
		}

		EditorInterface.Singleton.GetResourceFilesystem().Scan();
		DebugLogger.LogOperation("Resources Folder Created", ResourcesFolderPath);
		return true;
	}

	private bool EnsureScriptTemplateExists()
	{
		if (!EnsureResourcesFolderExists())
			return false;

		if (FileAccess.FileExists(ScriptTemplatePath))
			return true;

		string defaultTemplate =
			@"using Godot;

public sealed class {{CLASS_NAME}}
{
}
";

		FileAccess file;

		try
		{
			file = FileAccess.Open(ScriptTemplatePath, FileAccess.ModeFlags.Write);
		}
		catch (Exception exception)
		{
			ReportTreeOperationFailureOrWarning(
				"System Explorer could not create the script template.",
				$"Path='{ScriptTemplatePath}', Exception='{exception}'"
			);
			DebugLogger.LogOperation(
				"Create Script Template failed: open threw",
				$"Path='{ScriptTemplatePath}', Exception='{exception}'"
			);
			return false;
		}

		if (file == null)
		{
			Error openError = FileAccess.GetOpenError();

			ReportTreeOperationFailureOrWarning(
				"System Explorer could not create the script template.",
				$"Path='{ScriptTemplatePath}', Error='{openError}'"
			);
			DebugLogger.LogOperation(
				"Create Script Template failed: open returned null",
				$"Path='{ScriptTemplatePath}', Error='{openError}'"
			);
			return false;
		}

		string writeFailureDetail = "";

		try
		{
			using (file)
			{
				bool stored = file.StoreString(defaultTemplate);
				file.Flush();
				Error writeError = file.GetError();

				if (!stored || writeError != Error.Ok)
				{
					writeFailureDetail =
						$"Path='{ScriptTemplatePath}', StoreSucceeded='{stored}', Error='{writeError}'";
				}
			}
		}
		catch (Exception exception)
		{
			writeFailureDetail =
				$"Path='{ScriptTemplatePath}', Exception='{exception}'";
		}

		if (!string.IsNullOrWhiteSpace(writeFailureDetail))
		{
			ReportTreeOperationFailureOrWarning(
				"System Explorer could not create the script template.",
				writeFailureDetail
			);
			DebugLogger.LogOperation(
				"Create Script Template failed: write",
				writeFailureDetail
			);
			return false;
		}

		if (!FileAccess.FileExists(ScriptTemplatePath))
		{
			ReportTreeOperationFailureOrWarning(
				"System Explorer could not create the script template.",
				$"Path='{ScriptTemplatePath}', TargetMissingAfterWrite=true"
			);
			DebugLogger.LogOperation(
				"Create Script Template failed: target missing after write",
				ScriptTemplatePath
			);
			return false;
		}

		EditorInterface.Singleton.GetResourceFilesystem().Scan();
		DebugLogger.LogOperation("Script Template Created", ScriptTemplatePath);
		return true;
	}

	private static Texture2D GetEditorIcon(Theme theme, string iconName)
	{
		if (!theme.HasIcon(iconName, "EditorIcons"))
			return null;

		return theme.GetIcon(iconName, "EditorIcons");
	}

	public override void _ExitTree()
	{
		DebugLogger.LogOperation("Exit Tree");

		CancelPendingScriptRenameEditorRestore();
		ShutdownTreeOperationDialogs();
		ShutdownScriptEditorSync();
		ShutdownFolderBindingFilesystemLifecycle();
		_namespaceRefactorHost = null;

		if (_editorDock == null)
			return;

		RemoveDock(_editorDock);
		_editorDock.QueueFree();

		_editorDock = null;
		_dock = null;
	}

	#endregion
}
#endif
