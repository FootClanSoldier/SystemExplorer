#if TOOLS
using Godot;
using System;
using SystemExplorer.Autocomplete.Styling;

namespace SystemExplorer.Autocomplete;

internal sealed class AutocompleteEditorBinding
{
	private const string ScriptChangedSignalName = "editor_script_changed";
	private const string ScriptEditorDescription = "C# Autocomplete ScriptEditor";
	private const string TextChangedDescription = "C# Autocomplete CodeEdit TextChanged";
	private const string CompletionRequestedDescription =
		"C# Autocomplete CodeEdit CodeCompletionRequested";
	private const string GuiInputDescription = "C# Autocomplete CodeEdit GuiInput";

	private readonly Func<ScriptEditor> _scriptEditorProvider;
	private readonly Func<GodotObject, StringName, string, string, bool> _connectPluginSignal;
	private readonly Action<GodotObject, StringName, string, string> _disconnectPluginSignal;
	private readonly string _scriptChangedMethodName;
	private readonly string _textChangedMethodName;
	private readonly string _completionRequestedMethodName;
	private readonly string _guiInputMethodName;
	private readonly Action _invalidateCompletionState;
	private readonly AutocompleteCodeEditThemeController _themeController;

	private ScriptEditor _scriptEditor;
	private CodeEdit _codeEdit;

	internal AutocompleteEditorBinding(
		Func<ScriptEditor> scriptEditorProvider,
		Func<GodotObject, StringName, string, string, bool> connectPluginSignal,
		Action<GodotObject, StringName, string, string> disconnectPluginSignal,
		string scriptChangedMethodName,
		string textChangedMethodName,
		string completionRequestedMethodName,
		string guiInputMethodName,
		Action invalidateCompletionState,
		AutocompleteCodeEditThemeController themeController
	)
	{
		_scriptEditorProvider =
			scriptEditorProvider ?? throw new ArgumentNullException(nameof(scriptEditorProvider));
		_connectPluginSignal =
			connectPluginSignal ?? throw new ArgumentNullException(nameof(connectPluginSignal));
		_disconnectPluginSignal =
			disconnectPluginSignal ?? throw new ArgumentNullException(nameof(disconnectPluginSignal));
		_scriptChangedMethodName =
			scriptChangedMethodName
			?? throw new ArgumentNullException(nameof(scriptChangedMethodName));
		_textChangedMethodName =
			textChangedMethodName ?? throw new ArgumentNullException(nameof(textChangedMethodName));
		_completionRequestedMethodName =
			completionRequestedMethodName
			?? throw new ArgumentNullException(nameof(completionRequestedMethodName));
		_guiInputMethodName =
			guiInputMethodName ?? throw new ArgumentNullException(nameof(guiInputMethodName));
		_invalidateCompletionState =
			invalidateCompletionState
			?? throw new ArgumentNullException(nameof(invalidateCompletionState));
		_themeController =
			themeController ?? throw new ArgumentNullException(nameof(themeController));
	}

	internal bool EnsureLifecycleCurrent()
	{
		ScriptEditor currentScriptEditor = _scriptEditorProvider();

		if (!IsValidGodotObject(currentScriptEditor))
		{
			DisconnectCodeEdit(cancelCompletion: true);
			DisconnectScriptEditor();
			_scriptEditor = null;
			return false;
		}

		if (
			IsValidGodotObject(_scriptEditor)
			&& _scriptEditor.GetInstanceId() != currentScriptEditor.GetInstanceId()
		)
		{
			DisconnectCodeEdit(cancelCompletion: true);
			DisconnectScriptEditor();
		}

		_scriptEditor = currentScriptEditor;

		if (
			!currentScriptEditor.HasSignal(ScriptChangedSignalName)
			|| !_connectPluginSignal(
				currentScriptEditor,
				ScriptChangedSignalName,
				_scriptChangedMethodName,
				ScriptEditorDescription
			)
		)
		{
			return false;
		}

		return RefreshCodeEditBinding();
	}

	internal bool RefreshCodeEditBinding()
	{
		DisconnectCodeEdit(cancelCompletion: true);

		ScriptEditor scriptEditor = _scriptEditor;

		if (!IsValidGodotObject(scriptEditor))
			return false;

		Script currentScript = scriptEditor.GetCurrentScript();
		ScriptEditorBase currentEditor = scriptEditor.GetCurrentEditor();

		if (!IsCSharpScript(currentScript))
			return true;

		if (!IsValidGodotObject(currentEditor))
			return false;

		Control baseEditor = currentEditor.GetBaseEditor();

		if (baseEditor is not CodeEdit codeEdit || !IsValidGodotObject(codeEdit))
			return false;

		bool textChangedConnected = _connectPluginSignal(
			codeEdit,
			TextEdit.SignalName.TextChanged,
			_textChangedMethodName,
			TextChangedDescription
		);

		if (!textChangedConnected)
			return false;

		bool completionRequestedConnected = _connectPluginSignal(
			codeEdit,
			CodeEdit.SignalName.CodeCompletionRequested,
			_completionRequestedMethodName,
			CompletionRequestedDescription
		);

		if (!completionRequestedConnected)
		{
			_disconnectPluginSignal(
				codeEdit,
				TextEdit.SignalName.TextChanged,
				_textChangedMethodName,
				$"{TextChangedDescription} rollback"
			);
			return false;
		}

		_codeEdit = codeEdit;

		_connectPluginSignal(
			codeEdit,
			Control.SignalName.GuiInput,
			_guiInputMethodName,
			GuiInputDescription
		);

		_themeController.Apply(codeEdit);
		return true;
	}

	internal bool TryGetActiveCodeEdit(out CodeEdit codeEdit, out string scriptPath)
	{
		codeEdit = _codeEdit;
		scriptPath = "";
		ScriptEditor scriptEditor = _scriptEditor;

		if (!IsValidGodotObject(codeEdit) || !IsValidGodotObject(scriptEditor))
			return false;

		Script currentScript = scriptEditor.GetCurrentScript();
		ScriptEditorBase currentEditor = scriptEditor.GetCurrentEditor();

		if (!IsCSharpScript(currentScript) || !IsValidGodotObject(currentEditor))
			return false;

		Control baseEditor = currentEditor.GetBaseEditor();

		if (
			baseEditor is not CodeEdit currentCodeEdit
			|| !IsValidGodotObject(currentCodeEdit)
			|| currentCodeEdit.GetInstanceId() != codeEdit.GetInstanceId()
		)
		{
			return false;
		}

		scriptPath = currentScript.ResourcePath;
		return true;
	}

	internal void Shutdown()
	{
		DisconnectCodeEdit(cancelCompletion: true);
		DisconnectScriptEditor();
		_codeEdit = null;
		_scriptEditor = null;
		_themeController.Reset();
	}

	private void DisconnectCodeEdit(bool cancelCompletion)
	{
		_invalidateCompletionState();

		CodeEdit codeEdit = _codeEdit;

		if (IsValidGodotObject(codeEdit))
		{
			_disconnectPluginSignal(
				codeEdit,
				TextEdit.SignalName.TextChanged,
				_textChangedMethodName,
				TextChangedDescription
			);
			_disconnectPluginSignal(
				codeEdit,
				CodeEdit.SignalName.CodeCompletionRequested,
				_completionRequestedMethodName,
				CompletionRequestedDescription
			);
			_disconnectPluginSignal(
				codeEdit,
				Control.SignalName.GuiInput,
				_guiInputMethodName,
				GuiInputDescription
			);

			if (cancelCompletion)
				codeEdit.CancelCodeCompletion();
		}

		_themeController.Restore(codeEdit);
		_codeEdit = null;
	}

	private void DisconnectScriptEditor()
	{
		_disconnectPluginSignal(
			_scriptEditor,
			ScriptChangedSignalName,
			_scriptChangedMethodName,
			ScriptEditorDescription
		);
	}

	private static bool IsCSharpScript(Script script)
	{
		return IsValidGodotObject(script)
			&& !string.IsNullOrWhiteSpace(script.ResourcePath)
			&& script.ResourcePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsValidGodotObject(GodotObject source)
	{
		return source != null && GodotObject.IsInstanceValid(source);
	}
}
#endif
