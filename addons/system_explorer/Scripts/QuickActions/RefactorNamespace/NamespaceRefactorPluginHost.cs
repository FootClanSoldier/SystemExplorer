#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal sealed class NamespaceRefactorPluginHost
{
	private readonly AcceptDialog _dialog;
	private readonly AcceptDialog _incompleteWriteReportDialog;
	private readonly Label _descriptionLabel;
	private readonly Label _oldNamespaceLabel;
	private readonly LineEdit _oldNamespaceInput;
	private readonly Label _newNamespaceLabel;
	private readonly LineEdit _newNamespaceInput;
	private readonly Label _applyToLabel;
	private readonly CheckBox _existingNamespaceOption;
	private readonly OptionButton _existingNamespaceDropdown;
	private readonly CheckBox _withoutNamespaceOption;
	private readonly NamespaceRefactorDialogView _namespaceRefactorDialogView;
	private readonly NamespaceRefactorFeature _namespaceRefactorFeature;

	internal NamespaceRefactorPluginHost(
		AcceptDialog dialog,
		AcceptDialog incompleteWriteReportDialog,
		Label descriptionLabel,
		Label oldNamespaceLabel,
		LineEdit oldNamespaceInput,
		Label newNamespaceLabel,
		LineEdit newNamespaceInput,
		Label applyToLabel,
		CheckBox existingNamespaceOption,
		OptionButton existingNamespaceDropdown,
		CheckBox withoutNamespaceOption,
		ScriptEditorBufferLocator bufferLocator,
		ScriptEditorBufferAutosaveCoordinator bufferAutosaveCoordinator,
		ScriptEditorBufferBatchService bufferBatchService,
		Func<IReadOnlyDictionary<string, List<string>>> systemsProvider,
		Func<string, string> getSystemNameFromMetadata,
		Func<string, string> getFolderPathFromMetadata,
		Func<string, string> getEntryFromMetadata,
		Func<string, string> getScriptPathFromEntry,
		Func<string, string> getFolderPathFromEntry,
		string sceneEntryMarker,
		Func<string, bool> ensureSystemsLoadedForTreeOperation,
		Func<EditorInterface> editorInterfaceProvider,
		Action<string, string> showMissingScriptDialog,
		Action<string> debugLog,
		Func<bool> isDebugEnabled,
		Action<string> showWarning,
		Action<string, string> logOperation,
		Action<string, Action> startForegroundEditorOperation,
		Action beginBatchScriptEditorContextPreservation,
		Action endBatchScriptEditorContextPreservation,
		Action syncSelectionAfterOperation,
		Action releaseTreeFocusAfterNavigation
	)
	{
		_dialog = dialog ?? throw new ArgumentNullException(nameof(dialog));
		_incompleteWriteReportDialog =
			incompleteWriteReportDialog
			?? throw new ArgumentNullException(nameof(incompleteWriteReportDialog));
		_descriptionLabel = descriptionLabel ?? throw new ArgumentNullException(nameof(descriptionLabel));
		_oldNamespaceLabel = oldNamespaceLabel ?? throw new ArgumentNullException(nameof(oldNamespaceLabel));
		_oldNamespaceInput = oldNamespaceInput ?? throw new ArgumentNullException(nameof(oldNamespaceInput));
		_newNamespaceLabel = newNamespaceLabel ?? throw new ArgumentNullException(nameof(newNamespaceLabel));
		_newNamespaceInput = newNamespaceInput ?? throw new ArgumentNullException(nameof(newNamespaceInput));
		_applyToLabel = applyToLabel ?? throw new ArgumentNullException(nameof(applyToLabel));
		_existingNamespaceOption = existingNamespaceOption ?? throw new ArgumentNullException(nameof(existingNamespaceOption));
		_existingNamespaceDropdown = existingNamespaceDropdown ?? throw new ArgumentNullException(nameof(existingNamespaceDropdown));
		_withoutNamespaceOption = withoutNamespaceOption ?? throw new ArgumentNullException(nameof(withoutNamespaceOption));
		if (bufferLocator == null) throw new ArgumentNullException(nameof(bufferLocator));
		if (bufferAutosaveCoordinator == null) throw new ArgumentNullException(nameof(bufferAutosaveCoordinator));
		if (bufferBatchService == null) throw new ArgumentNullException(nameof(bufferBatchService));
		if (systemsProvider == null) throw new ArgumentNullException(nameof(systemsProvider));
		if (getSystemNameFromMetadata == null) throw new ArgumentNullException(nameof(getSystemNameFromMetadata));
		if (getFolderPathFromMetadata == null) throw new ArgumentNullException(nameof(getFolderPathFromMetadata));
		if (getEntryFromMetadata == null) throw new ArgumentNullException(nameof(getEntryFromMetadata));
		if (getScriptPathFromEntry == null) throw new ArgumentNullException(nameof(getScriptPathFromEntry));
		if (getFolderPathFromEntry == null) throw new ArgumentNullException(nameof(getFolderPathFromEntry));
		if (sceneEntryMarker == null) throw new ArgumentNullException(nameof(sceneEntryMarker));
		if (ensureSystemsLoadedForTreeOperation == null) throw new ArgumentNullException(nameof(ensureSystemsLoadedForTreeOperation));
		if (editorInterfaceProvider == null) throw new ArgumentNullException(nameof(editorInterfaceProvider));
		if (showMissingScriptDialog == null) throw new ArgumentNullException(nameof(showMissingScriptDialog));
		if (debugLog == null) throw new ArgumentNullException(nameof(debugLog));
		if (isDebugEnabled == null) throw new ArgumentNullException(nameof(isDebugEnabled));
		if (showWarning == null) throw new ArgumentNullException(nameof(showWarning));
		if (logOperation == null) throw new ArgumentNullException(nameof(logOperation));
		if (startForegroundEditorOperation == null) throw new ArgumentNullException(nameof(startForegroundEditorOperation));
		if (beginBatchScriptEditorContextPreservation == null) throw new ArgumentNullException(nameof(beginBatchScriptEditorContextPreservation));
		if (endBatchScriptEditorContextPreservation == null) throw new ArgumentNullException(nameof(endBatchScriptEditorContextPreservation));
		if (syncSelectionAfterOperation == null) throw new ArgumentNullException(nameof(syncSelectionAfterOperation));
		if (releaseTreeFocusAfterNavigation == null) throw new ArgumentNullException(nameof(releaseTreeFocusAfterNavigation));

		_namespaceRefactorDialogView = new NamespaceRefactorDialogView(
			_dialog,
			descriptionLabel,
			oldNamespaceLabel,
			oldNamespaceInput,
			newNamespaceLabel,
			newNamespaceInput,
			applyToLabel,
			existingNamespaceOption,
			existingNamespaceDropdown,
			withoutNamespaceOption
		);

		_namespaceRefactorFeature = new NamespaceRefactorFeature(
			_namespaceRefactorDialogView,
			bufferLocator,
			bufferAutosaveCoordinator,
			bufferBatchService,
			systemsProvider,
			getSystemNameFromMetadata,
			getFolderPathFromMetadata,
			getEntryFromMetadata,
			getScriptPathFromEntry,
			getFolderPathFromEntry,
			sceneEntryMarker,
			ensureSystemsLoadedForTreeOperation,
			editorInterfaceProvider,
			showMissingScriptDialog,
			debugLog,
			isDebugEnabled,
			showWarning,
			logOperation,
			ShowIncompleteWriteReport,
			startForegroundEditorOperation,
			beginBatchScriptEditorContextPreservation,
			endBatchScriptEditorContextPreservation,
			ScriptTextFileService.TryReadText,
			ShowConfiguredDialog,
			ScheduleDeferredBufferRefresh,
			syncSelectionAfterOperation,
			ScheduleDeferredTargetScriptRestoration,
			() => Callable.From(syncSelectionAfterOperation).CallDeferred(),
			() => Callable.From(releaseTreeFocusAfterNavigation).CallDeferred()
		);
	}

	internal bool IsBoundTo(
		AcceptDialog dialog,
		AcceptDialog incompleteWriteReportDialog,
		Label descriptionLabel,
		Label oldNamespaceLabel,
		LineEdit oldNamespaceInput,
		Label newNamespaceLabel,
		LineEdit newNamespaceInput,
		Label applyToLabel,
		CheckBox existingNamespaceOption,
		OptionButton existingNamespaceDropdown,
		CheckBox withoutNamespaceOption
	)
	{
		return ReferenceEquals(_dialog, dialog)
			&& ReferenceEquals(_incompleteWriteReportDialog, incompleteWriteReportDialog)
			&& ReferenceEquals(_descriptionLabel, descriptionLabel)
			&& ReferenceEquals(_oldNamespaceLabel, oldNamespaceLabel)
			&& ReferenceEquals(_oldNamespaceInput, oldNamespaceInput)
			&& ReferenceEquals(_newNamespaceLabel, newNamespaceLabel)
			&& ReferenceEquals(_newNamespaceInput, newNamespaceInput)
			&& ReferenceEquals(_applyToLabel, applyToLabel)
			&& ReferenceEquals(_existingNamespaceOption, existingNamespaceOption)
			&& ReferenceEquals(_existingNamespaceDropdown, existingNamespaceDropdown)
			&& ReferenceEquals(_withoutNamespaceOption, withoutNamespaceOption);
	}

	internal void Open(string metadata) => _namespaceRefactorFeature.OpenDialog(metadata);
	internal void ConfirmDialog() => _namespaceRefactorFeature.ConfirmDialog();
	internal void SetBatchApplyMode(bool useExistingNamespaceMode) =>
		_namespaceRefactorFeature.SetBatchApplyMode(useExistingNamespaceMode);
	internal void SelectExistingNamespace(long index) =>
		_namespaceRefactorFeature.SelectExistingNamespace(index);

	private void ShowIncompleteWriteReport(IReadOnlyList<string> failedWritePaths)
	{
		if (failedWritePaths == null || failedWritePaths.Count == 0)
			return;

		string heading = failedWritePaths.Count == 1
			? "The following script could not be updated:"
			: "The following scripts could not be updated:";

		_incompleteWriteReportDialog.DialogText =
			$"{heading}\n\n{string.Join("\n", failedWritePaths)}";
		Callable.From(() => _incompleteWriteReportDialog.PopupCentered()).CallDeferred();
	}


	private void ShowConfiguredDialog(bool selectAllNewNamespace)
	{
		_namespaceRefactorDialogView.ApplySize();
		_namespaceRefactorDialogView.PopupCentered();
		Callable.From(_namespaceRefactorDialogView.ApplySize).CallDeferred();
		_namespaceRefactorDialogView.FocusNewNamespace(selectAllNewNamespace);
	}

	private void ScheduleDeferredBufferRefresh(
		string scriptPathPayload,
		NamespaceRefactorDiagnosticContext diagnosticContext
	)
	{
		diagnosticContext?.Log(
			"DeferredSync",
			() => $"Deferred buffer refresh callback scheduled; Payload='{scriptPathPayload ?? ""}'."
		);
		Callable.From(
			() =>
			{
				diagnosticContext?.Log("DeferredSync", "Deferred buffer refresh callback started.");
				try
				{
					_namespaceRefactorFeature.RefreshOpenBuffersAfterDeferredResourceRefresh(
						scriptPathPayload,
						diagnosticContext
					);
				}
				finally
				{
					diagnosticContext?.Log("DeferredSync", "Deferred buffer refresh callback completed.");
				}
			}
		).CallDeferred();
	}

	private void ScheduleDeferredTargetScriptRestoration(string scriptPath)
	{
		Callable.From(
			() => _namespaceRefactorFeature.RestoreTargetScriptEditor(scriptPath)
		).CallDeferred();
	}
}
#endif
