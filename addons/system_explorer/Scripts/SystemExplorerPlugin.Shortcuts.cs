#if TOOLS
using Godot;
using System;
using SystemExplorer.EditorIntegration.ScriptEditing;

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

        Control focusedControl = GetTree()?.Root?.GuiGetFocusOwner();

        if (IsTextInputFocused(focusedControl))
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

        Control focusedControl = GetTree()?.Root?.GuiGetFocusOwner();

        if (
            TryHandleBeautifyShortcutForFocusedScriptEditor(
                focusedControl,
                out bool focusWasInScriptEditor
            )
        )
        {
            GetViewport().SetInputAsHandled();
            return;
        }

        if (focusWasInScriptEditor || IsTextInputFocused(focusedControl))
            return;

        if (_tree == null || _tree.GetSelected() == null)
            return;

        if (TryHandleBeautifyShortcutForSelectedItem())
            GetViewport().SetInputAsHandled();
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
                out string scriptPath,
                out focusWasInScriptEditor
            )
        )
            return false;

        OpenBeautifyScriptPathCSharpierCheckDialog(scriptPath);
        return true;
    }

    private static bool TryGetFocusedScriptEditorBeautifyTarget(
        Control focusedControl,
        out string scriptPath,
        out bool focusWasInScriptEditor
    )
    {
        scriptPath = "";
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
        scriptPath = ScriptPathUtility.Normalize(currentScript?.ResourcePath);

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
