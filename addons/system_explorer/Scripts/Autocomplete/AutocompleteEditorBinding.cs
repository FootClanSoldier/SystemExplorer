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

	private readonly string _managedAssemblyGeneration;
	private readonly bool _cancelNativeCompletionOnRebind;
	private readonly Func<ScriptEditor> _scriptEditorProvider;
	private readonly Func<GodotObject, StringName, string, string, bool> _connectPluginSignal;
	private readonly Action<GodotObject, StringName, string, string> _disconnectPluginSignal;
	private readonly string _scriptChangedMethodName;
	private readonly string _textChangedMethodName;
	private readonly string _completionRequestedMethodName;
	private readonly string _guiInputMethodName;
	private readonly Action _invalidateCompletionState;
	private readonly AutocompleteCodeCompletionPrefixController _completionPrefixController;
	private readonly AutocompleteCodeEditThemeController _themeController;
	private readonly AutocompleteCodeEditNativeOwnershipBridge _nativeOwnershipBridge = new();
	private readonly Action<string, string> _debugLog;
	private readonly Func<bool> _debugEnabled;
	private readonly string _bindingInstanceToken = Guid.NewGuid().ToString("N");
	private bool _nativeOwnershipMalformedMarkerLogged;
	private bool _nativeOwnershipRestoreFailureLogged;
	private bool _nativeOwnershipMarkerWriteFailureLogged;
	private bool _nativeOwnershipMarkerClearFailureLogged;
	private bool _nativeOwnershipSameGenerationMarkerLogged;

	private ScriptEditor _scriptEditor;
	private CodeEdit _codeEdit;

	internal AutocompleteEditorBinding(
		string managedAssemblyGeneration,
		bool cancelNativeCompletionOnRebind,
		Func<ScriptEditor> scriptEditorProvider,
		Func<GodotObject, StringName, string, string, bool> connectPluginSignal,
		Action<GodotObject, StringName, string, string> disconnectPluginSignal,
		string scriptChangedMethodName,
		string textChangedMethodName,
		string completionRequestedMethodName,
		string guiInputMethodName,
		Action invalidateCompletionState,
		AutocompleteCodeCompletionPrefixController completionPrefixController,
		AutocompleteCodeEditThemeController themeController,
		Action<string, string> debugLog,
		Func<bool> debugEnabled
	)
	{
		_managedAssemblyGeneration = !string.IsNullOrWhiteSpace(managedAssemblyGeneration)
			? managedAssemblyGeneration
			: throw new ArgumentException(
				"Managed assembly generation is required.",
				nameof(managedAssemblyGeneration)
			);
		_cancelNativeCompletionOnRebind = cancelNativeCompletionOnRebind;
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
		_completionPrefixController =
			completionPrefixController
			?? throw new ArgumentNullException(nameof(completionPrefixController));
		_themeController =
			themeController ?? throw new ArgumentNullException(nameof(themeController));
		_debugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
		_debugEnabled = debugEnabled ?? throw new ArgumentNullException(nameof(debugEnabled));
		Trace(
			"C# autocomplete binding constructed",
			$"ManagedAssemblyGeneration='{_managedAssemblyGeneration}', CancelNativeCompletionOnRebind='{_cancelNativeCompletionOnRebind}'"
		);

		if (!_cancelNativeCompletionOnRebind)
		{
			Trace(
				"C# autocomplete CodeEdit rebind native completion cancellation isolated",
				"Enabled='False', Scope='RefreshCodeEditBindingInitialDisconnect', SignalDisconnectRetained='True', ShutdownCancellationRetained='True', CompletionCoordinatorCancellationRetained='True'"
			);
		}
	}

	internal bool EnsureLifecycleCurrent(bool refreshCodeEditBinding = true)
	{
		ScriptEditor currentScriptEditor = _scriptEditorProvider();

		if (!IsValidGodotObject(currentScriptEditor))
		{
			Trace(
				"C# autocomplete binding lifecycle anomaly",
				() => $"Reason='Current ScriptEditor invalid', RefreshCodeEditBinding='{refreshCodeEditBinding}', {DescribeGodotObject("CurrentScriptEditor", currentScriptEditor)}"
			);
			if (refreshCodeEditBinding)
				DisconnectCodeEdit(cancelCompletion: true);
			DisconnectScriptEditor();
			_scriptEditor = null;
			return false;
		}

		bool scriptEditorIdentityChanged =
			IsValidGodotObject(_scriptEditor)
			&& _scriptEditor.GetInstanceId() != currentScriptEditor.GetInstanceId();

		if (scriptEditorIdentityChanged)
		{
			Trace(
				"C# autocomplete binding ScriptEditor identity changed",
				() => $"RefreshCodeEditBinding='{refreshCodeEditBinding}', {DescribeGodotObject("PreviousScriptEditor", _scriptEditor)}, {DescribeGodotObject("CurrentScriptEditor", currentScriptEditor)}"
			);
			if (refreshCodeEditBinding)
				DisconnectCodeEdit(cancelCompletion: true);
			DisconnectScriptEditor();
		}

		_scriptEditor = currentScriptEditor;

		bool hasScriptChangedSignal = currentScriptEditor.HasSignal(ScriptChangedSignalName);
		if (!hasScriptChangedSignal)
		{
			Trace(
				"C# autocomplete binding lifecycle anomaly",
				() => $"Reason='ScriptChanged signal unavailable', RefreshCodeEditBinding='{refreshCodeEditBinding}', {DescribeGodotObject("ScriptEditor", currentScriptEditor)}"
			);
			return false;
		}

		bool scriptChangedConnected = _connectPluginSignal(
			currentScriptEditor,
			ScriptChangedSignalName,
			_scriptChangedMethodName,
			ScriptEditorDescription
		);

		if (!scriptChangedConnected)
		{
			Trace(
				"C# autocomplete binding lifecycle anomaly",
				() => $"Reason='ScriptChanged connect failed', RefreshCodeEditBinding='{refreshCodeEditBinding}', {DescribeGodotObject("ScriptEditor", currentScriptEditor)}"
			);
			return false;
		}

		if (!refreshCodeEditBinding)
			return true;

		return RefreshCodeEditBinding();
	}

	internal bool RefreshCodeEditBinding(
		Action<string, ScriptEditor, CodeEdit> diagnosticPhase = null
	)
	{
		string previousCodeEditInstanceId = CaptureInstanceIdForDiagnostics(_codeEdit);
		DisconnectCodeEdit(cancelCompletion: _cancelNativeCompletionOnRebind);

		ScriptEditor scriptEditor = _scriptEditor;

		if (!IsValidGodotObject(scriptEditor))
		{
			Trace(
				"C# autocomplete binding rebind failed",
				() => $"Reason='Invalid ScriptEditor', PreviousCodeEditInstanceId='{previousCodeEditInstanceId}', {DescribeGodotObject("ScriptEditor", scriptEditor)}"
			);
			return false;
		}

		InvokeDiagnosticPhase(
			diagnosticPhase,
			"RefreshCodeEditBinding.GetCurrentScript",
			scriptEditor,
			codeEdit: null
		);
		Script currentScript = scriptEditor.GetCurrentScript();
		InvokeDiagnosticPhase(
			diagnosticPhase,
			"RefreshCodeEditBinding.GetCurrentEditor",
			scriptEditor,
			codeEdit: null
		);
		ScriptEditorBase currentEditor = scriptEditor.GetCurrentEditor();

		if (!IsCSharpScript(currentScript))
		{
			TraceRebindSummary(
				scriptEditor,
				previousCodeEditInstanceId,
				currentCodeEdit: null,
				currentScript,
				result: true,
				reason: "Current script is not C#"
			);
			return true;
		}

		if (!IsValidGodotObject(currentEditor))
		{
			Trace(
				"C# autocomplete binding rebind failed",
				() => $"Reason='Invalid ScriptEditorBase', PreviousCodeEditInstanceId='{previousCodeEditInstanceId}', {DescribeScript(currentScript)}, {DescribeGodotObject("ScriptEditorBase", currentEditor)}"
			);
			return false;
		}

		InvokeDiagnosticPhase(
			diagnosticPhase,
			"RefreshCodeEditBinding.GetBaseEditor",
			scriptEditor,
			codeEdit: null
		);
		Control baseEditor = currentEditor.GetBaseEditor();

		if (baseEditor is not CodeEdit codeEdit || !IsValidGodotObject(codeEdit))
		{
			Trace(
				"C# autocomplete binding rebind failed",
				() => $"Reason='Base editor is not a valid CodeEdit', PreviousCodeEditInstanceId='{previousCodeEditInstanceId}', {DescribeScript(currentScript)}, {DescribeGodotObject("BaseEditor", baseEditor)}"
			);
			return false;
		}

		bool allowFreshNativeOwnershipMarkerWrite = true;
		RecoverStaleNativeOwnershipIfNeeded(
			codeEdit,
			ref allowFreshNativeOwnershipMarkerWrite
		);

		InvokeDiagnosticPhase(
			diagnosticPhase,
			"RefreshCodeEditBinding.BindCodeEdit",
			scriptEditor,
			codeEdit
		);

		bool textChangedConnected = _connectPluginSignal(
			codeEdit,
			TextEdit.SignalName.TextChanged,
			_textChangedMethodName,
			TextChangedDescription
		);

		if (!textChangedConnected)
		{
			Trace(
				"C# autocomplete binding rebind failed",
				() => $"Reason='TextChanged connect failed', PreviousCodeEditInstanceId='{previousCodeEditInstanceId}', {DescribeGodotObject("CodeEdit", codeEdit)}, {DescribeScript(currentScript)}"
			);
			return false;
		}

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
			Trace(
				"C# autocomplete binding rebind failed",
				() => $"Reason='CodeCompletionRequested connect failed', PreviousCodeEditInstanceId='{previousCodeEditInstanceId}', {DescribeGodotObject("CodeEdit", codeEdit)}, {DescribeScript(currentScript)}"
			);
			return false;
		}

		_codeEdit = codeEdit;

		bool guiInputConnected = _connectPluginSignal(
			codeEdit,
			Control.SignalName.GuiInput,
			_guiInputMethodName,
			GuiInputDescription
		);
		if (!guiInputConnected)
		{
			Trace(
				"C# autocomplete binding anomaly",
				() => $"Reason='GuiInput connect failed; non-critical', {DescribeGodotObject("CodeEdit", codeEdit)}, {DescribeScript(currentScript)}"
			);
		}

		bool prefixApplied = _completionPrefixController.Apply(codeEdit);
		if (!prefixApplied)
		{
			DisconnectCodeEdit(cancelCompletion: true);
			Trace(
				"C# autocomplete binding rebind failed",
				() => $"Reason='CompletionPrefix Apply failed', PreviousCodeEditInstanceId='{previousCodeEditInstanceId}', {DescribeGodotObject("CodeEdit", codeEdit)}, {DescribeScript(currentScript)}"
			);
			return false;
		}

		try
		{
			_themeController.Apply(codeEdit);
			if (allowFreshNativeOwnershipMarkerWrite)
				PublishFreshNativeOwnershipMarkerBestEffort(codeEdit);

			TraceRebindSummary(
				scriptEditor,
				previousCodeEditInstanceId,
				codeEdit,
				currentScript,
				result: true,
				reason: "Bound C# CodeEdit"
			);
			return true;
		}
		catch (Exception exception)
		{
			Trace(
				"C# autocomplete binding Theme Apply failed",
				() => $"{DescribeGodotObject("CodeEdit", codeEdit)}, Exception='{exception}'"
			);
			DisconnectCodeEdit(cancelCompletion: true);
			throw;
		}
	}

	internal bool TryGetActiveCodeEdit(
		out CodeEdit codeEdit,
		out string scriptPath,
		Action<string, ScriptEditor, CodeEdit> diagnosticPhase = null
	)
	{
		codeEdit = _codeEdit;
		scriptPath = "";
		ScriptEditor scriptEditor = _scriptEditor;

		if (!IsValidGodotObject(codeEdit) || !IsValidGodotObject(scriptEditor))
			return false;

		InvokeDiagnosticPhase(
			diagnosticPhase,
			"ValidateActiveCodeEdit.GetCurrentScript",
			scriptEditor,
			codeEdit
		);
		Script currentScript = scriptEditor.GetCurrentScript();
		InvokeDiagnosticPhase(
			diagnosticPhase,
			"ValidateActiveCodeEdit.GetCurrentEditor",
			scriptEditor,
			codeEdit
		);
		ScriptEditorBase currentEditor = scriptEditor.GetCurrentEditor();

		if (!IsCSharpScript(currentScript) || !IsValidGodotObject(currentEditor))
			return false;

		InvokeDiagnosticPhase(
			diagnosticPhase,
			"ValidateActiveCodeEdit.GetBaseEditor",
			scriptEditor,
			codeEdit
		);
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
		InvokeDiagnosticPhase(
			diagnosticPhase,
			"ValidateActiveCodeEdit.Completed",
			scriptEditor,
			codeEdit
		);
		return true;
	}

	internal void Shutdown()
	{
		Trace(
			"C# autocomplete binding Shutdown begin",
			() => $"{DescribeGodotObject("ScriptEditor", _scriptEditor)}, {DescribeGodotObject("CodeEdit", _codeEdit)}"
		);
		DisconnectCodeEdit(cancelCompletion: true);
		DisconnectScriptEditor();
		_codeEdit = null;
		_scriptEditor = null;
		_completionPrefixController.Reset();
		_themeController.Reset();
		Trace("C# autocomplete binding Shutdown completed");
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
		else if (codeEdit != null)
		{
			Trace(
				"C# autocomplete binding cleanup anomaly",
				() => $"Reason='Bound CodeEdit invalid; native disconnect/cancel skipped', {DescribeGodotObject("CodeEdit", codeEdit)}"
			);
		}

		_completionPrefixController.Restore(codeEdit);
		_themeController.Restore(codeEdit);
		ClearCurrentGenerationNativeOwnershipMarkerBestEffort(codeEdit);
		_codeEdit = null;
	}

	private void RecoverStaleNativeOwnershipIfNeeded(
		CodeEdit codeEdit,
		ref bool allowFreshNativeOwnershipMarkerWrite
	)
	{
		AutocompleteCodeEditNativeOwnershipBridge.MarkerReadStatus markerStatus =
			_nativeOwnershipBridge.Inspect(
				codeEdit,
				out AutocompleteCodeEditNativeOwnershipBridge.OwnershipState state,
				out string failureDetail
			);

		if (markerStatus == AutocompleteCodeEditNativeOwnershipBridge.MarkerReadStatus.Missing)
			return;

		if (markerStatus != AutocompleteCodeEditNativeOwnershipBridge.MarkerReadStatus.Valid)
		{
			allowFreshNativeOwnershipMarkerWrite = false;
			TraceNativeOwnershipMalformedMarkerOnce(codeEdit, failureDetail);
			return;
		}

		if (
			string.Equals(
				state.OwnerManagedAssemblyGeneration,
				_managedAssemblyGeneration,
				StringComparison.Ordinal
			)
		)
		{
			allowFreshNativeOwnershipMarkerWrite = false;
			TraceNativeOwnershipSameGenerationMarkerOnce(state);
			return;
		}

		Trace(
			"C# autocomplete native CodeEdit ownership orphan detected",
			() =>
				$"CodeEditNativeInstanceId='{state.CodeEditNativeInstanceId}', PreviousManagedAssemblyGeneration='{state.OwnerManagedAssemblyGeneration}', CurrentManagedAssemblyGeneration='{_managedAssemblyGeneration}', PrefixOwned='{state.PrefixOwned}', CompletionExistingColorOwned='{state.CompletionExistingColorOwned}', HadPreviousCompletionExistingColorOverride='{state.HadPreviousCompletionExistingColorOverride}'"
		);

		bool prefixRestored =
			!state.PrefixOwned
			|| _completionPrefixController.TryRestoreOwnedPrefixesFromNativeBridge(
				codeEdit,
				state.PreviousCodeCompletionPrefixes
			);
		bool completionExistingColorRestored =
			!state.CompletionExistingColorOwned
			|| _themeController.TryRestoreCompletionExistingColorFromNativeBridge(
				codeEdit,
				state.HadPreviousCompletionExistingColorOverride,
				state.PreviousCompletionExistingColor
			);

		if (!prefixRestored || !completionExistingColorRestored)
		{
			allowFreshNativeOwnershipMarkerWrite = false;
			TraceNativeOwnershipRestoreFailureOnce(
				state,
				$"PrefixRestored='{prefixRestored}', CompletionExistingColorRestored='{completionExistingColorRestored}'"
			);
			return;
		}

		if (
			!_nativeOwnershipBridge.TryClearVerifiedMarker(
				codeEdit,
				state,
				out string clearFailureDetail
			)
		)
		{
			allowFreshNativeOwnershipMarkerWrite = false;
			TraceNativeOwnershipRestoreFailureOnce(
				state,
				$"RestorationApplied='True', MarkerClearFailure='{clearFailureDetail}'"
			);
			return;
		}

		Trace(
			"C# autocomplete native CodeEdit ownership orphan recovered",
			() =>
				$"CodeEditNativeInstanceId='{state.CodeEditNativeInstanceId}', PreviousManagedAssemblyGeneration='{state.OwnerManagedAssemblyGeneration}', CurrentManagedAssemblyGeneration='{_managedAssemblyGeneration}', PrefixOwned='{state.PrefixOwned}', CompletionExistingColorOwned='{state.CompletionExistingColorOwned}', HadPreviousCompletionExistingColorOverride='{state.HadPreviousCompletionExistingColorOverride}'"
		);
	}

	private void PublishFreshNativeOwnershipMarkerBestEffort(CodeEdit codeEdit)
	{
		if (
			!_completionPrefixController.TryCaptureNativeOwnershipState(
				codeEdit,
				out ulong prefixCodeEditInstanceId,
				out bool prefixOwned,
				out string[] previousPrefixes
			)
			|| !_themeController.TryCaptureCompletionExistingColorNativeOwnershipState(
				codeEdit,
				out ulong themeCodeEditInstanceId,
				out bool completionExistingColorOwned,
				out bool hadPreviousCompletionExistingColorOverride,
				out Color previousCompletionExistingColor
			)
		)
		{
			TraceNativeOwnershipMarkerWriteFailureOnce(
				codeEdit,
				"Managed reversible-state snapshot could not be mirrored."
			);
			return;
		}

		if (!prefixOwned && !completionExistingColorOwned)
			return;

		ulong codeEditNativeInstanceId = completionExistingColorOwned
			? themeCodeEditInstanceId
			: prefixCodeEditInstanceId;

		if (
			codeEditNativeInstanceId == 0
			|| (
				prefixOwned
				&& completionExistingColorOwned
				&& prefixCodeEditInstanceId != themeCodeEditInstanceId
			)
		)
		{
			TraceNativeOwnershipMarkerWriteFailureOnce(
				codeEdit,
				$"Managed snapshot CodeEdit identity mismatch. PrefixSnapshotInstanceId='{prefixCodeEditInstanceId}', ThemeSnapshotInstanceId='{themeCodeEditInstanceId}'."
			);
			return;
		}

		var state = new AutocompleteCodeEditNativeOwnershipBridge.OwnershipState(
			_managedAssemblyGeneration,
			codeEditNativeInstanceId,
			prefixOwned,
			prefixOwned ? previousPrefixes : Array.Empty<string>(),
			completionExistingColorOwned,
			hadPreviousCompletionExistingColorOverride,
			previousCompletionExistingColor
		);

		if (
			!_nativeOwnershipBridge.TryWrite(
				codeEdit,
				state,
				out string failureDetail
			)
		)
		{
			TraceNativeOwnershipMarkerWriteFailureOnce(codeEdit, failureDetail);
		}
	}

	private void ClearCurrentGenerationNativeOwnershipMarkerBestEffort(CodeEdit codeEdit)
	{
		if (!IsValidGodotObject(codeEdit))
			return;

		if (
			!_nativeOwnershipBridge.TryClearOwnedMarkerForGeneration(
				codeEdit,
				_managedAssemblyGeneration,
				out string failureDetail
			)
		)
		{
			if (failureDetail.StartsWith("Marker clear failed:", StringComparison.Ordinal))
				TraceNativeOwnershipMarkerClearFailureOnce(codeEdit, failureDetail);
			else
				TraceNativeOwnershipMalformedMarkerOnce(codeEdit, failureDetail);
		}
	}

	private void TraceNativeOwnershipMalformedMarkerOnce(
		CodeEdit codeEdit,
		string failureDetail
	)
	{
		if (_nativeOwnershipMalformedMarkerLogged)
			return;

		_nativeOwnershipMalformedMarkerLogged = true;
		Trace(
			"C# autocomplete native CodeEdit ownership marker malformed",
			() =>
				$"CurrentManagedAssemblyGeneration='{_managedAssemblyGeneration}', {DescribeGodotObject("CodeEdit", codeEdit)}, Detail='{failureDetail}'"
		);
	}

	private void TraceNativeOwnershipRestoreFailureOnce(
		AutocompleteCodeEditNativeOwnershipBridge.OwnershipState state,
		string failureDetail
	)
	{
		if (_nativeOwnershipRestoreFailureLogged)
			return;

		_nativeOwnershipRestoreFailureLogged = true;
		Trace(
			"C# autocomplete native CodeEdit ownership orphan restoration failure",
			$"CodeEditNativeInstanceId='{state?.CodeEditNativeInstanceId}', PreviousManagedAssemblyGeneration='{state?.OwnerManagedAssemblyGeneration}', CurrentManagedAssemblyGeneration='{_managedAssemblyGeneration}', Detail='{failureDetail}'"
		);
	}

	private void TraceNativeOwnershipMarkerWriteFailureOnce(
		CodeEdit codeEdit,
		string failureDetail
	)
	{
		if (_nativeOwnershipMarkerWriteFailureLogged)
			return;

		_nativeOwnershipMarkerWriteFailureLogged = true;
		Trace(
			"C# autocomplete native CodeEdit ownership fresh marker write failure",
			() =>
				$"CurrentManagedAssemblyGeneration='{_managedAssemblyGeneration}', {DescribeGodotObject("CodeEdit", codeEdit)}, Detail='{failureDetail}'"
		);
	}

	private void TraceNativeOwnershipMarkerClearFailureOnce(
		CodeEdit codeEdit,
		string failureDetail
	)
	{
		if (_nativeOwnershipMarkerClearFailureLogged)
			return;

		_nativeOwnershipMarkerClearFailureLogged = true;
		Trace(
			"C# autocomplete native CodeEdit ownership marker clear failure",
			() =>
				$"CurrentManagedAssemblyGeneration='{_managedAssemblyGeneration}', {DescribeGodotObject("CodeEdit", codeEdit)}, Detail='{failureDetail}'"
		);
	}

	private void TraceNativeOwnershipSameGenerationMarkerOnce(
		AutocompleteCodeEditNativeOwnershipBridge.OwnershipState state
	)
	{
		if (_nativeOwnershipSameGenerationMarkerLogged)
			return;

		_nativeOwnershipSameGenerationMarkerLogged = true;
		Trace(
			"C# autocomplete native CodeEdit ownership same-generation marker observed",
			$"CodeEditNativeInstanceId='{state?.CodeEditNativeInstanceId}', ManagedAssemblyGeneration='{_managedAssemblyGeneration}', PrefixOwned='{state?.PrefixOwned}', CompletionExistingColorOwned='{state?.CompletionExistingColorOwned}'"
		);
	}

	private void DisconnectScriptEditor()
	{
		if (_scriptEditor != null && !IsValidGodotObject(_scriptEditor))
		{
			Trace(
				"C# autocomplete binding cleanup anomaly",
				() => $"Reason='ScriptEditor invalid during disconnect', {DescribeGodotObject("ScriptEditor", _scriptEditor)}"
			);
		}

		_disconnectPluginSignal(
			_scriptEditor,
			ScriptChangedSignalName,
			_scriptChangedMethodName,
			ScriptEditorDescription
		);
	}

	private static void InvokeDiagnosticPhase(
		Action<string, ScriptEditor, CodeEdit> diagnosticPhase,
		string phase,
		ScriptEditor scriptEditor,
		CodeEdit codeEdit
	)
	{
		try
		{
			diagnosticPhase?.Invoke(phase ?? "", scriptEditor, codeEdit);
		}
		catch
		{
			// Diagnostic observation must not affect native binding behavior.
		}
	}

	private void TraceRebindSummary(
		ScriptEditor scriptEditor,
		string previousCodeEditInstanceId,
		CodeEdit currentCodeEdit,
		Script currentScript,
		bool result,
		string reason
	)
	{
		string currentCodeEditInstanceId = CaptureInstanceIdForDiagnostics(currentCodeEdit);
		if (
			result
			&& string.Equals(
				previousCodeEditInstanceId,
				currentCodeEditInstanceId,
				StringComparison.Ordinal
			)
		)
		{
			return;
		}

		Trace(
			"C# autocomplete binding rebound",
			() =>
				$"ScriptEditorInstanceId='{DescribeInstanceIdValue(scriptEditor)}', "
				+ $"PreviousCodeEditInstanceId='{previousCodeEditInstanceId}', "
				+ $"CurrentCodeEditInstanceId='{currentCodeEditInstanceId}', "
				+ $"ScriptPath='{DescribeScriptPath(currentScript)}', Result='{result}', Reason='{reason}'"
		);
	}

	private string CaptureInstanceIdForDiagnostics(GodotObject source)
	{
		try
		{
			if (!_debugEnabled())
				return "<debug-disabled>";
		}
		catch
		{
			return "<debug-state-read-failed>";
		}

		return DescribeInstanceIdValue(source);
	}

	private void Trace(string operation, string details = "")
	{
		try
		{
			if (!_debugEnabled())
				return;

			_debugLog(operation ?? "", AppendBindingIdentity(details ?? ""));
		}
		catch
		{
		}
	}

	private void Trace(string operation, Func<string> detailsFactory)
	{
		try
		{
			if (!_debugEnabled())
				return;

			string details;
			try
			{
				details = detailsFactory?.Invoke() ?? "";
			}
			catch (Exception exception)
			{
				details =
					$"DiagnosticReadFailed: {exception.GetType().Name}: {exception.Message}";
			}

			_debugLog(operation ?? "", AppendBindingIdentity(details));
		}
		catch
		{
		}
	}

	private string AppendBindingIdentity(string details)
	{
		string identity = $"BindingInstanceToken='{_bindingInstanceToken}'";
		return string.IsNullOrWhiteSpace(details) ? identity : $"{identity}, {details}";
	}

	private static string DescribeInstanceIdValue(GodotObject source)
	{
		try
		{
			if (source == null)
				return "<null>";
			if (!GodotObject.IsInstanceValid(source))
				return "<invalid>";

			return source.GetInstanceId().ToString();
		}
		catch (Exception exception)
		{
			return $"<read-failed:{exception.GetType().Name}>";
		}
	}

	private static string DescribeGodotObject(string name, GodotObject source)
	{
		string safeName = string.IsNullOrWhiteSpace(name) ? "GodotObject" : name;

		try
		{
			if (source == null)
				return $"{safeName}=<null>, {safeName}Valid='False'";

			bool isValid = GodotObject.IsInstanceValid(source);
			if (!isValid)
				return $"{safeName}=<invalid>, {safeName}Valid='False'";

			return $"{safeName}InstanceId='{source.GetInstanceId()}', {safeName}Valid='True'";
		}
		catch (Exception exception)
		{
			return
				$"{safeName}DiagnosticReadFailed='{exception.GetType().Name}: {exception.Message}'";
		}
	}

	private static string DescribeScript(Script script)
	{
		try
		{
			if (!IsValidGodotObject(script))
				return "Script=<null-or-invalid>, ScriptValid='False'";

			return
				$"ScriptInstanceId='{script.GetInstanceId()}', ScriptValid='True', ScriptPath='{script.ResourcePath}'";
		}
		catch (Exception exception)
		{
			return $"ScriptDiagnosticReadFailed='{exception.GetType().Name}: {exception.Message}'";
		}
	}

	private static string DescribeScriptPath(Script script)
	{
		try
		{
			return IsValidGodotObject(script) ? script.ResourcePath ?? "" : "";
		}
		catch
		{
			return "<read-failed>";
		}
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
