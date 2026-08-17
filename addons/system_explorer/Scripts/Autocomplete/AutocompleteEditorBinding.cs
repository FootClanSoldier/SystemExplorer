#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
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
	private readonly AutocompleteCodeEditMutationCoordinator _codeEditMutationCoordinator;
	private readonly Func<long> _currentReloadReadyEpochProvider;
	private readonly Func<bool> _reloadStabilizationReadyProvider;
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
	private readonly HashSet<ulong> _reloadNeutralizedCodeEditInstanceIds = new();
	private bool _nativeOwnershipMalformedMarkerLogged;
	private bool _nativeOwnershipRestoreFailureLogged;
	private bool _nativeOwnershipMarkerWriteFailureLogged;
	private bool _nativeOwnershipMarkerClearFailureLogged;
	private bool _nativeOwnershipSameGenerationMarkerLogged;
	private bool _nativeOwnershipOwnerMismatchLogged;
	private bool _nativeOwnershipMissingMarkerLogged;
	private bool _nativePresentationOwnershipChangedLogged;

	private ScriptEditor _scriptEditor;
	private CodeEdit _codeEdit;
	private EditorBindingLease? _bindingLease;

	internal bool WasReloadNeutralizedInCurrentGeneration(ulong codeEditInstanceId)
	{
		return codeEditInstanceId != 0
			&& _reloadNeutralizedCodeEditInstanceIds.Contains(codeEditInstanceId);
	}

	internal AutocompleteEditorBinding(
		string managedAssemblyGeneration,
		bool cancelNativeCompletionOnRebind,
		ScriptEditorLifecycleCoordinator scriptEditorLifecycleCoordinator,
		AutocompleteCodeEditMutationCoordinator codeEditMutationCoordinator,
		Func<long> currentReloadReadyEpochProvider,
		Func<bool> reloadStabilizationReadyProvider,
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
		_codeEditMutationCoordinator =
			codeEditMutationCoordinator
			?? throw new ArgumentNullException(nameof(codeEditMutationCoordinator));
		_currentReloadReadyEpochProvider =
			currentReloadReadyEpochProvider
			?? throw new ArgumentNullException(nameof(currentReloadReadyEpochProvider));
		_reloadStabilizationReadyProvider =
			reloadStabilizationReadyProvider
			?? throw new ArgumentNullException(nameof(reloadStabilizationReadyProvider));
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

	internal AutocompleteEditorBindingCandidateObservationKind TryObserveCodeEditBindingCandidate(
		long scriptTransitionId,
		long hostInstanceToken,
		out AutocompleteEditorBindingCandidate candidate
	)
	{
		candidate = default;
		if (
			hostInstanceToken <= 0
			|| !_scriptEditorLifecycleCoordinator.CanResolveBinding(
				_managedAssemblyGeneration,
				scriptTransitionId
			)
		)
		{
			return AutocompleteEditorBindingCandidateObservationKind.Unavailable;
		}

		try
		{
			ScriptEditor scriptEditor = _scriptEditorProvider();
			if (!IsValidGodotObject(scriptEditor))
				return AutocompleteEditorBindingCandidateObservationKind.Unavailable;

			if (
				!_scriptEditorLifecycleCoordinator.CanResolveBinding(
					_managedAssemblyGeneration,
					scriptTransitionId
				)
			)
			{
				return AutocompleteEditorBindingCandidateObservationKind.Unavailable;
			}

			Script currentScript = scriptEditor.GetCurrentScript();
			if (
				!_scriptEditorLifecycleCoordinator.CanResolveBinding(
					_managedAssemblyGeneration,
					scriptTransitionId
				)
			)
			{
				return AutocompleteEditorBindingCandidateObservationKind.Unavailable;
			}

			if (!IsCSharpScript(currentScript))
				return AutocompleteEditorBindingCandidateObservationKind.NonCSharpTarget;

			ScriptEditorBase currentEditor = scriptEditor.GetCurrentEditor();
			if (!IsValidGodotObject(currentEditor))
				return AutocompleteEditorBindingCandidateObservationKind.Unavailable;

			if (
				!_scriptEditorLifecycleCoordinator.CanResolveBinding(
					_managedAssemblyGeneration,
					scriptTransitionId
				)
			)
			{
				return AutocompleteEditorBindingCandidateObservationKind.Unavailable;
			}

			Control baseEditor = currentEditor.GetBaseEditor();
			if (baseEditor is not CodeEdit codeEdit || !IsValidGodotObject(codeEdit))
				return AutocompleteEditorBindingCandidateObservationKind.Unavailable;

			string scriptPath = ScriptPathUtility.Normalize(currentScript.ResourcePath);
			if (string.IsNullOrWhiteSpace(scriptPath))
				return AutocompleteEditorBindingCandidateObservationKind.Unavailable;

			candidate = new AutocompleteEditorBindingCandidate(
				_managedAssemblyGeneration,
				hostInstanceToken,
				scriptTransitionId,
				scriptEditor.GetInstanceId(),
				currentEditor.GetInstanceId(),
				codeEdit.GetInstanceId(),
				scriptPath
			).Normalized();

			if (
				!candidate.IsValid
				|| !_scriptEditorLifecycleCoordinator.CanResolveBinding(
					_managedAssemblyGeneration,
					scriptTransitionId
				)
			)
			{
				candidate = default;
				return AutocompleteEditorBindingCandidateObservationKind.Unavailable;
			}

			return AutocompleteEditorBindingCandidateObservationKind.Candidate;
		}
		catch
		{
			candidate = default;
			return AutocompleteEditorBindingCandidateObservationKind.Unavailable;
		}
	}

	private bool IsReloadReadyEpochCurrent(long reloadReadyEpoch)
	{
		return reloadReadyEpoch > 0
			&& reloadReadyEpoch == _currentReloadReadyEpochProvider();
	}

	internal bool ResolveCodeEditBinding(
		long scriptTransitionId,
		long hostInstanceToken,
		long reloadReadyEpoch,
		AutocompleteEditorBindingCandidate? requiredActivationCandidate,
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

		if (!IsReloadReadyEpochCurrent(reloadReadyEpoch))
		{
			Trace(
				"C# autocomplete binding resolution rejected",
				$"Reason='StaleReloadReadyEpochBeforeEditorMutation', ScriptTransitionId='{scriptTransitionId}', HostInstanceToken='{hostInstanceToken}', ReloadReadyEpoch='{reloadReadyEpoch}', CurrentReloadReadyEpoch='{_currentReloadReadyEpochProvider()}'"
			);
			return false;
		}

		if (requiredActivationCandidate.HasValue)
		{
			AutocompleteEditorBindingCandidate requiredCandidate =
				requiredActivationCandidate.Value.Normalized();
			if (
				!requiredCandidate.IsValid
				|| !string.Equals(
					requiredCandidate.ManagedAssemblyGeneration,
					_managedAssemblyGeneration,
					StringComparison.Ordinal
				)
				|| requiredCandidate.HostInstanceToken != hostInstanceToken
				|| requiredCandidate.ScriptTransitionId != scriptTransitionId
			)
			{
				Trace(
					"C# autocomplete binding resolution rejected",
					$"Reason='InvalidRequiredActivationCandidate', ScriptTransitionId='{scriptTransitionId}', HostInstanceToken='{hostInstanceToken}', ReloadReadyEpoch='{reloadReadyEpoch}'"
				);
				return false;
			}

			AutocompleteEditorBindingCandidateObservationKind observationKind =
				TryObserveCodeEditBindingCandidate(
					scriptTransitionId,
					hostInstanceToken,
					out AutocompleteEditorBindingCandidate observedCandidate
				);
			if (
				observationKind != AutocompleteEditorBindingCandidateObservationKind.Candidate
				|| !requiredCandidate.AuthorityEquals(observedCandidate)
				|| !IsReloadReadyEpochCurrent(reloadReadyEpoch)
			)
			{
				Trace(
					"C# autocomplete binding resolution rejected",
					$"Reason='StabilizedCandidateMismatchBeforeEditorMutation', ScriptTransitionId='{scriptTransitionId}', HostInstanceToken='{hostInstanceToken}', ReloadReadyEpoch='{reloadReadyEpoch}', ObservationKind='{observationKind}'"
				);
				return false;
			}
		}

		if (
			!_codeEditMutationCoordinator.TryBeginBindingActivation(
				hostInstanceToken,
				scriptTransitionId,
				reloadReadyEpoch,
				out AutocompleteBindingActivationTransactionLease activationLease
			)
		)
		{
			Trace(
				"C# autocomplete binding resolution rejected",
				$"Reason='MutationAuthorityUnavailable', ManagedAssemblyGeneration='{_managedAssemblyGeneration}', HostInstanceToken='{hostInstanceToken}', ScriptTransitionId='{scriptTransitionId}', ReloadReadyEpoch='{reloadReadyEpoch}'"
			);
			return false;
		}

		string activationOutcome = "Rejected";
		EditorBindingLease? committedBindingLease = null;
		CodeEdit resolvedCodeEdit = null;
		bool textChangedConnected = false;
		bool completionRequestedConnected = false;
		bool guiInputConnected = false;
		bool presentationOwnershipTouched = false;
		bool candidateRollbackCompleted = false;
		EditorBindingReservation? bindingReservation = null;
		bool bindingReservationCommitted = false;

		try
		{
			string previousCodeEditInstanceId = CaptureInstanceIdForDiagnostics(_codeEdit);
			bool outgoingCleanupAuthorityRetained =
				DisconnectCodeEditForBindingActivation(activationLease);

			if (
				!outgoingCleanupAuthorityRetained
				|| !_codeEditMutationCoordinator.IsBindingActivationForwardAuthorityCurrent(
					activationLease
				)
			)
			{
				Trace(
					"C# autocomplete binding resolution rejected",
					$"Reason='StaleLifecycleOrReloadAuthorityAfterOutgoingDisconnect', ScriptTransitionId='{scriptTransitionId}', HostInstanceToken='{hostInstanceToken}', ReloadReadyEpoch='{reloadReadyEpoch}'"
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

			if (!_codeEditMutationCoordinator.IsBindingActivationForwardAuthorityCurrent(activationLease))
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

			if (!_codeEditMutationCoordinator.IsBindingActivationForwardAuthorityCurrent(activationLease))
			{
				Trace(
					"C# autocomplete binding resolution rejected",
					$"Reason='StaleLifecycleOrReloadAuthorityAfterCurrentEditorResolution', ScriptTransitionId='{scriptTransitionId}', HostInstanceToken='{hostInstanceToken}', ReloadReadyEpoch='{reloadReadyEpoch}'"
				);
				return false;
			}

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
				activationOutcome = "CompletedWithoutBinding";
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

			if (!_codeEditMutationCoordinator.IsBindingActivationForwardAuthorityCurrent(activationLease))
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
			resolvedCodeEdit = codeEdit;
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
				_codeEditMutationCoordinator.IsBindingActivationForwardAuthorityCurrent(
					activationLease
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
					$"Reason='StaleLifecycleOrReloadAuthorityBeforeCandidateBinding', ScriptTransitionId='{scriptTransitionId}', HostInstanceToken='{hostInstanceToken}', ReloadReadyEpoch='{reloadReadyEpoch}'"
				);
				return false;
			}

			AutocompleteEditorBindingCandidate resolvedCandidate = new(
				_managedAssemblyGeneration,
				hostInstanceToken,
				scriptTransitionId,
				scriptEditor.GetInstanceId(),
				currentEditor.GetInstanceId(),
				codeEdit.GetInstanceId(),
				currentScriptPath
			);
			if (
				requiredActivationCandidate.HasValue
				&& !requiredActivationCandidate.Value.AuthorityEquals(resolvedCandidate)
			)
			{
				Trace(
					"C# autocomplete binding resolution rejected",
					$"Reason='StabilizedCandidateChangedBeforeNativeActivation', ScriptTransitionId='{scriptTransitionId}', HostInstanceToken='{hostInstanceToken}', ReloadReadyEpoch='{reloadReadyEpoch}', CodeEditInstanceId='{resolvedCandidate.CodeEditInstanceId}', ScriptPath='{resolvedCandidate.ScriptResourcePath}'"
				);
				return false;
			}

			if (!_codeEditMutationCoordinator.IsBindingActivationForwardAuthorityCurrent(activationLease))
			{
				Trace(
					"C# autocomplete binding resolution rejected",
					$"Reason='StaleLifecycleOrReloadAuthorityBeforeNativeOwnershipInspection', ScriptTransitionId='{scriptTransitionId}', HostInstanceToken='{hostInstanceToken}', ReloadReadyEpoch='{reloadReadyEpoch}'"
				);
				return false;
			}

			NativeOwnershipInspectionKind nativeOwnershipInspectionKind = InspectNativeOwnership(
				codeEdit,
				out AutocompleteCodeEditNativeOwnershipBridge.OwnershipState nativeOwnershipState,
				out string nativeOwnershipFailureDetail,
				nativeBoundaryDiagnosticPhase
			);

			if (nativeOwnershipInspectionKind == NativeOwnershipInspectionKind.CrossGenerationOrphan)
			{
				TraceNativeOwnershipOrphanDetected(nativeOwnershipState);
				if (
					!TryNeutralizeCrossGenerationCodeEditCompletion(
						activationLease,
						codeEdit,
						nativeOwnershipState,
						resolvedCandidate.CodeEditInstanceId,
						resolvedCandidate.ScriptResourcePath
					)
				)
				{
					Trace(
						"C# autocomplete binding resolution rejected",
						$"Reason='StaleLifecycleOrReloadAuthorityAfterReloadCodeEditCompletionNeutralization', ScriptTransitionId='{scriptTransitionId}', HostInstanceToken='{hostInstanceToken}', ReloadReadyEpoch='{reloadReadyEpoch}'"
					);
					return false;
				}
			}

			InvokeNativeBoundaryDiagnosticPhase(
				nativeBoundaryDiagnosticPhase,
				"ResolveCodeEditBinding.NativeOwnershipRecovery.Begin"
			);
			bool nativeOwnershipRecoveryAuthorityRetained = RecoverStaleNativeOwnershipIfNeeded(
				activationLease,
				codeEdit,
				nativeOwnershipInspectionKind,
				nativeOwnershipState,
				nativeOwnershipFailureDetail,
				nativeBoundaryDiagnosticPhase
			);
			InvokeNativeBoundaryDiagnosticPhase(
				nativeBoundaryDiagnosticPhase,
				"ResolveCodeEditBinding.NativeOwnershipRecovery.Returned",
				$"InspectionKind='{nativeOwnershipInspectionKind}'"
			);
			if (
				!nativeOwnershipRecoveryAuthorityRetained
				|| !_codeEditMutationCoordinator.IsBindingActivationForwardAuthorityCurrent(
					activationLease
				)
			)
			{
				Trace(
					"C# autocomplete binding resolution rejected",
					$"Reason='StaleLifecycleOrReloadAuthorityAfterNativeOwnershipRecovery', ScriptTransitionId='{scriptTransitionId}', HostInstanceToken='{hostInstanceToken}', ReloadReadyEpoch='{reloadReadyEpoch}'"
				);
				return false;
			}

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

			textChangedConnected = _connectPluginSignal(
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

			if (!_codeEditMutationCoordinator.IsBindingActivationForwardAuthorityCurrent(activationLease))
			{
				RollbackResolvedCodeEditCandidate(
					activationLease,
					codeEdit,
					textChangedConnected,
					completionRequestedConnected,
					guiInputConnected,
					restorePresentationOwnership: false
				);
				candidateRollbackCompleted = true;
				Trace(
					"C# autocomplete binding resolution rejected",
					$"Reason='StaleLifecycleOrReloadAuthorityAfterTextChangedConnect', ScriptTransitionId='{scriptTransitionId}', HostInstanceToken='{hostInstanceToken}', ReloadReadyEpoch='{reloadReadyEpoch}'"
				);
				return false;
			}

			completionRequestedConnected = _connectPluginSignal(
				codeEdit,
				CodeEdit.SignalName.CodeCompletionRequested,
				_completionRequestedMethodName,
				CompletionRequestedDescription
			);

			if (!completionRequestedConnected)
			{
				RollbackResolvedCodeEditCandidate(
					activationLease,
					codeEdit,
					textChangedConnected,
					completionRequestedConnected,
					guiInputConnected,
					restorePresentationOwnership: false
				);
				candidateRollbackCompleted = true;
				Trace(
					"C# autocomplete binding rebind failed",
					() => $"Reason='CodeCompletionRequested connect failed', PreviousCodeEditInstanceId='{previousCodeEditInstanceId}', ScriptTransitionId='{scriptTransitionId}', {DescribeGodotObject("CodeEdit", codeEdit)}, {DescribeScript(currentScript)}"
				);
				return false;
			}

			if (!_codeEditMutationCoordinator.IsBindingActivationForwardAuthorityCurrent(activationLease))
			{
				RollbackResolvedCodeEditCandidate(
					activationLease,
					codeEdit,
					textChangedConnected,
					completionRequestedConnected,
					guiInputConnected,
					restorePresentationOwnership: false
				);
				candidateRollbackCompleted = true;
				Trace(
					"C# autocomplete binding resolution rejected",
					$"Reason='StaleLifecycleOrReloadAuthorityAfterCompletionRequestedConnect', ScriptTransitionId='{scriptTransitionId}', HostInstanceToken='{hostInstanceToken}', ReloadReadyEpoch='{reloadReadyEpoch}'"
				);
				return false;
			}

			guiInputConnected = _connectPluginSignal(
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

			if (!_codeEditMutationCoordinator.IsBindingActivationForwardAuthorityCurrent(activationLease))
			{
				RollbackResolvedCodeEditCandidate(
					activationLease,
					codeEdit,
					textChangedConnected,
					completionRequestedConnected,
					guiInputConnected,
					restorePresentationOwnership: false
				);
				candidateRollbackCompleted = true;
				Trace(
					"C# autocomplete binding resolution rejected",
					$"Reason='StaleLifecycleOrReloadAuthorityAfterSignalConnect', ScriptTransitionId='{scriptTransitionId}', HostInstanceToken='{hostInstanceToken}', ReloadReadyEpoch='{reloadReadyEpoch}'"
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
				!_scriptEditorLifecycleCoordinator.TryReserveBinding(
					_managedAssemblyGeneration,
					hostInstanceToken,
					scriptTransitionId,
					reloadReadyEpoch,
					identity,
					out EditorBindingReservation reservation
				)
			)
			{
				RollbackResolvedCodeEditCandidate(
					activationLease,
					codeEdit,
					textChangedConnected,
					completionRequestedConnected,
					guiInputConnected,
					restorePresentationOwnership: false
				);
				candidateRollbackCompleted = true;
				Trace(
					"C# autocomplete binding resolution rejected",
					$"Reason='BindingReservationRejected', ScriptTransitionId='{scriptTransitionId}', HostInstanceToken='{hostInstanceToken}', ReloadReadyEpoch='{reloadReadyEpoch}', CodeEditInstanceId='{identity.CodeEditInstanceId}', ScriptPath='{identity.ScriptResourcePath}'"
				);
				return false;
			}

			bindingReservation = reservation;
			if (!_codeEditMutationCoordinator.IsBindingActivationForwardAuthorityCurrent(activationLease))
			{
				RollbackResolvedCodeEditCandidate(
					activationLease,
					codeEdit,
					textChangedConnected,
					completionRequestedConnected,
					guiInputConnected,
					restorePresentationOwnership: false,
					bindingReservation
				);
				candidateRollbackCompleted = true;
				Trace(
					"C# autocomplete binding resolution rejected",
					$"Reason='StaleLifecycleOrReloadAuthorityAfterBindingReservation', ScriptTransitionId='{scriptTransitionId}', HostInstanceToken='{hostInstanceToken}', ReloadReadyEpoch='{reloadReadyEpoch}', ReservedBindingEpoch='{reservation.BindingEpoch}'"
				);
				return false;
			}

			presentationOwnershipTouched = true;
			bool prefixApplied = _completionPrefixController.Apply(codeEdit);
			if (!prefixApplied)
			{
				RollbackResolvedCodeEditCandidate(
					activationLease,
					codeEdit,
					textChangedConnected,
					completionRequestedConnected,
					guiInputConnected,
					restorePresentationOwnership: true,
					bindingReservation
				);
				candidateRollbackCompleted = true;
				Trace(
					"C# autocomplete binding rebind failed",
					() => $"Reason='CompletionPrefix Apply failed', PreviousCodeEditInstanceId='{previousCodeEditInstanceId}', ScriptTransitionId='{scriptTransitionId}', ReservedBindingEpoch='{reservation.BindingEpoch}', {DescribeGodotObject("CodeEdit", codeEdit)}, {DescribeScript(currentScript)}"
				);
				return false;
			}

			if (!_codeEditMutationCoordinator.IsBindingActivationForwardAuthorityCurrent(activationLease))
			{
				RollbackResolvedCodeEditCandidate(
					activationLease,
					codeEdit,
					textChangedConnected,
					completionRequestedConnected,
					guiInputConnected,
					restorePresentationOwnership: true,
					bindingReservation
				);
				candidateRollbackCompleted = true;
				Trace(
					"C# autocomplete binding resolution rejected",
					$"Reason='StaleLifecycleOrReloadAuthorityAfterPrefixApply', ScriptTransitionId='{scriptTransitionId}', HostInstanceToken='{hostInstanceToken}', ReloadReadyEpoch='{reloadReadyEpoch}', ReservedBindingEpoch='{reservation.BindingEpoch}'"
				);
				return false;
			}

			try
			{
				_themeController.Apply(codeEdit);
			}
			catch (Exception exception)
			{
				Trace(
					"C# autocomplete binding Theme Apply failed",
					() => $"ScriptTransitionId='{scriptTransitionId}', ReservedBindingEpoch='{reservation.BindingEpoch}', {DescribeGodotObject("CodeEdit", codeEdit)}, Exception='{exception}'"
				);
				RollbackResolvedCodeEditCandidate(
					activationLease,
					codeEdit,
					textChangedConnected,
					completionRequestedConnected,
					guiInputConnected,
					restorePresentationOwnership: true,
					bindingReservation
				);
				candidateRollbackCompleted = true;
				throw;
			}

			if (!_codeEditMutationCoordinator.IsBindingActivationForwardAuthorityCurrent(activationLease))
			{
				RollbackResolvedCodeEditCandidate(
					activationLease,
					codeEdit,
					textChangedConnected,
					completionRequestedConnected,
					guiInputConnected,
					restorePresentationOwnership: true,
					bindingReservation
				);
				candidateRollbackCompleted = true;
				Trace(
					"C# autocomplete binding resolution rejected",
					$"Reason='StaleLifecycleOrReloadAuthorityAfterThemeApply', ScriptTransitionId='{scriptTransitionId}', HostInstanceToken='{hostInstanceToken}', ReloadReadyEpoch='{reloadReadyEpoch}', ReservedBindingEpoch='{reservation.BindingEpoch}'"
				);
				return false;
			}

			if (
				!TryPublishFreshNativeOwnershipMarker(
					activationLease,
					reservation,
					codeEdit
				)
			)
			{
				RollbackResolvedCodeEditCandidate(
					activationLease,
					codeEdit,
					textChangedConnected,
					completionRequestedConnected,
					guiInputConnected,
					restorePresentationOwnership: true,
					bindingReservation
				);
				candidateRollbackCompleted = true;
				Trace(
					"C# autocomplete binding resolution rejected",
					$"Reason='NativeOwnershipMarkerWriteRejected', ScriptTransitionId='{scriptTransitionId}', HostInstanceToken='{hostInstanceToken}', ReloadReadyEpoch='{reloadReadyEpoch}', ReservedBindingEpoch='{reservation.BindingEpoch}'"
				);
				return false;
			}

			if (!_codeEditMutationCoordinator.IsBindingActivationForwardAuthorityCurrent(activationLease))
			{
				RollbackResolvedCodeEditCandidate(
					activationLease,
					codeEdit,
					textChangedConnected,
					completionRequestedConnected,
					guiInputConnected,
					restorePresentationOwnership: true,
					bindingReservation
				);
				candidateRollbackCompleted = true;
				Trace(
					"C# autocomplete binding resolution rejected",
					$"Reason='StaleLifecycleOrReloadAuthorityAfterPresentationApply', ScriptTransitionId='{scriptTransitionId}', HostInstanceToken='{hostInstanceToken}', ReloadReadyEpoch='{reloadReadyEpoch}', ReservedBindingEpoch='{reservation.BindingEpoch}'"
				);
				return false;
			}

			if (
				!_scriptEditorLifecycleCoordinator.TryCommitReservedBinding(
					reservation,
					out EditorBindingLease lease
				)
			)
			{
				RollbackResolvedCodeEditCandidate(
					activationLease,
					codeEdit,
					textChangedConnected,
					completionRequestedConnected,
					guiInputConnected,
					restorePresentationOwnership: true,
					bindingReservation
				);
				candidateRollbackCompleted = true;
				Trace(
					"C# autocomplete binding resolution rejected",
					$"Reason='LifecycleReservedCommitRejected', ScriptTransitionId='{scriptTransitionId}', HostInstanceToken='{hostInstanceToken}', ReservedBindingEpoch='{reservation.BindingEpoch}', CodeEditInstanceId='{identity.CodeEditInstanceId}', ScriptPath='{identity.ScriptResourcePath}'"
				);
				return false;
			}

			bindingReservationCommitted = true;
			_codeEdit = codeEdit;
			_bindingLease = lease;
			committedBindingLease = lease;
			activationOutcome = "Committed";
			TraceRebindSummary(
				scriptEditor,
				previousCodeEditInstanceId,
				codeEdit,
				currentScript,
				result: true,
				reason: $"Bound C# CodeEdit; ScriptTransitionId={lease.ScriptTransitionId}; ReloadReadyEpoch={lease.ReloadReadyEpoch}; BindingEpoch={lease.BindingEpoch}"
			);
			return true;
		}
		catch
		{
			activationOutcome = "Exception";
			if (
				!candidateRollbackCompleted
				&& resolvedCodeEdit != null
				&& (
					textChangedConnected
					|| completionRequestedConnected
					|| guiInputConnected
					|| presentationOwnershipTouched
				)
				&& _codeEditMutationCoordinator.OwnsBindingActivation(activationLease)
			)
			{
				RollbackResolvedCodeEditCandidate(
					activationLease,
					resolvedCodeEdit,
					textChangedConnected,
					completionRequestedConnected,
					guiInputConnected,
					presentationOwnershipTouched,
					bindingReservation
				);
			}
			throw;
		}
		finally
		{
			if (bindingReservation.HasValue && !bindingReservationCommitted)
			{
				_scriptEditorLifecycleCoordinator.AbandonBindingReservation(
					bindingReservation.Value
				);
			}

			_codeEditMutationCoordinator.EndBindingActivation(
				activationLease,
				activationOutcome,
				committedBindingLease
			);
		}
	}

	internal bool TryGetActiveCodeEdit(
		out CodeEdit codeEdit,
		out string scriptPath,
		Action<string, ScriptEditor, CodeEdit> diagnosticPhase = null
	)
	{
		return TryGetActiveCodeEdit(
			out codeEdit,
			out scriptPath,
			out _,
			diagnosticPhase
		);
	}

	internal bool TryGetActiveCodeEdit(
		out CodeEdit codeEdit,
		out string scriptPath,
		out EditorBindingLease bindingLease,
		Action<string, ScriptEditor, CodeEdit> diagnosticPhase = null
	)
	{
		codeEdit = null;
		scriptPath = "";
		bindingLease = default;
		if (!_bindingLease.HasValue)
			return false;

		EditorBindingLease lease = _bindingLease.Value;
		if (
			!_scriptEditorLifecycleCoordinator.IsCurrentStableBinding(lease)
			|| !_reloadStabilizationReadyProvider()
			|| lease.ReloadReadyEpoch <= 0
			|| lease.ReloadReadyEpoch != _currentReloadReadyEpochProvider()
		)
		{
			return false;
		}

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
		bindingLease = lease;
		return true;
	}

	private void RollbackResolvedCodeEditCandidate(
		AutocompleteBindingActivationTransactionLease activationLease,
		CodeEdit codeEdit,
		bool textChangedConnected,
		bool completionRequestedConnected,
		bool guiInputConnected,
		bool restorePresentationOwnership,
		EditorBindingReservation? bindingReservation = null
	)
	{
		if (!_codeEditMutationCoordinator.OwnsBindingActivation(activationLease))
		{
			Trace(
				"C# autocomplete binding rollback rejected",
				$"Reason='BindingActivationOwnershipLost', MutationTransactionId='{activationLease.MutationTransactionId}', ScriptTransitionId='{activationLease.ScriptTransitionId}', ReloadReadyEpoch='{activationLease.ReloadReadyEpoch}'"
			);
			return;
		}

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

		if (!restorePresentationOwnership)
			return;

		if (!bindingReservation.HasValue)
		{
			_completionPrefixController.ForgetOwnedState(codeEdit);
			_themeController.ForgetOwnedState(codeEdit);
			return;
		}

		TryReleaseReservationPresentationOwnership(
			activationLease,
			codeEdit,
			bindingReservation.Value
		);
	}

	internal void Shutdown()
	{
		Trace(
			"C# autocomplete binding Shutdown begin",
			() => $"{DescribeGodotObject("ScriptEditor", _scriptEditor)}, {DescribeGodotObject("CodeEdit", _codeEdit)}"
		);
		DisconnectCodeEditForShutdown();
		DisconnectScriptEditor();
		_codeEdit = null;
		_scriptEditor = null;
		_completionPrefixController.Reset();
		_themeController.Reset();
		Trace("C# autocomplete binding Shutdown completed");
	}

	private bool DisconnectCodeEditForBindingActivation(
		AutocompleteBindingActivationTransactionLease activationLease
	)
	{
		if (
			!_codeEditMutationCoordinator.OwnsBindingActivation(activationLease)
			|| !_codeEditMutationCoordinator.IsBindingActivationForwardAuthorityCurrent(
				activationLease
			)
		)
		{
			Trace(
				"C# autocomplete binding cleanup rejected",
				$"Reason='BindingActivationAuthorityUnavailable', MutationTransactionId='{activationLease.MutationTransactionId}', ScriptTransitionId='{activationLease.ScriptTransitionId}', ReloadReadyEpoch='{activationLease.ReloadReadyEpoch}'"
			);
			return false;
		}

		return DisconnectCodeEditCore(
			cancelCompletion: false,
			activationLease: activationLease
		);
	}

	private void DisconnectCodeEditForShutdown()
	{
		DisconnectCodeEditCore(cancelCompletion: true, activationLease: null);
	}

	private bool DisconnectCodeEditCore(
		bool cancelCompletion,
		AutocompleteBindingActivationTransactionLease? activationLease
	)
	{
		_invalidateCompletionState();

		CodeEdit codeEdit = _codeEdit;
		EditorBindingLease? outgoingBindingLease = _bindingLease;
		bool authorityRetained = CanContinueOutgoingCodeEditMutation(activationLease);
		bool codeEditValid = false;
		if (authorityRetained)
		{
			codeEditValid = IsValidGodotObject(codeEdit);
			authorityRetained = CanContinueOutgoingCodeEditMutation(activationLease);
		}

		if (codeEditValid && authorityRetained)
		{
			_disconnectPluginSignal(
				codeEdit,
				TextEdit.SignalName.TextChanged,
				_textChangedMethodName,
				TextChangedDescription
			);
			authorityRetained = CanContinueOutgoingCodeEditMutation(activationLease);

			if (authorityRetained)
			{
				_disconnectPluginSignal(
					codeEdit,
					CodeEdit.SignalName.CodeCompletionRequested,
					_completionRequestedMethodName,
					CompletionRequestedDescription
				);
				authorityRetained = CanContinueOutgoingCodeEditMutation(activationLease);
			}

			if (authorityRetained)
			{
				_disconnectPluginSignal(
					codeEdit,
					Control.SignalName.GuiInput,
					_guiInputMethodName,
					GuiInputDescription
				);
				authorityRetained = CanContinueOutgoingCodeEditMutation(activationLease);
			}

			if (cancelCompletion && authorityRetained)
			{
				codeEdit.CancelCodeCompletion();
				authorityRetained = CanContinueOutgoingCodeEditMutation(activationLease);
			}
		}
		else if (authorityRetained && codeEdit != null && !codeEditValid)
		{
			Trace(
				"C# autocomplete binding cleanup anomaly",
				() => $"Reason='Bound CodeEdit invalid; native disconnect/cancel skipped', {DescribeGodotObject("CodeEdit", codeEdit)}"
			);
		}

		bool presentationReleased = true;
		if (authorityRetained)
		{
			presentationReleased = TryReleaseOutgoingPresentationOwnership(
				codeEdit,
				outgoingBindingLease,
				activationLease
			);
			authorityRetained = CanContinueOutgoingCodeEditMutation(activationLease);
		}
		else
		{
			// Forward authority was lost before presentation cleanup. Relinquish only
			// the managed snapshot; a surviving native marker remains recoverable by
			// a later exact BindingActivation and must not be guessed away here.
			_completionPrefixController.ForgetOwnedState(codeEdit);
			_themeController.ForgetOwnedState(codeEdit);
		}

		_codeEdit = null;
		_bindingLease = null;

		if ((!authorityRetained || !presentationReleased) && activationLease.HasValue)
			return false;

		return true;
	}

	private bool CanContinueOutgoingCodeEditMutation(
		AutocompleteBindingActivationTransactionLease? activationLease
	)
	{
		return !activationLease.HasValue
			|| _codeEditMutationCoordinator.IsBindingActivationForwardAuthorityCurrent(
				activationLease.Value
			);
	}

	private enum NativeOwnershipInspectionKind
	{
		Missing,
		Malformed,
		SameGeneration,
		CrossGenerationOrphan,
	}

	private NativeOwnershipInspectionKind InspectNativeOwnership(
		CodeEdit codeEdit,
		out AutocompleteCodeEditNativeOwnershipBridge.OwnershipState state,
		out string failureDetail,
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
				out state,
				out failureDetail,
				nativeBoundaryDiagnosticPhase
			);
		InvokeNativeBoundaryDiagnosticPhase(
			nativeBoundaryDiagnosticPhase,
			"NativeOwnershipRecovery.Inspect.Returned",
			$"Status='{markerStatus}', SchemaVersion='{state?.SchemaVersion ?? 0}', OwnerBindingEpoch='{state?.OwnerBindingEpoch ?? 0}'"
		);

		if (markerStatus == AutocompleteCodeEditNativeOwnershipBridge.MarkerReadStatus.Missing)
			return NativeOwnershipInspectionKind.Missing;

		if (markerStatus != AutocompleteCodeEditNativeOwnershipBridge.MarkerReadStatus.Valid)
			return NativeOwnershipInspectionKind.Malformed;

		return string.Equals(
			state.OwnerManagedAssemblyGeneration,
			_managedAssemblyGeneration,
			StringComparison.Ordinal
		)
			? NativeOwnershipInspectionKind.SameGeneration
			: NativeOwnershipInspectionKind.CrossGenerationOrphan;
	}

	private bool TryNeutralizeCrossGenerationCodeEditCompletion(
		AutocompleteBindingActivationTransactionLease activationLease,
		CodeEdit codeEdit,
		AutocompleteCodeEditNativeOwnershipBridge.OwnershipState state,
		ulong resolvedCodeEditInstanceId,
		string scriptPath
	)
	{
		if (
			!_codeEditMutationCoordinator.OwnsBindingActivation(activationLease)
			|| !_codeEditMutationCoordinator.IsBindingActivationForwardAuthorityCurrent(
				activationLease
			)
		)
		{
			return false;
		}

		if (
			state == null
			|| state.CodeEditNativeInstanceId == 0
			|| state.CodeEditNativeInstanceId != resolvedCodeEditInstanceId
			|| string.Equals(
				state.OwnerManagedAssemblyGeneration,
				_managedAssemblyGeneration,
				StringComparison.Ordinal
			)
		)
		{
			return false;
		}

		ulong codeEditInstanceId = state.CodeEditNativeInstanceId;
		if (_reloadNeutralizedCodeEditInstanceIds.Contains(codeEditInstanceId))
		{
			return _codeEditMutationCoordinator.IsBindingActivationForwardAuthorityCurrent(
				activationLease
			);
		}

		string details =
			$"ManagedAssemblyGeneration='{_managedAssemblyGeneration}', PreviousManagedAssemblyGeneration='{state.OwnerManagedAssemblyGeneration}', SchemaVersion='{state.SchemaVersion}', PreviousOwnerHostInstanceToken='{state.OwnerHostInstanceToken}', PreviousOwnerScriptTransitionId='{state.OwnerScriptTransitionId}', PreviousOwnerBindingEpoch='{state.OwnerBindingEpoch}', MutationTransactionId='{activationLease.MutationTransactionId}', HostInstanceToken='{activationLease.HostInstanceToken}', ScriptTransitionId='{activationLease.ScriptTransitionId}', ReloadReadyEpoch='{activationLease.ReloadReadyEpoch}', CodeEditInstanceId='{codeEditInstanceId}', ScriptPath='{scriptPath}'";
		Trace(
			"C# autocomplete reload CodeEdit completion neutralization begin",
			details
		);

		if (
			!_codeEditMutationCoordinator.IsBindingActivationForwardAuthorityCurrent(
				activationLease
			)
		)
		{
			return false;
		}

		try
		{
			codeEdit.CancelCodeCompletion();
		}
		catch (Exception exception)
		{
			Trace(
				"C# autocomplete reload CodeEdit completion neutralization failed",
				$"{details}, Exception='{exception.GetType().Name}: {exception.Message}'"
			);
			return false;
		}

		Trace(
			"C# autocomplete reload CodeEdit completion neutralization returned",
			details
		);
		_reloadNeutralizedCodeEditInstanceIds.Add(codeEditInstanceId);
		return _codeEditMutationCoordinator.IsBindingActivationForwardAuthorityCurrent(
			activationLease
		);
	}

	private bool RecoverStaleNativeOwnershipIfNeeded(
		AutocompleteBindingActivationTransactionLease activationLease,
		CodeEdit codeEdit,
		NativeOwnershipInspectionKind inspectionKind,
		AutocompleteCodeEditNativeOwnershipBridge.OwnershipState state,
		string failureDetail,
		Action<string, string> nativeBoundaryDiagnosticPhase = null
	)
	{
		if (!_codeEditMutationCoordinator.OwnsBindingActivation(activationLease))
			return false;

		if (inspectionKind == NativeOwnershipInspectionKind.Missing)
		{
			return _codeEditMutationCoordinator.IsBindingActivationForwardAuthorityCurrent(
				activationLease
			);
		}

		if (inspectionKind == NativeOwnershipInspectionKind.Malformed)
		{
			TraceNativeOwnershipMalformedMarkerOnce(codeEdit, failureDetail);
			return false;
		}

		if (inspectionKind == NativeOwnershipInspectionKind.SameGeneration)
		{
			if (state == null || state.IsLegacy)
			{
				TraceNativeOwnershipOwnerMismatchOnce(
					state,
					"Same-generation legacy marker cannot prove exact BindingEpoch ownership."
				);
				return false;
			}

			if (
				_bindingLease.HasValue
				&& _scriptEditorLifecycleCoordinator.IsCurrentStableBinding(
					_bindingLease.Value
				)
				&& NativeOwnershipOwnerMatches(state, _bindingLease.Value)
			)
			{
				TraceNativeOwnershipOwnerMismatchOnce(
					state,
					"Exact current stable owner was observed during BindingPending activation."
				);
				return false;
			}

			TraceNativeOwnershipSameGenerationMarkerOnce(state);
			return TryRecoverVerifiedStaleNativePresentationOwnership(
				activationLease,
				codeEdit,
				state,
				"SameGenerationStale",
				nativeBoundaryDiagnosticPhase
			);
		}

		return TryRecoverVerifiedStaleNativePresentationOwnership(
			activationLease,
			codeEdit,
			state,
			"CrossGeneration",
			nativeBoundaryDiagnosticPhase
		);
	}

	private bool TryRecoverVerifiedStaleNativePresentationOwnership(
		AutocompleteBindingActivationTransactionLease activationLease,
		CodeEdit codeEdit,
		AutocompleteCodeEditNativeOwnershipBridge.OwnershipState state,
		string recoveryKind,
		Action<string, string> nativeBoundaryDiagnosticPhase
	)
	{
		if (
			state == null
			|| !_codeEditMutationCoordinator.IsBindingActivationForwardAuthorityCurrent(
				activationLease
			)
		)
		{
			return false;
		}

		AutocompletePresentationRestoreResult prefixResult =
			AutocompletePresentationRestoreResult.Success();
		if (state.PrefixOwned)
		{
			prefixResult = _completionPrefixController.TryRestoreOwnedPrefixesFromNativeBridge(
				codeEdit,
				state.AppliedCodeCompletionPrefixes,
				state.PreviousCodeCompletionPrefixes
			);
		}
		if (!prefixResult.Succeeded)
		{
			_completionPrefixController.ForgetOwnedState(codeEdit);
			_themeController.ForgetOwnedState(codeEdit);
			TraceNativeOwnershipRestoreFailureOnce(
				state,
				$"RecoveryKind='{recoveryKind}', Component='CodeCompletionPrefixes'"
			);
			return false;
		}

		if (
			!_codeEditMutationCoordinator.IsBindingActivationForwardAuthorityCurrent(
				activationLease
			)
		)
		{
			return false;
		}

		AutocompletePresentationRestoreResult colorResult =
			AutocompletePresentationRestoreResult.Success();
		if (state.CompletionExistingColorOwned)
		{
			colorResult = _themeController.TryRestoreCompletionExistingColorFromNativeBridge(
				codeEdit,
				state.HadPreviousCompletionExistingColorOverride,
				state.PreviousCompletionExistingColor,
				state.AppliedCompletionExistingColor
			);
		}
		if (!colorResult.Succeeded)
		{
			_completionPrefixController.ForgetOwnedState(codeEdit);
			_themeController.ForgetOwnedState(codeEdit);
			TraceNativeOwnershipRestoreFailureOnce(
				state,
				$"RecoveryKind='{recoveryKind}', Component='completion_existing_color'"
			);
			return false;
		}

		_completionPrefixController.ForgetOwnedState(codeEdit);
		_themeController.ForgetOwnedState(codeEdit);
		TracePresentationOwnershipChangedIfNeeded(
			state,
			prefixResult,
			colorResult,
			AutocompletePresentationRestoreResult.Success(),
			recoveryKind
		);

		if (
			!_codeEditMutationCoordinator.IsBindingActivationForwardAuthorityCurrent(
				activationLease
			)
		)
		{
			return false;
		}

		if (
			!_nativeOwnershipBridge.TryClearVerifiedMarker(
				codeEdit,
				state,
				out string clearFailureDetail,
				nativeBoundaryDiagnosticPhase
			)
		)
		{
			TraceNativeOwnershipMarkerClearFailureOnce(codeEdit, clearFailureDetail);
			return false;
		}

		Trace(
			"C# autocomplete native CodeEdit ownership orphan recovered",
			$"RecoveryKind='{recoveryKind}', SchemaVersion='{state.SchemaVersion}', CodeEditNativeInstanceId='{state.CodeEditNativeInstanceId}', PreviousManagedAssemblyGeneration='{state.OwnerManagedAssemblyGeneration}', CurrentManagedAssemblyGeneration='{_managedAssemblyGeneration}', OwnerHostInstanceToken='{state.OwnerHostInstanceToken}', OwnerScriptTransitionId='{state.OwnerScriptTransitionId}', OwnerBindingEpoch='{state.OwnerBindingEpoch}', PrefixOwned='{state.PrefixOwned}', CompletionExistingColorOwned='{state.CompletionExistingColorOwned}'"
		);
		return _codeEditMutationCoordinator.IsBindingActivationForwardAuthorityCurrent(
			activationLease
		);
	}

	private bool TryReleaseOutgoingPresentationOwnership(
		CodeEdit codeEdit,
		EditorBindingLease? outgoingBindingLease,
		AutocompleteBindingActivationTransactionLease? activationLease
	)
	{
		bool failClosed = activationLease.HasValue;
		if (!IsValidGodotObject(codeEdit))
		{
			_completionPrefixController.ForgetOwnedState(codeEdit);
			_themeController.ForgetOwnedState(codeEdit);
			return true;
		}

		if (!outgoingBindingLease.HasValue)
		{
			_completionPrefixController.ForgetOwnedState(codeEdit);
			_themeController.ForgetOwnedState(codeEdit);
			TraceNativeOwnershipOwnerMismatchOnce(
				null,
				"Outgoing CodeEdit has no exact EditorBindingLease."
			);
			return !failClosed;
		}

		AutocompleteCodeEditNativeOwnershipBridge.MarkerReadStatus markerStatus =
			_nativeOwnershipBridge.Inspect(
				codeEdit,
				out AutocompleteCodeEditNativeOwnershipBridge.OwnershipState state,
				out string failureDetail
			);

		if (markerStatus == AutocompleteCodeEditNativeOwnershipBridge.MarkerReadStatus.Malformed)
		{
			_completionPrefixController.ForgetOwnedState(codeEdit);
			_themeController.ForgetOwnedState(codeEdit);
			TraceNativeOwnershipMalformedMarkerOnce(codeEdit, failureDetail);
			return !failClosed;
		}

		if (markerStatus == AutocompleteCodeEditNativeOwnershipBridge.MarkerReadStatus.Missing)
		{
			TraceNativeOwnershipMissingMarkerOnce(outgoingBindingLease.Value);
			AutocompletePresentationRestoreResult prefixResult =
				_completionPrefixController.Restore(codeEdit);
			AutocompletePresentationRestoreResult themeResult = _themeController.Restore(codeEdit);
			if (!prefixResult.Succeeded || !themeResult.Succeeded)
			{
				TraceNativeOwnershipRestoreFailureOnce(
					null,
					$"RecoveryKind='OutgoingBindingMissingMarker', PrefixSucceeded='{prefixResult.Succeeded}', ThemeSucceeded='{themeResult.Succeeded}'"
				);
				return !failClosed;
			}

			TracePresentationOwnershipChangedIfNeeded(
				null,
				prefixResult,
				AutocompletePresentationRestoreResult.Success(),
				themeResult,
				"OutgoingBinding"
			);
			return CanContinueOutgoingCodeEditMutation(activationLease);
		}

		EditorBindingLease outgoingLease = outgoingBindingLease.Value;
		if (state.IsLegacy || !NativeOwnershipOwnerMatches(state, outgoingLease))
		{
			_completionPrefixController.ForgetOwnedState(codeEdit);
			_themeController.ForgetOwnedState(codeEdit);
			TraceNativeOwnershipOwnerMismatchOnce(
				state,
				$"ExpectedOutgoingBindingEpoch='{outgoingLease.BindingEpoch}', ExpectedScriptTransitionId='{outgoingLease.ScriptTransitionId}', ExpectedHostInstanceToken='{outgoingLease.HostInstanceToken}'"
			);
			return !failClosed;
		}

		bool released = TryReleaseExactVerifiedPresentationOwnership(
			codeEdit,
			state,
			activationLease,
			"OutgoingBinding",
			includeManagedRemainingState: true
		);
		return released || !failClosed;
	}

	private bool TryReleaseReservationPresentationOwnership(
		AutocompleteBindingActivationTransactionLease activationLease,
		CodeEdit codeEdit,
		EditorBindingReservation reservation
	)
	{
		if (!IsValidGodotObject(codeEdit))
		{
			_completionPrefixController.ForgetOwnedState(codeEdit);
			_themeController.ForgetOwnedState(codeEdit);
			return false;
		}

		AutocompleteCodeEditNativeOwnershipBridge.MarkerReadStatus markerStatus =
			_nativeOwnershipBridge.Inspect(
				codeEdit,
				out AutocompleteCodeEditNativeOwnershipBridge.OwnershipState state,
				out string failureDetail
			);
		if (markerStatus == AutocompleteCodeEditNativeOwnershipBridge.MarkerReadStatus.Missing)
		{
			AutocompletePresentationRestoreResult prefixResult =
				_completionPrefixController.Restore(codeEdit);
			AutocompletePresentationRestoreResult themeResult = _themeController.Restore(codeEdit);
			TracePresentationOwnershipChangedIfNeeded(
				null,
				prefixResult,
				AutocompletePresentationRestoreResult.Success(),
				themeResult,
				"RollbackReservation",
				reservation.BindingEpoch,
				reservation.CodeEditInstanceId,
				reservation.ScriptResourcePath,
				reservation.ScriptTransitionId,
				reservation.HostInstanceToken
			);
			return prefixResult.Succeeded && themeResult.Succeeded;
		}

		if (markerStatus != AutocompleteCodeEditNativeOwnershipBridge.MarkerReadStatus.Valid)
		{
			_completionPrefixController.ForgetOwnedState(codeEdit);
			_themeController.ForgetOwnedState(codeEdit);
			TraceNativeOwnershipMalformedMarkerOnce(codeEdit, failureDetail);
			return false;
		}

		if (!NativeOwnershipOwnerMatches(state, reservation))
		{
			_completionPrefixController.ForgetOwnedState(codeEdit);
			_themeController.ForgetOwnedState(codeEdit);
			TraceNativeOwnershipOwnerMismatchOnce(
				state,
				$"RollbackReservedBindingEpoch='{reservation.BindingEpoch}'"
			);
			return false;
		}

		return TryReleaseExactVerifiedPresentationOwnership(
			codeEdit,
			state,
			null,
			"RollbackReservation",
			includeManagedRemainingState: true
		);
	}

	private bool TryReleaseExactVerifiedPresentationOwnership(
		CodeEdit codeEdit,
		AutocompleteCodeEditNativeOwnershipBridge.OwnershipState state,
		AutocompleteBindingActivationTransactionLease? activationLease,
		string recoveryKind,
		bool includeManagedRemainingState
	)
	{
		if (state == null || !CanContinueOutgoingCodeEditMutation(activationLease))
			return false;

		AutocompletePresentationRestoreResult prefixResult =
			AutocompletePresentationRestoreResult.Success();
		if (state.PrefixOwned)
		{
			prefixResult = _completionPrefixController.TryRestoreOwnedPrefixesFromNativeBridge(
				codeEdit,
				state.AppliedCodeCompletionPrefixes,
				state.PreviousCodeCompletionPrefixes
			);
			_completionPrefixController.ForgetOwnedState(codeEdit);
		}
		else if (includeManagedRemainingState)
		{
			prefixResult = _completionPrefixController.Restore(codeEdit);
		}

		if (!prefixResult.Succeeded || !CanContinueOutgoingCodeEditMutation(activationLease))
		{
			_completionPrefixController.ForgetOwnedState(codeEdit);
			_themeController.ForgetOwnedState(codeEdit);
			TraceNativeOwnershipRestoreFailureOnce(
				state,
				$"RecoveryKind='{recoveryKind}', Component='CodeCompletionPrefixes'"
			);
			return false;
		}

		AutocompletePresentationRestoreResult colorResult =
			AutocompletePresentationRestoreResult.Success();
		AutocompletePresentationRestoreResult remainingThemeResult =
			AutocompletePresentationRestoreResult.Success();
		if (state.CompletionExistingColorOwned)
		{
			colorResult = _themeController.TryRestoreCompletionExistingColorFromNativeBridge(
				codeEdit,
				state.HadPreviousCompletionExistingColorOverride,
				state.PreviousCompletionExistingColor,
				state.AppliedCompletionExistingColor
			);
			if (colorResult.Succeeded && includeManagedRemainingState)
			{
				remainingThemeResult =
					_themeController.RestoreRemainingOwnedStateAfterCompletionExistingColorBridge(
						codeEdit
					);
			}
			else
			{
				_themeController.ForgetOwnedState(codeEdit);
			}
		}
		else if (includeManagedRemainingState)
		{
			remainingThemeResult = _themeController.Restore(codeEdit);
		}
		else
		{
			_themeController.ForgetOwnedState(codeEdit);
		}

		if (
			!colorResult.Succeeded
			|| !remainingThemeResult.Succeeded
			|| !CanContinueOutgoingCodeEditMutation(activationLease)
		)
		{
			_completionPrefixController.ForgetOwnedState(codeEdit);
			_themeController.ForgetOwnedState(codeEdit);
			TraceNativeOwnershipRestoreFailureOnce(
				state,
				$"RecoveryKind='{recoveryKind}', CompletionExistingColorSucceeded='{colorResult.Succeeded}', RemainingThemeSucceeded='{remainingThemeResult.Succeeded}'"
			);
			return false;
		}

		TracePresentationOwnershipChangedIfNeeded(
			state,
			prefixResult,
			colorResult,
			remainingThemeResult,
			recoveryKind
		);

		if (!CanContinueOutgoingCodeEditMutation(activationLease))
			return false;

		if (
			!_nativeOwnershipBridge.TryClearVerifiedMarker(
				codeEdit,
				state,
				out string clearFailureDetail
			)
		)
		{
			TraceNativeOwnershipMarkerClearFailureOnce(codeEdit, clearFailureDetail);
			return false;
		}

		return CanContinueOutgoingCodeEditMutation(activationLease);
	}

	private bool TryPublishFreshNativeOwnershipMarker(
		AutocompleteBindingActivationTransactionLease activationLease,
		EditorBindingReservation reservation,
		CodeEdit codeEdit
	)
	{
		if (
			!_codeEditMutationCoordinator.OwnsBindingActivation(activationLease)
			|| !_codeEditMutationCoordinator.IsBindingActivationForwardAuthorityCurrent(
				activationLease
			)
		)
		{
			return false;
		}

		if (
			!_completionPrefixController.TryCaptureNativeOwnershipState(
				codeEdit,
				out ulong prefixCodeEditInstanceId,
				out bool prefixOwned,
				out string[] previousPrefixes,
				out string[] appliedPrefixes
			)
			|| !_themeController.TryCaptureCompletionExistingColorNativeOwnershipState(
				codeEdit,
				out ulong themeCodeEditInstanceId,
				out bool completionExistingColorOwned,
				out bool hadPreviousCompletionExistingColorOverride,
				out Color previousCompletionExistingColor,
				out Color appliedCompletionExistingColor
			)
		)
		{
			TraceNativeOwnershipMarkerWriteFailureOnce(
				codeEdit,
				"Managed reversible-state snapshot could not be mirrored."
			);
			return false;
		}

		if (!prefixOwned && !completionExistingColorOwned)
			return true;

		ulong codeEditNativeInstanceId = completionExistingColorOwned
			? themeCodeEditInstanceId
			: prefixCodeEditInstanceId;

		if (
			codeEditNativeInstanceId == 0
			|| codeEditNativeInstanceId != reservation.CodeEditInstanceId
			|| (
				prefixOwned
				&& completionExistingColorOwned
				&& prefixCodeEditInstanceId != themeCodeEditInstanceId
			)
		)
		{
			TraceNativeOwnershipMarkerWriteFailureOnce(
				codeEdit,
				$"Managed snapshot CodeEdit identity mismatch. ReservedCodeEditInstanceId='{reservation.CodeEditInstanceId}', PrefixSnapshotInstanceId='{prefixCodeEditInstanceId}', ThemeSnapshotInstanceId='{themeCodeEditInstanceId}'."
			);
			return false;
		}

		var state = new AutocompleteCodeEditNativeOwnershipBridge.OwnershipState(
			AutocompleteCodeEditNativeOwnershipBridge.CurrentSchemaVersion,
			isLegacy: false,
			reservation.ManagedAssemblyGeneration,
			reservation.HostInstanceToken,
			reservation.ScriptTransitionId,
			reservation.ReloadReadyEpoch,
			reservation.BindingEpoch,
			codeEditNativeInstanceId,
			reservation.ScriptResourcePath,
			prefixOwned,
			prefixOwned ? previousPrefixes : Array.Empty<string>(),
			prefixOwned ? appliedPrefixes : Array.Empty<string>(),
			completionExistingColorOwned,
			hadPreviousCompletionExistingColorOverride,
			previousCompletionExistingColor,
			appliedCompletionExistingColor
		);

		if (!NativeOwnershipOwnerMatches(state, reservation))
		{
			TraceNativeOwnershipMarkerWriteFailureOnce(
				codeEdit,
				"Fresh schema-v2 ownership state does not match the exact binding reservation."
			);
			return false;
		}

		if (
			!_codeEditMutationCoordinator.IsBindingActivationForwardAuthorityCurrent(
				activationLease
			)
		)
		{
			return false;
		}

		if (!_nativeOwnershipBridge.TryWrite(codeEdit, state, out string failureDetail))
		{
			TraceNativeOwnershipMarkerWriteFailureOnce(codeEdit, failureDetail);
			return false;
		}

		AutocompleteCodeEditNativeOwnershipBridge.MarkerReadStatus verificationStatus =
			_nativeOwnershipBridge.Inspect(
				codeEdit,
				out AutocompleteCodeEditNativeOwnershipBridge.OwnershipState verifiedState,
				out string verificationFailure
			);
		if (
			verificationStatus != AutocompleteCodeEditNativeOwnershipBridge.MarkerReadStatus.Valid
			|| !AutocompleteCodeEditNativeOwnershipBridge.MatchesState(verifiedState, state)
		)
		{
			TraceNativeOwnershipMarkerWriteFailureOnce(
				codeEdit,
				$"Fresh marker verification failed. Status='{verificationStatus}', Detail='{verificationFailure}'"
			);
			return false;
		}

		return _codeEditMutationCoordinator.IsBindingActivationForwardAuthorityCurrent(
			activationLease
		);
	}

	private static bool NativeOwnershipOwnerMatches(
		AutocompleteCodeEditNativeOwnershipBridge.OwnershipState state,
		EditorBindingLease lease
	)
	{
		return state != null
			&& !state.IsLegacy
			&& state.SchemaVersion == AutocompleteCodeEditNativeOwnershipBridge.CurrentSchemaVersion
			&& string.Equals(
				state.OwnerManagedAssemblyGeneration,
				lease.ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
			&& state.OwnerHostInstanceToken == lease.HostInstanceToken
			&& state.OwnerScriptTransitionId == lease.ScriptTransitionId
			&& state.OwnerReloadReadyEpoch == lease.ReloadReadyEpoch
			&& state.OwnerBindingEpoch == lease.BindingEpoch
			&& state.CodeEditNativeInstanceId == lease.CodeEditInstanceId
			&& string.Equals(
				ScriptPathUtility.Normalize(state.ScriptResourcePath),
				ScriptPathUtility.Normalize(lease.ScriptResourcePath),
				StringComparison.Ordinal
			);
	}

	private static bool NativeOwnershipOwnerMatches(
		AutocompleteCodeEditNativeOwnershipBridge.OwnershipState state,
		EditorBindingReservation reservation
	)
	{
		return state != null
			&& !state.IsLegacy
			&& state.SchemaVersion == AutocompleteCodeEditNativeOwnershipBridge.CurrentSchemaVersion
			&& string.Equals(
				state.OwnerManagedAssemblyGeneration,
				reservation.ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
			&& state.OwnerHostInstanceToken == reservation.HostInstanceToken
			&& state.OwnerScriptTransitionId == reservation.ScriptTransitionId
			&& state.OwnerReloadReadyEpoch == reservation.ReloadReadyEpoch
			&& state.OwnerBindingEpoch == reservation.BindingEpoch
			&& state.CodeEditNativeInstanceId == reservation.CodeEditInstanceId
			&& string.Equals(
				ScriptPathUtility.Normalize(state.ScriptResourcePath),
				ScriptPathUtility.Normalize(reservation.ScriptResourcePath),
				StringComparison.Ordinal
			);
	}

	private void TraceNativeOwnershipOrphanDetected(
		AutocompleteCodeEditNativeOwnershipBridge.OwnershipState state
	)
	{
		Trace(
			"C# autocomplete native CodeEdit ownership orphan detected",
			$"SchemaVersion='{state?.SchemaVersion ?? 0}', CodeEditNativeInstanceId='{state?.CodeEditNativeInstanceId ?? 0}', PreviousManagedAssemblyGeneration='{state?.OwnerManagedAssemblyGeneration}', CurrentManagedAssemblyGeneration='{_managedAssemblyGeneration}', OwnerHostInstanceToken='{state?.OwnerHostInstanceToken ?? 0}', OwnerScriptTransitionId='{state?.OwnerScriptTransitionId ?? 0}', OwnerBindingEpoch='{state?.OwnerBindingEpoch ?? 0}', PrefixOwned='{state?.PrefixOwned}', CompletionExistingColorOwned='{state?.CompletionExistingColorOwned}'"
		);
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
			$"SchemaVersion='{state?.SchemaVersion ?? 0}', CodeEditNativeInstanceId='{state?.CodeEditNativeInstanceId ?? 0}', PreviousManagedAssemblyGeneration='{state?.OwnerManagedAssemblyGeneration}', CurrentManagedAssemblyGeneration='{_managedAssemblyGeneration}', OwnerHostInstanceToken='{state?.OwnerHostInstanceToken ?? 0}', OwnerScriptTransitionId='{state?.OwnerScriptTransitionId ?? 0}', OwnerBindingEpoch='{state?.OwnerBindingEpoch ?? 0}', Detail='{failureDetail}'"
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
			"C# autocomplete native CodeEdit ownership same-generation stale marker observed",
			$"SchemaVersion='{state?.SchemaVersion ?? 0}', CodeEditNativeInstanceId='{state?.CodeEditNativeInstanceId ?? 0}', ManagedAssemblyGeneration='{_managedAssemblyGeneration}', OwnerHostInstanceToken='{state?.OwnerHostInstanceToken ?? 0}', OwnerScriptTransitionId='{state?.OwnerScriptTransitionId ?? 0}', OwnerBindingEpoch='{state?.OwnerBindingEpoch ?? 0}', PrefixOwned='{state?.PrefixOwned}', CompletionExistingColorOwned='{state?.CompletionExistingColorOwned}'"
		);
	}

	private void TraceNativeOwnershipOwnerMismatchOnce(
		AutocompleteCodeEditNativeOwnershipBridge.OwnershipState state,
		string detail
	)
	{
		if (_nativeOwnershipOwnerMismatchLogged)
			return;

		_nativeOwnershipOwnerMismatchLogged = true;
		Trace(
			"C# autocomplete native CodeEdit ownership owner mismatch",
			$"SchemaVersion='{state?.SchemaVersion ?? 0}', OwnerManagedAssemblyGeneration='{state?.OwnerManagedAssemblyGeneration}', OwnerHostInstanceToken='{state?.OwnerHostInstanceToken ?? 0}', OwnerScriptTransitionId='{state?.OwnerScriptTransitionId ?? 0}', OwnerReloadReadyEpoch='{state?.OwnerReloadReadyEpoch ?? 0}', OwnerBindingEpoch='{state?.OwnerBindingEpoch ?? 0}', CodeEditNativeInstanceId='{state?.CodeEditNativeInstanceId ?? 0}', ScriptPath='{state?.ScriptResourcePath}', Detail='{detail}'"
		);
	}

	private void TraceNativeOwnershipMissingMarkerOnce(EditorBindingLease lease)
	{
		if (_nativeOwnershipMissingMarkerLogged)
			return;

		_nativeOwnershipMissingMarkerLogged = true;
		Trace(
			"C# autocomplete native CodeEdit ownership marker missing during outgoing cleanup",
			$"OwnerBindingEpoch='{lease.BindingEpoch}', OwnerScriptTransitionId='{lease.ScriptTransitionId}', OwnerHostInstanceToken='{lease.HostInstanceToken}', CodeEditInstanceId='{lease.CodeEditInstanceId}', ScriptPath='{lease.ScriptResourcePath}'"
		);
	}

	private void TracePresentationOwnershipChangedIfNeeded(
		AutocompleteCodeEditNativeOwnershipBridge.OwnershipState state,
		AutocompletePresentationRestoreResult prefixResult,
		AutocompletePresentationRestoreResult colorResult,
		AutocompletePresentationRestoreResult remainingThemeResult,
		string recoveryKind,
		long fallbackBindingEpoch = 0,
		ulong fallbackCodeEditInstanceId = 0,
		string fallbackScriptPath = "",
		long fallbackScriptTransitionId = 0,
		long fallbackHostInstanceToken = 0
	)
	{
		if (_nativePresentationOwnershipChangedLogged)
			return;
		if (
			!prefixResult.CurrentStateChangedBeforeRestore
			&& !colorResult.CurrentStateChangedBeforeRestore
			&& !remainingThemeResult.CurrentStateChangedBeforeRestore
		)
		{
			return;
		}

		_nativePresentationOwnershipChangedLogged = true;
		var components = new List<string>();
		if (prefixResult.CurrentStateChangedBeforeRestore)
			components.Add("CodeCompletionPrefixes");
		if (colorResult.CurrentStateChangedBeforeRestore)
			components.Add("completion_existing_color");
		if (remainingThemeResult.CurrentStateChangedBeforeRestore)
			components.Add("ThemeOverrides");

		long ownerBindingEpoch = state?.OwnerBindingEpoch ?? fallbackBindingEpoch;
		long ownerScriptTransitionId = state?.OwnerScriptTransitionId ?? fallbackScriptTransitionId;
		long ownerHostInstanceToken = state?.OwnerHostInstanceToken ?? fallbackHostInstanceToken;
		ulong codeEditInstanceId = state?.CodeEditNativeInstanceId ?? fallbackCodeEditInstanceId;
		string scriptPath = state?.ScriptResourcePath ?? fallbackScriptPath ?? "";
		Trace(
			"C# autocomplete native presentation ownership changed before restore",
			$"Component='{string.Join(",", components)}', OwnerBindingEpoch='{ownerBindingEpoch}', OwnerScriptTransitionId='{ownerScriptTransitionId}', OwnerHostInstanceToken='{ownerHostInstanceToken}', CodeEditInstanceId='{codeEditInstanceId}', ScriptPath='{scriptPath}', RecoveryKind='{recoveryKind}'"
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
