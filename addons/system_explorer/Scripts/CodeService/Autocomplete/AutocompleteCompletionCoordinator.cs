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
	private bool _suppressAutomaticRequestOnNextTextChanged;
	private long _suppressedAutomaticRequestValidationGeneration = long.MinValue;

	internal AutocompleteCompletionCoordinator(AutocompletePrefixExtractor prefixExtractor, AutocompleteCodeEditPresenter presenter)
	{
		_prefixExtractor = prefixExtractor ?? throw new ArgumentNullException(nameof(prefixExtractor));
		_presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
	}

	internal bool TryCaptureCompletionRequest(CodeEdit codeEdit, string scriptPath, bool restoreCompatiblePublishedSession, out AutocompleteRequestContext request)
	{
		request = null;
		if (!_prefixExtractor.TryExtract(codeEdit, out AutocompletePrefixCapture capture)) return false;

		if (_session != null)
		{
			if (!_session.CanRemainOpen(scriptPath, capture.Line, capture.PrefixStartColumn, capture.Prefix))
			{
				_session = null;
				CancelCodeCompletionBestEffort(codeEdit);
			}
			else if (restoreCompatiblePublishedSession)
			{
				TryRestoreCompatiblePublishedSessionBestEffort(codeEdit);
			}
		}

		try { _requestGeneration = checked(_requestGeneration + 1); }
		catch (OverflowException) { InvalidatePendingValidations(); return false; }

		request = new AutocompleteRequestContext(
			scriptPath ?? "", capture.Prefix ?? "", capture.Line, capture.GodotCaretColumn,
			capture.LspCharacter, capture.PrefixStartColumn, codeEdit.GetInstanceId(),
			_validationGeneration, _requestGeneration);
		return true;
	}

	internal bool IsCompletionRequestCurrent(CodeEdit codeEdit, string scriptPath, AutocompleteRequestContext request)
	{
		if (request == null || request.RequestGeneration != _requestGeneration || request.ValidationGeneration != _validationGeneration
			|| !IsValidGodotObject(codeEdit) || codeEdit.GetInstanceId() != request.CodeEditInstanceId
			|| !string.Equals(scriptPath ?? "", request.ScriptPath, StringComparison.Ordinal)
			|| !_prefixExtractor.TryExtract(codeEdit, out AutocompletePrefixCapture current)) return false;

		return current.Line == request.Line && current.GodotCaretColumn == request.GodotCaretColumn
			&& current.LspCharacter == request.LspCharacter && current.PrefixStartColumn == request.PrefixStartColumn
			&& string.Equals(current.Prefix, request.Prefix, StringComparison.Ordinal);
	}

	internal bool TryRestorePublishedCompletionForRequest(
		CodeEdit codeEdit,
		string scriptPath,
		AutocompleteRequestContext request
	)
	{
		if (!IsCompletionRequestCurrent(codeEdit, scriptPath, request))
			return false;

		AutocompleteCompletionSession session = _session;
		if (session == null
			|| session.RequestGeneration != request.RequestGeneration
			|| !session.CanRemainOpen(
				scriptPath,
				request.Line,
				request.PrefixStartColumn,
				request.Prefix
			))
		{
			return false;
		}

		return TryRestoreCompatiblePublishedSessionBestEffort(codeEdit);
	}

	internal bool TryPublishCompletionResult(
		CodeEdit codeEdit,
		string scriptPath,
		AutocompleteRequestContext request,
		IReadOnlyList<AutocompleteCompletionItem> completionItems,
		AutocompleteCompletionAuthority authority,
		out string detail)
	{
		detail = "";
		if (completionItems == null || authority == null) { detail = "Completion items/authority are unavailable."; return false; }
		if (!IsCompletionRequestCurrent(codeEdit, scriptPath, request)) { detail = "Editor request state changed before publication."; return false; }

		if (completionItems.Count == 0)
		{
			_session = null;
			if (IsValidGodotObject(codeEdit)) codeEdit.CancelCodeCompletion();
			return true;
		}
		if (!CanPublishForPrefix(completionItems, request.Prefix)) { detail = "Completion items are no longer relevant for the current prefix."; return false; }
		if (!_presenter.TryPublish(codeEdit, completionItems, out detail))
		{
			_session = null;
			if (IsValidGodotObject(codeEdit)) codeEdit.CancelCodeCompletion();
			return false;
		}

		_session = new AutocompleteCompletionSession(request.ScriptPath, request.Line, request.PrefixStartColumn, request.RequestGeneration, completionItems, authority);
		return true;
	}

	internal bool TryGetSelectedImportCompletion(
		CodeEdit codeEdit,
		string scriptPath,
		out AutocompleteCompletionItem selectedItem,
		out AutocompleteCompletionAuthority authority,
		out long requestGeneration,
		out string detail)
	{
		selectedItem = null; authority = null; requestGeneration = 0; detail = "";
		AutocompleteCompletionSession session = _session;
		if (session == null || !string.Equals(session.ScriptPath, scriptPath ?? "", StringComparison.Ordinal))
		{
			detail = "No current managed completion session exists for this script.";
			return false;
		}
		if (!_presenter.TryGetSelectedImportCompletion(codeEdit, session, out selectedItem, out detail)) return false;
		authority = session.Authority;
		requestGeneration = session.RequestGeneration;
		return true;
	}

	internal void RetireAfterImportCommitInterception(CodeEdit codeEdit)
	{
		_validationGeneration++;
		_requestGeneration++;
		_session = null;
		CancelCodeCompletionBestEffort(codeEdit);
	}

	internal void SuppressAutomaticRequestForNextTextChanged()
	{
		// Import commit retires its published session before resolve. Preserve only the
		// ordinary-commit behavior we still need: the resolved edit's own deferred
		// TextChanged must not immediately create a fresh automatic completion request.
		_suppressAutomaticRequestOnNextTextChanged = true;
	}

	internal long BeginTextChangedValidation()
	{
		long generation = ++_validationGeneration;
		if (_suppressAutomaticRequestOnNextTextChanged)
		{
			_suppressAutomaticRequestOnNextTextChanged = false;
			_suppressedAutomaticRequestValidationGeneration = generation;
		}
		return generation;
	}

	internal bool IsValidationCurrent(long generation) => generation == _validationGeneration;

	internal bool ValidateAfterTextChanged(CodeEdit codeEdit, string scriptPath, long generation)
	{
		if (!IsValidationCurrent(generation)) return false;
		if (_suppressedAutomaticRequestValidationGeneration == generation)
		{
			_suppressedAutomaticRequestValidationGeneration = long.MinValue;
			_session = null;
			CancelCodeCompletionBestEffort(codeEdit);
			return true;
		}
		AutocompleteCompletionSession session = _session;
		if (session == null) return false;
		if (!IsNativeCodeCompletionActive(codeEdit)) { _session = null; return true; }
		if (_prefixExtractor.TryExtract(codeEdit, out AutocompletePrefixCapture capture)
			&& session.CanRemainOpen(scriptPath, capture.Line, capture.PrefixStartColumn, capture.Prefix)) return false;
		_session = null;
		CancelCodeCompletionBestEffort(codeEdit);
		return false;
	}

	internal void InvalidatePendingValidations()
	{
		_validationGeneration++;
		_requestGeneration++;
		_session = null;
		_suppressAutomaticRequestOnNextTextChanged = false;
		_suppressedAutomaticRequestValidationGeneration = long.MinValue;
	}
	internal void Reset() => InvalidatePendingValidations();

	private bool TryRestoreCompatiblePublishedSessionBestEffort(CodeEdit codeEdit)
	{
		AutocompleteCompletionSession session = _session;
		if (session == null) return false;
		try { if (_presenter.TryPublish(codeEdit, session.PublishedItems, out _)) return true; } catch { }
		_session = null;
		CancelCodeCompletionBestEffort(codeEdit);
		return false;
	}

	private static bool CanPublishForPrefix(IReadOnlyList<AutocompleteCompletionItem> completionItems, string prefix)
	{
		if (completionItems == null || prefix == null) return false;
		bool hasMatchingItem = false, hasActionableMatchingItem = false;
		foreach (AutocompleteCompletionItem item in completionItems)
		{
			string filterText = item?.FilterText ?? "";
			if (!filterText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
			hasMatchingItem = true;
			if (filterText.Length > prefix.Length
				|| (item?.InsertText != null && item.InsertText.Length > prefix.Length)
				|| (item?.RequiresImport == true && item.CompletionHandle.HasValue && item.DisplayText != null && item.DisplayText.Length > prefix.Length))
				hasActionableMatchingItem = true;
		}
		return prefix.Length == 0 ? hasMatchingItem : hasMatchingItem && hasActionableMatchingItem;
	}

	private static bool IsNativeCodeCompletionActive(CodeEdit codeEdit)
	{
		if (!IsValidGodotObject(codeEdit)) return false;
		try { return codeEdit.GetCodeCompletionSelectedIndex() >= 0; } catch { return false; }
	}
	private static void CancelCodeCompletionBestEffort(CodeEdit codeEdit)
	{
		if (!IsValidGodotObject(codeEdit)) return;
		try { codeEdit.CancelCodeCompletion(); } catch { }
	}
	private static bool IsValidGodotObject(GodotObject source) => source != null && GodotObject.IsInstanceValid(source);
}
#endif
