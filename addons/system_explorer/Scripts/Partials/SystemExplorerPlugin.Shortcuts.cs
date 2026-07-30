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

	#region Editor Shortcuts and Global Input Routing
	private const string BeautifyEditorShortcutPath =
		"system_explorer/beautify";
	private const string BeautifyEditorShortcutDisplayName =
		"Beautify";
	private const string RemoveSelectedItemEditorShortcutPath =
		"system_explorer/remove_selected_item";
	private const string RemoveSelectedItemEditorShortcutDisplayName =
		"Remove Selected Item";

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
			bool removeExists = editorSettings.HasShortcut(
				RemoveSelectedItemEditorShortcutPath
			);

			if (
				string.Equals(
					_editorShortcutsRegisteredGeneration,
					ManagedAssemblyGeneration,
					StringComparison.Ordinal
				)
				&& beautifyExists
				&& removeExists
			)
			{
				bool beautifyDisplayNameReady = EnsureEditorShortcutDisplayName(
					editorSettings,
					BeautifyEditorShortcutPath,
					BeautifyEditorShortcutDisplayName
				);
				bool removeDisplayNameReady = EnsureEditorShortcutDisplayName(
					editorSettings,
					RemoveSelectedItemEditorShortcutPath,
					RemoveSelectedItemEditorShortcutDisplayName
				);

				if (beautifyDisplayNameReady && removeDisplayNameReady)
					return true;

				DebugLogger.LogOperation(
					"Editor shortcut display-name verification failed",
					$"Beautify={beautifyDisplayNameReady}, RemoveSelectedItem={removeDisplayNameReady}"
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
				RemoveSelectedItemEditorShortcutPath,
				CreateEditorKeyShortcut(
					RemoveSelectedItemEditorShortcutDisplayName,
					Key.Delete
				)
			);

			beautifyExists = editorSettings.HasShortcut(BeautifyEditorShortcutPath);
			removeExists = editorSettings.HasShortcut(RemoveSelectedItemEditorShortcutPath);

			if (!beautifyExists || !removeExists)
			{
				DebugLogger.LogOperation(
					"Editor shortcut registration incomplete",
					$"Beautify={beautifyExists}, RemoveSelectedItem={removeExists}"
				);
				return false;
			}

			bool beautifyDisplayNameRegistered = EnsureEditorShortcutDisplayName(
				editorSettings,
				BeautifyEditorShortcutPath,
				BeautifyEditorShortcutDisplayName
			);
			bool removeDisplayNameRegistered = EnsureEditorShortcutDisplayName(
				editorSettings,
				RemoveSelectedItemEditorShortcutPath,
				RemoveSelectedItemEditorShortcutDisplayName
			);

			if (!beautifyDisplayNameRegistered || !removeDisplayNameRegistered)
			{
				DebugLogger.LogOperation(
					"Editor shortcut display-name registration incomplete",
					$"Beautify={beautifyDisplayNameRegistered}, RemoveSelectedItem={removeDisplayNameRegistered}"
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

		if (HandleGlobalBeautifyShortcut(inputEvent))
			return;

		HandleGlobalRemoveSelectedShortcut(inputEvent);
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
			TryHandleBeautifyShortcutForFocusedScriptEditor(
				focusedControl,
				out bool focusWasInScriptEditor
			)
		)
		{
			GetViewport().SetInputAsHandled();
			return true;
		}

		if (focusWasInScriptEditor || IsTextInputFocused(focusedControl))
			return false;

		if (_tree == null || _tree.GetSelected() == null)
			return false;

		if (!TryHandleBeautifyShortcutForSelectedItem())
			return false;

		GetViewport().SetInputAsHandled();
		return true;
	}

	private bool HandleGlobalRemoveSelectedShortcut(InputEvent inputEvent)
	{
		if (inputEvent is not InputEventKey keyEvent)
			return false;

		if (
			!keyEvent.Pressed
			|| keyEvent.Echo
			|| !IsEditorShortcut(RemoveSelectedItemEditorShortcutPath, keyEvent)
		)
		{
			return false;
		}

		if (_isFilteringScripts)
			return false;

		Control focusedControl = GetTree()?.Root?.GuiGetFocusOwner();

		if (!IsSystemExplorerShortcutFocusTarget(focusedControl))
			return false;

		if (!TryOpenRemoveDialogForSelectedItem())
			return false;

		GetViewport().SetInputAsHandled();
		return true;
	}

	private bool IsSystemExplorerShortcutFocusTarget(Control focusedControl)
	{
		return focusedControl != null
			&& _focusReleaseTarget != null
			&& GodotObject.IsInstanceValid(_focusReleaseTarget)
			&& _focusReleaseTarget.IsInsideTree()
			&& focusedControl == _focusReleaseTarget;
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

	private static bool IsCtrlShiftCollapseCommand(InputEventKey keyEvent)
	{
		bool isCtrlKey = keyEvent.Keycode == Key.Ctrl || keyEvent.PhysicalKeycode == Key.Ctrl;
		bool isShiftKey = keyEvent.Keycode == Key.Shift || keyEvent.PhysicalKeycode == Key.Shift;

		return (isCtrlKey && keyEvent.ShiftPressed) || (isShiftKey && keyEvent.CtrlPressed);
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
		CapturePendingRemoveScriptOccurrence();
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
