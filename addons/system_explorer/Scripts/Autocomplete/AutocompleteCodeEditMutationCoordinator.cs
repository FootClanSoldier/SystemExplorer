#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using SystemExplorer.Autocomplete.Confirmation;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.Autocomplete;

internal enum AutocompleteRequestDispatchOrigin
{
	None = 0,
	DormantRecovery,
	ForcedMemberFollowUp,
}

internal enum AutocompleteExternalMutationOrigin
{
	None = 0,
	NamespaceRefactor,
	CreateScript,
	SceneOpen,
}

internal enum AutocompleteRequestDispatchChildCaptureResult
{
	NoActiveRequestDispatch,
	AuthorizedChild,
	RejectedChild,
}

internal readonly record struct AutocompleteRequestDispatchTransactionLease(
	long MutationTransactionId,
	AutocompleteRequestDispatchOrigin Origin,
	bool Force,
	EditorBindingLease BindingLease,
	ulong CodeEditInstanceId,
	string ScriptPath
);

internal readonly record struct AutocompleteRequestDispatchChildLease(
	long ParentRequestDispatchMutationTransactionId,
	AutocompleteRequestDispatchOrigin Origin,
	EditorBindingLease BindingLease,
	ulong CodeEditInstanceId,
	string ScriptPath,
	long RequestObservationSequence
);

internal readonly record struct AutocompleteCompletionRequestLease(
	long TransactionId,
	long ParentRequestDispatchMutationTransactionId,
	AutocompleteRequestDispatchOrigin RequestDispatchOrigin,
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
	long OriginatingRequestDispatchMutationTransactionId,
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

internal readonly record struct AutocompleteExternalMutationLease(
	long MutationTransactionId,
	AutocompleteExternalMutationOrigin Origin,
	string OperationName,
	string ManagedAssemblyGeneration,
	long HostInstanceToken
);

internal readonly record struct AutocompleteStableBindingMutationLease(
	long MutationTransactionId,
	string Operation,
	long ParentRequestDispatchMutationTransactionId,
	EditorBindingLease BindingLease,
	ulong CodeEditInstanceId,
	string ScriptPath
);

internal sealed class AutocompleteCodeEditMutationCoordinator
{
	private const string DefaultValueKey = "default_value";

	private readonly string _managedAssemblyGeneration;
	private readonly Func<long> _hostInstanceTokenProvider;
	private readonly Func<long> _currentReloadReadyEpochProvider;
	private readonly Func<bool> _reloadStabilizationReadyProvider;
	private readonly ScriptEditorLifecycleCoordinator _scriptEditorLifecycleCoordinator;
	private readonly AutocompletePrefixExtractor _prefixExtractor;
	private readonly AutocompleteCompletionPublicationEnvelopeCodec _publicationEnvelopeCodec;
	private readonly AutocompleteCodeEditPresenter _presenter;
	private readonly AutocompleteProjectTypeConfirmationService _projectTypeConfirmationService;
	private readonly Func<EditorBindingLease, CodeEdit, string, bool> _isBindingCurrent;
	private readonly Action<string, string> _debugLog;
	private long _nextRequestTransactionId;
	private long _nextPublicationId;
	private long _nextMutationTransactionId;
	private AutocompleteOwnedCompletionPublicationLease? _ownedPublicationLease;
	private AutocompleteBindingActivationTransactionLease? _activeBindingActivation;
	private AutocompleteRequestDispatchTransactionLease? _activeRequestDispatch;
	private AutocompleteRequestDispatchChildLease? _activeRequestDispatchChildLease;
	private AutocompleteStableBindingMutationLease? _activeStableBindingMutation;
	private AutocompleteExternalMutationLease? _activeExternalMutation;

	internal AutocompleteCodeEditMutationCoordinator(
		string managedAssemblyGeneration,
		Func<long> hostInstanceTokenProvider,
		Func<long> currentReloadReadyEpochProvider,
		Func<bool> reloadStabilizationReadyProvider,
		ScriptEditorLifecycleCoordinator scriptEditorLifecycleCoordinator,
		AutocompletePrefixExtractor prefixExtractor,
		AutocompleteCompletionPublicationEnvelopeCodec publicationEnvelopeCodec,
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
		_prefixExtractor =
			prefixExtractor ?? throw new ArgumentNullException(nameof(prefixExtractor));
		_publicationEnvelopeCodec =
			publicationEnvelopeCodec
			?? throw new ArgumentNullException(nameof(publicationEnvelopeCodec));
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
		if (RejectCompletionMutationWhileNativeMutationActive("BindingActivation"))
			return false;
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

	internal bool TryBeginExternalMutation(
		long hostInstanceToken,
		AutocompleteExternalMutationOrigin origin,
		string operationName,
		out AutocompleteExternalMutationLease lease
	)
	{
		lease = default;
		if (!IsValidExternalMutationOrigin(origin))
		{
			LogExternalMutationRejected(origin, operationName, hostInstanceToken, "InvalidOrigin");
			return false;
		}
		if (string.IsNullOrWhiteSpace(operationName))
		{
			LogExternalMutationRejected(origin, operationName, hostInstanceToken, "InvalidOperationName");
			return false;
		}
		if (hostInstanceToken <= 0)
		{
			LogExternalMutationRejected(origin, operationName, hostInstanceToken, "InvalidHostInstanceToken");
			return false;
		}
		if (hostInstanceToken != _hostInstanceTokenProvider())
		{
			LogExternalMutationRejected(origin, operationName, hostInstanceToken, "HostInstanceTokenMismatch");
			return false;
		}
		if (_activeExternalMutation.HasValue)
		{
			LogExternalMutationRejected(origin, operationName, hostInstanceToken, "ExternalMutationActive");
			return false;
		}
		if (_activeBindingActivation.HasValue)
		{
			LogExternalMutationRejected(origin, operationName, hostInstanceToken, "BindingActivationActive");
			return false;
		}
		if (_activeRequestDispatch.HasValue)
		{
			LogExternalMutationRejected(origin, operationName, hostInstanceToken, "RequestDispatchActive");
			return false;
		}
		if (_activeRequestDispatchChildLease.HasValue)
		{
			LogExternalMutationRejected(origin, operationName, hostInstanceToken, "RequestDispatchChildActive");
			return false;
		}
		if (_activeStableBindingMutation.HasValue)
		{
			LogExternalMutationRejected(origin, operationName, hostInstanceToken, "StableBindingMutationActive");
			return false;
		}

		lease = new AutocompleteExternalMutationLease(
			NextPositive(ref _nextMutationTransactionId),
			origin,
			operationName,
			_managedAssemblyGeneration,
			hostInstanceToken
		);
		_activeExternalMutation = lease;
		RetireOwnedPublication("ExternalMutationLeaseAcquired");
		LogExternalMutationBegin(lease);
		return true;
	}

	internal bool OwnsExternalMutation(AutocompleteExternalMutationLease lease)
	{
		return _activeExternalMutation.HasValue
			&& _activeExternalMutation.Value.Equals(lease);
	}

	internal bool IsExternalMutationAuthorityCurrent(AutocompleteExternalMutationLease lease)
	{
		return OwnsExternalMutation(lease)
			&& string.Equals(
				lease.ManagedAssemblyGeneration,
				_managedAssemblyGeneration,
				StringComparison.Ordinal
			)
			&& lease.HostInstanceToken > 0
			&& lease.HostInstanceToken == _hostInstanceTokenProvider();
	}

	internal bool EndExternalMutation(AutocompleteExternalMutationLease lease)
	{
		if (!OwnsExternalMutation(lease))
		{
			LogExternalMutationStaleRelease(lease, "LeaseNotOwned");
			return false;
		}

		bool forwardAuthorityCurrent = IsExternalMutationAuthorityCurrent(lease);
		_activeExternalMutation = null;
		LogExternalMutationReturned(lease, forwardAuthorityCurrent);
		return true;
	}

	internal void RetireOwnedPublication(string reason)
	{
		if (!_ownedPublicationLease.HasValue)
			return;

		AutocompleteOwnedCompletionPublicationLease retired =
			_ownedPublicationLease.Value;
		_ownedPublicationLease = null;
		LogPublicationRetired(retired, reason);
	}

	internal AutocompleteRequestDispatchChildCaptureResult TryCaptureRequestDispatchChild(
		CodeEdit codeEdit,
		string scriptPath,
		EditorBindingLease bindingLease,
		long requestObservationSequence,
		out AutocompleteRequestDispatchChildLease childLease
	)
	{
		childLease = default;
		if (_activeExternalMutation.HasValue)
		{
			Log(
				"C# autocomplete RequestDispatch child rejected",
				$"Reason='ExternalMutationActive', RequestObservationSequence='{requestObservationSequence}', {DescribeExternalMutationLease(_activeExternalMutation.Value)}"
			);
			return AutocompleteRequestDispatchChildCaptureResult.RejectedChild;
		}
		if (!_activeRequestDispatch.HasValue)
			return AutocompleteRequestDispatchChildCaptureResult.NoActiveRequestDispatch;

		AutocompleteRequestDispatchTransactionLease parent = _activeRequestDispatch.Value;
		if (_activeRequestDispatchChildLease.HasValue)
		{
			LogRequestDispatchChildRejected(parent, requestObservationSequence, bindingLease, scriptPath, "ChildAlreadyCaptured");
			return AutocompleteRequestDispatchChildCaptureResult.RejectedChild;
		}

		if (requestObservationSequence <= 0)
		{
			LogRequestDispatchChildRejected(parent, requestObservationSequence, bindingLease, scriptPath, "InvalidRequestObservationSequence");
			return AutocompleteRequestDispatchChildCaptureResult.RejectedChild;
		}

		if (
			!IsRequestDispatchForwardAuthorityCurrent(parent, codeEdit, scriptPath)
			|| !bindingLease.Equals(parent.BindingLease)
			|| bindingLease.CodeEditInstanceId != parent.CodeEditInstanceId
			|| !string.Equals(
				ScriptPathUtility.Normalize(scriptPath),
				parent.ScriptPath,
				StringComparison.OrdinalIgnoreCase
			)
		)
		{
			LogRequestDispatchChildRejected(parent, requestObservationSequence, bindingLease, scriptPath, "ParentChildIdentityMismatch");
			return AutocompleteRequestDispatchChildCaptureResult.RejectedChild;
		}

		childLease = new AutocompleteRequestDispatchChildLease(
			parent.MutationTransactionId,
			parent.Origin,
			parent.BindingLease,
			parent.CodeEditInstanceId,
			parent.ScriptPath,
			requestObservationSequence
		);
		_activeRequestDispatchChildLease = childLease;
		LogRequestDispatchChildAdmitted(childLease);
		return AutocompleteRequestDispatchChildCaptureResult.AuthorizedChild;
	}

	internal bool TryCreateRequestLease(
		CodeEdit codeEdit,
		string scriptPath,
		EditorBindingLease bindingLease,
		long requestObservationSequence,
		AutocompleteRequestDispatchChildLease? requestDispatchChildLease,
		AutocompleteRequestContext request,
		string lineText,
		out AutocompleteCompletionRequestLease requestLease
	)
	{
		requestLease = default;
		if (request == null || requestObservationSequence <= 0)
			return false;
		if (_activeExternalMutation.HasValue)
		{
			RejectCompletionMutationWhileNativeMutationActive("RequestTransactionAdmission");
			return false;
		}

		long parentRequestDispatchMutationTransactionId = 0;
		AutocompleteRequestDispatchOrigin requestDispatchOrigin = AutocompleteRequestDispatchOrigin.None;
		if (requestDispatchChildLease.HasValue)
		{
			if (_activeBindingActivation.HasValue || _activeStableBindingMutation.HasValue)
				return false;

			AutocompleteRequestDispatchChildLease child = requestDispatchChildLease.Value;
			if (
				!IsRequestDispatchChildAuthorityCurrent(
					child,
					codeEdit,
					scriptPath,
					bindingLease,
					requestObservationSequence
				)
			)
			{
				return false;
			}

			parentRequestDispatchMutationTransactionId =
				child.ParentRequestDispatchMutationTransactionId;
			requestDispatchOrigin = child.Origin;
		}
		else if (RejectCompletionMutationWhileNativeMutationActive("RequestTransactionAdmission"))
		{
			return false;
		}

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

		if (
			requestDispatchChildLease.HasValue
			&& !IsRequestDispatchChildAuthorityCurrent(
				requestDispatchChildLease.Value,
				codeEdit,
				scriptPath,
				bindingLease,
				requestObservationSequence
			)
		)
		{
			return false;
		}

		requestLease = new AutocompleteCompletionRequestLease(
			NextPositive(ref _nextRequestTransactionId),
			parentRequestDispatchMutationTransactionId,
			requestDispatchOrigin,
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
		if (RejectCompletionMutationWhileNativeMutationActive("Publish", requestLease))
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
				requestLease,
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

			long candidatePublicationId = NextPositive(ref _nextPublicationId);
			var candidatePublicationLease = new AutocompleteOwnedCompletionPublicationLease(
				candidatePublicationId,
				requestLease.TransactionId,
				requestLease.ParentRequestDispatchMutationTransactionId,
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

			bool preserveSelectedIndex = CanPreserveSelectedIndexAcrossPublish(
				codeEdit,
				requestLease
			);
			bool resetSelectedIndexToFirst = !preserveSelectedIndex;

			// The first Add/Update for P may replace the native completion state. Any
			// previously owned publication must therefore be relinquished before the
			// presenter performs the first native mutation for this candidate. Selection
			// continuity has already been classified while the previous lease is intact.
			RetireOwnedPublication("SupersededByPublishAttempt");

			AutocompleteCompletionDiagnosticContext diagnosticContext =
				AutocompleteCompletionDiagnosticContext.FromRequestLease(
					requestLease,
					mutationLease.MutationTransactionId,
					candidatePublicationId
				);
			_presenter.Publish(
				codeEdit,
				items,
				candidatePublicationId,
				resetSelectedIndexToFirst,
				diagnosticContext
			);

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

			if (
				!TryValidatePublicationOwnershipCandidate(
					candidatePublicationLease,
					codeEdit,
					requestLease.ScriptPath,
					requestLease.BindingLease,
					requireLogicalAnchor: true,
					out rejectionReason
				)
			)
			{
				LogPublicationLeaseRejected(
					requestLease,
					$"PostPublishLivenessValidationFailed:{rejectionReason}"
				);
				return false;
			}

			publicationLease = candidatePublicationLease;
			_ownedPublicationLease = candidatePublicationLease;
			LogPublicationLeaseAcquired(publicationLease);
			return true;
		}
		finally
		{
			EndStableBindingMutation(mutationLease);
		}
	}

	private bool CanPreserveSelectedIndexAcrossPublish(
		CodeEdit codeEdit,
		AutocompleteCompletionRequestLease requestLease
	)
	{
		if (!_ownedPublicationLease.HasValue)
			return false;

		AutocompleteOwnedCompletionPublicationLease previousPublication =
			_ownedPublicationLease.Value;
		if (
			!AutocompleteCompletionAnchorPolicy.BelongsToSameAnchor(
				previousPublication.ScriptPath,
				previousPublication.RequestKind,
				previousPublication.CaretLine,
				previousPublication.PrefixStartColumn,
				requestLease.ScriptPath,
				requestLease.RequestKind,
				requestLease.CaretLine,
				requestLease.PrefixStartColumn
			)
		)
		{
			return false;
		}

		return TryValidatePublicationOwnershipCandidate(
			previousPublication,
			codeEdit,
			requestLease.ScriptPath,
			requestLease.BindingLease,
			requireLogicalAnchor: true,
			out _
		);
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
			!TryGetCurrentOwnedPublicationForCancellation(
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
				!TryGetCurrentOwnedPublicationForCancellation(
					codeEdit,
					scriptPath,
					currentBindingLease,
					out AutocompleteOwnedCompletionPublicationLease revalidatedOwned,
					out _
				)
				|| !revalidatedOwned.Equals(owned)
				|| revalidatedOwned.PublicationId != owned.PublicationId
			)
			{
				return false;
			}

			if (!TryConsumeOwnedPublication(owned, reason))
				return false;

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

	internal bool ObserveOwnedPublicationLiveness(
		CodeEdit codeEdit,
		string scriptPath,
		EditorBindingLease currentBindingLease
	)
	{
		return TryGetCurrentLiveOwnedPublication(
			codeEdit,
			scriptPath,
			currentBindingLease,
			out _,
			out _
		);
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

	private bool TryBeginRequestDispatch(
		CodeEdit codeEdit,
		string scriptPath,
		EditorBindingLease bindingLease,
		bool force,
		AutocompleteRequestDispatchOrigin origin,
		out AutocompleteRequestDispatchTransactionLease lease
	)
	{
		lease = default;
		if (!IsValidRequestDispatchOrigin(origin))
			return false;
		if (RejectCompletionMutationWhileNativeMutationActive("RequestDispatch"))
			return false;
		if (!IsExactStableBindingCurrent(bindingLease, codeEdit, scriptPath))
			return false;

		string normalizedScriptPath = ScriptPathUtility.Normalize(scriptPath);
		lease = new AutocompleteRequestDispatchTransactionLease(
			NextPositive(ref _nextMutationTransactionId),
			origin,
			force,
			bindingLease,
			bindingLease.CodeEditInstanceId,
			normalizedScriptPath
		);
		_activeRequestDispatch = lease;
		_activeRequestDispatchChildLease = null;
		return true;
	}

	private bool OwnsRequestDispatch(AutocompleteRequestDispatchTransactionLease lease)
	{
		return _activeRequestDispatch.HasValue
			&& _activeRequestDispatch.Value.Equals(lease);
	}

	private bool IsRequestDispatchForwardAuthorityCurrent(
		AutocompleteRequestDispatchTransactionLease lease,
		CodeEdit codeEdit,
		string scriptPath
	)
	{
		return OwnsRequestDispatch(lease)
			&& IsValidRequestDispatchOrigin(lease.Origin)
			&& lease.CodeEditInstanceId == lease.BindingLease.CodeEditInstanceId
			&& string.Equals(
				lease.ScriptPath,
				ScriptPathUtility.Normalize(scriptPath),
				StringComparison.OrdinalIgnoreCase
			)
			&& IsExactStableBindingCurrent(lease.BindingLease, codeEdit, scriptPath);
	}

	private bool IsRequestDispatchChildAuthorityCurrent(
		AutocompleteRequestDispatchChildLease childLease,
		CodeEdit codeEdit,
		string scriptPath,
		EditorBindingLease bindingLease,
		long requestObservationSequence
	)
	{
		if (
			!_activeRequestDispatch.HasValue
			|| !_activeRequestDispatchChildLease.HasValue
			|| !_activeRequestDispatchChildLease.Value.Equals(childLease)
			|| childLease.ParentRequestDispatchMutationTransactionId <= 0
			|| requestObservationSequence <= 0
			|| childLease.RequestObservationSequence != requestObservationSequence
			|| !bindingLease.Equals(childLease.BindingLease)
			|| bindingLease.CodeEditInstanceId != childLease.CodeEditInstanceId
			|| !string.Equals(
				ScriptPathUtility.Normalize(scriptPath),
				childLease.ScriptPath,
				StringComparison.OrdinalIgnoreCase
			)
		)
		{
			return false;
		}

		AutocompleteRequestDispatchTransactionLease parent = _activeRequestDispatch.Value;
		return parent.MutationTransactionId
				== childLease.ParentRequestDispatchMutationTransactionId
			&& parent.Origin == childLease.Origin
			&& parent.BindingLease.Equals(childLease.BindingLease)
			&& parent.CodeEditInstanceId == childLease.CodeEditInstanceId
			&& string.Equals(
				parent.ScriptPath,
				childLease.ScriptPath,
				StringComparison.OrdinalIgnoreCase
			)
			&& IsRequestDispatchForwardAuthorityCurrent(parent, codeEdit, scriptPath);
	}

	private bool IsRequestDispatchRequestLeaseLineageCurrent(
		AutocompleteCompletionRequestLease requestLease,
		CodeEdit codeEdit
	)
	{
		if (requestLease.ParentRequestDispatchMutationTransactionId <= 0)
			return !_activeRequestDispatch.HasValue;
		if (!_activeRequestDispatch.HasValue || !_activeRequestDispatchChildLease.HasValue)
			return false;

		AutocompleteRequestDispatchChildLease child = _activeRequestDispatchChildLease.Value;
		return child.ParentRequestDispatchMutationTransactionId
				== requestLease.ParentRequestDispatchMutationTransactionId
			&& child.Origin == requestLease.RequestDispatchOrigin
			&& child.BindingLease.Equals(requestLease.BindingLease)
			&& child.CodeEditInstanceId == requestLease.CodeEditInstanceId
			&& string.Equals(
				child.ScriptPath,
				requestLease.ScriptPath,
				StringComparison.OrdinalIgnoreCase
			)
			&& child.RequestObservationSequence == requestLease.RequestObservationSequence
			&& IsRequestDispatchChildAuthorityCurrent(
				child,
				codeEdit,
				requestLease.ScriptPath,
				requestLease.BindingLease,
				requestLease.RequestObservationSequence
			);
	}

	private bool IsExactNestedChildPublishAuthorityCurrent(
		AutocompleteCompletionRequestLease requestLease,
		EditorBindingLease bindingLease,
		CodeEdit codeEdit,
		string scriptPath
	)
	{
		return requestLease.ParentRequestDispatchMutationTransactionId > 0
			&& requestLease.RequestDispatchOrigin != AutocompleteRequestDispatchOrigin.None
			&& requestLease.BindingLease.Equals(bindingLease)
			&& requestLease.CodeEditInstanceId == bindingLease.CodeEditInstanceId
			&& string.Equals(
				requestLease.ScriptPath,
				ScriptPathUtility.Normalize(scriptPath),
				StringComparison.OrdinalIgnoreCase
			)
			&& IsRequestDispatchRequestLeaseLineageCurrent(requestLease, codeEdit);
	}

	private bool IsCapturedRequestDispatchChildIdentityCurrent(
		AutocompleteRequestDispatchTransactionLease parent
	)
	{
		if (!_activeRequestDispatchChildLease.HasValue)
			return false;

		AutocompleteRequestDispatchChildLease child = _activeRequestDispatchChildLease.Value;
		return child.ParentRequestDispatchMutationTransactionId == parent.MutationTransactionId
			&& child.Origin == parent.Origin
			&& child.BindingLease.Equals(parent.BindingLease)
			&& child.CodeEditInstanceId == parent.CodeEditInstanceId
			&& string.Equals(
				child.ScriptPath,
				parent.ScriptPath,
				StringComparison.OrdinalIgnoreCase
			)
			&& child.RequestObservationSequence > 0;
	}

	private bool HasCapturedRequestDispatchChild(
		AutocompleteRequestDispatchTransactionLease parent
	)
	{
		return OwnsRequestDispatch(parent)
			&& IsCapturedRequestDispatchChildIdentityCurrent(parent);
	}

	private void EndRequestDispatch(AutocompleteRequestDispatchTransactionLease lease)
	{
		if (!OwnsRequestDispatch(lease))
			return;

		_activeRequestDispatchChildLease = null;
		_activeRequestDispatch = null;
	}

	private static bool IsValidRequestDispatchOrigin(AutocompleteRequestDispatchOrigin origin)
	{
		return origin == AutocompleteRequestDispatchOrigin.DormantRecovery
			|| origin == AutocompleteRequestDispatchOrigin.ForcedMemberFollowUp;
	}

	internal bool TryRequestCodeCompletion(
		CodeEdit codeEdit,
		string scriptPath,
		EditorBindingLease bindingLease,
		bool force,
		AutocompleteRequestDispatchOrigin origin,
		string retirementReason
	)
	{
		if (
			!TryBeginRequestDispatch(
				codeEdit,
				scriptPath,
				bindingLease,
				force,
				origin,
				out AutocompleteRequestDispatchTransactionLease dispatchLease
			)
		)
		{
			return false;
		}

		bool dispatchBeginLogged = false;
		bool nativeCallReturned = false;
		try
		{
			if (!IsRequestDispatchForwardAuthorityCurrent(dispatchLease, codeEdit, scriptPath))
				return false;

			RetireOwnedPublication(retirementReason);
			LogRequestDispatchBegin(dispatchLease);
			dispatchBeginLogged = true;

			codeEdit.RequestCodeCompletion(force);
			nativeCallReturned = true;

			bool finalForwardAuthorityCurrent =
				IsRequestDispatchForwardAuthorityCurrent(dispatchLease, codeEdit, scriptPath);
			LogRequestDispatchReturned(
				dispatchLease,
				nativeCallReturned: true,
				finalForwardAuthorityCurrent: finalForwardAuthorityCurrent,
				childCallbackCaptured: HasCapturedRequestDispatchChild(dispatchLease)
			);
			return true;
		}
		finally
		{
			if (dispatchBeginLogged && !nativeCallReturned)
			{
				LogRequestDispatchReturned(
					dispatchLease,
					nativeCallReturned: false,
					finalForwardAuthorityCurrent: false,
					childCallbackCaptured: HasCapturedRequestDispatchChild(dispatchLease)
				);
			}

			EndRequestDispatch(dispatchLease);
		}
	}

	internal bool TryExecuteOwnedConfirmation(
		CodeEdit codeEdit,
		string scriptPath,
		EditorBindingLease currentBindingLease,
		long selectedPublicationId,
		int selectedIndex,
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
		if (
			selectedPublicationId <= 0
			|| selectedIndex < 0
			|| metadata == null
			|| preparation == null
		)
		{
			return false;
		}

		if (
			!TryGetCurrentLiveOwnedPublication(
				codeEdit,
				scriptPath,
				currentBindingLease,
				out AutocompleteOwnedCompletionPublicationLease owned,
				out string rejectionReason
			)
			|| owned.PublicationId != selectedPublicationId
		)
		{
			LogConfirmationLeaseRejected(
				currentBindingLease,
				codeEdit,
				scriptPath,
				owned.PublicationId > 0 && owned.PublicationId != selectedPublicationId
					? "SelectedPublicationIdChanged"
					: rejectionReason
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

			if (
				!TryGetCurrentLiveOwnedPublication(
					codeEdit,
					scriptPath,
					currentBindingLease,
					out AutocompleteOwnedCompletionPublicationLease revalidatedOwned,
					out rejectionReason
				)
				|| !revalidatedOwned.Equals(owned)
				|| revalidatedOwned.PublicationId != selectedPublicationId
			)
			{
				LogConfirmationLeaseRejected(
					currentBindingLease,
					codeEdit,
					scriptPath,
					string.IsNullOrWhiteSpace(rejectionReason)
						? "PublicationChangedAfterMutationAdmission"
						: rejectionReason
				);
				return false;
			}

			if (
				!TryValidateSelectedProjectTypeOption(
					codeEdit,
					selectedIndex,
					selectedPublicationId,
					metadata,
					out rejectionReason
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

			if (!TryConsumeOwnedPublication(owned, "Confirmation"))
			{
				LogConfirmationLeaseRejected(
					currentBindingLease,
					codeEdit,
					scriptPath,
					"PublicationChangedBeforeConsume"
				);
				return false;
			}

			consumedPublication = owned;

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
		return TryBeginStableBindingMutation(
			operation,
			bindingLease,
			codeEdit,
			scriptPath,
			requestLease: null,
			out lease
		);
	}

	private bool TryBeginStableBindingMutation(
		string operation,
		EditorBindingLease bindingLease,
		CodeEdit codeEdit,
		string scriptPath,
		AutocompleteCompletionRequestLease requestLease,
		out AutocompleteStableBindingMutationLease lease
	)
	{
		return TryBeginStableBindingMutation(
			operation,
			bindingLease,
			codeEdit,
			scriptPath,
			(AutocompleteCompletionRequestLease?)requestLease,
			out lease
		);
	}

	private bool TryBeginStableBindingMutation(
		string operation,
		EditorBindingLease bindingLease,
		CodeEdit codeEdit,
		string scriptPath,
		AutocompleteCompletionRequestLease? requestLease,
		out AutocompleteStableBindingMutationLease lease
	)
	{
		lease = default;
		if (_activeExternalMutation.HasValue)
		{
			RejectCompletionMutationWhileNativeMutationActive(operation);
			return false;
		}
		if (_activeBindingActivation.HasValue || _activeStableBindingMutation.HasValue)
			return false;
		if (string.IsNullOrWhiteSpace(operation))
			return false;

		long parentRequestDispatchMutationTransactionId = 0;
		if (_activeRequestDispatch.HasValue)
		{
			if (
				!string.Equals(operation, "Publish", StringComparison.Ordinal)
				|| !requestLease.HasValue
				|| !IsExactNestedChildPublishAuthorityCurrent(
					requestLease.Value,
					bindingLease,
					codeEdit,
					scriptPath
				)
			)
			{
				return false;
			}

			parentRequestDispatchMutationTransactionId =
				requestLease.Value.ParentRequestDispatchMutationTransactionId;
		}
		else if (
			requestLease.HasValue
			&& requestLease.Value.ParentRequestDispatchMutationTransactionId > 0
		)
		{
			return false;
		}

		if (!IsExactStableBindingCurrent(bindingLease, codeEdit, scriptPath))
			return false;

		string normalizedScriptPath = ScriptPathUtility.Normalize(scriptPath);
		lease = new AutocompleteStableBindingMutationLease(
			NextPositive(ref _nextMutationTransactionId),
			operation,
			parentRequestDispatchMutationTransactionId,
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
		if (
			!OwnsStableBindingMutation(lease)
			|| lease.CodeEditInstanceId != lease.BindingLease.CodeEditInstanceId
			|| !string.Equals(
				lease.ScriptPath,
				ScriptPathUtility.Normalize(scriptPath),
				StringComparison.OrdinalIgnoreCase
			)
			|| !IsExactStableBindingCurrent(lease.BindingLease, codeEdit, scriptPath)
		)
		{
			return false;
		}

		if (lease.ParentRequestDispatchMutationTransactionId <= 0)
			return !_activeRequestDispatch.HasValue;

		if (
			!string.Equals(lease.Operation, "Publish", StringComparison.Ordinal)
			|| !_activeRequestDispatch.HasValue
			|| _activeRequestDispatch.Value.MutationTransactionId
				!= lease.ParentRequestDispatchMutationTransactionId
		)
		{
			return false;
		}

		AutocompleteRequestDispatchTransactionLease parent = _activeRequestDispatch.Value;
		return parent.BindingLease.Equals(lease.BindingLease)
			&& parent.CodeEditInstanceId == lease.CodeEditInstanceId
			&& string.Equals(
				parent.ScriptPath,
				lease.ScriptPath,
				StringComparison.OrdinalIgnoreCase
			)
			&& IsRequestDispatchForwardAuthorityCurrent(parent, codeEdit, scriptPath)
			&& IsCapturedRequestDispatchChildIdentityCurrent(parent);
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
		return RejectCompletionMutationWhileNativeMutationActive(
			operation,
			requestLease: null
		);
	}

	private bool RejectCompletionMutationWhileNativeMutationActive(
		string operation,
		AutocompleteCompletionRequestLease requestLease
	)
	{
		return RejectCompletionMutationWhileNativeMutationActive(
			operation,
			(AutocompleteCompletionRequestLease?)requestLease
		);
	}

	private bool RejectCompletionMutationWhileNativeMutationActive(
		string operation,
		AutocompleteCompletionRequestLease? requestLease
	)
	{
		if (_activeExternalMutation.HasValue)
		{
			AutocompleteExternalMutationLease externalMutation =
				_activeExternalMutation.Value;
			Log(
				"C# autocomplete completion mutation rejected",
				$"Reason='ExternalMutationActive', Operation='{operation ?? ""}', {DescribeExternalMutationLease(externalMutation)}"
			);
			return true;
		}

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
				$"Reason='StableBindingMutationActive', Operation='{operation ?? ""}', ActiveOperation='{mutation.Operation ?? ""}', MutationTransactionId='{mutation.MutationTransactionId}', ParentRequestDispatchMutationTransactionId='{mutation.ParentRequestDispatchMutationTransactionId}', ScriptTransitionId='{mutation.BindingLease.ScriptTransitionId}', BindingEpoch='{mutation.BindingLease.BindingEpoch}', ReloadReadyEpoch='{mutation.BindingLease.ReloadReadyEpoch}', HostInstanceToken='{mutation.BindingLease.HostInstanceToken}', CodeEditInstanceId='{mutation.CodeEditInstanceId}', ScriptPath='{mutation.ScriptPath ?? ""}'"
			);
			return true;
		}

		if (_activeRequestDispatch.HasValue)
		{
			AutocompleteRequestDispatchTransactionLease dispatch = _activeRequestDispatch.Value;
			if (
				string.Equals(operation, "Publish", StringComparison.Ordinal)
				&& requestLease.HasValue
				&& requestLease.Value.ParentRequestDispatchMutationTransactionId
					== dispatch.MutationTransactionId
				&& IsCapturedRequestDispatchChildIdentityCurrent(dispatch)
			)
			{
				return false;
			}

			Log(
				"C# autocomplete completion mutation rejected",
				$"Reason='RequestDispatchActive', Operation='{operation ?? ""}', MutationTransactionId='{dispatch.MutationTransactionId}', RequestDispatchOrigin='{dispatch.Origin}', Force='{dispatch.Force}', ScriptTransitionId='{dispatch.BindingLease.ScriptTransitionId}', BindingEpoch='{dispatch.BindingLease.BindingEpoch}', ReloadReadyEpoch='{dispatch.BindingLease.ReloadReadyEpoch}', HostInstanceToken='{dispatch.BindingLease.HostInstanceToken}', CodeEditInstanceId='{dispatch.CodeEditInstanceId}', ScriptPath='{dispatch.ScriptPath ?? ""}'"
			);
			return true;
		}

		return false;
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
		rejectionReason = "";
		if (!IsRequestDispatchRequestLeaseLineageCurrent(requestLease, codeEdit))
		{
			rejectionReason = requestLease.ParentRequestDispatchMutationTransactionId > 0
				? "RequestDispatchChildAuthorityChanged"
				: "UnexpectedActiveRequestDispatch";
			return false;
		}

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

	private bool TryGetCurrentOwnedPublicationForCancellation(
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

		// Cancellation is commonly requested because the logical completion anchor
		// has just been invalidated (for example by typing a space/delimiter). The
		// cancel authority therefore rests on exact binding + native PublicationId
		// provenance, not on the now-invalid session anchor.
		if (
			!TryValidatePublicationOwnershipCandidate(
				candidate,
				codeEdit,
				scriptPath,
				currentBindingLease,
				requireLogicalAnchor: false,
				out rejectionReason
			)
		)
		{
			RetireOwnedPublicationIfMatches(
				candidate,
				$"CancellationOwnershipLost:{rejectionReason}"
			);
			return false;
		}

		owned = candidate;
		return true;
	}

	private bool TryGetCurrentLiveOwnedPublication(
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
		if (
			!TryValidatePublicationOwnershipCandidate(
				candidate,
				codeEdit,
				scriptPath,
				currentBindingLease,
				requireLogicalAnchor: true,
				out rejectionReason
			)
		)
		{
			RetireOwnedPublicationIfMatches(
				candidate,
				$"LivenessLost:{rejectionReason}"
			);
			return false;
		}

		owned = candidate;
		return true;
	}

	private bool TryValidatePublicationOwnershipCandidate(
		AutocompleteOwnedCompletionPublicationLease candidate,
		CodeEdit codeEdit,
		string scriptPath,
		EditorBindingLease currentBindingLease,
		bool requireLogicalAnchor,
		out string rejectionReason
	)
	{
		rejectionReason = "";
		if (candidate.PublicationId <= 0)
		{
			rejectionReason = "PublicationIdInvalid";
			return false;
		}
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
			if (codeEdit == null || !GodotObject.IsInstanceValid(codeEdit))
			{
				rejectionReason = "CodeEditUnavailable";
				return false;
			}
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

		if (requireLogicalAnchor)
		{
			try
			{
				if (
					!_prefixExtractor.TryExtract(
						codeEdit,
						out _,
						out int currentCaretLine,
						out _,
						out AutocompleteRequestKind currentRequestKind,
						out int currentPrefixStartColumn
					)
				)
				{
					rejectionReason = "LogicalAnchorChanged";
					return false;
				}

				if (
					!AutocompleteCompletionAnchorPolicy.BelongsToSameAnchor(
						candidate.ScriptPath,
						candidate.RequestKind,
						candidate.CaretLine,
						candidate.PrefixStartColumn,
						normalizedScriptPath,
						currentRequestKind,
						currentCaretLine,
						currentPrefixStartColumn
					)
				)
				{
					rejectionReason = "LogicalAnchorChanged";
					return false;
				}
			}
			catch
			{
				rejectionReason = "LogicalAnchorReadFailed";
				return false;
			}
		}

		try
		{
			var options = codeEdit.GetCodeCompletionOptions();
			if (options == null || options.Count <= 0)
			{
				rejectionReason = "NativeCompletionInactiveOrEmpty";
				return false;
			}

			int selectedIndex = codeEdit.GetCodeCompletionSelectedIndex();
			if (selectedIndex < 0 || selectedIndex >= options.Count)
			{
				rejectionReason = "NativeSelectedIndexInvalid";
				return false;
			}

			for (int index = 0; index < options.Count; index++)
			{
				Godot.Collections.Dictionary option = options[index];
				if (
					option == null
					|| !option.TryGetValue(DefaultValueKey, out Variant defaultValue)
				)
				{
					rejectionReason = "NativeOptionMissingDefaultValue";
					return false;
				}

				if (
					!_publicationEnvelopeCodec.TryDecodePublicationId(
						defaultValue,
						out long nativePublicationId
					)
				)
				{
					rejectionReason = "NativePublicationEnvelopeInvalid";
					return false;
				}

				if (nativePublicationId != candidate.PublicationId)
				{
					rejectionReason = "NativePublicationIdChanged";
					return false;
				}
			}
		}
		catch
		{
			rejectionReason = "NativePublicationReadFailed";
			return false;
		}

		return true;
	}

	private bool TryValidateSelectedProjectTypeOption(
		CodeEdit codeEdit,
		int expectedSelectedIndex,
		long expectedPublicationId,
		AutocompleteCompletionOptionMetadata expectedMetadata,
		out string rejectionReason
	)
	{
		rejectionReason = "";
		try
		{
			int currentSelectedIndex = codeEdit.GetCodeCompletionSelectedIndex();
			if (currentSelectedIndex != expectedSelectedIndex)
			{
				rejectionReason = "NativeSelectedIndexChanged";
				return false;
			}

			var options = codeEdit.GetCodeCompletionOptions();
			if (
				options == null
				|| expectedSelectedIndex < 0
				|| expectedSelectedIndex >= options.Count
			)
			{
				rejectionReason = "NativeSelectedOptionUnavailable";
				return false;
			}

			Godot.Collections.Dictionary selectedOption = options[expectedSelectedIndex];
			if (
				selectedOption == null
				|| !selectedOption.TryGetValue(DefaultValueKey, out Variant defaultValue)
				|| !_publicationEnvelopeCodec.TryDecodeWithItemMetadata(
					defaultValue,
					out AutocompleteCompletionPublicationEnvelope envelope
				)
			)
			{
				rejectionReason = "SelectedPublicationEnvelopeInvalid";
				return false;
			}

			if (envelope.PublicationId != expectedPublicationId)
			{
				rejectionReason = "SelectedPublicationIdChanged";
				return false;
			}

			AutocompleteCompletionOptionMetadata currentMetadata = envelope.ItemMetadata;
			if (
				currentMetadata == null
				|| !string.Equals(
					currentMetadata.Owner,
					AutocompleteCompletionOptionMetadata.SystemExplorerOwner,
					StringComparison.Ordinal
				)
				|| !string.Equals(
					currentMetadata.Source,
					AutocompleteCompletionOptionMetadata.ProjectTypeSource,
					StringComparison.Ordinal
				)
				|| !currentMetadata.Equals(expectedMetadata)
			)
			{
				rejectionReason = "SelectedProjectTypeMetadataChanged";
				return false;
			}
		}
		catch
		{
			rejectionReason = "SelectedNativeOptionReadFailed";
			return false;
		}

		return true;
	}

	private bool TryConsumeOwnedPublication(
		AutocompleteOwnedCompletionPublicationLease expected,
		string reason
	)
	{
		if (
			!_ownedPublicationLease.HasValue
			|| !_ownedPublicationLease.Value.Equals(expected)
		)
		{
			return false;
		}

		_ownedPublicationLease = null;
		LogPublicationConsumed(expected, reason);
		return true;
	}

	private void RetireOwnedPublicationIfMatches(
		AutocompleteOwnedCompletionPublicationLease expected,
		string reason
	)
	{
		if (
			!_ownedPublicationLease.HasValue
			|| !_ownedPublicationLease.Value.Equals(expected)
		)
		{
			return;
		}

		_ownedPublicationLease = null;
		LogPublicationRetired(expected, reason);
	}

	private void LogRequestTransactionAdmitted(AutocompleteCompletionRequestLease lease)
	{
		Log(
			"C# autocomplete completion request transaction admitted",
			DescribeRequestLease(lease)
		);
	}

	private static bool IsValidExternalMutationOrigin(
		AutocompleteExternalMutationOrigin origin
	)
	{
		return origin == AutocompleteExternalMutationOrigin.NamespaceRefactor
			|| origin == AutocompleteExternalMutationOrigin.CreateScript
			|| origin == AutocompleteExternalMutationOrigin.SceneOpen;
	}

	private void LogExternalMutationBegin(AutocompleteExternalMutationLease lease)
	{
		Log(
			"C# autocomplete external mutation lease begin",
			DescribeExternalMutationLease(lease)
		);
	}

	private void LogExternalMutationReturned(
		AutocompleteExternalMutationLease lease,
		bool forwardAuthorityCurrentAtRelease
	)
	{
		Log(
			"C# autocomplete external mutation lease returned",
			$"{DescribeExternalMutationLease(lease)}, ForwardAuthorityCurrentAtRelease='{forwardAuthorityCurrentAtRelease}'"
		);
	}

	private void LogExternalMutationRejected(
		AutocompleteExternalMutationOrigin origin,
		string operationName,
		long hostInstanceToken,
		string reason
	)
	{
		Log(
			"C# autocomplete external mutation lease rejected",
			$"Reason='{reason ?? ""}', Origin='{origin}', OperationName='{operationName ?? ""}', ManagedAssemblyGeneration='{_managedAssemblyGeneration}', ScheduledHostInstanceToken='{hostInstanceToken}', CurrentHostInstanceToken='{_hostInstanceTokenProvider()}'"
		);
	}

	private void LogExternalMutationStaleRelease(
		AutocompleteExternalMutationLease lease,
		string reason
	)
	{
		Log(
			"C# autocomplete external mutation lease stale release rejected",
			$"Reason='{reason ?? ""}', {DescribeExternalMutationLease(lease)}, CurrentHostInstanceToken='{_hostInstanceTokenProvider()}'"
		);
	}

	private static string DescribeExternalMutationLease(
		AutocompleteExternalMutationLease lease
	)
	{
		return $"MutationTransactionId='{lease.MutationTransactionId}', Origin='{lease.Origin}', OperationName='{lease.OperationName ?? ""}', ManagedAssemblyGeneration='{lease.ManagedAssemblyGeneration ?? ""}', HostInstanceToken='{lease.HostInstanceToken}'";
	}

	private void LogRequestDispatchBegin(
		AutocompleteRequestDispatchTransactionLease lease
	)
	{
		Log(
			"C# autocomplete RequestCodeCompletion dispatch begin",
			DescribeRequestDispatchLease(lease)
		);
	}

	private void LogRequestDispatchReturned(
		AutocompleteRequestDispatchTransactionLease lease,
		bool nativeCallReturned,
		bool finalForwardAuthorityCurrent,
		bool childCallbackCaptured
	)
	{
		Log(
			"C# autocomplete RequestCodeCompletion dispatch returned",
			$"{DescribeRequestDispatchLease(lease)}, NativeCallReturned='{nativeCallReturned}', FinalForwardAuthorityCurrent='{finalForwardAuthorityCurrent}', ChildCallbackCaptured='{childCallbackCaptured}'"
		);
	}

	private void LogRequestDispatchChildAdmitted(
		AutocompleteRequestDispatchChildLease childLease
	)
	{
		Log(
			"C# autocomplete RequestDispatch child admitted",
			$"ParentRequestDispatchMutationTransactionId='{childLease.ParentRequestDispatchMutationTransactionId}', RequestDispatchOrigin='{childLease.Origin}', RequestObservationSequence='{childLease.RequestObservationSequence}', ScriptTransitionId='{childLease.BindingLease.ScriptTransitionId}', BindingEpoch='{childLease.BindingLease.BindingEpoch}', ReloadReadyEpoch='{childLease.BindingLease.ReloadReadyEpoch}', HostInstanceToken='{childLease.BindingLease.HostInstanceToken}', CodeEditInstanceId='{childLease.CodeEditInstanceId}', ScriptPath='{childLease.ScriptPath ?? ""}'"
		);
	}

	private void LogRequestDispatchChildRejected(
		AutocompleteRequestDispatchTransactionLease parent,
		long requestObservationSequence,
		EditorBindingLease observedBindingLease,
		string observedScriptPath,
		string reason
	)
	{
		Log(
			"C# autocomplete RequestDispatch child rejected",
			$"Reason='{reason ?? ""}', ParentRequestDispatchMutationTransactionId='{parent.MutationTransactionId}', RequestDispatchOrigin='{parent.Origin}', RequestObservationSequence='{requestObservationSequence}', ParentBindingEpoch='{parent.BindingLease.BindingEpoch}', ObservedBindingEpoch='{observedBindingLease.BindingEpoch}', ParentCodeEditInstanceId='{parent.CodeEditInstanceId}', ObservedCodeEditInstanceId='{observedBindingLease.CodeEditInstanceId}', ParentScriptPath='{parent.ScriptPath ?? ""}', ObservedScriptPath='{ScriptPathUtility.Normalize(observedScriptPath)}'"
		);
	}

	private static string DescribeRequestDispatchLease(
		AutocompleteRequestDispatchTransactionLease lease
	)
	{
		EditorBindingLease bindingLease = lease.BindingLease;
		return $"MutationTransactionId='{lease.MutationTransactionId}', RequestDispatchOrigin='{lease.Origin}', Force='{lease.Force}', ManagedAssemblyGeneration='{bindingLease.ManagedAssemblyGeneration ?? ""}', ScriptTransitionId='{bindingLease.ScriptTransitionId}', BindingEpoch='{bindingLease.BindingEpoch}', ReloadReadyEpoch='{bindingLease.ReloadReadyEpoch}', HostInstanceToken='{bindingLease.HostInstanceToken}', CodeEditInstanceId='{lease.CodeEditInstanceId}', ScriptPath='{lease.ScriptPath ?? ""}'";
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

	private void LogPublicationRetired(
		AutocompleteOwnedCompletionPublicationLease lease,
		string reason
	)
	{
		Log(
			"C# autocomplete completion publication lease retired",
			$"{DescribePublicationLease(lease)}, Reason='{reason ?? ""}'"
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
		return $"RequestTransactionId='{lease.TransactionId}', ParentRequestDispatchMutationTransactionId='{lease.ParentRequestDispatchMutationTransactionId}', RequestDispatchOrigin='{lease.RequestDispatchOrigin}', RequestObservationSequence='{lease.RequestObservationSequence}', ScriptTransitionId='{lease.BindingLease.ScriptTransitionId}', BindingEpoch='{lease.BindingLease.BindingEpoch}', ReloadReadyEpoch='{lease.BindingLease.ReloadReadyEpoch}', HostInstanceToken='{lease.BindingLease.HostInstanceToken}', CodeEditInstanceId='{lease.CodeEditInstanceId}', ScriptPath='{lease.ScriptPath ?? ""}', RequestKind='{lease.RequestKind}', CaretLine='{lease.CaretLine}', CaretColumn='{lease.CaretColumn}', PrefixStartColumn='{lease.PrefixStartColumn}'";
	}

	private static string DescribePublicationLease(
		AutocompleteOwnedCompletionPublicationLease lease
	)
	{
		return $"PublicationId='{lease.PublicationId}', RequestTransactionId='{lease.RequestTransactionId}', OriginatingRequestDispatchMutationTransactionId='{lease.OriginatingRequestDispatchMutationTransactionId}', RequestObservationSequence='{lease.RequestObservationSequence}', ScriptTransitionId='{lease.BindingLease.ScriptTransitionId}', BindingEpoch='{lease.BindingLease.BindingEpoch}', ReloadReadyEpoch='{lease.BindingLease.ReloadReadyEpoch}', HostInstanceToken='{lease.BindingLease.HostInstanceToken}', CodeEditInstanceId='{lease.CodeEditInstanceId}', ScriptPath='{lease.ScriptPath ?? ""}'";
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
