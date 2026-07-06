#if TOOLS
using Godot;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

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
			Text = "Also delete script file(s) from FileSystem",
			ButtonPressed = false,
		};

		removeDialogContainer.AddChild(_removeFromFilesystemCheckBox);

		_removeDialog.AddChild(removeDialogContainer);

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

		_addFolderDialog = new AcceptDialog
		{
			Title = "Add Folder",
			MinSize = new Vector2I(350, 0),
		};

		_addFolderInput = new LineEdit { PlaceholderText = "Folder name" };
		_addFolderDialog.AddChild(_addFolderInput);

		_refactorNamespaceDialog = new AcceptDialog
		{
			Title = "Refactor Namespace",
			MinSize = RefactorNamespaceDialogSize,
			Unresizable = true,
		};

		var refactorNamespaceContainer = new VBoxContainer
		{
			CustomMinimumSize = new Vector2(480, 0),
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
		};
		refactorNamespaceContainer.AddChild(
			new Label
			{
				Text =
					"Update the selected script namespace and\nmatching using statements in linked C# files.",
				AutowrapMode = TextServer.AutowrapMode.Off,
				SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			}
		);
		refactorNamespaceContainer.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });
		refactorNamespaceContainer.AddChild(new Label { Text = "Old namespace" });
		_oldNamespaceInput = new LineEdit { PlaceholderText = "Old namespace", Editable = false };
		refactorNamespaceContainer.AddChild(_oldNamespaceInput);
		refactorNamespaceContainer.AddChild(new Label { Text = "New namespace" });
		_newNamespaceInput = new LineEdit { PlaceholderText = "New namespace" };
		refactorNamespaceContainer.AddChild(_newNamespaceInput);
		_refactorNamespaceDialog.AddChild(refactorNamespaceContainer);

		_csharpierInstalledDialog = new AcceptDialog
		{
			Title = "Beautify Script",
			DialogText = "CSharpier is already installed.",
			MinSize = new Vector2I(420, 160),
		};

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
		_contextMenu.IdPressed += OnContextMenuIdPressed;
		_contextNewSubmenu.IdPressed += OnContextMenuIdPressed;
		_contextAddSubmenu.IdPressed += OnContextMenuIdPressed;
		_contextQuickActionsSubmenu.IdPressed += OnContextMenuIdPressed;
		_removeDialog.Confirmed += OnRemoveConfirmed;
		_removeDialog.WindowInput += OnRemoveDialogWindowInput;
		_removeFromFilesystemCheckBox.GuiInput += OnRemoveDialogWindowInput;
		_renameDialog.Confirmed += OnRenameConfirmed;
		_renameDialog.WindowInput += OnRenameDialogWindowInput;
		_renameInput.TextSubmitted += _ => ConfirmRenameDialogFromEnter();
		_addFolderDialog.Confirmed += OnAddFolderConfirmed;
		_addFolderDialog.WindowInput += OnAddFolderDialogWindowInput;
		_addFolderInput.TextSubmitted += _ => ConfirmAddFolderDialogFromEnter();
		_refactorNamespaceDialog.Confirmed += OnRefactorNamespaceConfirmed;
		_csharpierNotInstalledDialog.Confirmed += OnCSharpierInstallConfirmed;
		_refactorNamespaceDialog.WindowInput += OnRefactorNamespaceDialogWindowInput;
		_oldNamespaceInput.TextSubmitted += _ => ConfirmRefactorNamespaceDialogFromEnter();
		_newNamespaceInput.TextSubmitted += _ => ConfirmRefactorNamespaceDialogFromEnter();

		_dock.AddChild(_systemNameInput);
		_dock.AddChild(_scriptFilterInput);
		_dock.AddChild(_tree);
		_dock.AddChild(_focusReleaseTarget);
		_dock.AddChild(_fileDialog);
		_dock.AddChild(_relinkScriptDialog);
		_dock.AddChild(_linkSceneDialog);
		_dock.AddChild(_addSceneDialog);
		_dock.AddChild(_relinkSceneDialog);
		_dock.AddChild(_contextMenu);
		_dock.AddChild(_removeDialog);
		_dock.AddChild(_missingScriptDialog);
		_dock.AddChild(_missingSceneDialog);
		_dock.AddChild(_renameDialog);
		_dock.AddChild(_addFolderDialog);
		_dock.AddChild(_createScriptDialog);
		_dock.AddChild(_refactorNamespaceDialog);
		_dock.AddChild(_csharpierInstalledDialog);
		_dock.AddChild(_csharpierInstallResultDialog);
		_dock.AddChild(_csharpierNotInstalledDialog);
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
