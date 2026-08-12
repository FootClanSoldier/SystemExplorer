#if TOOLS
using Godot;
using System.Collections.Generic;
using SystemExplorer.QuickActions.RefactorNamespace;

public partial class SystemExplorerPlugin
{
	private const float FirstRunWelcomeNoteMaximumWidth = 700.0f;
	private const int FirstRunWelcomeNoteMinimumHorizontalMargin = 16;

	#region Dock UI Setup
	private void BuildDock()
	{
		_dock = new VBoxContainer { Name = "System Explorer" };

		_systemNameInput = new LineEdit { PlaceholderText = "System Name" };
		UpdateSystemNameEnterIconVisibility(_systemNameInput.Text);
		_scriptFilterInput = new LineEdit { PlaceholderText = "Filter Items" };
		UpdateScriptFilterSearchIconVisibility(_scriptFilterInput.Text);
		_firstRunWelcomeNote = CreateFirstRunWelcomeNote();

		_tree = new Tree { HideRoot = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
		ConfigureTreeColumns();

		_focusReleaseTarget = new Control
		{
			Name = "Focus Release Target",
			FocusMode = Control.FocusModeEnum.All,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			CustomMinimumSize = Vector2.Zero,
			SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
			SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
		};

		ConfigureTreeDirectionalFocusFallback();

		_fileDialog = new EditorFileDialog
		{
			FileMode = EditorFileDialog.FileModeEnum.OpenFiles,
			Access = EditorFileDialog.AccessEnum.Resources,
			Title = "Select C# Script(s)",
		};

		_folderBindingDialog = new EditorFileDialog
		{
			FileMode = EditorFileDialog.FileModeEnum.OpenDir,
			Access = EditorFileDialog.AccessEnum.Resources,
			Title = "Bind To Folder",
		};

		_createScriptDialog = new EditorFileDialog
		{
			FileMode = EditorFileDialog.FileModeEnum.SaveFile,
			Access = EditorFileDialog.AccessEnum.Resources,
			Title = "Create C# Script",
		};

		_relinkScriptDialog = new EditorFileDialog
		{
			FileMode = EditorFileDialog.FileModeEnum.OpenFile,
			Access = EditorFileDialog.AccessEnum.Resources,
			Title = "Relink C# Script",
		};

		_linkSceneDialog = new EditorFileDialog
		{
			FileMode = EditorFileDialog.FileModeEnum.OpenFile,
			Access = EditorFileDialog.AccessEnum.Resources,
			Title = "Link Godot Scene",
		};

		_addSceneDialog = new EditorFileDialog
		{
			FileMode = EditorFileDialog.FileModeEnum.OpenFiles,
			Access = EditorFileDialog.AccessEnum.Resources,
			Title = "Add Godot Scene(s)",
		};

		_relinkSceneDialog = new EditorFileDialog
		{
			FileMode = EditorFileDialog.FileModeEnum.OpenFile,
			Access = EditorFileDialog.AccessEnum.Resources,
			Title = "Relink Godot Scene",
		};

		_fileDialog.Filters = new[] { "*.cs ; C# Scripts" };
		_createScriptDialog.Filters = new[] { "*.cs ; C# Scripts" };
		_relinkScriptDialog.Filters = new[] { "*.cs ; C# Scripts" };
		_linkSceneDialog.Filters = new[] { "*.tscn ; Godot Scenes" };
		_addSceneDialog.Filters = new[] { "*.tscn ; Godot Scenes" };
		_relinkSceneDialog.Filters = new[] { "*.tscn ; Godot Scenes" };

		_contextMenu = new PopupMenu();
		_contextNewSubmenu = new PopupMenu { Name = "ContextNewSubmenu" };
		_contextAddSubmenu = new PopupMenu { Name = "ContextAddSubmenu" };
		_contextQuickActionsSubmenu = new PopupMenu { Name = "ContextQuickActionsSubmenu" };
		_contextMenu.AddChild(_contextNewSubmenu);
		_contextMenu.AddChild(_contextAddSubmenu);
		_contextMenu.AddChild(_contextQuickActionsSubmenu);

		_removeDialog = new ConfirmationDialog
		{
			Title = "Remove Item",
			DialogText = "Remove selected item from System Explorer?",
			MinSize = new Vector2I(420, 220),
		};

		var removeDialogContainer = new VBoxContainer();

		removeDialogContainer.AddChild(new Control { CustomMinimumSize = new Vector2(0, 68) });

		_removeFromFilesystemCheckBox = new CheckBox
		{
			Text = "Also delete files from FileSystem",
			ButtonPressed = false,
		};

		removeDialogContainer.AddChild(_removeFromFilesystemCheckBox);

		_removeDialog.AddChild(removeDialogContainer);

		_physicalRemoveIncompleteDialog = new AcceptDialog
		{
			Title = "File Deletion Incomplete",
			OkButtonText = "OK",
			MinSize = new Vector2I(520, 180),
		};

		_missingScriptDialog = new ConfirmationDialog
		{
			Title = "Script Not Found",
			DialogText = "The script file could not be found.",
			MinSize = new Vector2I(520, 220),
			OkButtonText = "Relink Script...",
		};

		_missingScriptDialog.AddButton("Remove from Plugin", false, "remove_from_plugin");

		_missingSceneDialog = new ConfirmationDialog
		{
			Title = "Scene Not Found",
			DialogText = "The linked scene could not be found:",
			MinSize = new Vector2I(520, 220),
			OkButtonText = "Relink Scene",
		};

		_missingSceneDialog.AddButton("Remove Scene Link", false, "remove_scene_link");

		_renameDialog = new AcceptDialog { Title = "Rename Item", MinSize = new Vector2I(350, 0) };

		_renameInput = new LineEdit { PlaceholderText = "New name..." };
		_renameDialog.AddChild(_renameInput);
		_renameDialog.RegisterTextEnter(_renameInput);

		_renameInputWarningDialog = CreateInputWarningDialog(
			"Folder Already Exists",
			"A folder with this name already exists."
		);
		_renameDialog.AddChild(_renameInputWarningDialog);

		_addFolderDialog = new AcceptDialog
		{
			Title = "Add Folder",
			MinSize = new Vector2I(350, 0),
			DialogHideOnOk = false,
		};

		_addFolderInput = new LineEdit { PlaceholderText = "Folder name" };
		_addFolderDialog.AddChild(_addFolderInput);
		_addFolderDialog.RegisterTextEnter(_addFolderInput);

		_addFolderInputWarningDialog = CreateInputWarningDialog(
			"Folder Already Exists",
			"A folder with this name already exists."
		);
		_addFolderDialog.AddChild(_addFolderInputWarningDialog);

		_addSystemInputWarningDialog = CreateInputWarningDialog(
			"System Already Exists",
			"A system with this name already exists."
		);

		_createScriptInputWarningDialog = CreateInputWarningDialog(
			"Invalid Script Path",
			"System Explorer cannot create this script because its path is invalid."
		);

		CreateNamespaceRefactorDialogs();
		CreateTreeOperationDialog();
		CreateTreeShortcutConflictDialog();


		_csharpierInstallResultDialog = new AcceptDialog
		{
			Title = "Beautify Script",
			MinSize = new Vector2I(460, 160),
		};

		_csharpierNotInstalledDialog = new ConfirmationDialog
		{
			Title = "CSharpier Required",
			DialogText = "To Beautify Scripts you need CSharpier installed.",
			OkButtonText = "Install",
			MinSize = new Vector2I(460, 180),
		};

		if (!ConnectDockSignals())
			DebugLogger.Log("One or more dock signals could not be connected.");

		if (!ConnectTreeOperationDialogSignals())
			DebugLogger.Log("Tree operation dialog signals could not be connected.");

		if (!ConnectNamespaceRefactorDialogSignals())
			DebugLogger.Log("Refactor Namespace dialog signals could not be connected.");

		_dock.AddChild(_systemNameInput);
		_dock.AddChild(_scriptFilterInput);
		_dock.AddChild(_firstRunWelcomeNote);
		_dock.AddChild(_tree);
		_dock.AddChild(_focusReleaseTarget);
		_dock.AddChild(_fileDialog);
		_dock.AddChild(_folderBindingDialog);
		_dock.AddChild(_relinkScriptDialog);
		_dock.AddChild(_linkSceneDialog);
		_dock.AddChild(_addSceneDialog);
		_dock.AddChild(_relinkSceneDialog);
		_dock.AddChild(_contextMenu);
		_dock.AddChild(_removeDialog);
		_dock.AddChild(_physicalRemoveIncompleteDialog);
		_dock.AddChild(_treeOperationDialog);
		_dock.AddChild(_treeShortcutConflictDialog);
		_dock.AddChild(_missingScriptDialog);
		_dock.AddChild(_missingSceneDialog);
		_dock.AddChild(_renameDialog);
		_dock.AddChild(_addFolderDialog);
		_dock.AddChild(_addSystemInputWarningDialog);
		_dock.AddChild(_createScriptInputWarningDialog);
		_dock.AddChild(_createScriptDialog);
		_dock.AddChild(_namespaceRefactorDialog);
		_dock.AddChild(_namespaceRefactorIncompleteWriteReportDialog);
		_dock.AddChild(_csharpierInstallResultDialog);
		_dock.AddChild(_csharpierNotInstalledDialog);

		_namespaceRefactorHost = CreateNamespaceRefactorHost();
	}

	private MarginContainer CreateFirstRunWelcomeNote()
	{

		Color noteBackgroundColor = Color.FromHtml("#211F1F");
		Color noteTitleColor = Color.FromHtml("#AAA6A6");
		Color noteBodyColor = Color.FromHtml("#908C8C");
		Color noteShadowColor = new Color(0.0f, 0.0f, 0.0f, 0.35f);

		var outerMargin = new MarginContainer
		{
			Name = "First Run Welcome Note",
			Visible = false,
			FocusMode = Control.FocusModeEnum.None,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
		};

		outerMargin.AddThemeConstantOverride(
			"margin_left",
			FirstRunWelcomeNoteMinimumHorizontalMargin
		);
		outerMargin.AddThemeConstantOverride("margin_top", 24);
		outerMargin.AddThemeConstantOverride(
			"margin_right",
			FirstRunWelcomeNoteMinimumHorizontalMargin
		);
		outerMargin.AddThemeConstantOverride("margin_bottom", 12);

		var noteStyle = new StyleBoxFlat
		{
			BgColor = noteBackgroundColor,
			BorderWidthLeft = 0,
			BorderWidthTop = 0,
			BorderWidthRight = 0,
			BorderWidthBottom = 0,
			CornerRadiusTopLeft = 6,
			CornerRadiusTopRight = 6,
			CornerRadiusBottomRight = 6,
			CornerRadiusBottomLeft = 6,
			ShadowColor = noteShadowColor,
			ShadowSize = 4,
			ShadowOffset = new Vector2(0.0f, 2.0f),
		};

		var panel = new PanelContainer
		{
			FocusMode = Control.FocusModeEnum.None,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
		};
		panel.AddThemeStyleboxOverride("panel", noteStyle);


		var innerMargin = new MarginContainer
		{
			FocusMode = Control.FocusModeEnum.None,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
		};
		innerMargin.AddThemeConstantOverride("margin_left", 16);
		innerMargin.AddThemeConstantOverride("margin_top", 14);
		innerMargin.AddThemeConstantOverride("margin_right", 16);
		innerMargin.AddThemeConstantOverride("margin_bottom", 14);

		var content = new VBoxContainer
		{
			FocusMode = Control.FocusModeEnum.None,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
		};
		content.AddThemeConstantOverride("separation", 4);

		var title = new Label
		{
			Text = "Welcome to System Explorer!",
			HorizontalAlignment = HorizontalAlignment.Center,
			FocusMode = Control.FocusModeEnum.None,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
		};
		title.AddThemeColorOverride("font_color", noteTitleColor);
		title.AddThemeFontSizeOverride("font_size", 22);

		var instructions = new Label
		{
			Text = "To get started, type a system name in the System Name field and press Enter.",
			AutowrapMode = TextServer.AutowrapMode.WordSmart,
			FocusMode = Control.FocusModeEnum.None,
			MouseFilter = Control.MouseFilterEnum.Ignore,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
		};
		instructions.AddThemeColorOverride("font_color", noteBodyColor);

		content.AddChild(title);
		content.AddChild(instructions);
		innerMargin.AddChild(content);
		panel.AddChild(innerMargin);
		outerMargin.AddChild(panel);

		return outerMargin;
	}

	private bool AreDockSignalSourcesValid(out string failureDetail)
	{
		var invalidSources = new List<string>();
		AddInvalidDockSignalSource(invalidSources, nameof(_createScriptDialog), _createScriptDialog);
		AddInvalidDockSignalSource(invalidSources, nameof(_relinkScriptDialog), _relinkScriptDialog);
		AddInvalidDockSignalSource(invalidSources, nameof(_linkSceneDialog), _linkSceneDialog);
		AddInvalidDockSignalSource(invalidSources, nameof(_addSceneDialog), _addSceneDialog);
		AddInvalidDockSignalSource(invalidSources, nameof(_relinkSceneDialog), _relinkSceneDialog);
		AddInvalidDockSignalSource(invalidSources, nameof(_missingScriptDialog), _missingScriptDialog);
		AddInvalidDockSignalSource(invalidSources, nameof(_missingSceneDialog), _missingSceneDialog);
		AddInvalidDockSignalSource(invalidSources, nameof(_systemNameInput), _systemNameInput);
		AddInvalidDockSignalSource(invalidSources, nameof(_scriptFilterInput), _scriptFilterInput);
		AddInvalidDockSignalSource(invalidSources, nameof(_tree), _tree);
		AddInvalidDockSignalSource(invalidSources, nameof(_fileDialog), _fileDialog);
		AddInvalidDockSignalSource(invalidSources, nameof(_folderBindingDialog), _folderBindingDialog);
		AddInvalidDockSignalSource(invalidSources, nameof(_contextMenu), _contextMenu);
		AddInvalidDockSignalSource(invalidSources, nameof(_contextNewSubmenu), _contextNewSubmenu);
		AddInvalidDockSignalSource(invalidSources, nameof(_contextAddSubmenu), _contextAddSubmenu);
		AddInvalidDockSignalSource(invalidSources, nameof(_contextQuickActionsSubmenu), _contextQuickActionsSubmenu);
		AddInvalidDockSignalSource(invalidSources, nameof(_removeDialog), _removeDialog);
		AddInvalidDockSignalSource(invalidSources, nameof(_removeFromFilesystemCheckBox), _removeFromFilesystemCheckBox);
		AddInvalidDockSignalSource(invalidSources, nameof(_renameDialog), _renameDialog);
		AddInvalidDockSignalSource(invalidSources, nameof(_renameInputWarningDialog), _renameInputWarningDialog);
		AddInvalidDockSignalSource(invalidSources, nameof(_addFolderDialog), _addFolderDialog);
		AddInvalidDockSignalSource(invalidSources, nameof(_addFolderInputWarningDialog), _addFolderInputWarningDialog);
		AddInvalidDockSignalSource(invalidSources, nameof(_addSystemInputWarningDialog), _addSystemInputWarningDialog);
		AddInvalidDockSignalSource(invalidSources, nameof(_createScriptInputWarningDialog), _createScriptInputWarningDialog);
		AddInvalidDockSignalSource(invalidSources, nameof(_csharpierNotInstalledDialog), _csharpierNotInstalledDialog);
		AddInvalidDockSignalSource(invalidSources, nameof(_firstRunWelcomeNote), _firstRunWelcomeNote);

		failureDetail = invalidSources.Count == 0
			? ""
			: $"Invalid dock signal sources: {string.Join(", ", invalidSources)}";
		return invalidSources.Count == 0;
	}

	private static void AddInvalidDockSignalSource(
		List<string> invalidSources,
		string sourceName,
		GodotObject source
	)
	{
		if (!IsValidGodotObject(source))
			invalidSources.Add(sourceName);
	}

	private bool ConnectDockSignals()
	{
		if (!AreDockSignalSourcesValid(out string failureDetail))
		{
			DebugLogger.LogOperation("Dock signal connection failed", failureDetail);
			return false;
		}

		bool connected = true;
		connected &= TryConnectPluginSignal(_createScriptDialog, EditorFileDialog.SignalName.FileSelected, nameof(OnCreateScriptFileSelectedSignal), nameof(_createScriptDialog));
		connected &= TryConnectPluginSignal(_relinkScriptDialog, EditorFileDialog.SignalName.FileSelected, nameof(OnRelinkScriptFileSelectedSignal), nameof(_relinkScriptDialog));
		connected &= TryConnectPluginSignal(_linkSceneDialog, EditorFileDialog.SignalName.FileSelected, nameof(OnLinkSceneFileSelectedSignal), nameof(_linkSceneDialog));
		connected &= TryConnectPluginSignal(_addSceneDialog, EditorFileDialog.SignalName.FilesSelected, nameof(OnSceneFilesSelectedSignal), nameof(_addSceneDialog));
		connected &= TryConnectPluginSignal(_relinkSceneDialog, EditorFileDialog.SignalName.FileSelected, nameof(OnRelinkSceneFileSelectedSignal), nameof(_relinkSceneDialog));
		connected &= TryConnectPluginSignal(_missingScriptDialog, AcceptDialog.SignalName.Confirmed, nameof(OnMissingScriptRelinkPressedSignal), nameof(_missingScriptDialog));
		connected &= TryConnectPluginSignal(_missingScriptDialog, AcceptDialog.SignalName.CustomAction, nameof(OnMissingScriptCustomActionSignal), nameof(_missingScriptDialog));
		connected &= TryConnectPluginSignal(_missingSceneDialog, AcceptDialog.SignalName.Confirmed, nameof(OnMissingSceneRelinkPressedSignal), nameof(_missingSceneDialog));
		connected &= TryConnectPluginSignal(_missingSceneDialog, AcceptDialog.SignalName.CustomAction, nameof(OnMissingSceneCustomActionSignal), nameof(_missingSceneDialog));
		connected &= TryConnectPluginSignal(_systemNameInput, LineEdit.SignalName.TextChanged, nameof(OnSystemNameTextChanged), nameof(_systemNameInput));
		connected &= TryConnectPluginSignal(_systemNameInput, LineEdit.SignalName.TextSubmitted, nameof(OnSystemNameSubmitted), nameof(_systemNameInput));
		connected &= TryConnectPluginSignal(_systemNameInput, Control.SignalName.GuiInput, nameof(OnSystemNameInputGuiInputSignal), nameof(_systemNameInput));
		connected &= TryConnectPluginSignal(_systemNameInput, Control.SignalName.MouseExited, nameof(OnSystemNameInputMouseExited), nameof(_systemNameInput));
		connected &= TryConnectPluginSignal(_scriptFilterInput, LineEdit.SignalName.TextChanged, nameof(OnScriptFilterTextChangedSignal), nameof(_scriptFilterInput));
		connected &= TryConnectPluginSignal(_scriptFilterInput, Control.SignalName.GuiInput, nameof(OnScriptFilterInputGuiInputSignal), nameof(_scriptFilterInput));
		connected &= TryConnectPluginSignal(_scriptFilterInput, Control.SignalName.MouseExited, nameof(OnScriptFilterInputMouseExited), nameof(_scriptFilterInput));
		connected &= TryConnectPluginSignal(_tree, Tree.SignalName.ItemSelected, nameof(OnItemSelectedSignal), nameof(_tree));
		TryConnectPluginSignal(_tree, Tree.SignalName.ItemCollapsed, nameof(OnTreeItemCollapsedSignal), nameof(_tree));
		connected &= TryConnectPluginSignal(_tree, Control.SignalName.GuiInput, nameof(OnTreeGuiInputSignal), nameof(_tree));
		connected &= TryConnectPluginSignal(_tree, Control.SignalName.MouseExited, nameof(OnTreeMouseExited), nameof(_tree));
		connected &= TryConnectPluginSignal(_fileDialog, EditorFileDialog.SignalName.FilesSelected, nameof(OnScriptFilesSelectedSignal), nameof(_fileDialog));
		connected &= TryConnectPluginSignal(_folderBindingDialog, EditorFileDialog.SignalName.DirSelected, nameof(OnFolderBindingDirectorySelectedSignal), nameof(_folderBindingDialog));
		connected &= TryConnectPluginSignal(_contextMenu, PopupMenu.SignalName.IdPressed, nameof(OnContextMenuIdPressedSignal), nameof(_contextMenu));
		connected &= TryConnectPluginSignal(_contextNewSubmenu, PopupMenu.SignalName.IdPressed, nameof(OnContextMenuIdPressedSignal), nameof(_contextNewSubmenu));
		connected &= TryConnectPluginSignal(_contextAddSubmenu, PopupMenu.SignalName.IdPressed, nameof(OnContextMenuIdPressedSignal), nameof(_contextAddSubmenu));
		connected &= TryConnectPluginSignal(_contextQuickActionsSubmenu, PopupMenu.SignalName.IdPressed, nameof(OnContextMenuIdPressedSignal), nameof(_contextQuickActionsSubmenu));
		connected &= TryConnectPluginSignal(_removeDialog, AcceptDialog.SignalName.Confirmed, nameof(OnRemoveConfirmedSignal), nameof(_removeDialog));
		connected &= TryConnectPluginSignal(_removeDialog, Window.SignalName.WindowInput, nameof(OnRemoveDialogWindowInputSignal), nameof(_removeDialog));
		connected &= TryConnectPluginSignal(_removeFromFilesystemCheckBox, Control.SignalName.GuiInput, nameof(OnRemoveDialogWindowInputSignal), nameof(_removeFromFilesystemCheckBox));
		connected &= TryConnectPluginSignal(_renameDialog, AcceptDialog.SignalName.Confirmed, nameof(OnRenameConfirmedSignal), nameof(_renameDialog));
		connected &= TryConnectPluginSignal(_renameInputWarningDialog, AcceptDialog.SignalName.Confirmed, nameof(OnRenameInputWarningDialogClosed), nameof(_renameInputWarningDialog));
		connected &= TryConnectPluginSignal(_renameInputWarningDialog, AcceptDialog.SignalName.Canceled, nameof(OnRenameInputWarningDialogClosed), nameof(_renameInputWarningDialog));
		connected &= TryConnectPluginSignal(_addFolderDialog, AcceptDialog.SignalName.Confirmed, nameof(OnAddFolderConfirmedSignal), nameof(_addFolderDialog));
		connected &= TryConnectPluginSignal(_addFolderInputWarningDialog, AcceptDialog.SignalName.Confirmed, nameof(OnAddFolderInputWarningDialogClosed), nameof(_addFolderInputWarningDialog));
		connected &= TryConnectPluginSignal(_addFolderInputWarningDialog, AcceptDialog.SignalName.Canceled, nameof(OnAddFolderInputWarningDialogClosed), nameof(_addFolderInputWarningDialog));
		connected &= TryConnectPluginSignal(_addSystemInputWarningDialog, AcceptDialog.SignalName.Confirmed, nameof(OnAddSystemInputWarningDialogClosed), nameof(_addSystemInputWarningDialog));
		connected &= TryConnectPluginSignal(_addSystemInputWarningDialog, AcceptDialog.SignalName.Canceled, nameof(OnAddSystemInputWarningDialogClosed), nameof(_addSystemInputWarningDialog));
		connected &= TryConnectPluginSignal(_createScriptInputWarningDialog, AcceptDialog.SignalName.Confirmed, nameof(OnCreateScriptInputWarningDialogClosed), nameof(_createScriptInputWarningDialog));
		connected &= TryConnectPluginSignal(_createScriptInputWarningDialog, AcceptDialog.SignalName.Canceled, nameof(OnCreateScriptInputWarningDialogClosed), nameof(_createScriptInputWarningDialog));
		connected &= TryConnectPluginSignal(_csharpierNotInstalledDialog, AcceptDialog.SignalName.Confirmed, nameof(OnCSharpierInstallConfirmedSignal), nameof(_csharpierNotInstalledDialog));
		connected &= TryConnectPluginSignal(_firstRunWelcomeNote, Control.SignalName.Resized, nameof(UpdateFirstRunWelcomeNoteHorizontalMargins), nameof(_firstRunWelcomeNote));
		connected &= TryConnectPluginSignal(_firstRunWelcomeNote, CanvasItem.SignalName.VisibilityChanged, nameof(UpdateFirstRunWelcomeNoteHorizontalMargins), nameof(_firstRunWelcomeNote));

		if (!connected)
			return false;

		UpdateFirstRunWelcomeNoteHorizontalMargins();
		return true;
	}

	private void DisconnectDockSignals()
	{
		DisconnectPluginSignal(_createScriptDialog, EditorFileDialog.SignalName.FileSelected, nameof(OnCreateScriptFileSelectedSignal), nameof(_createScriptDialog));
		DisconnectPluginSignal(_relinkScriptDialog, EditorFileDialog.SignalName.FileSelected, nameof(OnRelinkScriptFileSelectedSignal), nameof(_relinkScriptDialog));
		DisconnectPluginSignal(_linkSceneDialog, EditorFileDialog.SignalName.FileSelected, nameof(OnLinkSceneFileSelectedSignal), nameof(_linkSceneDialog));
		DisconnectPluginSignal(_addSceneDialog, EditorFileDialog.SignalName.FilesSelected, nameof(OnSceneFilesSelectedSignal), nameof(_addSceneDialog));
		DisconnectPluginSignal(_relinkSceneDialog, EditorFileDialog.SignalName.FileSelected, nameof(OnRelinkSceneFileSelectedSignal), nameof(_relinkSceneDialog));
		DisconnectPluginSignal(_missingScriptDialog, AcceptDialog.SignalName.Confirmed, nameof(OnMissingScriptRelinkPressedSignal), nameof(_missingScriptDialog));
		DisconnectPluginSignal(_missingScriptDialog, AcceptDialog.SignalName.CustomAction, nameof(OnMissingScriptCustomActionSignal), nameof(_missingScriptDialog));
		DisconnectPluginSignal(_missingSceneDialog, AcceptDialog.SignalName.Confirmed, nameof(OnMissingSceneRelinkPressedSignal), nameof(_missingSceneDialog));
		DisconnectPluginSignal(_missingSceneDialog, AcceptDialog.SignalName.CustomAction, nameof(OnMissingSceneCustomActionSignal), nameof(_missingSceneDialog));
		DisconnectPluginSignal(_systemNameInput, LineEdit.SignalName.TextChanged, nameof(OnSystemNameTextChanged), nameof(_systemNameInput));
		DisconnectPluginSignal(_systemNameInput, LineEdit.SignalName.TextSubmitted, nameof(OnSystemNameSubmitted), nameof(_systemNameInput));
		DisconnectPluginSignal(_systemNameInput, Control.SignalName.GuiInput, nameof(OnSystemNameInputGuiInputSignal), nameof(_systemNameInput));
		DisconnectPluginSignal(_systemNameInput, Control.SignalName.MouseExited, nameof(OnSystemNameInputMouseExited), nameof(_systemNameInput));
		DisconnectPluginSignal(_scriptFilterInput, LineEdit.SignalName.TextChanged, nameof(OnScriptFilterTextChangedSignal), nameof(_scriptFilterInput));
		DisconnectPluginSignal(_scriptFilterInput, Control.SignalName.GuiInput, nameof(OnScriptFilterInputGuiInputSignal), nameof(_scriptFilterInput));
		DisconnectPluginSignal(_scriptFilterInput, Control.SignalName.MouseExited, nameof(OnScriptFilterInputMouseExited), nameof(_scriptFilterInput));
		DisconnectPluginSignal(_tree, Tree.SignalName.ItemSelected, nameof(OnItemSelectedSignal), nameof(_tree));
		DisconnectPluginSignal(_tree, Tree.SignalName.ItemCollapsed, nameof(OnTreeItemCollapsedSignal), nameof(_tree));
		DisconnectPluginSignal(_tree, Control.SignalName.GuiInput, nameof(OnTreeGuiInputSignal), nameof(_tree));
		DisconnectPluginSignal(_tree, Control.SignalName.MouseExited, nameof(OnTreeMouseExited), nameof(_tree));
		DisconnectPluginSignal(_fileDialog, EditorFileDialog.SignalName.FilesSelected, nameof(OnScriptFilesSelectedSignal), nameof(_fileDialog));
		DisconnectPluginSignal(_folderBindingDialog, EditorFileDialog.SignalName.DirSelected, nameof(OnFolderBindingDirectorySelectedSignal), nameof(_folderBindingDialog));
		DisconnectPluginSignal(_contextMenu, PopupMenu.SignalName.IdPressed, nameof(OnContextMenuIdPressedSignal), nameof(_contextMenu));
		DisconnectPluginSignal(_contextNewSubmenu, PopupMenu.SignalName.IdPressed, nameof(OnContextMenuIdPressedSignal), nameof(_contextNewSubmenu));
		DisconnectPluginSignal(_contextAddSubmenu, PopupMenu.SignalName.IdPressed, nameof(OnContextMenuIdPressedSignal), nameof(_contextAddSubmenu));
		DisconnectPluginSignal(_contextQuickActionsSubmenu, PopupMenu.SignalName.IdPressed, nameof(OnContextMenuIdPressedSignal), nameof(_contextQuickActionsSubmenu));
		DisconnectPluginSignal(_removeDialog, AcceptDialog.SignalName.Confirmed, nameof(OnRemoveConfirmedSignal), nameof(_removeDialog));
		DisconnectPluginSignal(_removeDialog, Window.SignalName.WindowInput, nameof(OnRemoveDialogWindowInputSignal), nameof(_removeDialog));
		DisconnectPluginSignal(_removeFromFilesystemCheckBox, Control.SignalName.GuiInput, nameof(OnRemoveDialogWindowInputSignal), nameof(_removeFromFilesystemCheckBox));
		DisconnectPluginSignal(_renameDialog, AcceptDialog.SignalName.Confirmed, nameof(OnRenameConfirmedSignal), nameof(_renameDialog));
		DisconnectPluginSignal(_renameInputWarningDialog, AcceptDialog.SignalName.Confirmed, nameof(OnRenameInputWarningDialogClosed), nameof(_renameInputWarningDialog));
		DisconnectPluginSignal(_renameInputWarningDialog, AcceptDialog.SignalName.Canceled, nameof(OnRenameInputWarningDialogClosed), nameof(_renameInputWarningDialog));
		DisconnectPluginSignal(_addFolderDialog, AcceptDialog.SignalName.Confirmed, nameof(OnAddFolderConfirmedSignal), nameof(_addFolderDialog));
		DisconnectPluginSignal(_addFolderInputWarningDialog, AcceptDialog.SignalName.Confirmed, nameof(OnAddFolderInputWarningDialogClosed), nameof(_addFolderInputWarningDialog));
		DisconnectPluginSignal(_addFolderInputWarningDialog, AcceptDialog.SignalName.Canceled, nameof(OnAddFolderInputWarningDialogClosed), nameof(_addFolderInputWarningDialog));
		DisconnectPluginSignal(_addSystemInputWarningDialog, AcceptDialog.SignalName.Confirmed, nameof(OnAddSystemInputWarningDialogClosed), nameof(_addSystemInputWarningDialog));
		DisconnectPluginSignal(_addSystemInputWarningDialog, AcceptDialog.SignalName.Canceled, nameof(OnAddSystemInputWarningDialogClosed), nameof(_addSystemInputWarningDialog));
		DisconnectPluginSignal(_createScriptInputWarningDialog, AcceptDialog.SignalName.Confirmed, nameof(OnCreateScriptInputWarningDialogClosed), nameof(_createScriptInputWarningDialog));
		DisconnectPluginSignal(_createScriptInputWarningDialog, AcceptDialog.SignalName.Canceled, nameof(OnCreateScriptInputWarningDialogClosed), nameof(_createScriptInputWarningDialog));
		DisconnectPluginSignal(_csharpierNotInstalledDialog, AcceptDialog.SignalName.Confirmed, nameof(OnCSharpierInstallConfirmedSignal), nameof(_csharpierNotInstalledDialog));
		DisconnectPluginSignal(_firstRunWelcomeNote, Control.SignalName.Resized, nameof(UpdateFirstRunWelcomeNoteHorizontalMargins), nameof(_firstRunWelcomeNote));
		DisconnectPluginSignal(_firstRunWelcomeNote, CanvasItem.SignalName.VisibilityChanged, nameof(UpdateFirstRunWelcomeNoteHorizontalMargins), nameof(_firstRunWelcomeNote));
	}

	private void UpdateFirstRunWelcomeNoteHorizontalMargins()
	{
		if (!IsValidGodotObject(_firstRunWelcomeNote))
			return;

		int resolvedHorizontalMargin = Mathf.Max(
			FirstRunWelcomeNoteMinimumHorizontalMargin,
			Mathf.RoundToInt(
				(_firstRunWelcomeNote.Size.X - FirstRunWelcomeNoteMaximumWidth) / 2.0f
			)
		);

		_firstRunWelcomeNote.AddThemeConstantOverride("margin_left", resolvedHorizontalMargin);
		_firstRunWelcomeNote.AddThemeConstantOverride("margin_right", resolvedHorizontalMargin);
	}

	private void OnCreateScriptFileSelectedSignal(string path)
	{
		if (EnsureManagedAssemblyStateCurrent("Create Script File Selected"))
			OnCreateScriptFileSelected(path);
	}

	private void OnRelinkScriptFileSelectedSignal(string path)
	{
		if (EnsureManagedAssemblyStateCurrent("Relink Script File Selected"))
			OnRelinkScriptFileSelected(path);
	}

	private void OnLinkSceneFileSelectedSignal(string path)
	{
		if (EnsureManagedAssemblyStateCurrent("Link Scene File Selected"))
			OnLinkSceneFileSelected(path);
	}

	private void OnSceneFilesSelectedSignal(string[] paths)
	{
		if (EnsureManagedAssemblyStateCurrent("Add Scene Files Selected"))
			OnSceneFilesSelected(paths);
	}

	private void OnRelinkSceneFileSelectedSignal(string path)
	{
		if (EnsureManagedAssemblyStateCurrent("Relink Scene File Selected"))
			OnRelinkSceneFileSelected(path);
	}

	private void OnMissingScriptRelinkPressedSignal()
	{
		if (EnsureManagedAssemblyStateCurrent("Missing Script Relink"))
			OnMissingScriptRelinkPressed();
	}

	private void OnMissingScriptCustomActionSignal(StringName action)
	{
		if (EnsureManagedAssemblyStateCurrent("Missing Script Custom Action"))
			OnMissingScriptCustomAction(action);
	}

	private void OnMissingSceneRelinkPressedSignal()
	{
		if (EnsureManagedAssemblyStateCurrent("Missing Scene Relink"))
			OnMissingSceneRelinkPressed();
	}

	private void OnMissingSceneCustomActionSignal(StringName action)
	{
		if (EnsureManagedAssemblyStateCurrent("Missing Scene Custom Action"))
			OnMissingSceneCustomAction(action);
	}

	private void OnSystemNameSubmitted(string submittedText)
	{
		if (
			HasMissingSystemsWithFolderBindingsConflict
			|| EnsureManagedAssemblyStateCurrent("Submit System Name")
		)
		{
			OnAddSystemPressed();
		}
	}

	private void OnSystemNameInputGuiInputSignal(InputEvent inputEvent)
	{
		if (
			HasMissingSystemsWithFolderBindingsConflict
			|| EnsureManagedAssemblyStateCurrent("System Name Input")
		)
		{
			OnSystemNameInputGuiInput(inputEvent);
		}
	}

	private void OnScriptFilterTextChangedSignal(string filterText)
	{
		if (EnsureManagedAssemblyStateCurrent("Filter Tree"))
			OnScriptFilterTextChanged(filterText);
	}

	private void OnScriptFilterInputGuiInputSignal(InputEvent inputEvent)
	{
		if (EnsureManagedAssemblyStateCurrent("Script Filter Input"))
			OnScriptFilterInputGuiInput(inputEvent);
	}

	private void OnItemSelectedSignal()
	{
		LogScriptEditorCallbackEntry("TreeItemSelected");

		if (EnsureManagedAssemblyStateCurrent("Tree Item Selected"))
			OnItemSelected();
	}

	private void OnTreeItemCollapsedSignal(TreeItem item)
	{
		if (EnsureManagedAssemblyStateCurrent("Tree Item Expansion Changed"))
			QueuePersistentTreeStateSave();
	}

	private void OnTreeGuiInputSignal(InputEvent inputEvent)
	{
		if (EnsureManagedAssemblyStateCurrent("Tree Input"))
			OnTreeGuiInput(inputEvent);
	}

	private void OnScriptFilesSelectedSignal(string[] paths)
	{
		if (EnsureManagedAssemblyStateCurrent("Add Script Files Selected"))
			OnScriptFilesSelected(paths);
	}

	private void OnFolderBindingDirectorySelectedSignal(string selectedDirectory)
	{
		if (EnsureManagedAssemblyStateCurrent("Folder Binding Directory Selected"))
			OnFolderBindingDirectorySelected(selectedDirectory);
	}

	private void OnContextMenuIdPressedSignal(long id)
	{
		if (EnsureManagedAssemblyStateCurrent("Context Menu Action"))
			OnContextMenuIdPressed(id);
	}

	private void OnRemoveDialogWindowInputSignal(InputEvent inputEvent)
	{
		if (EnsureManagedAssemblyStateCurrent("Remove Dialog Input"))
			OnRemoveDialogWindowInput(inputEvent);
	}

	private void OnRemoveConfirmedSignal()
	{
		if (EnsureManagedAssemblyStateCurrent("Remove Confirmed"))
			OnRemoveConfirmed();
	}

	private void OnRenameConfirmedSignal()
	{
		if (EnsureManagedAssemblyStateCurrent("Rename Confirmed"))
			OnRenameConfirmed();
	}

	private void OnAddFolderConfirmedSignal()
	{
		if (EnsureManagedAssemblyStateCurrent("Add Folder Confirmed"))
			OnAddFolderConfirmed();
	}

	private void OnCSharpierInstallConfirmedSignal()
	{
		if (EnsureManagedAssemblyStateCurrent("Install CSharpier"))
			OnCSharpierInstallConfirmed();
	}

	private void ClearDockControlReferences()
	{
		_systemNameInput = null;
		_scriptFilterInput = null;
		_firstRunWelcomeNote = null;
		_tree = null;
		_focusReleaseTarget = null;
		_fileDialog = null;
		_folderBindingDialog = null;
		_contextMenu = null;
		_contextNewSubmenu = null;
		_contextAddSubmenu = null;
		_contextQuickActionsSubmenu = null;
		_removeDialog = null;
		_physicalRemoveIncompleteDialog = null;
		_treeShortcutConflictDialog = null;
		_removeFromFilesystemCheckBox = null;
		_renameDialog = null;
		_renameInput = null;
		_renameInputWarningDialog = null;
		_addFolderDialog = null;
		_addFolderInput = null;
		_addFolderInputWarningDialog = null;
		_addSystemInputWarningDialog = null;
		_createScriptInputWarningDialog = null;
		_namespaceRefactorDialog = null;
		_namespaceRefactorIncompleteWriteReportDialog = null;
		_namespaceRefactorDescriptionLabel = null;
		_namespaceRefactorNewNamespaceLabel = null;
		_namespaceRefactorNewNamespaceInput = null;
		_namespaceRefactorOldNamespaceLabel = null;
		_namespaceRefactorOldNamespaceInput = null;
		_namespaceRefactorApplyToLabel = null;
		_namespaceRefactorExistingNamespaceOption = null;
		_namespaceRefactorExistingNamespaceDropdown = null;
		_namespaceRefactorWithoutNamespaceOption = null;
		_csharpierInstallResultDialog = null;
		_csharpierNotInstalledDialog = null;
		_createScriptDialog = null;
		_relinkScriptDialog = null;
		_linkSceneDialog = null;
		_addSceneDialog = null;
		_relinkSceneDialog = null;
		_missingScriptDialog = null;
		_missingSceneDialog = null;
		_treeOperationDialog = null;
		_isRenameInputWarningPopupPending = false;
		_isAddFolderInputWarningPopupPending = false;
		_isAddSystemInputWarningPopupPending = false;
		_isCreateScriptInputWarningPopupPending = false;
	}

	private void UpdateFirstRunWelcomeNoteVisibility()
	{
		if (_firstRunWelcomeNote == null)
			return;

		_firstRunWelcomeNote.Visible =
			HasVerifiedPersistentTreeStateForCurrentAssembly
			&& _systems.Count == 0
			&& !FileAccess.FileExists(SavePath);
	}

	private static AcceptDialog CreateInputWarningDialog(string title, string dialogText)
	{
		var dialog = new AcceptDialog
		{
			Title = title,
			DialogText = dialogText,
			OkButtonText = "OK",
			MinSize = WrappedAcceptDialogMinimumSize,
			DialogAutowrap = true,
			Unresizable = true,
		};

		ConfigureWrappedAcceptDialogMessageLabel(dialog);
		return dialog;
	}

	private void ConfigureTreeColumns()
	{
		if (_tree == null)
			return;

		_tree.Columns = 1;
		_tree.SetColumnExpand(0, true);
	}

	private void ConfigureTreeDirectionalFocusFallback()
	{
		if (
			_tree == null
			|| !GodotObject.IsInstanceValid(_tree)
			|| _focusReleaseTarget == null
			|| !GodotObject.IsInstanceValid(_focusReleaseTarget)
		)
		{
			return;
		}

		NodePath focusReleaseTargetPath = new NodePath("../Focus Release Target");

		_tree.FocusNeighborTop = focusReleaseTargetPath;
		_tree.FocusNeighborBottom = focusReleaseTargetPath;
		_tree.FocusNeighborLeft = focusReleaseTargetPath;
		_tree.FocusNeighborRight = focusReleaseTargetPath;
	}

	private void OnSystemNameTextChanged(string text)
	{
		UpdateSystemNameEnterIconVisibility(text);
	}

	private void UpdateSystemNameEnterIconVisibility(string text)
	{
		if (_systemNameInput == null)
			return;

		_systemNameInput.RightIcon = !string.IsNullOrWhiteSpace(text) ? _systemNameEnterIcon : null;

		if (string.IsNullOrWhiteSpace(text))
			ResetSystemNameInputCursor();
	}

	private void ClearSystemNameInputForKeyboardNavigation()
	{
		if (
			_systemNameInput == null
			|| !GodotObject.IsInstanceValid(_systemNameInput)
			|| _systemNameInput.IsQueuedForDeletion()
		)
		{
			return;
		}

		_systemNameInput.Text = "";
		UpdateSystemNameEnterIconVisibility("");
		_systemNameInput.Edit(true);
	}

	private void OnSystemNameInputGuiInput(InputEvent inputEvent)
	{
		if (_systemNameInput == null)
			return;

		if (inputEvent is InputEventMouseMotion mouseMotion)
		{
			UpdateSystemNameInputCursor(mouseMotion.Position);
			return;
		}

		if (string.IsNullOrWhiteSpace(_systemNameInput.Text))
			return;

		if (inputEvent is not InputEventMouseButton mouseButton)
			return;

		if (mouseButton.ButtonIndex != MouseButton.Left || !mouseButton.Pressed)
			return;

		if (!IsLineEditRightIconClick(_systemNameInput, mouseButton.Position))
			return;

		OnAddSystemPressed();
		_systemNameInput.AcceptEvent();
	}

	private void OnSystemNameInputMouseExited()
	{
		ResetSystemNameInputCursor();
	}

	private void UpdateSystemNameInputCursor(Vector2 localMousePosition)
	{
		if (_systemNameInput == null)
			return;

		if (IsEditorOperationBusyCursorActive)
		{
			_systemNameInput.MouseDefaultCursorShape = Control.CursorShape.Busy;
			return;
		}

		bool isHoveringAddIcon =
			!string.IsNullOrWhiteSpace(_systemNameInput.Text)
			&& _systemNameInput.RightIcon == _systemNameEnterIcon
			&& IsLineEditRightIconClick(_systemNameInput, localMousePosition);

		_systemNameInput.MouseDefaultCursorShape = isHoveringAddIcon
			? Control.CursorShape.Arrow
			: Control.CursorShape.Ibeam;
	}

	private void ResetSystemNameInputCursor()
	{
		if (_systemNameInput == null)
			return;

		_systemNameInput.MouseDefaultCursorShape = IsEditorOperationBusyCursorActive
			? Control.CursorShape.Busy
			: Control.CursorShape.Ibeam;
	}

	#endregion
}
#endif
