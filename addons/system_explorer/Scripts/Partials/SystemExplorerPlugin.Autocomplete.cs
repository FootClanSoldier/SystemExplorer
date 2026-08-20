#if TOOLS
using Godot;
using System;
using SystemExplorer.Autocomplete;
using SystemExplorer.Autocomplete.Confirmation;
using SystemExplorer.EditorIntegration.ScriptEditing;

public partial class SystemExplorerPlugin
{
	#region C# Autocomplete Integration
	private AutocompletePluginHost _autocompleteHost;
	private string _autocompleteHostManagedAssemblyGeneration = "";
	private long _autocompleteHostInstanceToken;
	private bool _autocompleteHostShutdownInProgress;
	private bool _autocompleteTextChangedRecoveryQueued;
	private long _autocompleteTextChangedRecoveryToken;
	private bool _autocompleteScriptChangePendingAfterTreeKeyboardNavigation;
	private int _autocompleteScriptEditorChangedCallbackDepth;
	private bool _autocompleteDeferredScriptChangeRebindPending;
	private bool _autocompleteDeferredScriptChangeRebindQueued;
	private bool _autocompleteDeferredScriptChangeRebindExecutionActive;
	private long _autocompleteDeferredScriptChangeRebindToken;
	private long _autocompleteDeferredScriptChangeTargetTransitionId;
	private int _autocompleteDeferredScriptChangeCoalescedCount;
	private string _autocompleteDeferredScriptChangeLatestOrigin = "";
	private int _autocompleteCodeEditGuiInputCallbackDepth;
	private AutocompleteDeferredUsingInsertionRequest _autocompleteDeferredUsingInsertionRequest;
	private bool _autocompleteDeferredUsingInsertionPending;
	private bool _autocompleteDeferredUsingInsertionQueued;
	private bool _autocompleteDeferredUsingInsertionExecutionActive;
	private long _autocompleteDeferredUsingInsertionToken;
	private long _autocompleteDeferredUsingInsertionHostInstanceToken;
	private string _autocompleteDeferredUsingInsertionManagedAssemblyGeneration = "";
	private int _suppressedAutocompleteDeferredUsingTextChangedCount;
	private int _suppressedAutocompleteDeferredUsingValidationCount;
	private int _suppressedAutocompleteDeferredUsingCompletionRequestedCount;
	private int _suppressedAutocompleteDeferredUsingGuiInputCount;
	private AutocompleteExternalMutationLease? _autocompleteExternalMutationLease;
	private long _autocompleteExternalMutationOperationToken;
	private string _autocompleteExternalMutationOperationName = "";
	private AutocompleteExternalMutationOrigin _autocompleteExternalMutationOrigin;
	private bool _pendingExternalMutationAutocompleteScriptChange;
	private bool _pendingExternalMutationAutocompleteTextChange;
	private bool _pendingExternalMutationAutocompleteFilesystemChange;
	private bool _pendingExternalMutationAutocompleteProcessFollowUp;
	private int _suppressedExternalMutationAutocompleteScriptChangedCount;
	private int _suppressedExternalMutationAutocompleteTextChangedCount;
	private int _suppressedExternalMutationAutocompleteFilesystemChangedCount;
	private int _suppressedExternalMutationAutocompleteCompletionRequestedCount;
	private int _suppressedExternalMutationAutocompleteGuiInputCount;

	private AutocompletePluginHost CreateAutocompleteHost(
		string hostManagedAssemblyGeneration
	)
	{
		Action<string, string> persistentWorkerDiagnosticLog =
			DebugLogger.CreatePersistentFileOnlyDiagnosticSink();

		return new AutocompletePluginHost(
			hostManagedAssemblyGeneration,
			() => EditorInterface.Singleton?.GetScriptEditor(),
			() => EditorInterface.Singleton?.GetResourceFilesystem(),
			() => ProjectSettings.GlobalizePath("res://"),
			TryConnectPluginSignal,
			DisconnectPluginSignal,
			nameof(OnAutocompleteScriptChanged),
			nameof(OnAutocompleteTextChanged),
			nameof(OnAutocompleteCodeCompletionRequested),
			nameof(OnAutocompleteCodeEditGuiInput),
			nameof(OnAutocompleteProjectFilesystemChanged),
			(operation, details) => DebugLogger.LogOperation(operation, details),
			persistentWorkerDiagnosticLog,
			() => DebugState,
			() => _autocompleteHostInstanceToken,
			() => CurrentAutocompleteReloadReadyEpoch,
			() => IsAutocompleteReloadStabilizationReady(),
			ScriptEditorLifecycleCoordinator,
			RequestScriptEditorLifecycleRebind,
			semanticMemberPipelineEnabled: true,
			cancelNativeCompletionOnRebind: false,
			activeDocumentSyntaxOverlayEnabled: true,
			automaticUsingInsertTextExecutionEnabled: true,
			automaticUsingDeferInsertTextAfterGuiInputEnabled: true,
			automaticUsingComplexOperationWrapperEnabled: false
		);
	}

	private bool IsAutocompletePluginBoundaryAvailable()
	{
		return !_editorOperationShutdownStarted
			&& !_autocompleteHostShutdownInProgress
			&& GodotObject.IsInstanceValid(this)
			&& IsInsideTree();
	}

	private bool IsAutocompleteScriptEditorChangedCallbackActive =>
		_autocompleteScriptEditorChangedCallbackDepth > 0;

	private bool IsAutocompleteScriptChangeRebindBarrierActive =>
		!IsScriptEditorLifecycleStableForCurrentAutocompleteHost()
		|| IsAutocompleteScriptEditorChangedCallbackActive
		|| _autocompleteDeferredScriptChangeRebindPending
		|| _autocompleteDeferredScriptChangeRebindExecutionActive;

	private bool IsAutocompleteDeferredUsingInsertionBarrierActive =>
		_autocompleteDeferredUsingInsertionPending
		|| _autocompleteDeferredUsingInsertionExecutionActive;

	private bool IsAutocompleteExternalMutationActive =>
		_autocompleteExternalMutationLease.HasValue;

	private void EnterAutocompleteCodeEditGuiInputCallbackScope()
	{
		if (_autocompleteCodeEditGuiInputCallbackDepth < int.MaxValue)
		{
			_autocompleteCodeEditGuiInputCallbackDepth++;
			return;
		}

		DebugLogger.LogOperation(
			"C# autocomplete CodeEdit GuiInput callback depth anomaly",
			"Reason='Depth overflow prevented'"
		);
	}

	private void ExitAutocompleteCodeEditGuiInputCallbackScope()
	{
		if (_autocompleteCodeEditGuiInputCallbackDepth <= 0)
		{
			_autocompleteCodeEditGuiInputCallbackDepth = 0;
			DebugLogger.LogOperation(
				"C# autocomplete CodeEdit GuiInput callback depth anomaly",
				"Reason='Depth underflow prevented'"
			);
			return;
		}

		_autocompleteCodeEditGuiInputCallbackDepth--;
	}

	private void EnterAutocompleteScriptEditorChangedCallbackScope()
	{
		if (_autocompleteScriptEditorChangedCallbackDepth < int.MaxValue)
		{
			_autocompleteScriptEditorChangedCallbackDepth++;
			return;
		}

		DebugLogger.LogOperation(
			"C# autocomplete ScriptEditor changed callback depth anomaly",
			"Reason='Depth overflow prevented'"
		);
	}

	private void ExitAutocompleteScriptEditorChangedCallbackScope()
	{
		if (_autocompleteScriptEditorChangedCallbackDepth <= 0)
		{
			_autocompleteScriptEditorChangedCallbackDepth = 0;
			DebugLogger.LogOperation(
				"C# autocomplete ScriptEditor changed callback depth anomaly",
				"Reason='Depth underflow prevented'"
			);
			return;
		}

		_autocompleteScriptEditorChangedCallbackDepth--;
	}

	private long AdvanceDeferredAutocompleteScriptChangeRebindToken()
	{
		unchecked
		{
			_autocompleteDeferredScriptChangeRebindToken++;
			if (_autocompleteDeferredScriptChangeRebindToken <= 0)
				_autocompleteDeferredScriptChangeRebindToken = 1;
		}

		return _autocompleteDeferredScriptChangeRebindToken;
	}

	private void ResetDeferredAutocompleteScriptChangeRebindState(bool invalidateToken)
	{
		_autocompleteDeferredScriptChangeRebindPending = false;
		_autocompleteDeferredScriptChangeRebindQueued = false;
		_autocompleteDeferredScriptChangeTargetTransitionId = 0;
		_autocompleteDeferredScriptChangeCoalescedCount = 0;
		_autocompleteDeferredScriptChangeLatestOrigin = "";

		if (invalidateToken)
			AdvanceDeferredAutocompleteScriptChangeRebindToken();
	}

	private void ConsumeDeferredAutocompleteScriptChangeRebind(long token)
	{
		if (token != _autocompleteDeferredScriptChangeRebindToken)
			return;

		_autocompleteDeferredScriptChangeRebindPending = false;
		_autocompleteDeferredScriptChangeRebindQueued = false;
		_autocompleteDeferredScriptChangeTargetTransitionId = 0;
		_autocompleteDeferredScriptChangeCoalescedCount = 0;
		_autocompleteDeferredScriptChangeLatestOrigin = "";
	}

	private void QueueDeferredAutocompleteScriptChangeRebind(
		string origin,
		bool bypassSystemExplorerNavigationQuiescenceAdmission = false
	)
	{
		if (IsAutocompleteExternalMutationActive)
		{
			_pendingExternalMutationAutocompleteScriptChange = true;
			return;
		}

		if (IsTreeKeyboardNavigationBurstActive)
		{
			_autocompleteScriptChangePendingAfterTreeKeyboardNavigation = true;
			return;
		}

		if (
			!bypassSystemExplorerNavigationQuiescenceAdmission
			&& TryInterceptSystemExplorerNavigationBindingQuiescenceAdmission()
		)
		{
			return;
		}

		ScriptEditorLifecycleSnapshot lifecycleSnapshot =
			ScriptEditorLifecycleCoordinator.Snapshot;
		if (
			lifecycleSnapshot.State != ScriptEditorLifecycleState.BindingPending
			|| lifecycleSnapshot.ScriptTransitionId <= 0
		)
		{
			return;
		}

		_autocompleteDeferredScriptChangeRebindPending = true;
		_autocompleteDeferredScriptChangeTargetTransitionId =
			lifecycleSnapshot.ScriptTransitionId;
		_autocompleteDeferredScriptChangeLatestOrigin = origin ?? "";

		if (_autocompleteDeferredScriptChangeRebindQueued)
		{
			if (_autocompleteDeferredScriptChangeCoalescedCount < int.MaxValue)
				_autocompleteDeferredScriptChangeCoalescedCount++;
			return;
		}

		long token = AdvanceDeferredAutocompleteScriptChangeRebindToken();
		long scheduledHostInstanceToken = _autocompleteHostInstanceToken;
		string scheduledManagedAssemblyGeneration = ManagedAssemblyGeneration;
		_autocompleteDeferredScriptChangeRebindQueued = true;
		_autocompleteDeferredScriptChangeCoalescedCount = 0;


		try
		{
			CallDeferred(
				nameof(ApplyDeferredAutocompleteScriptChangeRebind),
				token,
				scheduledHostInstanceToken,
				scheduledManagedAssemblyGeneration
			);
		}
		catch (Exception exception)
		{
			if (token == _autocompleteDeferredScriptChangeRebindToken)
				ResetDeferredAutocompleteScriptChangeRebindState(invalidateToken: true);

			DebugLogger.LogOperation(
				"C# autocomplete deferred ScriptEditor rebind scheduling failed",
				exception.ToString()
			);
		}
	}


	private long AdvanceDeferredAutocompleteUsingInsertionToken()
	{
		unchecked
		{
			_autocompleteDeferredUsingInsertionToken++;
			if (_autocompleteDeferredUsingInsertionToken <= 0)
				_autocompleteDeferredUsingInsertionToken = 1;
		}

		return _autocompleteDeferredUsingInsertionToken;
	}

	private static void IncrementAutocompleteDeferredUsingSuppression(ref int counter)
	{
		if (counter < int.MaxValue)
			counter++;
	}

	private void ResetDeferredAutocompleteUsingInsertionState(bool invalidateToken)
	{
		_autocompleteDeferredUsingInsertionRequest = null;
		_autocompleteDeferredUsingInsertionPending = false;
		_autocompleteDeferredUsingInsertionQueued = false;
		_autocompleteDeferredUsingInsertionExecutionActive = false;
		_autocompleteDeferredUsingInsertionHostInstanceToken = 0;
		_autocompleteDeferredUsingInsertionManagedAssemblyGeneration = "";
		_suppressedAutocompleteDeferredUsingTextChangedCount = 0;
		_suppressedAutocompleteDeferredUsingValidationCount = 0;
		_suppressedAutocompleteDeferredUsingCompletionRequestedCount = 0;
		_suppressedAutocompleteDeferredUsingGuiInputCount = 0;

		if (invalidateToken)
			AdvanceDeferredAutocompleteUsingInsertionToken();
	}

	private void CancelDeferredAutocompleteUsingInsertion(string reason)
	{
		AutocompleteDeferredUsingInsertionRequest request =
			_autocompleteDeferredUsingInsertionRequest;
		if (request != null && IsAutocompleteDeferredUsingInsertionBarrierActive)
		{
			LogAutocompleteDeferredUsingInsertionRejection(
				reason,
				_autocompleteDeferredUsingInsertionToken,
				request,
				_autocompleteDeferredUsingInsertionHostInstanceToken,
				_autocompleteDeferredUsingInsertionManagedAssemblyGeneration,
				0,
				""
			);
		}

		ResetDeferredAutocompleteUsingInsertionState(invalidateToken: true);
	}

	private void QueueDeferredAutocompleteUsingInsertion(
		AutocompletePluginHost originatingHost,
		AutocompleteDeferredUsingInsertionRequest request
	)
	{
		if (request == null)
			return;

		if (IsAutocompleteDeferredUsingInsertionBarrierActive)
		{
			LogAutocompleteDeferredUsingInsertionRejection(
				"ExistingPendingRequest",
				_autocompleteDeferredUsingInsertionToken,
				request,
				_autocompleteHostInstanceToken,
				ManagedAssemblyGeneration,
				0,
				""
			);
			return;
		}

		if (!ReferenceEquals(originatingHost, _autocompleteHost))
		{
			LogAutocompleteDeferredUsingInsertionRejection(
				"HostChanged",
				_autocompleteDeferredUsingInsertionToken,
				request,
				_autocompleteHostInstanceToken,
				ManagedAssemblyGeneration,
				0,
				""
			);
			return;
		}

		if (
			!string.Equals(
				_autocompleteHostManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
		)
		{
			LogAutocompleteDeferredUsingInsertionRejection(
				"ManagedGenerationChanged",
				_autocompleteDeferredUsingInsertionToken,
				request,
				_autocompleteHostInstanceToken,
				_autocompleteHostManagedAssemblyGeneration,
				0,
				""
			);
			return;
		}

		long token = AdvanceDeferredAutocompleteUsingInsertionToken();
		long hostInstanceToken = _autocompleteHostInstanceToken;
		string managedAssemblyGeneration = ManagedAssemblyGeneration;

		_autocompleteDeferredUsingInsertionRequest = request;
		_autocompleteDeferredUsingInsertionPending = true;
		_autocompleteDeferredUsingInsertionQueued = true;
		_autocompleteDeferredUsingInsertionExecutionActive = false;
		_autocompleteDeferredUsingInsertionHostInstanceToken = hostInstanceToken;
		_autocompleteDeferredUsingInsertionManagedAssemblyGeneration =
			managedAssemblyGeneration;
		_suppressedAutocompleteDeferredUsingTextChangedCount = 0;
		_suppressedAutocompleteDeferredUsingValidationCount = 0;
		_suppressedAutocompleteDeferredUsingCompletionRequestedCount = 0;
		_suppressedAutocompleteDeferredUsingGuiInputCount = 0;

		try
		{
			originatingHost?.InvalidatePendingValidations();
			originatingHost?.ClearPendingCompletionProcessWork();
		}
		catch (Exception exception)
		{
			DebugLogger.LogOperation(
				"C# autocomplete automatic using deferred managed-state invalidation failed",
				$"Token='{token}', ExceptionType='{exception.GetType().FullName}', Exception='{exception}'"
			);
		}

		try
		{
			CallDeferred(
				nameof(ApplyDeferredAutocompleteUsingInsertion),
				token,
				hostInstanceToken,
				managedAssemblyGeneration
			);
		}
		catch (Exception exception)
		{
			DebugLogger.LogOperation(
				"C# autocomplete automatic using deferred InsertText scheduling failed",
				$"Token='{token}', Name='{request.CompletionName}', Namespace='{request.NamespaceName}', OriginatingCompletionPublicationId='{request.OriginatingCompletionPublicationId}', CodeEditNativeInstanceId='{request.CodeEditNativeInstanceId}', ScriptPath='{request.ScriptPath}', ScriptTransitionId='{request.BindingLease.ScriptTransitionId}', BindingEpoch='{request.BindingLease.BindingEpoch}', ReloadReadyEpoch='{request.BindingLease.ReloadReadyEpoch}', HostInstanceToken='{hostInstanceToken}', ManagedAssemblyGeneration='{managedAssemblyGeneration}', GuiInputCallbackDepth='{_autocompleteCodeEditGuiInputCallbackDepth}', ExceptionType='{exception.GetType().FullName}', Exception='{exception}'"
			);
			ResetDeferredAutocompleteUsingInsertionState(invalidateToken: true);
			RefreshEditorPluginProcessingState();
			return;
		}

		DebugLogger.LogOperation(
			"C# autocomplete automatic using deferred InsertText scheduled",
			$"Token='{token}', Name='{request.CompletionName}', Namespace='{request.NamespaceName}', OriginatingCompletionPublicationId='{request.OriginatingCompletionPublicationId}', CodeEditNativeInstanceId='{request.CodeEditNativeInstanceId}', ScriptPath='{request.ScriptPath}', ScriptTransitionId='{request.BindingLease.ScriptTransitionId}', BindingEpoch='{request.BindingLease.BindingEpoch}', ReloadReadyEpoch='{request.BindingLease.ReloadReadyEpoch}', HostInstanceToken='{hostInstanceToken}', ManagedAssemblyGeneration='{managedAssemblyGeneration}', GuiInputCallbackDepth='{_autocompleteCodeEditGuiInputCallbackDepth}'"
		);
		RefreshEditorPluginProcessingState();
	}

	private void ApplyDeferredAutocompleteUsingInsertion(
		long token,
		long scheduledHostInstanceToken,
		string scheduledManagedAssemblyGeneration
	)
	{
		if (
			!string.Equals(
				scheduledManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
		)
		{
			LogStaleDeferredAutocompleteOperation(
				"DeferredAutomaticUsingInsertText",
				scheduledManagedAssemblyGeneration,
				token,
				_autocompleteDeferredUsingInsertionToken,
				scheduledHostInstanceToken,
				_autocompleteHostInstanceToken
			);
			return;
		}

		if (
			token != _autocompleteDeferredUsingInsertionToken
			|| !_autocompleteDeferredUsingInsertionPending
		)
		{
			return;
		}

		if (scheduledHostInstanceToken != _autocompleteHostInstanceToken)
			return;

		_autocompleteDeferredUsingInsertionQueued = false;
		_autocompleteDeferredUsingInsertionExecutionActive = true;

		AutocompleteDeferredUsingInsertionRequest request =
			_autocompleteDeferredUsingInsertionRequest;

		try
		{
			if (request == null)
			{
				RejectDeferredAutocompleteUsingInsertion(
					"PluginUnavailable",
					token,
					request,
					scheduledHostInstanceToken,
					scheduledManagedAssemblyGeneration
				);
				return;
			}

			if (!IsAutocompletePluginBoundaryAvailable())
			{
				RejectDeferredAutocompleteUsingInsertion(
					"PluginUnavailable",
					token,
					request,
					scheduledHostInstanceToken,
					scheduledManagedAssemblyGeneration
				);
				return;
			}

			if (_autocompleteCodeEditGuiInputCallbackDepth != 0)
			{
				RejectDeferredAutocompleteUsingInsertion(
					"GuiInputStillActive",
					token,
					request,
					scheduledHostInstanceToken,
					scheduledManagedAssemblyGeneration
				);
				return;
			}

			if (IsAutocompleteScriptChangeRebindBarrierActive)
			{
				RejectDeferredAutocompleteUsingInsertion(
					"ScriptChangeRebindPending",
					token,
					request,
					scheduledHostInstanceToken,
					scheduledManagedAssemblyGeneration
				);
				return;
			}

			if (IsAutocompleteExternalMutationActive)
			{
				RejectDeferredAutocompleteUsingInsertion(
					"ExternalMutationLease",
					token,
					request,
					scheduledHostInstanceToken,
					scheduledManagedAssemblyGeneration
				);
				return;
			}

			if (IsTreeKeyboardNavigationBurstActive)
			{
				RejectDeferredAutocompleteUsingInsertion(
					"KeyboardNavigationBurst",
					token,
					request,
					scheduledHostInstanceToken,
					scheduledManagedAssemblyGeneration
				);
				return;
			}

			if (_isRecoveringManagedAssemblyState)
			{
				RejectDeferredAutocompleteUsingInsertion(
					"ManagedRecovery",
					token,
					request,
					scheduledHostInstanceToken,
					scheduledManagedAssemblyGeneration
				);
				return;
			}

			if (!HasVerifiedPersistentTreeStateForCurrentAssembly)
			{
				RejectDeferredAutocompleteUsingInsertion(
					"PersistentTreeStateNotCurrent",
					token,
					request,
					scheduledHostInstanceToken,
					scheduledManagedAssemblyGeneration
				);
				return;
			}


			AutocompletePluginHost currentHost = _autocompleteHost;
			if (currentHost == null)
			{
				RejectDeferredAutocompleteUsingInsertion(
					"HostChanged",
					token,
					request,
					scheduledHostInstanceToken,
					scheduledManagedAssemblyGeneration
				);
				return;
			}

			if (
				!string.Equals(
					_autocompleteHostManagedAssemblyGeneration,
					ManagedAssemblyGeneration,
					StringComparison.Ordinal
				)
			)
			{
				RejectDeferredAutocompleteUsingInsertion(
					"ManagedGenerationChanged",
					token,
					request,
					scheduledHostInstanceToken,
					scheduledManagedAssemblyGeneration
				);
				return;
			}

			AutocompleteDeferredUsingInsertionApplyResult applyResult =
				currentHost.TryApplyDeferredUsingInsertion(
					request,
					scheduledHostInstanceToken,
					scheduledManagedAssemblyGeneration,
					_autocompleteCodeEditGuiInputCallbackDepth
				);

			if (applyResult?.Succeeded == true)
			{
				ResetDeferredAutocompleteUsingInsertionState(invalidateToken: true);
				RefreshEditorPluginProcessingState();
				return;
			}

			string failureReason = applyResult?.FailureReason ?? "PluginUnavailable";
			if (
				string.Equals(
					failureReason,
					AutocompleteProjectTypeConfirmationService.UsingActionFailedAfterConfirmationDeferred,
					StringComparison.Ordinal
				)
			)
			{
				ResetDeferredAutocompleteUsingInsertionState(invalidateToken: true);
				RefreshEditorPluginProcessingState();
				return;
			}

			RejectDeferredAutocompleteUsingInsertion(
				failureReason,
				token,
				request,
				scheduledHostInstanceToken,
				scheduledManagedAssemblyGeneration,
				applyResult?.CurrentCodeEditNativeInstanceId ?? 0UL,
				applyResult?.CurrentScriptPath ?? "",
				applyResult?.CurrentBindingLease
			);
		}
		catch (Exception exception)
		{
			DebugLogger.LogOperation(
				"C# autocomplete automatic using deferred InsertText failed after confirmation",
				$"UsingAction='{AutocompleteProjectTypeConfirmationService.UsingActionFailedAfterConfirmationDeferred}', Token='{token}', Name='{request?.CompletionName ?? ""}', Namespace='{request?.NamespaceName ?? ""}', OriginatingCompletionPublicationId='{request?.OriginatingCompletionPublicationId ?? 0}', CodeEditNativeInstanceId='{request?.CodeEditNativeInstanceId ?? 0UL}', ScriptPath='{request?.ScriptPath ?? ""}', ExpectedScriptTransitionId='{request?.BindingLease.ScriptTransitionId ?? 0}', ExpectedBindingEpoch='{request?.BindingLease.BindingEpoch ?? 0}', ExpectedReloadReadyEpoch='{request?.BindingLease.ReloadReadyEpoch ?? 0}', HostInstanceToken='{scheduledHostInstanceToken}', ManagedAssemblyGeneration='{scheduledManagedAssemblyGeneration}', GuiInputCallbackDepth='{_autocompleteCodeEditGuiInputCallbackDepth}', ExceptionType='{exception.GetType().FullName}', Exception='{exception}'"
			);
			ResetDeferredAutocompleteUsingInsertionState(invalidateToken: true);
			RefreshEditorPluginProcessingState();
		}
	}

	private void RejectDeferredAutocompleteUsingInsertion(
		string reason,
		long token,
		AutocompleteDeferredUsingInsertionRequest request,
		long scheduledHostInstanceToken,
		string scheduledManagedAssemblyGeneration,
		ulong currentCodeEditNativeInstanceId = 0,
		string currentScriptPath = "",
		EditorBindingLease? currentBindingLease = null
	)
	{
		LogAutocompleteDeferredUsingInsertionRejection(
			reason,
			token,
			request,
			scheduledHostInstanceToken,
			scheduledManagedAssemblyGeneration,
			currentCodeEditNativeInstanceId,
			currentScriptPath,
			currentBindingLease
		);
		ResetDeferredAutocompleteUsingInsertionState(invalidateToken: true);
		RefreshEditorPluginProcessingState();
	}

	private void LogAutocompleteDeferredUsingInsertionRejection(
		string reason,
		long token,
		AutocompleteDeferredUsingInsertionRequest request,
		long scheduledHostInstanceToken,
		string scheduledManagedAssemblyGeneration,
		ulong currentCodeEditNativeInstanceId,
		string currentScriptPath,
		EditorBindingLease? currentBindingLease = null
	)
	{
		string currentCodeEditIdentity = currentCodeEditNativeInstanceId == 0
			? "<not-resolved>"
			: currentCodeEditNativeInstanceId.ToString();

		DebugLogger.LogOperation(
			"C# autocomplete automatic using deferred InsertText rejected",
			$"Reason='{reason ?? ""}', Token='{token}', Name='{request?.CompletionName ?? ""}', Namespace='{request?.NamespaceName ?? ""}', OriginatingCompletionPublicationId='{request?.OriginatingCompletionPublicationId ?? 0}', ExpectedCodeEditNativeInstanceId='{request?.CodeEditNativeInstanceId ?? 0UL}', CurrentCodeEditNativeInstanceId='{currentCodeEditIdentity}', ScriptPath='{request?.ScriptPath ?? ""}', CurrentScriptPath='{currentScriptPath ?? ""}', ExpectedScriptTransitionId='{request?.BindingLease.ScriptTransitionId ?? 0}', CurrentScriptTransitionId='{currentBindingLease?.ScriptTransitionId ?? 0}', ExpectedBindingEpoch='{request?.BindingLease.BindingEpoch ?? 0}', CurrentBindingEpoch='{currentBindingLease?.BindingEpoch ?? 0}', ExpectedReloadReadyEpoch='{request?.BindingLease.ReloadReadyEpoch ?? 0}', CurrentReloadReadyEpoch='{currentBindingLease?.ReloadReadyEpoch ?? 0}', ScheduledHostInstanceToken='{scheduledHostInstanceToken}', CurrentHostInstanceToken='{_autocompleteHostInstanceToken}', ScheduledManagedAssemblyGeneration='{scheduledManagedAssemblyGeneration ?? ""}', CurrentManagedAssemblyGeneration='{ManagedAssemblyGeneration}', GuiInputCallbackDepth='{_autocompleteCodeEditGuiInputCallbackDepth}', SuppressedTextChanged='{_suppressedAutocompleteDeferredUsingTextChangedCount}', SuppressedValidation='{_suppressedAutocompleteDeferredUsingValidationCount}', SuppressedCompletionRequested='{_suppressedAutocompleteDeferredUsingCompletionRequestedCount}', SuppressedGuiInput='{_suppressedAutocompleteDeferredUsingGuiInputCount}'"
		);
	}

	private long AdvanceAutocompleteExternalMutationOperationToken()
	{
		unchecked
		{
			_autocompleteExternalMutationOperationToken++;
			if (_autocompleteExternalMutationOperationToken <= 0)
				_autocompleteExternalMutationOperationToken = 1;
		}

		return _autocompleteExternalMutationOperationToken;
	}

	private bool TryBeginAutocompleteExternalMutation(
		AutocompleteExternalMutationOrigin origin,
		string operationName,
		out long operationToken
	)
	{
		operationToken = 0;
		if (IsAutocompleteExternalMutationActive)
		{
			DebugLogger.LogPersistentFileOnlyOperation(
				"C# autocomplete external mutation begin rejected",
				$"Reason='ExternalMutationAlreadyActive', Origin='{origin}', Operation='{operationName ?? ""}', ActiveMutationTransactionId='{_autocompleteExternalMutationLease?.MutationTransactionId ?? 0}', ActiveOrigin='{_autocompleteExternalMutationOrigin}', ActiveOperation='{_autocompleteExternalMutationOperationName}', HostInstanceToken='{_autocompleteHostInstanceToken}', ManagedAssemblyGeneration='{ManagedAssemblyGeneration}'"
			);
			return false;
		}

		if (!IsAutocompletePluginBoundaryAvailable())
			return false;

		if (!EnsureManagedAssemblyStateCurrent("C# Autocomplete External Mutation Begin"))
			return false;

		if (!IsAutocompletePluginBoundaryAvailable())
			return false;

		if (!TryEnsureAutocompleteHost(out AutocompletePluginHost host))
			return false;

		long hostInstanceToken = _autocompleteHostInstanceToken;
		bool pendingProcessFollowUp = false;
		try
		{
			pendingProcessFollowUp = host.HasPendingCompletionProcessWork();
		}
		catch
		{
			// Optional managed follow-up state must not control lease admission.
		}

		if (
			!host.TryBeginExternalMutation(
				hostInstanceToken,
				origin,
				operationName,
				out AutocompleteExternalMutationLease lease
			)
		)
		{
			return false;
		}

		operationToken = AdvanceAutocompleteExternalMutationOperationToken();
		_autocompleteExternalMutationLease = lease;
		_autocompleteExternalMutationOrigin = origin;
		_autocompleteExternalMutationOperationName = operationName;
		_pendingExternalMutationAutocompleteScriptChange = false;
		_pendingExternalMutationAutocompleteTextChange = false;
		_pendingExternalMutationAutocompleteFilesystemChange = false;
		_pendingExternalMutationAutocompleteProcessFollowUp = pendingProcessFollowUp;
		_suppressedExternalMutationAutocompleteScriptChangedCount = 0;
		_suppressedExternalMutationAutocompleteTextChangedCount = 0;
		_suppressedExternalMutationAutocompleteFilesystemChangedCount = 0;
		_suppressedExternalMutationAutocompleteCompletionRequestedCount = 0;
		_suppressedExternalMutationAutocompleteGuiInputCount = 0;

		DebugLogger.LogPersistentFileOnlyOperation(
			"C# autocomplete external mutation envelope begin",
			$"OperationToken='{operationToken}', MutationTransactionId='{lease.MutationTransactionId}', Origin='{lease.Origin}', Operation='{lease.OperationName}', HostInstanceToken='{lease.HostInstanceToken}', ManagedAssemblyGeneration='{lease.ManagedAssemblyGeneration}', PendingProcessFollowUp='{pendingProcessFollowUp}'"
		);
		RefreshEditorPluginProcessingState();
		return true;
	}

	private bool IsAutocompleteExternalMutationOperationCurrent(
		long operationToken,
		AutocompleteExternalMutationOrigin expectedOrigin,
		string expectedOperationName,
		string scheduledManagedAssemblyGeneration
	)
	{
		if (
			!string.Equals(
				scheduledManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
			|| operationToken <= 0
			|| operationToken != _autocompleteExternalMutationOperationToken
			|| !_autocompleteExternalMutationLease.HasValue
			|| _autocompleteExternalMutationOrigin != expectedOrigin
			|| !string.Equals(
				_autocompleteExternalMutationOperationName,
				expectedOperationName,
				StringComparison.Ordinal
			)
		)
		{
			return false;
		}

		AutocompleteExternalMutationLease lease = _autocompleteExternalMutationLease.Value;
		if (
			lease.Origin != expectedOrigin
			|| !string.Equals(
				lease.OperationName,
				expectedOperationName,
				StringComparison.Ordinal
			)
			|| !string.Equals(
				lease.ManagedAssemblyGeneration,
				scheduledManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
			|| !IsAutocompletePluginBoundaryAvailable()
			|| _autocompleteHost == null
			|| !IsAutocompleteHostManagedAssemblyGenerationCurrent()
		)
		{
			return false;
		}

		return _autocompleteHost.IsExternalMutationAuthorityCurrent(lease);
	}

	private void ResetAutocompleteExternalMutationState(bool invalidateToken)
	{
		_autocompleteExternalMutationLease = null;
		_autocompleteExternalMutationOperationName = "";
		_autocompleteExternalMutationOrigin = AutocompleteExternalMutationOrigin.None;
		_pendingExternalMutationAutocompleteScriptChange = false;
		_pendingExternalMutationAutocompleteTextChange = false;
		_pendingExternalMutationAutocompleteFilesystemChange = false;
		_pendingExternalMutationAutocompleteProcessFollowUp = false;
		_suppressedExternalMutationAutocompleteScriptChangedCount = 0;
		_suppressedExternalMutationAutocompleteTextChangedCount = 0;
		_suppressedExternalMutationAutocompleteFilesystemChangedCount = 0;
		_suppressedExternalMutationAutocompleteCompletionRequestedCount = 0;
		_suppressedExternalMutationAutocompleteGuiInputCount = 0;

		if (invalidateToken)
			AdvanceAutocompleteExternalMutationOperationToken();
	}

	private void ScheduleAutocompleteExternalMutationRelease(long operationToken)
	{
		if (
			!_autocompleteExternalMutationLease.HasValue
			|| operationToken != _autocompleteExternalMutationOperationToken
		)
		{
			return;
		}

		AutocompleteExternalMutationLease lease = _autocompleteExternalMutationLease.Value;
		if (!IsAutocompletePluginBoundaryAvailable())
		{
			DebugLogger.LogPersistentFileOnlyOperation(
				"C# autocomplete external mutation release scheduling rejected",
				$"Reason='PluginBoundaryUnavailable', OperationToken='{operationToken}', MutationTransactionId='{lease.MutationTransactionId}', Origin='{lease.Origin}', Operation='{lease.OperationName}', HostInstanceToken='{lease.HostInstanceToken}', ManagedAssemblyGeneration='{lease.ManagedAssemblyGeneration}'"
			);
			return;
		}

		try
		{
			CallDeferred(
				nameof(CompleteAutocompleteExternalMutationDeferred),
				operationToken,
				lease.MutationTransactionId,
				lease.HostInstanceToken,
				lease.ManagedAssemblyGeneration
			);
		}
		catch (Exception exception)
		{
			DebugLogger.LogPersistentFileOnlyOperation(
				"C# autocomplete external mutation release scheduling failed",
				$"OperationToken='{operationToken}', MutationTransactionId='{lease.MutationTransactionId}', Origin='{lease.Origin}', Operation='{lease.OperationName}', HostInstanceToken='{lease.HostInstanceToken}', ManagedAssemblyGeneration='{lease.ManagedAssemblyGeneration}', Exception='{exception}'"
			);
			throw;
		}
	}

	private void CompleteAutocompleteExternalMutationDeferred(
		long operationToken,
		long scheduledMutationTransactionId,
		long scheduledHostInstanceToken,
		string scheduledManagedAssemblyGeneration
	)
	{
		if (
			!string.Equals(
				scheduledManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
		)
		{
			LogStaleDeferredAutocompleteOperation(
				"ExternalMutationRelease",
				scheduledManagedAssemblyGeneration,
				operationToken,
				_autocompleteExternalMutationOperationToken,
				scheduledHostInstanceToken,
				_autocompleteHostInstanceToken
			);
			if (
				_autocompleteExternalMutationLease.HasValue
				&& operationToken == _autocompleteExternalMutationOperationToken
				&& _autocompleteExternalMutationLease.Value.MutationTransactionId
					== scheduledMutationTransactionId
				&& _autocompleteExternalMutationLease.Value.HostInstanceToken
					== scheduledHostInstanceToken
				&& string.Equals(
					_autocompleteExternalMutationLease.Value.ManagedAssemblyGeneration,
					scheduledManagedAssemblyGeneration,
					StringComparison.Ordinal
				)
			)
			{
				ResetAutocompleteExternalMutationState(invalidateToken: true);
			}
			return;
		}

		if (operationToken != _autocompleteExternalMutationOperationToken)
		{
			LogAutocompleteExternalMutationStaleRelease(
				"OperationTokenChanged",
				operationToken,
				scheduledMutationTransactionId,
				scheduledHostInstanceToken,
				scheduledManagedAssemblyGeneration
			);
			return;
		}

		if (scheduledHostInstanceToken != _autocompleteHostInstanceToken)
		{
			LogAutocompleteExternalMutationStaleRelease(
				"HostInstanceTokenChanged",
				operationToken,
				scheduledMutationTransactionId,
				scheduledHostInstanceToken,
				scheduledManagedAssemblyGeneration
			);
			return;
		}

		if (!_autocompleteExternalMutationLease.HasValue)
		{
			LogAutocompleteExternalMutationStaleRelease(
				"LocalLeaseMissing",
				operationToken,
				scheduledMutationTransactionId,
				scheduledHostInstanceToken,
				scheduledManagedAssemblyGeneration
			);
			return;
		}

		AutocompleteExternalMutationLease lease = _autocompleteExternalMutationLease.Value;
		if (
			lease.MutationTransactionId != scheduledMutationTransactionId
			|| lease.HostInstanceToken != scheduledHostInstanceToken
			|| !string.Equals(
				lease.ManagedAssemblyGeneration,
				scheduledManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
		)
		{
			LogAutocompleteExternalMutationStaleRelease(
				"StoredLeaseIdentityChanged",
				operationToken,
				scheduledMutationTransactionId,
				scheduledHostInstanceToken,
				scheduledManagedAssemblyGeneration
			);
			return;
		}

		if (!IsAutocompletePluginBoundaryAvailable())
		{
			ResetAutocompleteExternalMutationState(invalidateToken: true);
			return;
		}

		AutocompletePluginHost host = _autocompleteHost;
		if (
			host == null
			|| !IsAutocompleteHostManagedAssemblyGenerationCurrent()
			|| !host.IsExternalMutationAuthorityCurrent(lease)
		)
		{
			LogAutocompleteExternalMutationStaleRelease(
				"CoordinatorAuthorityNotCurrent",
				operationToken,
				scheduledMutationTransactionId,
				scheduledHostInstanceToken,
				scheduledManagedAssemblyGeneration
			);
			ResetAutocompleteExternalMutationState(invalidateToken: true);
			return;
		}

		string operationName = _autocompleteExternalMutationOperationName;
		AutocompleteExternalMutationOrigin origin = _autocompleteExternalMutationOrigin;
		bool pendingScriptChange = _pendingExternalMutationAutocompleteScriptChange;
		bool pendingTextChange = _pendingExternalMutationAutocompleteTextChange;
		bool pendingFilesystemChange = _pendingExternalMutationAutocompleteFilesystemChange;
		bool pendingProcessFollowUp = _pendingExternalMutationAutocompleteProcessFollowUp;
		int suppressedScriptChangedCount = _suppressedExternalMutationAutocompleteScriptChangedCount;
		int suppressedTextChangedCount = _suppressedExternalMutationAutocompleteTextChangedCount;
		int suppressedFilesystemChangedCount = _suppressedExternalMutationAutocompleteFilesystemChangedCount;
		int suppressedCompletionRequestedCount = _suppressedExternalMutationAutocompleteCompletionRequestedCount;
		int suppressedGuiInputCount = _suppressedExternalMutationAutocompleteGuiInputCount;

		bool rebindCatchUp = false;
		bool projectRefreshCatchUp = false;
		bool releaseSucceeded = false;
		try
		{
			releaseSucceeded = host.EndExternalMutation(lease);
			if (!releaseSucceeded)
				return;

			ResetAutocompleteExternalMutationState(invalidateToken: true);

			bool observedAutocompleteWork =
				pendingScriptChange
					|| pendingTextChange
					|| pendingFilesystemChange
					|| pendingProcessFollowUp;
			if (!observedAutocompleteWork)
				return;

			host.ClearPendingCompletionProcessWork();

			if (pendingScriptChange || pendingTextChange)
			{
				host.InvalidatePendingValidations();
				RequestScriptEditorLifecycleRebind("ExternalMutationRelease");
				rebindCatchUp = true;
			}

			if (pendingFilesystemChange)
			{
				host.HandleProjectFilesystemChanged();
				projectRefreshCatchUp = true;
			}

			RefreshEditorPluginProcessingState();
		}
		finally
		{
			DebugLogger.LogPersistentFileOnlyOperation(
				"C# autocomplete external mutation consolidated release",
				$"OperationToken='{operationToken}', MutationTransactionId='{lease.MutationTransactionId}', Origin='{origin}', Operation='{operationName}', ManagedAssemblyGeneration='{lease.ManagedAssemblyGeneration}', HostInstanceToken='{lease.HostInstanceToken}', ReleaseSucceeded='{releaseSucceeded}', SuppressedScriptChanged='{suppressedScriptChangedCount}', SuppressedTextChanged='{suppressedTextChangedCount}', SuppressedFilesystemChanged='{suppressedFilesystemChangedCount}', SuppressedCompletionRequested='{suppressedCompletionRequestedCount}', SuppressedGuiInput='{suppressedGuiInputCount}', PendingProcessFollowUp='{pendingProcessFollowUp}', RebindCatchUp='{rebindCatchUp}', ProjectRefreshCatchUp='{projectRefreshCatchUp}'"
			);
		}
	}

	private void LogAutocompleteExternalMutationStaleRelease(
		string reason,
		long operationToken,
		long scheduledMutationTransactionId,
		long scheduledHostInstanceToken,
		string scheduledManagedAssemblyGeneration
	)
	{
		DebugLogger.LogPersistentFileOnlyOperation(
			"C# autocomplete external mutation stale release rejected",
			$"Reason='{reason}', ScheduledOperationToken='{operationToken}', CurrentOperationToken='{_autocompleteExternalMutationOperationToken}', ScheduledMutationTransactionId='{scheduledMutationTransactionId}', CurrentMutationTransactionId='{_autocompleteExternalMutationLease?.MutationTransactionId ?? 0}', ScheduledHostInstanceToken='{scheduledHostInstanceToken}', CurrentHostInstanceToken='{_autocompleteHostInstanceToken}', ScheduledManagedAssemblyGeneration='{scheduledManagedAssemblyGeneration ?? ""}', CurrentManagedAssemblyGeneration='{ManagedAssemblyGeneration}', ExternalMutationActive='{IsAutocompleteExternalMutationActive}'"
		);
	}

	private long AdvanceAutocompleteHostInstanceToken()
	{
		unchecked
		{
			_autocompleteHostInstanceToken++;
			if (_autocompleteHostInstanceToken <= 0)
				_autocompleteHostInstanceToken = 1;
		}

		return _autocompleteHostInstanceToken;
	}

	private bool IsAutocompleteHostManagedAssemblyGenerationCurrent()
	{
		return _autocompleteHost != null
			&& string.Equals(
				_autocompleteHostManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			);
	}

	private void RetireAutocompleteHostForManagedAssemblyGenerationMismatch(
		string origin
	)
	{
		if (_autocompleteHost == null)
		{
			_autocompleteHostManagedAssemblyGeneration = "";
			return;
		}

		long detachedHostInstanceToken = _autocompleteHostInstanceToken;
		string detachedHostManagedAssemblyGeneration =
			_autocompleteHostManagedAssemblyGeneration;

		DebugLogger.LogOperation(
			"C# autocomplete stale managed-generation host detected",
			$"Origin='{origin}', DetachedHostInstanceToken='{detachedHostInstanceToken}', HostManagedAssemblyGeneration='{detachedHostManagedAssemblyGeneration}', CurrentManagedAssemblyGeneration='{ManagedAssemblyGeneration}', Reason='Host managed assembly generation is empty or does not exactly match the current managed assembly generation.'"
		);

		ShutdownAutocomplete();

		DebugLogger.LogOperation(
			"C# autocomplete stale managed-generation host retirement completed",
			() =>
				$"Origin='{origin}', DetachedHostInstanceToken='{detachedHostInstanceToken}', DetachedHostManagedAssemblyGeneration='{detachedHostManagedAssemblyGeneration}', CurrentManagedAssemblyGeneration='{ManagedAssemblyGeneration}', CurrentHostNull='{_autocompleteHost == null}', CurrentHostManagedAssemblyGeneration='{_autocompleteHostManagedAssemblyGeneration}', NextEnsure='ColdCompose'"
		);
	}

	private bool TryEnsureAutocompleteHost(out AutocompletePluginHost host)
	{
		host = null;
		bool hostGenerationMismatch =
			_autocompleteHost != null
			&& !IsAutocompleteHostManagedAssemblyGenerationCurrent();
		bool traceEnsureCall =
			_autocompleteHost == null
			|| _isRecoveringManagedAssemblyState
			|| _autocompleteHostShutdownInProgress
			|| hostGenerationMismatch;
		if (traceEnsureCall)
		{
			DebugLogger.LogOperation(
				"C# autocomplete TryEnsureAutocompleteHost begin",
				() =>
					$"HostNull='{_autocompleteHost == null}', HostInstanceToken='{_autocompleteHostInstanceToken}', HostManagedAssemblyGeneration='{_autocompleteHostManagedAssemblyGeneration}', ShutdownInProgress='{_autocompleteHostShutdownInProgress}', ManagedRecoveryInProgress='{_isRecoveringManagedAssemblyState}', ManagedAssemblyGeneration='{ManagedAssemblyGeneration}'"
			);
		}

		if (hostGenerationMismatch)
		{
			RetireAutocompleteHostForManagedAssemblyGenerationMismatch(
				"TryEnsureAutocompleteHost"
			);
		}

		if (!IsAutocompletePluginBoundaryAvailable())
		{
			if (traceEnsureCall)
			{
				DebugLogger.LogOperation(
					"C# autocomplete TryEnsureAutocompleteHost completed",
					() =>
						$"Result='False', Reason='Plugin boundary unavailable', HostNull='{_autocompleteHost == null}', HostInstanceToken='{_autocompleteHostInstanceToken}', HostManagedAssemblyGeneration='{_autocompleteHostManagedAssemblyGeneration}'"
				);
			}
			return false;
		}

		if (_autocompleteHost != null)
		{
			if (!IsAutocompleteHostManagedAssemblyGenerationCurrent())
			{
				RetireAutocompleteHostForManagedAssemblyGenerationMismatch(
					"TryEnsureAutocompleteHostPostBoundary"
				);
				if (!IsAutocompletePluginBoundaryAvailable())
					return false;
			}
		}

		if (_autocompleteHost != null)
		{
			host = _autocompleteHost;
			if (traceEnsureCall)
			{
				DebugLogger.LogOperation(
					"C# autocomplete existing host reused",
					$"HostInstanceToken='{_autocompleteHostInstanceToken}', HostManagedAssemblyGeneration='{_autocompleteHostManagedAssemblyGeneration}', CurrentManagedAssemblyGeneration='{ManagedAssemblyGeneration}', GenerationMatched='True'"
				);
				DebugLogger.LogOperation(
					"C# autocomplete TryEnsureAutocompleteHost completed",
					$"Result='True', ExistingHost='True', HostInstanceToken='{_autocompleteHostInstanceToken}', HostManagedAssemblyGeneration='{_autocompleteHostManagedAssemblyGeneration}'"
				);
			}
			return true;
		}

		_autocompleteHostManagedAssemblyGeneration = "";

		try
		{
			long previousHostInstanceToken = _autocompleteHostInstanceToken;
			string hostManagedAssemblyGeneration = ManagedAssemblyGeneration;
			DebugLogger.LogOperation(
				"C# autocomplete host composition begin",
				$"ExistingHost='False', HostInstanceTokenBefore='{previousHostInstanceToken}', ManagedAssemblyGeneration='{hostManagedAssemblyGeneration}'"
			);
			AutocompletePluginHost composedHost = CreateAutocompleteHost(
				hostManagedAssemblyGeneration
			);
			DebugLogger.LogOperation(
				"C# autocomplete host composition constructed",
				$"HostInstanceTokenBeforePublish='{_autocompleteHostInstanceToken}', ManagedAssemblyGeneration='{hostManagedAssemblyGeneration}'"
			);
			long hostInstanceToken = AdvanceAutocompleteHostInstanceToken();
			_autocompleteHostManagedAssemblyGeneration = hostManagedAssemblyGeneration;
			_autocompleteHost = composedHost;
			host = composedHost;
			DebugLogger.LogOperation(
				"C# autocomplete host restored",
				$"Rebuilt the managed autocomplete feature graph. HostInstanceToken='{hostInstanceToken}', HostManagedAssemblyGeneration='{hostManagedAssemblyGeneration}', CurrentManagedAssemblyGeneration='{ManagedAssemblyGeneration}'"
			);
			DebugLogger.LogOperation(
				"C# autocomplete TryEnsureAutocompleteHost completed",
				$"Result='True', ExistingHost='False', HostInstanceTokenBefore='{previousHostInstanceToken}', HostInstanceTokenAfter='{hostInstanceToken}', HostManagedAssemblyGeneration='{hostManagedAssemblyGeneration}'"
			);
			return true;
		}
		catch (Exception exception)
		{
			_autocompleteHost = null;
			_autocompleteHostManagedAssemblyGeneration = "";
			host = null;
			DebugLogger.LogOperation(
				"C# autocomplete host recovery failed: composition",
				exception.ToString()
			);
			DebugLogger.LogOperation(
				"C# autocomplete TryEnsureAutocompleteHost completed",
				$"Result='False', ExistingHost='False', HostInstanceToken='{_autocompleteHostInstanceToken}', HostManagedAssemblyGeneration='{_autocompleteHostManagedAssemblyGeneration}'"
			);
			return false;
		}
	}

	private bool EnsureAutocompleteLifecycleCurrent()
	{
		bool lifecycleCurrent =
			TryEnsureAutocompleteHost(out AutocompletePluginHost host)
			&& host.EnsureLifecycleCurrent();

		if (lifecycleCurrent)
			EnsureScriptEditorLifecycleRecoveryQueued("EnsureAutocompleteLifecycleCurrent");

		RefreshEditorPluginProcessingState();
		return lifecycleCurrent;
	}

	private void ResetAutocompleteTransientStateAfterManagedAssemblyReload()
	{
		InvalidateAutocompleteScriptTransitionStabilizationAuthority();
		InvalidateScriptEditorLifecycle(
			"ResetAutocompleteTransientStateAfterManagedAssemblyReload"
		);
		DebugLogger.LogOperation(
			"C# autocomplete managed reload transient reset begin",
			() =>
				$"HostNull='{_autocompleteHost == null}', HostInstanceToken='{_autocompleteHostInstanceToken}', HostManagedAssemblyGeneration='{_autocompleteHostManagedAssemblyGeneration}', ShutdownInProgress='{_autocompleteHostShutdownInProgress}', ManagedRecoveryInProgress='{_isRecoveringManagedAssemblyState}', ManagedAssemblyGeneration='{ManagedAssemblyGeneration}'"
		);
		ResetAutocompleteTextChangedRecoveryState(invalidateToken: true);
		_autocompleteScriptChangePendingAfterTreeKeyboardNavigation = false;
		CancelDeferredAutocompleteUsingInsertion("ManagedGenerationChanged");
		ResetDeferredAutocompleteScriptChangeRebindState(invalidateToken: true);
		ResetAutocompleteExternalMutationState(invalidateToken: true);

		AutocompletePluginHost host = _autocompleteHost;
		if (host != null && IsAutocompleteHostManagedAssemblyGenerationCurrent())
		{
			DebugLogger.LogOperation(
				"C# autocomplete managed reload host ResetTransientState begin",
				$"HostInstanceToken='{_autocompleteHostInstanceToken}', HostManagedAssemblyGeneration='{_autocompleteHostManagedAssemblyGeneration}', CurrentManagedAssemblyGeneration='{ManagedAssemblyGeneration}', GenerationMatched='True'"
			);
			host.ResetTransientState();
			DebugLogger.LogOperation(
				"C# autocomplete managed reload host ResetTransientState completed",
				$"HostInstanceToken='{_autocompleteHostInstanceToken}', HostManagedAssemblyGeneration='{_autocompleteHostManagedAssemblyGeneration}'"
			);
		}
		else if (host != null)
		{
			RetireAutocompleteHostForManagedAssemblyGenerationMismatch(
				"ManagedReloadReset"
			);
		}
		else
		{
			_autocompleteHostManagedAssemblyGeneration = "";
			DebugLogger.LogOperation(
				"C# autocomplete managed reload host ResetTransientState skipped",
				$"Reason='Host reference is null', HostInstanceToken='{_autocompleteHostInstanceToken}', HostManagedAssemblyGeneration='{_autocompleteHostManagedAssemblyGeneration}'"
			);
		}

		RefreshEditorPluginProcessingState();
		DebugLogger.LogOperation(
			"C# autocomplete managed reload transient reset completed",
			() =>
				$"HostNull='{_autocompleteHost == null}', HostInstanceToken='{_autocompleteHostInstanceToken}', HostManagedAssemblyGeneration='{_autocompleteHostManagedAssemblyGeneration}'"
		);
	}

	private void ShutdownAutocomplete()
	{
		InvalidateAutocompleteScriptTransitionStabilizationAuthority();
		InvalidateScriptEditorLifecycle("ShutdownAutocomplete");
		InvalidateAutocompleteReloadStabilizationAuthority(parkObservation: true);
		DebugLogger.LogOperation(
			"C# autocomplete ShutdownAutocomplete begin",
			() =>
				$"HostNull='{_autocompleteHost == null}', HostInstanceToken='{_autocompleteHostInstanceToken}', HostManagedAssemblyGeneration='{_autocompleteHostManagedAssemblyGeneration}', ShutdownInProgress='{_autocompleteHostShutdownInProgress}', ManagedRecoveryInProgress='{_isRecoveringManagedAssemblyState}'"
		);
		ResetAutocompleteTextChangedRecoveryState(invalidateToken: true);
		_autocompleteScriptChangePendingAfterTreeKeyboardNavigation = false;
		CancelDeferredAutocompleteUsingInsertion("PluginUnavailable");
		ResetDeferredAutocompleteScriptChangeRebindState(invalidateToken: true);
		ResetAutocompleteExternalMutationState(invalidateToken: true);

		AutocompletePluginHost host = _autocompleteHost;
		if (host == null)
		{
			_autocompleteHostManagedAssemblyGeneration = "";
			DebugLogger.LogOperation(
				"C# autocomplete ShutdownAutocomplete completed",
				$"HostPresent='False', HostInstanceToken='{_autocompleteHostInstanceToken}', HostManagedAssemblyGeneration='{_autocompleteHostManagedAssemblyGeneration}'"
			);
			return;
		}

		long detachedHostInstanceToken = _autocompleteHostInstanceToken;
		string detachedHostManagedAssemblyGeneration =
			_autocompleteHostManagedAssemblyGeneration;
		_autocompleteHostShutdownInProgress = true;
		_autocompleteHost = null;
		_autocompleteHostManagedAssemblyGeneration = "";
		long invalidatedHostInstanceToken = AdvanceAutocompleteHostInstanceToken();
		DebugLogger.LogOperation(
			"C# autocomplete host detached before Shutdown",
			$"DetachedHostInstanceToken='{detachedHostInstanceToken}', DetachedHostManagedAssemblyGeneration='{detachedHostManagedAssemblyGeneration}', CurrentHostInstanceToken='{invalidatedHostInstanceToken}', CurrentHostManagedAssemblyGeneration='{_autocompleteHostManagedAssemblyGeneration}', ShutdownInProgress='{_autocompleteHostShutdownInProgress}'"
		);

		try
		{
			DebugLogger.LogOperation(
				"C# autocomplete detached host Shutdown begin",
				$"DetachedHostInstanceToken='{detachedHostInstanceToken}', DetachedHostManagedAssemblyGeneration='{detachedHostManagedAssemblyGeneration}'"
			);
			host.Shutdown();
			DebugLogger.LogOperation(
				"C# autocomplete detached host Shutdown completed",
				$"DetachedHostInstanceToken='{detachedHostInstanceToken}', DetachedHostManagedAssemblyGeneration='{detachedHostManagedAssemblyGeneration}'"
			);
		}
		catch (Exception exception)
		{
			DebugLogger.LogOperation(
				"C# autocomplete host shutdown cleanup failed",
				$"HostInstanceToken='{detachedHostInstanceToken}', HostManagedAssemblyGeneration='{detachedHostManagedAssemblyGeneration}', Exception='{exception}'"
			);
		}
		finally
		{
			_autocompleteHostShutdownInProgress = false;
			DebugLogger.LogOperation(
				"C# autocomplete ShutdownAutocomplete completed",
				() =>
					$"DetachedHostPresent='True', DetachedHostInstanceToken='{detachedHostInstanceToken}', DetachedHostManagedAssemblyGeneration='{detachedHostManagedAssemblyGeneration}', CurrentHostNull='{_autocompleteHost == null}', CurrentHostManagedAssemblyGeneration='{_autocompleteHostManagedAssemblyGeneration}', CurrentHostInstanceToken='{_autocompleteHostInstanceToken}', ShutdownInProgress='{_autocompleteHostShutdownInProgress}'"
			);
		}
	}

	private void OnAutocompleteScriptChanged(Script script)
	{
		EnterAutocompleteScriptEditorChangedCallbackScope();
		try
		{
			if (IsAutocompleteDeferredUsingInsertionBarrierActive)
				CancelDeferredAutocompleteUsingInsertion("ScriptChanged");

			if (!IsAutocompletePluginBoundaryAvailable())
			{
				LogAutocompleteCallbackBoundaryRejection("ScriptChanged");
				return;
			}

			if (IsAutocompleteExternalMutationActive)
			{
				_pendingExternalMutationAutocompleteScriptChange = true;
				_suppressedExternalMutationAutocompleteScriptChangedCount++;
				RefreshEditorPluginProcessingState();
				return;
			}

			if (!EnsureManagedAssemblyStateCurrent("C# Autocomplete Script Changed"))
				return;

			bool traceScriptChanged =
				_autocompleteHost == null
				|| _isRecoveringManagedAssemblyState
				|| IsTreeKeyboardNavigationBurstActive;
			if (traceScriptChanged)
			{
				DebugLogger.LogOperation(
					"C# autocomplete script changed",
					() =>
						$"{DescribeAutocompleteScriptForDiagnostics(script)}, HostNull='{_autocompleteHost == null}', HostInstanceToken='{_autocompleteHostInstanceToken}', ManagedRecoveryInProgress='{_isRecoveringManagedAssemblyState}', TreeKeyboardNavigationBurstActive='{IsTreeKeyboardNavigationBurstActive}'"
				);
			}

			_autocompleteHost?.InvalidatePendingValidations();

			if (IsTreeKeyboardNavigationBurstActive)
			{
				_autocompleteScriptChangePendingAfterTreeKeyboardNavigation = true;
				RefreshEditorPluginProcessingState();
				return;
			}

			QueueDeferredAutocompleteScriptChangeRebind("AutocompleteScriptChanged");
			RefreshEditorPluginProcessingState();
		}
		finally
		{
			ExitAutocompleteScriptEditorChangedCallbackScope();
		}
	}

	private void ApplyDeferredAutocompleteScriptChangeRebind(
		long token,
		long scheduledHostInstanceToken,
		string scheduledManagedAssemblyGeneration
	)
	{
		if (
			!string.Equals(
				scheduledManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
		)
		{
			LogStaleDeferredAutocompleteOperation(
				"DeferredScriptEditorRebind",
				scheduledManagedAssemblyGeneration,
				token,
				_autocompleteDeferredScriptChangeRebindToken,
				scheduledHostInstanceToken,
				_autocompleteHostInstanceToken
			);
			return;
		}

		if (
			token != _autocompleteDeferredScriptChangeRebindToken
			|| !_autocompleteDeferredScriptChangeRebindPending
		)
		{
			return;
		}

		if (scheduledHostInstanceToken != _autocompleteHostInstanceToken)
			return;

		long targetTransitionId = _autocompleteDeferredScriptChangeTargetTransitionId;
		if (
			targetTransitionId <= 0
			|| !ScriptEditorLifecycleCoordinator.CanResolveBinding(
				scheduledManagedAssemblyGeneration,
				targetTransitionId
			)
		)
		{
			LogScriptEditorLifecycle(
				"ScriptEditor lifecycle stale binding resolution rejected",
				$"Reason='PreEditorAccessAuthorityCheck', OperationToken='{token}', TargetScriptTransitionId='{targetTransitionId}', HostInstanceToken='{scheduledHostInstanceToken}', {DescribeScriptEditorLifecycleForDiagnostics()}"
			);
			ConsumeDeferredAutocompleteScriptChangeRebind(token);
			RefreshEditorPluginProcessingState();
			return;
		}

		_autocompleteDeferredScriptChangeRebindQueued = false;
		_autocompleteDeferredScriptChangeRebindExecutionActive = true;
		try
		{
			if (!IsAutocompletePluginBoundaryAvailable())
			{
				ConsumeDeferredAutocompleteScriptChangeRebind(token);
				RefreshEditorPluginProcessingState();
				return;
			}

			if (IsAutocompleteExternalMutationActive)
			{
				_pendingExternalMutationAutocompleteScriptChange = true;
				ConsumeDeferredAutocompleteScriptChangeRebind(token);
				RefreshEditorPluginProcessingState();
				return;
			}

			if (IsTreeKeyboardNavigationBurstActive)
			{
				_autocompleteScriptChangePendingAfterTreeKeyboardNavigation = true;
				ConsumeDeferredAutocompleteScriptChangeRebind(token);
				RefreshEditorPluginProcessingState();
				return;
			}

			if (
				!EnsureManagedAssemblyStateCurrent(
					"C# Autocomplete Deferred Script Changed"
				)
			)
			{
				ConsumeDeferredAutocompleteScriptChangeRebind(token);
				RefreshEditorPluginProcessingState();
				return;
			}

			if (
				token != _autocompleteDeferredScriptChangeRebindToken
				|| !_autocompleteDeferredScriptChangeRebindPending
				|| scheduledHostInstanceToken != _autocompleteHostInstanceToken
			)
			{
				RefreshEditorPluginProcessingState();
				return;
			}

			targetTransitionId = _autocompleteDeferredScriptChangeTargetTransitionId;
			if (
				targetTransitionId <= 0
				|| !ScriptEditorLifecycleCoordinator.CanResolveBinding(
					ManagedAssemblyGeneration,
					targetTransitionId
				)
			)
			{
				LogScriptEditorLifecycle(
					"ScriptEditor lifecycle stale binding resolution rejected",
					$"Reason='PostRecoveryAuthorityCheck', OperationToken='{token}', TargetScriptTransitionId='{targetTransitionId}', HostInstanceToken='{scheduledHostInstanceToken}', {DescribeScriptEditorLifecycleForDiagnostics()}"
				);
				ConsumeDeferredAutocompleteScriptChangeRebind(token);
				RefreshEditorPluginProcessingState();
				return;
			}

			if (IsAutocompleteExternalMutationActive)
			{
				_pendingExternalMutationAutocompleteScriptChange = true;
				ConsumeDeferredAutocompleteScriptChangeRebind(token);
				RefreshEditorPluginProcessingState();
				return;
			}

			if (IsTreeKeyboardNavigationBurstActive)
			{
				_autocompleteScriptChangePendingAfterTreeKeyboardNavigation = true;
				ConsumeDeferredAutocompleteScriptChangeRebind(token);
				RefreshEditorPluginProcessingState();
				return;
			}

			if (!TryEnsureAutocompleteHost(out AutocompletePluginHost host))
			{
				DebugLogger.LogOperation(
					"C# autocomplete deferred ScriptEditor rebind aborted",
					$"Reason='Host unavailable', Token='{token}', ScriptTransitionId='{targetTransitionId}'"
				);
				ConsumeDeferredAutocompleteScriptChangeRebind(token);
				RefreshEditorPluginProcessingState();
				return;
			}

			if (
				scheduledHostInstanceToken != _autocompleteHostInstanceToken
				|| !ScriptEditorLifecycleCoordinator.CanResolveBinding(
					ManagedAssemblyGeneration,
					targetTransitionId
				)
			)
			{
				LogScriptEditorLifecycle(
					"ScriptEditor lifecycle stale binding resolution rejected",
					$"Reason='HostOrTransitionChangedBeforeScriptEditorLifecycleEnsure', OperationToken='{token}', TargetScriptTransitionId='{targetTransitionId}', ScheduledHostInstanceToken='{scheduledHostInstanceToken}', CurrentHostInstanceToken='{_autocompleteHostInstanceToken}', {DescribeScriptEditorLifecycleForDiagnostics()}"
				);
				ConsumeDeferredAutocompleteScriptChangeRebind(token);
				RefreshEditorPluginProcessingState();
				return;
			}

			if (!host.EnsureLifecycleCurrent())
			{
				DebugLogger.LogOperation(
					"C# autocomplete deferred ScriptEditor rebind aborted",
					$"Reason='ScriptEditor lifecycle unavailable', Token='{token}', HostInstanceToken='{_autocompleteHostInstanceToken}', ScriptTransitionId='{targetTransitionId}'"
				);
				ConsumeDeferredAutocompleteScriptChangeRebind(token);
				RefreshEditorPluginProcessingState();
				return;
			}

			if (
				token != _autocompleteDeferredScriptChangeRebindToken
				|| !_autocompleteDeferredScriptChangeRebindPending
				|| targetTransitionId != _autocompleteDeferredScriptChangeTargetTransitionId
				|| !ScriptEditorLifecycleCoordinator.CanResolveBinding(
					ManagedAssemblyGeneration,
					targetTransitionId
				)
			)
			{
				LogScriptEditorLifecycle(
					"ScriptEditor lifecycle stale binding resolution rejected",
					$"Reason='AuthorityChangedBeforeBindingResolution', OperationToken='{token}', TargetScriptTransitionId='{targetTransitionId}', {DescribeScriptEditorLifecycleForDiagnostics()}"
				);
				RefreshEditorPluginProcessingState();
				return;
			}

			if (
				!TryGetAutocompleteReloadRebindAdmission(
					scheduledHostInstanceToken,
					targetTransitionId,
					out long reloadReadyEpoch,
					out AutocompleteEditorBindingCandidate? requiredActivationCandidate
				)
			)
			{
				ArmAutocompleteReloadStabilizationObservation();
				ConsumeDeferredAutocompleteScriptChangeRebind(token);
				RefreshEditorPluginProcessingState();
				return;
			}

			AutocompleteBindingActivationStabilizationKind stabilizationKind =
				AutocompleteBindingActivationStabilizationKind.None;
			if (requiredActivationCandidate.HasValue)
			{
				stabilizationKind = AutocompleteBindingActivationStabilizationKind.Reload;
			}
			else
			{
				ScriptEditorLifecycleSnapshot lifecycle =
					ScriptEditorLifecycleCoordinator.Snapshot;
				if (
					ShouldRequireAutocompleteScriptTransitionStabilization(
						reloadReadyEpoch,
						lifecycle
					)
				)
				{
					if (
						!TryGetAutocompleteScriptTransitionRebindAdmission(
							scheduledHostInstanceToken,
							targetTransitionId,
							ShouldRequireSystemExplorerNavigationBindingQuiescence(
								reloadReadyEpoch,
								lifecycle
							),
							out AutocompleteEditorBindingCandidate ordinaryCandidate
						)
					)
					{
						ConsumeDeferredAutocompleteScriptChangeRebind(token);
						RefreshEditorPluginProcessingState();
						return;
					}

					requiredActivationCandidate = ordinaryCandidate;
					stabilizationKind =
						AutocompleteBindingActivationStabilizationKind.ScriptTransition;
				}
			}

			try
			{
				bool bindingResolved = host.HandleScriptChanged(
					targetTransitionId,
					reloadReadyEpoch,
					requiredActivationCandidate
				);

				switch (stabilizationKind)
				{
					case AutocompleteBindingActivationStabilizationKind.Reload:
						if (bindingResolved)
						{
							bindingResolved = TryCompleteAutocompleteReloadActivation(
								reloadReadyEpoch,
								requiredActivationCandidate.Value
							);
						}
						else
						{
							RestartAutocompleteReloadActivation(
								"BindingResolutionRejected"
							);
						}
						break;

					case AutocompleteBindingActivationStabilizationKind.ScriptTransition:
						if (bindingResolved)
						{
							bindingResolved =
								TryCompleteAutocompleteScriptTransitionActivation(
									reloadReadyEpoch,
									requiredActivationCandidate.Value
								);
						}
						else
						{
							RestartAutocompleteScriptTransitionActivation(
								"BindingResolutionRejected"
							);
						}
						break;
				}
			}
			catch (Exception exception)
			{
				switch (stabilizationKind)
				{
					case AutocompleteBindingActivationStabilizationKind.Reload:
						RestartAutocompleteReloadActivation(
							"BindingResolutionException"
						);
						break;
					case AutocompleteBindingActivationStabilizationKind.ScriptTransition:
						RestartAutocompleteScriptTransitionActivation(
							"BindingResolutionException"
						);
						break;
				}

				DebugLogger.LogOperation(
					"C# autocomplete deferred ScriptEditor rebind failed",
					exception.ToString()
				);
			}
			finally
			{
				ConsumeDeferredAutocompleteScriptChangeRebind(token);
				RefreshEditorPluginProcessingState();
			}
		}
		finally
		{
			_autocompleteDeferredScriptChangeRebindExecutionActive = false;
		}
	}

	private void FlushPendingAutocompleteScriptChangeAfterTreeKeyboardNavigation()
	{
		if (!_autocompleteScriptChangePendingAfterTreeKeyboardNavigation)
			return;

		_autocompleteScriptChangePendingAfterTreeKeyboardNavigation = false;

		if (!IsAutocompletePluginBoundaryAvailable())
		{
			RefreshEditorPluginProcessingState();
			return;
		}

		if (IsAutocompleteExternalMutationActive)
		{
			_pendingExternalMutationAutocompleteScriptChange = true;
			RefreshEditorPluginProcessingState();
			return;
		}

		if (
			!EnsureManagedAssemblyStateCurrent(
				"C# Autocomplete Tree Keyboard Navigation Finalize"
			)
		)
		{
			RefreshEditorPluginProcessingState();
			return;
		}

		QueueDeferredAutocompleteScriptChangeRebind(
			"TreeKeyboardNavigationFinalize"
		);

		RefreshEditorPluginProcessingState();
	}

	private void OnAutocompleteCodeCompletionRequested()
	{
		AutocompletePluginHost host = null;

		try
		{
			if (!IsAutocompletePluginBoundaryAvailable())
			{
				LogAutocompleteCallbackBoundaryRejection("CodeCompletionRequested");
				return;
			}

			if (IsAutocompleteExternalMutationActive)
			{
				_suppressedExternalMutationAutocompleteCompletionRequestedCount++;
				RefreshEditorPluginProcessingState();
				return;
			}

			if (IsAutocompleteDeferredUsingInsertionBarrierActive)
			{
				IncrementAutocompleteDeferredUsingSuppression(
					ref _suppressedAutocompleteDeferredUsingCompletionRequestedCount
				);
				return;
			}

			if (IsAutocompleteScriptChangeRebindBarrierActive)
				return;

			if (!EnsureManagedAssemblyStateCurrent("C# Autocomplete Completion Requested"))
				return;

			if (TryEnsureAutocompleteHost(out host))
			{
				host.HandleCompletionRequested();
				if (host.IsCompletionPipelineFaulted)
				{
					CancelDeferredAutocompleteUsingInsertion(
						"CompletionPipelineFaulted"
					);
				}
			}

			RefreshEditorPluginProcessingState();
		}
		catch (Exception exception)
		{
			try
			{
				AutocompletePluginHost faultHost = host;
				if (
					faultHost == null
					&& _autocompleteHost != null
					&& string.Equals(
						_autocompleteHostManagedAssemblyGeneration,
						ManagedAssemblyGeneration,
						StringComparison.Ordinal
					)
				)
				{
					faultHost = _autocompleteHost;
				}

				faultHost?.MarkCompletionPipelineFaultedFromCallbackBoundary(exception);
			}
			catch
			{
			}

			try
			{
				CancelDeferredAutocompleteUsingInsertion("CompletionPipelineFaulted");
			}
			catch
			{
			}

			try
			{
				DebugLogger.LogOperation(
					"C# autocomplete completion signal callback failed",
					$"HostInstanceToken='{_autocompleteHostInstanceToken}', ManagedAssemblyGeneration='{ManagedAssemblyGeneration}', Stage='PluginSignalBoundary', ExceptionType='{exception.GetType().FullName}', Exception='{exception}'"
				);
			}
			catch
			{
			}

			try
			{
				RefreshEditorPluginProcessingState();
			}
			catch
			{
			}
		}
	}

	private void OnAutocompleteCodeEditGuiInput(InputEvent inputEvent)
	{
		EnterAutocompleteCodeEditGuiInputCallbackScope();
		try
		{
			if (!IsAutocompletePluginBoundaryAvailable())
				return;

			if (IsAutocompleteExternalMutationActive)
			{
				_suppressedExternalMutationAutocompleteGuiInputCount++;
				return;
			}

			if (IsAutocompleteDeferredUsingInsertionBarrierActive)
			{
				IncrementAutocompleteDeferredUsingSuppression(
					ref _suppressedAutocompleteDeferredUsingGuiInputCount
				);
				return;
			}

			if (IsAutocompleteScriptChangeRebindBarrierActive)
				return;

			if (!EnsureManagedAssemblyStateCurrent("C# Autocomplete CodeEdit Input"))
				return;

			if (!TryEnsureAutocompleteHost(out AutocompletePluginHost host))
				return;

			AutocompleteDeferredUsingInsertionRequest deferredUsingInsertionRequest =
				host.HandleCodeEditGuiInput(inputEvent);
			if (deferredUsingInsertionRequest != null)
			{
				QueueDeferredAutocompleteUsingInsertion(
					host,
					deferredUsingInsertionRequest
				);
			}
		}
		finally
		{
			ExitAutocompleteCodeEditGuiInputCallbackScope();
		}
	}

	private void OnAutocompleteProjectFilesystemChanged()
	{
		if (!IsAutocompletePluginBoundaryAvailable())
		{
			LogAutocompleteCallbackBoundaryRejection("ProjectFilesystemChanged");
			return;
		}

		if (IsAutocompleteExternalMutationActive)
		{
			_pendingExternalMutationAutocompleteFilesystemChange = true;
			_suppressedExternalMutationAutocompleteFilesystemChangedCount++;
			return;
		}

		if (!EnsureManagedAssemblyStateCurrent("C# Autocomplete Filesystem Changed"))
			return;

		AutocompleteFilesystemDiagnosticBoundaryContext diagnosticBoundary =
			BeginAutocompleteFilesystemChangedDiagnosticBoundary();
		try
		{
			if (TryEnsureAutocompleteHost(out AutocompletePluginHost host))
			{
				host.HandleProjectFilesystemChanged(
					CreateAutocompleteFilesystemChangedDiagnosticPhase(diagnosticBoundary)
				);
			}
		}
		finally
		{
			CompleteAutocompleteFilesystemChangedDiagnosticBoundary(diagnosticBoundary);
			RefreshEditorPluginProcessingState();
		}
	}

	private void OnAutocompleteTextChanged()
	{
		if (!IsAutocompletePluginBoundaryAvailable())
			return;

		if (IsAutocompleteExternalMutationActive)
		{
			_pendingExternalMutationAutocompleteTextChange = true;
			_suppressedExternalMutationAutocompleteTextChangedCount++;
			return;
		}

		if (IsAutocompleteDeferredUsingInsertionBarrierActive)
		{
			IncrementAutocompleteDeferredUsingSuppression(
				ref _suppressedAutocompleteDeferredUsingTextChangedCount
			);
			return;
		}

		if (IsAutocompleteScriptChangeRebindBarrierActive)
			return;

		if (
			!HasVerifiedPersistentTreeStateForCurrentAssembly
			|| _isRecoveringManagedAssemblyState
		)
		{
			QueueAutocompleteTextChangedRecovery();
			return;
		}

		AutocompletePluginHost host = _autocompleteHost;

		if (host != null)
		{
			long hostInstanceToken = _autocompleteHostInstanceToken;
			long validationGeneration = host.BeginTextChangedValidation();
			RefreshEditorPluginProcessingState();
			if (host.IsCompletionPipelineFaulted)
				return;

			CallDeferred(
				nameof(ValidateAutocompleteAfterTextChangedDeferred),
				validationGeneration,
				hostInstanceToken,
				ManagedAssemblyGeneration
			);
			return;
		}

		QueueAutocompleteTextChangedRecovery();
	}

	private long AdvanceAutocompleteTextChangedRecoveryToken()
	{
		unchecked
		{
			_autocompleteTextChangedRecoveryToken++;
			if (_autocompleteTextChangedRecoveryToken <= 0)
				_autocompleteTextChangedRecoveryToken = 1;
		}

		return _autocompleteTextChangedRecoveryToken;
	}

	private void ResetAutocompleteTextChangedRecoveryState(bool invalidateToken)
	{
		_autocompleteTextChangedRecoveryQueued = false;
		if (invalidateToken)
			AdvanceAutocompleteTextChangedRecoveryToken();
	}

	private void QueueAutocompleteTextChangedRecovery()
	{
		if (!IsAutocompletePluginBoundaryAvailable())
			return;

		if (_autocompleteTextChangedRecoveryQueued)
			return;

		long token = AdvanceAutocompleteTextChangedRecoveryToken();
		long scheduledHostInstanceToken = _autocompleteHostInstanceToken;
		string scheduledManagedAssemblyGeneration = ManagedAssemblyGeneration;
		_autocompleteTextChangedRecoveryQueued = true;
		CallDeferred(
			nameof(RecoverAutocompleteAfterTextChangedDeferred),
			token,
			scheduledHostInstanceToken,
			scheduledManagedAssemblyGeneration
		);
	}

	private void RecoverAutocompleteAfterTextChangedDeferred(
		long token,
		long scheduledHostInstanceToken,
		string scheduledManagedAssemblyGeneration
	)
	{
		if (
			!string.Equals(
				scheduledManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
		)
		{
			LogStaleDeferredAutocompleteOperation(
				"TextChangedRecovery",
				scheduledManagedAssemblyGeneration,
				token,
				_autocompleteTextChangedRecoveryToken,
				scheduledHostInstanceToken,
				_autocompleteHostInstanceToken
			);
			return;
		}

		if (token != _autocompleteTextChangedRecoveryToken)
			return;

		if (scheduledHostInstanceToken != _autocompleteHostInstanceToken)
			return;

		_autocompleteTextChangedRecoveryQueued = false;

		if (!IsAutocompletePluginBoundaryAvailable())
			return;

		if (IsAutocompleteDeferredUsingInsertionBarrierActive)
			return;

		EnsureManagedAssemblyStateCurrent(
			"C# Autocomplete Text Changed Recovery"
		);
	}

	private void ValidateAutocompleteAfterTextChangedDeferred(
		long validationGeneration,
		long scheduledHostInstanceToken,
		string scheduledManagedAssemblyGeneration
	)
	{
		if (
			!string.Equals(
				scheduledManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
		)
		{
			LogStaleDeferredAutocompleteOperation(
				"TextChangedValidation",
				scheduledManagedAssemblyGeneration,
				validationGeneration,
				validationGeneration,
				scheduledHostInstanceToken,
				_autocompleteHostInstanceToken
			);
			return;
		}

		if (scheduledHostInstanceToken != _autocompleteHostInstanceToken)
			return;

		if (!IsAutocompletePluginBoundaryAvailable())
			return;

		if (IsAutocompleteExternalMutationActive)
		{
			_pendingExternalMutationAutocompleteTextChange = true;
			return;
		}

		if (IsAutocompleteDeferredUsingInsertionBarrierActive)
		{
			IncrementAutocompleteDeferredUsingSuppression(
				ref _suppressedAutocompleteDeferredUsingValidationCount
			);
			return;
		}

		if (IsAutocompleteScriptChangeRebindBarrierActive)
			return;

		if (
			!HasVerifiedPersistentTreeStateForCurrentAssembly
			|| _isRecoveringManagedAssemblyState
		)
		{
			if (!EnsureManagedAssemblyStateCurrent("C# Autocomplete Text Validation"))
				return;

			if (!IsAutocompletePluginBoundaryAvailable())
				return;
		}

		AutocompletePluginHost scheduledHost = _autocompleteHost;

		if (
			scheduledHost == null
			|| scheduledHostInstanceToken != _autocompleteHostInstanceToken
			|| !scheduledHost.IsValidationCurrent(validationGeneration)
		)
		{
			LogAutocompleteDeferredValidationRejection(
				"BeforeManagedStateEnsure",
				scheduledHostInstanceToken,
				validationGeneration,
				scheduledHost,
				sameHostReference: true
			);
			return;
		}

		if (!EnsureManagedAssemblyStateCurrent("C# Autocomplete Text Validation"))
			return;

		if (!IsAutocompletePluginBoundaryAvailable())
			return;

		AutocompletePluginHost currentHost = _autocompleteHost;

		if (
			currentHost == null
			|| scheduledHostInstanceToken != _autocompleteHostInstanceToken
			|| !ReferenceEquals(scheduledHost, currentHost)
			|| !currentHost.IsValidationCurrent(validationGeneration)
		)
		{
			LogAutocompleteDeferredValidationRejection(
				"AfterManagedStateEnsure",
				scheduledHostInstanceToken,
				validationGeneration,
				currentHost,
				ReferenceEquals(scheduledHost, currentHost)
			);
			return;
		}

		currentHost.ValidateAfterTextChanged(validationGeneration);
		RefreshEditorPluginProcessingState();
	}

	private void LogStaleDeferredAutocompleteOperation(
		string operation,
		string scheduledManagedAssemblyGeneration,
		long scheduledOperationToken,
		long currentOperationToken,
		long scheduledHostInstanceToken,
		long currentHostInstanceToken
	)
	{
		DebugLogger.LogPersistentFileOnlyOperation(
			"C# autocomplete deferred operation rejected",
			$"Reason='StaleManagedAssemblyGeneration', Operation='{operation ?? ""}', ScheduledManagedAssemblyGeneration='{scheduledManagedAssemblyGeneration ?? ""}', CurrentManagedAssemblyGeneration='{ManagedAssemblyGeneration}', ScheduledOperationToken='{scheduledOperationToken}', CurrentOperationToken='{currentOperationToken}', ScheduledHostInstanceToken='{scheduledHostInstanceToken}', CurrentHostInstanceToken='{currentHostInstanceToken}'"
		);
	}

	private void LogAutocompleteDeferredValidationRejection(
		string stage,
		long scheduledHostInstanceToken,
		long validationGeneration,
		AutocompletePluginHost observedHost,
		bool sameHostReference
	)
	{
		DebugLogger.LogOperation(
			"C# autocomplete deferred validation rejected",
			() =>
				$"Stage='{stage}', ScheduledHostInstanceToken='{scheduledHostInstanceToken}', CurrentHostInstanceToken='{_autocompleteHostInstanceToken}', ValidationGeneration='{validationGeneration}', HostNull='{observedHost == null}', SameHostReference='{sameHostReference}', ManagedRecoveryInProgress='{_isRecoveringManagedAssemblyState}'"
		);
	}

	private void LogAutocompleteCallbackBoundaryRejection(string callbackName)
	{
		DebugLogger.LogOperation(
			"C# autocomplete callback rejected",
			() =>
				$"Callback='{callbackName}', HostNull='{_autocompleteHost == null}', HostInstanceToken='{_autocompleteHostInstanceToken}', ShutdownInProgress='{_autocompleteHostShutdownInProgress}', ManagedRecoveryInProgress='{_isRecoveringManagedAssemblyState}'"
		);
	}

	private static string DescribeAutocompleteScriptForDiagnostics(Script script)
	{
		try
		{
			if (script == null || !GodotObject.IsInstanceValid(script))
				return "Script=<null-or-invalid>, ScriptValid='False'";

			return
				$"ScriptInstanceId='{script.GetInstanceId()}', ScriptValid='True', ScriptPath='{script.ResourcePath}'";
		}
		catch (Exception exception)
		{
			return $"ScriptDiagnosticReadFailed='{exception.GetType().Name}: {exception.Message}'";
		}
	}

	private bool HasPendingAutocompleteIndexingQuiescenceProcessWork()
	{
		if (
			!IsAutocompletePluginBoundaryAvailable()
			|| _autocompleteHost == null
			|| !IsAutocompleteHostManagedAssemblyGenerationCurrent()
		)
		{
			return false;
		}

		try
		{
			return _autocompleteHost.HasPendingIndexingQuiescenceWork;
		}
		catch
		{
			return false;
		}
	}

	private bool IsAutocompleteIndexingQuiescenceAdmissionAllowed()
	{
		return IsAutocompletePluginBoundaryAvailable()
			&& HasVerifiedPersistentTreeStateForCurrentAssembly
			&& !_isRecoveringManagedAssemblyState
			&& !IsAutocompleteExternalMutationActive
			&& !IsAutocompleteScriptChangeRebindBarrierActive
			&& !IsAutocompleteDeferredUsingInsertionBarrierActive
			&& IsAutocompleteReloadStabilizationReady()
			&& _autocompleteHost != null
			&& IsAutocompleteHostManagedAssemblyGenerationCurrent();
	}

	private void ProcessPendingAutocompleteIndexingQuiescence(double delta)
	{
		AutocompletePluginHost host = _autocompleteHost;
		if (
			host == null
			|| !IsAutocompleteHostManagedAssemblyGenerationCurrent()
			|| !host.HasPendingIndexingQuiescenceWork
		)
		{
			return;
		}

		bool admissionAllowed = IsAutocompleteIndexingQuiescenceAdmissionAllowed();
		host.ProcessPendingIndexingQuiescence(delta, admissionAllowed);
	}

	private void ClearPendingAutocompleteIndexingQuiescenceProcessWork()
	{
		try
		{
			if (
				_autocompleteHost != null
				&& IsAutocompleteHostManagedAssemblyGenerationCurrent()
			)
			{
				_autocompleteHost.ClearPendingIndexingQuiescenceWork();
			}
		}
		catch
		{
		}
	}

	private void CapturePendingAutocompleteProcessFollowUpDuringExternalMutation()
	{
		if (!IsAutocompleteExternalMutationActive)
			return;

		try
		{
			if (
				_autocompleteHost != null
				&& IsAutocompleteHostManagedAssemblyGenerationCurrent()
				&& _autocompleteHost.HasPendingCompletionProcessWork()
			)
			{
				_pendingExternalMutationAutocompleteProcessFollowUp = true;
			}
		}
		catch
		{
			// Pure-managed follow-up bookkeeping is best effort while the external lease owns the editor.
		}
	}

	private bool HasPendingAutocompleteProcessWork()
	{
		if (IsAutocompleteExternalMutationActive)
		{
			CapturePendingAutocompleteProcessFollowUpDuringExternalMutation();
			return false;
		}

		if (
			IsAutocompleteScriptChangeRebindBarrierActive
			|| IsAutocompleteDeferredUsingInsertionBarrierActive
		)
		{
			return false;
		}

		if (
			!IsAutocompletePluginBoundaryAvailable()
			|| !HasVerifiedPersistentTreeStateForCurrentAssembly
			|| _isRecoveringManagedAssemblyState
		)
		{
			return false;
		}

		try
		{
			return _autocompleteHost?.HasPendingCompletionProcessWork() == true;
		}
		catch
		{
			return false;
		}
	}

	private void ProcessPendingAutocompleteProcessWork()
	{
		if (IsAutocompleteExternalMutationActive)
		{
			CapturePendingAutocompleteProcessFollowUpDuringExternalMutation();
			return;
		}

		if (
			IsAutocompleteScriptChangeRebindBarrierActive
			|| IsAutocompleteDeferredUsingInsertionBarrierActive
		)
		{
			return;
		}

		if (!HasPendingAutocompleteProcessWork())
			return;

		if (!IsAutocompletePluginBoundaryAvailable())
			return;

		if (!EnsureManagedAssemblyStateCurrent("C# Autocomplete Process Follow-up"))
		{
			ClearPendingAutocompleteProcessWork();
			return;
		}

		if (IsAutocompleteExternalMutationActive)
		{
			CapturePendingAutocompleteProcessFollowUpDuringExternalMutation();
			return;
		}

		if (
			IsAutocompleteScriptChangeRebindBarrierActive
			|| IsAutocompleteDeferredUsingInsertionBarrierActive
		)
		{
			return;
		}

		if (!TryEnsureAutocompleteHost(out AutocompletePluginHost host))
		{
			ClearPendingAutocompleteProcessWork();
			return;
		}

		host.ProcessPendingCompletionWork();
	}

	private void ClearPendingAutocompleteProcessWork()
	{
		if (
			!IsAutocompletePluginBoundaryAvailable()
			|| !HasVerifiedPersistentTreeStateForCurrentAssembly
			|| _isRecoveringManagedAssemblyState
		)
		{
			return;
		}

		try
		{
			_autocompleteHost?.ClearPendingCompletionProcessWork();
		}
		catch
		{
		}
	}
	#endregion
}
#endif
