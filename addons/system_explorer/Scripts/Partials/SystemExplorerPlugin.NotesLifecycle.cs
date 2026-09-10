#if TOOLS
using System;
using SystemExplorer.Notes;

public partial class SystemExplorerPlugin
{
	private NoteLifecycleStore _noteLifecycleStore;

	private bool TryGetNoteLifecycleStore(
		out NoteLifecycleStore lifecycleStore,
		out string failureDetail
	)
	{
		lifecycleStore = _noteLifecycleStore;
		failureDetail = "";

		if (lifecycleStore != null)
			return true;

		if (!TryGetAbsoluteNotesDirectoryPath(out string absoluteNotesPath, out failureDetail))
			return false;

		try
		{
			_noteLifecycleStore = new NoteLifecycleStore(absoluteNotesPath);
			lifecycleStore = _noteLifecycleStore;
			return true;
		}
		catch (Exception exception)
		{
			failureDetail =
				$"Could not compose the Notes lifecycle store for '{absoluteNotesPath}'. Exception='{exception}'";
			return false;
		}
	}

	private bool TryApplyReversibleNoteSystemRename(
		string oldSystemName,
		string newSystemName,
		SystemsAndFolderBindingsSnapshot metadataSnapshot,
		out NoteLifecycleMutation mutation
	)
	{
		mutation = NoteLifecycleMutation.NoOp;

		if (!TryGetNoteLifecycleStore(out NoteLifecycleStore lifecycleStore, out string compositionFailureDetail))
		{
			RestoreSystemsAndFolderBindingsSnapshot(metadataSnapshot);
			ReportReversibleNoteLifecycleApplyFailure(
				"rename",
				$"System rename '{oldSystemName}' -> '{newSystemName}'",
				compositionFailureDetail,
				finalStateUnclear: false
			);
			return false;
		}

		if (
			!lifecycleStore.TryApplySystemRename(
				oldSystemName,
				newSystemName,
				out mutation,
				out bool finalStateUnclear,
				out string failureDetail
			)
		)
		{
			RestoreSystemsAndFolderBindingsSnapshot(metadataSnapshot);
			ReportReversibleNoteLifecycleApplyFailure(
				"rename",
				$"System rename '{oldSystemName}' -> '{newSystemName}'",
				failureDetail,
				finalStateUnclear
			);
			return false;
		}

		DebugLogger.LogOperation(
			"Note lifecycle System rename applied",
			$"OldSystemName='{oldSystemName}', NewSystemName='{newSystemName}', DiskMutationApplied={!mutation.IsNoOp}"
		);
		return true;
	}

	private bool TryApplyReversibleNoteFolderRename(
		string systemName,
		string oldFolderPath,
		string newFolderPath,
		SystemsAndFolderBindingsSnapshot metadataSnapshot,
		out NoteLifecycleMutation mutation
	)
	{
		mutation = NoteLifecycleMutation.NoOp;

		if (!TryGetNoteLifecycleStore(out NoteLifecycleStore lifecycleStore, out string compositionFailureDetail))
		{
			RestoreSystemsAndFolderBindingsSnapshot(metadataSnapshot);
			ReportReversibleNoteLifecycleApplyFailure(
				"rename",
				$"Folder rename System='{systemName}', OldFolderPath='{oldFolderPath}', NewFolderPath='{newFolderPath}'",
				compositionFailureDetail,
				finalStateUnclear: false
			);
			return false;
		}

		if (
			!lifecycleStore.TryApplyFolderRename(
				systemName,
				oldFolderPath,
				newFolderPath,
				out mutation,
				out bool finalStateUnclear,
				out string failureDetail
			)
		)
		{
			RestoreSystemsAndFolderBindingsSnapshot(metadataSnapshot);
			ReportReversibleNoteLifecycleApplyFailure(
				"rename",
				$"Folder rename System='{systemName}', OldFolderPath='{oldFolderPath}', NewFolderPath='{newFolderPath}'",
				failureDetail,
				finalStateUnclear
			);
			return false;
		}

		DebugLogger.LogOperation(
			"Note lifecycle Folder rename applied",
			$"SystemName='{systemName}', OldFolderPath='{oldFolderPath}', NewFolderPath='{newFolderPath}', DiskMutationApplied={!mutation.IsNoOp}"
		);
		return true;
	}

	private bool TryApplyReversibleNoteRemove(
		string metadata,
		SystemsAndFolderBindingsSnapshot metadataSnapshot,
		out NoteLifecycleMutation mutation
	)
	{
		mutation = NoteLifecycleMutation.NoOp;

		bool removesSystem = metadata.StartsWith("system::", StringComparison.Ordinal);
		bool removesFolder = metadata.StartsWith("folder::", StringComparison.Ordinal);

		if (!removesSystem && !removesFolder)
			return true;

		string systemName = GetSystemNameFromMetadata(metadata);
		string folderPath = removesFolder ? GetFolderPathFromMetadata(metadata) : "";

		if (!TryGetNoteLifecycleStore(out NoteLifecycleStore lifecycleStore, out string compositionFailureDetail))
		{
			RestoreSystemsAndFolderBindingsSnapshot(metadataSnapshot);
			ReportReversibleNoteLifecycleApplyFailure(
				"remove",
				BuildNoteRemoveTargetDescription(systemName, folderPath),
				compositionFailureDetail,
				finalStateUnclear: false
			);
			return false;
		}

		bool applied;
		bool finalStateUnclear;
		string failureDetail;

		if (removesSystem)
		{
			applied = lifecycleStore.TryApplySystemRemove(
				systemName,
				out mutation,
				out finalStateUnclear,
				out failureDetail
			);
		}
		else
		{
			applied = lifecycleStore.TryApplyFolderRemove(
				systemName,
				folderPath,
				out mutation,
				out finalStateUnclear,
				out failureDetail
			);
		}

		if (!applied)
		{
			RestoreSystemsAndFolderBindingsSnapshot(metadataSnapshot);
			ReportReversibleNoteLifecycleApplyFailure(
				"remove",
				BuildNoteRemoveTargetDescription(systemName, folderPath),
				failureDetail,
				finalStateUnclear
			);
			return false;
		}

		DebugLogger.LogOperation(
			removesSystem
				? "Note lifecycle System remove applied"
				: "Note lifecycle Folder remove applied",
			removesSystem
				? $"SystemName='{systemName}', DiskMutationApplied={!mutation.IsNoOp}"
				: $"SystemName='{systemName}', FolderPath='{folderPath}', DiskMutationApplied={!mutation.IsNoOp}"
		);
		return true;
	}

	private void RollbackNoteLifecycleAfterReversibleMetadataPersistenceFailure(
		NoteLifecycleMutation mutation,
		string operationName,
		string targetDescription
	)
	{
		if (mutation == null || mutation.IsNoOp)
		{
			DebugLogger.LogOperation(
				"Note lifecycle rollback completed",
				$"Operation='{operationName}', Target={targetDescription}, RollbackRequired=false"
			);
			return;
		}

		if (!TryGetNoteLifecycleStore(out NoteLifecycleStore lifecycleStore, out string compositionFailureDetail))
		{
			ReportNoteLifecycleRollbackFailure(
				operationName,
				targetDescription,
				compositionFailureDetail
			);
			return;
		}

		if (
			lifecycleStore.TryRollback(
				mutation,
				out _,
				out string rollbackFailureDetail
			)
		)
		{
			DebugLogger.LogOperation(
				"Note lifecycle rollback completed",
				$"Operation='{operationName}', Target={targetDescription}, RollbackRequired=true"
			);
			return;
		}

		ReportNoteLifecycleRollbackFailure(
			operationName,
			targetDescription,
			rollbackFailureDetail
		);
	}

	private void ApplyPhysicalRemoveNoteCleanup(string removeMetadata)
	{
		bool removesSystem = removeMetadata.StartsWith("system::", StringComparison.Ordinal);
		bool removesFolder = removeMetadata.StartsWith("folder::", StringComparison.Ordinal);

		if (!removesSystem && !removesFolder)
			return;

		string systemName = GetSystemNameFromMetadata(removeMetadata);
		string folderPath = removesFolder ? GetFolderPathFromMetadata(removeMetadata) : "";
		string targetDescription = BuildNoteRemoveTargetDescription(systemName, folderPath);

		if (!TryGetNoteLifecycleStore(out NoteLifecycleStore lifecycleStore, out string compositionFailureDetail))
		{
			ReportPhysicalRemoveNoteCleanupFailure(
				targetDescription,
				compositionFailureDetail,
				finalStateUnclear: false
			);
			return;
		}

		NoteLifecycleMutation ignoredMutation;
		bool applied;
		bool finalStateUnclear;
		string failureDetail;

		if (removesSystem)
		{
			applied = lifecycleStore.TryApplySystemRemove(
				systemName,
				out ignoredMutation,
				out finalStateUnclear,
				out failureDetail
			);
		}
		else
		{
			applied = lifecycleStore.TryApplyFolderRemove(
				systemName,
				folderPath,
				out ignoredMutation,
				out finalStateUnclear,
				out failureDetail
			);
		}

		if (!applied)
		{
			ReportPhysicalRemoveNoteCleanupFailure(
				targetDescription,
				failureDetail,
				finalStateUnclear
			);
			return;
		}

		DebugLogger.LogOperation(
			removesSystem
				? "Note lifecycle System remove applied"
				: "Note lifecycle Folder remove applied",
			removesSystem
				? $"PhysicalRemove=true, SystemName='{systemName}', DiskMutationApplied={!ignoredMutation.IsNoOp}"
				: $"PhysicalRemove=true, SystemName='{systemName}', FolderPath='{folderPath}', DiskMutationApplied={!ignoredMutation.IsNoOp}"
		);
	}

	private void ReportReversibleNoteLifecycleApplyFailure(
		string operationName,
		string targetDescription,
		string failureDetail,
		bool finalStateUnclear
	)
	{
		string userMessage = finalStateUnclear
			? $"System Explorer could not complete the {operationName} and could not fully verify restoration of the associated Note data. Restart Godot and inspect the Notes metadata before continuing."
			: $"System Explorer could not complete the {operationName} because the associated Note metadata could not be updated. The in-memory metadata was restored.";

		ReportTreeOperationFailure(
			userMessage,
			$"Target={targetDescription}, NoteLifecycleFailure=({failureDetail}), FinalStateUnclear={finalStateUnclear}",
			finalStateUnclear
				? TreeOperationOutcomeSeverity.FinalStateUnclear
				: TreeOperationOutcomeSeverity.Failed,
			replaceExistingReport: finalStateUnclear
		);

		DebugLogger.LogOperation(
			"Note lifecycle reversible apply failed",
			$"Operation='{operationName}', Target={targetDescription}, FailureDetail=({failureDetail}), FinalStateUnclear={finalStateUnclear}"
		);
	}

	private void ReportNoteLifecycleRollbackFailure(
		string operationName,
		string targetDescription,
		string rollbackFailureDetail
	)
	{
		const string userMessage =
			"System Explorer could not complete the operation and could not fully verify restoration of the associated Note data. Restart Godot and inspect the Notes metadata before continuing.";

		ReportTreeOperationFailure(
			userMessage,
			$"Operation='{operationName}', Target={targetDescription}, NoteLifecycleRollbackFailure=({rollbackFailureDetail})",
			TreeOperationOutcomeSeverity.FinalStateUnclear,
			replaceExistingReport: true
		);

		DebugLogger.LogOperation(
			"Note lifecycle rollback failed",
			$"Operation='{operationName}', Target={targetDescription}, FailureDetail=({rollbackFailureDetail}), FinalStateUnclear=true"
		);
	}

	private void ReportPhysicalRemoveNoteCleanupFailure(
		string targetDescription,
		string failureDetail,
		bool finalStateUnclear
	)
	{
		string noteFailureMessage = finalStateUnclear
			? "The files and System Explorer metadata were removed, but the final state of the associated Note metadata could not be verified. Restart Godot and inspect the Notes metadata before continuing."
			: "The files and System Explorer metadata were removed, but the associated Note metadata could not be cleaned up.";
		string existingFailureMessage = GetActiveTreeOperationFailureUserMessage();
		bool existingStateUnclear = IsActiveTreeOperationFinalStateUnclear;
		string userMessage = string.IsNullOrWhiteSpace(existingFailureMessage)
			? noteFailureMessage
			: existingFailureMessage.Trim() + "\n\n" + noteFailureMessage;
		TreeOperationOutcomeSeverity severity = finalStateUnclear || existingStateUnclear
			? TreeOperationOutcomeSeverity.FinalStateUnclear
			: TreeOperationOutcomeSeverity.Incomplete;

		ReportTreeOperationFailure(
			userMessage,
			$"Target={targetDescription}, NoteCleanupFailure=({failureDetail}), FinalStateUnclear={finalStateUnclear}",
			severity,
			replaceExistingReport: !string.IsNullOrWhiteSpace(existingFailureMessage) || finalStateUnclear
		);

		DebugLogger.LogOperation(
			"Physical Remove Note cleanup failed",
			$"Target={targetDescription}, FailureDetail=({failureDetail}), FinalStateUnclear={finalStateUnclear}"
		);
	}

	private static string BuildNoteRemoveTargetDescription(
		string systemName,
		string folderPath
	)
	{
		return string.IsNullOrEmpty(folderPath)
			? $"SystemName='{systemName}'"
			: $"SystemName='{systemName}', FolderPath='{folderPath}'";
	}
}
#endif
