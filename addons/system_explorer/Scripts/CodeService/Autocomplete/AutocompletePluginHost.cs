#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using SystemExplorer.CodeService.Autocomplete.Hint;
using SystemExplorer.CodeService.Autocomplete.Styling;
using SystemExplorer.CodeService.Completion;

namespace SystemExplorer.CodeService.Autocomplete;

internal sealed class AutocompletePluginHost
{
	private readonly AutocompleteEditorBinding _editorBinding;
	private readonly AutocompleteCompletionCoordinator _completionCoordinator;
	private readonly AutocompleteHintController _hintController;
	private readonly AutocompleteCodeEditThemeController _themeController;
	private readonly AutocompleteLocalImportCommitApplier _localImportCommitApplier;
	private readonly AutocompleteResolvedEditApplier _resolvedEditApplier;

	internal AutocompletePluginHost(
		Func<ScriptEditor> scriptEditorProvider,
		Func<GodotObject, StringName, string, string, bool> connectPluginSignal,
		Action<GodotObject, StringName, string, string> disconnectPluginSignal,
		string scriptChangedMethodName,
		string textChangedMethodName,
		string completionRequestedMethodName,
		string guiInputMethodName,
		string iconRootPath,
		Action editorBindingInvalidated,
		Action hintProcessWorkChanged)
	{
		var prefixExtractor = new AutocompletePrefixExtractor();
		var godotTypeIconProvider = new AutocompleteGodotTypeIconProvider(
			() => EditorInterface.Singleton?.GetEditorTheme());
		var iconProvider = new AutocompleteCompletionIconProvider(
			iconRootPath,
			godotTypeIconProvider);
		var presenter = new AutocompleteCodeEditPresenter(iconProvider);
		_completionCoordinator = new AutocompleteCompletionCoordinator(prefixExtractor, presenter);
		_hintController = new AutocompleteHintController(
			prefixExtractor,
			hintProcessWorkChanged ?? throw new ArgumentNullException(nameof(hintProcessWorkChanged))
		);
		_localImportCommitApplier = new AutocompleteLocalImportCommitApplier();
		_resolvedEditApplier = new AutocompleteResolvedEditApplier();

		var themeDefinition = new AutocompleteThemeDefinition { CompletionExistingColor = Colors.Transparent };
		_themeController = new AutocompleteCodeEditThemeController(themeDefinition);
		if (editorBindingInvalidated == null)
			throw new ArgumentNullException(nameof(editorBindingInvalidated));
		_editorBinding = new AutocompleteEditorBinding(
			scriptEditorProvider, connectPluginSignal, disconnectPluginSignal,
			scriptChangedMethodName, textChangedMethodName, completionRequestedMethodName,
			guiInputMethodName,
			InvalidateCompletionStateForEditorBinding,
			editorBindingInvalidated,
			_themeController);
	}

	internal bool EnsureLifecycleCurrent() => _editorBinding.EnsureLifecycleCurrent();

	internal void ObserveHintGuiInput(InputEvent inputEvent)
	{
		_hintController.ObserveGuiInput(inputEvent);
	}

	internal void HandleScriptChanged()
	{
		_completionCoordinator.InvalidatePendingValidations();
		_hintController.Reset();
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
		bool captured = _completionCoordinator.TryCaptureCompletionRequest(
			codeEdit,
			scriptPath,
			true,
			out request
		);
		SynchronizeHintTracking(codeEdit, scriptPath);
		return captured;
	}

	internal bool TryCaptureAutomaticCompletionRequest(out AutocompleteRequestContext request)
	{
		request = null;
		if (!_editorBinding.TryGetActiveCodeEdit(out CodeEdit codeEdit, out string scriptPath)) return false;
		bool captured = _completionCoordinator.TryCaptureCompletionRequest(
			codeEdit,
			scriptPath,
			false,
			out request
		);
		SynchronizeHintTracking(codeEdit, scriptPath);
		return captured;
	}

	internal bool IsCompletionRequestCurrent(AutocompleteRequestContext request)
	{
		if (!_editorBinding.TryGetActiveCodeEdit(out CodeEdit codeEdit, out string scriptPath)) return false;
		return _completionCoordinator.IsCompletionRequestCurrent(codeEdit, scriptPath, request);
	}

	internal bool TryRestorePublishedCompletionForRequest(AutocompleteRequestContext request)
	{
		if (!_editorBinding.TryGetActiveCodeEdit(out CodeEdit codeEdit, out string scriptPath)) return false;
		bool restored = _completionCoordinator.TryRestorePublishedCompletionForRequest(
			codeEdit,
			scriptPath,
			request
		);
		SynchronizeHintTracking(codeEdit, scriptPath);
		return restored;
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
		bool published = _completionCoordinator.TryPublishCompletionResult(
			codeEdit,
			scriptPath,
			request,
			items,
			authority,
			out detail
		);
		SynchronizeHintTracking(codeEdit, scriptPath);
		return published;
	}

	internal bool TryInterceptSelectedManagedCommit(out AutocompleteManagedCommitSelection selection, out string detail)
	{
		selection = null;
		detail = "";
		if (!_editorBinding.TryGetActiveCodeEdit(out CodeEdit codeEdit, out string scriptPath))
		{
			detail = "Active CodeEdit is unavailable for managed commit interception.";
			return false;
		}

		bool captured = _completionCoordinator.TryGetSelectedManagedCommitCompletion(
			codeEdit,
			scriptPath,
			out AutocompleteCompletionItem item,
			out AutocompleteCompletionAuthority authority,
			out long requestGeneration,
			out AutocompletePrefixCapture commitCapture,
			out AutocompleteManagedCommitShape commitShape,
			out bool selectedManagedCommitIdentified,
			out detail);
		if (!captured)
		{
			if (!selectedManagedCommitIdentified)
				return false;

			// The native option was conclusively identified as one of our managed commit
			// rows, but its commit-time editor anchor already drifted. Swallow confirmation
			// and retire the popup so neither an import placeholder nor an ambiguous plain
			// direct label can commit after authority changed.
			SuppressNativeManagedCommitBestEffort(codeEdit, ref detail);
			_completionCoordinator.RetireAfterManagedCommitInterception(codeEdit);
			_hintController.Retire();
			return true;
		}

		// Once an exact managed commit row has been identified its native confirmation
		// must never be allowed to race the plugin-owned mutation. Accept synchronously
		// before returning from gui_input, then retire native and managed popup state.
		if (!SuppressNativeManagedCommitBestEffort(codeEdit, ref detail))
		{
			_completionCoordinator.RetireAfterManagedCommitInterception(codeEdit);
			_hintController.Retire();
			return true;
		}

		selection = new AutocompleteManagedCommitSelection(
			codeEdit.GetInstanceId(),
			scriptPath ?? "",
			requestGeneration,
			item,
			authority,
			commitCapture,
			commitShape);
		_completionCoordinator.RetireAfterManagedCommitInterception(codeEdit);
		_hintController.Retire();
		return true;
	}

	private static bool SuppressNativeManagedCommitBestEffort(CodeEdit codeEdit, ref string detail)
	{
		try
		{
			codeEdit.AcceptEvent();
			return true;
		}
		catch
		{
			detail = "NativeCommitSuppressionFailed";
			try { codeEdit.CancelCodeCompletion(); } catch { }
			return false;
		}
	}

	internal AutocompleteLocalImportCommitApplyResult ApplyLocalManagedCommit(
		ulong codeEditInstanceId,
		string scriptPath,
		AutocompleteCompletionItem item,
		AutocompletePrefixCapture commitCapture,
		AutocompleteManagedCommitShape commitShape)
	{
		if (!TryValidateManagedCommitEditor(codeEditInstanceId, scriptPath, out CodeEdit codeEdit, out _))
		{
			return AutocompleteLocalImportCommitApplyResult.FailedClosed("EditorBindingChanged");
		}

		AutocompleteLocalImportCommitApplyResult result = _localImportCommitApplier.TryApply(
			codeEdit,
			item,
			commitCapture,
			commitShape);
		if (result.Outcome == AutocompleteLocalImportCommitOutcome.Applied)
			_completionCoordinator.SuppressAutomaticRequestForNextTextChanged();
		return result;
	}

	internal bool TryValidateManagedCommitEditor(
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
			if (current.GetCaretCount() != 1) { detail = "Managed commit requires exactly one caret."; return false; }
			if (current.HasSelection(0)) { detail = "Managed commit requires no active selection."; return false; }
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
		if (!TryValidateManagedCommitEditor(codeEditInstanceId, scriptPath, out CodeEdit codeEdit, out string detail))
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
		SynchronizeHintTracking(codeEdit, scriptPath);
		return _completionCoordinator.IsValidationCurrent(generation);
	}

	internal bool HasHintProcessWork => _hintController.HasProcessWork;

	internal void ProcessHintFrame(double delta)
	{
		if (!_editorBinding.TryGetActiveCodeEdit(out CodeEdit codeEdit, out string scriptPath)
			|| !_completionCoordinator.HasPublishedSessionForScript(scriptPath))
		{
			_hintController.Retire();
			return;
		}

		AutocompleteHintContent hintContent = null;
		if (_completionCoordinator.TryGetSelectedPublishedCompletion(
			codeEdit,
			scriptPath,
			out AutocompleteCompletionItem selectedItem,
			out long requestGeneration,
			out _))
		{
			hintContent = new AutocompleteHintContent(
				requestGeneration,
				selectedItem.ServiceKind,
				selectedItem.DisplayText,
				selectedItem.ContainingNamespace,
				selectedItem.ValueType,
				selectedItem.MethodSignatureSet,
				selectedItem.PropertyAccessorSet);
		}

		_hintController.ProcessFrame(codeEdit, hintContent, delta);
	}

	internal void ResetHintPresentation() => _hintController.Reset();

	internal void InvalidatePendingValidations()
	{
		_completionCoordinator.InvalidatePendingValidations();
		_hintController.Retire();
	}

	internal void ResetTransientState()
	{
		_hintController.Reset();
		_completionCoordinator.Reset();
		_editorBinding.Shutdown();
	}

	internal void Shutdown()
	{
		_hintController.Shutdown();
		_completionCoordinator.InvalidatePendingValidations();
		_editorBinding.Shutdown();
		_themeController.Reset();
	}

	private void SynchronizeHintTracking(CodeEdit codeEdit, string scriptPath)
	{
		if (_completionCoordinator.HasPublishedSessionForScript(scriptPath))
			_hintController.Activate(codeEdit);
		else
			_hintController.Retire();
	}

	private void InvalidateCompletionStateForEditorBinding()
	{
		_completionCoordinator.InvalidatePendingValidations();
		_hintController.Reset();
	}
	private static string ToSingleLine(string value) => (value ?? "").Replace('\r', ' ').Replace('\n', ' ');
}
#endif
