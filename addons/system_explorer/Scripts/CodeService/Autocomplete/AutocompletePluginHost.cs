#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using SystemExplorer.CodeService.Autocomplete.Styling;

namespace SystemExplorer.CodeService.Autocomplete;

internal sealed class AutocompletePluginHost
{
	private readonly AutocompleteEditorBinding _editorBinding;
	private readonly AutocompleteCompletionCoordinator _completionCoordinator;
	private readonly AutocompleteCodeEditThemeController _themeController;

	internal AutocompletePluginHost(
		Func<ScriptEditor> scriptEditorProvider,
		Func<GodotObject, StringName, string, string, bool> connectPluginSignal,
		Action<GodotObject, StringName, string, string> disconnectPluginSignal,
		string scriptChangedMethodName,
		string textChangedMethodName,
		string completionRequestedMethodName
	)
	{
		var prefixExtractor = new AutocompletePrefixExtractor();
		var presenter = new AutocompleteCodeEditPresenter();
		_completionCoordinator = new AutocompleteCompletionCoordinator(prefixExtractor, presenter);

		var themeDefinition = new AutocompleteThemeDefinition
		{
			CompletionExistingColor = Colors.Transparent,
		};
		_themeController = new AutocompleteCodeEditThemeController(themeDefinition);

		_editorBinding = new AutocompleteEditorBinding(
			scriptEditorProvider,
			connectPluginSignal,
			disconnectPluginSignal,
			scriptChangedMethodName,
			textChangedMethodName,
			completionRequestedMethodName,
			_completionCoordinator.InvalidatePendingValidations,
			_themeController
		);
	}

	internal bool EnsureLifecycleCurrent()
	{
		return _editorBinding.EnsureLifecycleCurrent();
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
		return _completionCoordinator.TryCaptureCompletionRequest(
			codeEdit,
			scriptPath,
			restoreCompatiblePublishedSession: true,
			out request
		);
	}

	internal bool TryCaptureAutomaticCompletionRequest(out AutocompleteRequestContext request)
	{
		request = null;
		if (!_editorBinding.TryGetActiveCodeEdit(out CodeEdit codeEdit, out string scriptPath))
			return false;

		return _completionCoordinator.TryCaptureCompletionRequest(
			codeEdit,
			scriptPath,
			restoreCompatiblePublishedSession: false,
			out request
		);
	}

	internal bool IsCompletionRequestCurrent(AutocompleteRequestContext request)
	{
		if (!_editorBinding.TryGetActiveCodeEdit(out CodeEdit codeEdit, out string scriptPath))
			return false;
		return _completionCoordinator.IsCompletionRequestCurrent(codeEdit, scriptPath, request);
	}

	internal bool TryPublishCompletionResult(
		AutocompleteRequestContext request,
		IReadOnlyList<AutocompleteCompletionItem> items,
		out string detail
	)
	{
		detail = "";
		if (!_editorBinding.TryGetActiveCodeEdit(out CodeEdit codeEdit, out string scriptPath))
		{
			detail = "Active CodeEdit is unavailable before publication.";
			return false;
		}
		return _completionCoordinator.TryPublishCompletionResult(codeEdit, scriptPath, request, items, out detail);
	}

	internal long BeginTextChangedValidation()
	{
		return _completionCoordinator.BeginTextChangedValidation();
	}

	internal bool IsValidationCurrent(long generation)
	{
		return _completionCoordinator.IsValidationCurrent(generation);
	}

	internal bool TryValidateAfterTextChangedCurrentBinding(
		long generation,
		out bool suppressAutomaticRequest
	)
	{
		suppressAutomaticRequest = false;
		if (!_completionCoordinator.IsValidationCurrent(generation))
			return false;

		if (!_editorBinding.TryGetActiveCodeEdit(out CodeEdit codeEdit, out string scriptPath))
			return false;

		if (!_completionCoordinator.IsValidationCurrent(generation))
			return false;

		suppressAutomaticRequest =
			_completionCoordinator.ValidateAfterTextChanged(codeEdit, scriptPath, generation);
		return _completionCoordinator.IsValidationCurrent(generation);
	}

	internal void InvalidatePendingValidations()
	{
		_completionCoordinator.InvalidatePendingValidations();
	}

	internal void ResetTransientState()
	{
		_completionCoordinator.Reset();
		_editorBinding.Shutdown();
	}

	internal void Shutdown()
	{
		_completionCoordinator.InvalidatePendingValidations();
		_editorBinding.Shutdown();
		_themeController.Reset();
	}
}
#endif
