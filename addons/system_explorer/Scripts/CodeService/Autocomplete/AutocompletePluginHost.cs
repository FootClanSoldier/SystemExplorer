#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using SystemExplorer.CodeService.Autocomplete.Styling;
using SystemExplorer.CodeService.Completion;

namespace SystemExplorer.CodeService.Autocomplete;

internal sealed class AutocompletePluginHost
{
	private readonly AutocompleteEditorBinding _editorBinding;
	private readonly AutocompleteCompletionCoordinator _completionCoordinator;
	private readonly AutocompleteCodeEditThemeController _themeController;
	private readonly AutocompleteResolvedEditApplier _resolvedEditApplier;

	internal AutocompletePluginHost(
		Func<ScriptEditor> scriptEditorProvider,
		Func<GodotObject, StringName, string, string, bool> connectPluginSignal,
		Action<GodotObject, StringName, string, string> disconnectPluginSignal,
		string scriptChangedMethodName,
		string textChangedMethodName,
		string completionRequestedMethodName,
		string guiInputMethodName,
		Action editorBindingInvalidated)
	{
		var prefixExtractor = new AutocompletePrefixExtractor();
		var presenter = new AutocompleteCodeEditPresenter();
		_completionCoordinator = new AutocompleteCompletionCoordinator(prefixExtractor, presenter);
		_resolvedEditApplier = new AutocompleteResolvedEditApplier();

		var themeDefinition = new AutocompleteThemeDefinition { CompletionExistingColor = Colors.Transparent };
		_themeController = new AutocompleteCodeEditThemeController(themeDefinition);
		if (editorBindingInvalidated == null)
			throw new ArgumentNullException(nameof(editorBindingInvalidated));
		_editorBinding = new AutocompleteEditorBinding(
			scriptEditorProvider, connectPluginSignal, disconnectPluginSignal,
			scriptChangedMethodName, textChangedMethodName, completionRequestedMethodName,
			guiInputMethodName,
			_completionCoordinator.InvalidatePendingValidations,
			editorBindingInvalidated,
			_themeController);
	}

	internal bool EnsureLifecycleCurrent() => _editorBinding.EnsureLifecycleCurrent();

	internal bool TrySuppressTypedOpeningParenthesisAutoClose()
	{
		return _editorBinding.TrySuppressTypedOpeningParenthesisAutoClose();
	}

	internal void RestoreTypedOpeningParenthesisAutoCloseSuppression()
	{
		_editorBinding.RestoreTypedOpeningParenthesisAutoCloseSuppression();
	}

	internal void HandleScriptChanged()
	{
		_completionCoordinator.InvalidatePendingValidations();
		_editorBinding.RefreshCodeEditBinding();
	}

	internal bool TryCaptureCompletionRequest(out AutocompleteRequestContext request)
	{
		request = null;
		if (!_editorBinding.TryGetActiveCodeEdit(out CodeEdit codeEdit, out string scriptPath))
		{
			_editorBinding.RefreshCodeEditBinding();
			return false;
		}
		return _completionCoordinator.TryCaptureCompletionRequest(codeEdit, scriptPath, true, out request);
	}

	internal bool TryCaptureAutomaticCompletionRequest(out AutocompleteRequestContext request)
	{
		request = null;
		if (!_editorBinding.TryGetActiveCodeEdit(out CodeEdit codeEdit, out string scriptPath)) return false;
		return _completionCoordinator.TryCaptureCompletionRequest(codeEdit, scriptPath, false, out request);
	}

	internal bool IsCompletionRequestCurrent(AutocompleteRequestContext request)
	{
		if (!_editorBinding.TryGetActiveCodeEdit(out CodeEdit codeEdit, out string scriptPath)) return false;
		return _completionCoordinator.IsCompletionRequestCurrent(codeEdit, scriptPath, request);
	}

	internal bool TryRestorePublishedCompletionForRequest(AutocompleteRequestContext request)
	{
		if (!_editorBinding.TryGetActiveCodeEdit(out CodeEdit codeEdit, out string scriptPath)) return false;
		return _completionCoordinator.TryRestorePublishedCompletionForRequest(
			codeEdit,
			scriptPath,
			request
		);
	}

	internal bool TryPublishCompletionResult(
		AutocompleteRequestContext request,
		IReadOnlyList<AutocompleteCompletionItem> items,
		AutocompleteCompletionAuthority authority,
		out string detail)
	{
		detail = "";
		if (!_editorBinding.TryGetActiveCodeEdit(out CodeEdit codeEdit, out string scriptPath))
		{
			detail = "Active CodeEdit is unavailable before publication.";
			return false;
		}
		return _completionCoordinator.TryPublishCompletionResult(codeEdit, scriptPath, request, items, authority, out detail);
	}

	internal bool TryInterceptSelectedImportCommit(out AutocompleteImportCommitSelection selection, out string detail)
	{
		selection = null;
		detail = "";
		if (!_editorBinding.TryGetActiveCodeEdit(out CodeEdit codeEdit, out string scriptPath))
		{
			detail = "Active CodeEdit is unavailable for import commit interception.";
			return false;
		}
		if (!_completionCoordinator.TryGetSelectedImportCompletion(
			codeEdit, scriptPath, out AutocompleteCompletionItem item,
			out AutocompleteCompletionAuthority authority, out long requestGeneration, out detail))
			return false;

		// Once an exact managed import item has been identified the native placeholder
		// must never be allowed to commit. Accept the input event synchronously before
		// returning from gui_input, then retire the native/managed popup state.
		try
		{
			codeEdit.AcceptEvent();
		}
		catch (Exception exception)
		{
			detail = "Could not accept the native import confirmation event: " + ToSingleLine(exception.Message);
			try { codeEdit.CancelCodeCompletion(); } catch { }
			_completionCoordinator.RetireAfterImportCommitInterception(codeEdit);
			return true;
		}

		selection = new AutocompleteImportCommitSelection(
			codeEdit.GetInstanceId(), scriptPath ?? "", requestGeneration, item, authority);
		_completionCoordinator.RetireAfterImportCommitInterception(codeEdit);
		return true;
	}

	internal bool TryValidateImportCommitEditor(
		ulong codeEditInstanceId,
		string scriptPath,
		out CodeEdit codeEdit,
		out string detail)
	{
		codeEdit = null;
		detail = "";
		if (!_editorBinding.TryGetActiveCodeEdit(out CodeEdit current, out string currentScriptPath)
			|| current.GetInstanceId() != codeEditInstanceId
			|| !string.Equals(currentScriptPath ?? "", scriptPath ?? "", StringComparison.Ordinal))
		{
			detail = "Bound CodeEdit/script identity changed.";
			return false;
		}
		try
		{
			if (!current.Editable) { detail = "Current CodeEdit is not editable."; return false; }
			if (current.GetCaretCount() != 1) { detail = "Import commit requires exactly one caret."; return false; }
			if (current.HasSelection(0)) { detail = "Import commit requires no active selection."; return false; }
		}
		catch (Exception exception)
		{
			detail = "Current CodeEdit state could not be validated: " + ToSingleLine(exception.Message);
			return false;
		}
		codeEdit = current;
		return true;
	}

	internal AutocompleteResolvedEditApplyResult ApplyResolvedImportEdit(
		ulong codeEditInstanceId,
		string scriptPath,
		CodeServiceCompletionTextEdit edit)
	{
		if (!TryValidateImportCommitEditor(codeEditInstanceId, scriptPath, out CodeEdit codeEdit, out string detail))
			return new AutocompleteResolvedEditApplyResult(false, false, detail);

		AutocompleteResolvedEditApplyResult result = _resolvedEditApplier.TryApply(codeEdit, edit);
		if (result.SourceApplied)
			_completionCoordinator.SuppressAutomaticRequestForNextTextChanged();
		return result;
	}

	internal long BeginTextChangedValidation() => _completionCoordinator.BeginTextChangedValidation();
	internal bool IsValidationCurrent(long generation) => _completionCoordinator.IsValidationCurrent(generation);

	internal bool TryValidateAfterTextChangedCurrentBinding(long generation, out bool suppressAutomaticRequest)
	{
		suppressAutomaticRequest = false;
		if (!_completionCoordinator.IsValidationCurrent(generation)) return false;
		if (!_editorBinding.TryGetActiveCodeEdit(out CodeEdit codeEdit, out string scriptPath)) return false;
		if (!_completionCoordinator.IsValidationCurrent(generation)) return false;
		suppressAutomaticRequest = _completionCoordinator.ValidateAfterTextChanged(codeEdit, scriptPath, generation);
		return _completionCoordinator.IsValidationCurrent(generation);
	}

	internal void InvalidatePendingValidations() => _completionCoordinator.InvalidatePendingValidations();
	internal void ResetTransientState() { _completionCoordinator.Reset(); _editorBinding.Shutdown(); }
	internal void Shutdown() { _completionCoordinator.InvalidatePendingValidations(); _editorBinding.Shutdown(); _themeController.Reset(); }
	private static string ToSingleLine(string value) => (value ?? "").Replace('\r', ' ').Replace('\n', ' ');
}
#endif
