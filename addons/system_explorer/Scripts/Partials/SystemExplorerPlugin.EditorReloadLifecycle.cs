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
		"System Explorer could not safely restore its editor state after the C# assembly reload. The existing tree was left unchanged. Fix the reported problem, then build again or restart Godot.";

	private static readonly string ManagedAssemblyGeneration =
		Guid.NewGuid().ToString("N");

	private string _loadedPersistentTreeStateGeneration = "";
	private string _reportedManagedAssemblyRecoveryFailureGeneration = "";
	private bool _isRecoveringManagedAssemblyState;

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
		)
		{
			return true;
		}

		if (_isRecoveringManagedAssemblyState)
		{
			DebugLogger.LogOperation(
				"Managed assembly recovery skipped: reentrant call",
				$"Reason='{reason}'"
			);
			return false;
		}

		_isRecoveringManagedAssemblyState = true;

		try
		{
			DebugLogger.LogOperation(
				"Managed assembly recovery requested",
				$"Reason='{reason}', LoadedGeneration='{_loadedPersistentTreeStateGeneration}', CurrentGeneration='{ManagedAssemblyGeneration}'"
			);

			if (!ValidateManagedAssemblyUiReferences(out string uiFailureDetail))
			{
				ReportManagedAssemblyRecoveryFailure(reason, uiFailureDetail);
				return false;
			}

			if (!TryReadAndCommitPersistentTreeStateFromDisk(reason, out string stateFailureDetail))
			{
				ReportManagedAssemblyRecoveryFailure(reason, stateFailureDetail);
				return false;
			}

			ResetManagedAssemblyTransientStateAfterReload();

			if (
				!EnsureManagedAssemblySignalIntegrationsCurrent(
					out string signalFailureDetail
				)
			)
			{
				ReportManagedAssemblyRecoveryFailure(reason, signalFailureDetail);
				return false;
			}

			_loadedPersistentTreeStateGeneration = ManagedAssemblyGeneration;
			ClearManagedAssemblyRecoveryFailure();

			DebugLogger.LogOperation(
				"Managed assembly recovery completed",
				$"Reason='{reason}', Generation='{ManagedAssemblyGeneration}', Systems={_systems.Count}, FolderBindings={CountFolderBindings(_folderBindings)}"
			);
			DebugLogStateSnapshot("Managed Assembly Recovery Completed");
			return true;
		}
		catch (Exception exception)
		{
			ReportManagedAssemblyRecoveryFailure(
				reason,
				$"Unexpected recovery exception: {exception}"
			);
			return false;
		}
		finally
		{
			_isRecoveringManagedAssemblyState = false;
		}
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
			GD.PushWarning(ManagedAssemblyRecoveryFailureMessage);
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
