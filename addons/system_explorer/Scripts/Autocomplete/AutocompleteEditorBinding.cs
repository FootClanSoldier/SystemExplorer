#if TOOLS
using Godot;
using System;
using SystemExplorer.Autocomplete.Styling;
using SystemExplorer.EditorIntegration.ScriptEditing;

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
	private readonly ScriptEditorLifecycleCoordinator _scriptEditorLifecycleCoordinator;
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
	private EditorBindingLease? _bindingLease;

	internal AutocompleteEditorBinding(
		string managedAssemblyGeneration,
		bool cancelNativeCompletionOnRebind,
		ScriptEditorLifecycleCoordinator scriptEditorLifecycleCoordinator,
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
		_scriptEditorLifecycleCoordinator =
			scriptEditorLifecycleCoordinator
			?? throw new ArgumentNullException(nameof(scriptEditorLifecycleCoordinator));
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
				"Enabled='False', Scope='ResolveCodeEditBindingInitialDisconnect', SignalDisconnectRetained='True', ShutdownCancellationRetained='True', CompletionCoordinatorCancellationRetained='True'"
			);
		}
	}

	internal bool EnsureLifecycleCurrent(out bool bindingResolutionRequired)
	{
		bindingResolutionRequired = false;
		ScriptEditor currentScriptEditor = _scriptEditorProvider();

		if (!IsValidGodotObject(currentScriptEditor))
		{
			Trace(
				"C# autocomplete binding lifecycle anomaly",
				() => $"Reason='Current ScriptEditor invalid', {DescribeGodotObject("CurrentScriptEditor", currentScriptEditor)}"
			);
			DisconnectScriptEditor();
			_scriptEditor = null;
			return false;
		}

		bool scriptEditorIdentityChanged =
			IsValidGodotObject(_scriptEditor)
			&& _scriptEditor.GetInstanceId() != currentScriptEditor.GetInstanceId();

		if (scriptEditorIdentityChanged)
		{
			bindingResolutionRequired = true;
			Trace(
				"C# autocomplete binding ScriptEditor identity changed",
				() => $"{DescribeGodotObject("PreviousScriptEditor", _scriptEditor)}, {DescribeGodotObject("CurrentScriptEditor", currentScriptEditor)}"
			);
			DisconnectScriptEditor();
		}

		_scriptEditor = currentScriptEditor;

		bool hasScriptChangedSignal = currentScriptEditor.HasSignal(ScriptChangedSignalName);
		if (!hasScriptChangedSignal)
		{
			Trace(
				"C# autocomplete binding lifecycle anomaly",
				() => $"Reason='ScriptChanged signal unavailable', {DescribeGodotObject("ScriptEditor", currentScriptEditor)}"
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
				() => $"Reason='ScriptChanged connect failed', {DescribeGodotObject("ScriptEditor", currentScriptEditor)}"
			);
			return false;
		}

		return true;
	}

	internal bool ResolveCodeEditBinding(
		long scriptTransitionId,
		long hostInstanceToken,
		Action<string, ScriptEditor, CodeEdit> diagnosticPhase = null,
		Action<string, string> nativeBoundaryDiagnosticPhase = null
	)
	{
		if (
			!_scriptEditorLifecycleCoordinator.CanResolveBinding(
				_managedAssemblyGeneration,
				scriptTransitionId
			)
		)
		{
			Trace(
				"C# autocomplete binding resolution rejected",
				$"Reason='StaleLifecycleAuthorityBeforeEditorAccess', ScriptTransitionId='{scriptTransitionId}', HostInstanceToken='{hostInstanceToken}'"
			);
			return false;
		}

		string previousCodeEditInstanceId = CaptureInstanceIdForDiagnostics(_codeEdit);
		DisconnectCodeEdit(cancelCompletion: _cancelNativeCompletionOnRebind);

		if (
			!_scriptEditorLifecycleCoordinator.CanResolveBinding(
				_managedAssemblyGeneration,
				scriptTransitionId
			)
		)
		{
			Trace(
				"C# autocomplete binding resolution rejected",
				$"Reason='StaleLifecycleAuthorityAfterOutgoingDisconnect', ScriptTransitionId='{scriptTransitionId}', HostInstanceToken='{hostInstanceToken}'"
			);
			return false;
		}

		ScriptEditor scriptEditor = _scriptEditor;

		if (!IsValidGodotObject(scriptEditor))
		{
			Trace(
				"C# autocomplete binding rebind failed",
				() => $"Reason='Invalid ScriptEditor', PreviousCodeEditInstanceId='{previousCodeEditInstanceId}', ScriptTransitionId='{scriptTransitionId}', {DescribeGodotObject("ScriptEditor", scriptEditor)}"
			);
			return false;
		}

		InvokeDiagnosticPhase(
			diagnosticPhase,
			"ResolveCodeEditBinding.GetCurrentScript",
			scriptEditor,
			codeEdit: null
		);
		Script currentScript = scriptEditor.GetCurrentScript();

		if (
			!_scriptEditorLifecycleCoordinator.CanResolveBinding(
				_managedAssemblyGeneration,
				scriptTransitionId
			)
		)
		{
			Trace(
				"C# autocomplete binding resolution rejected",
				$"Reason='StaleLifecycleAuthorityAfterGetCurrentScript', ScriptTransitionId='{scriptTransitionId}', HostInstanceToken='{hostInstanceToken}'"
			);
			return false;
		}

		InvokeDiagnosticPhase(
			diagnosticPhase,
			"ResolveCodeEditBinding.GetCurrentEditor",
			scriptEditor,
			codeEdit: null
		);
		ScriptEditorBase currentEditor = scriptEditor.GetCurrentEditor();
		string currentScriptPath = ScriptPathUtility.Normalize(
			IsValidGodotObject(currentScript) ? currentScript.ResourcePath : ""
		);

		if (!IsCSharpScript(currentScript))
		{
			bool completedWithoutBinding =
				_scriptEditorLifecycleCoordinator.TryCompleteWithoutBinding(
					_managedAssemblyGeneration,
					scriptTransitionId,
					currentScriptPath
				);
			if (!completedWithoutBinding)
			{
				Trace(
					"C# autocomplete binding resolution rejected",
					$"Reason='StaleLifecycleAuthorityForNonCSharpResolution', ScriptTransitionId='{scriptTransitionId}', HostInstanceToken='{hostInstanceToken}', ScriptPath='{currentScriptPath}'"
				);
				return false;
			}

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
				() => $"Reason='Invalid ScriptEditorBase', PreviousCodeEditInstanceId='{previousCodeEditInstanceId}', ScriptTransitionId='{scriptTransitionId}', {DescribeScript(currentScript)}, {DescribeGodotObject("ScriptEditorBase", currentEditor)}"
			);
			return false;
		}

		if (
			!_scriptEditorLifecycleCoordinator.CanResolveBinding(
				_managedAssemblyGeneration,
				scriptTransitionId
			)
		)
		{
			Trace(
				"C# autocomplete binding resolution rejected",
				$"Reason='StaleLifecycleAuthorityBeforeGetBaseEditor', ScriptTransitionId='{scriptTransitionId}', HostInstanceToken='{hostInstanceToken}'"
			);
			return false;
		}

		InvokeDiagnosticPhase(
			diagnosticPhase,
			"ResolveCodeEditBinding.GetBaseEditor",
			scriptEditor,
			codeEdit: null
		);
		InvokeNativeBoundaryDiagnosticPhase(
			nativeBoundaryDiagnosticPhase,
			"ResolveCodeEditBinding.GetBaseEditor.Call.Begin"
		);
		Control baseEditor = currentEditor.GetBaseEditor();
		InvokeNativeBoundaryDiagnosticPhase(
			nativeBoundaryDiagnosticPhase,
			"ResolveCodeEditBinding.GetBaseEditor.Call.Returned"
		);

		bool candidateIsCodeEdit = baseEditor is CodeEdit;
		InvokeNativeBoundaryDiagnosticPhase(
			nativeBoundaryDiagnosticPhase,
			"ResolveCodeEditBinding.CandidateTypeCheck.Returned",
			$"IsCodeEdit='{candidateIsCodeEdit}'"
		);
		if (!candidateIsCodeEdit)
		{
			Trace(
				"C# autocomplete binding rebind failed",
				() => $"Reason='Base editor is not a valid CodeEdit', PreviousCodeEditInstanceId='{previousCodeEditInstanceId}', ScriptTransitionId='{scriptTransitionId}', {DescribeScript(currentScript)}, {DescribeGodotObject("BaseEditor", baseEditor)}"
			);
			return false;
		}

		CodeEdit codeEdit = (CodeEdit)baseEditor;
		InvokeNativeBoundaryDiagnosticPhase(
			nativeBoundaryDiagnosticPhase,
			"ResolveCodeEditBinding.CandidateIsInstanceValid.Begin"
		);
		bool candidateValid = IsValidGodotObject(codeEdit);
		InvokeNativeBoundaryDiagnosticPhase(
			nativeBoundaryDiagnosticPhase,
			"ResolveCodeEditBinding.CandidateIsInstanceValid.Returned",
			$"Result='{candidateValid}'"
		);
		if (!candidateValid)
		{
			Trace(
				"C# autocomplete binding rebind failed",
				() => $"Reason='Base editor is not a valid CodeEdit', PreviousCodeEditInstanceId='{previousCodeEditInstanceId}', ScriptTransitionId='{scriptTransitionId}', {DescribeScript(currentScript)}, {DescribeGodotObject("BaseEditor", baseEditor)}"
			);
			return false;
		}

		bool candidateAuthority =
			_scriptEditorLifecycleCoordinator.CanResolveBinding(
				_managedAssemblyGeneration,
				scriptTransitionId
			);
		InvokeNativeBoundaryDiagnosticPhase(
			nativeBoundaryDiagnosticPhase,
			"ResolveCodeEditBinding.CandidateAuthority.Checked",
			$"Result='{candidateAuthority}'"
		);
		if (!candidateAuthority)
		{
			Trace(
				"C# autocomplete binding resolution rejected",
				$"Reason='StaleLifecycleAuthorityBeforeCandidateBinding', ScriptTransitionId='{scriptTransitionId}', HostInstanceToken='{hostInstanceToken}'"
			);
			return false;
		}

		bool allowFreshNativeOwnershipMarkerWrite = true;
		InvokeNativeBoundaryDiagnosticPhase(
			nativeBoundaryDiagnosticPhase,
			"ResolveCodeEditBinding.NativeOwnershipRecovery.Begin"
		);
		RecoverStaleNativeOwnershipIfNeeded(
			codeEdit,
			ref allowFreshNativeOwnershipMarkerWrite,
			nativeBoundaryDiagnosticPhase
		);
		InvokeNativeBoundaryDiagnosticPhase(
			nativeBoundaryDiagnosticPhase,
			"ResolveCodeEditBinding.NativeOwnershipRecovery.Returned",
			$"AllowFreshNativeOwnershipMarkerWrite='{allowFreshNativeOwnershipMarkerWrite}'"
		);

		InvokeNativeBoundaryDiagnosticPhase(
			nativeBoundaryDiagnosticPhase,
			"ResolveCodeEditBinding.LegacyBindDiagnostic.Begin"
		);
		InvokeDiagnosticPhase(
			diagnosticPhase,
			"ResolveCodeEditBinding.BindCodeEdit",
			scriptEditor,
			codeEdit
		);
		InvokeNativeBoundaryDiagnosticPhase(
			nativeBoundaryDiagnosticPhase,
			"ResolveCodeEditBinding.LegacyBindDiagnostic.Returned"
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
				() => $"Reason='TextChanged connect failed', PreviousCodeEditInstanceId='{previousCodeEditInstanceId}', ScriptTransitionId='{scriptTransitionId}', {DescribeGodotObject("CodeEdit", codeEdit)}, {DescribeScript(currentScript)}"
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
			RollbackResolvedCodeEditCandidate(
				codeEdit,
				textChangedConnected: true,
				completionRequestedConnected: false,
				guiInputConnected: false,
				restorePresentationOwnership: false
			);
			Trace(
				"C# autocomplete binding rebind failed",
				() => $"Reason='CodeCompletionRequested connect failed', PreviousCodeEditInstanceId='{previousCodeEditInstanceId}', ScriptTransitionId='{scriptTransitionId}', {DescribeGodotObject("CodeEdit", codeEdit)}, {DescribeScript(currentScript)}"
			);
			return false;
		}

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
				() => $"Reason='GuiInput connect failed; non-critical', ScriptTransitionId='{scriptTransitionId}', {DescribeGodotObject("CodeEdit", codeEdit)}, {DescribeScript(currentScript)}"
			);
		}

		if (
			!_scriptEditorLifecycleCoordinator.CanResolveBinding(
				_managedAssemblyGeneration,
				scriptTransitionId
			)
		)
		{
			RollbackResolvedCodeEditCandidate(
				codeEdit,
				textChangedConnected: true,
				completionRequestedConnected: true,
				guiInputConnected: guiInputConnected,
				restorePresentationOwnership: false
			);
			Trace(
				"C# autocomplete binding resolution rejected",
				$"Reason='StaleLifecycleAuthorityAfterSignalConnect', ScriptTransitionId='{scriptTransitionId}', HostInstanceToken='{hostInstanceToken}'"
			);
			return false;
		}

		bool prefixApplied = _completionPrefixController.Apply(codeEdit);
		if (!prefixApplied)
		{
			RollbackResolvedCodeEditCandidate(
				codeEdit,
				textChangedConnected: true,
				completionRequestedConnected: true,
				guiInputConnected: guiInputConnected,
				restorePresentationOwnership: true
			);
			Trace(
				"C# autocomplete binding rebind failed",
				() => $"Reason='CompletionPrefix Apply failed', PreviousCodeEditInstanceId='{previousCodeEditInstanceId}', ScriptTransitionId='{scriptTransitionId}', {DescribeGodotObject("CodeEdit", codeEdit)}, {DescribeScript(currentScript)}"
			);
			return false;
		}

		try
		{
			_themeController.Apply(codeEdit);
			if (allowFreshNativeOwnershipMarkerWrite)
				PublishFreshNativeOwnershipMarkerBestEffort(codeEdit);

			if (
				!_scriptEditorLifecycleCoordinator.CanResolveBinding(
					_managedAssemblyGeneration,
					scriptTransitionId
				)
			)
			{
				RollbackResolvedCodeEditCandidate(
					codeEdit,
					textChangedConnected: true,
					completionRequestedConnected: true,
					guiInputConnected: guiInputConnected,
					restorePresentationOwnership: true
				);
				Trace(
					"C# autocomplete binding resolution rejected",
					$"Reason='StaleLifecycleAuthorityAfterPresentationApply', ScriptTransitionId='{scriptTransitionId}', HostInstanceToken='{hostInstanceToken}'"
				);
				return false;
			}

			ScriptEditorBindingIdentity identity = new(
				scriptEditor.GetInstanceId(),
				currentEditor.GetInstanceId(),
				codeEdit.GetInstanceId(),
				currentScriptPath
			);
			if (
				!_scriptEditorLifecycleCoordinator.TryCommitBinding(
					_managedAssemblyGeneration,
					hostInstanceToken,
					scriptTransitionId,
					identity,
					out EditorBindingLease lease
				)
			)
			{
				RollbackResolvedCodeEditCandidate(
					codeEdit,
					textChangedConnected: true,
					completionRequestedConnected: true,
					guiInputConnected: guiInputConnected,
					restorePresentationOwnership: true
				);
				Trace(
					"C# autocomplete binding resolution rejected",
					$"Reason='LifecycleCommitRejected', ScriptTransitionId='{scriptTransitionId}', HostInstanceToken='{hostInstanceToken}', CodeEditInstanceId='{identity.CodeEditInstanceId}', ScriptPath='{identity.ScriptResourcePath}'"
				);
				return false;
			}

			_codeEdit = codeEdit;
			_bindingLease = lease;
			TraceRebindSummary(
				scriptEditor,
				previousCodeEditInstanceId,
				codeEdit,
				currentScript,
				result: true,
				reason: $"Bound C# CodeEdit; ScriptTransitionId={lease.ScriptTransitionId}; BindingEpoch={lease.BindingEpoch}"
			);
			return true;
		}
		catch (Exception exception)
		{
			Trace(
				"C# autocomplete binding Theme Apply failed",
				() => $"ScriptTransitionId='{scriptTransitionId}', {DescribeGodotObject("CodeEdit", codeEdit)}, Exception='{exception}'"
			);
			RollbackResolvedCodeEditCandidate(
				codeEdit,
				textChangedConnected: true,
				completionRequestedConnected: true,
				guiInputConnected: guiInputConnected,
				restorePresentationOwnership: true
			);
			throw;
		}
	}

	internal bool TryGetActiveCodeEdit(
		out CodeEdit codeEdit,
		out string scriptPath,
		Action<string, ScriptEditor, CodeEdit> diagnosticPhase = null
	)
	{
		codeEdit = null;
		scriptPath = "";
		if (!_bindingLease.HasValue)
			return false;

		EditorBindingLease lease = _bindingLease.Value;
		if (!_scriptEditorLifecycleCoordinator.IsCurrentStableBinding(lease))
			return false;

		CodeEdit boundCodeEdit = _codeEdit;
		ScriptEditor scriptEditor = _scriptEditor;
		if (!IsValidGodotObject(boundCodeEdit) || !IsValidGodotObject(scriptEditor))
			return false;

		if (
			scriptEditor.GetInstanceId() != lease.ScriptEditorInstanceId
			|| boundCodeEdit.GetInstanceId() != lease.CodeEditInstanceId
		)
		{
			return false;
		}

		InvokeDiagnosticPhase(
			diagnosticPhase,
			"ValidateActiveCodeEdit.GetCurrentScript",
			scriptEditor,
			boundCodeEdit
		);
		Script currentScript = scriptEditor.GetCurrentScript();
		InvokeDiagnosticPhase(
			diagnosticPhase,
			"ValidateActiveCodeEdit.GetCurrentEditor",
			scriptEditor,
			boundCodeEdit
		);
		ScriptEditorBase currentEditor = scriptEditor.GetCurrentEditor();

		if (!IsCSharpScript(currentScript) || !IsValidGodotObject(currentEditor))
			return false;
		if (currentEditor.GetInstanceId() != lease.ScriptEditorBaseInstanceId)
			return false;

		InvokeDiagnosticPhase(
			diagnosticPhase,
			"ValidateActiveCodeEdit.GetBaseEditor",
			scriptEditor,
			boundCodeEdit
		);
		Control baseEditor = currentEditor.GetBaseEditor();

		if (
			baseEditor is not CodeEdit currentCodeEdit
			|| !IsValidGodotObject(currentCodeEdit)
			|| currentCodeEdit.GetInstanceId() != lease.CodeEditInstanceId
		)
		{
			return false;
		}

		string normalizedScriptPath = ScriptPathUtility.Normalize(currentScript.ResourcePath);
		if (
			!string.Equals(
				normalizedScriptPath,
				lease.ScriptResourcePath,
				StringComparison.OrdinalIgnoreCase
			)
		)
		{
			return false;
		}

		codeEdit = boundCodeEdit;
		scriptPath = normalizedScriptPath;
		InvokeDiagnosticPhase(
			diagnosticPhase,
			"ValidateActiveCodeEdit.Completed",
			scriptEditor,
			boundCodeEdit
		);
		return true;
	}

	private void RollbackResolvedCodeEditCandidate(
		CodeEdit codeEdit,
		bool textChangedConnected,
		bool completionRequestedConnected,
		bool guiInputConnected,
		bool restorePresentationOwnership
	)
	{
		if (IsValidGodotObject(codeEdit))
		{
			if (textChangedConnected)
			{
				_disconnectPluginSignal(
					codeEdit,
					TextEdit.SignalName.TextChanged,
					_textChangedMethodName,
					$"{TextChangedDescription} resolution rollback"
				);
			}

			if (completionRequestedConnected)
			{
				_disconnectPluginSignal(
					codeEdit,
					CodeEdit.SignalName.CodeCompletionRequested,
					_completionRequestedMethodName,
					$"{CompletionRequestedDescription} resolution rollback"
				);
			}

			if (guiInputConnected)
			{
				_disconnectPluginSignal(
					codeEdit,
					Control.SignalName.GuiInput,
					_guiInputMethodName,
					$"{GuiInputDescription} resolution rollback"
				);
			}
		}

		if (restorePresentationOwnership)
		{
			_completionPrefixController.Restore(codeEdit);
			_themeController.Restore(codeEdit);
			ClearCurrentGenerationNativeOwnershipMarkerBestEffort(codeEdit);
		}
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
		_bindingLease = null;
	}

	private void RecoverStaleNativeOwnershipIfNeeded(
		CodeEdit codeEdit,
		ref bool allowFreshNativeOwnershipMarkerWrite,
		Action<string, string> nativeBoundaryDiagnosticPhase = null
	)
	{
		InvokeNativeBoundaryDiagnosticPhase(
			nativeBoundaryDiagnosticPhase,
			"NativeOwnershipRecovery.Inspect.Begin"
		);
		AutocompleteCodeEditNativeOwnershipBridge.MarkerReadStatus markerStatus =
			_nativeOwnershipBridge.Inspect(
				codeEdit,
				out AutocompleteCodeEditNativeOwnershipBridge.OwnershipState state,
				out string failureDetail,
				nativeBoundaryDiagnosticPhase
			);
		InvokeNativeBoundaryDiagnosticPhase(
			nativeBoundaryDiagnosticPhase,
			"NativeOwnershipRecovery.Inspect.Returned",
			$"Status='{markerStatus}'"
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

		InvokeNativeBoundaryDiagnosticPhase(
			nativeBoundaryDiagnosticPhase,
			"NativeOwnershipRecovery.PrefixRestore.Begin"
		);
		bool prefixRestored =
			!state.PrefixOwned
			|| _completionPrefixController.TryRestoreOwnedPrefixesFromNativeBridge(
				codeEdit,
				state.PreviousCodeCompletionPrefixes
			);
		InvokeNativeBoundaryDiagnosticPhase(
			nativeBoundaryDiagnosticPhase,
			"NativeOwnershipRecovery.PrefixRestore.Returned",
			$"Result='{prefixRestored}'"
		);

		InvokeNativeBoundaryDiagnosticPhase(
			nativeBoundaryDiagnosticPhase,
			"NativeOwnershipRecovery.ThemeRestore.Begin"
		);
		bool completionExistingColorRestored =
			!state.CompletionExistingColorOwned
			|| _themeController.TryRestoreCompletionExistingColorFromNativeBridge(
				codeEdit,
				state.HadPreviousCompletionExistingColorOverride,
				state.PreviousCompletionExistingColor
			);
		InvokeNativeBoundaryDiagnosticPhase(
			nativeBoundaryDiagnosticPhase,
			"NativeOwnershipRecovery.ThemeRestore.Returned",
			$"Result='{completionExistingColorRestored}'"
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

		InvokeNativeBoundaryDiagnosticPhase(
			nativeBoundaryDiagnosticPhase,
			"NativeOwnershipRecovery.MarkerClear.Begin"
		);
		bool markerCleared = _nativeOwnershipBridge.TryClearVerifiedMarker(
			codeEdit,
			state,
			out string clearFailureDetail,
			nativeBoundaryDiagnosticPhase
		);
		InvokeNativeBoundaryDiagnosticPhase(
			nativeBoundaryDiagnosticPhase,
			"NativeOwnershipRecovery.MarkerClear.Returned",
			$"Result='{markerCleared}'"
		);
		if (!markerCleared)
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

	private static void InvokeNativeBoundaryDiagnosticPhase(
		Action<string, string> nativeBoundaryDiagnosticPhase,
		string phase,
		string details = ""
	)
	{
		try
		{
			nativeBoundaryDiagnosticPhase?.Invoke(phase ?? "", details ?? "");
		}
		catch
		{
			// Operation-local diagnostics must never affect binding control flow.
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
		if (result)
			return;

		string currentCodeEditInstanceId = CaptureInstanceIdForDiagnostics(currentCodeEdit);

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
		ScriptEditorLifecycleSnapshot lifecycle =
			_scriptEditorLifecycleCoordinator.Snapshot;
		string identity =
			$"BindingInstanceToken='{_bindingInstanceToken}', ManagedAssemblyGeneration='{lifecycle.ManagedAssemblyGeneration}', LifecycleState='{lifecycle.State}', ScriptTransitionId='{lifecycle.ScriptTransitionId}', BindingEpoch='{lifecycle.BindingEpoch}', BindingHostInstanceToken='{lifecycle.HostInstanceToken}', BindingScriptEditorInstanceId='{lifecycle.ScriptEditorInstanceId}', BindingScriptEditorBaseInstanceId='{lifecycle.ScriptEditorBaseInstanceId}', BindingCodeEditInstanceId='{lifecycle.CodeEditInstanceId}', BindingScriptResourcePath='{lifecycle.BoundScriptResourcePath}'";
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
