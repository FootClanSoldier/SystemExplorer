#if TOOLS
using Godot;
using System;

namespace SystemExplorer.CodeService.Documents;

internal sealed class CodeServiceDocumentEditorBinding
{
	private const string ScriptChangedSignalName = "editor_script_changed";
	private const string ScriptCloseSignalName = "script_close";
	private const string ScriptEditorDescription = "CodeService Document ScriptEditor";
	private const string TextChangedDescription = "CodeService Document CodeEdit TextChanged";

	private readonly Func<ScriptEditor> _scriptEditorProvider;
	private readonly Func<GodotObject, StringName, string, string, bool> _connectPluginSignal;
	private readonly Action<GodotObject, StringName, string, string> _disconnectPluginSignal;
	private readonly string _scriptChangedMethodName;
	private readonly string _scriptCloseMethodName;
	private readonly string _textChangedMethodName;

	private ScriptEditor _scriptEditor;
	private Script _script;
	private ScriptEditorBase _scriptEditorBase;
	private CodeEdit _codeEdit;
	private string _resourcePath = "";
	private string _wirePath = "";

	internal CodeServiceDocumentEditorBinding(
		Func<ScriptEditor> scriptEditorProvider,
		Func<GodotObject, StringName, string, string, bool> connectPluginSignal,
		Action<GodotObject, StringName, string, string> disconnectPluginSignal,
		string scriptChangedMethodName,
		string scriptCloseMethodName,
		string textChangedMethodName
	)
	{
		_scriptEditorProvider = scriptEditorProvider ?? throw new ArgumentNullException(nameof(scriptEditorProvider));
		_connectPluginSignal = connectPluginSignal ?? throw new ArgumentNullException(nameof(connectPluginSignal));
		_disconnectPluginSignal = disconnectPluginSignal ?? throw new ArgumentNullException(nameof(disconnectPluginSignal));
		_scriptChangedMethodName = scriptChangedMethodName ?? throw new ArgumentNullException(nameof(scriptChangedMethodName));
		_scriptCloseMethodName = scriptCloseMethodName ?? throw new ArgumentNullException(nameof(scriptCloseMethodName));
		_textChangedMethodName = textChangedMethodName ?? throw new ArgumentNullException(nameof(textChangedMethodName));
	}

	internal ScriptEditor ScriptEditor => IsValid(_scriptEditor) ? _scriptEditor : null;
	internal string BoundWirePath => _wirePath;

	internal bool EnsureLifecycleCurrent()
	{
		ScriptEditor currentScriptEditor = _scriptEditorProvider();
		if (!IsValid(currentScriptEditor))
		{
			DisconnectBoundCodeEdit();
			DisconnectScriptEditor();
			_scriptEditor = null;
			return false;
		}

		if (IsValid(_scriptEditor) && _scriptEditor.GetInstanceId() != currentScriptEditor.GetInstanceId())
		{
			DisconnectBoundCodeEdit();
			DisconnectScriptEditor();
		}

		_scriptEditor = currentScriptEditor;
		if (!currentScriptEditor.HasSignal(ScriptChangedSignalName)
			|| !currentScriptEditor.HasSignal(ScriptCloseSignalName)
			|| !_connectPluginSignal(currentScriptEditor, ScriptChangedSignalName, _scriptChangedMethodName, ScriptEditorDescription)
			|| !_connectPluginSignal(currentScriptEditor, ScriptCloseSignalName, _scriptCloseMethodName, ScriptEditorDescription + " Close"))
		{
			return false;
		}

		// Avoid disconnect/reconnect churn on every quiet boundary when the already-bound
		// editor is still exactly current. If current identity changed, callers preserve
		// the outgoing buffer before asking this method to rebind.
		if (TryGetCurrentDocument(out _, out _, out _))
			return true;
		if (!TryGetBoundDocument(out _, out _, out _)
			&& !IsCSharpScript(currentScriptEditor.GetCurrentScript()))
		{
			return true;
		}

		return RefreshCurrentBinding();
	}

	internal bool RefreshCurrentBinding()
	{
		DisconnectBoundCodeEdit();
		ScriptEditor scriptEditor = _scriptEditor;
		if (!IsValid(scriptEditor))
			return false;

		Script currentScript = scriptEditor.GetCurrentScript();
		ScriptEditorBase currentEditor = scriptEditor.GetCurrentEditor();
		if (!IsCSharpScript(currentScript))
			return true;
		if (!IsValid(currentEditor))
			return false;

		Control baseEditor = currentEditor.GetBaseEditor();
		if (baseEditor is not CodeEdit codeEdit || !IsValid(codeEdit) || codeEdit.IsQueuedForDeletion())
			return false;
		if (!CodeServiceDocumentPath.TryFromResourcePath(currentScript.ResourcePath, out string wirePath, out _))
			return false;
		if (!_connectPluginSignal(codeEdit, TextEdit.SignalName.TextChanged, _textChangedMethodName, TextChangedDescription))
			return false;

		_script = currentScript;
		_scriptEditorBase = currentEditor;
		_codeEdit = codeEdit;
		_resourcePath = currentScript.ResourcePath;
		_wirePath = wirePath;
		return true;
	}

	internal bool TryGetBoundDocument(out string resourcePath, out string wirePath, out CodeEdit codeEdit)
	{
		resourcePath = _resourcePath;
		wirePath = _wirePath;
		codeEdit = _codeEdit;
		return !string.IsNullOrEmpty(wirePath)
			&& IsValid(_script)
			&& IsValid(_scriptEditorBase)
			&& IsValid(codeEdit)
			&& !codeEdit.IsQueuedForDeletion();
	}

	internal bool TryGetCurrentDocument(out string resourcePath, out string wirePath, out CodeEdit codeEdit)
	{
		resourcePath = "";
		wirePath = "";
		codeEdit = null;
		if (!TryGetBoundDocument(out string boundResourcePath, out string boundWirePath, out CodeEdit boundCodeEdit)
			|| !IsValid(_scriptEditor))
			return false;

		Script currentScript = _scriptEditor.GetCurrentScript();
		ScriptEditorBase currentEditor = _scriptEditor.GetCurrentEditor();
		if (!IsValid(currentScript) || !IsValid(currentEditor)
			|| currentScript.GetInstanceId() != _script.GetInstanceId()
			|| currentEditor.GetInstanceId() != _scriptEditorBase.GetInstanceId())
			return false;

		Control baseEditor = currentEditor.GetBaseEditor();
		if (baseEditor is not CodeEdit currentCodeEdit || !IsValid(currentCodeEdit)
			|| currentCodeEdit.GetInstanceId() != boundCodeEdit.GetInstanceId())
			return false;
		if (!string.Equals(currentScript.ResourcePath, boundResourcePath, StringComparison.Ordinal))
			return false;

		resourcePath = boundResourcePath;
		wirePath = boundWirePath;
		codeEdit = boundCodeEdit;
		return true;
	}

	internal void Shutdown()
	{
		DisconnectBoundCodeEdit();
		DisconnectScriptEditor();
		_scriptEditor = null;
	}

	private void DisconnectBoundCodeEdit()
	{
		if (IsValid(_codeEdit))
		{
			_disconnectPluginSignal(_codeEdit, TextEdit.SignalName.TextChanged, _textChangedMethodName, TextChangedDescription);
		}
		_script = null;
		_scriptEditorBase = null;
		_codeEdit = null;
		_resourcePath = "";
		_wirePath = "";
	}

	private void DisconnectScriptEditor()
	{
		_disconnectPluginSignal(_scriptEditor, ScriptChangedSignalName, _scriptChangedMethodName, ScriptEditorDescription);
		_disconnectPluginSignal(_scriptEditor, ScriptCloseSignalName, _scriptCloseMethodName, ScriptEditorDescription + " Close");
	}

	private static bool IsCSharpScript(Script script) =>
		IsValid(script)
		&& !string.IsNullOrWhiteSpace(script.ResourcePath)
		&& script.ResourcePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

	private static bool IsValid(GodotObject value) => value != null && GodotObject.IsInstanceValid(value);
}
#endif
