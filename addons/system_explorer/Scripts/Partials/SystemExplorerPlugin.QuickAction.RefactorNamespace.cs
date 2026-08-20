#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SystemExplorer.Autocomplete;
using SystemExplorer.EditorIntegration.Operations;
using SystemExplorer.QuickActions.RefactorNamespace;

public partial class SystemExplorerPlugin
{
	#region Refactor Namespace UI and Host Composition
	private const string NamespaceRefactorIntegrationWarning =
		"Refactor Namespace could not be opened because its editor dialog integration could not be restored safely after the C# assembly reload. Disable and re-enable System Explorer or restart Godot.";

	private void CreateNamespaceRefactorDialogs()
	{
		_namespaceRefactorDialog = new AcceptDialog
		{
			Title = "Refactor Namespace",
			Unresizable = true,
		};

		_namespaceRefactorIncompleteWriteReportDialog = new AcceptDialog
		{
			Title = "Namespace Refactor Incomplete",
			OkButtonText = "OK",
			MinSize = new Vector2I(460, 160),
			Unresizable = true,
		};

		var container = new VBoxContainer
		{
			CustomMinimumSize = new Vector2(480, 0),
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
		};

		_namespaceRefactorDescriptionLabel = new Label
		{
			Text =
				"Update the selected script namespace and\nmatching using statements in linked C# files.",
			AutowrapMode = TextServer.AutowrapMode.Off,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
		};
		container.AddChild(_namespaceRefactorDescriptionLabel);
		container.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });

		_namespaceRefactorNewNamespaceLabel = new Label { Text = "New namespace" };
		container.AddChild(_namespaceRefactorNewNamespaceLabel);
		_namespaceRefactorNewNamespaceInput = new LineEdit
		{
			PlaceholderText = "New namespace",
		};
		container.AddChild(_namespaceRefactorNewNamespaceInput);

		_namespaceRefactorOldNamespaceLabel = new Label { Text = "Old namespace" };
		container.AddChild(_namespaceRefactorOldNamespaceLabel);
		_namespaceRefactorOldNamespaceInput = new LineEdit
		{
			PlaceholderText = "Old namespace",
			Editable = false,
		};
		container.AddChild(_namespaceRefactorOldNamespaceInput);

		_namespaceRefactorApplyToLabel = new Label { Text = "Apply to:" };
		container.AddChild(_namespaceRefactorApplyToLabel);

		var applyModeGroup = new ButtonGroup();

		_namespaceRefactorExistingNamespaceOption = new CheckBox
		{
			Text = "All scripts with namespace:",
			ButtonPressed = true,
			ButtonGroup = applyModeGroup,
		};
		container.AddChild(_namespaceRefactorExistingNamespaceOption);

		_namespaceRefactorExistingNamespaceDropdown = new OptionButton
		{
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
		};
		container.AddChild(_namespaceRefactorExistingNamespaceDropdown);

		_namespaceRefactorWithoutNamespaceOption = new CheckBox
		{
			Text = "Scripts without namespace",
			ButtonGroup = applyModeGroup,
		};
		container.AddChild(_namespaceRefactorWithoutNamespaceOption);

		_namespaceRefactorDialog.AddChild(container);
	}

	private bool AreNamespaceRefactorSignalSourcesValid(out string failureDetail)
	{
		List<string> invalidComponents = GetInvalidNamespaceRefactorUiComponents();
		failureDetail = invalidComponents.Count == 0
			? ""
			: $"Invalid Refactor Namespace signal sources: {string.Join(", ", invalidComponents)}";
		return invalidComponents.Count == 0;
	}

	private bool ConnectNamespaceRefactorDialogSignals()
	{
		if (!AreNamespaceRefactorSignalSourcesValid(out string failureDetail))
		{
			DebugLogger.LogOperation(
				"Refactor Namespace signal connection failed",
				failureDetail
			);
			return false;
		}

		bool connected = true;
		connected &= TryConnectPluginSignal(
			_namespaceRefactorDialog,
			AcceptDialog.SignalName.Confirmed,
			nameof(OnNamespaceRefactorConfirmed),
			nameof(_namespaceRefactorDialog)
		);
		connected &= TryConnectPluginSignal(
			_namespaceRefactorDialog,
			Window.SignalName.WindowInput,
			nameof(OnNamespaceRefactorDialogWindowInput),
			nameof(_namespaceRefactorDialog)
		);
		connected &= TryConnectPluginSignal(
			_namespaceRefactorExistingNamespaceOption,
			BaseButton.SignalName.Toggled,
			nameof(OnNamespaceRefactorExistingNamespaceOptionToggled),
			nameof(_namespaceRefactorExistingNamespaceOption)
		);
		connected &= TryConnectPluginSignal(
			_namespaceRefactorExistingNamespaceDropdown,
			OptionButton.SignalName.ItemSelected,
			nameof(OnNamespaceRefactorExistingNamespaceSelected),
			nameof(_namespaceRefactorExistingNamespaceDropdown)
		);
		connected &= TryConnectPluginSignal(
			_namespaceRefactorWithoutNamespaceOption,
			BaseButton.SignalName.Toggled,
			nameof(OnNamespaceRefactorWithoutNamespaceOptionToggled),
			nameof(_namespaceRefactorWithoutNamespaceOption)
		);
		connected &= TryConnectPluginSignal(
			_namespaceRefactorOldNamespaceInput,
			LineEdit.SignalName.TextSubmitted,
			nameof(OnNamespaceRefactorOldNamespaceSubmitted),
			nameof(_namespaceRefactorOldNamespaceInput)
		);
		connected &= TryConnectPluginSignal(
			_namespaceRefactorNewNamespaceInput,
			LineEdit.SignalName.TextSubmitted,
			nameof(OnNamespaceRefactorNewNamespaceSubmitted),
			nameof(_namespaceRefactorNewNamespaceInput)
		);

		return connected;
	}

	private void DisconnectNamespaceRefactorDialogSignals()
	{
		DisconnectPluginSignal(
			_namespaceRefactorDialog,
			AcceptDialog.SignalName.Confirmed,
			nameof(OnNamespaceRefactorConfirmed),
			nameof(_namespaceRefactorDialog)
		);
		DisconnectPluginSignal(
			_namespaceRefactorDialog,
			Window.SignalName.WindowInput,
			nameof(OnNamespaceRefactorDialogWindowInput),
			nameof(_namespaceRefactorDialog)
		);
		DisconnectPluginSignal(
			_namespaceRefactorExistingNamespaceOption,
			BaseButton.SignalName.Toggled,
			nameof(OnNamespaceRefactorExistingNamespaceOptionToggled),
			nameof(_namespaceRefactorExistingNamespaceOption)
		);
		DisconnectPluginSignal(
			_namespaceRefactorExistingNamespaceDropdown,
			OptionButton.SignalName.ItemSelected,
			nameof(OnNamespaceRefactorExistingNamespaceSelected),
			nameof(_namespaceRefactorExistingNamespaceDropdown)
		);
		DisconnectPluginSignal(
			_namespaceRefactorWithoutNamespaceOption,
			BaseButton.SignalName.Toggled,
			nameof(OnNamespaceRefactorWithoutNamespaceOptionToggled),
			nameof(_namespaceRefactorWithoutNamespaceOption)
		);
		DisconnectPluginSignal(
			_namespaceRefactorOldNamespaceInput,
			LineEdit.SignalName.TextSubmitted,
			nameof(OnNamespaceRefactorOldNamespaceSubmitted),
			nameof(_namespaceRefactorOldNamespaceInput)
		);
		DisconnectPluginSignal(
			_namespaceRefactorNewNamespaceInput,
			LineEdit.SignalName.TextSubmitted,
			nameof(OnNamespaceRefactorNewNamespaceSubmitted),
			nameof(_namespaceRefactorNewNamespaceInput)
		);
	}

	private void StartNamespaceRefactorForegroundOperation(
		string operationName,
		Action operationBody
	)
	{
		if (operationBody == null)
			throw new ArgumentNullException(nameof(operationBody));

		StartObservedEditorOperation(
			operationName,
			operation =>
				RunNamespaceRefactorForegroundOperationAsync(
					operation,
					operationBody
				)
		);
	}

	private async Task RunNamespaceRefactorForegroundOperationAsync(
		EditorOperationLease operation,
		Action operationBody
	)
	{
		operation.CancellationToken.ThrowIfCancellationRequested();

		if (!IsEditorOperationAccessValid(operation))
			return;

		if (
			!TryBeginAutocompleteExternalMutation(
				AutocompleteExternalMutationOrigin.NamespaceRefactor,
				operation.OperationName,
				out long externalMutationOperationToken
			)
		)
		{
			throw new InvalidOperationException(
				$"{operation.OperationName} could not acquire autocomplete ExternalMutationLease authority."
			);
		}

		try
		{
			LogNamespaceRefactorForegroundBoundary(
				"BeforeProcessFrameAwait",
				externalMutationOperationToken
			);

			SceneTree tree = GetTree();

			if (tree == null || !GodotObject.IsInstanceValid(tree))
				return;

			await ToSignal(tree, SceneTree.SignalName.ProcessFrame);

			LogNamespaceRefactorForegroundBoundary(
				"AfterProcessFrameAwait",
				externalMutationOperationToken
			);

			operation.CancellationToken.ThrowIfCancellationRequested();

			if (!IsEditorOperationAccessValid(operation))
				return;

			operationBody();
		}
		finally
		{
			ScheduleAutocompleteExternalMutationRelease(externalMutationOperationToken);
		}
	}

	private void LogNamespaceRefactorForegroundBoundary(
		string phase,
		long externalMutationOperationToken
	)
	{
		DebugLogger.LogPersistentFileOnlyOperation(
			"Namespace Refactor foreground boundary",
			$"Phase='{phase}', ExternalMutationActive='{IsAutocompleteExternalMutationActive}', OperationToken='{externalMutationOperationToken}', MutationTransactionId='{_autocompleteExternalMutationLease?.MutationTransactionId ?? 0}', Origin='{_autocompleteExternalMutationOrigin}'"
		);
	}

	private void ScheduleNamespaceRefactorIncompleteWriteReportPresentationDeferred()
	{
		string scheduledManagedAssemblyGeneration = ManagedAssemblyGeneration;
		CallDeferred(
			nameof(ApplyNamespaceRefactorIncompleteWriteReportPresentationDeferred),
			scheduledManagedAssemblyGeneration
		);
	}

	private void ApplyNamespaceRefactorIncompleteWriteReportPresentationDeferred(
		string scheduledManagedAssemblyGeneration
	)
	{
		if (
			!string.Equals(
				scheduledManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
		)
		{
			return;
		}

		if (!IsNamespaceRefactorDeferredPluginBoundaryAvailable())
			return;

		NamespaceRefactorPluginHost host = _namespaceRefactorHost;
		if (host == null)
			return;

		host.PresentIncompleteWriteReportDeferred();
	}

	private void ScheduleNamespaceRefactorConfiguredDialogSizeCorrectionDeferred()
	{
		string scheduledManagedAssemblyGeneration = ManagedAssemblyGeneration;
		CallDeferred(
			nameof(ApplyNamespaceRefactorConfiguredDialogSizeCorrectionDeferred),
			scheduledManagedAssemblyGeneration
		);
	}

	private void ApplyNamespaceRefactorConfiguredDialogSizeCorrectionDeferred(
		string scheduledManagedAssemblyGeneration
	)
	{
		if (
			!string.Equals(
				scheduledManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
		)
		{
			return;
		}

		if (!IsNamespaceRefactorDeferredPluginBoundaryAvailable())
			return;

		NamespaceRefactorPluginHost host = _namespaceRefactorHost;
		if (host == null)
			return;

		host.ApplyConfiguredDialogSizeCorrectionDeferred();
	}

	private void ScheduleNamespaceRefactorDeferredBufferRefreshDispatch(long requestToken)
	{
		string scheduledManagedAssemblyGeneration = ManagedAssemblyGeneration;
		CallDeferred(
			nameof(ApplyNamespaceRefactorDeferredBufferRefreshDispatch),
			scheduledManagedAssemblyGeneration,
			requestToken
		);
	}

	private void ApplyNamespaceRefactorDeferredBufferRefreshDispatch(
		string scheduledManagedAssemblyGeneration,
		long requestToken
	)
	{
		if (
			!string.Equals(
				scheduledManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
		)
		{
			return;
		}

		if (!IsNamespaceRefactorDeferredPluginBoundaryAvailable())
			return;

		NamespaceRefactorPluginHost host = _namespaceRefactorHost;
		if (host == null)
			return;

		host.ApplyDeferredBufferRefresh(requestToken);
	}

	private void ScheduleNamespaceRefactorTargetScriptRestorationDeferred(string scriptPath)
	{
		string scheduledManagedAssemblyGeneration = ManagedAssemblyGeneration;
		CallDeferred(
			nameof(ApplyNamespaceRefactorTargetScriptRestorationDeferred),
			scheduledManagedAssemblyGeneration,
			scriptPath ?? ""
		);
	}

	private void ApplyNamespaceRefactorTargetScriptRestorationDeferred(
		string scheduledManagedAssemblyGeneration,
		string scriptPath
	)
	{
		if (
			!string.Equals(
				scheduledManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
		)
		{
			return;
		}

		if (!IsNamespaceRefactorDeferredPluginBoundaryAvailable())
			return;

		NamespaceRefactorPluginHost host = _namespaceRefactorHost;
		if (host == null)
			return;

		host.RestoreTargetScriptEditorDeferred(scriptPath);
	}

	private void ScheduleNamespaceRefactorSelectionSyncDeferred()
	{
		string scheduledManagedAssemblyGeneration = ManagedAssemblyGeneration;
		CallDeferred(
			nameof(ApplyNamespaceRefactorSelectionSyncDeferred),
			scheduledManagedAssemblyGeneration
		);
	}

	private void ApplyNamespaceRefactorSelectionSyncDeferred(
		string scheduledManagedAssemblyGeneration
	)
	{
		if (
			!string.Equals(
				scheduledManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
		)
		{
			return;
		}

		if (!IsNamespaceRefactorDeferredPluginBoundaryAvailable())
			return;

		if (_namespaceRefactorHost == null)
			return;

		SyncSelectionToActiveScriptAfterOperation();
	}

	private void ScheduleNamespaceRefactorTreeFocusReleaseDeferred()
	{
		string scheduledManagedAssemblyGeneration = ManagedAssemblyGeneration;
		CallDeferred(
			nameof(ApplyNamespaceRefactorTreeFocusReleaseDeferred),
			scheduledManagedAssemblyGeneration
		);
	}

	private void ApplyNamespaceRefactorTreeFocusReleaseDeferred(
		string scheduledManagedAssemblyGeneration
	)
	{
		if (
			!string.Equals(
				scheduledManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
		)
		{
			return;
		}

		if (!IsNamespaceRefactorDeferredPluginBoundaryAvailable())
			return;

		if (_namespaceRefactorHost == null)
			return;

		ReleaseTreeFocusAfterNavigation();
	}

	private bool IsNamespaceRefactorDeferredPluginBoundaryAvailable()
	{
		return !_editorOperationShutdownStarted
			&& GodotObject.IsInstanceValid(this)
			&& IsInsideTree();
	}

	private NamespaceRefactorPluginHost CreateNamespaceRefactorHost()
	{
		return new NamespaceRefactorPluginHost(
			_namespaceRefactorDialog,
			_namespaceRefactorIncompleteWriteReportDialog,
			_namespaceRefactorDescriptionLabel,
			_namespaceRefactorOldNamespaceLabel,
			_namespaceRefactorOldNamespaceInput,
			_namespaceRefactorNewNamespaceLabel,
			_namespaceRefactorNewNamespaceInput,
			_namespaceRefactorApplyToLabel,
			_namespaceRefactorExistingNamespaceOption,
			_namespaceRefactorExistingNamespaceDropdown,
			_namespaceRefactorWithoutNamespaceOption,
			OpenScriptEditorBufferLocator,
			OpenScriptEditorBufferAutosaveCoordinator,
			OpenScriptEditorBufferBatchService,
			() => _systems,
			GetSystemNameFromMetadata,
			GetFolderPathFromMetadata,
			GetEntryFromMetadata,
			GetScriptPathFromEntry,
			GetFolderPathFromEntry,
			SceneEntryMarker,
			EnsureSystemsLoadedForTreeOperation,
			() => EditorInterface.Singleton,
			(entry, path) => OpenMissingScriptDialog(entry, path),
			DebugLogger.Log,
			() => DebugLogger.IsEnabled,
			message => GD.PushWarning(message),
			(operation, details) => DebugLogger.LogOperation(operation, details),
			StartNamespaceRefactorForegroundOperation,
			BeginBatchScriptEditorContextPreservation,
			EndBatchScriptEditorContextPreservation,
			SyncSelectionToActiveScriptAfterOperation,
			ScheduleNamespaceRefactorIncompleteWriteReportPresentationDeferred,
			ScheduleNamespaceRefactorConfiguredDialogSizeCorrectionDeferred,
			ScheduleNamespaceRefactorDeferredBufferRefreshDispatch,
			ScheduleNamespaceRefactorTargetScriptRestorationDeferred,
			ScheduleNamespaceRefactorSelectionSyncDeferred,
			ScheduleNamespaceRefactorTreeFocusReleaseDeferred
		);
	}

	private bool TryEnsureNamespaceRefactorHost(
		out NamespaceRefactorPluginHost namespaceRefactorHost
	)
	{
		namespaceRefactorHost = null;
		List<string> invalidComponents = GetInvalidNamespaceRefactorUiComponents();

		if (invalidComponents.Count > 0)
		{
			DebugLogger.LogOperation(
				"Refactor Namespace host recovery failed: invalid UI",
				string.Join(", ", invalidComponents)
			);
			GD.PushWarning(NamespaceRefactorIntegrationWarning);
			return false;
		}

		if (
			_namespaceRefactorHost != null
			&& _namespaceRefactorHost.IsBoundTo(
				_namespaceRefactorDialog,
				_namespaceRefactorIncompleteWriteReportDialog,
				_namespaceRefactorDescriptionLabel,
				_namespaceRefactorOldNamespaceLabel,
				_namespaceRefactorOldNamespaceInput,
				_namespaceRefactorNewNamespaceLabel,
				_namespaceRefactorNewNamespaceInput,
				_namespaceRefactorApplyToLabel,
				_namespaceRefactorExistingNamespaceOption,
				_namespaceRefactorExistingNamespaceDropdown,
				_namespaceRefactorWithoutNamespaceOption
			)
		)
		{
			namespaceRefactorHost = _namespaceRefactorHost;
			return true;
		}

		try
		{
			_namespaceRefactorHost = CreateNamespaceRefactorHost();
			namespaceRefactorHost = _namespaceRefactorHost;
			DebugLogger.LogOperation(
				"Refactor Namespace host restored",
				"Rebuilt managed host and feature graph around existing plugin-owned UI."
			);
			return true;
		}
		catch (Exception exception)
		{
			_namespaceRefactorHost = null;
			DebugLogger.LogOperation(
				"Refactor Namespace host recovery failed: composition",
				exception.ToString()
			);
			GD.PushWarning(NamespaceRefactorIntegrationWarning);
			return false;
		}
	}

	private List<string> GetInvalidNamespaceRefactorUiComponents()
	{
		var invalidComponents = new List<string>();
		AddInvalidGodotObject(invalidComponents, nameof(_namespaceRefactorDialog), _namespaceRefactorDialog);
		AddInvalidGodotObject(invalidComponents, nameof(_namespaceRefactorIncompleteWriteReportDialog), _namespaceRefactorIncompleteWriteReportDialog);
		AddInvalidGodotObject(invalidComponents, nameof(_namespaceRefactorDescriptionLabel), _namespaceRefactorDescriptionLabel);
		AddInvalidGodotObject(invalidComponents, nameof(_namespaceRefactorNewNamespaceLabel), _namespaceRefactorNewNamespaceLabel);
		AddInvalidGodotObject(invalidComponents, nameof(_namespaceRefactorNewNamespaceInput), _namespaceRefactorNewNamespaceInput);
		AddInvalidGodotObject(invalidComponents, nameof(_namespaceRefactorOldNamespaceLabel), _namespaceRefactorOldNamespaceLabel);
		AddInvalidGodotObject(invalidComponents, nameof(_namespaceRefactorOldNamespaceInput), _namespaceRefactorOldNamespaceInput);
		AddInvalidGodotObject(invalidComponents, nameof(_namespaceRefactorApplyToLabel), _namespaceRefactorApplyToLabel);
		AddInvalidGodotObject(invalidComponents, nameof(_namespaceRefactorExistingNamespaceOption), _namespaceRefactorExistingNamespaceOption);
		AddInvalidGodotObject(invalidComponents, nameof(_namespaceRefactorExistingNamespaceDropdown), _namespaceRefactorExistingNamespaceDropdown);
		AddInvalidGodotObject(invalidComponents, nameof(_namespaceRefactorWithoutNamespaceOption), _namespaceRefactorWithoutNamespaceOption);
		return invalidComponents;
	}

	private static void AddInvalidGodotObject(
		List<string> invalidComponents,
		string componentName,
		GodotObject component
	)
	{
		if (component == null)
		{
			invalidComponents.Add($"{componentName}=null");
			return;
		}

		if (!GodotObject.IsInstanceValid(component))
			invalidComponents.Add($"{componentName}=invalid");
	}

	private bool TryOpenNamespaceRefactorDialog(string metadata)
	{
		if (
			string.IsNullOrWhiteSpace(metadata)
			|| !EnableQuickActions
			|| _isBeautifyingScript
		)
		{
			return false;
		}

		bool isSupportedTarget =
			metadata.StartsWith("system::", StringComparison.Ordinal)
			|| metadata.StartsWith("folder::", StringComparison.Ordinal)
			|| metadata.StartsWith("script::", StringComparison.Ordinal);

		if (!isSupportedTarget)
			return false;

		if (!EnsureManagedAssemblyStateCurrent("Open Refactor Namespace"))
			return false;

		if (!TryEnsureNamespaceRefactorHost(out NamespaceRefactorPluginHost host))
			return false;

		host.Open(metadata);
		return true;
	}

	private bool TryOpenNamespaceRefactorDialogForSelectedItem()
	{
		if (
			_tree == null
			|| !GodotObject.IsInstanceValid(_tree)
			|| _tree.IsQueuedForDeletion()
		)
		{
			return false;
		}

		TreeItem selectedItem = _tree.GetSelected();

		if (selectedItem == null || !GodotObject.IsInstanceValid(selectedItem))
			return false;

		string metadata = selectedItem.GetMetadata(0).AsString();
		bool isSystem = metadata.StartsWith("system::", StringComparison.Ordinal);
		bool isFolder = metadata.StartsWith("folder::", StringComparison.Ordinal);
		bool isScript = metadata.StartsWith("script::", StringComparison.Ordinal);

		if (!isSystem && !isFolder && !isScript)
			return false;

		if ((isSystem || isFolder) && !TreeItemSubtreeContainsScript(selectedItem))
			return false;

		return TryOpenNamespaceRefactorDialog(metadata);
	}

	private void OnNamespaceRefactorConfirmed()
	{
		if (!EnsureManagedAssemblyStateCurrent("Confirm Refactor Namespace"))
			return;

		if (TryEnsureNamespaceRefactorHost(out NamespaceRefactorPluginHost host))
			host.ConfirmDialog();
	}

	private void OnNamespaceRefactorDialogWindowInput(InputEvent inputEvent)
	{
		if (!EnsureManagedAssemblyStateCurrent("Refactor Namespace Dialog Input"))
			return;

		if (!IsEnterPressed(inputEvent))
			return;

		ConfirmNamespaceRefactorDialogFromEnter();
	}

	private void OnNamespaceRefactorOldNamespaceSubmitted(string _)
	{
		if (EnsureManagedAssemblyStateCurrent("Submit Old Namespace"))
			ConfirmNamespaceRefactorDialogFromEnter();
	}

	private void OnNamespaceRefactorNewNamespaceSubmitted(string _)
	{
		if (EnsureManagedAssemblyStateCurrent("Submit New Namespace"))
			ConfirmNamespaceRefactorDialogFromEnter();
	}

	private void ConfirmNamespaceRefactorDialogFromEnter()
	{
		if (
			_namespaceRefactorDialog == null
			|| !GodotObject.IsInstanceValid(_namespaceRefactorDialog)
			|| !_namespaceRefactorDialog.Visible
		)
		{
			return;
		}

		if (!TryEnsureNamespaceRefactorHost(out NamespaceRefactorPluginHost host))
			return;

		_namespaceRefactorDialog.Hide();
		host.ConfirmDialog();
	}

	private void OnNamespaceRefactorExistingNamespaceOptionToggled(bool pressed)
	{
		if (!EnsureManagedAssemblyStateCurrent("Select Existing Namespace Mode"))
			return;

		if (!pressed)
			return;

		if (TryEnsureNamespaceRefactorHost(out NamespaceRefactorPluginHost host))
			host.SetBatchApplyMode(true);
	}

	private void OnNamespaceRefactorExistingNamespaceSelected(long index)
	{
		if (!EnsureManagedAssemblyStateCurrent("Select Existing Namespace"))
			return;

		if (TryEnsureNamespaceRefactorHost(out NamespaceRefactorPluginHost host))
			host.SelectExistingNamespace(index);
	}

	private void OnNamespaceRefactorWithoutNamespaceOptionToggled(bool pressed)
	{
		if (!EnsureManagedAssemblyStateCurrent("Select Without Namespace Mode"))
			return;

		if (!pressed)
			return;

		if (TryEnsureNamespaceRefactorHost(out NamespaceRefactorPluginHost host))
			host.SetBatchApplyMode(false);
	}
	#endregion
}
#endif
