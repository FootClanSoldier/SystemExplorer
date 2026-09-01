#if TOOLS
using Godot;
using System;
using System.Collections.Generic;

namespace SystemExplorer.CodeService.Autocomplete;

internal sealed class AutocompleteCompletionCoordinator
{
	private readonly AutocompletePrefixExtractor _prefixExtractor;
	private readonly AutocompleteCodeEditPresenter _presenter;
	private AutocompleteCompletionSession _session;
	private long _validationGeneration;
	private long _requestGeneration;

	internal AutocompleteCompletionCoordinator(
		AutocompletePrefixExtractor prefixExtractor,
		AutocompleteCodeEditPresenter presenter
	)
	{
		_prefixExtractor = prefixExtractor ?? throw new ArgumentNullException(nameof(prefixExtractor));
		_presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
	}

	internal bool TryCaptureCompletionRequest(
		CodeEdit codeEdit,
		string scriptPath,
		bool restoreCompatiblePublishedSession,
		out AutocompleteRequestContext request
	)
	{
		request = null;
		if (!_prefixExtractor.TryExtract(codeEdit, out AutocompletePrefixCapture capture))
			return false;

		if (_session != null)
		{
			if (!_session.CanRemainOpen(
				scriptPath,
				capture.Line,
				capture.PrefixStartColumn,
				capture.Prefix
			))
			{
				_session = null;
				CancelCodeCompletionBestEffort(codeEdit);
			}
			else if (restoreCompatiblePublishedSession)
			{
				TryRestoreCompatiblePublishedSessionBestEffort(codeEdit);
			}
		}

		try
		{
			_requestGeneration = checked(_requestGeneration + 1);
		}
		catch (OverflowException)
		{
			InvalidatePendingValidations();
			return false;
		}

		request = new AutocompleteRequestContext(
			scriptPath ?? "",
			capture.Prefix ?? "",
			capture.Line,
			capture.GodotCaretColumn,
			capture.LspCharacter,
			capture.PrefixStartColumn,
			codeEdit.GetInstanceId(),
			_validationGeneration,
			_requestGeneration
		);
		return true;
	}

	internal bool IsCompletionRequestCurrent(
		CodeEdit codeEdit,
		string scriptPath,
		AutocompleteRequestContext request
	)
	{
		if (request == null
			|| request.RequestGeneration != _requestGeneration
			|| request.ValidationGeneration != _validationGeneration
			|| !IsValidGodotObject(codeEdit)
			|| codeEdit.GetInstanceId() != request.CodeEditInstanceId
			|| !string.Equals(scriptPath ?? "", request.ScriptPath, StringComparison.Ordinal)
			|| !_prefixExtractor.TryExtract(codeEdit, out AutocompletePrefixCapture current))
		{
			return false;
		}

		return current.Line == request.Line
			&& current.GodotCaretColumn == request.GodotCaretColumn
			&& current.LspCharacter == request.LspCharacter
			&& current.PrefixStartColumn == request.PrefixStartColumn
			&& string.Equals(current.Prefix, request.Prefix, StringComparison.Ordinal);
	}

	internal bool TryPublishCompletionResult(
		CodeEdit codeEdit,
		string scriptPath,
		AutocompleteRequestContext request,
		IReadOnlyList<AutocompleteCompletionItem> completionItems,
		out string detail
	)
	{
		detail = "";
		if (completionItems == null)
		{
			detail = "Completion items are unavailable.";
			return false;
		}
		if (!IsCompletionRequestCurrent(codeEdit, scriptPath, request))
		{
			detail = "Editor request state changed before publication.";
			return false;
		}

		if (completionItems.Count == 0)
		{
			_session = null;
			if (IsValidGodotObject(codeEdit))
				codeEdit.CancelCodeCompletion();
			return true;
		}

		if (!CanPublishForPrefix(completionItems, request.Prefix))
		{
			detail = "Completion items are no longer relevant for the current prefix.";
			return false;
		}

		if (!_presenter.TryPublish(codeEdit, completionItems, out detail))
		{
			_session = null;
			if (IsValidGodotObject(codeEdit))
				codeEdit.CancelCodeCompletion();
			return false;
		}

		_session = new AutocompleteCompletionSession(
			request.ScriptPath,
			request.Line,
			request.PrefixStartColumn,
			completionItems
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

	internal bool ValidateAfterTextChanged(
		CodeEdit codeEdit,
		string scriptPath,
		long generation
	)
	{
		if (!IsValidationCurrent(generation))
			return false;

		AutocompleteCompletionSession session = _session;
		if (session == null)
			return false;

		if (!IsNativeCodeCompletionActive(codeEdit))
		{
			_session = null;
			return true;
		}

		if (_prefixExtractor.TryExtract(codeEdit, out AutocompletePrefixCapture capture)
			&& session.CanRemainOpen(
				scriptPath,
				capture.Line,
				capture.PrefixStartColumn,
				capture.Prefix
			))
		{
			return false;
		}

		_session = null;
		CancelCodeCompletionBestEffort(codeEdit);
		return false;
	}

	internal void InvalidatePendingValidations()
	{
		_validationGeneration++;
		_requestGeneration++;
		_session = null;
	}

	internal void Reset()
	{
		InvalidatePendingValidations();
	}

	private void TryRestoreCompatiblePublishedSessionBestEffort(CodeEdit codeEdit)
	{
		AutocompleteCompletionSession session = _session;
		if (session == null)
			return;

		try
		{
			if (_presenter.TryPublish(codeEdit, session.PublishedItems, out _))
				return;
		}
		catch
		{
		}

		_session = null;
		CancelCodeCompletionBestEffort(codeEdit);
	}

	private static bool CanPublishForPrefix(
		IReadOnlyList<AutocompleteCompletionItem> completionItems,
		string prefix
	)
	{
		if (completionItems == null || prefix == null)
			return false;

		bool hasMatchingItem = false;
		bool hasActionableMatchingItem = false;
		foreach (AutocompleteCompletionItem item in completionItems)
		{
			string filterText = item?.FilterText ?? "";
			if (!filterText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				continue;

			hasMatchingItem = true;
			string insertText = item?.InsertText ?? "";
			if (filterText.Length > prefix.Length || insertText.Length > prefix.Length)
				hasActionableMatchingItem = true;
		}

		return prefix.Length == 0
			? hasMatchingItem
			: hasMatchingItem && hasActionableMatchingItem;
	}

	private static bool IsNativeCodeCompletionActive(CodeEdit codeEdit)
	{
		if (!IsValidGodotObject(codeEdit))
			return false;

		try
		{
			return codeEdit.GetCodeCompletionSelectedIndex() >= 0;
		}
		catch
		{
			return false;
		}
	}

	private static void CancelCodeCompletionBestEffort(CodeEdit codeEdit)
	{
		if (!IsValidGodotObject(codeEdit))
			return;

		try
		{
			codeEdit.CancelCodeCompletion();
		}
		catch
		{
		}
	}

	private static bool IsValidGodotObject(GodotObject source)
	{
		return source != null && GodotObject.IsInstanceValid(source);
	}
}
#endif
