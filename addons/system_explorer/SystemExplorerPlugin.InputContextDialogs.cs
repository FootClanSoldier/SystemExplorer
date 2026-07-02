#if TOOLS
using Godot;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

public partial class SystemExplorerPlugin
{
	#region Global Input Shortcuts
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

	private static bool IsTextInputFocused(Control focusedControl)
	{
		return focusedControl is LineEdit || focusedControl is TextEdit;
	}

	#endregion

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
					_selectedScriptEntryFromFilter = GetEntryFromMetadata(item.GetMetadata(0).AsString());
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
					_selectedScriptEntryFromFilter = GetEntryFromMetadata(item.GetMetadata(0).AsString());

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

	private static bool IsCtrlLockCommand(InputEventKey keyEvent)
	{
		return keyEvent.CtrlPressed
			&& !keyEvent.ShiftPressed
			&& !keyEvent.AltPressed
			&& (keyEvent.Keycode == Key.L || keyEvent.PhysicalKeycode == Key.L);
	}

	#endregion

	#region Context Menu
	private void OpenContextMenuForMetadata(string metadata)
	{
		if (string.IsNullOrWhiteSpace(metadata))
			return;

		_pendingRemoveMetadata = metadata;
		_pendingRenameMetadata = metadata;
		_pendingAddFolderMetadata = metadata;
		_pendingShowInFileManagerMetadata = metadata;

		BuildContextMenuForMetadata(metadata);

		_contextMenu.Position = DisplayServer.MouseGetPosition();
		_contextMenu.Popup();
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

	private void BuildContextMenuForMetadata(string metadata)
	{
		_contextMenu.Clear();

		bool isSystem = metadata.StartsWith("system::");
		bool isFolder = metadata.StartsWith("folder::");
		bool isScript = metadata.StartsWith("script::");
		bool isScene = metadata.StartsWith("sceneLink::");

		if (isSystem || isFolder)
		{
			AddContextMenuIconItem("New Folder", ContextAddFolder, _contextFolderIcon);
		}

		if (!_isFilteringScripts)
		{
			AddContextMenuIconItem("New Script", ContextNewScript, _contextNewScriptIcon);
			AddContextMenuIconItem("Add Script", ContextAddScript, _contextAddScriptIcon);
			AddContextMenuIconItem("Add Scene", ContextAddScene, _sceneIcon);
		}

		if (isScript)
		{
			if (!_isFilteringScripts)
				_contextMenu.AddSeparator();

			string entry = GetEntryFromMetadata(metadata);

			if (string.IsNullOrWhiteSpace(GetLinkedScenePathFromEntry(entry)))
			{
				AddContextMenuIconItem("Link to Scene", ContextLinkScene, _contextLinkSceneIcon);
			}
			else
			{
				AddContextMenuIconItem("Unlink from Scene", ContextUnlinkScene, _contextUnlinkSceneIcon);
			}
		}

		_contextMenu.AddSeparator();
		AddContextMenuIconItem("Rename", ContextRename, _contextRenameIcon);
		AddContextMenuIconItem("Remove", ContextRemove, _contextRemoveIcon);

		if (isScript || isScene)
		{
			_contextMenu.AddSeparator();
			AddContextMenuIconItem("Open File Path", ContextShowInFileManager, _contextShowInFileSystemIcon);
		}
	}

	private void SetContextMenuItemDisabled(int id, bool disabled)
	{
		int index = _contextMenu.GetItemIndex(id);

		if (index < 0)
			return;

		_contextMenu.SetItemDisabled(index, disabled);
	}

	private void OnContextMenuIdPressed(long id)
	{
		switch (id)
		{
			case ContextAddFolder:
				OpenAddFolderDialog();
				break;

			case ContextAddScript:
				OnAddScriptPressed();
				break;

			case ContextAddScene:
				OnAddScenePressed();
				break;

			case ContextNewScript:
				_createScriptDialog.CurrentFile = "";
				_createScriptDialog.PopupCenteredRatio(0.8f);
				break;

			case ContextRename:
				OpenRenameDialog();
				break;

			case ContextRemove:
				OpenRemoveDialog();
				break;

			case ContextLinkScene:
				OpenLinkSceneDialog();
				break;

			case ContextUnlinkScene:
				UnlinkSceneFromPendingScript();
				break;

			case ContextShowInFileManager:
				ShowPendingScriptInFileManager();
				break;
		}
	}

	private void ShowPendingScriptInFileManager()
	{
		if (string.IsNullOrWhiteSpace(_pendingShowInFileManagerMetadata))
			return;

		string path = "";
		string missingEntry = "";

		if (_pendingShowInFileManagerMetadata.StartsWith("script::"))
		{
			string entry = GetEntryFromMetadata(_pendingShowInFileManagerMetadata);
			path = GetScriptPathFromEntry(entry);
			missingEntry = entry;
		}
		else if (_pendingShowInFileManagerMetadata.StartsWith("sceneLink::"))
		{
			string entry = _pendingShowInFileManagerMetadata.Substring("sceneLink::".Length);
			path = GetScenePathFromEntry(entry);
			missingEntry = entry;
		}
		else
		{
			return;
		}

		if (!FileAccess.FileExists(path))
		{
			if (_pendingShowInFileManagerMetadata.StartsWith("sceneLink::"))
				OpenMissingSceneDialog(missingEntry, path);
			else
				OpenMissingScriptDialog(missingEntry, path);

			return;
		}

		string globalPath = ProjectSettings.GlobalizePath(path);

		if (string.IsNullOrWhiteSpace(globalPath))
		{
			GD.PushWarning($"Could not resolve file path: {path}");
			return;
		}

		OS.ShellShowInFileManager(globalPath, false);
	}

	#endregion

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
			_removeFromFilesystemCheckBox.Text = "Also delete scripts from FileSystem";
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

	private static bool IsEnterPressed(InputEvent inputEvent)
	{
		return inputEvent is InputEventKey keyEvent
			&& keyEvent.Pressed
			&& !keyEvent.Echo
			&& (keyEvent.Keycode == Key.Enter || keyEvent.Keycode == Key.KpEnter);
	}

	#endregion

	#region Global Input Routing
	public override void _Input(InputEvent inputEvent)
	{
		HandleGlobalLockShortcut(inputEvent);

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

	#endregion

	#region Keyboard Shortcut Actions
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
