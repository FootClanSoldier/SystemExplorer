#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using SystemExplorer.Autocomplete.Confirmation;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.Autocomplete;

internal readonly record struct AutocompleteCompletionRequestLease(
	long TransactionId,
	EditorBindingLease BindingLease,
	long RequestObservationSequence,
	ulong CodeEditInstanceId,
	string ScriptPath,
	AutocompleteRequestKind RequestKind,
	int CaretLine,
	int CaretColumn,
	int PrefixStartColumn,
	string LineText
);

internal readonly record struct AutocompleteOwnedCompletionPublicationLease(
	long PublicationId,
	long RequestTransactionId,
	EditorBindingLease BindingLease,
	long RequestObservationSequence,
	ulong CodeEditInstanceId,
	string ScriptPath,
	AutocompleteRequestKind RequestKind,
	int CaretLine,
	int CaretColumn,
	int PrefixStartColumn,
	string LineText
);

internal readonly record struct AutocompleteBindingActivationTransactionLease(
	long MutationTransactionId,
	string ManagedAssemblyGeneration,
	long HostInstanceToken,
	long ScriptTransitionId,
	long ReloadReadyEpoch
);

internal readonly record struct AutocompleteStableBindingMutationLease(
	long MutationTransactionId,
	string Operation,
	EditorBindingLease BindingLease,
	ulong CodeEditInstanceId,
	string ScriptPath
);

internal sealed class AutocompleteCodeEditMutationCoordinator
{
	private readonly string _managedAssemblyGeneration;
	private readonly Func<long> _hostInstanceTokenProvider;
	private readonly Func<long> _currentReloadReadyEpochProvider;
	private readonly Func<bool> _reloadStabilizationReadyProvider;
	private readonly ScriptEditorLifecycleCoordinator _scriptEditorLifecycleCoordinator;
	private readonly AutocompleteCodeEditPresenter _presenter;
	private readonly AutocompleteProjectTypeConfirmationService _projectTypeConfirmationService;
	private readonly Func<EditorBindingLease, CodeEdit, string, bool> _isBindingCurrent;
	private readonly Action<string, string> _debugLog;
	private long _nextRequestTransactionId;
	private long _nextPublicationId;
	private long _nextMutationTransactionId;
	private AutocompleteOwnedCompletionPublicationLease? _ownedPublicationLease;
	private AutocompleteBindingActivationTransactionLease? _activeBindingActivation;
	private AutocompleteStableBindingMutationLease? _activeStableBindingMutation;

	internal AutocompleteCodeEditMutationCoordinator(
		string managedAssemblyGeneration,
		Func<long> hostInstanceTokenProvider,
		Func<long> currentReloadReadyEpochProvider,
		Func<bool> reloadStabilizationReadyProvider,
		ScriptEditorLifecycleCoordinator scriptEditorLifecycleCoordinator,
		AutocompleteCodeEditPresenter presenter,
		AutocompleteProjectTypeConfirmationService projectTypeConfirmationService,
		Func<EditorBindingLease, CodeEdit, string, bool> isBindingCurrent,
		Action<string, string> debugLog
	)
	{
		_managedAssemblyGeneration = !string.IsNullOrWhiteSpace(managedAssemblyGeneration)
			? managedAssemblyGeneration
			: throw new ArgumentException(
				"Managed assembly generation is required.",
				nameof(managedAssemblyGeneration)
			);
		_hostInstanceTokenProvider =
			hostInstanceTokenProvider
			?? throw new ArgumentNullException(nameof(hostInstanceTokenProvider));
		_currentReloadReadyEpochProvider =
			currentReloadReadyEpochProvider
			?? throw new ArgumentNullException(nameof(currentReloadReadyEpochProvider));
		_reloadStabilizationReadyProvider =
			reloadStabilizationReadyProvider
			?? throw new ArgumentNullException(nameof(reloadStabilizationReadyProvider));
		_scriptEditorLifecycleCoordinator =
			scriptEditorLifecycleCoordinator
			?? throw new ArgumentNullException(nameof(scriptEditorLifecycleCoordinator));
		_presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
		_projectTypeConfirmationService =
			projectTypeConfirmationService
			?? throw new ArgumentNullException(nameof(projectTypeConfirmationService));
		_isBindingCurrent =
			isBindingCurrent ?? throw new ArgumentNullException(nameof(isBindingCurrent));
		_debugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
	}

	internal bool TryBeginBindingActivation(
		long hostInstanceToken,
		long scriptTransitionId,
		long reloadReadyEpoch,
		out AutocompleteBindingActivationTransactionLease lease
	)
	{
		lease = default;
		if (_activeBindingActivation.HasValue)
			return false;
		if (_activeStableBindingMutation.HasValue)
		{
			LogNativeMutationAuthorityRejection(
				"BindingActivation",
				"StableBindingMutationActive"
			);
			return false;
		}
		if (hostInstanceToken <= 0 || scriptTransitionId <= 0 || reloadReadyEpoch <= 0)
			return false;
		if (hostInstanceToken != _hostInstanceTokenProvider())
			return false;
		if (
			!_scriptEditorLifecycleCoordinator.CanResolveBinding(
				_managedAssemblyGeneration,
				scriptTransitionId
			)
		)
		{
			return false;
		}
		if (!IsBindingActivationReloadAuthorityCurrent(reloadReadyEpoch))
			return false;

		RetireOwnedPublication("BindingActivation");
		lease = new AutocompleteBindingActivationTransactionLease(
			NextPositive(ref _nextMutationTransactionId),
			_managedAssemblyGeneration,
			hostInstanceToken,
			scriptTransitionId,
			reloadReadyEpoch
		);
		_activeBindingActivation = lease;
		LogBindingActivationBegin(lease);
		return true;
	}

	internal bool OwnsBindingActivation(AutocompleteBindingActivationTransactionLease lease)
	{
		return _activeBindingActivation.HasValue
			&& _activeBindingActivation.Value.Equals(lease);
	}

	internal bool IsBindingActivationForwardAuthorityCurrent(
		AutocompleteBindingActivationTransactionLease lease
	)
	{
		return OwnsBindingActivation(lease)
			&& lease.HostInstanceToken == _hostInstanceTokenProvider()
			&& _scriptEditorLifecycleCoordinator.CanResolveBinding(
				lease.ManagedAssemblyGeneration,
				lease.ScriptTransitionId
			)
			&& IsBindingActivationReloadAuthorityCurrent(lease.ReloadReadyEpoch);
	}

	internal void EndBindingActivation(
		AutocompleteBindingActivationTransactionLease lease,
		string outcome,
		EditorBindingLease? committedBindingLease = null
	)
	{
		if (!OwnsBindingActivation(lease))
			return;

		_activeBindingActivation = null;
		LogBindingActivationReturned(lease, outcome, committedBindingLease);
	}

	internal void RetireOwnedPublication(string reason)
	{
		if (!_ownedPublicationLease.HasValue)
			return;

		AutocompleteOwnedCompletionPublicationLease consumed =
			_ownedPublicationLease.Value;
		_ownedPublicationLease = null;
		LogPublicationConsumed(consumed, reason);
	}

	internal bool TryCreateRequestLease(
		CodeEdit codeEdit,
		string scriptPath,
		EditorBindingLease bindingLease,
		long requestObservationSequence,
		AutocompleteRequestContext request,
		string lineText,
		out AutocompleteCompletionRequestLease requestLease
	)
	{
		requestLease = default;
		if (RejectCompletionMutationWhileNativeMutationActive("RequestTransactionAdmission"))
			return false;
		if (request == null || requestObservationSequence <= 0)
			return false;

		if (
			!TryValidateRequestAnchor(
				bindingLease,
				codeEdit,
				scriptPath,
				request.CaretLine,
				request.CaretColumn,
				lineText,
				out _
			)
		)
		{
			return false;
		}

		requestLease = new AutocompleteCompletionRequestLease(
			NextPositive(ref _nextRequestTransactionId),
			bindingLease,
			requestObservationSequence,
			bindingLease.CodeEditInstanceId,
			ScriptPathUtility.Normalize(scriptPath),
			request.Kind,
			request.CaretLine,
			request.CaretColumn,
			request.PrefixStartColumn,
			lineText ?? ""
		);

		LogRequestTransactionAdmitted(requestLease);
		return true;
	}

	internal bool TryPublish(
		CodeEdit codeEdit,
		AutocompleteCompletionRequestLease requestLease,
		IReadOnlyList<AutocompleteCompletionItem> items,
		out AutocompleteOwnedCompletionPublicationLease publicationLease
	)
	{
		publicationLease = default;
		if (RejectCompletionMutationWhileNativeMutationActive("Publish"))
			return false;
		if (items == null)
			throw new ArgumentNullException(nameof(items));

		if (!TryValidateRequestLease(codeEdit, requestLease, out string rejectionReason))
		{
			LogPrePublishRevalidation(
				"C# autocomplete completion pre-publish revalidation rejected",
				requestLease,
				rejectionReason
			);
			return false;
		}

		LogPrePublishRevalidation(
			"C# autocomplete completion pre-publish revalidation accepted",
			requestLease,
			""
		);

		if (
			!TryBeginStableBindingMutation(
				"Publish",
				requestLease.BindingLease,
				codeEdit,
				requestLease.ScriptPath,
				out AutocompleteStableBindingMutationLease mutationLease
			)
		)
		{
			LogPublicationLeaseRejected(
				requestLease,
				"StableBindingMutationAdmissionRejected"
			);
			return false;
		}

		try
		{
			if (
				!IsStableBindingMutationForwardAuthorityCurrent(
					mutationLease,
					codeEdit,
					requestLease.ScriptPath
				)
			)
			{
				LogPublicationLeaseRejected(
					requestLease,
					"StableBindingMutationAuthorityChanged"
				);
				return false;
			}

			if (!TryValidateRequestLease(codeEdit, requestLease, out rejectionReason))
			{
				LogPublicationLeaseRejected(
					requestLease,
					$"FinalRequestAnchorRevalidationFailed:{rejectionReason}"
				);
				return false;
			}

			AutocompleteCompletionDiagnosticContext diagnosticContext =
				AutocompleteCompletionDiagnosticContext.FromRequestLease(
					requestLease,
					mutationLease.MutationTransactionId
				);
			_presenter.Publish(codeEdit, items, diagnosticContext);

			if (
				!IsStableBindingMutationForwardAuthorityCurrent(
					mutationLease,
					codeEdit,
					requestLease.ScriptPath
				)
			)
			{
				LogPublicationLeaseRejected(
					requestLease,
					"StableBindingMutationAuthorityChangedAfterPublish"
				);
				return false;
			}

			if (!TryValidateRequestLease(codeEdit, requestLease, out rejectionReason))
			{
				LogPublicationLeaseRejected(
					requestLease,
					$"PostPublishRequestAnchorRevalidationFailed:{rejectionReason}"
				);
				return false;
			}

			publicationLease = new AutocompleteOwnedCompletionPublicationLease(
				NextPositive(ref _nextPublicationId),
				requestLease.TransactionId,
				requestLease.BindingLease,
				requestLease.RequestObservationSequence,
				requestLease.CodeEditInstanceId,
				requestLease.ScriptPath,
				requestLease.RequestKind,
				requestLease.CaretLine,
				requestLease.CaretColumn,
				requestLease.PrefixStartColumn,
				requestLease.LineText
			);
			_ownedPublicationLease = publicationLease;
			LogPublicationLeaseAcquired(publicationLease);
			return true;
		}
		finally
		{
			EndStableBindingMutation(mutationLease);
		}
	}

	internal bool TryCancelOwnedPublication(
		CodeEdit codeEdit,
		string scriptPath,
		EditorBindingLease currentBindingLease,
		string reason
	)
	{
		if (RejectCompletionMutationWhileNativeMutationActive("OwnedCancel"))
			return false;
		if (
			!TryGetCurrentOwnedPublication(
				codeEdit,
				scriptPath,
				currentBindingLease,
				out AutocompleteOwnedCompletionPublicationLease owned,
				out _
			)
		)
		{
			return false;
		}

		if (
			!TryBeginStableBindingMutation(
				"OwnedCancel",
				owned.BindingLease,
				codeEdit,
				scriptPath,
				out AutocompleteStableBindingMutationLease mutationLease
			)
		)
		{
			return false;
		}

		try
		{
			if (
				!IsStableBindingMutationForwardAuthorityCurrent(
					mutationLease,
					codeEdit,
					scriptPath
				)
			)
			{
				return false;
			}

			if (
				!TryGetCurrentOwnedPublication(
					codeEdit,
					scriptPath,
					currentBindingLease,
					out AutocompleteOwnedCompletionPublicationLease revalidatedOwned,
					out _
				)
				|| !revalidatedOwned.Equals(owned)
			)
			{
				return false;
			}

			_ownedPublicationLease = null;
			LogPublicationConsumed(owned, reason);

			if (
				!IsStableBindingMutationForwardAuthorityCurrent(
					mutationLease,
					codeEdit,
					scriptPath
				)
			)
			{
				return false;
			}

			LogOwnedCancellationBoundary(
				"C# autocomplete owned completion cancellation begin",
				mutationLease,
				owned,
				reason
			);
			codeEdit.CancelCodeCompletion();
			LogOwnedCancellationBoundary(
				"C# autocomplete owned completion cancellation returned",
				mutationLease,
				owned,
				reason
			);
			return true;
		}
		finally
		{
			EndStableBindingMutation(mutationLease);
		}
	}

	internal AutocompleteDeferredUsingInsertionApplyResult TryExecuteDeferredUsingInsertion(
		CodeEdit codeEdit,
		string scriptPath,
		EditorBindingLease currentBindingLease,
		AutocompleteDeferredUsingInsertionRequest request,
		long hostInstanceToken,
		string managedAssemblyGeneration,
		int guiInputCallbackDepth
	)
	{
		string normalizedScriptPath = ScriptPathUtility.Normalize(scriptPath);
		if (request == null)
		{
			return AutocompleteDeferredUsingInsertionApplyResult.Rejected(
				"PluginUnavailable",
				currentBindingLease.CodeEditInstanceId,
				normalizedScriptPath,
				currentBindingLease
			);
		}

		if (!currentBindingLease.Equals(request.BindingLease))
		{
			return AutocompleteDeferredUsingInsertionApplyResult.Rejected(
				"BindingLeaseChanged",
				currentBindingLease.CodeEditInstanceId,
				normalizedScriptPath,
				currentBindingLease
			);
		}

		string normalizedRequestScriptPath = ScriptPathUtility.Normalize(
			request.BindingLease.ScriptResourcePath
		);
		if (
			string.IsNullOrWhiteSpace(normalizedScriptPath)
			|| !string.Equals(
				normalizedScriptPath,
				normalizedRequestScriptPath,
				StringComparison.OrdinalIgnoreCase
			)
			|| request.CodeEditNativeInstanceId == 0
			|| request.CodeEditNativeInstanceId != request.BindingLease.CodeEditInstanceId
			|| currentBindingLease.CodeEditInstanceId != request.CodeEditNativeInstanceId
		)
		{
			return AutocompleteDeferredUsingInsertionApplyResult.Rejected(
				"BindingLeaseChanged",
				currentBindingLease.CodeEditInstanceId,
				normalizedScriptPath,
				currentBindingLease
			);
		}

		if (RejectCompletionMutationWhileNativeMutationActive("DeferredUsingInsertText"))
		{
			return AutocompleteDeferredUsingInsertionApplyResult.Rejected(
				"StableBindingMutationAdmissionRejected",
				currentBindingLease.CodeEditInstanceId,
				normalizedScriptPath,
				currentBindingLease
			);
		}

		if (
			!TryBeginStableBindingMutation(
				"DeferredUsingInsertText",
				request.BindingLease,
				codeEdit,
				normalizedScriptPath,
				out AutocompleteStableBindingMutationLease mutationLease
			)
		)
		{
			return AutocompleteDeferredUsingInsertionApplyResult.Rejected(
				"StableBindingMutationAdmissionRejected",
				currentBindingLease.CodeEditInstanceId,
				normalizedScriptPath,
				currentBindingLease
			);
		}

		try
		{
			if (
				!IsStableBindingMutationForwardAuthorityCurrent(
					mutationLease,
					codeEdit,
					normalizedScriptPath
				)
			)
			{
				return AutocompleteDeferredUsingInsertionApplyResult.Rejected(
					"StableBindingMutationAuthorityChanged",
					mutationLease.CodeEditInstanceId,
					mutationLease.ScriptPath,
					mutationLease.BindingLease
				);
			}

			if (
				!mutationLease.BindingLease.Equals(request.BindingLease)
				|| !currentBindingLease.Equals(request.BindingLease)
			)
			{
				return AutocompleteDeferredUsingInsertionApplyResult.Rejected(
					"BindingLeaseChanged",
					mutationLease.CodeEditInstanceId,
					mutationLease.ScriptPath,
					mutationLease.BindingLease
				);
			}

			var executionContext = new AutocompleteDeferredUsingInsertionExecutionContext(
				mutationLease.MutationTransactionId,
				mutationLease.CodeEditInstanceId,
				mutationLease.ScriptPath,
				mutationLease.BindingLease,
				hostInstanceToken,
				managedAssemblyGeneration ?? "",
				guiInputCallbackDepth
			);

			return _projectTypeConfirmationService.ApplyDeferredUsingInsertion(
				codeEdit,
				request,
				executionContext
			);
		}
		finally
		{
			EndStableBindingMutation(mutationLease);
		}
	}

	internal bool TryRequestCodeCompletion(
		CodeEdit codeEdit,
		string scriptPath,
		EditorBindingLease bindingLease,
		bool force,
		string retirementReason
	)
	{
		if (RejectCompletionMutationWhileNativeMutationActive("RequestCodeCompletion"))
			return false;
		if (!_isBindingCurrent(bindingLease, codeEdit, scriptPath))
			return false;

		RetireOwnedPublication(retirementReason);

		// Intentionally not wrapped in an ordinary StableBindingMutation. Godot may
		// synchronously raise CodeCompletionRequested from this native dispatch, and
		// that legitimate child callback must be able to admit its own request/publish
		// transaction. A future request-dispatch parent/child protocol must model that
		// reentrancy explicitly before this call can join the exclusive stable slot.
		codeEdit.RequestCodeCompletion(force);
		return true;
	}

	internal bool TryExecuteOwnedConfirmation(
		CodeEdit codeEdit,
		string scriptPath,
		EditorBindingLease currentBindingLease,
		AutocompleteCompletionOptionMetadata metadata,
		bool requestedReplace,
		AutocompleteProjectTypeConfirmationPreparation preparation,
		out AutocompleteOwnedCompletionPublicationLease consumedPublication,
		out AutocompleteProjectTypeConfirmationResult confirmationResult
	)
	{
		consumedPublication = default;
		confirmationResult = null;

		if (RejectCompletionMutationWhileNativeMutationActive("Confirmation"))
			return false;
		if (metadata == null || preparation == null)
			return false;

		if (
			!TryGetCurrentOwnedPublication(
				codeEdit,
				scriptPath,
				currentBindingLease,
				out AutocompleteOwnedCompletionPublicationLease owned,
				out string rejectionReason
			)
		)
		{
			LogConfirmationLeaseRejected(
				currentBindingLease,
				codeEdit,
				scriptPath,
				rejectionReason
			);
			return false;
		}

		if (
			!TryBeginStableBindingMutation(
				"Confirmation",
				currentBindingLease,
				codeEdit,
				scriptPath,
				out AutocompleteStableBindingMutationLease mutationLease
			)
		)
		{
			LogConfirmationLeaseRejected(
				currentBindingLease,
				codeEdit,
				scriptPath,
				"StableBindingMutationAdmissionRejected"
			);
			return false;
		}

		try
		{
			if (
				!IsStableBindingMutationForwardAuthorityCurrent(
					mutationLease,
					codeEdit,
					scriptPath
				)
			)
			{
				LogConfirmationLeaseRejected(
					currentBindingLease,
					codeEdit,
					scriptPath,
					"StableBindingMutationAuthorityChanged"
				);
				return false;
			}

			_ownedPublicationLease = null;
			consumedPublication = owned;
			LogPublicationConsumed(owned, "Confirmation");

			if (
				!IsStableBindingMutationForwardAuthorityCurrent(
					mutationLease,
					codeEdit,
					scriptPath
				)
			)
			{
				LogConfirmationLeaseRejected(
					currentBindingLease,
					codeEdit,
					scriptPath,
					"FinalBindingRevalidationFailedAfterConsume"
				);
				return false;
			}

			LogOwnedConfirmationMutationBegin(
				mutationLease,
				owned,
				requestedReplace,
				preparation.NativeReplace
			);

			confirmationResult =
				_projectTypeConfirmationService.ExecutePreparedConfirmation(
					codeEdit,
					metadata,
					preparation
				);

			LogOwnedConfirmationMutationReturned(
				mutationLease,
				owned,
				requestedReplace,
				preparation.NativeReplace,
				confirmationResult
			);
			return true;
		}
		finally
		{
			EndStableBindingMutation(mutationLease);
		}
	}

	private bool TryBeginStableBindingMutation(
		string operation,
		EditorBindingLease bindingLease,
		CodeEdit codeEdit,
		string scriptPath,
		out AutocompleteStableBindingMutationLease lease
	)
	{
		lease = default;
		if (_activeBindingActivation.HasValue || _activeStableBindingMutation.HasValue)
			return false;
		if (string.IsNullOrWhiteSpace(operation))
			return false;
		if (!IsExactStableBindingCurrent(bindingLease, codeEdit, scriptPath))
			return false;

		string normalizedScriptPath = ScriptPathUtility.Normalize(scriptPath);
		lease = new AutocompleteStableBindingMutationLease(
			NextPositive(ref _nextMutationTransactionId),
			operation,
			bindingLease,
			bindingLease.CodeEditInstanceId,
			normalizedScriptPath
		);
		_activeStableBindingMutation = lease;
		return true;
	}

	private bool OwnsStableBindingMutation(AutocompleteStableBindingMutationLease lease)
	{
		return _activeStableBindingMutation.HasValue
			&& _activeStableBindingMutation.Value.Equals(lease);
	}

	private bool IsStableBindingMutationForwardAuthorityCurrent(
		AutocompleteStableBindingMutationLease lease,
		CodeEdit codeEdit,
		string scriptPath
	)
	{
		return OwnsStableBindingMutation(lease)
			&& lease.CodeEditInstanceId == lease.BindingLease.CodeEditInstanceId
			&& string.Equals(
				lease.ScriptPath,
				ScriptPathUtility.Normalize(scriptPath),
				StringComparison.OrdinalIgnoreCase
			)
			&& IsExactStableBindingCurrent(lease.BindingLease, codeEdit, scriptPath);
	}

	private void EndStableBindingMutation(AutocompleteStableBindingMutationLease lease)
	{
		if (!OwnsStableBindingMutation(lease))
			return;

		_activeStableBindingMutation = null;
	}

	private bool IsBindingActivationReloadAuthorityCurrent(long reloadReadyEpoch)
	{
		if (reloadReadyEpoch <= 0)
			return false;

		long currentReloadReadyEpoch = _currentReloadReadyEpochProvider();
		if (currentReloadReadyEpoch != reloadReadyEpoch)
			return false;

		if (_reloadStabilizationReadyProvider())
			return true;

		// First post-reload binding activation is intentionally performed while the
		// reload coordinator is ActivationPending. In that state the candidate has
		// already earned a nonzero ReloadReadyEpoch, but IsReady becomes true only
		// after the exact BindingEpoch commit is proven and activation is completed.
		return currentReloadReadyEpoch > 0;
	}

	private bool RejectCompletionMutationWhileNativeMutationActive(string operation)
	{
		if (_activeBindingActivation.HasValue)
		{
			AutocompleteBindingActivationTransactionLease activation =
				_activeBindingActivation.Value;
			Log(
				"C# autocomplete completion mutation rejected",
				$"Reason='BindingActivationActive', Operation='{operation ?? ""}', MutationTransactionId='{activation.MutationTransactionId}', ManagedAssemblyGeneration='{activation.ManagedAssemblyGeneration}', HostInstanceToken='{activation.HostInstanceToken}', ScriptTransitionId='{activation.ScriptTransitionId}', ReloadReadyEpoch='{activation.ReloadReadyEpoch}'"
			);
			return true;
		}

		if (_activeStableBindingMutation.HasValue)
		{
			AutocompleteStableBindingMutationLease mutation =
				_activeStableBindingMutation.Value;
			Log(
				"C# autocomplete completion mutation rejected",
				$"Reason='StableBindingMutationActive', Operation='{operation ?? ""}', ActiveOperation='{mutation.Operation ?? ""}', MutationTransactionId='{mutation.MutationTransactionId}', ScriptTransitionId='{mutation.BindingLease.ScriptTransitionId}', BindingEpoch='{mutation.BindingLease.BindingEpoch}', ReloadReadyEpoch='{mutation.BindingLease.ReloadReadyEpoch}', HostInstanceToken='{mutation.BindingLease.HostInstanceToken}', CodeEditInstanceId='{mutation.CodeEditInstanceId}', ScriptPath='{mutation.ScriptPath ?? ""}'"
			);
			return true;
		}

		return false;
	}

	private void LogNativeMutationAuthorityRejection(string operation, string reason)
	{
		if (!_activeStableBindingMutation.HasValue)
			return;

		AutocompleteStableBindingMutationLease mutation =
			_activeStableBindingMutation.Value;
		Log(
			"C# autocomplete native mutation authority rejected",
			$"Reason='{reason ?? ""}', Operation='{operation ?? ""}', ActiveOperation='{mutation.Operation ?? ""}', MutationTransactionId='{mutation.MutationTransactionId}', ScriptTransitionId='{mutation.BindingLease.ScriptTransitionId}', BindingEpoch='{mutation.BindingLease.BindingEpoch}', ReloadReadyEpoch='{mutation.BindingLease.ReloadReadyEpoch}', HostInstanceToken='{mutation.BindingLease.HostInstanceToken}', CodeEditInstanceId='{mutation.CodeEditInstanceId}', ScriptPath='{mutation.ScriptPath ?? ""}'"
		);
	}

	private bool IsExactStableBindingCurrent(
		EditorBindingLease bindingLease,
		CodeEdit codeEdit,
		string scriptPath
	)
	{
		if (!IsValidStableBindingLease(bindingLease))
			return false;
		if (!_isBindingCurrent(bindingLease, codeEdit, scriptPath))
			return false;

		try
		{
			if (codeEdit == null || !GodotObject.IsInstanceValid(codeEdit))
				return false;
			if (codeEdit.GetInstanceId() != bindingLease.CodeEditInstanceId)
				return false;
		}
		catch
		{
			return false;
		}

		string normalizedScriptPath = ScriptPathUtility.Normalize(scriptPath);
		string normalizedLeasePath = ScriptPathUtility.Normalize(
			bindingLease.ScriptResourcePath
		);
		return !string.IsNullOrWhiteSpace(normalizedScriptPath)
			&& string.Equals(
				normalizedScriptPath,
				normalizedLeasePath,
				StringComparison.OrdinalIgnoreCase
			);
	}

	private static bool IsValidStableBindingLease(EditorBindingLease bindingLease)
	{
		return !string.IsNullOrWhiteSpace(bindingLease.ManagedAssemblyGeneration)
			&& bindingLease.HostInstanceToken > 0
			&& bindingLease.ScriptTransitionId > 0
			&& bindingLease.ReloadReadyEpoch > 0
			&& bindingLease.BindingEpoch > 0
			&& bindingLease.ScriptEditorInstanceId != 0
			&& bindingLease.ScriptEditorBaseInstanceId != 0
			&& bindingLease.CodeEditInstanceId != 0
			&& !string.IsNullOrWhiteSpace(
				ScriptPathUtility.Normalize(bindingLease.ScriptResourcePath)
			);
	}

	private bool TryValidateRequestLease(
		CodeEdit codeEdit,
		AutocompleteCompletionRequestLease requestLease,
		out string rejectionReason
	)
	{
		return TryValidateRequestAnchor(
			requestLease.BindingLease,
			codeEdit,
			requestLease.ScriptPath,
			requestLease.CaretLine,
			requestLease.CaretColumn,
			requestLease.LineText,
			out rejectionReason
		);
	}

	private bool TryValidateRequestAnchor(
		EditorBindingLease bindingLease,
		CodeEdit codeEdit,
		string scriptPath,
		int caretLine,
		int caretColumn,
		string lineText,
		out string rejectionReason
	)
	{
		rejectionReason = "";
		if (!_isBindingCurrent(bindingLease, codeEdit, scriptPath))
		{
			rejectionReason = "BindingChanged";
			return false;
		}

		try
		{
			if (codeEdit.GetCaretLine() != caretLine)
			{
				rejectionReason = "CaretLineChanged";
				return false;
			}
			if (codeEdit.GetCaretColumn() != caretColumn)
			{
				rejectionReason = "CaretColumnChanged";
				return false;
			}
			if (caretLine < 0 || caretLine >= codeEdit.GetLineCount())
			{
				rejectionReason = "CaretLineUnavailable";
				return false;
			}
			if (!string.Equals(codeEdit.GetLine(caretLine) ?? "", lineText ?? "", StringComparison.Ordinal))
			{
				rejectionReason = "LineTextChanged";
				return false;
			}
		}
		catch
		{
			rejectionReason = "AnchorReadFailed";
			return false;
		}

		return true;
	}

	private bool TryGetCurrentOwnedPublication(
		CodeEdit codeEdit,
		string scriptPath,
		EditorBindingLease currentBindingLease,
		out AutocompleteOwnedCompletionPublicationLease owned,
		out string rejectionReason
	)
	{
		owned = default;
		rejectionReason = "";
		if (!_ownedPublicationLease.HasValue)
		{
			rejectionReason = "NoOwnedPublication";
			return false;
		}

		AutocompleteOwnedCompletionPublicationLease candidate =
			_ownedPublicationLease.Value;
		if (!candidate.BindingLease.Equals(currentBindingLease))
		{
			rejectionReason = "BindingLeaseChanged";
			return false;
		}
		if (!_isBindingCurrent(currentBindingLease, codeEdit, scriptPath))
		{
			rejectionReason = "BindingNotCurrent";
			return false;
		}

		try
		{
			if (codeEdit.GetInstanceId() != candidate.CodeEditInstanceId)
			{
				rejectionReason = "CodeEditChanged";
				return false;
			}
		}
		catch
		{
			rejectionReason = "CodeEditUnavailable";
			return false;
		}

		string normalizedScriptPath = ScriptPathUtility.Normalize(scriptPath);
		if (
			!string.Equals(
				normalizedScriptPath,
				candidate.ScriptPath,
				StringComparison.OrdinalIgnoreCase
			)
		)
		{
			rejectionReason = "ScriptChanged";
			return false;
		}

		owned = candidate;
		return true;
	}

	private void LogRequestTransactionAdmitted(AutocompleteCompletionRequestLease lease)
	{
		Log(
			"C# autocomplete completion request transaction admitted",
			DescribeRequestLease(lease)
		);
	}

	private void LogBindingActivationBegin(
		AutocompleteBindingActivationTransactionLease lease
	)
	{
		Log(
			"C# autocomplete CodeEdit binding activation transaction begin",
			$"MutationTransactionId='{lease.MutationTransactionId}', ManagedAssemblyGeneration='{lease.ManagedAssemblyGeneration}', HostInstanceToken='{lease.HostInstanceToken}', ScriptTransitionId='{lease.ScriptTransitionId}', ReloadReadyEpoch='{lease.ReloadReadyEpoch}'"
		);
	}

	private void LogBindingActivationReturned(
		AutocompleteBindingActivationTransactionLease lease,
		string outcome,
		EditorBindingLease? committedBindingLease
	)
	{
		EditorBindingLease committed = committedBindingLease ?? default;
		Log(
			"C# autocomplete CodeEdit binding activation transaction returned",
			$"MutationTransactionId='{lease.MutationTransactionId}', ManagedAssemblyGeneration='{lease.ManagedAssemblyGeneration}', HostInstanceToken='{lease.HostInstanceToken}', ScriptTransitionId='{lease.ScriptTransitionId}', ReloadReadyEpoch='{lease.ReloadReadyEpoch}', Outcome='{outcome ?? ""}', CommittedBindingEpoch='{committed.BindingEpoch}', CodeEditInstanceId='{committed.CodeEditInstanceId}', ScriptPath='{committed.ScriptResourcePath ?? ""}'"
		);
	}

	private void LogPrePublishRevalidation(
		string operation,
		AutocompleteCompletionRequestLease lease,
		string reason
	)
	{
		string details = DescribeRequestLease(lease);
		if (!string.IsNullOrWhiteSpace(reason))
			details += $", Reason='{reason}'";
		Log(operation, details);
	}

	private void LogPublicationLeaseAcquired(
		AutocompleteOwnedCompletionPublicationLease lease
	)
	{
		Log(
			"C# autocomplete completion publication lease acquired",
			DescribePublicationLease(lease)
		);
	}

	private void LogPublicationLeaseRejected(
		AutocompleteCompletionRequestLease lease,
		string reason
	)
	{
		Log(
			"C# autocomplete completion publication lease rejected",
			$"{DescribeRequestLease(lease)}, Reason='{reason ?? ""}'"
		);
	}

	private void LogPublicationConsumed(
		AutocompleteOwnedCompletionPublicationLease lease,
		string reason
	)
	{
		Log(
			"C# autocomplete completion publication lease consumed",
			$"{DescribePublicationLease(lease)}, Reason='{reason ?? ""}'"
		);
	}

	private void LogOwnedCancellationBoundary(
		string operation,
		AutocompleteStableBindingMutationLease mutationLease,
		AutocompleteOwnedCompletionPublicationLease lease,
		string reason
	)
	{
		Log(
			operation,
			$"MutationTransactionId='{mutationLease.MutationTransactionId}', {DescribePublicationLease(lease)}, Reason='{reason ?? ""}'"
		);
	}

	private void LogOwnedConfirmationMutationBegin(
		AutocompleteStableBindingMutationLease mutationLease,
		AutocompleteOwnedCompletionPublicationLease publicationLease,
		bool requestedReplace,
		bool nativeReplace
	)
	{
		Log(
			"C# autocomplete owned confirmation native mutation begin",
			DescribeOwnedConfirmationMutation(
				mutationLease,
				publicationLease,
				requestedReplace,
				nativeReplace
			)
		);
	}

	private void LogOwnedConfirmationMutationReturned(
		AutocompleteStableBindingMutationLease mutationLease,
		AutocompleteOwnedCompletionPublicationLease publicationLease,
		bool requestedReplace,
		bool nativeReplace,
		AutocompleteProjectTypeConfirmationResult result
	)
	{
		Log(
			"C# autocomplete owned confirmation native mutation returned",
			$"{DescribeOwnedConfirmationMutation(mutationLease, publicationLease, requestedReplace, nativeReplace)}, ConfirmationSucceeded='{result?.ConfirmationSucceeded == true}', UsingAction='{result?.UsingAction ?? ""}'"
		);
	}

	private static string DescribeOwnedConfirmationMutation(
		AutocompleteStableBindingMutationLease mutationLease,
		AutocompleteOwnedCompletionPublicationLease publicationLease,
		bool requestedReplace,
		bool nativeReplace
	)
	{
		EditorBindingLease bindingLease = mutationLease.BindingLease;
		return $"MutationTransactionId='{mutationLease.MutationTransactionId}', PublicationId='{publicationLease.PublicationId}', RequestTransactionId='{publicationLease.RequestTransactionId}', ScriptTransitionId='{bindingLease.ScriptTransitionId}', BindingEpoch='{bindingLease.BindingEpoch}', ReloadReadyEpoch='{bindingLease.ReloadReadyEpoch}', HostInstanceToken='{bindingLease.HostInstanceToken}', CodeEditInstanceId='{mutationLease.CodeEditInstanceId}', ScriptPath='{mutationLease.ScriptPath ?? ""}', RequestedReplace='{requestedReplace}', NativeReplace='{nativeReplace}'";
	}

	private void LogConfirmationLeaseRejected(
		EditorBindingLease bindingLease,
		CodeEdit codeEdit,
		string scriptPath,
		string reason
	)
	{
		ulong currentCodeEditId = 0;
		try
		{
			if (codeEdit != null && GodotObject.IsInstanceValid(codeEdit))
				currentCodeEditId = codeEdit.GetInstanceId();
		}
		catch
		{
		}

		Log(
			"C# autocomplete confirmation lease rejected",
			$"Reason='{reason ?? ""}', ScriptTransitionId='{bindingLease.ScriptTransitionId}', BindingEpoch='{bindingLease.BindingEpoch}', ReloadReadyEpoch='{bindingLease.ReloadReadyEpoch}', CodeEditInstanceId='{currentCodeEditId}', ScriptPath='{ScriptPathUtility.Normalize(scriptPath)}'"
		);
	}

	private static string DescribeRequestLease(AutocompleteCompletionRequestLease lease)
	{
		return $"RequestTransactionId='{lease.TransactionId}', RequestObservationSequence='{lease.RequestObservationSequence}', ScriptTransitionId='{lease.BindingLease.ScriptTransitionId}', BindingEpoch='{lease.BindingLease.BindingEpoch}', ReloadReadyEpoch='{lease.BindingLease.ReloadReadyEpoch}', HostInstanceToken='{lease.BindingLease.HostInstanceToken}', CodeEditInstanceId='{lease.CodeEditInstanceId}', ScriptPath='{lease.ScriptPath ?? ""}', RequestKind='{lease.RequestKind}', CaretLine='{lease.CaretLine}', CaretColumn='{lease.CaretColumn}', PrefixStartColumn='{lease.PrefixStartColumn}'";
	}

	private static string DescribePublicationLease(
		AutocompleteOwnedCompletionPublicationLease lease
	)
	{
		return $"PublicationId='{lease.PublicationId}', RequestTransactionId='{lease.RequestTransactionId}', RequestObservationSequence='{lease.RequestObservationSequence}', ScriptTransitionId='{lease.BindingLease.ScriptTransitionId}', BindingEpoch='{lease.BindingLease.BindingEpoch}', ReloadReadyEpoch='{lease.BindingLease.ReloadReadyEpoch}', HostInstanceToken='{lease.BindingLease.HostInstanceToken}', CodeEditInstanceId='{lease.CodeEditInstanceId}', ScriptPath='{lease.ScriptPath ?? ""}'";
	}

	private void Log(string operation, string details)
	{
		try
		{
			_debugLog(operation ?? "", details ?? "");
		}
		catch
		{
			// Completion ownership diagnostics must never affect mutation authority.
		}
	}

	private static long NextPositive(ref long value)
	{
		unchecked
		{
			value++;
			if (value <= 0)
				value = 1;
		}

		return value;
	}
}
#endif
