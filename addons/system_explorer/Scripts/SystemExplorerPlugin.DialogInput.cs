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
			_removeFromFilesystemCheckBox.Text = "Also delete files from FileSystem";
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

	private void OnRenameDialogWindowInput(InputEvent inputEvent)
	{
		if (!IsEnterPressed(inputEvent))
			return;

		ConfirmRenameDialogFromEnter();
	}

	private void ConfirmRenameDialogFromEnter()
	{
		if (_renameDialog == null || !_renameDialog.Visible)
			return;

		_renameDialog.Hide();
		OnRenameConfirmed();
	}

	private void OnAddFolderDialogWindowInput(InputEvent inputEvent)
	{
		if (!IsEnterPressed(inputEvent))
			return;

		ConfirmAddFolderDialogFromEnter();
	}

	private void ConfirmAddFolderDialogFromEnter()
	{
		if (_addFolderDialog == null || !_addFolderDialog.Visible)
			return;

		_addFolderDialog.Hide();
		OnAddFolderConfirmed();
	}

	private void OnRefactorNamespaceDialogWindowInput(InputEvent inputEvent)
	{
		if (!IsEnterPressed(inputEvent))
			return;

		ConfirmRefactorNamespaceDialogFromEnter();
	}

	private void ConfirmRefactorNamespaceDialogFromEnter()
	{
		if (_refactorNamespaceDialog == null || !_refactorNamespaceDialog.Visible)
			return;

		_refactorNamespaceDialog.Hide();
		OnRefactorNamespaceConfirmed();
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
