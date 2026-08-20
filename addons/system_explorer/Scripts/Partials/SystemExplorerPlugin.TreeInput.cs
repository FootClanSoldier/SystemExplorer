#if TOOLS
using Godot;

public partial class SystemExplorerPlugin
{
	#region Tree Input and Keyboard Handling
	private enum TreeMouseScriptActivationOwner
	{
		None,
		ItemSelected,
		MouseRelease,
		RightPress,
	}

	private bool _treeMouseScriptClickIntentActive;
	private bool _treeMouseScriptClickPressObservedByTreeInput;
	private bool _treeMouseScriptClickReleaseObserved;
	private long _treeMouseScriptClickToken;
	private string _treeMouseScriptClickPressedMetadata = "";
	private MouseButton _treeMouseScriptClickButton;
	private TreeMouseScriptActivationOwner _treeMouseScriptClickOwner;
	private bool _treeMouseScriptClickFiltering;

	private void ObserveTreeMouseScriptPressBeforeTreeGuiInput(InputEvent inputEvent)
	{
		if (
			inputEvent is not InputEventMouseButton mouseButton
			|| !mouseButton.Pressed
			|| (
				mouseButton.ButtonIndex != MouseButton.Left
				&& mouseButton.ButtonIndex != MouseButton.Right
			)
		)
		{
			return;
		}

		// Every physical left/right press starts a new gesture boundary. Only an
		// ordinary script press inside the live Tree is eligible for pre-GUI
		// ownership state. Left keeps its existing double-click/Shift exclusions;
		// right press keeps the context-menu semantics independent of those rules.
		ResetTreeMouseScriptClickIntent();

		if (
			mouseButton.ButtonIndex == MouseButton.Left
			&& (mouseButton.DoubleClick || IsShiftPressed(mouseButton))
		)
		{
			return;
		}

		if (
			_tree == null
			|| !GodotObject.IsInstanceValid(_tree)
			|| _tree.IsQueuedForDeletion()
			|| !_tree.IsInsideTree()
			|| !_tree.IsVisibleInTree()
		)
		{
			return;
		}

		Vector2 mousePosition = _tree.GetLocalMousePosition();

		if (
			mousePosition.X < 0.0f
			|| mousePosition.Y < 0.0f
			|| mousePosition.X >= _tree.Size.X
			|| mousePosition.Y >= _tree.Size.Y
		)
		{
			return;
		}

		TreeItem item = _tree.GetItemAtPosition(mousePosition);

		if (item == null || !GodotObject.IsInstanceValid(item))
			return;

		string pressedMetadata = item.GetMetadata(0).AsString();

		if (!pressedMetadata.StartsWith("script::", System.StringComparison.Ordinal))
			return;

		StartTreeMouseScriptClickIntent(
			pressedMetadata,
			mouseButton.ButtonIndex,
			_isFilteringScripts,
			pressObservedByTreeInput: false,
			owner: TreeMouseScriptActivationOwner.None
		);
		LogTreeMouseScriptClickIntentStarted(
			"Tree mouse script click intent pre-captured",
			"GlobalInput"
		);
	}

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
			if (!mouseButton.Pressed)
				return;

			ResetTreeMouseScriptClickIntent();

			if (item == null)
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
					ResetTreeMouseScriptClickIntent();
					item.Select(0);
					_selectedScriptEntryFromFilter = GetEntryFromMetadata(
						item.GetMetadata(0).AsString()
					);
					_ignoreNextScriptFilterReleaseOpen = true;

					InvalidateDeferredTreeKeyboardNavigationScriptActivationForNonBurstTakeover();

					if (IsSceneItem(item))
						OpenSceneFromTreeItem(item);
					else
						OpenLinkedSceneFromTreeItem(item);

					_tree.AcceptEvent();
					return;
				}

				if (mouseButton.Pressed)
				{
					BeginOrAdoptTreeMouseScriptClickIntent(
						item,
						MouseButton.Left,
						filtering: true
					);
					return;
				}

				if (IsScriptOrSceneItem(item))
				{
					if (TrySuppressFilteredTreeScriptReleaseRetarget(item))
					{
						_tree.AcceptEvent();
						return;
					}

					item.Select(0);
					_selectedScriptEntryFromFilter = GetEntryFromMetadata(
						item.GetMetadata(0).AsString()
					);

					if (_ignoreNextScriptFilterReleaseOpen)
					{
						_ignoreNextScriptFilterReleaseOpen = false;
						ResetTreeMouseScriptClickIntent();
						_tree.AcceptEvent();
						return;
					}

					InvalidateDeferredTreeKeyboardNavigationScriptActivationForNonBurstTakeover();

					if (IsSceneItem(item))
					{
						ResetTreeMouseScriptClickIntent();
						OpenSceneFromTreeItem(item);
					}
					else if (TryClaimTreeMouseScriptActivationFromMouseRelease(item))
					{
						OpenScriptFromTreeItem(item);
					}

					_tree.AcceptEvent();
				}
				else
				{
					ResetTreeMouseScriptClickIntent();
				}

				_ignoreNextScriptFilterReleaseOpen = false;
				return;
			}

			if (mouseButton.Pressed && mouseButton.DoubleClick)
			{
				ResetTreeMouseScriptClickIntent();

				if (IsScriptItem(item))
				{
					InvalidateDeferredTreeKeyboardNavigationScriptActivationForNonBurstTakeover();
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
				ResetTreeMouseScriptClickIntent();
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
				BeginOrAdoptTreeMouseScriptClickIntent(
					item,
					MouseButton.Left,
					filtering: false
				);
			}
			else
			{
				if (string.IsNullOrWhiteSpace(_draggedMetadata) || item == null)
				{
					ResetTreeMouseScriptClickIntent();
					ClearDragState();
					return;
				}

				string releaseMetadata = item.GetMetadata(0).AsString();
				float dragDistance = _leftMousePressPosition.DistanceTo(mousePosition);
				bool releasedOnPressedItem = _leftMousePressedMetadata == releaseMetadata;

				if (dragDistance <= ClickOpenDragThreshold)
				{
					if (releasedOnPressedItem && IsScriptItem(item))
					{
						InvalidateDeferredTreeKeyboardNavigationScriptActivationForNonBurstTakeover();

						if (TryClaimTreeMouseScriptActivationFromMouseRelease(item))
							OpenScriptFromTreeItem(item);
					}
					else if (_leftMousePressedOnSelectedScript && releasedOnPressedItem)
					{
						ResetTreeMouseScriptClickIntent();
						InvalidateDeferredTreeKeyboardNavigationScriptActivationForNonBurstTakeover();
						OpenSceneFromTreeItem(item);
					}
					else if (!releasedOnPressedItem)
					{
						ResetTreeMouseScriptClickIntent();
					}

					ClearDragState();
					return;
				}

				ResetTreeMouseScriptClickIntent();
				ClearDragDropTargetHighlight();
				MoveDraggedItem(_draggedMetadata, item);

				ClearDragState();
			}

			return;
		}

		if (!mouseButton.Pressed || mouseButton.ButtonIndex != MouseButton.Right)
			return;

		if (item == null)
		{
			ResetTreeMouseScriptClickIntent();
			return;
		}

		if (_isFilteringScripts)
		{
			if (!IsScriptOrSceneItem(item))
			{
				ResetTreeMouseScriptClickIntent();
				return;
			}

			if (IsScriptItem(item))
			{
				BeginOrAdoptTreeMouseScriptClickIntent(
					item,
					MouseButton.Right,
					filtering: true
				);
			}
			else
			{
				ResetTreeMouseScriptClickIntent();
			}

			item.Select(0);

			string filteredScriptMetadata = item.GetMetadata(0).AsString();
			_selectedScriptEntryFromFilter = GetEntryFromMetadata(filteredScriptMetadata);

			if (
				IsScriptItem(item)
				&& TryClaimTreeMouseScriptActivationFromRightPress(item)
			)
			{
				InvalidateDeferredTreeKeyboardNavigationScriptActivationForNonBurstTakeover();
				OpenScriptFromTreeItem(item);
			}

			OpenContextMenuForTreeItem(item);
			_tree.AcceptEvent();
			return;
		}

		if (IsScriptItem(item))
		{
			BeginOrAdoptTreeMouseScriptClickIntent(
				item,
				MouseButton.Right,
				filtering: false
			);
		}
		else
		{
			ResetTreeMouseScriptClickIntent();
		}

		item.Select(0);

		if (
			IsScriptItem(item)
			&& TryClaimTreeMouseScriptActivationFromRightPress(item)
		)
		{
			InvalidateDeferredTreeKeyboardNavigationScriptActivationForNonBurstTakeover();
			OpenScriptFromTreeItem(item);
		}

		OpenContextMenuForTreeItem(item);
		_tree.AcceptEvent();
		return;
	}

	private bool TrySuppressFilteredTreeScriptReleaseRetarget(TreeItem releaseItem)
	{
		if (!_treeMouseScriptClickIntentActive || !_treeMouseScriptClickFiltering)
			return false;

		string releaseMetadata =
			releaseItem != null && GodotObject.IsInstanceValid(releaseItem)
				? releaseItem.GetMetadata(0).AsString()
				: "";

		if (
			string.Equals(
				_treeMouseScriptClickPressedMetadata,
				releaseMetadata,
				System.StringComparison.Ordinal
			)
		)
		{
			return false;
		}

		long clickToken = _treeMouseScriptClickToken;
		string pressedMetadata = _treeMouseScriptClickPressedMetadata;
		TreeMouseScriptActivationOwner existingOwner = _treeMouseScriptClickOwner;
		bool filtering = _treeMouseScriptClickFiltering;
		bool pressObservedByTreeInput = _treeMouseScriptClickPressObservedByTreeInput;

		DebugLogger.LogOperation(
			"Tree filtered mouse script release retarget suppressed",
			$"ClickToken='{clickToken}', PressedMetadata='{pressedMetadata}', ReleaseMetadata='{releaseMetadata}', ExistingOwner='{existingOwner}', Filtering='{filtering}', PressObservedByTreeInput='{pressObservedByTreeInput}'"
		);

		_ignoreNextScriptFilterReleaseOpen = false;
		ResetTreeMouseScriptClickIntent();
		return true;
	}

	private void BeginOrAdoptTreeMouseScriptClickIntent(
		TreeItem item,
		MouseButton button,
		bool filtering
	)
	{
		string pressedMetadata =
			item != null && GodotObject.IsInstanceValid(item)
				? item.GetMetadata(0).AsString()
				: "";

		if (!pressedMetadata.StartsWith("script::", System.StringComparison.Ordinal))
		{
			ResetTreeMouseScriptClickIntent();
			return;
		}

		if (
			_treeMouseScriptClickIntentActive
			&& !_treeMouseScriptClickPressObservedByTreeInput
			&& !_treeMouseScriptClickReleaseObserved
			&& _treeMouseScriptClickButton == button
			&& string.Equals(
				_treeMouseScriptClickPressedMetadata,
				pressedMetadata,
				System.StringComparison.Ordinal
			)
		)
		{
			_treeMouseScriptClickPressObservedByTreeInput = true;
			return;
		}

		StartTreeMouseScriptClickIntent(
			pressedMetadata,
			button,
			filtering,
			pressObservedByTreeInput: true,
			owner: TreeMouseScriptActivationOwner.None
		);
		LogTreeMouseScriptClickIntentStarted(
			"Tree mouse script click intent fallback started",
			"TreeGuiInput"
		);
	}

	private bool ShouldSuppressTreeMouseScriptActivationFromItemSelected(
		TreeItem selectedItem
	)
	{
		string selectedMetadata =
			selectedItem != null && GodotObject.IsInstanceValid(selectedItem)
				? selectedItem.GetMetadata(0).AsString()
				: "";
		bool selectedIsScript = selectedMetadata.StartsWith(
			"script::",
			System.StringComparison.Ordinal
		);

		if (!selectedIsScript)
		{
			ResetTreeMouseScriptClickIntent();
			return false;
		}

		if (_treeMouseScriptClickIntentActive)
		{
			if (
				string.Equals(
					_treeMouseScriptClickPressedMetadata,
					selectedMetadata,
					System.StringComparison.Ordinal
				)
			)
			{
				if (_treeMouseScriptClickOwner == TreeMouseScriptActivationOwner.None)
				{
					_treeMouseScriptClickOwner = TreeMouseScriptActivationOwner.ItemSelected;
					LogTreeMouseScriptActivationClaimed(
						TreeMouseScriptActivationOwner.ItemSelected
					);
					return false;
				}

				LogTreeMouseDuplicateScriptActivationSuppressed(
					_treeMouseScriptClickOwner,
					TreeMouseScriptActivationOwner.ItemSelected
				);
				return true;
			}

			ResetTreeMouseScriptClickIntent();
		}

		return false;
	}

	private bool TryClaimTreeMouseScriptActivationFromMouseRelease(TreeItem item)
	{
		string releaseMetadata =
			item != null && GodotObject.IsInstanceValid(item)
				? item.GetMetadata(0).AsString()
				: "";

		if (!releaseMetadata.StartsWith("script::", System.StringComparison.Ordinal))
		{
			ResetTreeMouseScriptClickIntent();
			return false;
		}

		if (
			!_treeMouseScriptClickIntentActive
			|| _treeMouseScriptClickButton != MouseButton.Left
			|| !string.Equals(
				_treeMouseScriptClickPressedMetadata,
				releaseMetadata,
				System.StringComparison.Ordinal
			)
		)
		{
			ResetTreeMouseScriptClickIntent();
			return false;
		}

		_treeMouseScriptClickReleaseObserved = true;

		if (_treeMouseScriptClickOwner == TreeMouseScriptActivationOwner.None)
		{
			_treeMouseScriptClickOwner = TreeMouseScriptActivationOwner.MouseRelease;
			LogTreeMouseScriptActivationClaimed(
				TreeMouseScriptActivationOwner.MouseRelease
			);
			return true;
		}

		LogTreeMouseDuplicateScriptActivationSuppressed(
			_treeMouseScriptClickOwner,
			TreeMouseScriptActivationOwner.MouseRelease
		);
		return false;
	}

	private bool TryClaimTreeMouseScriptActivationFromRightPress(TreeItem item)
	{
		string pressedMetadata =
			item != null && GodotObject.IsInstanceValid(item)
				? item.GetMetadata(0).AsString()
				: "";

		if (!pressedMetadata.StartsWith("script::", System.StringComparison.Ordinal))
		{
			ResetTreeMouseScriptClickIntent();
			return false;
		}

		if (
			!_treeMouseScriptClickIntentActive
			|| _treeMouseScriptClickButton != MouseButton.Right
			|| !string.Equals(
				_treeMouseScriptClickPressedMetadata,
				pressedMetadata,
				System.StringComparison.Ordinal
			)
		)
		{
			ResetTreeMouseScriptClickIntent();
			return false;
		}

		if (_treeMouseScriptClickOwner == TreeMouseScriptActivationOwner.None)
		{
			_treeMouseScriptClickOwner = TreeMouseScriptActivationOwner.RightPress;
			LogTreeMouseScriptActivationClaimed(
				TreeMouseScriptActivationOwner.RightPress
			);
			return true;
		}

		LogTreeMouseDuplicateScriptActivationSuppressed(
			_treeMouseScriptClickOwner,
			TreeMouseScriptActivationOwner.RightPress
		);
		return false;
	}

	private void StartTreeMouseScriptClickIntent(
		string pressedMetadata,
		MouseButton button,
		bool filtering,
		bool pressObservedByTreeInput,
		TreeMouseScriptActivationOwner owner
	)
	{
		AdvanceTreeMouseScriptClickToken();
		_treeMouseScriptClickIntentActive = true;
		_treeMouseScriptClickPressObservedByTreeInput = pressObservedByTreeInput;
		_treeMouseScriptClickReleaseObserved = false;
		_treeMouseScriptClickPressedMetadata = pressedMetadata ?? "";
		_treeMouseScriptClickButton = button;
		_treeMouseScriptClickOwner = owner;
		_treeMouseScriptClickFiltering = filtering;
	}

	private long AdvanceTreeMouseScriptClickToken()
	{
		unchecked
		{
			_treeMouseScriptClickToken++;

			if (_treeMouseScriptClickToken <= 0)
				_treeMouseScriptClickToken = 1;
		}

		return _treeMouseScriptClickToken;
	}

	private void ResetTreeMouseScriptClickIntent()
	{
		_treeMouseScriptClickIntentActive = false;
		_treeMouseScriptClickPressObservedByTreeInput = false;
		_treeMouseScriptClickReleaseObserved = false;
		_treeMouseScriptClickPressedMetadata = "";
		_treeMouseScriptClickButton = MouseButton.None;
		_treeMouseScriptClickOwner = TreeMouseScriptActivationOwner.None;
		_treeMouseScriptClickFiltering = false;
	}

	private void LogTreeMouseScriptClickIntentStarted(
		string operation,
		string source
	)
	{
		DebugLogger.LogOperation(
			operation,
			$"ClickToken='{_treeMouseScriptClickToken}', Source='{source}', Button='{_treeMouseScriptClickButton}', Metadata='{_treeMouseScriptClickPressedMetadata}', Filtering='{_treeMouseScriptClickFiltering}'"
		);
	}

	private void LogTreeMouseScriptActivationClaimed(
		TreeMouseScriptActivationOwner owner
	)
	{
		DebugLogger.LogOperation(
			"Tree mouse script activation claimed",
			$"ClickToken='{_treeMouseScriptClickToken}', Owner='{owner}', Button='{_treeMouseScriptClickButton}', Metadata='{_treeMouseScriptClickPressedMetadata}', Filtering='{_treeMouseScriptClickFiltering}'"
		);
	}

	private void LogTreeMouseDuplicateScriptActivationSuppressed(
		TreeMouseScriptActivationOwner existingOwner,
		TreeMouseScriptActivationOwner suppressedOwner
	)
	{
		DebugLogger.LogOperation(
			"Tree mouse duplicate script activation suppressed",
			$"ClickToken='{_treeMouseScriptClickToken}', ExistingOwner='{existingOwner}', SuppressedOwner='{suppressedOwner}', Button='{_treeMouseScriptClickButton}', Metadata='{_treeMouseScriptClickPressedMetadata}', Filtering='{_treeMouseScriptClickFiltering}'"
		);
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
		TryTakeKeyboardControlTransitionEcho(keyEvent);

		if (TryHandleActiveScriptFilterTreeEscape(keyEvent))
		{
			_tree.AcceptEvent();
			return;
		}

		if (TryApplyTreeKeyboardNavigation(keyEvent))
		{
			_tree.AcceptEvent();
			return;
		}

		TryDispatchTreeShortcut(
			keyEvent,
			TreeShortcutInputRoute.TreeGuiInput
		);
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
