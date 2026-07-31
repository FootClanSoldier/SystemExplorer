#if TOOLS
using Godot;

public partial class SystemExplorerPlugin
{
	#region Hidden-Focus Tree Keyboard Navigation
	private enum TreeKeyboardNavigationCommand
	{
		None,
		Up,
		Down,
		Left,
		Right,
	}

	private bool HandleGlobalTreeKeyboardNavigation(InputEvent inputEvent)
	{
		if (inputEvent is not InputEventKey keyEvent)
			return false;

		Control focusedControl = GetTree()?.Root?.GuiGetFocusOwner();

		if (!IsSystemExplorerFocusReleaseTarget(focusedControl))
			return false;

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

		ApplyHiddenFocusTreeKeyboardNavigation(command);
		GetViewport().SetInputAsHandled();
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

		if (
			keyEvent == null
			|| !keyEvent.Pressed
			|| keyEvent.CtrlPressed
			|| keyEvent.ShiftPressed
			|| keyEvent.AltPressed
			|| keyEvent.MetaPressed
		)
		{
			return false;
		}

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

	private void ApplyHiddenFocusTreeKeyboardNavigation(
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
				SelectTreeItemFromKeyboardNavigation(
					selectedItem.GetPrevVisible(false)
				);
				break;

			case TreeKeyboardNavigationCommand.Down:
				SelectTreeItemFromKeyboardNavigation(
					selectedItem.GetNextVisible(false)
				);
				break;

			case TreeKeyboardNavigationCommand.Left:
				ApplyHiddenFocusTreeKeyboardNavigationLeft(selectedItem);
				break;

			case TreeKeyboardNavigationCommand.Right:
				ApplyHiddenFocusTreeKeyboardNavigationRight(selectedItem);
				break;
		}
	}

	private void ApplyTreeKeyboardNavigationWithoutSelection(
		TreeKeyboardNavigationCommand command
	)
	{
		TreeItem firstVisibleItem = _tree.GetRoot()?.GetFirstChild();

		if (firstVisibleItem == null)
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

	private void ApplyHiddenFocusTreeKeyboardNavigationLeft(TreeItem selectedItem)
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

	private void ApplyHiddenFocusTreeKeyboardNavigationRight(TreeItem selectedItem)
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
