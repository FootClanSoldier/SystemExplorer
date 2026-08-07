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

	internal void HandleCompletionRequested(CodeEdit codeEdit, string scriptPath)
	{
		_session = null;

		if (
			!_prefixExtractor.TryExtract(
				codeEdit,
				out string prefix,
				out int caretLine,
				out int caretColumn
			)
		)
		{
			return;
		}

		var request = new AutocompleteRequestContext(
			scriptPath ?? "",
			prefix,
			caretLine,
			caretColumn
		);
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
			return;

		_presenter.Publish(codeEdit, completionItems);
		_session = new AutocompleteCompletionSession(completionItems, _matchPolicy);
	}

	internal long BeginTextChangedValidation()
	{
		return ++_validationGeneration;
	}

	internal bool IsValidationCurrent(long generation)
	{
		return generation == _validationGeneration;
	}

	internal void ValidateAfterTextChanged(CodeEdit codeEdit, long generation)
	{
		if (!IsValidationCurrent(generation))
			return;

		if (
			_session != null
			&& _prefixExtractor.TryExtract(codeEdit, out string prefix)
			&& _session.CanRemainOpen(prefix)
		)
		{
			return;
		}

		CancelCompletion(codeEdit);
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

	private void CancelCompletion(CodeEdit codeEdit)
	{
		InvalidatePendingValidations();

		if (IsValidGodotObject(codeEdit))
			codeEdit.CancelCodeCompletion();
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

	private static bool IsValidGodotObject(GodotObject source)
	{
		return source != null && GodotObject.IsInstanceValid(source);
	}
}
#endif
