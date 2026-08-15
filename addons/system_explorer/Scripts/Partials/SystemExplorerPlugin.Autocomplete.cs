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
	private bool _namespaceRefactorAutocompleteQuiescenceActive;
	private long _namespaceRefactorAutocompleteQuiescenceToken;
	private string _namespaceRefactorAutocompleteQuiescenceOperationName = "";
	private bool _pendingNamespaceRefactorAutocompleteScriptChange;
	private bool _pendingNamespaceRefactorAutocompleteTextChange;
	private bool _pendingNamespaceRefactorAutocompleteFilesystemChange;
	private bool _pendingNamespaceRefactorAutocompleteProcessFollowUp;
	private int _suppressedNamespaceRefactorAutocompleteScriptChangedCount;
	private int _suppressedNamespaceRefactorAutocompleteTextChangedCount;
	private int _suppressedNamespaceRefactorAutocompleteFilesystemChangedCount;
	private int _suppressedNamespaceRefactorAutocompleteCompletionRequestedCount;
	private int _suppressedNamespaceRefactorAutocompleteGuiInputCount;

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
			ScriptEditorLifecycleCoordinator,
			RequestScriptEditorLifecycleRebind,
			semanticMemberPipelineEnabled: false,
			cancelNativeCompletionOnRebind: false,
			activeDocumentSyntaxOverlayEnabled: false,
			cancelNativeCompletionOnTextChangedValidation: false,
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

	private void QueueDeferredAutocompleteScriptChangeRebind(string origin)
	{
		if (_namespaceRefactorAutocompleteQuiescenceActive)
		{
			_pendingNamespaceRefactorAutocompleteScriptChange = true;
			return;
		}

		if (IsTreeKeyboardNavigationBurstActive)
		{
			_autocompleteScriptChangePendingAfterTreeKeyboardNavigation = true;
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

		DebugLogger.LogOperation(
			"C# autocomplete ScriptEditor rebind deferred",
			() =>
				$"Token='{token}', TargetScriptTransitionId='{_autocompleteDeferredScriptChangeTargetTransitionId}', HostInstanceToken='{scheduledHostInstanceToken}', ManagedAssemblyGeneration='{scheduledManagedAssemblyGeneration}', ScriptEditorChangedCallbackDepth='{_autocompleteScriptEditorChangedCallbackDepth}', TreeKeyboardNavigationBurstActive='{IsTreeKeyboardNavigationBurstActive}', NamespaceRefactorQuiescenceActive='{_namespaceRefactorAutocompleteQuiescenceActive}'"
		);

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
				$"Token='{token}', Name='{request.CompletionName}', Namespace='{request.NamespaceName}', CodeEditNativeInstanceId='{request.CodeEditNativeInstanceId}', ScriptPath='{request.ScriptPath}', HostInstanceToken='{hostInstanceToken}', ManagedAssemblyGeneration='{managedAssemblyGeneration}', GuiInputCallbackDepth='{_autocompleteCodeEditGuiInputCallbackDepth}', ExceptionType='{exception.GetType().FullName}', Exception='{exception}'"
			);
			ResetDeferredAutocompleteUsingInsertionState(invalidateToken: true);
			RefreshEditorPluginProcessingState();
			return;
		}

		DebugLogger.LogOperation(
			"C# autocomplete automatic using deferred InsertText scheduled",
			$"Token='{token}', Name='{request.CompletionName}', Namespace='{request.NamespaceName}', CodeEditNativeInstanceId='{request.CodeEditNativeInstanceId}', ScriptPath='{request.ScriptPath}', HostInstanceToken='{hostInstanceToken}', ManagedAssemblyGeneration='{managedAssemblyGeneration}', GuiInputCallbackDepth='{_autocompleteCodeEditGuiInputCallbackDepth}'"
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

			if (_namespaceRefactorAutocompleteQuiescenceActive)
			{
				RejectDeferredAutocompleteUsingInsertion(
					"NamespaceRefactorQuiescence",
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
				applyResult?.CurrentScriptPath ?? ""
			);
		}
		catch (Exception exception)
		{
			DebugLogger.LogOperation(
				"C# autocomplete automatic using deferred InsertText failed after confirmation",
				$"UsingAction='{AutocompleteProjectTypeConfirmationService.UsingActionFailedAfterConfirmationDeferred}', Token='{token}', Name='{request?.CompletionName ?? ""}', Namespace='{request?.NamespaceName ?? ""}', CodeEditNativeInstanceId='{request?.CodeEditNativeInstanceId ?? 0UL}', ScriptPath='{request?.ScriptPath ?? ""}', HostInstanceToken='{scheduledHostInstanceToken}', ManagedAssemblyGeneration='{scheduledManagedAssemblyGeneration}', GuiInputCallbackDepth='{_autocompleteCodeEditGuiInputCallbackDepth}', ExceptionType='{exception.GetType().FullName}', Exception='{exception}'"
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
		string currentScriptPath = ""
	)
	{
		LogAutocompleteDeferredUsingInsertionRejection(
			reason,
			token,
			request,
			scheduledHostInstanceToken,
			scheduledManagedAssemblyGeneration,
			currentCodeEditNativeInstanceId,
			currentScriptPath
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
		string currentScriptPath
	)
	{
		string currentCodeEditIdentity = currentCodeEditNativeInstanceId == 0
			? "<not-resolved>"
			: currentCodeEditNativeInstanceId.ToString();

		DebugLogger.LogOperation(
			"C# autocomplete automatic using deferred InsertText rejected",
			$"Reason='{reason ?? ""}', Token='{token}', Name='{request?.CompletionName ?? ""}', Namespace='{request?.NamespaceName ?? ""}', ExpectedCodeEditNativeInstanceId='{request?.CodeEditNativeInstanceId ?? 0UL}', CurrentCodeEditNativeInstanceId='{currentCodeEditIdentity}', ScriptPath='{request?.ScriptPath ?? ""}', CurrentScriptPath='{currentScriptPath ?? ""}', ScheduledHostInstanceToken='{scheduledHostInstanceToken}', CurrentHostInstanceToken='{_autocompleteHostInstanceToken}', ScheduledManagedAssemblyGeneration='{scheduledManagedAssemblyGeneration ?? ""}', CurrentManagedAssemblyGeneration='{ManagedAssemblyGeneration}', GuiInputCallbackDepth='{_autocompleteCodeEditGuiInputCallbackDepth}', SuppressedTextChanged='{_suppressedAutocompleteDeferredUsingTextChangedCount}', SuppressedValidation='{_suppressedAutocompleteDeferredUsingValidationCount}', SuppressedCompletionRequested='{_suppressedAutocompleteDeferredUsingCompletionRequestedCount}', SuppressedGuiInput='{_suppressedAutocompleteDeferredUsingGuiInputCount}'"
		);
	}

	private long AdvanceNamespaceRefactorAutocompleteQuiescenceToken()
	{
		unchecked
		{
			_namespaceRefactorAutocompleteQuiescenceToken++;
			if (_namespaceRefactorAutocompleteQuiescenceToken <= 0)
				_namespaceRefactorAutocompleteQuiescenceToken = 1;
		}

		return _namespaceRefactorAutocompleteQuiescenceToken;
	}

	private long BeginNamespaceRefactorAutocompleteQuiescence(string operationName)
	{
		long token = AdvanceNamespaceRefactorAutocompleteQuiescenceToken();
		_namespaceRefactorAutocompleteQuiescenceActive = true;
		_namespaceRefactorAutocompleteQuiescenceOperationName =
			string.IsNullOrWhiteSpace(operationName)
				? "Refactor Namespace"
				: operationName;
		_pendingNamespaceRefactorAutocompleteScriptChange = false;
		_pendingNamespaceRefactorAutocompleteTextChange = false;
		_pendingNamespaceRefactorAutocompleteFilesystemChange = false;
		_pendingNamespaceRefactorAutocompleteProcessFollowUp = false;
		_suppressedNamespaceRefactorAutocompleteScriptChangedCount = 0;
		_suppressedNamespaceRefactorAutocompleteTextChangedCount = 0;
		_suppressedNamespaceRefactorAutocompleteFilesystemChangedCount = 0;
		_suppressedNamespaceRefactorAutocompleteCompletionRequestedCount = 0;
		_suppressedNamespaceRefactorAutocompleteGuiInputCount = 0;

		try
		{
			if (
				_autocompleteHost != null
				&& IsAutocompleteHostManagedAssemblyGenerationCurrent()
				&& _autocompleteHost.HasPendingCompletionProcessWork()
			)
			{
				_pendingNamespaceRefactorAutocompleteProcessFollowUp = true;
			}
		}
		catch
		{
			// Quiescence admission must never depend on optional managed follow-up state.
		}

		DebugLogger.LogPersistentFileOnlyOperation(
			"C# autocomplete Namespace Refactor quiescence begin",
			$"Token='{token}', Operation='{_namespaceRefactorAutocompleteQuiescenceOperationName}', HostInstanceToken='{_autocompleteHostInstanceToken}', ManagedAssemblyGeneration='{ManagedAssemblyGeneration}'"
		);

		return token;
	}

	private void ResetNamespaceRefactorAutocompleteQuiescenceState(bool invalidateToken)
	{
		_namespaceRefactorAutocompleteQuiescenceActive = false;
		_namespaceRefactorAutocompleteQuiescenceOperationName = "";
		_pendingNamespaceRefactorAutocompleteScriptChange = false;
		_pendingNamespaceRefactorAutocompleteTextChange = false;
		_pendingNamespaceRefactorAutocompleteFilesystemChange = false;
		_pendingNamespaceRefactorAutocompleteProcessFollowUp = false;
		_suppressedNamespaceRefactorAutocompleteScriptChangedCount = 0;
		_suppressedNamespaceRefactorAutocompleteTextChangedCount = 0;
		_suppressedNamespaceRefactorAutocompleteFilesystemChangedCount = 0;
		_suppressedNamespaceRefactorAutocompleteCompletionRequestedCount = 0;
		_suppressedNamespaceRefactorAutocompleteGuiInputCount = 0;

		if (invalidateToken)
			AdvanceNamespaceRefactorAutocompleteQuiescenceToken();
	}

	private void ScheduleNamespaceRefactorAutocompleteQuiescenceRelease(long token)
	{
		if (
			!_namespaceRefactorAutocompleteQuiescenceActive
			|| token != _namespaceRefactorAutocompleteQuiescenceToken
		)
		{
			return;
		}

		if (!IsAutocompletePluginBoundaryAvailable())
		{
			ResetNamespaceRefactorAutocompleteQuiescenceState(invalidateToken: true);
			return;
		}

		long scheduledHostInstanceToken = _autocompleteHostInstanceToken;
		string scheduledManagedAssemblyGeneration = ManagedAssemblyGeneration;

		try
		{
			CallDeferred(
				nameof(CompleteNamespaceRefactorAutocompleteQuiescenceDeferred),
				token,
				scheduledHostInstanceToken,
				scheduledManagedAssemblyGeneration
			);
		}
		catch
		{
			if (
				_namespaceRefactorAutocompleteQuiescenceActive
				&& token == _namespaceRefactorAutocompleteQuiescenceToken
			)
			{
				ResetNamespaceRefactorAutocompleteQuiescenceState(invalidateToken: true);
			}
		}
	}

	private void CompleteNamespaceRefactorAutocompleteQuiescenceDeferred(
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
				"NamespaceRefactorQuiescenceRelease",
				scheduledManagedAssemblyGeneration,
				token,
				_namespaceRefactorAutocompleteQuiescenceToken,
				scheduledHostInstanceToken,
				_autocompleteHostInstanceToken
			);
			return;
		}

		if (
			!_namespaceRefactorAutocompleteQuiescenceActive
			|| token != _namespaceRefactorAutocompleteQuiescenceToken
		)
		{
			if (DebugLogger.IsEnabled)
			{
				DebugLogger.LogPersistentFileOnlyOperation(
					"C# autocomplete Namespace Refactor quiescence stale release ignored",
					$"ScheduledToken='{token}', CurrentToken='{_namespaceRefactorAutocompleteQuiescenceToken}', QuiescenceActive='{_namespaceRefactorAutocompleteQuiescenceActive}'"
				);
			}
			return;
		}

		if (scheduledHostInstanceToken != _autocompleteHostInstanceToken)
		{
			if (DebugLogger.IsEnabled)
			{
				DebugLogger.LogPersistentFileOnlyOperation(
					"C# autocomplete Namespace Refactor quiescence stale release ignored",
					$"Reason='HostChanged', ScheduledToken='{token}', CurrentToken='{_namespaceRefactorAutocompleteQuiescenceToken}', ScheduledHostInstanceToken='{scheduledHostInstanceToken}', CurrentHostInstanceToken='{_autocompleteHostInstanceToken}'"
				);
			}
			return;
		}

		string operationName = _namespaceRefactorAutocompleteQuiescenceOperationName;
		bool pendingScriptChange = _pendingNamespaceRefactorAutocompleteScriptChange;
		bool pendingTextChange = _pendingNamespaceRefactorAutocompleteTextChange;
		bool pendingFilesystemChange = _pendingNamespaceRefactorAutocompleteFilesystemChange;
		bool pendingProcessFollowUp = _pendingNamespaceRefactorAutocompleteProcessFollowUp;
		int suppressedScriptChangedCount = _suppressedNamespaceRefactorAutocompleteScriptChangedCount;
		int suppressedTextChangedCount = _suppressedNamespaceRefactorAutocompleteTextChangedCount;
		int suppressedFilesystemChangedCount = _suppressedNamespaceRefactorAutocompleteFilesystemChangedCount;
		int suppressedCompletionRequestedCount = _suppressedNamespaceRefactorAutocompleteCompletionRequestedCount;
		int suppressedGuiInputCount = _suppressedNamespaceRefactorAutocompleteGuiInputCount;

		_namespaceRefactorAutocompleteQuiescenceActive = false;
		_namespaceRefactorAutocompleteQuiescenceOperationName = "";
		_pendingNamespaceRefactorAutocompleteScriptChange = false;
		_pendingNamespaceRefactorAutocompleteTextChange = false;
		_pendingNamespaceRefactorAutocompleteFilesystemChange = false;
		_pendingNamespaceRefactorAutocompleteProcessFollowUp = false;
		_suppressedNamespaceRefactorAutocompleteScriptChangedCount = 0;
		_suppressedNamespaceRefactorAutocompleteTextChangedCount = 0;
		_suppressedNamespaceRefactorAutocompleteFilesystemChangedCount = 0;
		_suppressedNamespaceRefactorAutocompleteCompletionRequestedCount = 0;
		_suppressedNamespaceRefactorAutocompleteGuiInputCount = 0;

		bool rebindCatchUp = false;
		bool projectRefreshCatchUp = false;
		try
		{
			bool observedAutocompleteWork =
				pendingScriptChange
				|| pendingTextChange
				|| pendingFilesystemChange
				|| pendingProcessFollowUp
				|| suppressedCompletionRequestedCount > 0
				|| suppressedGuiInputCount > 0;
			if (!observedAutocompleteWork)
				return;

			if (!IsAutocompletePluginBoundaryAvailable())
				return;

			if (
				!EnsureManagedAssemblyStateCurrent(
					"C# Autocomplete Namespace Refactor Quiescence Release"
				)
			)
			{
				return;
			}

			if (token != _namespaceRefactorAutocompleteQuiescenceToken)
				return;

			if (!IsAutocompletePluginBoundaryAvailable())
				return;

			if (!TryEnsureAutocompleteHost(out AutocompletePluginHost host))
				return;

			if (token != _namespaceRefactorAutocompleteQuiescenceToken)
				return;

			host.ClearPendingCompletionProcessWork();

			bool needsRebind = pendingScriptChange || pendingTextChange;
			if (needsRebind)
			{
				host.InvalidatePendingValidations();
				RequestScriptEditorLifecycleRebind(
					"NamespaceRefactorQuiescenceRelease"
				);
				rebindCatchUp = true;
			}
			else
			{
				host.InvalidatePendingValidations();
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
				"C# autocomplete Namespace Refactor quiescence release",
				$"Token='{token}', Operation='{operationName}', SuppressedScriptChanged='{suppressedScriptChangedCount}', SuppressedTextChanged='{suppressedTextChangedCount}', SuppressedFilesystemChanged='{suppressedFilesystemChangedCount}', SuppressedCompletionRequested='{suppressedCompletionRequestedCount}', SuppressedGuiInput='{suppressedGuiInputCount}', PendingProcessFollowUp='{pendingProcessFollowUp}', RebindCatchUp='{rebindCatchUp}', ProjectRefreshCatchUp='{projectRefreshCatchUp}'"
			);
		}
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
		DebugLogger.LogOperation(
			"C# autocomplete EnsureAutocompleteLifecycleCurrent begin",
			() =>
				$"HostNull='{_autocompleteHost == null}', HostInstanceToken='{_autocompleteHostInstanceToken}', HostManagedAssemblyGeneration='{_autocompleteHostManagedAssemblyGeneration}', ManagedRecoveryInProgress='{_isRecoveringManagedAssemblyState}', ManagedAssemblyGeneration='{ManagedAssemblyGeneration}', ScriptEditorChangedCallbackDepth='{_autocompleteScriptEditorChangedCallbackDepth}', DeferredScriptChangeRebindPending='{_autocompleteDeferredScriptChangeRebindPending}', {DescribeScriptEditorLifecycleForDiagnostics()}"
		);
		bool lifecycleCurrent =
			TryEnsureAutocompleteHost(out AutocompletePluginHost host)
			&& host.EnsureLifecycleCurrent();

		if (lifecycleCurrent)
			EnsureScriptEditorLifecycleRecoveryQueued("EnsureAutocompleteLifecycleCurrent");

		RefreshEditorPluginProcessingState();
		DebugLogger.LogOperation(
			"C# autocomplete EnsureAutocompleteLifecycleCurrent completed",
			() =>
				$"Result='{lifecycleCurrent}', HostNull='{_autocompleteHost == null}', HostInstanceToken='{_autocompleteHostInstanceToken}', HostManagedAssemblyGeneration='{_autocompleteHostManagedAssemblyGeneration}', DeferredScriptChangeRebindPending='{_autocompleteDeferredScriptChangeRebindPending}', {DescribeScriptEditorLifecycleForDiagnostics()}"
		);
		return lifecycleCurrent;
	}

	private void ResetAutocompleteTransientStateAfterManagedAssemblyReload()
	{
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
		ResetNamespaceRefactorAutocompleteQuiescenceState(invalidateToken: true);

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
		InvalidateScriptEditorLifecycle("ShutdownAutocomplete");
		DebugLogger.LogOperation(
			"C# autocomplete ShutdownAutocomplete begin",
			() =>
				$"HostNull='{_autocompleteHost == null}', HostInstanceToken='{_autocompleteHostInstanceToken}', HostManagedAssemblyGeneration='{_autocompleteHostManagedAssemblyGeneration}', ShutdownInProgress='{_autocompleteHostShutdownInProgress}', ManagedRecoveryInProgress='{_isRecoveringManagedAssemblyState}'"
		);
		ResetAutocompleteTextChangedRecoveryState(invalidateToken: true);
		_autocompleteScriptChangePendingAfterTreeKeyboardNavigation = false;
		CancelDeferredAutocompleteUsingInsertion("PluginUnavailable");
		ResetDeferredAutocompleteScriptChangeRebindState(invalidateToken: true);
		ResetNamespaceRefactorAutocompleteQuiescenceState(invalidateToken: true);

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

			if (!EnsureManagedAssemblyStateCurrent("C# Autocomplete Script Changed"))
				return;

			if (_namespaceRefactorAutocompleteQuiescenceActive)
			{
				_pendingNamespaceRefactorAutocompleteScriptChange = true;
				_suppressedNamespaceRefactorAutocompleteScriptChangedCount++;
				RefreshEditorPluginProcessingState();
				return;
			}

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
		bool crashTailReachedHandleScriptChanged = false;
		string crashTailTargetScriptPath = "";
		try
		{
			if (!IsAutocompletePluginBoundaryAvailable())
			{
				ConsumeDeferredAutocompleteScriptChangeRebind(token);
				RefreshEditorPluginProcessingState();
				return;
			}

			if (_namespaceRefactorAutocompleteQuiescenceActive)
			{
				_pendingNamespaceRefactorAutocompleteScriptChange = true;
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

			if (_namespaceRefactorAutocompleteQuiescenceActive)
			{
				_pendingNamespaceRefactorAutocompleteScriptChange = true;
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

			ScriptEditorLifecycleSnapshot lifecycleSnapshotBeforeEnsure =
				ScriptEditorLifecycleCoordinator.Snapshot;
			crashTailTargetScriptPath = !string.IsNullOrWhiteSpace(
				lifecycleSnapshotBeforeEnsure.ObservedScriptPath
			)
				? lifecycleSnapshotBeforeEnsure.ObservedScriptPath
				: lifecycleSnapshotBeforeEnsure.ExpectedScriptPath;
			Action<string, string> lifecycleEnsureDiagnosticPhase =
				CreateDeferredScriptRebindLifecycleEnsureDiagnosticPhase(
					token,
					scheduledHostInstanceToken,
					crashTailTargetScriptPath
				);

			if (!host.EnsureLifecycleCurrent(lifecycleEnsureDiagnosticPhase))
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

			ScriptEditorLifecycleSnapshot lifecycleSnapshot =
				ScriptEditorLifecycleCoordinator.Snapshot;
			crashTailTargetScriptPath = !string.IsNullOrWhiteSpace(
				lifecycleSnapshot.ObservedScriptPath
			)
				? lifecycleSnapshot.ObservedScriptPath
				: lifecycleSnapshot.ExpectedScriptPath;
			crashTailReachedHandleScriptChanged = true;

			try
			{
				LogCompactScriptEditorCrashTail(
					"DeferredScriptRebind",
					"HandleScriptChanged.Begin",
					operationToken: token,
					targetScriptPath: crashTailTargetScriptPath,
					hostInstanceToken: scheduledHostInstanceToken
				);
				bool bindingResolved = host.HandleScriptChanged(targetTransitionId);
				LogCompactScriptEditorCrashTail(
					"DeferredScriptRebind",
					"HandleScriptChanged.Returned",
					operationToken: token,
					targetScriptPath: crashTailTargetScriptPath,
					hostInstanceToken: scheduledHostInstanceToken,
					extraDetails: $"BindingResolved='{bindingResolved}'"
				);
			}
			catch (Exception exception)
			{
				DebugLogger.LogOperation(
					"C# autocomplete deferred ScriptEditor rebind failed",
					exception.ToString()
				);
			}
			finally
			{
				ConsumeDeferredAutocompleteScriptChangeRebind(token);
				LogCompactScriptEditorCrashTail(
					"DeferredScriptRebind",
					"RefreshProcessing.Begin",
					operationToken: token,
					targetScriptPath: crashTailTargetScriptPath,
					hostInstanceToken: scheduledHostInstanceToken
				);
				RefreshEditorPluginProcessingState();
				LogCompactScriptEditorCrashTail(
					"DeferredScriptRebind",
					"RefreshProcessing.Returned",
					operationToken: token,
					targetScriptPath: crashTailTargetScriptPath,
					hostInstanceToken: scheduledHostInstanceToken
				);
			}
		}
		finally
		{
			_autocompleteDeferredScriptChangeRebindExecutionActive = false;
			if (crashTailReachedHandleScriptChanged)
			{
				LogCompactScriptEditorCrashTail(
					"DeferredScriptRebind",
					"CallbackExit",
					operationToken: token,
					targetScriptPath: crashTailTargetScriptPath,
					hostInstanceToken: scheduledHostInstanceToken
				);
			}
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

		if (
			!EnsureManagedAssemblyStateCurrent(
				"C# Autocomplete Tree Keyboard Navigation Finalize"
			)
		)
		{
			RefreshEditorPluginProcessingState();
			return;
		}

		if (_namespaceRefactorAutocompleteQuiescenceActive)
		{
			_pendingNamespaceRefactorAutocompleteScriptChange = true;
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
		if (!IsAutocompletePluginBoundaryAvailable())
		{
			LogAutocompleteCallbackBoundaryRejection("CodeCompletionRequested");
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

		if (_namespaceRefactorAutocompleteQuiescenceActive)
		{
			_suppressedNamespaceRefactorAutocompleteCompletionRequestedCount++;
			RefreshEditorPluginProcessingState();
			return;
		}

		if (TryEnsureAutocompleteHost(out AutocompletePluginHost host))
			host.HandleCompletionRequested();

		RefreshEditorPluginProcessingState();
	}

	private void OnAutocompleteCodeEditGuiInput(InputEvent inputEvent)
	{
		EnterAutocompleteCodeEditGuiInputCallbackScope();
		try
		{
			if (!IsAutocompletePluginBoundaryAvailable())
				return;

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

			if (_namespaceRefactorAutocompleteQuiescenceActive)
			{
				_suppressedNamespaceRefactorAutocompleteGuiInputCount++;
				return;
			}

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

		if (!EnsureManagedAssemblyStateCurrent("C# Autocomplete Filesystem Changed"))
			return;

		if (_namespaceRefactorAutocompleteQuiescenceActive)
		{
			_pendingNamespaceRefactorAutocompleteFilesystemChange = true;
			_suppressedNamespaceRefactorAutocompleteFilesystemChangedCount++;
			return;
		}

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
		}
	}

	private void OnAutocompleteTextChanged()
	{
		if (!IsAutocompletePluginBoundaryAvailable())
			return;

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

		if (_namespaceRefactorAutocompleteQuiescenceActive)
		{
			_pendingNamespaceRefactorAutocompleteTextChange = true;
			_suppressedNamespaceRefactorAutocompleteTextChangedCount++;
			return;
		}

		AutocompletePluginHost host = _autocompleteHost;

		if (host != null)
		{
			long hostInstanceToken = _autocompleteHostInstanceToken;
			long validationGeneration = host.BeginTextChangedValidation();
			RefreshEditorPluginProcessingState();
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

		if (_namespaceRefactorAutocompleteQuiescenceActive)
		{
			_pendingNamespaceRefactorAutocompleteTextChange = true;
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

		LogCompactScriptEditorCrashTail(
			"TextChangedValidation",
			"HostValidate.Begin",
			operationToken: validationGeneration,
			hostInstanceToken: scheduledHostInstanceToken
		);
		currentHost.ValidateAfterTextChanged(validationGeneration);
		LogCompactScriptEditorCrashTail(
			"TextChangedValidation",
			"HostValidate.Returned",
			operationToken: validationGeneration,
			hostInstanceToken: scheduledHostInstanceToken
		);
		LogCompactScriptEditorCrashTail(
			"TextChangedValidation",
			"RefreshProcessing.Begin",
			operationToken: validationGeneration,
			hostInstanceToken: scheduledHostInstanceToken
		);
		RefreshEditorPluginProcessingState();
		LogCompactScriptEditorCrashTail(
			"TextChangedValidation",
			"RefreshProcessing.Returned",
			operationToken: validationGeneration,
			hostInstanceToken: scheduledHostInstanceToken
		);
		LogCompactScriptEditorCrashTail(
			"TextChangedValidation",
			"CallbackExit",
			operationToken: validationGeneration,
			hostInstanceToken: scheduledHostInstanceToken
		);
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

	private bool HasPendingAutocompleteProcessWork()
	{
		if (
			_namespaceRefactorAutocompleteQuiescenceActive
			|| IsAutocompleteScriptChangeRebindBarrierActive
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
		if (
			_namespaceRefactorAutocompleteQuiescenceActive
			|| IsAutocompleteScriptChangeRebindBarrierActive
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

		if (
			_namespaceRefactorAutocompleteQuiescenceActive
			|| IsAutocompleteScriptChangeRebindBarrierActive
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
