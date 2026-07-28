#if TOOLS
using Godot;
using System;
using System.Collections.Generic;

public partial class SystemExplorerPlugin
{
	#region Managed Assembly Reload Lifecycle
	private const string ManagedAssemblyRecoveryDeduplicationKey =
		"ManagedAssemblyRecovery";
	private const string ManagedAssemblyRecoveryFailureTitle =
		"System Explorer Reload Failed";
	private const string ManagedAssemblyRecoveryFailureMessage =
		"System Explorer could not restore its editor integration after the C# assembly reload. Fix the reported persistent-state or editor-integration problem, then build again or restart Godot.";

	private static readonly string ManagedAssemblyGeneration =
		Guid.NewGuid().ToString("N");

	private const int ManagedAssemblyRecoveryMaximumDeferredAttempts = 3;

	private enum ManagedAssemblyRecoveryState
	{
		NotQueued,
		Queued,
		Recovering,
		Completed,
		PermanentlyFailed,
	}

	private string _loadedPersistentTreeStateGeneration = "";
	private string _reportedManagedAssemblyRecoveryFailureGeneration = "";
	private bool _isRecoveringManagedAssemblyState;
	private ManagedAssemblyRecoveryState _managedAssemblyRecoveryState;
	private int _managedAssemblyRecoveryDeferredAttempts;
	private string _managedAssemblyRecoveryReason = "";

	private bool InitializePersistentTreeStateForCurrentAssembly(string reason)
	{
		try
		{
			if (!TryReadAndCommitPersistentTreeStateFromDisk(reason, out string failureDetail))
			{
				ReportManagedAssemblyRecoveryFailure(reason, failureDetail);
				return false;
			}

			_loadedPersistentTreeStateGeneration = ManagedAssemblyGeneration;
			ClearManagedAssemblyRecoveryFailure();
			return true;
		}
		catch (Exception exception)
		{
			ReportManagedAssemblyRecoveryFailure(
				reason,
				$"Unexpected startup state exception: {exception}"
			);
			return false;
		}
	}

	private bool EnsureManagedAssemblyStateCurrent(string reason)
	{
		if (
			string.Equals(
				_loadedPersistentTreeStateGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
			&& VerifyCriticalManagedAssemblySignals()
		)
		{
			EnsureEditorOperationLifecycleCurrentForManagedAssembly();
			_managedAssemblyRecoveryState = ManagedAssemblyRecoveryState.Completed;
			return true;
		}

		if (_managedAssemblyRecoveryState == ManagedAssemblyRecoveryState.PermanentlyFailed)
			return false;

		if (_isRecoveringManagedAssemblyState)
			return false;

		if (TryRecoverManagedAssemblyEditorIntegration(reason, out string failureDetail))
			return true;

		if (_managedAssemblyRecoveryState == ManagedAssemblyRecoveryState.PermanentlyFailed)
			return false;

		QueueManagedAssemblyRecovery(reason, failureDetail);
		return false;
	}

	private bool TryRecoverManagedAssemblyEditorIntegration(string reason, out string failureDetail)
	{
		failureDetail = "";
		_isRecoveringManagedAssemblyState = true;
		_managedAssemblyRecoveryState = ManagedAssemblyRecoveryState.Recovering;

		try
		{
			PrepareTreeStatePersistenceForManagedAssemblyRecovery();

			if (!TryReadAndCommitPersistentTreeStateFromDisk(reason, out string stateFailureDetail))
			{
				failureDetail = stateFailureDetail;
				_managedAssemblyRecoveryState = ManagedAssemblyRecoveryState.PermanentlyFailed;
				ReportManagedAssemblyRecoveryFailure(reason, failureDetail);
				return false;
			}

			ResetManagedAssemblyTransientStateAfterReload();
			EnsureEditorOperationLifecycleCurrentForManagedAssembly();

			if (TryRecoverExistingManagedAssemblyEditorIntegration(out string reconnectFailure))
				return CompleteManagedAssemblyRecovery(reason, "reconnected existing editor integration");

			if (TryRebuildManagedAssemblyEditorIntegration(out string rebuildFailure))
				return CompleteManagedAssemblyRecovery(reason, "rebuilt editor integration");

			failureDetail = $"Reconnect='{reconnectFailure}', Rebuild='{rebuildFailure}'";
			return false;
		}
		catch (Exception exception)
		{
			failureDetail = $"Unexpected recovery exception: {exception}";
			return false;
		}
		finally
		{
			_isRecoveringManagedAssemblyState = false;
		}
	}

	private bool CompleteManagedAssemblyRecovery(string reason, string strategy)
	{
		_loadedPersistentTreeStateGeneration = ManagedAssemblyGeneration;
		_managedAssemblyRecoveryState = ManagedAssemblyRecoveryState.Completed;
		_managedAssemblyRecoveryDeferredAttempts = 0;
		_managedAssemblyRecoveryReason = "";
		ClearManagedAssemblyRecoveryFailure();
		BuildTree(keepCurrentExpansionState: true);
		RestorePersistentTreeSelectionBestEffort(reason);
		CallDeferred(nameof(MakeSystemExplorerDockVisible));
		DebugLogger.LogOperation("Managed assembly recovery completed", $"Reason='{reason}', Strategy='{strategy}'");
		return true;
	}

	private bool TryRecoverExistingManagedAssemblyEditorIntegration(out string failureDetail)
	{
		failureDetail = "";
		if (!ValidateManagedAssemblyUiReferences(out failureDetail))
			return false;
		if (!EnsureManagedAssemblySignalIntegrationsCurrent(out failureDetail))
			return false;
		if (!VerifyCriticalManagedAssemblySignals())
		{
			failureDetail = "Critical dock or context-menu signals are not connected to the current managed plugin instance.";
			return false;
		}
		return true;
	}

	private bool TryRebuildManagedAssemblyEditorIntegration(out string failureDetail)
	{
		failureDetail = "";
		try
		{
			ShutdownScriptEditorSync();
			ShutdownFolderBindingFilesystemLifecycle();
			DisconnectNamespaceRefactorDialogSignals();
			DisconnectDockSignals();
			_namespaceRefactorHost = null;

			if (IsValidGodotObject(_editorDock))
			{
				if (_editorDock.IsInsideTree()) RemoveDock(_editorDock);
				_editorDock.QueueFree();
			}
			else if (IsValidGodotObject(_dock))
			{
				_dock.QueueFree();
			}

			_editorDock = null;
			_dock = null;
			ClearDockControlReferences();
			BuildDock();
			_editorDock = new EditorDock { Title = "System Explorer", DefaultSlot = EditorDock.DockSlot.RightUl };
			_editorDock.AddChild(_dock);
			AddDock(_editorDock);

			if (!EnsureManagedAssemblySignalIntegrationsCurrent(out failureDetail))
				return false;
			if (!VerifyCriticalManagedAssemblySignals())
			{
				failureDetail = "Critical signals were not current after dock rebuild.";
				return false;
			}

			return true;
		}
		catch (Exception exception)
		{
			failureDetail = $"Dock rebuild exception: {exception}";
			return false;
		}
	}

	private bool VerifyCriticalManagedAssemblySignals()
	{
		return IsPluginSignalConnected(_tree, Control.SignalName.GuiInput, nameof(OnTreeGuiInputSignal))
			&& IsPluginSignalConnected(_tree, Tree.SignalName.ItemSelected, nameof(OnItemSelectedSignal))
			&& IsPluginSignalConnected(_contextMenu, PopupMenu.SignalName.IdPressed, nameof(OnContextMenuIdPressedSignal))
			&& IsPluginSignalConnected(_contextNewSubmenu, PopupMenu.SignalName.IdPressed, nameof(OnContextMenuIdPressedSignal))
			&& IsPluginSignalConnected(_contextAddSubmenu, PopupMenu.SignalName.IdPressed, nameof(OnContextMenuIdPressedSignal))
			&& IsPluginSignalConnected(_contextQuickActionsSubmenu, PopupMenu.SignalName.IdPressed, nameof(OnContextMenuIdPressedSignal));
	}

	private void QueueManagedAssemblyRecovery(string reason, string failureDetail)
	{
		_managedAssemblyRecoveryReason = reason ?? "Managed Assembly Recovery";
		if (_managedAssemblyRecoveryState == ManagedAssemblyRecoveryState.Queued)
			return;
		_managedAssemblyRecoveryState = ManagedAssemblyRecoveryState.Queued;
		DebugLogger.LogOperation("Managed assembly recovery deferred", failureDetail);
		CallDeferred(nameof(RunDeferredManagedAssemblyRecovery));
	}

	private void RunDeferredManagedAssemblyRecovery()
	{
		if (_managedAssemblyRecoveryState != ManagedAssemblyRecoveryState.Queued)
			return;
		_managedAssemblyRecoveryDeferredAttempts++;
		if (TryRecoverManagedAssemblyEditorIntegration(_managedAssemblyRecoveryReason, out string failureDetail))
			return;
		if (_managedAssemblyRecoveryState == ManagedAssemblyRecoveryState.PermanentlyFailed)
			return;
		if (_managedAssemblyRecoveryDeferredAttempts < ManagedAssemblyRecoveryMaximumDeferredAttempts)
		{
			_managedAssemblyRecoveryState = ManagedAssemblyRecoveryState.Queued;
			CallDeferred(nameof(RunDeferredManagedAssemblyRecovery));
			return;
		}
		_managedAssemblyRecoveryState = ManagedAssemblyRecoveryState.PermanentlyFailed;
		ReportManagedAssemblyRecoveryFailure(_managedAssemblyRecoveryReason, failureDetail);
	}

	private bool TryReadAndCommitPersistentTreeStateFromDisk(
		string reason,
		out string failureDetail
	)
	{
		failureDetail = "";
		SystemsFileReadResult systemsReadResult = ReadSystemsFileFromDisk();

		if (
			systemsReadResult.Status != SystemsFileReadStatus.Missing
			&& !systemsReadResult.IsValid
		)
		{
			failureDetail =
				$"Reason='{reason}', File='systems.json', Status='{systemsReadResult.Status}', Detail='{systemsReadResult.FailureDetail}'";
			return false;
		}

		Dictionary<string, List<string>> validatedSystems =
			CreateNormalizedSystemsCopy(
				systemsReadResult.Status == SystemsFileReadStatus.Missing
					? null
					: systemsReadResult.Systems
			);

		FolderBindingsFileReadResult folderBindingsReadResult =
			ReadFolderBindingsFileFromDisk(validatedSystems);

		if (!folderBindingsReadResult.IsValid)
		{
			failureDetail =
				$"Reason='{reason}', File='folder_bindings.json', Status='{folderBindingsReadResult.Status}', Detail='{folderBindingsReadResult.FailureDetail}'";
			return false;
		}

		CommitPersistentTreeState(
			validatedSystems,
			folderBindingsReadResult.FolderBindings
		);
		LoadPersistentTreeStateBestEffort(reason);

		DebugLogger.LogOperation(
			"Persistent tree state loaded",
			$"Reason='{reason}', SystemsStatus='{systemsReadResult.Status}', FolderBindingsStatus='{folderBindingsReadResult.Status}', Systems={validatedSystems.Count}, FolderBindings={CountFolderBindings(folderBindingsReadResult.FolderBindings)}"
		);
		return true;
	}

	private static Dictionary<string, List<string>> CreateNormalizedSystemsCopy(
		Dictionary<string, List<string>> systems
	)
	{
		var normalizedSystems = new Dictionary<string, List<string>>(StringComparer.Ordinal);

		if (systems == null)
			return normalizedSystems;

		foreach (KeyValuePair<string, List<string>> system in systems)
		{
			normalizedSystems[system.Key] = NormalizeSystemEntries(
				system.Value == null ? new List<string>() : new List<string>(system.Value)
			);
		}

		return normalizedSystems;
	}

	private void CommitPersistentTreeState(
		Dictionary<string, List<string>> systems,
		Dictionary<string, Dictionary<string, string>> folderBindings
	)
	{
		_systems.Clear();

		foreach (KeyValuePair<string, List<string>> system in systems)
			_systems[system.Key] = new List<string>(system.Value);

		_folderBindings.Clear();

		foreach (
			KeyValuePair<string, Dictionary<string, string>> systemBinding in folderBindings
		)
		{
			_folderBindings[systemBinding.Key] = new Dictionary<string, string>(
				systemBinding.Value,
				StringComparer.Ordinal
			);
		}
	}

	private bool ValidateManagedAssemblyUiReferences(out string failureDetail)
	{
		failureDetail = "";

		if (!AreDockSignalSourcesValid(out failureDetail))
			return false;

		if (!IsValidGodotObject(_treeOperationDialog))
		{
			failureDetail = "The shared tree-operation dialog is unavailable.";
			return false;
		}

		if (!AreNamespaceRefactorSignalSourcesValid(out failureDetail))
			return false;

		if (!IsValidGodotObject(_dock) || !IsValidGodotObject(_tree))
		{
			failureDetail = "The existing System Explorer dock or tree is unavailable.";
			return false;
		}

		return true;
	}

	private bool EnsureManagedAssemblySignalIntegrationsCurrent(
		out string failureDetail
	)
	{
		failureDetail = "";

		try
		{
			if (!ConnectDockSignals())
			{
				failureDetail = "One or more dock signal connections could not be restored.";
				return false;
			}

			if (!ConnectTreeOperationDialogSignals())
			{
				failureDetail = "The tree-operation dialog signals could not be restored.";
				return false;
			}

			if (!ConnectNamespaceRefactorDialogSignals())
			{
				failureDetail = "The Refactor Namespace dialog signals could not be restored.";
				return false;
			}

			if (!InitializeFolderBindingFilesystemLifecycle())
			{
				failureDetail = "The EditorFileSystem signal could not be restored.";
				return false;
			}

			if (!EnsureScriptEditorSyncLifecycleCurrent())
			{
				failureDetail = "The ScriptEditorSync integration could not be restored.";
				return false;
			}

			if (!TryEnsureNamespaceRefactorHost(out _))
			{
				failureDetail = "The Refactor Namespace managed host could not be restored.";
				return false;
			}

			return true;
		}
		catch (Exception exception)
		{
			failureDetail = $"Unexpected signal-integration exception: {exception}";
			return false;
		}
	}

	private void ResetManagedAssemblyTransientStateAfterReload()
	{
		RecoverEditorOperationBusyCursorAfterManagedAssemblyReload();
		_renameFilesystemFinalStateRefreshQueued = false;
		_boundFolderSyncQueued = false;
		_boundFolderSyncRunning = false;
		ResetScriptEditorSyncTransientStateAfterManagedAssemblyReload();
		CancelPendingScriptRenameEditorRestore();
		ResetTreeOperationDialogQueuedStateAfterManagedAssemblyReload();
		ResetUnsafePendingTreeOperationsAfterManagedAssemblyReload();
	}

	private void ResetUnsafePendingTreeOperationsAfterManagedAssemblyReload()
	{
		_pendingRemoveMetadata = "";
		_pendingRenameMetadata = "";
		_pendingAddFolderMetadata = "";
		_pendingFolderBindingMetadata = "";
		_pendingShowInFileManagerMetadata = "";
		_pendingBeautifyScriptMetadata = "";
		_pendingBeautifyAfterCSharpierInstallMetadata = "";
		_pendingBeautifyAfterCSharpierInstallScriptPaths = Array.Empty<string>();
		_pendingBeautifyAfterCSharpierInstallIsBatch = false;
		_pendingBeautifyAfterCSharpierInstallReleaseTreeFocusAfterNavigation = true;
		_pendingMissingScriptEntry = "";
		_pendingMissingScriptPath = "";
		_pendingSceneLinkEntry = "";
		_pendingMissingSceneEntry = "";
		_pendingMissingScenePath = "";
		_pendingRemoveScriptOccurrence = null;
		_pendingScriptRenameTreeState = null;
		_pendingSceneLinkSourceOccurrence = null;
		_pendingMissingSceneScriptOccurrence = null;
		_isRenameNameConflictPopupPending = false;
		_isAddFolderConflictPopupPending = false;
		_isAddSystemConflictPopupPending = false;
		_namespaceRefactorHost = null;

		HideWindowForManagedAssemblyReload(_removeDialog);
		HideWindowForManagedAssemblyReload(_renameNameConflictDialog);
		HideWindowForManagedAssemblyReload(_renameDialog);
		HideWindowForManagedAssemblyReload(_addFolderConflictDialog);
		HideWindowForManagedAssemblyReload(_addFolderDialog);
		HideWindowForManagedAssemblyReload(_addSystemConflictDialog);
		HideWindowForManagedAssemblyReload(_fileDialog);
		HideWindowForManagedAssemblyReload(_folderBindingDialog);
		HideWindowForManagedAssemblyReload(_createScriptDialog);
		HideWindowForManagedAssemblyReload(_relinkScriptDialog);
		HideWindowForManagedAssemblyReload(_linkSceneDialog);
		HideWindowForManagedAssemblyReload(_addSceneDialog);
		HideWindowForManagedAssemblyReload(_relinkSceneDialog);
		HideWindowForManagedAssemblyReload(_missingScriptDialog);
		HideWindowForManagedAssemblyReload(_missingSceneDialog);
		HideWindowForManagedAssemblyReload(_namespaceRefactorDialog);
		HideWindowForManagedAssemblyReload(_csharpierNotInstalledDialog);
	}

	private static void HideWindowForManagedAssemblyReload(Window window)
	{
		if (IsValidGodotObject(window))
			window.Hide();
	}

	private void ReportManagedAssemblyRecoveryFailure(string reason, string technicalDetail)
	{
		if (
			string.Equals(
				_reportedManagedAssemblyRecoveryFailureGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
		)
		{
			return;
		}

		_reportedManagedAssemblyRecoveryFailureGeneration = ManagedAssemblyGeneration;
		string details = $"Reason='{reason}', {technicalDetail}";

		if (IsValidGodotObject(_treeOperationDialog))
		{
			ConnectTreeOperationDialogSignals();
			QueueStandaloneTreeOperationDialog(
				ManagedAssemblyRecoveryFailureTitle,
				ManagedAssemblyRecoveryFailureMessage,
				details,
				ManagedAssemblyRecoveryDeduplicationKey
			);
		}
		else
		{
			DebugLogger.LogOperation(ManagedAssemblyRecoveryFailureTitle, details);
		}
	}

	private void ClearManagedAssemblyRecoveryFailure()
	{
		_reportedManagedAssemblyRecoveryFailureGeneration = "";
		ClearPersistentTreeOperationFailure(ManagedAssemblyRecoveryDeduplicationKey);
	}

	private bool TryConnectPluginSignal(
		GodotObject source,
		StringName signalName,
		string methodName,
		string sourceDescription
	)
	{
		if (!IsValidGodotObject(source))
		{
			DebugLogger.LogOperation(
				"Signal connect failed: invalid source",
				$"Source='{sourceDescription}', Signal='{signalName}', Method='{methodName}'"
			);
			return false;
		}

		Callable callable = new(this, methodName);

		try
		{
			if (source.IsConnected(signalName, callable))
				return true;

			Error error = source.Connect(signalName, callable);

			if (error == Error.Ok)
				return true;

			DebugLogger.LogOperation(
				"Signal connect failed",
				$"Source='{sourceDescription}', Signal='{signalName}', Method='{methodName}', Error='{error}'"
			);
			return false;
		}
		catch (Exception exception)
		{
			DebugLogger.LogOperation(
				"Signal connect threw",
				$"Source='{sourceDescription}', Signal='{signalName}', Method='{methodName}', Exception='{exception}'"
			);
			return false;
		}
	}

	private bool IsPluginSignalConnected(
		GodotObject source,
		StringName signalName,
		string methodName
	)
	{
		if (!IsValidGodotObject(source))
			return false;

		try
		{
			return source.IsConnected(signalName, new Callable(this, methodName));
		}
		catch (Exception exception)
		{
			DebugLogger.LogOperation(
				"Signal status check threw",
				$"Signal='{signalName}', Method='{methodName}', Exception='{exception}'"
			);
			return false;
		}
	}

	private void DisconnectPluginSignal(
		GodotObject source,
		StringName signalName,
		string methodName,
		string sourceDescription
	)
	{
		if (!IsValidGodotObject(source))
			return;

		Callable callable = new(this, methodName);

		try
		{
			if (source.IsConnected(signalName, callable))
				source.Disconnect(signalName, callable);
		}
		catch (Exception exception)
		{
			DebugLogger.LogOperation(
				"Signal disconnect threw",
				$"Source='{sourceDescription}', Signal='{signalName}', Method='{methodName}', Exception='{exception}'"
			);
		}
	}

	private static bool IsValidGodotObject(GodotObject source)
	{
		return source != null && GodotObject.IsInstanceValid(source);
	}

	private static int CountFolderBindings(
		Dictionary<string, Dictionary<string, string>> folderBindings
	)
	{
		int count = 0;

		foreach (Dictionary<string, string> bindings in folderBindings.Values)
			count += bindings?.Count ?? 0;

		return count;
	}
	#endregion
}
#endif
