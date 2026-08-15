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

	private enum TreeKeyboardNavigationPersistenceKeyState
	{
		None = 0,
		Up = 1 << 0,
		Down = 1 << 1,
		Left = 1 << 2,
		Right = 1 << 3,
	}

	private Key? _pendingKeyboardControlTransitionKey;
	private TreeKeyboardNavigationPersistenceKeyState
		_heldTreeKeyboardNavigationPersistenceKeys;
	private bool _treeKeyboardNavigationScriptActivationPending;
	private int _treeKeyboardNavigationSuppressedScriptActivationCount;
	private bool _deferredTreeKeyboardNavigationScriptActivationPending;
	private bool _deferredTreeKeyboardNavigationScriptActivationQueued;
	private long _deferredTreeKeyboardNavigationScriptActivationOperationToken;
	private int _deferredTreeKeyboardNavigationSuppressedScriptActivationCount;

	private bool IsTreeKeyboardNavigationBurstActive =>
		_heldTreeKeyboardNavigationPersistenceKeys
		!= TreeKeyboardNavigationPersistenceKeyState.None;

	private bool IsTreeKeyboardNavigationPersistenceDeferred =>
		IsTreeKeyboardNavigationBurstActive;

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

		RegisterTreeKeyboardNavigationPersistenceKey(keyEvent);

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

				return TryEnterTreeContextFromScriptFilterInput(keyEvent);

			default:
				return false;
		}
	}


	private bool TryEnterTreeContextFromScriptFilterInput(
		InputEventKey keyEvent
	)
	{
		if (!TryGetFirstVisibleTreeItem(out TreeItem firstVisibleItem))
			return true;

		TreeItem selectedItem = _tree.GetSelected();

		if (!IsVisibleTreeItem(selectedItem))
		{
			RegisterTreeKeyboardNavigationPersistenceKey(keyEvent);
			SelectTreeItemFromKeyboardNavigation(firstVisibleItem);
		}

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

		RegisterTreeKeyboardNavigationPersistenceKey(keyEvent);
		ApplyTreeKeyboardNavigation(command);
		return true;
	}

	private void RegisterTreeKeyboardNavigationPersistenceKey(
		InputEventKey keyEvent
	)
	{
		if (
			!IsUnmodifiedPressedKeyEvent(keyEvent)
			|| !TryGetTreeKeyboardNavigationPersistenceKeyState(
				keyEvent.Keycode,
				out TreeKeyboardNavigationPersistenceKeyState keyState
			)
		)
		{
			return;
		}

		_heldTreeKeyboardNavigationPersistenceKeys |= keyState;
	}

	private void ObserveTreeKeyboardNavigationPersistenceRelease(
		InputEvent inputEvent
	)
	{
		if (
			inputEvent is not InputEventKey keyEvent
			|| keyEvent.Pressed
			|| !TryGetTreeKeyboardNavigationPersistenceKeyState(
				keyEvent.Keycode,
				out TreeKeyboardNavigationPersistenceKeyState keyState
			)
			|| (_heldTreeKeyboardNavigationPersistenceKeys & keyState)
				== TreeKeyboardNavigationPersistenceKeyState.None
		)
		{
			return;
		}

		_heldTreeKeyboardNavigationPersistenceKeys &= ~keyState;

		if (IsTreeKeyboardNavigationBurstActive)
			return;

		if (_treeStateSaveDirty)
			QueuePersistentTreeStateSave();

		FlushPendingAutocompleteScriptChangeAfterTreeKeyboardNavigation();
		FinalizePendingTreeKeyboardNavigationScriptActivation();
	}

	private static bool TryGetTreeKeyboardNavigationPersistenceKeyState(
		Key key,
		out TreeKeyboardNavigationPersistenceKeyState keyState
	)
	{
		keyState = key switch
		{
			Key.Up => TreeKeyboardNavigationPersistenceKeyState.Up,
			Key.Down => TreeKeyboardNavigationPersistenceKeyState.Down,
			Key.Left => TreeKeyboardNavigationPersistenceKeyState.Left,
			Key.Right => TreeKeyboardNavigationPersistenceKeyState.Right,
			_ => TreeKeyboardNavigationPersistenceKeyState.None,
		};

		return keyState != TreeKeyboardNavigationPersistenceKeyState.None;
	}

	private void ResetTreeKeyboardNavigationPersistenceDeferral(string reason)
	{
		_heldTreeKeyboardNavigationPersistenceKeys =
			TreeKeyboardNavigationPersistenceKeyState.None;
		ClearPendingTreeKeyboardNavigationScriptActivation();
		InvalidateDeferredTreeKeyboardNavigationScriptActivation(
			reason,
			forceOperationTokenInvalidation: true
		);
	}

	private bool TryCoalesceTreeKeyboardNavigationScriptActivation(
		TreeItem selectedItem
	)
	{
		if (
			!IsTreeKeyboardNavigationBurstActive
			|| selectedItem == null
			|| !GodotObject.IsInstanceValid(selectedItem)
		)
		{
			return false;
		}

		string metadata = selectedItem.GetMetadata(0).AsString();

		if (!metadata.StartsWith("script::", System.StringComparison.Ordinal))
			return false;

		if (!_treeKeyboardNavigationScriptActivationPending)
		{
			InvalidateDeferredTreeKeyboardNavigationScriptActivation(
				"SupersededByNewKeyboardBurst"
			);
			_treeKeyboardNavigationScriptActivationPending = true;
			_treeKeyboardNavigationSuppressedScriptActivationCount = 1;
			ClearPendingSystemExplorerScriptActivation();

			DebugLogger.LogOperation(
				"Tree keyboard navigation script activation coalescing started",
				$"SuppressedCount='{_treeKeyboardNavigationSuppressedScriptActivationCount}', ManagedAssemblyGeneration='{ManagedAssemblyGeneration}', HostInstanceToken='{_autocompleteHostInstanceToken}'"
			);
		}
		else
		{
			_treeKeyboardNavigationSuppressedScriptActivationCount++;
		}

		return true;
	}

	private void FinalizePendingTreeKeyboardNavigationScriptActivation()
	{
		if (!_treeKeyboardNavigationScriptActivationPending)
			return;

		int suppressedCount = _treeKeyboardNavigationSuppressedScriptActivationCount;
		ClearPendingTreeKeyboardNavigationScriptActivation();
		QueueDeferredTreeKeyboardNavigationScriptActivation(suppressedCount);
	}

	private void QueueDeferredTreeKeyboardNavigationScriptActivation(
		int suppressedCount
	)
	{
		long operationToken =
			AdvanceDeferredTreeKeyboardNavigationScriptActivationToken();
		string scheduledManagedAssemblyGeneration = ManagedAssemblyGeneration;

		_deferredTreeKeyboardNavigationScriptActivationPending = true;
		_deferredTreeKeyboardNavigationScriptActivationQueued = true;
		_deferredTreeKeyboardNavigationSuppressedScriptActivationCount =
			suppressedCount;

		DebugLogger.LogOperation(
			"Tree keyboard navigation final script activation deferred",
			$"OperationToken='{operationToken}', SuppressedCount='{suppressedCount}', ScheduledManagedAssemblyGeneration='{scheduledManagedAssemblyGeneration}', TreeKeyboardNavigationBurstActive='{IsTreeKeyboardNavigationBurstActive}'"
		);

		try
		{
			CallDeferred(
				nameof(ApplyDeferredTreeKeyboardNavigationScriptActivation),
				operationToken,
				scheduledManagedAssemblyGeneration
			);
		}
		catch (System.Exception exception)
		{
			if (
				operationToken
					== _deferredTreeKeyboardNavigationScriptActivationOperationToken
				&& _deferredTreeKeyboardNavigationScriptActivationPending
			)
			{
				_deferredTreeKeyboardNavigationScriptActivationPending = false;
				_deferredTreeKeyboardNavigationScriptActivationQueued = false;
				_deferredTreeKeyboardNavigationSuppressedScriptActivationCount = 0;
				AdvanceDeferredTreeKeyboardNavigationScriptActivationToken();
			}

			DebugLogger.LogOperation(
				"Tree keyboard navigation deferred script activation scheduling failed",
				$"OperationToken='{operationToken}', ScheduledManagedAssemblyGeneration='{scheduledManagedAssemblyGeneration}', CurrentManagedAssemblyGeneration='{ManagedAssemblyGeneration}', CurrentOperationToken='{_deferredTreeKeyboardNavigationScriptActivationOperationToken}', Exception='{exception}'"
			);
		}
	}

	private void ApplyDeferredTreeKeyboardNavigationScriptActivation(
		long operationToken,
		string scheduledManagedAssemblyGeneration
	)
	{
		if (
			!string.Equals(
				scheduledManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				System.StringComparison.Ordinal
			)
		)
		{
			DebugLogger.LogPersistentFileOnlyOperation(
				"Tree keyboard navigation deferred script activation rejected",
				$"Reason='StaleManagedAssemblyGeneration', ScheduledManagedAssemblyGeneration='{scheduledManagedAssemblyGeneration ?? ""}', CurrentManagedAssemblyGeneration='{ManagedAssemblyGeneration}', ScheduledOperationToken='{operationToken}', CurrentOperationToken='{_deferredTreeKeyboardNavigationScriptActivationOperationToken}'"
			);
			return;
		}

		if (
			operationToken
			!= _deferredTreeKeyboardNavigationScriptActivationOperationToken
		)
		{
			DebugLogger.LogPersistentFileOnlyOperation(
				"Tree keyboard navigation deferred script activation rejected",
				$"Reason='StaleOperationToken', ScheduledManagedAssemblyGeneration='{scheduledManagedAssemblyGeneration ?? ""}', CurrentManagedAssemblyGeneration='{ManagedAssemblyGeneration}', ScheduledOperationToken='{operationToken}', CurrentOperationToken='{_deferredTreeKeyboardNavigationScriptActivationOperationToken}'"
			);
			return;
		}

		if (
			!_deferredTreeKeyboardNavigationScriptActivationPending
			|| !_deferredTreeKeyboardNavigationScriptActivationQueued
		)
		{
			DebugLogger.LogPersistentFileOnlyOperation(
				"Tree keyboard navigation deferred script activation rejected",
				$"Reason='OperationNoLongerPending', OperationToken='{operationToken}', ScheduledManagedAssemblyGeneration='{scheduledManagedAssemblyGeneration}', CurrentManagedAssemblyGeneration='{ManagedAssemblyGeneration}', Pending='{_deferredTreeKeyboardNavigationScriptActivationPending}', Queued='{_deferredTreeKeyboardNavigationScriptActivationQueued}'"
			);
			return;
		}

		_deferredTreeKeyboardNavigationScriptActivationQueued = false;

		if (_editorOperationShutdownStarted)
		{
			RejectCurrentDeferredTreeKeyboardNavigationScriptActivation(
				operationToken,
				"ShutdownInProgress"
			);
			return;
		}

		if (_isRecoveringManagedAssemblyState)
		{
			RejectCurrentDeferredTreeKeyboardNavigationScriptActivation(
				operationToken,
				"ManagedRecoveryInProgress"
			);
			return;
		}

		if (
			!HasVerifiedPersistentTreeStateForCurrentAssembly
			|| !GodotObject.IsInstanceValid(this)
			|| !IsInsideTree()
		)
		{
			RejectCurrentDeferredTreeKeyboardNavigationScriptActivation(
				operationToken,
				"PluginBoundaryUnavailable"
			);
			return;
		}

		if (IsTreeKeyboardNavigationBurstActive)
		{
			RejectCurrentDeferredTreeKeyboardNavigationScriptActivation(
				operationToken,
				"SupersededByNewKeyboardBurst",
				invalidateOperationToken: true
			);
			return;
		}

		if (
			_tree == null
			|| !GodotObject.IsInstanceValid(_tree)
			|| _tree.IsQueuedForDeletion()
			|| !_tree.IsInsideTree()
		)
		{
			RejectCurrentDeferredTreeKeyboardNavigationScriptActivation(
				operationToken,
				"TreeUnavailable"
			);
			return;
		}

		TreeItem selectedItem = _tree.GetSelected();
		string finalSelectionMetadata =
			selectedItem != null && GodotObject.IsInstanceValid(selectedItem)
				? selectedItem.GetMetadata(0).AsString()
				: "";
		bool finalSelectionIsScript = finalSelectionMetadata.StartsWith(
			"script::",
			System.StringComparison.Ordinal
		);
		int suppressedCount =
			_deferredTreeKeyboardNavigationSuppressedScriptActivationCount;

		DebugLogger.LogOperation(
			"Tree keyboard navigation deferred script activation executing",
			$"OperationToken='{operationToken}', SuppressedCount='{suppressedCount}', FinalSelectionMetadata='{finalSelectionMetadata}', FinalSelectionIsScript='{finalSelectionIsScript}', ScheduledManagedAssemblyGeneration='{scheduledManagedAssemblyGeneration}', CurrentManagedAssemblyGeneration='{ManagedAssemblyGeneration}', TreeKeyboardNavigationBurstActive='{IsTreeKeyboardNavigationBurstActive}'"
		);

		ConsumeCurrentDeferredTreeKeyboardNavigationScriptActivation(operationToken);

		if (!finalSelectionIsScript)
		{
			DebugLogger.LogOperation(
				"Tree keyboard navigation deferred script activation rejected",
				$"Reason='FinalSelectionNotScript', OperationToken='{operationToken}', FinalSelectionMetadata='{finalSelectionMetadata}', ScheduledManagedAssemblyGeneration='{scheduledManagedAssemblyGeneration}', CurrentManagedAssemblyGeneration='{ManagedAssemblyGeneration}'"
			);
			return;
		}

		OpenScriptFromTreeItem(selectedItem);
	}

	private long AdvanceDeferredTreeKeyboardNavigationScriptActivationToken()
	{
		unchecked
		{
			_deferredTreeKeyboardNavigationScriptActivationOperationToken++;
			if (_deferredTreeKeyboardNavigationScriptActivationOperationToken <= 0)
				_deferredTreeKeyboardNavigationScriptActivationOperationToken = 1;
		}

		return _deferredTreeKeyboardNavigationScriptActivationOperationToken;
	}

	private void ConsumeCurrentDeferredTreeKeyboardNavigationScriptActivation(
		long operationToken
	)
	{
		if (
			operationToken
			!= _deferredTreeKeyboardNavigationScriptActivationOperationToken
		)
		{
			return;
		}

		_deferredTreeKeyboardNavigationScriptActivationPending = false;
		_deferredTreeKeyboardNavigationScriptActivationQueued = false;
		_deferredTreeKeyboardNavigationSuppressedScriptActivationCount = 0;
	}

	private void RejectCurrentDeferredTreeKeyboardNavigationScriptActivation(
		long operationToken,
		string reason,
		bool invalidateOperationToken = false
	)
	{
		if (
			operationToken
			!= _deferredTreeKeyboardNavigationScriptActivationOperationToken
			|| !_deferredTreeKeyboardNavigationScriptActivationPending
		)
		{
			return;
		}

		int suppressedCount =
			_deferredTreeKeyboardNavigationSuppressedScriptActivationCount;
		ConsumeCurrentDeferredTreeKeyboardNavigationScriptActivation(operationToken);

		if (invalidateOperationToken)
			AdvanceDeferredTreeKeyboardNavigationScriptActivationToken();

		DebugLogger.LogOperation(
			"Tree keyboard navigation deferred script activation rejected",
			$"Reason='{reason}', OperationToken='{operationToken}', CurrentOperationToken='{_deferredTreeKeyboardNavigationScriptActivationOperationToken}', SuppressedCount='{suppressedCount}', ManagedAssemblyGeneration='{ManagedAssemblyGeneration}', TreeKeyboardNavigationBurstActive='{IsTreeKeyboardNavigationBurstActive}'"
		);
	}

	private bool InvalidateDeferredTreeKeyboardNavigationScriptActivation(
		string reason,
		bool forceOperationTokenInvalidation = false
	)
	{
		bool hadPendingOperation =
			_deferredTreeKeyboardNavigationScriptActivationPending
			|| _deferredTreeKeyboardNavigationScriptActivationQueued;

		if (!hadPendingOperation && !forceOperationTokenInvalidation)
			return false;

		long invalidatedOperationToken =
			_deferredTreeKeyboardNavigationScriptActivationOperationToken;
		int suppressedCount =
			_deferredTreeKeyboardNavigationSuppressedScriptActivationCount;

		_deferredTreeKeyboardNavigationScriptActivationPending = false;
		_deferredTreeKeyboardNavigationScriptActivationQueued = false;
		_deferredTreeKeyboardNavigationSuppressedScriptActivationCount = 0;
		long currentOperationToken =
			AdvanceDeferredTreeKeyboardNavigationScriptActivationToken();

		if (hadPendingOperation)
		{
			DebugLogger.LogOperation(
				"Tree keyboard navigation deferred script activation invalidated",
				$"Reason='{reason}', InvalidatedOperationToken='{invalidatedOperationToken}', CurrentOperationToken='{currentOperationToken}', SuppressedCount='{suppressedCount}', ManagedAssemblyGeneration='{ManagedAssemblyGeneration}', TreeKeyboardNavigationBurstActive='{IsTreeKeyboardNavigationBurstActive}'"
			);
		}

		return hadPendingOperation;
	}

	private void InvalidateDeferredTreeKeyboardNavigationScriptActivationForNonBurstTakeover()
	{
		if (IsTreeKeyboardNavigationBurstActive)
			return;

		InvalidateDeferredTreeKeyboardNavigationScriptActivation(
			"SupersededByNonBurstSelection"
		);
	}

	private void ClearPendingTreeKeyboardNavigationScriptActivation()
	{
		_treeKeyboardNavigationScriptActivationPending = false;
		_treeKeyboardNavigationSuppressedScriptActivationCount = 0;
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
