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

	private void ConfigureRenameNameConflictDialog(RenameConflictItemType itemType)
	{
		if (
			_renameNameConflictDialog == null
			|| !GodotObject.IsInstanceValid(_renameNameConflictDialog)
		)
		{
			return;
		}

		switch (itemType)
		{
			case RenameConflictItemType.System:
				_renameNameConflictDialog.Title = "System Already Exists";
				_renameNameConflictDialog.DialogText = "A system with this name already exists.";
				break;
			case RenameConflictItemType.Folder:
				_renameNameConflictDialog.Title = "Folder Already Exists";
				_renameNameConflictDialog.DialogText = "A folder with this name already exists.";
				break;
			case RenameConflictItemType.Script:
				_renameNameConflictDialog.Title = "Script Already Exists";
				_renameNameConflictDialog.DialogText = "A script with this name already exists.";
				break;
			case RenameConflictItemType.Scene:
				_renameNameConflictDialog.Title = "Scene Already Exists";
				_renameNameConflictDialog.DialogText = "A scene with this name already exists.";
				break;
		}
	}

	private void ShowRenameNameConflictWarning()
	{
		if (
			_isRenameNameConflictPopupPending
			|| _renameNameConflictDialog == null
			|| !GodotObject.IsInstanceValid(_renameNameConflictDialog)
			|| _renameNameConflictDialog.Visible
		)
			return;

		_isRenameNameConflictPopupPending = true;
		CallDeferred(nameof(PopupRenameNameConflictWarningDeferred));
	}

	private void PopupRenameNameConflictWarningDeferred()
	{
		_isRenameNameConflictPopupPending = false;

		if (
			_renameDialog == null
			|| !GodotObject.IsInstanceValid(_renameDialog)
			|| !_renameDialog.Visible
			|| _renameNameConflictDialog == null
			|| !GodotObject.IsInstanceValid(_renameNameConflictDialog)
			|| _renameNameConflictDialog.Visible
		)
			return;

		_renameNameConflictDialog.PopupCentered();
	}

	private void ShowAddFolderConflictWarning()
	{
		if (
			_isAddFolderConflictPopupPending
			|| _addFolderConflictDialog == null
			|| !GodotObject.IsInstanceValid(_addFolderConflictDialog)
			|| _addFolderConflictDialog.Visible
		)
			return;

		_isAddFolderConflictPopupPending = true;
		CallDeferred(nameof(PopupAddFolderConflictWarningDeferred));
	}

	private void PopupAddFolderConflictWarningDeferred()
	{
		_isAddFolderConflictPopupPending = false;

		if (
			_addFolderDialog == null
			|| !GodotObject.IsInstanceValid(_addFolderDialog)
			|| !_addFolderDialog.Visible
			|| _addFolderConflictDialog == null
			|| !GodotObject.IsInstanceValid(_addFolderConflictDialog)
			|| _addFolderConflictDialog.Visible
		)
			return;

		_addFolderConflictDialog.PopupCentered();
	}

	private void ShowAddSystemConflictWarning()
	{
		if (
			_isAddSystemConflictPopupPending
			|| _addSystemConflictDialog == null
			|| !GodotObject.IsInstanceValid(_addSystemConflictDialog)
			|| _addSystemConflictDialog.Visible
		)
			return;

		_isAddSystemConflictPopupPending = true;
		CallDeferred(nameof(PopupAddSystemConflictWarningDeferred));
	}

	private void PopupAddSystemConflictWarningDeferred()
	{
		_isAddSystemConflictPopupPending = false;

		if (
			_dock == null
			|| !GodotObject.IsInstanceValid(_dock)
			|| !_dock.IsInsideTree()
			|| _addSystemConflictDialog == null
			|| !GodotObject.IsInstanceValid(_addSystemConflictDialog)
			|| _addSystemConflictDialog.Visible
		)
			return;

		_addSystemConflictDialog.PopupCentered();
	}

	private void OnRenameNameConflictDialogClosed()
	{
		CallDeferred(nameof(RestoreRenameInputFocusDeferred));
	}

	private void RestoreRenameInputFocusDeferred()
	{
		RestoreDialogInputFocus(_renameDialog, _renameInput);
	}

	private void OnAddFolderConflictDialogClosed()
	{
		CallDeferred(nameof(RestoreAddFolderInputFocusDeferred));
	}

	private void RestoreAddFolderInputFocusDeferred()
	{
		RestoreDialogInputFocus(_addFolderDialog, _addFolderInput);
	}

	private void OnAddSystemConflictDialogClosed()
	{
		CallDeferred(nameof(RestoreSystemNameInputFocusDeferred));
	}

	private void RestoreSystemNameInputFocusDeferred()
	{
		if (
			_dock == null
			|| !GodotObject.IsInstanceValid(_dock)
			|| !_dock.IsInsideTree()
			|| _systemNameInput == null
			|| !GodotObject.IsInstanceValid(_systemNameInput)
		)
			return;

		_systemNameInput.Edit(true);
		_systemNameInput.CaretColumn = 0;
	}

	private static void RestoreDialogInputFocus(AcceptDialog parentDialog, LineEdit input)
	{
		if (
			parentDialog == null
			|| !GodotObject.IsInstanceValid(parentDialog)
			|| !parentDialog.Visible
			|| input == null
			|| !GodotObject.IsInstanceValid(input)
		)
			return;

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
