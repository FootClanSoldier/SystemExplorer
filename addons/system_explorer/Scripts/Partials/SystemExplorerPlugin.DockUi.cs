#if TOOLS
using Godot;
using SystemExplorer.QuickActions.RefactorNamespace;

public partial class SystemExplorerPlugin
{
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

		_renameNameConflictDialog = CreateNameConflictDialog(
			"Folder Already Exists",
			"A folder with this name already exists."
		);
		_renameDialog.AddChild(_renameNameConflictDialog);

		_addFolderDialog = new AcceptDialog
		{
			Title = "Add Folder",
			MinSize = new Vector2I(350, 0),
			DialogHideOnOk = false,
		};

		_addFolderInput = new LineEdit { PlaceholderText = "Folder name" };
		_addFolderDialog.AddChild(_addFolderInput);
		_addFolderDialog.RegisterTextEnter(_addFolderInput);

		_addFolderConflictDialog = CreateNameConflictDialog(
			"Folder Already Exists",
			"A folder with this name already exists."
		);
		_addFolderDialog.AddChild(_addFolderConflictDialog);

		_addSystemConflictDialog = CreateNameConflictDialog(
			"System Already Exists",
			"A system with this name already exists."
		);

		CreateNamespaceRefactorDialogs();
		CreateTreeOperationDialog();


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

		_createScriptDialog.FileSelected += OnCreateScriptFileSelected;
		_relinkScriptDialog.FileSelected += OnRelinkScriptFileSelected;
		_linkSceneDialog.FileSelected += OnLinkSceneFileSelected;
		_addSceneDialog.FilesSelected += OnSceneFilesSelected;
		_relinkSceneDialog.FileSelected += OnRelinkSceneFileSelected;
		_missingScriptDialog.Confirmed += OnMissingScriptRelinkPressed;
		_missingScriptDialog.CustomAction += OnMissingScriptCustomAction;
		_missingSceneDialog.Confirmed += OnMissingSceneRelinkPressed;
		_missingSceneDialog.CustomAction += OnMissingSceneCustomAction;
		_systemNameInput.TextChanged += OnSystemNameTextChanged;
		_systemNameInput.TextSubmitted += _ => OnAddSystemPressed();
		_systemNameInput.GuiInput += OnSystemNameInputGuiInput;
		_systemNameInput.MouseExited += OnSystemNameInputMouseExited;
		_scriptFilterInput.TextChanged += OnScriptFilterTextChanged;
		_scriptFilterInput.GuiInput += OnScriptFilterInputGuiInput;
		_scriptFilterInput.MouseExited += OnScriptFilterInputMouseExited;
		_tree.ItemSelected += OnItemSelected;
		_tree.GuiInput += OnTreeGuiInput;
		_tree.MouseExited += OnTreeMouseExited;
		_fileDialog.FilesSelected += OnScriptFilesSelected;
		_folderBindingDialog.DirSelected += OnFolderBindingDirectorySelected;
		_contextMenu.IdPressed += OnContextMenuIdPressed;
		_contextNewSubmenu.IdPressed += OnContextMenuIdPressed;
		_contextAddSubmenu.IdPressed += OnContextMenuIdPressed;
		_contextQuickActionsSubmenu.IdPressed += OnContextMenuIdPressed;
		_removeDialog.Confirmed += OnRemoveConfirmed;
		_removeDialog.WindowInput += OnRemoveDialogWindowInput;
		_removeFromFilesystemCheckBox.GuiInput += OnRemoveDialogWindowInput;
		_renameDialog.Confirmed += OnRenameConfirmed;
		_renameNameConflictDialog.Confirmed += OnRenameNameConflictDialogClosed;
		_renameNameConflictDialog.Canceled += OnRenameNameConflictDialogClosed;
		_addFolderDialog.Confirmed += OnAddFolderConfirmed;
		_addFolderConflictDialog.Confirmed += OnAddFolderConflictDialogClosed;
		_addFolderConflictDialog.Canceled += OnAddFolderConflictDialogClosed;
		_addSystemConflictDialog.Confirmed += OnAddSystemConflictDialogClosed;
		_addSystemConflictDialog.Canceled += OnAddSystemConflictDialogClosed;
		_csharpierNotInstalledDialog.Confirmed += OnCSharpierInstallConfirmed;
		ConnectNamespaceRefactorDialogSignals();

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
		_dock.AddChild(_missingScriptDialog);
		_dock.AddChild(_missingSceneDialog);
		_dock.AddChild(_renameDialog);
		_dock.AddChild(_addFolderDialog);
		_dock.AddChild(_addSystemConflictDialog);
		_dock.AddChild(_createScriptDialog);
		_dock.AddChild(_namespaceRefactorDialog);
		_dock.AddChild(_namespaceRefactorIncompleteWriteReportDialog);
		_dock.AddChild(_csharpierInstallResultDialog);
		_dock.AddChild(_csharpierNotInstalledDialog);

		_namespaceRefactorHost = CreateNamespaceRefactorHost();
	}

	private static MarginContainer CreateFirstRunWelcomeNote()
	{
		float noteMaximumWidth = 700.0f;
		int noteMinimumHorizontalMargin = 16;

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

		outerMargin.AddThemeConstantOverride("margin_left", noteMinimumHorizontalMargin);
		outerMargin.AddThemeConstantOverride("margin_top", 24);
		outerMargin.AddThemeConstantOverride("margin_right", noteMinimumHorizontalMargin);
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

		void UpdateNoteHorizontalMargins()
		{
			int resolvedHorizontalMargin = Mathf.Max(
				noteMinimumHorizontalMargin,
				Mathf.RoundToInt((outerMargin.Size.X - noteMaximumWidth) / 2.0f)
			);

			outerMargin.AddThemeConstantOverride("margin_left", resolvedHorizontalMargin);
			outerMargin.AddThemeConstantOverride("margin_right", resolvedHorizontalMargin);
		}

		outerMargin.Resized += UpdateNoteHorizontalMargins;
		outerMargin.VisibilityChanged += UpdateNoteHorizontalMargins;

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

	private void UpdateFirstRunWelcomeNoteVisibility()
	{
		if (_firstRunWelcomeNote == null)
			return;

		_firstRunWelcomeNote.Visible = _systems.Count == 0 && !FileAccess.FileExists(SavePath);
	}

	private static AcceptDialog CreateNameConflictDialog(string title, string dialogText)
	{
		return new AcceptDialog
		{
			Title = title,
			DialogText = dialogText,
			OkButtonText = "OK",
			MinSize = new Vector2I(420, 160),
		};
	}

	private void ConfigureTreeColumns()
	{
		if (_tree == null)
			return;

		_tree.Columns = 1;
		_tree.SetColumnExpand(0, true);
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

		_systemNameInput.MouseDefaultCursorShape = Control.CursorShape.Ibeam;
	}

	#endregion
}
#endif
