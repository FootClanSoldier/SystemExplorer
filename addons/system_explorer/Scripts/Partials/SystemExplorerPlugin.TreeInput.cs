#if TOOLS
using Godot;

public partial class SystemExplorerPlugin
{
	#region Tree Input and Keyboard Handling
	private void OnTreeGuiInput(InputEvent inputEvent)
	{
		if (inputEvent is InputEventKey keyEvent)
		{
			HandleTreeKeyboardInput(keyEvent);
			return;
		}

		if (inputEvent is InputEventMouseMotion)
		{
			UpdateHoveredTreeItemLockVisibility();
			UpdateDragDropTargetHighlight();
			return;
		}

		if (inputEvent is not InputEventMouseButton mouseButton)
			return;

		Vector2 mousePosition = _tree.GetLocalMousePosition();
		TreeItem item = _tree.GetItemAtPosition(mousePosition);

		if (mouseButton.ButtonIndex == MouseButton.Middle)
		{
			if (!mouseButton.Pressed || item == null)
				return;

			ToggleItemLock(item, selectToggledItemAfterBuild: false);
			_tree.AcceptEvent();
			return;
		}

		if (mouseButton.ButtonIndex == MouseButton.Left)
		{
			if (_isFilteringScripts)
			{
				ClearDragState();

				if (mouseButton.Pressed && mouseButton.DoubleClick && IsScriptOrSceneItem(item))
				{
					item.Select(0);
					_selectedScriptEntryFromFilter = GetEntryFromMetadata(
						item.GetMetadata(0).AsString()
					);
					_ignoreNextScriptFilterReleaseOpen = true;

					if (IsSceneItem(item))
						OpenSceneFromTreeItem(item);
					else
						OpenLinkedSceneFromTreeItem(item);

					_tree.AcceptEvent();
					return;
				}

				if (!mouseButton.Pressed && IsScriptOrSceneItem(item))
				{
					item.Select(0);
					_selectedScriptEntryFromFilter = GetEntryFromMetadata(
						item.GetMetadata(0).AsString()
					);

					if (_ignoreNextScriptFilterReleaseOpen)
					{
						_ignoreNextScriptFilterReleaseOpen = false;
						_tree.AcceptEvent();
						return;
					}

					if (IsSceneItem(item))
						OpenSceneFromTreeItem(item);
					else
						OpenScriptFromTreeItem(item);

					_tree.AcceptEvent();
				}

				if (!mouseButton.Pressed)
					_ignoreNextScriptFilterReleaseOpen = false;

				return;
			}

			if (mouseButton.Pressed && mouseButton.DoubleClick)
			{
				if (IsScriptItem(item))
				{
					OpenLinkedSceneFromTreeItem(item);
					_tree.AcceptEvent();
					return;
				}

				if (ToggleExpandedIfSystemOrFolder(item))
				{
					ClearDragState();
					_tree.AcceptEvent();
					return;
				}
			}

			if (IsShiftPressed(mouseButton))
			{
				ClearDragState();

				if (mouseButton.Pressed)
					ToggleExpandedIfSystemOrFolder(item);

				_tree.AcceptEvent();
				return;
			}

			if (mouseButton.Pressed)
			{
				ClearDragDropTargetHighlight();
				_draggedMetadata = item?.GetMetadata(0).AsString() ?? "";
				_draggedSourceSystemName = item == null ? "" : GetSystemNameFromTreeItem(item);
				_draggedSourceFolderPath = item == null ? "" : GetFolderPathFromTreeItem(item);
				_leftMousePressPosition = mousePosition;
				_leftMousePressedMetadata = _draggedMetadata;
				_leftMousePressedOnSelectedScript = IsSelectedScriptOrSceneItem(item);
			}
			else
			{
				if (string.IsNullOrWhiteSpace(_draggedMetadata) || item == null)
				{
					ClearDragState();
					return;
				}

				string releaseMetadata = item.GetMetadata(0).AsString();
				bool isClick =
					_leftMousePressedOnSelectedScript
					&& _leftMousePressedMetadata == releaseMetadata
					&& _leftMousePressPosition.DistanceTo(mousePosition) <= ClickOpenDragThreshold;

				if (isClick)
				{
					OpenScriptFromTreeItem(item);
					OpenSceneFromTreeItem(item);
					ClearDragState();
					return;
				}

				ClearDragDropTargetHighlight();
				MoveDraggedItem(_draggedMetadata, item);

				ClearDragState();
			}

			return;
		}

		if (!mouseButton.Pressed || mouseButton.ButtonIndex != MouseButton.Right)
			return;

		if (item == null)
			return;

		if (_isFilteringScripts)
		{
			if (!IsScriptOrSceneItem(item))
				return;

			item.Select(0);

			string filteredScriptMetadata = item.GetMetadata(0).AsString();
			_selectedScriptEntryFromFilter = GetEntryFromMetadata(filteredScriptMetadata);
			OpenContextMenuForMetadata(filteredScriptMetadata);
			_tree.AcceptEvent();
			return;
		}

		item.Select(0);

		string rightClickMetadata = item.GetMetadata(0).AsString();
		OpenContextMenuForMetadata(rightClickMetadata);
	}

	private void OnTreeMouseExited()
	{
		ClearDragDropTargetHighlight();

		if (string.IsNullOrWhiteSpace(_hoveredTreeItemMetadata))
			return;

		_hoveredTreeItemMetadata = "";
		UpdateTreeLockIconVisibility();
	}

	private void UpdateHoveredTreeItemLockVisibility()
	{
		if (_tree == null)
			return;

		TreeItem hoveredItem = _tree.GetItemAtPosition(_tree.GetLocalMousePosition());
		string hoveredMetadata = hoveredItem?.GetMetadata(0).AsString() ?? "";

		if (_hoveredTreeItemMetadata == hoveredMetadata)
			return;

		_hoveredTreeItemMetadata = hoveredMetadata;
		UpdateTreeLockIconVisibility();
	}

	private void HandleTreeKeyboardInput(InputEventKey keyEvent)
	{
		if (!keyEvent.Pressed || keyEvent.Echo)
			return;

		if (IsCtrlLockCommand(keyEvent))
		{
			ToggleSelectedItemLock();
			_tree.AcceptEvent();
			return;
		}

		if (IsCtrlBeautifyCommand(keyEvent))
		{
			if (TryHandleBeautifyShortcutForSelectedItem())
				_tree.AcceptEvent();

			return;
		}

		if (!IsCtrlShiftCollapseCommand(keyEvent))
			return;

		bool hadSystemsLoaded = _systems.Count > 0;

		if (!EnsureSystemsLoadedForTreeOperation("Collapse Entire Tree"))
			return;

		if (!hadSystemsLoaded)
			BuildTree(keepCurrentExpansionState: true);

		CollapseEntireTree();
		_tree.AcceptEvent();
	}

	private static bool IsShiftPressed(InputEventMouseButton mouseButton)
	{
		return mouseButton.ShiftPressed || Input.IsKeyPressed(Key.Shift);
	}

	private static bool ToggleExpandedIfSystemOrFolder(TreeItem item)
	{
		if (item == null)
			return false;

		string metadata = item.GetMetadata(0).AsString();

		if (!metadata.StartsWith("system::") && !metadata.StartsWith("folder::"))
			return false;

		item.Collapsed = !item.Collapsed;
		return true;
	}

	private bool IsSelectedScriptOrSceneItem(TreeItem item)
	{
		if (item == null)
			return false;

		if (_tree.GetSelected() != item)
			return false;

		string metadata = item.GetMetadata(0).AsString();

		return IsScriptOrSceneMetadata(metadata);
	}

	private static bool IsScriptItem(TreeItem item)
	{
		if (item == null)
			return false;

		string metadata = item.GetMetadata(0).AsString();
		return metadata.StartsWith("script::");
	}

	private static bool IsScriptOrSceneItem(TreeItem item)
	{
		return IsScriptItem(item) || IsSceneItem(item);
	}

	private static bool IsSceneItem(TreeItem item)
	{
		if (item == null)
			return false;

		string metadata = item.GetMetadata(0).AsString();
		return metadata.StartsWith("sceneLink::");
	}
	#endregion
}
#endif
