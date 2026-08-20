#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.Autocomplete;

internal sealed class AutocompleteCompletionCoordinator
{
	private readonly AutocompletePrefixExtractor _prefixExtractor;
	private readonly AutocompleteCodeEditMutationCoordinator _codeEditMutationCoordinator;
	private readonly AutocompleteCompletionMatchPolicy _matchPolicy;
	private readonly IReadOnlyList<AutocompleteCompletionSourceRegistration> _completionSourceRegistry;
	private readonly Action<string, string> _debugLog;
	private AutocompleteCompletionSession _session;
	private long _validationGeneration;
	private bool _isIssuingDormantRecoveryRequest;


	internal AutocompleteCompletionCoordinator(
		AutocompletePrefixExtractor prefixExtractor,
		AutocompleteCodeEditMutationCoordinator codeEditMutationCoordinator,
		AutocompleteCompletionMatchPolicy matchPolicy,
		IReadOnlyList<AutocompleteCompletionSourceRegistration> completionSources,
		Action<string, string> debugLog
	)
	{
		_prefixExtractor =
			prefixExtractor ?? throw new ArgumentNullException(nameof(prefixExtractor));
		_codeEditMutationCoordinator =
			codeEditMutationCoordinator
			?? throw new ArgumentNullException(nameof(codeEditMutationCoordinator));
		_matchPolicy = matchPolicy ?? throw new ArgumentNullException(nameof(matchPolicy));
		if (completionSources == null)
			throw new ArgumentNullException(nameof(completionSources));

		var completionSourceRegistry = new AutocompleteCompletionSourceRegistration[
			completionSources.Count
		];
		for (int index = 0; index < completionSources.Count; index++)
		{
			AutocompleteCompletionSourceRegistration registration = completionSources[index];
			if (registration == null)
			{
				throw new ArgumentException(
					$"Completion source registration at index {index} is null.",
					nameof(completionSources)
				);
			}
			if (string.IsNullOrWhiteSpace(registration.SourceId))
			{
				throw new ArgumentException(
					$"Completion source registration at index {index} has no SourceId.",
					nameof(completionSources)
				);
			}
			if (registration.Source == null)
			{
				throw new ArgumentException(
					$"Completion source registration '{registration.SourceId}' has no source.",
					nameof(completionSources)
				);
			}

			completionSourceRegistry[index] = registration;
		}

		_completionSourceRegistry = Array.AsReadOnly(completionSourceRegistry);
		_debugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
	}

	internal bool HandleCompletionRequested(
		CodeEdit codeEdit,
		string scriptPath,
		EditorBindingLease requestBindingLease,
		long requestObservationSequence,
		AutocompleteRequestDispatchChildLease? requestDispatchChildLease
	)
	{
		if (
			!_prefixExtractor.TryExtract(
				codeEdit,
				out string prefix,
				out int caretLine,
				out int caretColumn,
				out AutocompleteRequestKind kind,
				out int prefixStartColumn,
				out string lineText
			)
		)
		{
			_session = null;
			return false;
		}

		var request = new AutocompleteRequestContext(
			scriptPath ?? "",
			prefix,
			caretLine,
			caretColumn,
			kind,
			prefixStartColumn
		);

		if (
			!_codeEditMutationCoordinator.TryCreateRequestLease(
				codeEdit,
				scriptPath,
				requestBindingLease,
				requestObservationSequence,
				requestDispatchChildLease,
				request,
				lineText,
				out AutocompleteCompletionRequestLease requestLease
			)
		)
		{
			_session = null;
			return false;
		}

		AutocompleteCompletionDiagnosticContext diagnosticContext =
			AutocompleteCompletionDiagnosticContext.FromRequestLease(requestLease);

		if (
			_session != null
			&& _session.IsCompleteMemberAccessSession
			&& _session.BelongsToSameAnchor(request)
		)
		{
			if (_session.HasAvailableMatch(request))
			{
				if (
					_codeEditMutationCoordinator.TryPublish(
						codeEdit,
						requestLease,
						_session.PublishedItems,
						out _
					)
				)
				{
					_session.MarkActive();
					return true;
				}

				_session = null;
				return false;
			}

			_session.MarkDormant();
			return false;
		}

		_session = null;
		var completionItems = new List<AutocompleteCompletionItem>();
		LogCompletionCollectionBoundary(
			"C# autocomplete completion collection begin",
			diagnosticContext,
			request
		);

		foreach (AutocompleteCompletionSourceRegistration registration in _completionSourceRegistry)
		{
			try
			{
				if (registration == null)
					throw new InvalidOperationException("Completion source registration is null.");
				if (string.IsNullOrWhiteSpace(registration.SourceId))
					throw new InvalidOperationException("Completion source registration has no SourceId.");

				IAutocompleteCompletionSource completionSource =
					registration.Source
					?? throw new InvalidOperationException(
						$"Completion source '{registration.SourceId}' is unavailable."
					);
				IReadOnlyList<AutocompleteCompletionItem> sourceItems =
					completionSource.GetCompletions(request)
					?? throw new InvalidOperationException(
						$"Completion source '{registration.SourceId}' returned a null collection."
					);

				var copiedSourceItems = new List<AutocompleteCompletionItem>();
				foreach (AutocompleteCompletionItem item in sourceItems)
				{
					if (item != null)
						copiedSourceItems.Add(item);
				}

				completionItems.AddRange(copiedSourceItems);
			}
			catch (Exception exception)
			{
				LogCompletionSourceFailure(
					registration?.SourceId ?? "<invalid>",
					diagnosticContext,
					request,
					exception
				);
			}
		}

		LogCompletionCollectionBoundary(
			"C# autocomplete completion collection returned",
			diagnosticContext,
			request,
			completionItems.Count
		);

		if (!_matchPolicy.CanRemainAvailable(completionItems, prefix))
			return false;

		if (
			!_codeEditMutationCoordinator.TryPublish(
				codeEdit,
				requestLease,
				completionItems,
				out _
			)
		)
		{
			return false;
		}

		_session = new AutocompleteCompletionSession(
			completionItems,
			request,
			_matchPolicy
		);
		return true;
	}

	internal long BeginTextChangedValidation()
	{
		return ++_validationGeneration;
	}

	internal bool IsValidationCurrent(long generation)
	{
		return generation == _validationGeneration;
	}

	internal void ValidateAfterTextChanged(
		CodeEdit codeEdit,
		string scriptPath,
		EditorBindingLease bindingLease,
		long generation
	)
	{
		if (!IsValidationCurrent(generation))
			return;

		AutocompleteCompletionSession session = _session;
		if (
			session == null
			|| !_prefixExtractor.TryExtract(
				codeEdit,
				out string prefix,
				out int caretLine,
				out int caretColumn,
				out AutocompleteRequestKind kind,
				out int prefixStartColumn
			)
		)
		{
			InvalidateManagedValidationState();
			_codeEditMutationCoordinator.TryCancelOwnedPublication(
				codeEdit,
				scriptPath,
				bindingLease,
				"TextChangedInvalidation"
			);
			return;
		}

		var request = new AutocompleteRequestContext(
			scriptPath ?? "",
			prefix,
			caretLine,
			caretColumn,
			kind,
			prefixStartColumn
		);

		if (
			session.IsCompleteMemberAccessSession
			&& session.BelongsToSameAnchor(request)
		)
		{
			if (!session.HasAvailableMatch(request))
			{
				if (session.MarkDormant())
				{
					_codeEditMutationCoordinator.TryCancelOwnedPublication(
						codeEdit,
						scriptPath,
						bindingLease,
						"CompleteMemberSessionBecameDormant"
					);
				}
				return;
			}

			if (!session.IsDormant)
			{
				_codeEditMutationCoordinator.ObserveOwnedPublicationLiveness(
					codeEdit,
					scriptPath,
					bindingLease
				);
				return;
			}

			if (
				!_isIssuingDormantRecoveryRequest
				&& session.TryBeginDormantRecoveryRequest()
			)
			{
				RequestDormantCompletionRecovery(codeEdit, scriptPath, bindingLease);
			}
			return;
		}

		if (session.CanRemainOpen(request))
		{
			_codeEditMutationCoordinator.ObserveOwnedPublicationLiveness(
				codeEdit,
				scriptPath,
				bindingLease
			);
			return;
		}

		InvalidateManagedValidationState();
		_codeEditMutationCoordinator.TryCancelOwnedPublication(
			codeEdit,
			scriptPath,
			bindingLease,
			"OrdinarySessionCannotRemainOpen"
		);
	}

	internal void InvalidatePendingValidations()
	{
		InvalidatePendingValidations("ManagedInvalidation");
	}

	internal void InvalidatePendingValidations(string retirementReason)
	{
		InvalidateManagedValidationState();
		_codeEditMutationCoordinator.RetireOwnedPublication(retirementReason);
	}

	internal void Reset()
	{
		InvalidateManagedValidationState();
		_codeEditMutationCoordinator.RetireOwnedPublication("Reset");
	}

	private void InvalidateManagedValidationState()
	{
		_validationGeneration++;
		_session = null;
	}

	private void RequestDormantCompletionRecovery(
		CodeEdit codeEdit,
		string scriptPath,
		EditorBindingLease bindingLease
	)
	{
		if (_isIssuingDormantRecoveryRequest)
			return;

		_isIssuingDormantRecoveryRequest = true;

		try
		{
			_codeEditMutationCoordinator.TryRequestCodeCompletion(
				codeEdit,
				scriptPath,
				bindingLease,
				force: false,
				origin: AutocompleteRequestDispatchOrigin.DormantRecovery,
				retirementReason: "DormantRecoveryRequest"
			);
		}
		catch (Exception exception)
		{
			LogDormantRecoveryRequestFailure(exception);
		}
		finally
		{
			_isIssuingDormantRecoveryRequest = false;
		}
	}

	private void LogCompletionCollectionBoundary(
		string operation,
		AutocompleteCompletionDiagnosticContext diagnosticContext,
		AutocompleteRequestContext request,
		int? collectedItemCount = null
	)
	{
		try
		{
			string details =
				$"RequestTransactionId='{diagnosticContext.RequestTransactionId}', "
				+ $"ParentRequestDispatchMutationTransactionId='{diagnosticContext.ParentRequestDispatchMutationTransactionId}', "
				+ $"RequestObservationSequence='{diagnosticContext.RequestObservationSequence}', "
				+ $"ScriptTransitionId='{diagnosticContext.ScriptTransitionId}', "
				+ $"BindingEpoch='{diagnosticContext.BindingEpoch}', "
				+ $"ReloadReadyEpoch='{diagnosticContext.ReloadReadyEpoch}', "
				+ $"CodeEditInstanceId='{diagnosticContext.CodeEditInstanceId}', "
				+ $"ScriptPath='{request.ScriptPath ?? diagnosticContext.ScriptPath ?? ""}', "
				+ $"RequestKind='{request.Kind}', "
				+ $"CaretLine='{request.CaretLine}', "
				+ $"CaretColumn='{request.CaretColumn}', "
				+ $"PrefixStartColumn='{request.PrefixStartColumn}', "
				+ $"PrefixLength='{request.Prefix?.Length ?? 0}'";

			if (collectedItemCount.HasValue)
				details += $", CollectedItemCount='{collectedItemCount.Value}'";

			_debugLog(operation ?? "", details);
		}
		catch
		{
			// Collection diagnostics must never affect completion source isolation or publication.
		}
	}

	private void LogCompletionSourceFailure(
		string sourceId,
		AutocompleteCompletionDiagnosticContext diagnosticContext,
		AutocompleteRequestContext request,
		Exception exception
	)
	{
		try
		{
			_debugLog(
				"C# autocomplete completion source failed",
				$"SourceId='{sourceId ?? ""}', "
					+ $"RequestTransactionId='{diagnosticContext.RequestTransactionId}', "
					+ $"ParentRequestDispatchMutationTransactionId='{diagnosticContext.ParentRequestDispatchMutationTransactionId}', "
					+ $"RequestObservationSequence='{diagnosticContext.RequestObservationSequence}', "
					+ $"ScriptTransitionId='{diagnosticContext.ScriptTransitionId}', "
					+ $"BindingEpoch='{diagnosticContext.BindingEpoch}', "
					+ $"ReloadReadyEpoch='{diagnosticContext.ReloadReadyEpoch}', "
					+ $"CodeEditInstanceId='{diagnosticContext.CodeEditInstanceId}', "
					+ $"ScriptPath='{request?.ScriptPath ?? diagnosticContext.ScriptPath ?? ""}', "
					+ $"ExceptionType='{exception?.GetType().FullName ?? ""}', "
					+ $"Exception='{exception}'"
			);
		}
		catch
		{
			// Debug logging must never turn an isolated source failure into a callback failure.
		}
	}

	private void LogDormantRecoveryRequestFailure(Exception exception)
	{
		try
		{
			_debugLog(
				"C# autocomplete dormant member recovery request failed",
				exception?.ToString() ?? ""
			);
		}
		catch
		{
			// Debug logging must never turn a native request failure into a callback failure.
		}
	}
}
#endif
