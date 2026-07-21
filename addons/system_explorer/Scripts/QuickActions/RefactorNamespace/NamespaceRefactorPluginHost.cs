#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal sealed class NamespaceRefactorPluginHost
{
	private readonly NamespaceRefactorDialogView _namespaceRefactorDialogView;
	private readonly NamespaceRefactorFeature _namespaceRefactorFeature;
	private readonly CheckBox _existingNamespaceOption;
	private readonly OptionButton _existingNamespaceDropdown;
	private readonly CheckBox _withoutNamespaceOption;
	private readonly LineEdit _oldNamespaceInput;
	private readonly LineEdit _newNamespaceInput;

	internal NamespaceRefactorPluginHost(
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
		Action<string> showWarning,
		Action<string, string> logOperation,
		Action beginBatchScriptEditorContextPreservation,
		Action endBatchScriptEditorContextPreservation,
		Action syncSelectionAfterOperation,
		Action releaseTreeFocusAfterNavigation
	)
	{
		if (bufferLocator == null)
			throw new ArgumentNullException(nameof(bufferLocator));
		if (bufferAutosaveCoordinator == null)
			throw new ArgumentNullException(nameof(bufferAutosaveCoordinator));
		if (bufferBatchService == null)
			throw new ArgumentNullException(nameof(bufferBatchService));
		if (systemsProvider == null)
			throw new ArgumentNullException(nameof(systemsProvider));
		if (getSystemNameFromMetadata == null)
			throw new ArgumentNullException(nameof(getSystemNameFromMetadata));
		if (getFolderPathFromMetadata == null)
			throw new ArgumentNullException(nameof(getFolderPathFromMetadata));
		if (getEntryFromMetadata == null)
			throw new ArgumentNullException(nameof(getEntryFromMetadata));
		if (getScriptPathFromEntry == null)
			throw new ArgumentNullException(nameof(getScriptPathFromEntry));
		if (getFolderPathFromEntry == null)
			throw new ArgumentNullException(nameof(getFolderPathFromEntry));
		if (sceneEntryMarker == null)
			throw new ArgumentNullException(nameof(sceneEntryMarker));
		if (ensureSystemsLoadedForTreeOperation == null)
			throw new ArgumentNullException(nameof(ensureSystemsLoadedForTreeOperation));
		if (editorInterfaceProvider == null)
			throw new ArgumentNullException(nameof(editorInterfaceProvider));
		if (showMissingScriptDialog == null)
			throw new ArgumentNullException(nameof(showMissingScriptDialog));
		if (debugLog == null)
			throw new ArgumentNullException(nameof(debugLog));
		if (showWarning == null)
			throw new ArgumentNullException(nameof(showWarning));
		if (logOperation == null)
			throw new ArgumentNullException(nameof(logOperation));
		if (beginBatchScriptEditorContextPreservation == null)
			throw new ArgumentNullException(nameof(beginBatchScriptEditorContextPreservation));
		if (endBatchScriptEditorContextPreservation == null)
			throw new ArgumentNullException(nameof(endBatchScriptEditorContextPreservation));
		if (syncSelectionAfterOperation == null)
			throw new ArgumentNullException(nameof(syncSelectionAfterOperation));
		if (releaseTreeFocusAfterNavigation == null)
			throw new ArgumentNullException(nameof(releaseTreeFocusAfterNavigation));

		Dialog = new AcceptDialog
		{
			Title = "Refactor Namespace",
			Unresizable = true,
		};

		var container = new VBoxContainer
		{
			CustomMinimumSize = new Vector2(480, 0),
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
		};

		var descriptionLabel = new Label
		{
			Text =
				"Update the selected script namespace and\nmatching using statements in linked C# files.",
			AutowrapMode = TextServer.AutowrapMode.Off,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
		};
		container.AddChild(descriptionLabel);
		container.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });

		var newNamespaceLabel = new Label { Text = "New namespace" };
		container.AddChild(newNamespaceLabel);
		_newNamespaceInput = new LineEdit { PlaceholderText = "New namespace" };
		container.AddChild(_newNamespaceInput);

		var oldNamespaceLabel = new Label { Text = "Old namespace" };
		container.AddChild(oldNamespaceLabel);
		_oldNamespaceInput = new LineEdit
		{
			PlaceholderText = "Old namespace",
			Editable = false,
		};
		container.AddChild(_oldNamespaceInput);

		var applyToLabel = new Label { Text = "Apply to:" };
		container.AddChild(applyToLabel);

		var applyModeGroup = new ButtonGroup();

		_existingNamespaceOption = new CheckBox
		{
			Text = "All scripts with namespace:",
			ButtonPressed = true,
			ButtonGroup = applyModeGroup,
		};
		container.AddChild(_existingNamespaceOption);

		_existingNamespaceDropdown = new OptionButton
		{
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
		};
		container.AddChild(_existingNamespaceDropdown);

		_withoutNamespaceOption = new CheckBox
		{
			Text = "Scripts without namespace",
			ButtonGroup = applyModeGroup,
		};
		container.AddChild(_withoutNamespaceOption);

		Dialog.AddChild(container);

		_namespaceRefactorDialogView = new NamespaceRefactorDialogView(
			Dialog,
			descriptionLabel,
			oldNamespaceLabel,
			_oldNamespaceInput,
			newNamespaceLabel,
			_newNamespaceInput,
			applyToLabel,
			_existingNamespaceOption,
			_existingNamespaceDropdown,
			_withoutNamespaceOption
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
			showWarning,
			logOperation,
			beginBatchScriptEditorContextPreservation,
			endBatchScriptEditorContextPreservation,
			ReadNamespaceFromScript,
			ShowConfiguredDialog,
			ScheduleDeferredBufferRefresh,
			syncSelectionAfterOperation,
			ScheduleDeferredTargetScriptRestoration,
			() => Callable.From(syncSelectionAfterOperation).CallDeferred(),
			() => Callable.From(releaseTreeFocusAfterNavigation).CallDeferred()
		);

		Dialog.Confirmed += OnConfirmed;
		_existingNamespaceOption.Toggled += OnExistingNamespaceOptionToggled;
		_existingNamespaceDropdown.ItemSelected += OnExistingNamespaceSelected;
		_withoutNamespaceOption.Toggled += OnWithoutNamespaceOptionToggled;
		Dialog.WindowInput += OnDialogWindowInput;
		_oldNamespaceInput.TextSubmitted += _ => ConfirmDialogFromEnter();
		_newNamespaceInput.TextSubmitted += _ => ConfirmDialogFromEnter();
	}

	internal AcceptDialog Dialog { get; }

	internal void Open(string metadata)
	{
		_namespaceRefactorFeature.OpenDialog(metadata);
	}

	private static string ReadNamespaceFromScript(string scriptPath)
	{
		if (!FileAccess.FileExists(scriptPath))
			return "";

		return NamespaceTextRewriter.GetNamespaceFromText(
			ScriptTextFileService.ReadText(scriptPath)
		);
	}

	private void ShowConfiguredDialog(bool selectAllNewNamespace)
	{
		ApplyDialogSize();
		_namespaceRefactorDialogView.PopupCentered();
		Callable.From(_namespaceRefactorDialogView.ApplySize).CallDeferred();
		_namespaceRefactorDialogView.FocusNewNamespace(selectAllNewNamespace);
	}

	private void ApplyDialogSize()
	{
		if (Dialog == null)
			return;

		_namespaceRefactorDialogView.ApplySize();
	}

	private void ScheduleDeferredBufferRefresh(string scriptPathPayload)
	{
		Callable
			.From(
				() =>
					_namespaceRefactorFeature.RefreshOpenBuffersAfterDeferredResourceRefresh(
						scriptPathPayload
					)
			)
			.CallDeferred();
	}

	private void ScheduleDeferredTargetScriptRestoration(string scriptPath)
	{
		Callable
			.From(() => _namespaceRefactorFeature.RestoreTargetScriptEditor(scriptPath))
			.CallDeferred();
	}

	private void OnConfirmed()
	{
		_namespaceRefactorFeature.ConfirmDialog();
	}

	private void OnExistingNamespaceOptionToggled(bool pressed)
	{
		if (!pressed)
			return;

		_namespaceRefactorFeature.SetBatchApplyMode(true);
	}

	private void OnExistingNamespaceSelected(long index)
	{
		_namespaceRefactorFeature.SelectExistingNamespace(index);
	}

	private void OnWithoutNamespaceOptionToggled(bool pressed)
	{
		if (!pressed)
			return;

		_namespaceRefactorFeature.SetBatchApplyMode(false);
	}

	private void OnDialogWindowInput(InputEvent inputEvent)
	{
		if (!IsEnterPressed(inputEvent))
			return;

		ConfirmDialogFromEnter();
	}

	private void ConfirmDialogFromEnter()
	{
		if (Dialog == null || !Dialog.Visible)
			return;

		Dialog.Hide();
		_namespaceRefactorFeature.ConfirmDialog();
	}

	private static bool IsEnterPressed(InputEvent inputEvent)
	{
		return inputEvent is InputEventKey keyEvent
			&& keyEvent.Pressed
			&& !keyEvent.Echo
			&& (keyEvent.Keycode == Key.Enter || keyEvent.Keycode == Key.KpEnter);
	}
}
#endif
