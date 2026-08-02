#if TOOLS
using Godot;

public partial class SystemExplorerPlugin
{
	#region Dialog Input Helpers
	private void OpenRemoveDialog()
	{
		if (string.IsNullOrWhiteSpace(_pendingRemoveMetadata))
			return;

		_removeFromFilesystemCheckBox.ButtonPressed = false;
		_removeFromFilesystemCheckBox.Disabled = false;
		_removeFromFilesystemCheckBox.Text = "Also delete files from FileSystem";

		if (_pendingRemoveMetadata.StartsWith("system::"))
		{
			_removeDialog.Title = "Remove System";
			_removeDialog.DialogText = "Remove selected system from System Explorer?";
			_removeFromFilesystemCheckBox.Text = "Also delete files from FileSystem";
			DisablePhysicalRemoveCheckBoxForVerifiedEmptyTarget();
		}
		else if (_pendingRemoveMetadata.StartsWith("folder::"))
		{
			_removeDialog.Title = "Remove Folder";
			_removeDialog.DialogText = "Remove selected folder from System Explorer?";
			_removeFromFilesystemCheckBox.Text = "Also delete files from FileSystem";
			DisablePhysicalRemoveCheckBoxForVerifiedEmptyTarget();
		}
		else if (_pendingRemoveMetadata.StartsWith("script::"))
		{
			_removeDialog.Title = "Remove Script";
			_removeDialog.DialogText = "Remove selected script from System Explorer?";
			_removeFromFilesystemCheckBox.Text = "Also delete script from FileSystem";
		}
		else if (_pendingRemoveMetadata.StartsWith("sceneLink::"))
		{
			_removeDialog.Title = "Remove Scene";
			_removeDialog.DialogText = "Remove selected scene from System Explorer?";
			_removeFromFilesystemCheckBox.Text = "Also delete scene from FileSystem";
		}
		else
		{
				DebugLogger.LogOperation(
		"Open Remove Dialog cancelled: unidentified remove target",
		_pendingRemoveMetadata
	);
	return;
		}

		_removeDialog.PopupCentered();
		CallDeferred(nameof(ReleaseRemoveDialogFocus));
	}

	private void DisablePhysicalRemoveCheckBoxForVerifiedEmptyTarget()
	{
		PhysicalRemoveTargetInspection inspection = InspectPhysicalRemoveTarget(
			_pendingRemoveMetadata
		);

		if (
			inspection.Status
			!= PhysicalRemoveTargetStatus.ValidWithoutPhysicalFiles
		)
		{
			return;
		}

		_removeFromFilesystemCheckBox.ButtonPressed = false;
		_removeFromFilesystemCheckBox.Disabled = true;
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

	private void OnRemoveDialogWindowInput(InputEvent inputEvent)
	{
		if (!IsEnterPressed(inputEvent))
			return;

		ConfirmRemoveDialogFromEnter();
	}

	private void ConfirmRemoveDialogFromEnter()
	{
		if (_removeDialog == null || !_removeDialog.Visible)
			return;

		_removeDialog.Hide();
		OnRemoveConfirmed();
	}

	private void ShowRenameNameConflictWarning(RenameConflictItemType itemType)
	{
		switch (itemType)
		{
			case RenameConflictItemType.System:
				ShowRenameInputWarning(
					"System Already Exists",
					"A system with this name already exists."
				);
				break;
			case RenameConflictItemType.Folder:
				ShowRenameInputWarning(
					"Folder Already Exists",
					"A folder with this name already exists."
				);
				break;
			case RenameConflictItemType.Script:
				ShowRenameInputWarning(
					"Script Already Exists",
					"A script with this name already exists."
				);
				break;
			case RenameConflictItemType.Scene:
				ShowRenameInputWarning(
					"Scene Already Exists",
					"A scene with this name already exists."
				);
				break;
		}
	}

	private void ShowRenameInputWarning(string title, string message)
	{
		if (
			_isRenameInputWarningPopupPending
			|| !IsValidGodotObject(_renameDialog)
			|| !_renameDialog.Visible
			|| !IsValidGodotObject(_renameInputWarningDialog)
			|| _renameInputWarningDialog.Visible
		)
		{
			return;
		}

		ConfigureInputWarningDialog(_renameInputWarningDialog, title, message);
		_isRenameInputWarningPopupPending = true;
		CallDeferred(nameof(PopupRenameInputWarningDeferred));
	}

	private void PopupRenameInputWarningDeferred()
	{
		_isRenameInputWarningPopupPending = false;

		if (
			!IsValidGodotObject(_renameDialog)
			|| !_renameDialog.Visible
			|| !IsValidGodotObject(_renameInputWarningDialog)
			|| _renameInputWarningDialog.Visible
		)
		{
			return;
		}

		PopupWrappedAcceptDialogForCurrentContent(_renameInputWarningDialog);
	}

	private void ShowAddFolderInputWarning(string title, string message)
	{
		if (
			_isAddFolderInputWarningPopupPending
			|| !IsValidGodotObject(_addFolderDialog)
			|| !_addFolderDialog.Visible
			|| !IsValidGodotObject(_addFolderInputWarningDialog)
			|| _addFolderInputWarningDialog.Visible
		)
		{
			return;
		}

		ConfigureInputWarningDialog(_addFolderInputWarningDialog, title, message);
		_isAddFolderInputWarningPopupPending = true;
		CallDeferred(nameof(PopupAddFolderInputWarningDeferred));
	}

	private void PopupAddFolderInputWarningDeferred()
	{
		_isAddFolderInputWarningPopupPending = false;

		if (
			!IsValidGodotObject(_addFolderDialog)
			|| !_addFolderDialog.Visible
			|| !IsValidGodotObject(_addFolderInputWarningDialog)
			|| _addFolderInputWarningDialog.Visible
		)
		{
			return;
		}

		PopupWrappedAcceptDialogForCurrentContent(_addFolderInputWarningDialog);
	}

	private void ShowAddSystemInputWarning(string title, string message)
	{
		if (
			_isAddSystemInputWarningPopupPending
			|| !IsValidGodotObject(_dock)
			|| !_dock.IsInsideTree()
			|| !IsValidGodotObject(_addSystemInputWarningDialog)
			|| _addSystemInputWarningDialog.Visible
		)
		{
			return;
		}

		ConfigureInputWarningDialog(_addSystemInputWarningDialog, title, message);
		_isAddSystemInputWarningPopupPending = true;
		CallDeferred(nameof(PopupAddSystemInputWarningDeferred));
	}

	private void PopupAddSystemInputWarningDeferred()
	{
		_isAddSystemInputWarningPopupPending = false;

		if (
			!IsValidGodotObject(_dock)
			|| !_dock.IsInsideTree()
			|| !IsValidGodotObject(_addSystemInputWarningDialog)
			|| _addSystemInputWarningDialog.Visible
		)
		{
			return;
		}

		PopupWrappedAcceptDialogForCurrentContent(_addSystemInputWarningDialog);
	}

	private void ShowCreateScriptInputWarning(string title, string message)
	{
		if (
			_isCreateScriptInputWarningPopupPending
			|| !IsValidGodotObject(_dock)
			|| !_dock.IsInsideTree()
			|| !IsValidGodotObject(_createScriptInputWarningDialog)
			|| _createScriptInputWarningDialog.Visible
		)
		{
			return;
		}

		ConfigureInputWarningDialog(_createScriptInputWarningDialog, title, message);
		_isCreateScriptInputWarningPopupPending = true;
		CallDeferred(nameof(PopupCreateScriptInputWarningDeferred));
	}

	private void PopupCreateScriptInputWarningDeferred()
	{
		_isCreateScriptInputWarningPopupPending = false;

		if (
			!IsValidGodotObject(_dock)
			|| !_dock.IsInsideTree()
			|| !IsValidGodotObject(_createScriptInputWarningDialog)
			|| _createScriptInputWarningDialog.Visible
		)
		{
			return;
		}

		PopupWrappedAcceptDialogForCurrentContent(_createScriptInputWarningDialog);
	}

	private static void ConfigureInputWarningDialog(
		AcceptDialog dialog,
		string title,
		string message
	)
	{
		if (!IsValidGodotObject(dialog))
			return;

		dialog.Title = title;
		dialog.DialogText = message;
	}

	private void OnRenameInputWarningDialogClosed()
	{
		_isRenameInputWarningPopupPending = false;
		CallDeferred(nameof(RestoreRenameInputFocusDeferred));
	}

	private void RestoreRenameInputFocusDeferred()
	{
		RestoreDialogInputFocus(_renameDialog, _renameInput);
	}

	private void OnAddFolderInputWarningDialogClosed()
	{
		_isAddFolderInputWarningPopupPending = false;
		CallDeferred(nameof(RestoreAddFolderInputFocusDeferred));
	}

	private void RestoreAddFolderInputFocusDeferred()
	{
		RestoreDialogInputFocus(_addFolderDialog, _addFolderInput);
	}

	private void OnAddSystemInputWarningDialogClosed()
	{
		_isAddSystemInputWarningPopupPending = false;
		CallDeferred(nameof(RestoreSystemNameInputFocusDeferred));
	}

	private void RestoreSystemNameInputFocusDeferred()
	{
		if (
			!IsValidGodotObject(_dock)
			|| !_dock.IsInsideTree()
			|| !IsValidGodotObject(_systemNameInput)
		)
		{
			return;
		}

		_systemNameInput.Edit(true);
		_systemNameInput.CaretColumn = 0;
	}

	private void OnCreateScriptInputWarningDialogClosed()
	{
		_isCreateScriptInputWarningPopupPending = false;
		CallDeferred(nameof(ReopenCreateScriptDialogAfterInputWarningDeferred));
	}

	private void ReopenCreateScriptDialogAfterInputWarningDeferred()
	{
		if (!TryOpenCreateScriptDialogForSelectedItem())
			return;

		ClearCreateScriptFileNameInputBestEffort();
		CallDeferred(nameof(RestoreCreateScriptFileNameInputFocusDeferred));
	}

	private void ClearCreateScriptFileNameInputBestEffort()
	{
		if (!IsValidGodotObject(_createScriptDialog))
			return;

		_createScriptDialog.CurrentFile = "";
		LineEdit lineEdit = _createScriptDialog.GetLineEdit();

		if (IsValidGodotObject(lineEdit))
			lineEdit.Text = "";
	}

	private void RestoreCreateScriptFileNameInputFocusDeferred()
	{
		if (
			!IsValidGodotObject(_createScriptDialog)
			|| !_createScriptDialog.Visible
		)
		{
			return;
		}

		LineEdit lineEdit = _createScriptDialog.GetLineEdit();

		if (!IsValidGodotObject(lineEdit))
			return;

		lineEdit.Text = "";
		lineEdit.Edit(true);
		lineEdit.CaretColumn = 0;
	}

	private static void RestoreDialogInputFocus(AcceptDialog parentDialog, LineEdit input)
	{
		if (
			!IsValidGodotObject(parentDialog)
			|| !parentDialog.Visible
			|| !IsValidGodotObject(input)
		)
		{
			return;
		}

		input.Edit(true);
		input.CaretColumn = 0;
	}

	private static bool IsEnterPressed(InputEvent inputEvent)
	{
		return inputEvent is InputEventKey keyEvent
			&& keyEvent.Pressed
			&& !keyEvent.Echo
			&& (keyEvent.Keycode == Key.Enter || keyEvent.Keycode == Key.KpEnter);
	}
	#endregion
}
#endif
