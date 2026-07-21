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

    private void ShowRenameFolderConflictWarning()
    {
        if (
            _isRenameFolderConflictPopupPending
            || _renameFolderConflictDialog == null
            || !GodotObject.IsInstanceValid(_renameFolderConflictDialog)
            || _renameFolderConflictDialog.Visible
        )
            return;

        _isRenameFolderConflictPopupPending = true;
        CallDeferred(nameof(PopupRenameFolderConflictWarningDeferred));
    }

    private void PopupRenameFolderConflictWarningDeferred()
    {
        _isRenameFolderConflictPopupPending = false;

        if (
            _renameDialog == null
            || !GodotObject.IsInstanceValid(_renameDialog)
            || !_renameDialog.Visible
            || _renameFolderConflictDialog == null
            || !GodotObject.IsInstanceValid(_renameFolderConflictDialog)
            || _renameFolderConflictDialog.Visible
        )
            return;

        _renameFolderConflictDialog.PopupCentered();
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

    private void OnRenameFolderConflictDialogClosed()
    {
        CallDeferred(nameof(RestoreRenameFolderInputFocusDeferred));
    }

    private void RestoreRenameFolderInputFocusDeferred()
    {
        RestoreFolderDialogInputFocus(_renameDialog, _renameInput);
    }

    private void OnAddFolderConflictDialogClosed()
    {
        CallDeferred(nameof(RestoreAddFolderInputFocusDeferred));
    }

    private void RestoreAddFolderInputFocusDeferred()
    {
        RestoreFolderDialogInputFocus(_addFolderDialog, _addFolderInput);
    }

    private static void RestoreFolderDialogInputFocus(AcceptDialog parentDialog, LineEdit input)
    {
        if (
            parentDialog == null
            || !GodotObject.IsInstanceValid(parentDialog)
            || !parentDialog.Visible
            || input == null
            || !GodotObject.IsInstanceValid(input)
        )
            return;

        input.GrabFocus();
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
