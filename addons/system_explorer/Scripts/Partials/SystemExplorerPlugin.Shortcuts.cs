#if TOOLS
using Godot;
using System;
using SystemExplorer.EditorIntegration.ScriptEditing;
using static SystemExplorer.QuickActions.Beautify.BeautifyEditorStateService;

public partial class SystemExplorerPlugin
{
	private readonly record struct FocusedScriptEditorBeautifyTarget(
		string ScriptPath,
		Script Script,
		ScriptEditorBase ScriptEditorBase,
		TextEdit TextEditor
	);

	private enum SystemExplorerToggleFocusReturnTarget
	{
		Tree,
		SystemName,
		FilterItems,
	}

	private SystemExplorerToggleFocusReturnTarget
		_systemExplorerToggleFocusReturnTarget =
			SystemExplorerToggleFocusReturnTarget.Tree;

	#region Editor Shortcuts and Global Input Routing
	private const string BeautifyEditorShortcutPath =
		"system_explorer/beautify";
	private const string BeautifyEditorShortcutDisplayName =
		"Beautify";
	private const string NewScriptEditorShortcutPath =
		"system_explorer/new_script";
	private const string NewScriptEditorShortcutDisplayName =
		"New Script";
	private const string RemoveSelectedItemEditorShortcutPath =
		"system_explorer/remove_selected_item";
	private const string RemoveSelectedItemEditorShortcutDisplayName =
		"Remove Selected Item";
	private const string ToggleTreeScriptEditorFocusShortcutPath =
		"system_explorer/toggle_tree_script_editor_focus";
	private const string ToggleTreeScriptEditorFocusShortcutDisplayName =
		"Toggle Tree / Script Editor Focus";
	private const string CollapseTreeEditorShortcutPath =
		"system_explorer/collapse_tree";
	private const string CollapseTreeEditorShortcutDisplayName =
		"Collapse Tree";
	private const string RenameSelectedItemEditorShortcutPath =
		"system_explorer/rename_selected_item";
	private const string RenameSelectedItemEditorShortcutDisplayName =
		"Rename";
	private const string NewFolderEditorShortcutPath =
		"system_explorer/new_folder";
	private const string NewFolderEditorShortcutDisplayName =
		"New Folder";
	private const string AddExistingScriptsEditorShortcutPath =
		"system_explorer/add_existing_scripts";
	private const string AddExistingScriptsEditorShortcutDisplayName =
		"Add Scripts";
	private const string AddExistingScenesEditorShortcutPath =
		"system_explorer/add_existing_scenes";
	private const string AddExistingScenesEditorShortcutDisplayName =
		"Add Scenes";
	private const string RefactorNamespaceEditorShortcutPath =
		"system_explorer/refactor_namespace";
	private const string RefactorNamespaceEditorShortcutDisplayName =
		"Refactor Namespace";

	private string _editorShortcutsRegisteredGeneration = "";

	private static Shortcut CreateEditorKeyShortcut(
		string displayName,
		Key keycode,
		bool ctrlPressed = false,
		bool shiftPressed = false,
		bool altPressed = false,
		bool metaPressed = false
	)
	{
		InputEventKey keyEvent = new()
		{
			Keycode = keycode,
			CtrlPressed = ctrlPressed,
			ShiftPressed = shiftPressed,
			AltPressed = altPressed,
			MetaPressed = metaPressed,
		};

		Godot.Collections.Array events = new()
		{
			keyEvent,
		};

		return new Shortcut
		{
			ResourceName = displayName,
			Events = events,
		};
	}

	private static Shortcut CreateEditorPhysicalKeyShortcut(
		string displayName,
		Key physicalKeycode,
		bool ctrlPressed = false,
		bool shiftPressed = false,
		bool altPressed = false,
		bool metaPressed = false
	)
	{
		InputEventKey keyEvent = new()
		{
			PhysicalKeycode = physicalKeycode,
			CtrlPressed = ctrlPressed,
			ShiftPressed = shiftPressed,
			AltPressed = altPressed,
			MetaPressed = metaPressed,
		};

		Godot.Collections.Array events = new()
		{
			keyEvent,
		};

		return new Shortcut
		{
			ResourceName = displayName,
			Events = events,
		};
	}

	private static bool EnsureEditorShortcutDisplayName(
		EditorSettings editorSettings,
		string shortcutPath,
		string displayName
	)
	{
		if (
			editorSettings == null
			|| string.IsNullOrWhiteSpace(shortcutPath)
			|| string.IsNullOrWhiteSpace(displayName)
		)
		{
			return false;
		}

		Shortcut shortcut = editorSettings.GetShortcut(shortcutPath);

		if (shortcut == null)
			return false;

		if (string.Equals(shortcut.ResourceName, displayName, StringComparison.Ordinal))
			return true;

		shortcut.ResourceName = displayName;

		return string.Equals(
			shortcut.ResourceName,
			displayName,
			StringComparison.Ordinal
		);
	}

	private bool EnsureEditorShortcutsRegistered()
	{
		try
		{
			EditorSettings editorSettings = EditorInterface.Singleton?.GetEditorSettings();

			if (editorSettings == null)
			{
				DebugLogger.LogOperation(
					"Editor shortcut registration unavailable",
					"EditorSettings was null."
				);
				return false;
			}

			bool beautifyExists = editorSettings.HasShortcut(BeautifyEditorShortcutPath);
			bool newScriptExists = editorSettings.HasShortcut(NewScriptEditorShortcutPath);
			bool removeExists = editorSettings.HasShortcut(
				RemoveSelectedItemEditorShortcutPath
			);
			bool toggleFocusExists = editorSettings.HasShortcut(
				ToggleTreeScriptEditorFocusShortcutPath
			);
			bool collapseTreeExists = editorSettings.HasShortcut(
				CollapseTreeEditorShortcutPath
			);
			bool renameSelectedItemExists = editorSettings.HasShortcut(
				RenameSelectedItemEditorShortcutPath
			);
			bool newFolderExists = editorSettings.HasShortcut(
				NewFolderEditorShortcutPath
			);
			bool addExistingScriptsExists = editorSettings.HasShortcut(
				AddExistingScriptsEditorShortcutPath
			);
			bool addExistingScenesExists = editorSettings.HasShortcut(
				AddExistingScenesEditorShortcutPath
			);
			bool refactorNamespaceExists = editorSettings.HasShortcut(
				RefactorNamespaceEditorShortcutPath
			);

			if (
				string.Equals(
					_editorShortcutsRegisteredGeneration,
					ManagedAssemblyGeneration,
					StringComparison.Ordinal
				)
				&& beautifyExists
				&& newScriptExists
				&& removeExists
				&& toggleFocusExists
				&& collapseTreeExists
				&& renameSelectedItemExists
				&& newFolderExists
				&& addExistingScriptsExists
				&& addExistingScenesExists
				&& refactorNamespaceExists
			)
			{
				bool beautifyDisplayNameReady = EnsureEditorShortcutDisplayName(
					editorSettings,
					BeautifyEditorShortcutPath,
					BeautifyEditorShortcutDisplayName
				);
				bool newScriptDisplayNameReady = EnsureEditorShortcutDisplayName(
					editorSettings,
					NewScriptEditorShortcutPath,
					NewScriptEditorShortcutDisplayName
				);
				bool removeDisplayNameReady = EnsureEditorShortcutDisplayName(
					editorSettings,
					RemoveSelectedItemEditorShortcutPath,
					RemoveSelectedItemEditorShortcutDisplayName
				);
				bool toggleFocusDisplayNameReady = EnsureEditorShortcutDisplayName(
					editorSettings,
					ToggleTreeScriptEditorFocusShortcutPath,
					ToggleTreeScriptEditorFocusShortcutDisplayName
				);
				bool collapseTreeDisplayNameReady = EnsureEditorShortcutDisplayName(
					editorSettings,
					CollapseTreeEditorShortcutPath,
					CollapseTreeEditorShortcutDisplayName
				);
				bool renameSelectedItemDisplayNameReady = EnsureEditorShortcutDisplayName(
					editorSettings,
					RenameSelectedItemEditorShortcutPath,
					RenameSelectedItemEditorShortcutDisplayName
				);
				bool newFolderDisplayNameReady = EnsureEditorShortcutDisplayName(
					editorSettings,
					NewFolderEditorShortcutPath,
					NewFolderEditorShortcutDisplayName
				);
				bool addExistingScriptsDisplayNameReady = EnsureEditorShortcutDisplayName(
					editorSettings,
					AddExistingScriptsEditorShortcutPath,
					AddExistingScriptsEditorShortcutDisplayName
				);
				bool addExistingScenesDisplayNameReady = EnsureEditorShortcutDisplayName(
					editorSettings,
					AddExistingScenesEditorShortcutPath,
					AddExistingScenesEditorShortcutDisplayName
				);
				bool refactorNamespaceDisplayNameReady = EnsureEditorShortcutDisplayName(
					editorSettings,
					RefactorNamespaceEditorShortcutPath,
					RefactorNamespaceEditorShortcutDisplayName
				);

				if (
					beautifyDisplayNameReady
					&& newScriptDisplayNameReady
					&& removeDisplayNameReady
					&& toggleFocusDisplayNameReady
					&& collapseTreeDisplayNameReady
					&& renameSelectedItemDisplayNameReady
					&& newFolderDisplayNameReady
					&& addExistingScriptsDisplayNameReady
					&& addExistingScenesDisplayNameReady
					&& refactorNamespaceDisplayNameReady
				)
				{
					return true;
				}

				DebugLogger.LogOperation(
					"Editor shortcut display-name verification failed",
					$"Beautify={beautifyDisplayNameReady}, NewScript={newScriptDisplayNameReady}, RemoveSelectedItem={removeDisplayNameReady}, ToggleTreeScriptEditorFocus={toggleFocusDisplayNameReady}, CollapseTree={collapseTreeDisplayNameReady}, RenameSelectedItem={renameSelectedItemDisplayNameReady}, NewFolder={newFolderDisplayNameReady}, AddExistingScripts={addExistingScriptsDisplayNameReady}, AddExistingScenes={addExistingScenesDisplayNameReady}, RefactorNamespace={refactorNamespaceDisplayNameReady}"
				);
				return false;
			}

			editorSettings.AddShortcut(
				BeautifyEditorShortcutPath,
				CreateEditorKeyShortcut(
					BeautifyEditorShortcutDisplayName,
					Key.B,
					ctrlPressed: true
				)
			);

			editorSettings.AddShortcut(
				NewScriptEditorShortcutPath,
				CreateEditorKeyShortcut(
					NewScriptEditorShortcutDisplayName,
					Key.S,
					ctrlPressed: true
				)
			);

			editorSettings.AddShortcut(
				RemoveSelectedItemEditorShortcutPath,
				CreateEditorKeyShortcut(
					RemoveSelectedItemEditorShortcutDisplayName,
					Key.Delete
				)
			);

			editorSettings.AddShortcut(
				ToggleTreeScriptEditorFocusShortcutPath,
				CreateEditorPhysicalKeyShortcut(
					ToggleTreeScriptEditorFocusShortcutDisplayName,
					Key.Quoteleft
				)
			);

			editorSettings.AddShortcut(
				CollapseTreeEditorShortcutPath,
				CreateEditorKeyShortcut(
					CollapseTreeEditorShortcutDisplayName,
					Key.T,
					ctrlPressed: true
				)
			);

			editorSettings.AddShortcut(
				RenameSelectedItemEditorShortcutPath,
				CreateEditorKeyShortcut(
					RenameSelectedItemEditorShortcutDisplayName,
					Key.R,
					ctrlPressed: true
				)
			);

			editorSettings.AddShortcut(
				NewFolderEditorShortcutPath,
				CreateEditorKeyShortcut(
					NewFolderEditorShortcutDisplayName,
					Key.F,
					ctrlPressed: true
				)
			);

			editorSettings.AddShortcut(
				AddExistingScriptsEditorShortcutPath,
				CreateEditorKeyShortcut(
					AddExistingScriptsEditorShortcutDisplayName,
					Key.S,
					ctrlPressed: true,
					altPressed: true
				)
			);

			editorSettings.AddShortcut(
				AddExistingScenesEditorShortcutPath,
				CreateEditorKeyShortcut(
					AddExistingScenesEditorShortcutDisplayName,
					Key.A,
					ctrlPressed: true,
					altPressed: true
				)
			);

			editorSettings.AddShortcut(
				RefactorNamespaceEditorShortcutPath,
				CreateEditorKeyShortcut(
					RefactorNamespaceEditorShortcutDisplayName,
					Key.N,
					ctrlPressed: true
				)
			);

			beautifyExists = editorSettings.HasShortcut(BeautifyEditorShortcutPath);
			newScriptExists = editorSettings.HasShortcut(NewScriptEditorShortcutPath);
			removeExists = editorSettings.HasShortcut(RemoveSelectedItemEditorShortcutPath);
			toggleFocusExists = editorSettings.HasShortcut(
				ToggleTreeScriptEditorFocusShortcutPath
			);
			collapseTreeExists = editorSettings.HasShortcut(
				CollapseTreeEditorShortcutPath
			);
			renameSelectedItemExists = editorSettings.HasShortcut(
				RenameSelectedItemEditorShortcutPath
			);
			newFolderExists = editorSettings.HasShortcut(
				NewFolderEditorShortcutPath
			);
			addExistingScriptsExists = editorSettings.HasShortcut(
				AddExistingScriptsEditorShortcutPath
			);
			addExistingScenesExists = editorSettings.HasShortcut(
				AddExistingScenesEditorShortcutPath
			);
			refactorNamespaceExists = editorSettings.HasShortcut(
				RefactorNamespaceEditorShortcutPath
			);

			if (
				!beautifyExists
				|| !newScriptExists
				|| !removeExists
				|| !toggleFocusExists
				|| !collapseTreeExists
				|| !renameSelectedItemExists
				|| !newFolderExists
				|| !addExistingScriptsExists
				|| !addExistingScenesExists
				|| !refactorNamespaceExists
			)
			{
				DebugLogger.LogOperation(
					"Editor shortcut registration incomplete",
					$"Beautify={beautifyExists}, NewScript={newScriptExists}, RemoveSelectedItem={removeExists}, ToggleTreeScriptEditorFocus={toggleFocusExists}, CollapseTree={collapseTreeExists}, RenameSelectedItem={renameSelectedItemExists}, NewFolder={newFolderExists}, AddExistingScripts={addExistingScriptsExists}, AddExistingScenes={addExistingScenesExists}, RefactorNamespace={refactorNamespaceExists}"
				);
				return false;
			}

			bool beautifyDisplayNameRegistered = EnsureEditorShortcutDisplayName(
				editorSettings,
				BeautifyEditorShortcutPath,
				BeautifyEditorShortcutDisplayName
			);
			bool newScriptDisplayNameRegistered = EnsureEditorShortcutDisplayName(
				editorSettings,
				NewScriptEditorShortcutPath,
				NewScriptEditorShortcutDisplayName
			);
			bool removeDisplayNameRegistered = EnsureEditorShortcutDisplayName(
				editorSettings,
				RemoveSelectedItemEditorShortcutPath,
				RemoveSelectedItemEditorShortcutDisplayName
			);
			bool toggleFocusDisplayNameRegistered = EnsureEditorShortcutDisplayName(
				editorSettings,
				ToggleTreeScriptEditorFocusShortcutPath,
				ToggleTreeScriptEditorFocusShortcutDisplayName
			);
			bool collapseTreeDisplayNameRegistered = EnsureEditorShortcutDisplayName(
				editorSettings,
				CollapseTreeEditorShortcutPath,
				CollapseTreeEditorShortcutDisplayName
			);
			bool renameSelectedItemDisplayNameRegistered = EnsureEditorShortcutDisplayName(
				editorSettings,
				RenameSelectedItemEditorShortcutPath,
				RenameSelectedItemEditorShortcutDisplayName
			);
			bool newFolderDisplayNameRegistered = EnsureEditorShortcutDisplayName(
				editorSettings,
				NewFolderEditorShortcutPath,
				NewFolderEditorShortcutDisplayName
			);
			bool addExistingScriptsDisplayNameRegistered = EnsureEditorShortcutDisplayName(
				editorSettings,
				AddExistingScriptsEditorShortcutPath,
				AddExistingScriptsEditorShortcutDisplayName
			);
			bool addExistingScenesDisplayNameRegistered = EnsureEditorShortcutDisplayName(
				editorSettings,
				AddExistingScenesEditorShortcutPath,
				AddExistingScenesEditorShortcutDisplayName
			);
			bool refactorNamespaceDisplayNameRegistered = EnsureEditorShortcutDisplayName(
				editorSettings,
				RefactorNamespaceEditorShortcutPath,
				RefactorNamespaceEditorShortcutDisplayName
			);

			if (
				!beautifyDisplayNameRegistered
				|| !newScriptDisplayNameRegistered
				|| !removeDisplayNameRegistered
				|| !toggleFocusDisplayNameRegistered
				|| !collapseTreeDisplayNameRegistered
				|| !renameSelectedItemDisplayNameRegistered
				|| !newFolderDisplayNameRegistered
				|| !addExistingScriptsDisplayNameRegistered
				|| !addExistingScenesDisplayNameRegistered
				|| !refactorNamespaceDisplayNameRegistered
			)
			{
				DebugLogger.LogOperation(
					"Editor shortcut display-name registration incomplete",
					$"Beautify={beautifyDisplayNameRegistered}, NewScript={newScriptDisplayNameRegistered}, RemoveSelectedItem={removeDisplayNameRegistered}, ToggleTreeScriptEditorFocus={toggleFocusDisplayNameRegistered}, CollapseTree={collapseTreeDisplayNameRegistered}, RenameSelectedItem={renameSelectedItemDisplayNameRegistered}, NewFolder={newFolderDisplayNameRegistered}, AddExistingScripts={addExistingScriptsDisplayNameRegistered}, AddExistingScenes={addExistingScenesDisplayNameRegistered}, RefactorNamespace={refactorNamespaceDisplayNameRegistered}"
				);
				return false;
			}

			_editorShortcutsRegisteredGeneration = ManagedAssemblyGeneration;
			return true;
		}
		catch (Exception exception)
		{
			DebugLogger.LogOperation(
				"Editor shortcut registration failed",
				exception.ToString()
			);
			return false;
		}
	}

	private bool TryGetCurrentEditorShortcut(
		string shortcutPath,
		out Shortcut shortcut
	)
	{
		shortcut = null;

		if (string.IsNullOrWhiteSpace(shortcutPath))
			return false;

		try
		{
			EditorSettings editorSettings = EditorInterface.Singleton?.GetEditorSettings();

			if (editorSettings == null)
				return false;

			if (!editorSettings.HasShortcut(shortcutPath))
			{
				if (!EnsureEditorShortcutsRegistered())
					return false;

				editorSettings = EditorInterface.Singleton?.GetEditorSettings();

				if (
					editorSettings == null
					|| !editorSettings.HasShortcut(shortcutPath)
				)
				{
					return false;
				}
			}

			Shortcut currentShortcut = editorSettings.GetShortcut(shortcutPath);

			if (currentShortcut == null || !currentShortcut.HasValidEvent())
				return false;

			shortcut = currentShortcut;
			return true;
		}
		catch (Exception exception)
		{
			DebugLogger.LogOperation(
				"Editor shortcut lookup failed",
				$"Path='{shortcutPath}', Exception='{exception}'"
			);
			return false;
		}
	}

	private bool IsEditorShortcut(string shortcutPath, InputEvent inputEvent)
	{
		if (string.IsNullOrWhiteSpace(shortcutPath) || inputEvent == null)
			return false;

		try
		{
			if (!EnsureEditorShortcutsRegistered())
				return false;

			EditorSettings editorSettings = EditorInterface.Singleton?.GetEditorSettings();

			if (editorSettings == null || !editorSettings.HasShortcut(shortcutPath))
				return false;

			return editorSettings.IsShortcut(shortcutPath, inputEvent);
		}
		catch (Exception exception)
		{
			DebugLogger.LogOperation(
				"Editor shortcut matching failed",
				$"Path='{shortcutPath}', Exception='{exception}'"
			);
			return false;
		}
	}

	public override void _Input(InputEvent inputEvent)
	{
		if (!EnsureManagedAssemblyStateCurrent("Global Input"))
			return;

		ObserveTreeKeyboardNavigationPersistenceRelease(inputEvent);

		if (HandleGlobalDockAndFilteredTreeKeyboardInput(inputEvent))
			return;

		if (HandleGlobalBeautifyShortcut(inputEvent))
			return;

		if (HandleGlobalToggleTreeScriptEditorFocusShortcut(inputEvent))
			return;

		if (HandleGlobalTreeKeyboardNavigation(inputEvent))
			return;

		HandleGlobalTreeShortcutDispatch(inputEvent);
	}

	private bool HandleGlobalBeautifyShortcut(InputEvent inputEvent)
	{
		if (inputEvent is not InputEventKey keyEvent)
			return false;

		if (
			!keyEvent.Pressed
			|| keyEvent.Echo
			|| !IsEditorShortcut(BeautifyEditorShortcutPath, keyEvent)
		)
		{
			return false;
		}

		Control focusedControl = GetTree()?.Root?.GuiGetFocusOwner();

		if (IsUnmodifiedTextEntryEvent(keyEvent) && IsTextInputFocused(focusedControl))
			return false;

		if (
			!TryHandleBeautifyShortcutForFocusedScriptEditor(
				focusedControl,
				out _
			)
		)
		{
			return false;
		}

		GetViewport().SetInputAsHandled();
		return true;
	}

	private bool HandleGlobalToggleTreeScriptEditorFocusShortcut(
		InputEvent inputEvent
	)
	{
		if (inputEvent is not InputEventKey keyEvent)
			return false;

		if (
			!keyEvent.Pressed
			|| keyEvent.Echo
			|| !IsEditorShortcut(
				ToggleTreeScriptEditorFocusShortcutPath,
				keyEvent
			)
		)
		{
			return false;
		}

		Control focusedControl = GetTree()?.Root?.GuiGetFocusOwner();
		bool isSystemNameInputFocused = IsExactFocusedControl(
			focusedControl,
			_systemNameInput
		);
		bool isScriptFilterInputFocused = IsExactFocusedControl(
			focusedControl,
			_scriptFilterInput
		);
		bool isScriptEditorFocused =
			IsCurrentScriptEditorTextEditorFocused(focusedControl);

		if (
			!isSystemNameInputFocused
			&& !isScriptFilterInputFocused
			&& !isScriptEditorFocused
		)
		{
			return false;
		}

		GetViewport().SetInputAsHandled();

		bool transitionSucceeded = isSystemNameInputFocused
			? TryFocusCurrentScriptEditorCursorFromSystemExplorer(
				SystemExplorerToggleFocusReturnTarget.SystemName
			)
			: isScriptFilterInputFocused
				? TryFocusCurrentScriptEditorCursorFromSystemExplorer(
					SystemExplorerToggleFocusReturnTarget.FilterItems
				)
				: TryReturnToSystemExplorerFromScriptEditor();

		if (!transitionSucceeded)
		{
			DebugLogger.LogOperation(
				"Toggle Focus command unavailable",
				$"FocusedControl='{focusedControl?.GetPath()}', ReturnTarget='{_systemExplorerToggleFocusReturnTarget}'"
			);
		}

		return true;
	}

	private static bool IsUnmodifiedTextEntryEvent(InputEventKey keyEvent)
	{
		return keyEvent != null
			&& keyEvent.Unicode != 0
			&& !keyEvent.CtrlPressed
			&& !keyEvent.AltPressed
			&& !keyEvent.MetaPressed;
	}

	private static bool IsTextInputFocused(Control focusedControl)
	{
		return IsFocusedControlInsideControlType<LineEdit>(focusedControl)
			|| IsFocusedControlInsideControlType<TextEdit>(focusedControl);
	}

	private static bool IsFocusedControlInsideControlType<T>(Control focusedControl)
		where T : Control
	{
		Node current = focusedControl;

		while (current != null)
		{
			if (current is T)
				return true;

			current = current.GetParent();
		}

		return false;
	}

	private static bool TryGetCurrentScriptEditorTextEditor(
		out TextEdit textEditor
	)
	{
		textEditor = null;

		EditorInterface editorInterface = EditorInterface.Singleton;

		if (editorInterface == null || !GodotObject.IsInstanceValid(editorInterface))
			return false;

		ScriptEditor scriptEditor = editorInterface.GetScriptEditor();

		if (scriptEditor == null || !GodotObject.IsInstanceValid(scriptEditor))
			return false;

		Script currentScript = scriptEditor.GetCurrentScript();

		if (currentScript == null || !GodotObject.IsInstanceValid(currentScript))
			return false;

		ScriptEditorBase currentEditor = scriptEditor.GetCurrentEditor();

		if (currentEditor == null || !GodotObject.IsInstanceValid(currentEditor))
			return false;

		Control baseEditor = currentEditor.GetBaseEditor();

		if (baseEditor is not TextEdit currentTextEditor)
			return false;

		if (
			!GodotObject.IsInstanceValid(currentTextEditor)
			|| currentTextEditor.IsQueuedForDeletion()
			|| !currentTextEditor.IsInsideTree()
			|| !currentTextEditor.IsVisibleInTree()
		)
		{
			return false;
		}

		textEditor = currentTextEditor;
		return true;
	}

	private static bool IsCurrentScriptEditorTextEditorFocused(
		Control focusedControl
	)
	{
		return TryGetCurrentScriptEditorTextEditor(out TextEdit textEditor)
			&& ControlContainsFocusedControl(textEditor, focusedControl);
	}

	private static bool TryFocusCurrentScriptEditorCursor()
	{
		if (!TryGetCurrentScriptEditorTextEditor(out TextEdit textEditor))
			return false;

		textEditor.GrabFocus();
		return true;
	}

	private bool TryFocusCurrentScriptEditorCursorFromSystemExplorer(
		SystemExplorerToggleFocusReturnTarget returnTarget
	)
	{
		if (!TryFocusCurrentScriptEditorCursor())
			return false;

		Control focusedControl = GetTree()?.Root?.GuiGetFocusOwner();

		if (!IsCurrentScriptEditorTextEditorFocused(focusedControl))
			return false;

		_systemExplorerToggleFocusReturnTarget = returnTarget;
		return true;
	}

	private bool TryReturnToSystemExplorerFromScriptEditor()
	{
		bool transitionSucceeded = _systemExplorerToggleFocusReturnTarget switch
		{
			SystemExplorerToggleFocusReturnTarget.SystemName =>
				TryReturnToSystemExplorerLineEditFromScriptEditor(
					_systemNameInput
				),
			SystemExplorerToggleFocusReturnTarget.FilterItems =>
				TryReturnToSystemExplorerLineEditFromScriptEditor(
					_scriptFilterInput
				),
			_ => TryReturnToSystemExplorerTreeFromScriptEditor(),
		};

		if (transitionSucceeded)
		{
			_systemExplorerToggleFocusReturnTarget =
				SystemExplorerToggleFocusReturnTarget.Tree;
		}

		return transitionSucceeded;
	}

	private bool TryReturnToSystemExplorerLineEditFromScriptEditor(
		LineEdit lineEdit
	)
	{
		if (
			TryRevealSystemExplorerDockForToggleFocus()
			&& TryActivateLineEditForKeyboardNavigation(lineEdit)
		)
		{
			return true;
		}

		return TryReturnToSystemExplorerTreeFromScriptEditor();
	}

	private bool TryRevealSystemExplorerDockForToggleFocus()
	{
		if (
			_editorDock == null
			|| !GodotObject.IsInstanceValid(_editorDock)
			|| _editorDock.IsQueuedForDeletion()
			|| !_editorDock.IsInsideTree()
		)
		{
			return false;
		}

		_editorDock.MakeVisible();
		return true;
	}

	private bool TryReturnToSystemExplorerTreeFromScriptEditor()
	{
		if (!TryRevealSystemExplorerDockForToggleFocus())
			return false;

		if (
			_tree == null
			|| !GodotObject.IsInstanceValid(_tree)
			|| _tree.IsQueuedForDeletion()
			|| !_tree.IsInsideTree()
		)
		{
			return false;
		}

		TreeItem selectedItem = _tree.GetSelected();

		if (IsVisibleTreeItem(selectedItem))
		{
			_tree.ScrollToItem(selectedItem);
			return TryFocusSystemExplorerHiddenTreeContext(revealDock: false);
		}

		if (TryGetFirstVisibleTreeItem(out TreeItem firstVisibleItem))
		{
			SelectTreeItemFromKeyboardNavigation(firstVisibleItem);

			if (_tree.GetSelected() != firstVisibleItem)
				return false;

			return TryFocusSystemExplorerHiddenTreeContext(revealDock: false);
		}

		return TryActivateLineEditForKeyboardNavigation(
			_isFilteringScripts ? _scriptFilterInput : _systemNameInput
		);
	}

	private bool TryHandleBeautifyShortcutForFocusedScriptEditor(
		Control focusedControl,
		out bool focusWasInScriptEditor
	)
	{
		focusWasInScriptEditor = false;

		if (!EnableQuickActions)
			return false;

		if (
			!TryGetFocusedScriptEditorBeautifyTarget(
				focusedControl,
				out FocusedScriptEditorBeautifyTarget target,
				out focusWasInScriptEditor
			)
		)
			return false;

		OpenFocusedScriptEditorBeautifyCSharpierCheck(target);
		return true;
	}

	private static bool TryGetFocusedScriptEditorBeautifyTarget(
		Control focusedControl,
		out FocusedScriptEditorBeautifyTarget target,
		out bool focusWasInScriptEditor
	)
	{
		target = default;
		focusWasInScriptEditor = false;

		if (focusedControl == null)
			return false;

		ScriptEditor scriptEditor = EditorInterface.Singleton?.GetScriptEditor();

		if (scriptEditor == null)
			return false;

		ScriptEditorBase currentEditor = scriptEditor.GetCurrentEditor();
		Control baseEditor = currentEditor?.GetBaseEditor();
		focusWasInScriptEditor = IsFocusedControlInsideScriptEditor(
			focusedControl,
			currentEditor,
			baseEditor
		);

		if (!focusWasInScriptEditor)
			return false;

		Script currentScript = scriptEditor.GetCurrentScript();
		string scriptPath = ScriptPathUtility.Normalize(currentScript?.ResourcePath);

		if (string.IsNullOrWhiteSpace(scriptPath))
			return false;

		if (!scriptPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
			return false;

		if (baseEditor is not TextEdit textEditor)
			return false;

		if (!FocusedControlMatchesActiveScriptTextEditor(focusedControl, textEditor))
			return false;

		if (!FileAccess.FileExists(scriptPath))
			return false;

		target = new FocusedScriptEditorBeautifyTarget(
			scriptPath,
			currentScript,
			currentEditor,
			textEditor
		);
		return true;
	}

	private static bool IsFocusedControlInsideScriptEditor(
		Control focusedControl,
		ScriptEditorBase currentEditor,
		Control baseEditor
	)
	{
		if (focusedControl == null)
			return false;

		if (ControlContainsFocusedControl(baseEditor, focusedControl))
			return true;

		return currentEditor is Control currentEditorControl
			&& ControlContainsFocusedControl(currentEditorControl, focusedControl);
	}

	private static bool FocusedControlMatchesActiveScriptTextEditor(
		Control focusedControl,
		TextEdit textEditor
	)
	{
		return IsBeautifyTextEditorAvailable(textEditor)
			&& ControlContainsFocusedControl(textEditor, focusedControl);
	}

	private static bool ControlContainsFocusedControl(Control container, Control focusedControl)
	{
		if (container == null || focusedControl == null)
			return false;

		return container == focusedControl || container.IsAncestorOf(focusedControl);
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

	private bool TryOpenRemoveDialogForSelectedItem()
	{
		if (_tree == null || _removeDialog == null || !GodotObject.IsInstanceValid(_removeDialog))
			return false;

		TreeItem selectedItem = _tree.GetSelected();

		if (selectedItem == null)
			return false;

		string metadata = selectedItem.GetMetadata(0).AsString();

		if (!IsRemoveTargetMetadata(metadata))
			return false;

		_pendingRemoveMetadata = metadata;
		CapturePendingRemoveTreeSelectionState(selectedItem);
		CapturePendingRemoveScriptOccurrence(selectedItem);
		OpenRemoveDialog();
		return true;
	}

	private static bool IsRemoveTargetMetadata(string metadata)
	{
		return !string.IsNullOrWhiteSpace(metadata)
			&& (
				metadata.StartsWith("system::", StringComparison.Ordinal)
				|| metadata.StartsWith("folder::", StringComparison.Ordinal)
				|| metadata.StartsWith("script::", StringComparison.Ordinal)
				|| metadata.StartsWith("sceneLink::", StringComparison.Ordinal)
			);
	}
	#endregion
}
#endif
