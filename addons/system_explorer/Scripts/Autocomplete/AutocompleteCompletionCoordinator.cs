#if TOOLS
using Godot;
using System;
using System.Collections.Generic;

namespace SystemExplorer.Autocomplete;

internal sealed class AutocompleteCompletionCoordinator
{
	private readonly AutocompletePrefixExtractor _prefixExtractor;
	private readonly AutocompleteCodeEditPresenter _presenter;
	private readonly AutocompleteCompletionMatchPolicy _matchPolicy;
	private readonly IReadOnlyList<IAutocompleteCompletionSource> _completionSources;
	private readonly Action<string, string> _debugLog;
	private AutocompleteCompletionSession _session;
	private long _validationGeneration;
	private bool _isIssuingDormantRecoveryRequest;

	internal AutocompleteCompletionCoordinator(
		AutocompletePrefixExtractor prefixExtractor,
		AutocompleteCodeEditPresenter presenter,
		AutocompleteCompletionMatchPolicy matchPolicy,
		IReadOnlyList<IAutocompleteCompletionSource> completionSources,
		Action<string, string> debugLog
	)
	{
		_prefixExtractor =
			prefixExtractor ?? throw new ArgumentNullException(nameof(prefixExtractor));
		_presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
		_matchPolicy = matchPolicy ?? throw new ArgumentNullException(nameof(matchPolicy));
		_completionSources =
			completionSources ?? throw new ArgumentNullException(nameof(completionSources));
		_debugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
	}

	internal bool HandleCompletionRequested(CodeEdit codeEdit, string scriptPath)
	{
		if (
			!_prefixExtractor.TryExtract(
				codeEdit,
				out string prefix,
				out int caretLine,
				out int caretColumn,
				out AutocompleteRequestKind kind,
				out int prefixStartColumn
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
			_session != null
			&& _session.IsCompleteMemberAccessSession
			&& _session.BelongsToSameAnchor(request)
		)
		{
			if (_session.HasAvailableMatch(request))
			{
				_presenter.Publish(codeEdit, _session.PublishedItems);
				_session.MarkActive();
				return true;
			}

			if (_session.MarkDormant())
				CancelNativeCompletionPreservingSession(codeEdit);
			return false;
		}

		_session = null;
		var completionItems = new List<AutocompleteCompletionItem>();

		foreach (IAutocompleteCompletionSource completionSource in _completionSources)
		{
			if (completionSource == null)
				continue;

			string sourceType =
				completionSource.GetType().FullName
				?? completionSource.GetType().Name;

			try
			{
				IReadOnlyList<AutocompleteCompletionItem> sourceItems =
					completionSource.GetCompletions(request);

				if (sourceItems == null)
					continue;

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
				LogCompletionSourceFailure(sourceType, scriptPath, exception);
			}
		}

		if (!_matchPolicy.CanRemainAvailable(completionItems, prefix))
			return false;

		_presenter.Publish(codeEdit, completionItems);
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
			CancelCompletionAndInvalidateSession(codeEdit);
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
					CancelNativeCompletionPreservingSession(codeEdit);
				return;
			}

			if (!session.IsDormant)
				return;

			if (
				!_isIssuingDormantRecoveryRequest
				&& session.TryBeginDormantRecoveryRequest()
			)
			{
				RequestDormantCompletionRecovery(codeEdit);
			}
			return;
		}

		if (session.CanRemainOpen(request))
			return;

		CancelCompletionAndInvalidateSession(codeEdit);
	}

	internal void InvalidatePendingValidations()
	{
		_validationGeneration++;
		_session = null;
	}

	internal void Reset()
	{
		InvalidatePendingValidations();
	}

	private void CancelNativeCompletionPreservingSession(CodeEdit codeEdit)
	{
		if (IsValidGodotObject(codeEdit))
			codeEdit.CancelCodeCompletion();
	}

	private void CancelCompletionAndInvalidateSession(CodeEdit codeEdit)
	{
		InvalidatePendingValidations();
		CancelNativeCompletionPreservingSession(codeEdit);
	}

	private void RequestDormantCompletionRecovery(CodeEdit codeEdit)
	{
		if (_isIssuingDormantRecoveryRequest || !IsValidGodotObject(codeEdit))
			return;

		_isIssuingDormantRecoveryRequest = true;

		try
		{
			codeEdit.RequestCodeCompletion(false);
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

	private void LogCompletionSourceFailure(
		string sourceType,
		string scriptPath,
		Exception exception
	)
	{
		try
		{
			_debugLog(
				"C# autocomplete completion source failed",
				$"Source='{sourceType ?? ""}', "
					+ $"ScriptPath='{scriptPath ?? ""}', "
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

	private static bool IsValidGodotObject(GodotObject source)
	{
		return source != null && GodotObject.IsInstanceValid(source);
	}
}
#endif
