#if TOOLS
using Godot;

public partial class SystemExplorerPlugin
{
	#region Global Input Shortcuts
	public override void _Input(InputEvent inputEvent)
	{
		HandleGlobalLockShortcut(inputEvent);
		HandleGlobalBeautifyShortcut(inputEvent);

		if (inputEvent is not InputEventKey keyEvent)
			return;

		if (!keyEvent.Pressed || keyEvent.Echo)
			return;

		if (keyEvent.Keycode != Key.Delete)
			return;

		if (!keyEvent.CtrlPressed)
			return;

		if (_isFilteringScripts)
			return;

		OpenRemoveDialogForSelectedItem();

		GetViewport().SetInputAsHandled();
	}

	private void HandleGlobalLockShortcut(InputEvent inputEvent)
	{
		if (inputEvent is not InputEventKey keyEvent)
			return;

		if (!keyEvent.Pressed || keyEvent.Echo || !IsCtrlLockCommand(keyEvent))
			return;

		if (_tree == null || _tree.GetSelected() == null)
			return;

		Control focusedControl = GetTree()?.Root?.GuiGetFocusOwner();

		if (IsTextInputFocused(focusedControl))
			return;

		ToggleSelectedItemLock();
		GetViewport().SetInputAsHandled();
	}

	private void HandleGlobalBeautifyShortcut(InputEvent inputEvent)
	{
		if (inputEvent is not InputEventKey keyEvent)
			return;

		if (!keyEvent.Pressed || keyEvent.Echo || !IsCtrlBeautifyCommand(keyEvent))
			return;

		if (_tree == null || _tree.GetSelected() == null)
			return;

		Control focusedControl = GetTree()?.Root?.GuiGetFocusOwner();

		if (IsTextInputFocused(focusedControl))
			return;

		if (TryHandleBeautifyShortcutForSelectedItem())
			GetViewport().SetInputAsHandled();
	}

	private static bool IsTextInputFocused(Control focusedControl)
	{
		return focusedControl is LineEdit || focusedControl is TextEdit;
	}

	private static bool IsCtrlLockCommand(InputEventKey keyEvent)
	{
		return keyEvent.CtrlPressed
			&& !keyEvent.ShiftPressed
			&& !keyEvent.AltPressed
			&& (keyEvent.Keycode == Key.L || keyEvent.PhysicalKeycode == Key.L);
	}

	private static bool IsCtrlBeautifyCommand(InputEventKey keyEvent)
	{
		return keyEvent.CtrlPressed
			&& !keyEvent.ShiftPressed
			&& !keyEvent.AltPressed
			&& (keyEvent.Keycode == Key.B || keyEvent.PhysicalKeycode == Key.B);
	}

	private static bool IsCtrlShiftCollapseCommand(InputEventKey keyEvent)
	{
		bool isCtrlKey = keyEvent.Keycode == Key.Ctrl || keyEvent.PhysicalKeycode == Key.Ctrl;
		bool isShiftKey = keyEvent.Keycode == Key.Shift || keyEvent.PhysicalKeycode == Key.Shift;

		return (isCtrlKey && keyEvent.ShiftPressed) || (isShiftKey && keyEvent.CtrlPressed);
	}

	private bool TryHandleBeautifyShortcutForSelectedItem()
	{
		if (!EnableQuickActions || _tree == null)
			return false;

		TreeItem selectedItem = _tree.GetSelected();

		if (selectedItem == null)
			return false;

		string metadata = selectedItem.GetMetadata(0).AsString();

		if (string.IsNullOrWhiteSpace(metadata))
			return false;

		bool isScriptTarget = metadata.StartsWith("script::");
		bool isBatchTarget =
			!_isFilteringScripts
			&& (metadata.StartsWith("system::") || metadata.StartsWith("folder::"));

		if (!isScriptTarget && !isBatchTarget)
			return false;

		_pendingBeautifyScriptMetadata = metadata;

		if (isScriptTarget)
			OpenBeautifyScriptCSharpierCheckDialog();
		else
			OpenBeautifyScriptsCSharpierCheckDialog();

		return true;
	}

	private void OpenRemoveDialogForSelectedItem()
	{
		TreeItem selectedItem = _tree.GetSelected();

		if (selectedItem == null)
			return;

		string metadata = selectedItem.GetMetadata(0).AsString();

		if (string.IsNullOrWhiteSpace(metadata))
			return;

		_pendingRemoveMetadata = metadata;

		OpenRemoveDialog();
	}
	#endregion
}
#endif
