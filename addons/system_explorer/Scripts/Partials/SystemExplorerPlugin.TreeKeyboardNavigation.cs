#if TOOLS
using Godot;

public partial class SystemExplorerPlugin
{
	#region Tree Keyboard Navigation
	private enum TreeKeyboardNavigationCommand
	{
		None,
		Up,
		Down,
		Left,
		Right,
	}

	private Key? _pendingKeyboardControlTransitionKey;

	private bool HandleGlobalDockAndFilteredTreeKeyboardInput(
		InputEvent inputEvent
	)
	{
		if (inputEvent is not InputEventKey keyEvent)
			return false;

		bool isTransitionContinuationEcho =
			TryTakeKeyboardControlTransitionEcho(keyEvent);

		Control focusedControl = GetTree()?.Root?.GuiGetFocusOwner();
		bool isSystemNameInputFocused = IsExactFocusedControl(
			focusedControl,
			_systemNameInput
		);
		bool isScriptFilterInputFocused = IsExactFocusedControl(
			focusedControl,
			_scriptFilterInput
		);
		bool isHiddenTreeContextFocused = IsSystemExplorerFocusReleaseTarget(
			focusedControl
		);

		if (
			!isSystemNameInputFocused
			&& !isScriptFilterInputFocused
			&& !isHiddenTreeContextFocused
		)
		{
			return false;
		}

		bool handled = isSystemNameInputFocused
			? TryHandleSystemNameKeyboardNavigation(keyEvent)
			: isScriptFilterInputFocused
				? TryHandleScriptFilterKeyboardNavigation(
					keyEvent,
					isTransitionContinuationEcho
				)
				: TryHandleActiveScriptFilterTreeEscape(keyEvent);

		if (!handled)
			return false;

		GetViewport().SetInputAsHandled();
		return true;
	}

	private bool HandleGlobalTreeKeyboardNavigation(InputEvent inputEvent)
	{
		if (inputEvent is not InputEventKey keyEvent)
			return false;

		Control focusedControl = GetTree()?.Root?.GuiGetFocusOwner();

		if (!IsSystemExplorerFocusReleaseTarget(focusedControl))
			return false;

		if (!TryApplyTreeKeyboardNavigation(keyEvent))
			return false;

		GetViewport().SetInputAsHandled();
		return true;
	}

	private static bool IsExactFocusedControl(
		Control focusedControl,
		Control expectedControl
	)
	{
		return focusedControl != null
			&& expectedControl != null
			&& GodotObject.IsInstanceValid(expectedControl)
			&& expectedControl.IsInsideTree()
			&& focusedControl == expectedControl;
	}

	private bool TryHandleSystemNameKeyboardNavigation(
		InputEventKey keyEvent
	)
	{
		if (!IsUnmodifiedPressedKeyEvent(keyEvent))
			return false;

		switch (keyEvent.Keycode)
		{
			case Key.Escape:
				ClearSystemNameInputForKeyboardNavigation();
				return true;

			case Key.Up:
				return true;

			case Key.Down:
				if (keyEvent.Echo)
					return true;

				if (TryActivateLineEditForKeyboardNavigation(_scriptFilterInput))
					RegisterKeyboardControlTransition(Key.Down);

				return true;

			default:
				return false;
		}
	}

	private bool TryHandleScriptFilterKeyboardNavigation(
		InputEventKey keyEvent,
		bool isTransitionContinuationEcho
	)
	{
		if (!IsUnmodifiedPressedKeyEvent(keyEvent))
			return false;

		switch (keyEvent.Keycode)
		{
			case Key.Escape:
				ClearScriptFilterInput();
				TryActivateLineEditForKeyboardNavigation(_scriptFilterInput);
				return true;

			case Key.Up:
				if (keyEvent.Echo && !isTransitionContinuationEcho)
					return true;

				if (TryActivateLineEditForKeyboardNavigation(_systemNameInput))
					RegisterKeyboardControlTransition(Key.Up);

				return true;

			case Key.Down:
				if (keyEvent.Echo && !isTransitionContinuationEcho)
					return true;

				return TryEnterTreeContextFromScriptFilterInput();

			default:
				return false;
		}
	}


	private bool TryEnterTreeContextFromScriptFilterInput()
	{
		if (!TryGetFirstVisibleTreeItem(out TreeItem firstVisibleItem))
			return true;

		TreeItem selectedItem = _tree.GetSelected();

		if (!IsVisibleTreeItem(selectedItem))
			SelectTreeItemFromKeyboardNavigation(firstVisibleItem);

		if (TryFocusSystemExplorerHiddenTreeContext(revealDock: false))
			RegisterKeyboardControlTransition(Key.Down);

		return true;
	}

	private bool TryHandleActiveScriptFilterTreeEscape(
		InputEventKey keyEvent
	)
	{
		return IsUnmodifiedPressedKeyEvent(keyEvent)
			&& keyEvent.Keycode == Key.Escape
			&& TryClearActiveScriptFilterFromTreeKeyboardContext();
	}

	private static bool IsUnmodifiedPressedKeyEvent(InputEventKey keyEvent)
	{
		return keyEvent != null
			&& keyEvent.Pressed
			&& !keyEvent.CtrlPressed
			&& !keyEvent.ShiftPressed
			&& !keyEvent.AltPressed
			&& !keyEvent.MetaPressed;
	}

	private bool TryActivateLineEditForKeyboardNavigation(LineEdit lineEdit)
	{
		if (
			lineEdit == null
			|| !GodotObject.IsInstanceValid(lineEdit)
			|| lineEdit.IsQueuedForDeletion()
			|| !lineEdit.IsInsideTree()
		)
		{
			return false;
		}

		lineEdit.GrabFocus(true);
		lineEdit.Edit(true);

		Control focusedControl = GetTree()?.Root?.GuiGetFocusOwner();

		return IsExactFocusedControl(focusedControl, lineEdit)
			&& lineEdit.IsEditing();
	}

	private void RegisterKeyboardControlTransition(Key key)
	{
		_pendingKeyboardControlTransitionKey = key;
	}

	private bool TryTakeKeyboardControlTransitionEcho(
		InputEventKey keyEvent
	)
	{
		if (
			keyEvent == null
			|| !_pendingKeyboardControlTransitionKey.HasValue
			|| keyEvent.Keycode != _pendingKeyboardControlTransitionKey.Value
		)
		{
			return false;
		}

		if (!keyEvent.Pressed)
		{
			_pendingKeyboardControlTransitionKey = null;
			return false;
		}

		if (!keyEvent.Echo)
		{
			// A fresh press means the previous release was not observed by this
			// plugin route. Clear the stale transition and continue normally.
			_pendingKeyboardControlTransitionKey = null;
			return false;
		}

		_pendingKeyboardControlTransitionKey = null;
		return true;
	}

	private bool IsSystemExplorerFocusReleaseTarget(Control focusedControl)
	{
		return focusedControl != null
			&& _focusReleaseTarget != null
			&& GodotObject.IsInstanceValid(_focusReleaseTarget)
			&& _focusReleaseTarget.IsInsideTree()
			&& focusedControl == _focusReleaseTarget;
	}

	private static bool TryGetTreeKeyboardNavigationCommand(
		InputEventKey keyEvent,
		out TreeKeyboardNavigationCommand command
	)
	{
		command = TreeKeyboardNavigationCommand.None;

		if (!IsUnmodifiedPressedKeyEvent(keyEvent))
			return false;

		command = keyEvent.Keycode switch
		{
			Key.Up => TreeKeyboardNavigationCommand.Up,
			Key.Down => TreeKeyboardNavigationCommand.Down,
			Key.Left => TreeKeyboardNavigationCommand.Left,
			Key.Right => TreeKeyboardNavigationCommand.Right,
			_ => TreeKeyboardNavigationCommand.None,
		};

		return command != TreeKeyboardNavigationCommand.None;
	}

	private bool TryApplyTreeKeyboardNavigation(InputEventKey keyEvent)
	{
		if (
			!TryGetTreeKeyboardNavigationCommand(
				keyEvent,
				out TreeKeyboardNavigationCommand command
			)
		)
		{
			return false;
		}

		if (
			_tree == null
			|| !GodotObject.IsInstanceValid(_tree)
			|| !_tree.IsInsideTree()
		)
		{
			return false;
		}

		ApplyTreeKeyboardNavigation(command);
		return true;
	}

	private void ApplyTreeKeyboardNavigation(
		TreeKeyboardNavigationCommand command
	)
	{
		TreeItem selectedItem = _tree.GetSelected();

		if (selectedItem == null)
		{
			ApplyTreeKeyboardNavigationWithoutSelection(command);
			return;
		}

		switch (command)
		{
			case TreeKeyboardNavigationCommand.Up:
				ApplyTreeKeyboardNavigationUp(selectedItem);
				break;

			case TreeKeyboardNavigationCommand.Down:
				SelectTreeItemFromKeyboardNavigation(
					selectedItem.GetNextVisible(false)
				);
				break;

			case TreeKeyboardNavigationCommand.Left:
				ApplyTreeKeyboardNavigationLeft(selectedItem);
				break;

			case TreeKeyboardNavigationCommand.Right:
				ApplyTreeKeyboardNavigationRight(selectedItem);
				break;
		}
	}

	private void ApplyTreeKeyboardNavigationUp(TreeItem selectedItem)
	{
		TreeItem previousVisibleItem = selectedItem.GetPrevVisible(false);

		if (previousVisibleItem != null)
		{
			SelectTreeItemFromKeyboardNavigation(previousVisibleItem);
			return;
		}

		if (!TryActivateLineEditForKeyboardNavigation(_scriptFilterInput))
			return;

		_tree.DeselectAll();
		UpdateTreeLockIconVisibility();
		ClearPersistentTreeSelectionForKeyboardNavigation();
		RegisterKeyboardControlTransition(Key.Up);
	}

	private void ApplyTreeKeyboardNavigationWithoutSelection(
		TreeKeyboardNavigationCommand command
	)
	{
		if (!TryGetFirstVisibleTreeItem(out TreeItem firstVisibleItem))
			return;

		if (command == TreeKeyboardNavigationCommand.Down)
		{
			SelectTreeItemFromKeyboardNavigation(firstVisibleItem);
			return;
		}

		if (command != TreeKeyboardNavigationCommand.Up)
			return;

		TreeItem lastVisibleItem = firstVisibleItem;
		TreeItem nextVisibleItem = lastVisibleItem.GetNextVisible(false);

		while (nextVisibleItem != null)
		{
			lastVisibleItem = nextVisibleItem;
			nextVisibleItem = lastVisibleItem.GetNextVisible(false);
		}

		SelectTreeItemFromKeyboardNavigation(lastVisibleItem);
	}

	private bool TryGetFirstVisibleTreeItem(out TreeItem firstVisibleItem)
	{
		firstVisibleItem = null;

		if (
			_tree == null
			|| !GodotObject.IsInstanceValid(_tree)
			|| _tree.IsQueuedForDeletion()
			|| !_tree.IsInsideTree()
		)
		{
			return false;
		}

		firstVisibleItem = _tree.GetRoot()?.GetFirstChild();
		return firstVisibleItem != null
			&& GodotObject.IsInstanceValid(firstVisibleItem);
	}

	private bool IsVisibleTreeItem(TreeItem item)
	{
		if (
			item == null
			|| !GodotObject.IsInstanceValid(item)
			|| !TryGetFirstVisibleTreeItem(out TreeItem current)
		)
		{
			return false;
		}

		while (current != null)
		{
			if (current == item)
				return true;

			current = current.GetNextVisible(false);
		}

		return false;
	}

	private void ApplyTreeKeyboardNavigationLeft(TreeItem selectedItem)
	{
		TreeItem firstChild = selectedItem.GetFirstChild();

		if (firstChild != null && !selectedItem.Collapsed)
		{
			selectedItem.Collapsed = true;
			return;
		}

		SelectTreeItemFromKeyboardNavigation(
			selectedItem.GetPrevVisible(false)
		);
	}

	private void ApplyTreeKeyboardNavigationRight(TreeItem selectedItem)
	{
		TreeItem firstChild = selectedItem.GetFirstChild();

		if (firstChild == null)
		{
			SelectTreeItemFromKeyboardNavigation(
				selectedItem.GetNextVisible(false)
			);
			return;
		}

		if (selectedItem.Collapsed)
		{
			selectedItem.Collapsed = false;
			return;
		}

		SelectTreeItemFromKeyboardNavigation(firstChild);
	}

	private void SelectTreeItemFromKeyboardNavigation(TreeItem item)
	{
		if (
			item == null
			|| !GodotObject.IsInstanceValid(item)
			|| _tree == null
			|| !GodotObject.IsInstanceValid(_tree)
		)
		{
			return;
		}

		item.Select(0);
		_tree.ScrollToItem(item);
	}
	#endregion
}
#endif
