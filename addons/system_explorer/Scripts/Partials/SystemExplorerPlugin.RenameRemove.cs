#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SystemExplorer.EditorIntegration.ScriptEditing;
using SystemExplorer.FileOperations;

public partial class SystemExplorerPlugin
{
	private enum RenameMutationResult
	{
		Success,
		NameConflict,
		NoChange,
		Failed,
	}

	private enum RenameConflictItemType
	{
		System,
		Folder,
		Script,
		Scene,
	}

	private enum RenameFilesystemRollbackState
	{
		OriginalRestored,
		TargetRetained,
		Unclear,
	}

	private readonly record struct RenameFilesystemRollbackResult(
		RenameFilesystemRollbackState State,
		string VerifiedResourcePath,
		string TemporaryPath,
		string Details
	);

	private readonly record struct RemoveMetadataMutationResult(
		bool Removed,
		bool FolderBindingsChanged
	);

	private sealed class TreeSelectionOperationState
	{
		public PersistentTreeSelection OriginalSelection { get; init; }
		public PersistentTreeSelection? PreviousVisibleSelection { get; init; }
		public PersistentTreeSelection? NextVisibleSelectionOutsideSubtree { get; init; }
		public bool IsValid =>
			!string.IsNullOrWhiteSpace(OriginalSelection.SystemName)
			&& !string.IsNullOrWhiteSpace(OriginalSelection.Metadata);
	}

	private enum RemoveSelectionFocusTarget
	{
		None,
		HiddenTreeContext,
		SystemNameInput,
	}

	#region Rename and Remove Operations
	private ScriptTreeOccurrence? _pendingRemoveScriptOccurrence;
	private TreeSelectionOperationState _pendingNonScriptRenameTreeSelectionState;
	private TreeSelectionOperationState _pendingRemoveTreeSelectionState;
	private RemoveSelectionFocusTarget _pendingRemoveSelectionFocusTarget;
	private bool _pendingRemoveSelectionFocusCommitted;

	private TreeSelectionOperationState CaptureTreeSelectionOperationState(TreeItem selectedItem)
	{
		if (
			selectedItem == null
			|| !GodotObject.IsInstanceValid(selectedItem)
			|| !TryCreatePersistentTreeSelection(
				selectedItem,
				out PersistentTreeSelection originalSelection
			)
		)
		{
			return null;
		}

		PersistentTreeSelection? previousSelection = null;
		TreeItem previousVisibleItem = selectedItem.GetPrevVisible(false);

		if (
			previousVisibleItem != null
			&& TryCreatePersistentTreeSelection(
				previousVisibleItem,
				out PersistentTreeSelection capturedPreviousSelection
			)
		)
		{
			previousSelection = capturedPreviousSelection;
		}

		PersistentTreeSelection? nextSelection = null;
		TreeItem nextVisibleItemOutsideSubtree =
			FindNextVisibleTreeItemOutsideSubtree(selectedItem);

		if (
			nextVisibleItemOutsideSubtree != null
			&& TryCreatePersistentTreeSelection(
				nextVisibleItemOutsideSubtree,
				out PersistentTreeSelection capturedNextSelection
			)
		)
		{
			nextSelection = capturedNextSelection;
		}

		return new TreeSelectionOperationState
		{
			OriginalSelection = originalSelection,
			PreviousVisibleSelection = previousSelection,
			NextVisibleSelectionOutsideSubtree = nextSelection,
		};
	}

	private static TreeItem FindNextVisibleTreeItemOutsideSubtree(TreeItem selectedItem)
	{
		TreeItem current = selectedItem;

		while (current != null)
		{
			TreeItem nextSibling = current.GetNext();
			if (nextSibling != null)
				return nextSibling;

			current = current.GetParent();
			if (current == null || current.GetParent() == null)
				break;
		}

		return null;
	}

	private void CapturePendingNonScriptRenameTreeSelectionState(
		TreeItem selectedItem,
		string metadata
	)
	{
		_pendingNonScriptRenameTreeSelectionState = null;

		if (
			string.IsNullOrWhiteSpace(metadata)
			|| metadata.StartsWith("script::", StringComparison.Ordinal)
		)
		{
			return;
		}

		TreeSelectionOperationState state = CaptureTreeSelectionOperationState(selectedItem);

		if (
			state != null
			&& state.IsValid
			&& string.Equals(
				state.OriginalSelection.Metadata,
				metadata,
				StringComparison.Ordinal
			)
		)
		{
			_pendingNonScriptRenameTreeSelectionState = state;
		}
	}

	private void CapturePendingRemoveTreeSelectionState(TreeItem selectedItem)
	{
		_pendingRemoveSelectionFocusTarget = RemoveSelectionFocusTarget.None;
		_pendingRemoveSelectionFocusCommitted = false;
		_pendingRemoveTreeSelectionState = CaptureTreeSelectionOperationState(selectedItem);
	}

	private static TreeSelectionOperationState RemapTreeSelectionStateAfterSceneRename(
		TreeSelectionOperationState state,
		string oldScenePath,
		string newScenePath
	)
	{
		if (state == null || !state.IsValid)
			return state;

		return new TreeSelectionOperationState
		{
			OriginalSelection = RemapPersistentSelectionAfterSceneRename(
				state.OriginalSelection,
				oldScenePath,
				newScenePath
			),
			PreviousVisibleSelection = RemapOptionalPersistentSelectionAfterSceneRename(
				state.PreviousVisibleSelection,
				oldScenePath,
				newScenePath
			),
			NextVisibleSelectionOutsideSubtree =
				RemapOptionalPersistentSelectionAfterSceneRename(
					state.NextVisibleSelectionOutsideSubtree,
					oldScenePath,
					newScenePath
				),
		};
	}

	private static PersistentTreeSelection? RemapOptionalPersistentSelectionAfterSceneRename(
		PersistentTreeSelection? selection,
		string oldScenePath,
		string newScenePath
	)
	{
		return selection.HasValue
			? RemapPersistentSelectionAfterSceneRename(
				selection.Value,
				oldScenePath,
				newScenePath
			)
			: null;
	}

	private static PersistentTreeSelection RemapPersistentSelectionAfterSceneRename(
		PersistentTreeSelection selection,
		string oldScenePath,
		string newScenePath
	)
	{
		if (!IsScriptOrScenePersistentTreeSelection(selection))
			return selection;

		string entry = GetEntryFromMetadata(selection.Metadata);
		string normalizedOldScenePath = NormalizeRenameResourcePath(oldScenePath);
		string normalizedNewScenePath = NormalizeRenameResourcePath(newScenePath);

		if (selection.Metadata.StartsWith("sceneLink::", StringComparison.Ordinal))
		{
			if (!string.Equals(
				NormalizeRenameResourcePath(GetScenePathFromEntry(entry)),
				normalizedOldScenePath,
				StringComparison.OrdinalIgnoreCase
			))
			{
				return selection;
			}

			string updatedSceneEntry = BuildSceneEntry(
				GetFolderPathFromEntry(entry),
				normalizedNewScenePath,
				IsEntryLocked(entry)
			);
			return new PersistentTreeSelection(
				selection.SystemName,
				$"sceneLink::{updatedSceneEntry}"
			);
		}

		if (!string.Equals(
			NormalizeRenameResourcePath(GetLinkedScenePathFromEntry(entry)),
			normalizedOldScenePath,
			StringComparison.OrdinalIgnoreCase
		))
		{
			return selection;
		}

		string updatedScriptEntry = BuildScriptEntry(
			GetFolderPathFromEntry(entry),
			GetScriptPathFromEntry(entry),
			normalizedNewScenePath,
			IsEntryLocked(entry)
		);
		return new PersistentTreeSelection(
			selection.SystemName,
			$"script::{updatedScriptEntry}"
		);
	}

	private void RestoreNonScriptRenameSelectionAfterRebuild(
		TreeSelectionOperationState state,
		PersistentTreeSelection renamedSelection,
		string reason
	)
	{
		bool restored = TryRestoreTreeSelectionByIdentity(renamedSelection, reason);

		if (
			!restored
			&& state != null
			&& state.PreviousVisibleSelection.HasValue
		)
		{
			restored = TryRestoreTreeSelectionByIdentity(
				state.PreviousVisibleSelection.Value,
				$"{reason}: previous visible fallback"
			);
		}

		if (
			!restored
			&& state != null
			&& state.NextVisibleSelectionOutsideSubtree.HasValue
		)
		{
			restored = TryRestoreTreeSelectionByIdentity(
				state.NextVisibleSelectionOutsideSubtree.Value,
				$"{reason}: next visible fallback"
			);
		}

		if (!restored)
			restored = TryRestoreFirstVisibleTreeSelection($"{reason}: first visible fallback");

		if (!restored)
			ClearPersistentTreeSelectionAndTreeSelection();

		CallDeferred(nameof(ReleaseTreeFocusAfterNavigation));
	}

	private void RestoreTreeSelectionAfterRemove(
		TreeSelectionOperationState state,
		string reason
	)
	{
		if (_systems.Count == 0)
		{
			ClearPersistentTreeSelectionAndTreeSelection();
			QueueRemoveSelectionFocus(RemoveSelectionFocusTarget.SystemNameInput);
			return;
		}

		bool restored =
			state != null
			&& state.IsValid
			&& TryRestoreTreeSelectionByIdentity(
				state.OriginalSelection,
				$"{reason}: original selection"
			);

		if (
			!restored
			&& state != null
			&& state.PreviousVisibleSelection.HasValue
		)
		{
			restored = TryRestoreTreeSelectionByIdentity(
				state.PreviousVisibleSelection.Value,
				$"{reason}: previous visible selection"
			);
		}

		if (
			!restored
			&& state != null
			&& state.NextVisibleSelectionOutsideSubtree.HasValue
		)
		{
			restored = TryRestoreTreeSelectionByIdentity(
				state.NextVisibleSelectionOutsideSubtree.Value,
				$"{reason}: next visible selection"
			);
		}

		if (!restored)
			restored = TryRestoreFirstVisibleTreeSelection($"{reason}: first visible selection");

		if (!restored)
			ClearPersistentTreeSelectionAndTreeSelection();

		QueueRemoveSelectionFocus(RemoveSelectionFocusTarget.HiddenTreeContext);
	}

	private void QueueRemoveSelectionFocus(RemoveSelectionFocusTarget target)
	{
		_pendingRemoveSelectionFocusTarget = target;
		_pendingRemoveSelectionFocusCommitted = true;
		CallDeferred(nameof(ApplyPendingRemoveSelectionFocusDeferred));
	}

	private void ApplyPendingRemoveSelectionFocusDeferred()
	{
		RemoveSelectionFocusTarget target = _pendingRemoveSelectionFocusTarget;
		_pendingRemoveSelectionFocusTarget = RemoveSelectionFocusTarget.None;
		_pendingRemoveSelectionFocusCommitted = false;

		if (target == RemoveSelectionFocusTarget.SystemNameInput)
		{
			if (
				_systems.Count == 0
				&& _systemNameInput != null
				&& GodotObject.IsInstanceValid(_systemNameInput)
				&& _systemNameInput.IsInsideTree()
			)
			{
				_systemNameInput.Edit(true);
			}

			return;
		}

		if (target == RemoveSelectionFocusTarget.HiddenTreeContext && _systems.Count > 0)
			TryFocusSystemExplorerHiddenTreeContext(revealDock: false);
	}

	private void CapturePendingRemoveScriptOccurrence(TreeItem selectedItem = null)
	{
		_pendingRemoveScriptOccurrence = null;

		if (
			string.IsNullOrWhiteSpace(_pendingRemoveMetadata)
			|| !_pendingRemoveMetadata.StartsWith("script::", StringComparison.Ordinal)
		)
		{
			return;
		}

		if (
			!TryGetScriptTreeOccurrenceFromTreeItem(
				selectedItem ?? _tree?.GetSelected(),
				out ScriptTreeOccurrence occurrence
			)
			|| !string.Equals(
				occurrence.Entry,
				GetEntryFromMetadata(_pendingRemoveMetadata),
				StringComparison.Ordinal
			)
		)
		{
			return;
		}

		_pendingRemoveScriptOccurrence = occurrence;
	}

	private bool TryOpenRenameDialogForSelectedItem()
	{
		if (
			_tree == null
			|| !GodotObject.IsInstanceValid(_tree)
			|| _renameDialog == null
			|| !GodotObject.IsInstanceValid(_renameDialog)
			|| _renameInput == null
			|| !GodotObject.IsInstanceValid(_renameInput)
		)
		{
			return false;
		}

		TreeItem selectedItem = _tree.GetSelected();

		if (selectedItem == null || !GodotObject.IsInstanceValid(selectedItem))
			return false;

		string metadata = selectedItem.GetMetadata(0).AsString();

		if (!IsRenameTargetMetadata(metadata))
			return false;

		_pendingRenameMetadata = metadata;
		CapturePendingNonScriptRenameTreeSelectionState(selectedItem, metadata);
		_pendingScriptRenameTreeState = metadata.StartsWith(
			"script::",
			StringComparison.Ordinal
		)
			? CaptureScriptRenameTreeState(GetEntryFromMetadata(metadata))
			: null;

		if (OpenRenameDialog())
			return true;

		_pendingRenameMetadata = "";
		_pendingScriptRenameTreeState = null;
		_pendingNonScriptRenameTreeSelectionState = null;
		return false;
	}

	private static bool IsRenameTargetMetadata(string metadata)
	{
		return !string.IsNullOrWhiteSpace(metadata)
			&& (
				metadata.StartsWith("system::", StringComparison.Ordinal)
				|| metadata.StartsWith("folder::", StringComparison.Ordinal)
				|| metadata.StartsWith("script::", StringComparison.Ordinal)
				|| metadata.StartsWith("sceneLink::", StringComparison.Ordinal)
			);
	}

	private bool OpenRenameDialog()
	{
		if (
			!IsRenameTargetMetadata(_pendingRenameMetadata)
			|| _renameDialog == null
			|| !GodotObject.IsInstanceValid(_renameDialog)
			|| _renameInput == null
			|| !GodotObject.IsInstanceValid(_renameInput)
		)
		{
			return false;
		}

		bool renamesScript = _pendingRenameMetadata.StartsWith(
			"script::",
			StringComparison.Ordinal
		);

		if (!renamesScript)
		{
			if (
				_pendingNonScriptRenameTreeSelectionState == null
				|| !_pendingNonScriptRenameTreeSelectionState.IsValid
				|| !string.Equals(
					_pendingNonScriptRenameTreeSelectionState.OriginalSelection.Metadata,
					_pendingRenameMetadata,
					StringComparison.Ordinal
				)
			)
			{
				CapturePendingNonScriptRenameTreeSelectionState(
					_tree?.GetSelected(),
					_pendingRenameMetadata
				);
			}

			if (
				_pendingNonScriptRenameTreeSelectionState == null
				|| !_pendingNonScriptRenameTreeSelectionState.IsValid
			)
			{
				ReportTreeOperationFailureOrWarning(
					"System Explorer could not identify the exact selected tree occurrence before opening the rename dialog."
				);
				return false;
			}
		}

		if (_pendingRenameMetadata.StartsWith("system::", StringComparison.Ordinal))
		{
			string systemName = GetSystemNameFromMetadata(_pendingRenameMetadata);

			if (string.IsNullOrWhiteSpace(systemName))
				return false;

			_renameInput.Text = systemName;
		}
		else if (_pendingRenameMetadata.StartsWith("folder::", StringComparison.Ordinal))
		{
			string folderPath = GetFolderPathFromMetadata(_pendingRenameMetadata);

			if (string.IsNullOrWhiteSpace(folderPath))
				return false;

			_renameInput.Text = folderPath.GetFile();
		}
		else if (_pendingRenameMetadata.StartsWith("script::", StringComparison.Ordinal))
		{
			string entry = GetEntryFromMetadata(_pendingRenameMetadata);
			string scriptPath = GetScriptPathFromEntry(entry);

			_pendingScriptRenameTreeState ??= CaptureScriptRenameTreeState(entry);

			if (_pendingScriptRenameTreeState == null || !_pendingScriptRenameTreeState.IsValid)
			{
				ReportTreeOperationFailureOrWarning(
					"System Explorer could not identify the exact selected script entry before opening the rename dialog."
				);
				return false;
			}

			_renameInput.Text = scriptPath.GetFile().GetBaseName();
		}
		else if (_pendingRenameMetadata.StartsWith("sceneLink::", StringComparison.Ordinal))
		{
			string entry = GetEntryFromMetadata(_pendingRenameMetadata);
			string scenePath = GetScenePathFromEntry(entry);

			if (string.IsNullOrWhiteSpace(scenePath))
				return false;

			_renameInput.Text = scenePath.GetFile().GetBaseName();
		}
		else
		{
			return false;
		}

		_renameDialog.DialogHideOnOk = false;
		_renameDialog.PopupCentered();
		_renameInput.Edit(true);
		_renameInput.SelectAll();
		return true;
	}

	private void OnRemoveConfirmed()
	{
		DebugLogger.LogOperation("Remove Confirmed", _pendingRemoveMetadata);

		if (string.IsNullOrWhiteSpace(_pendingRemoveMetadata))
			return;

		using TreeOperationDialogScope operationScope = BeginTreeOperationDialogScope(
			"Remove Failed",
			CloseRemoveUiAfterFailure
		);

		string removeMetadata = _pendingRemoveMetadata;
		TreeSelectionOperationState removeSelectionState =
			_pendingRemoveTreeSelectionState;

		if (!EnsureSystemsLoadedForTreeOperation("Remove Item"))
			return;

		IntentionalEmptySystemsSaveAuthorization intentionalEmptySaveAuthorization = null;
		string authorizedLastSystemName = "";

		if (removeMetadata.StartsWith("system::", StringComparison.Ordinal))
		{
			string systemName = removeMetadata.Substring("system::".Length);
			bool removesCurrentLastSystem =
				!string.IsNullOrWhiteSpace(systemName)
				&& _systems.Count == 1
				&& _systems.ContainsKey(systemName);

			if (removesCurrentLastSystem)
			{
				if (
					!TryCreateIntentionalEmptySystemsSaveAuthorization(
						systemName,
						out intentionalEmptySaveAuthorization,
						out string authorizationFailureMessage
					)
				)
				{
					ReportTreeOperationFailureOrWarning(authorizationFailureMessage);
					DebugLogger.LogOperation(
						"Remove last system cancelled: persistence state could not be verified",
						systemName
					);
					return;
				}

				authorizedLastSystemName = systemName;
			}
		}

		bool removeFromFilesystem =
			!_removeFromFilesystemCheckBox.Disabled
			&& _removeFromFilesystemCheckBox.ButtonPressed;

		if (!removeFromFilesystem)
		{
			SystemsAndFolderBindingsSnapshot snapshot =
				CaptureSystemsAndFolderBindingsSnapshot();
			RemoveMetadataMutationResult mutationResult = RemoveMetadata(
				removeMetadata,
				_pendingRemoveScriptOccurrence
			);

			if (!mutationResult.Removed)
			{
				if (!HasActiveTreeOperationFailure)
				{
					ReportTreeOperationFailure(
						"System Explorer could not remove the verified tree item.",
						removeMetadata
					);
				}

				DebugLogger.LogOperation("Remove cancelled: mutation failed", removeMetadata);
				return;
			}

			bool virtualRemoveAuthorizationMatchesRemovedSystem =
				intentionalEmptySaveAuthorization != null
				&& removeMetadata.StartsWith("system::", StringComparison.Ordinal)
				&& string.Equals(
					authorizedLastSystemName,
					intentionalEmptySaveAuthorization.SystemName,
					StringComparison.Ordinal
				)
				&& string.Equals(
					removeMetadata.Substring("system::".Length),
					intentionalEmptySaveAuthorization.SystemName,
					StringComparison.Ordinal
				);

			IntentionalEmptySystemsSaveAuthorization virtualRemoveAuthorization =
				virtualRemoveAuthorizationMatchesRemovedSystem
					? intentionalEmptySaveAuthorization
					: null;

			if (
				!TryPersistReversibleSystemsAndFolderBindingsMutation(
					snapshot,
					systemsChanged: mutationResult.Removed,
					folderBindingsChanged: mutationResult.FolderBindingsChanged,
					operationName: "Virtual Remove",
					intentionalEmptyAuthorization: virtualRemoveAuthorization
				)
			)
			{
				if (!HasActiveTreeOperationFailure)
				{
					ReportTreeOperationFailure(
						"System Explorer could not save the remove operation. The in-memory metadata was restored.",
						removeMetadata
					);
				}

				DebugLogger.LogOperation(
					"Virtual remove cancelled: coordinated persistence failed",
					removeMetadata
				);
				return;
			}

			BuildTree(keepCurrentExpansionState: true);
			RestoreTreeSelectionAfterRemove(
				removeSelectionState,
				"Virtual Remove"
			);
			ClearPendingPhysicalRemoveState();
			return;
		}

		if (
			!TryCollectPhysicalRemoveTargets(
				removeMetadata,
				out List<string> filePathsToDelete,
				out string collectionFailureMessage
			)
		)
		{
			if (!string.IsNullOrWhiteSpace(collectionFailureMessage))
				ReportTreeOperationFailureOrWarning(collectionFailureMessage);
			else if (!HasActiveTreeOperationFailure)
				ReportTreeOperationFailure("System Explorer could not identify the selected files for removal.");

			DebugLogger.LogOperation(
				"Physical remove cancelled during target collection",
				collectionFailureMessage
			);
			return;
		}

		bool folderBindingsPreflightRequired =
			PhysicalRemoveMayChangeFolderBindings(removeMetadata);
		SystemsAndFolderBindingsSnapshot physicalRemoveBaseline =
			CaptureSystemsAndFolderBindingsSnapshot();

		if (
			!TryPreflightMetadataPersistenceForPhysicalMutation(
				"Remove",
				systemsRequired: true,
				folderBindingsRequired: folderBindingsPreflightRequired,
				physicalConsequence: "No project files were deleted."
			)
		)
		{
			RestoreSystemsAndFolderBindingsSnapshot(physicalRemoveBaseline);
			return;
		}

		if (
			!TryPrepareScriptsForPhysicalRemove(
				filePathsToDelete,
				out string preparationFailureMessage
			)
		)
		{
			RestoreSystemsAndFolderBindingsSnapshot(physicalRemoveBaseline);

			if (!string.IsNullOrWhiteSpace(preparationFailureMessage))
				ReportTreeOperationFailureOrWarning(preparationFailureMessage);
			else if (!HasActiveTreeOperationFailure)
				ReportTreeOperationFailure("System Explorer could not prepare the selected files for removal.");

			DebugLogger.LogOperation(
				"Physical remove cancelled during editor preparation",
				preparationFailureMessage
			);
			return;
		}

		PhysicalDeleteResult deleteResult = DeleteFiles(filePathsToDelete);
		HashSet<string> verifiedDeletedResourcePaths = deleteResult
			.VerifiedDeletedResourcePaths
			.Select(NormalizePhysicalRemovePath)
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		bool requestedPhysicalResources = filePathsToDelete.Count > 0;
		bool allRequestedResourcesDeleted = filePathsToDelete.All(path =>
			verifiedDeletedResourcePaths.Contains(NormalizePhysicalRemovePath(path))
		);

		if (requestedPhysicalResources && verifiedDeletedResourcePaths.Count == 0)
		{
			RestoreSystemsAndFolderBindingsSnapshot(physicalRemoveBaseline);
			BuildTree(keepCurrentExpansionState: true);
			RestoreTreeSelectionAfterRemove(
				removeSelectionState,
				"Physical Remove without verified deletion"
			);
			ClearPendingPhysicalRemoveState();
			HideTreeOperationOriginWindow(_removeDialog);

			string terminalFailureMessage = GetActiveTreeOperationFailureUserMessage();

			if (!string.IsNullOrWhiteSpace(terminalFailureMessage))
				SuppressActiveTreeOperationDialogPresentation();

			ShowPhysicalRemoveResultDialog(
				deleteResult,
				filePathsToDelete,
				terminalFailureMessage
			);

			DebugLogger.LogOperation(
				"Physical remove batch failed without verified resource deletion",
				$"RequestedResources={filePathsToDelete.Count}, VerifiedDeletedResources=0, Failures={deleteResult.Failures.Count}, UnclearFailures={deleteResult.UnclearFailures.Count}, RecoveredAnomalies={deleteResult.RecoveredAnomalies.Count}"
			);
			return;
		}

		PhysicalRemoveMetadataRepairResult metadataRepairResult =
			RemoveMetadataAfterPhysicalDelete(
				removeMetadata,
				deleteResult.VerifiedDeletedResourcePaths,
				allRequestedResourcesDeleted
			);

		bool systemsChanged = metadataRepairResult.SystemsChanged;
		bool authorizationMatchesRemovedSystem =
			allRequestedResourcesDeleted
			&& intentionalEmptySaveAuthorization != null
			&& removeMetadata.StartsWith("system::", StringComparison.Ordinal)
			&& string.Equals(
				authorizedLastSystemName,
				intentionalEmptySaveAuthorization.SystemName,
				StringComparison.Ordinal
			)
			&& string.Equals(
				removeMetadata.Substring("system::".Length),
				intentionalEmptySaveAuthorization.SystemName,
				StringComparison.Ordinal
			);

		bool systemsSaved = true;

		if (systemsChanged)
		{
			systemsSaved = SaveSystemsForCoordinatedMetadataMutation(
				authorizationMatchesRemovedSystem
					? intentionalEmptySaveAuthorization
					: null
			);
		}

		bool folderBindingsSaved = !metadataRepairResult.FolderBindingsChanged;

		if (systemsSaved && metadataRepairResult.FolderBindingsChanged)
			folderBindingsSaved = SaveFolderBindings();

		bool deletedAnyVerifiedPhysicalPath =
			deleteResult.VerifiedDeletedAnyResource
			|| deleteResult.VerifiedDeletedUidSidecarPaths.Count > 0;

		if (!systemsSaved && systemsChanged)
		{
			bool metadataStateUnclear = IsActiveTreeOperationFinalStateUnclear;
			string userMessage = deleteResult.VerifiedDeletedAnyResource
				? metadataStateUnclear
					? "One or more project files were deleted, but the final state of the repaired systems metadata could not be verified. The repaired in-memory tree was kept."
					: "One or more project files were deleted, but System Explorer could not save the repaired systems metadata. The repaired in-memory tree was kept."
				: "System Explorer could not save the remove operation. The repaired in-memory tree was kept.";

			ReportTreeOperationFailure(
				userMessage,
				$"Metadata='{removeMetadata}', FolderBindingsSaveSkipped={metadataRepairResult.FolderBindingsChanged}",
				metadataStateUnclear
					? TreeOperationOutcomeSeverity.FinalStateUnclear
					: deleteResult.VerifiedDeletedAnyResource
						? TreeOperationOutcomeSeverity.Incomplete
						: TreeOperationOutcomeSeverity.Failed,
				replaceExistingReport: true
			);
			DebugLogger.LogOperation(
				"Physical remove completed but systems metadata could not be persisted",
				$"Metadata='{removeMetadata}', FolderBindingsSaveSkipped={metadataRepairResult.FolderBindingsChanged}"
			);
		}
		else if (!folderBindingsSaved)
		{
			bool metadataStateUnclear = IsActiveTreeOperationFinalStateUnclear;
			string userMessage = deleteResult.VerifiedDeletedAnyResource
				? metadataStateUnclear
					? "One or more project files were deleted and systems.json was saved, but the final state of the folder binding cleanup could not be verified. The repaired in-memory state was kept."
					: "One or more project files were deleted and systems.json was saved, but System Explorer could not save the folder binding cleanup. The repaired in-memory state was kept."
				: "System Explorer could not save the folder binding cleanup. The repaired in-memory state was kept.";

			ReportTreeOperationFailure(
				userMessage,
				removeMetadata,
				metadataStateUnclear
					? TreeOperationOutcomeSeverity.FinalStateUnclear
					: deleteResult.VerifiedDeletedAnyResource
						? TreeOperationOutcomeSeverity.Incomplete
						: TreeOperationOutcomeSeverity.Failed,
				replaceExistingReport: true
			);
			DebugLogger.LogOperation(
				"Physical remove completed but folder binding cleanup could not be persisted",
				removeMetadata
			);
		}

		BuildTree(keepCurrentExpansionState: true);
		RestoreTreeSelectionAfterRemove(
			removeSelectionState,
			"Physical Remove final state"
		);

		if (deletedAnyVerifiedPhysicalPath)
			EditorInterface.Singleton.GetResourceFilesystem().Scan();

		ClearPendingPhysicalRemoveState();

		DebugLogger.LogOperation(
			"Physical remove batch completed",
			$"RequestedResources={filePathsToDelete.Count}, VerifiedDeletedResources={deleteResult.VerifiedDeletedResourcePaths.Count}, VerifiedDeletedUidSidecars={deleteResult.VerifiedDeletedUidSidecarPaths.Count}, FailedResources={deleteResult.Failures.Count(failure => failure.Kind == PhysicalDeleteFailureKind.ResourceFile)}, FailedUidSidecars={deleteResult.Failures.Count(failure => failure.Kind == PhysicalDeleteFailureKind.UidSidecar)}, UnclearResources={deleteResult.UnclearFailures.Count(failure => failure.Kind == PhysicalDeleteFailureKind.ResourceFile)}, UnclearUidSidecars={deleteResult.UnclearFailures.Count(failure => failure.Kind == PhysicalDeleteFailureKind.UidSidecar)}, RecoveredAnomalies={deleteResult.RecoveredAnomalies.Count}, RemovedEntries={metadataRepairResult.RemovedEntryCount}, ClearedSceneLinks={metadataRepairResult.ClearedSceneLinkCount}, FolderBindingsChanged={metadataRepairResult.FolderBindingsChanged}, SaveSystemsSucceeded={systemsSaved}, SaveFolderBindingsSucceeded={folderBindingsSaved}"
		);

		string metadataFailureMessage = GetActiveTreeOperationFailureUserMessage();
		bool shouldShowPhysicalRemoveResult =
			requestedPhysicalResources
			&& (deleteResult.HasIssues || !string.IsNullOrWhiteSpace(metadataFailureMessage));

		if (shouldShowPhysicalRemoveResult)
		{
			HideTreeOperationOriginWindow(_removeDialog);

			if (!string.IsNullOrWhiteSpace(metadataFailureMessage))
				SuppressActiveTreeOperationDialogPresentation();

			ShowPhysicalRemoveResultDialog(
				deleteResult,
				filePathsToDelete,
				metadataFailureMessage
			);
		}
	}

	private void ClearPendingPhysicalRemoveState()
	{
		_pendingRemoveMetadata = "";
		_pendingRemoveScriptOccurrence = null;
		_pendingRemoveTreeSelectionState = null;
		_removeFromFilesystemCheckBox.ButtonPressed = false;
	}

	private RemoveMetadataMutationResult RemoveMetadata(
		string metadata,
		ScriptTreeOccurrence? selectedScriptOccurrence = null
	)
	{
		DebugLogger.LogOperation("Remove Mutation Requested", metadata);

		if (!EnsureSystemsLoadedForTreeOperation("Remove Item"))
			return default;

		if (metadata.StartsWith("system::"))
		{
			string systemName = metadata.Replace("system::", "");
			bool removed = _systems.Remove(systemName);

			bool folderBindingsChanged = removed
				&& RemoveFolderBindingsForSystem(systemName);

			DebugLogger.LogOperation(
				removed ? "Remove System Mutated" : "Remove System failed",
				systemName
			);
			return new RemoveMetadataMutationResult(removed, folderBindingsChanged);
		}

		if (metadata.StartsWith("script::"))
		{
			string entry = GetEntryFromMetadata(metadata);

			if (
				!selectedScriptOccurrence.HasValue
				|| !string.Equals(
					selectedScriptOccurrence.Value.Entry,
					entry,
					StringComparison.Ordinal
				)
			)
			{
				ReportTreeOperationFailureOrWarning(
					"System Explorer could not identify the exact selected script occurrence. The remove operation was cancelled."
				);
				DebugLogger.LogOperation(
					"Remove Script cancelled: exact occurrence unavailable",
					entry
				);
				return default;
			}

			bool removed = RemoveScriptTreeOccurrence(selectedScriptOccurrence.Value);
			DebugLogger.LogOperation(removed ? "Remove Script Mutated" : "Remove Script failed", entry);
			return new RemoveMetadataMutationResult(removed, false);
		}

		if (metadata.StartsWith("sceneLink::"))
		{
			string entry = metadata.Substring("sceneLink::".Length);
			bool removed = RemoveEntry(entry);
			DebugLogger.LogOperation(removed ? "Remove Scene Mutated" : "Remove Scene failed", entry);
			return new RemoveMetadataMutationResult(removed, false);
		}

		if (metadata.StartsWith("folder::"))
		{
			bool removed = RemoveFolder(metadata);

			bool folderBindingsChanged = removed
				&& RemoveFolderBindingsForFolderAndDescendants(metadata);

			DebugLogger.LogOperation(removed ? "Remove Folder Mutated" : "Remove Folder failed", metadata);
			return new RemoveMetadataMutationResult(removed, folderBindingsChanged);
		}

		return default;
	}

	private bool RemoveScriptTreeOccurrence(ScriptTreeOccurrence occurrence)
	{
		if (!EnsureSystemsLoadedForTreeOperation("Remove Script Occurrence"))
			return false;

		if (TryRemoveScriptTreeOccurrenceFromCurrentSystems(occurrence))
			return true;

		return TryRecoverSystemsFromDisk("Remove Script Occurrence")
			&& TryRemoveScriptTreeOccurrenceFromCurrentSystems(occurrence);
	}

	private bool TryRemoveScriptTreeOccurrenceFromCurrentSystems(
		ScriptTreeOccurrence occurrence
	)
	{
		if (
			string.IsNullOrWhiteSpace(occurrence.SystemName)
			|| string.IsNullOrWhiteSpace(occurrence.Entry)
			|| string.IsNullOrWhiteSpace(occurrence.ScriptPath)
			|| !EnsureSystemAvailable(occurrence.SystemName, "Remove Script Occurrence")
		)
		{
			return false;
		}

		List<string> entries = _systems[occurrence.SystemName];
		int index = entries.FindIndex(entry =>
			string.Equals(entry, occurrence.Entry, StringComparison.Ordinal)
			&& string.Equals(
				NormalizeScriptPathForSync(GetScriptPathFromEntry(entry)),
				occurrence.ScriptPath,
				StringComparison.OrdinalIgnoreCase
			)
		);

		if (index < 0)
			return false;

		entries.RemoveAt(index);
		ClearSelectedScriptEntryFromFilter(occurrence.Entry);

		DebugLogger.LogOperation(
			"Remove exact Script occurrence mutated",
			$"{occurrence.SystemName}: {occurrence.Entry}"
		);

		return true;
	}

	private readonly record struct PhysicalRemoveMetadataRepairResult(
		int RemovedEntryCount,
		int ClearedSceneLinkCount,
		bool RemovedSelectedStructure,
		bool FolderBindingsChanged
	)
	{
		internal bool SystemsChanged =>
			RemovedSelectedStructure || RemovedEntryCount > 0 || ClearedSceneLinkCount > 0;
	}

	private PhysicalRemoveMetadataRepairResult RemoveMetadataAfterPhysicalDelete(
		string metadata,
		IReadOnlyCollection<string> deletedResourcePaths,
		bool allRequestedResourcesDeleted
	)
	{
		if (!EnsureSystemsLoadedForTreeOperation("Remove Deleted File References"))
			return default;

		bool removedSelectedStructure = false;
		bool folderBindingsChanged = false;

		if (
			allRequestedResourcesDeleted
			&& (
				metadata.StartsWith("system::", StringComparison.Ordinal)
				|| metadata.StartsWith("folder::", StringComparison.Ordinal)
			)
		)
		{
			RemoveMetadataMutationResult structureMutation = RemoveMetadata(metadata);
			removedSelectedStructure = structureMutation.Removed;
			folderBindingsChanged = structureMutation.FolderBindingsChanged;
		}

		PhysicalRemoveMetadataRepairResult repairResult =
			RepairEntriesReferencingDeletedPhysicalResources(deletedResourcePaths);
		PhysicalRemoveMetadataRepairResult combinedResult = new(
			repairResult.RemovedEntryCount,
			repairResult.ClearedSceneLinkCount,
			removedSelectedStructure,
			folderBindingsChanged || repairResult.FolderBindingsChanged
		);

		DebugLogger.LogOperation(
			"Remove deleted file references mutated",
			$"SelectedStructureRemoved={removedSelectedStructure}, FolderBindingsChanged={folderBindingsChanged}, ReferencesRemoved={combinedResult.RemovedEntryCount}, SceneLinksCleared={combinedResult.ClearedSceneLinkCount}"
		);

		return combinedResult;
	}

	private PhysicalRemoveMetadataRepairResult RepairEntriesReferencingDeletedPhysicalResources(
		IEnumerable<string> deletedResourcePaths
	)
	{
		HashSet<string> normalizedDeletedScriptPaths = new(StringComparer.OrdinalIgnoreCase);
		HashSet<string> normalizedDeletedScenePaths = new(StringComparer.OrdinalIgnoreCase);

		foreach (string deletedResourcePath in deletedResourcePaths ?? Enumerable.Empty<string>())
		{
			string normalizedPath = NormalizePhysicalRemovePath(deletedResourcePath);

			if (string.IsNullOrWhiteSpace(normalizedPath))
				continue;

			if (normalizedPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
				normalizedDeletedScriptPaths.Add(normalizedPath);
			else if (normalizedPath.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase))
				normalizedDeletedScenePaths.Add(normalizedPath);
		}

		if (normalizedDeletedScriptPaths.Count == 0 && normalizedDeletedScenePaths.Count == 0)
			return default;

		int removedEntryCount = 0;
		int clearedSceneLinkCount = 0;

		foreach (string systemName in _systems.Keys.ToList())
		{
			List<string> updatedEntries = new();

			foreach (string entry in _systems[systemName])
			{
				if (!IsScriptOrSceneEntry(entry))
				{
					updatedEntries.Add(entry);
					continue;
				}

				if (IsSceneEntry(entry))
				{
					string scenePath = NormalizePhysicalRemovePath(GetScenePathFromEntry(entry));

					if (normalizedDeletedScenePaths.Contains(scenePath))
					{
						ClearSelectedScriptEntryFromFilter(entry);
						removedEntryCount++;
						continue;
					}

					updatedEntries.Add(entry);
					continue;
				}

				string scriptPath = NormalizePhysicalRemovePath(GetScriptPathFromEntry(entry));

				if (normalizedDeletedScriptPaths.Contains(scriptPath))
				{
					ClearSelectedScriptEntryFromFilter(entry);
					removedEntryCount++;
					continue;
				}

				string linkedScenePath = NormalizePhysicalRemovePath(
					GetLinkedScenePathFromEntry(entry)
				);

				if (
					!string.IsNullOrWhiteSpace(linkedScenePath)
					&& normalizedDeletedScenePaths.Contains(linkedScenePath)
				)
				{
					string updatedEntry = BuildScriptEntry(
						GetFolderPathFromEntry(entry),
						GetScriptPathFromEntry(entry),
						"",
						IsEntryLocked(entry)
					);

					UpdateSelectedScriptEntryFromFilter(entry, updatedEntry);
					updatedEntries.Add(updatedEntry);
					clearedSceneLinkCount++;
					continue;
				}

				updatedEntries.Add(entry);
			}

			_systems[systemName] = updatedEntries;
		}

		return new PhysicalRemoveMetadataRepairResult(
			removedEntryCount,
			clearedSceneLinkCount,
			false,
			false
		);
	}

	private static string NormalizePhysicalRemovePath(string path)
	{
		string normalizedPath = path?.Trim().Replace('\\', '/');

		if (string.IsNullOrWhiteSpace(normalizedPath))
			return "";

		return normalizedPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
			? ScriptPathUtility.Normalize(normalizedPath)
			: normalizedPath;
	}

	private enum PhysicalRemoveTargetStatus
	{
		InvalidOrUnidentified,
		ValidWithoutPhysicalFiles,
		ValidWithPhysicalFiles,
	}

	private readonly record struct PhysicalRemoveTargetInspection(
		PhysicalRemoveTargetStatus Status,
		IReadOnlyList<string> FilePaths
	);

	private bool PhysicalRemoveMayChangeFolderBindings(string metadata)
	{
		if (string.IsNullOrWhiteSpace(metadata))
			return false;

		if (metadata.StartsWith("system::", StringComparison.Ordinal))
		{
			string targetSystemName = metadata.Substring("system::".Length);
			return !string.IsNullOrWhiteSpace(targetSystemName)
				&& _folderBindings.TryGetValue(targetSystemName, out Dictionary<string, string> bindings)
				&& bindings != null
				&& bindings.Count > 0;
		}

		if (!metadata.StartsWith("folder::", StringComparison.Ordinal))
			return false;

		string[] parts = metadata.Split(new[] { "::" }, StringSplitOptions.None);

		if (parts.Length != 3)
			return false;

		string systemName = parts[1];
		string folderPath = parts[2];

		if (
			!_folderBindings.TryGetValue(systemName, out Dictionary<string, string> systemBindings)
			|| systemBindings == null
		)
		{
			return false;
		}

		string descendantPrefix = $"{folderPath.TrimEnd('/')}/";
		return systemBindings.Keys.Any(bindingPath =>
			string.Equals(bindingPath, folderPath, StringComparison.Ordinal)
			|| bindingPath.StartsWith(descendantPrefix, StringComparison.Ordinal)
		);
	}

	private PhysicalRemoveTargetInspection InspectPhysicalRemoveTarget(string metadata)
	{
		if (string.IsNullOrWhiteSpace(metadata))
		{
			return new PhysicalRemoveTargetInspection(
				PhysicalRemoveTargetStatus.InvalidOrUnidentified,
				Array.Empty<string>()
			);
		}

		if (metadata.StartsWith("script::", StringComparison.Ordinal))
		{
			string entry = GetEntryFromMetadata(metadata);

			if (
				!TryGetUnambiguousPhysicalResourcePath(
					entry,
					false,
					out string scriptPath
				)
			)
			{
				return new PhysicalRemoveTargetInspection(
					PhysicalRemoveTargetStatus.InvalidOrUnidentified,
					Array.Empty<string>()
				);
			}

			return new PhysicalRemoveTargetInspection(
				PhysicalRemoveTargetStatus.ValidWithPhysicalFiles,
				new[] { scriptPath }
			);
		}

		if (metadata.StartsWith("sceneLink::", StringComparison.Ordinal))
		{
			string entry = metadata.Substring("sceneLink::".Length);

			if (
				!TryGetUnambiguousPhysicalResourcePath(
					entry,
					true,
					out string scenePath
				)
			)
			{
				return new PhysicalRemoveTargetInspection(
					PhysicalRemoveTargetStatus.InvalidOrUnidentified,
					Array.Empty<string>()
				);
			}

			return new PhysicalRemoveTargetInspection(
				PhysicalRemoveTargetStatus.ValidWithPhysicalFiles,
				new[] { scenePath }
			);
		}

		if (metadata.StartsWith("system::", StringComparison.Ordinal))
		{
			string systemName = metadata.Substring("system::".Length);

			if (
				string.IsNullOrWhiteSpace(systemName)
				|| !_systems.TryGetValue(systemName, out List<string> entries)
				|| entries == null
			)
			{
				return new PhysicalRemoveTargetInspection(
					PhysicalRemoveTargetStatus.InvalidOrUnidentified,
					Array.Empty<string>()
				);
			}

			List<string> filePaths = new();

			foreach (string entry in entries)
			{
				if (!IsScriptOrSceneEntry(entry))
					continue;

				if (
					!TryGetUnambiguousPhysicalResourcePath(
						entry,
						IsSceneEntry(entry),
						out string resourcePath
					)
				)
				{
					return new PhysicalRemoveTargetInspection(
						PhysicalRemoveTargetStatus.InvalidOrUnidentified,
						Array.Empty<string>()
					);
				}

				filePaths.Add(resourcePath);
			}

			filePaths = filePaths.Distinct().ToList();

			return new PhysicalRemoveTargetInspection(
				filePaths.Count == 0
					? PhysicalRemoveTargetStatus.ValidWithoutPhysicalFiles
					: PhysicalRemoveTargetStatus.ValidWithPhysicalFiles,
				filePaths
			);
		}

		if (metadata.StartsWith("folder::", StringComparison.Ordinal))
		{
			string[] parts = metadata.Split(new[] { "::" }, StringSplitOptions.None);

			if (parts.Length != 3)
			{
				return new PhysicalRemoveTargetInspection(
					PhysicalRemoveTargetStatus.InvalidOrUnidentified,
					Array.Empty<string>()
				);
			}

			string systemName = parts[1];
			string folderPath = parts[2];

			if (
				string.IsNullOrWhiteSpace(systemName)
				|| string.IsNullOrWhiteSpace(folderPath)
				|| !_systems.TryGetValue(systemName, out List<string> entries)
				|| entries == null
				|| !entries.Any(entry =>
					entry.StartsWith("folder::", StringComparison.Ordinal)
					&& string.Equals(
						GetFolderPathFromFolderEntry(entry),
						folderPath,
						StringComparison.Ordinal
					)
				)
			)
			{
				return new PhysicalRemoveTargetInspection(
					PhysicalRemoveTargetStatus.InvalidOrUnidentified,
					Array.Empty<string>()
				);
			}

			List<string> filePaths = new();

			foreach (string entry in entries)
			{
				if (
					!IsScriptOrSceneEntry(entry)
					|| (
						!entry.StartsWith($"{folderPath}|")
						&& !entry.StartsWith($"{folderPath}/")
					)
				)
				{
					continue;
				}

				if (
					!TryGetUnambiguousPhysicalResourcePath(
						entry,
						IsSceneEntry(entry),
						out string resourcePath
					)
				)
				{
					return new PhysicalRemoveTargetInspection(
						PhysicalRemoveTargetStatus.InvalidOrUnidentified,
						Array.Empty<string>()
					);
				}

				filePaths.Add(resourcePath);
			}

			filePaths = filePaths.Distinct().ToList();

			return new PhysicalRemoveTargetInspection(
				filePaths.Count == 0
					? PhysicalRemoveTargetStatus.ValidWithoutPhysicalFiles
					: PhysicalRemoveTargetStatus.ValidWithPhysicalFiles,
				filePaths
			);
		}

		return new PhysicalRemoveTargetInspection(
			PhysicalRemoveTargetStatus.InvalidOrUnidentified,
			Array.Empty<string>()
		);
	}

	private bool TryCollectPhysicalRemoveTargets(
		string metadata,
		out List<string> filePaths,
		out string failureMessage
	)
	{
		filePaths = new List<string>();
		failureMessage = "";

		if (!EnsureSystemsLoadedForTreeOperation("Remove Item From Filesystem"))
		{
			failureMessage =
				"System Explorer could not load the current systems data before physical removal.";
			return false;
		}

		PhysicalRemoveTargetInspection inspection = InspectPhysicalRemoveTarget(metadata);

		if (inspection.Status == PhysicalRemoveTargetStatus.InvalidOrUnidentified)
		{
			failureMessage =
				"System Explorer cancelled physical removal because the selected item or one of its stored entries could not be interpreted safely. No project file was deleted.";
			return false;
		}

		HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);

		foreach (string collectedPath in inspection.FilePaths)
		{
			string normalizedPath = collectedPath?.Trim().Replace('\\', '/');

			if (
				!string.IsNullOrWhiteSpace(normalizedPath)
				&& normalizedPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
			)
			{
				normalizedPath = ScriptPathUtility.Normalize(normalizedPath);
			}

			if (
				string.IsNullOrWhiteSpace(normalizedPath)
				|| !normalizedPath.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
			)
			{
				failureMessage =
					"System Explorer cancelled physical removal because an invalid project file path was found.";
				return false;
			}

			if (seenPaths.Add(normalizedPath))
				filePaths.Add(normalizedPath);
		}

		if (filePaths.Count == 0)
		{
			if (
				inspection.Status
				== PhysicalRemoveTargetStatus.ValidWithoutPhysicalFiles
			)
			{
				return true;
			}

			failureMessage =
				"System Explorer could not identify any physical files for the selected remove operation.";
			return false;
		}


		return true;
	}

	private bool TryPrepareScriptsForPhysicalRemove(
		IReadOnlyList<string> filePaths,
		out string failureMessage
	)
	{
		failureMessage = "";

		List<string> scriptPaths = filePaths
			.Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
			.ToList();

		if (scriptPaths.Count == 0)
			return true;

		EditorInterface editorInterface = EditorInterface.Singleton;
		ScriptEditor scriptEditor = editorInterface?.GetScriptEditor();

		if (editorInterface == null || scriptEditor == null)
		{
			failureMessage =
				"System Explorer could not access Godot's Script Editor before physical removal.";
			return false;
		}

		Dictionary<string, List<Script>> matchingScriptsByPath = new(
			StringComparer.OrdinalIgnoreCase
		);

		foreach (string scriptPath in scriptPaths)
			matchingScriptsByPath[scriptPath] = new List<Script>();

		foreach (Script openScript in scriptEditor.GetOpenScripts())
		{
			if (openScript == null)
				continue;

			string openPath = ScriptPathUtility.Normalize(openScript.ResourcePath);

			if (matchingScriptsByPath.TryGetValue(openPath, out List<Script> matches))
				matches.Add(openScript);
		}

		foreach ((string scriptPath, List<Script> matches) in matchingScriptsByPath)
		{
			int distinctResourceCount = matches
				.Where(script => script != null && GodotObject.IsInstanceValid(script))
				.Select(script => script.GetInstanceId())
				.Distinct()
				.Count();

			if (distinctResourceCount <= 1)
				continue;

			failureMessage =
				$"System Explorer cancelled physical removal because more than one open Script resource matched:\n{scriptPath}\n\nClose the duplicate script tabs/resources and try again.";
			return false;
		}

		Script activeScriptBeforeOperation = scriptEditor.GetCurrentScript();
		HashSet<string> targetScriptPaths = scriptPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);

		BeginScriptEditorSyncSuppression();

		try
		{
			foreach ((string scriptPath, List<Script> matches) in matchingScriptsByPath)
			{
				Script targetScript = matches.FirstOrDefault(script =>
					script != null && GodotObject.IsInstanceValid(script)
				);

				if (targetScript == null)
					continue;

				if (
					!TryActivateExactScriptEditorForFileOperation(
						editorInterface,
						scriptEditor,
						targetScript,
						scriptPath,
						"physical removal",
						out ScriptEditorBase scriptEditorBase,
						out TextEdit textEditor
					)
				)
				{
					failureMessage =
						$"System Explorer could not safely activate the exact Script Editor buffer before deleting:\n{scriptPath}\n\nNo files or System Explorer metadata were changed.";
					return false;
				}

				OpenScriptEditorBuffer openEditorBuffer = new(scriptPath, textEditor);

				ScriptEditorBufferAutosaveOperationResult autosaveResult =
					OpenScriptEditorBufferAutosaveCoordinator.TryAutosaveIfNeeded(
						openEditorBuffer,
						failOnSavedDiskMismatch: true
					);

				if (!autosaveResult.Success)
				{
					string autosaveFailureMessage =
						ScriptFileOperationAutosaveFailureMessageBuilder.Build(
							autosaveResult.FailedAutosave,
							"Remove Script"
						);

					failureMessage = autosaveFailureMessage;
					return false;
				}

				ScriptEditorTabCloseResult closeResult = _scriptEditorTabService.TryCloseScriptTab(
					scriptEditor,
					targetScript,
					scriptEditorBase,
					textEditor
				);

				if (!closeResult.Success)
				{
					failureMessage =
						$"System Explorer could not safely close the Script Editor tab before deleting:\n{scriptPath}\n\nNo files or System Explorer metadata were changed.";
					DebugLogger.LogOperation(
						"Physical remove failed: Script Editor tab close",
						closeResult.FailureMessage
					);
					return false;
				}

				if (DoesScriptEditorStillContainOldScript(scriptEditor, targetScript, scriptPath))
				{
					failureMessage =
						$"System Explorer cancelled physical removal because Godot still reported the script as open after closing its tab:\n{scriptPath}\n\nNo files or System Explorer metadata were changed.";
					return false;
				}
			}

			string activePathBeforeOperation = ScriptPathUtility.Normalize(
				activeScriptBeforeOperation?.ResourcePath
			);

			if (
				activeScriptBeforeOperation != null
				&& GodotObject.IsInstanceValid(activeScriptBeforeOperation)
				&& !targetScriptPaths.Contains(activePathBeforeOperation)
				&& FileAccess.FileExists(activePathBeforeOperation)
			)
			{
				editorInterface.EditScript(activeScriptBeforeOperation, -1, 0, false);
			}

			return true;
		}
		finally
		{
			EndScriptEditorSyncSuppression();
		}
	}

	private enum PhysicalDeleteFailureKind
	{
		ResourceFile,
		UidSidecar,
	}

	private enum PhysicalDeleteFailureReason
	{
		MissingBeforeDelete,
		RemoveCallFailed,
		StillExistsAfterRemove,
		ExistenceVerificationFailed,
	}

	private enum PhysicalDeleteCallOutcome
	{
		NotAttempted,
		ReturnedOk,
		ReturnedError,
		ThrewException,
	}

	private readonly record struct PhysicalPathExistenceObservation(
		bool GlobalPathResolutionSucceeded,
		string GlobalPath,
		bool GodotCheckSucceeded,
		bool GodotExists,
		bool SystemIoCheckSucceeded,
		bool SystemIoExists,
		string Details
	)
	{
		internal bool VerifiedExistsByBoth =>
			GodotCheckSucceeded
			&& GodotExists
			&& SystemIoCheckSucceeded
			&& SystemIoExists;

		internal bool VerifiedMissingByBoth =>
			GodotCheckSucceeded
			&& !GodotExists
			&& SystemIoCheckSucceeded
			&& !SystemIoExists;

		internal bool ChecksDisagree =>
			GodotCheckSucceeded
			&& SystemIoCheckSucceeded
			&& GodotExists != SystemIoExists;
	}

	private readonly record struct PhysicalDeleteAttempt(
		PhysicalDeleteFailureKind Kind,
		string ResourcePath,
		string TargetPath,
		string GlobalPath,
		bool MissingBeforeDeleteIsFailure,
		PhysicalPathExistenceObservation PreDeleteObservation,
		PhysicalDeleteCallOutcome CallOutcome,
		Error? GodotError,
		string CallDetails,
		PhysicalPathExistenceObservation FirstPostDeleteObservation
	)
	{
		internal bool DeleteCallAttempted =>
			CallOutcome != PhysicalDeleteCallOutcome.NotAttempted;
	}

	private readonly record struct PhysicalDeleteFailure(
		PhysicalDeleteFailureKind Kind,
		PhysicalDeleteFailureReason Reason,
		string ResourcePath,
		string FailedPath,
		string Details
	);

	private readonly record struct PhysicalDeleteRecoveredAnomaly(
		PhysicalDeleteFailureKind Kind,
		string ResourcePath,
		string AffectedPath,
		string Details
	);

	private readonly record struct PhysicalDeleteReconciliation(
		IReadOnlyList<string> VerifiedDeletedPaths,
		IReadOnlyList<PhysicalDeleteFailure> Failures,
		IReadOnlyList<PhysicalDeleteFailure> UnclearFailures,
		IReadOnlyList<PhysicalDeleteRecoveredAnomaly> RecoveredAnomalies
	);

	private readonly record struct PhysicalDeleteResult(
		IReadOnlyList<string> VerifiedDeletedResourcePaths,
		IReadOnlyList<string> VerifiedDeletedUidSidecarPaths,
		IReadOnlyList<PhysicalDeleteFailure> Failures,
		IReadOnlyList<PhysicalDeleteFailure> UnclearFailures,
		IReadOnlyList<PhysicalDeleteRecoveredAnomaly> RecoveredAnomalies
	)
	{
		internal bool HasIssues => Failures.Count > 0 || UnclearFailures.Count > 0;
		internal bool VerifiedDeletedAnyResource => VerifiedDeletedResourcePaths.Count > 0;
	}

	private PhysicalDeleteResult DeleteFiles(IReadOnlyList<string> filePaths)
	{
		List<PhysicalDeleteAttempt> resourceAttempts = new();
		HashSet<string> scheduledResourcePaths = new(StringComparer.OrdinalIgnoreCase);

		foreach (string resourcePath in filePaths ?? Array.Empty<string>())
		{
			string normalizedResourcePath = NormalizePhysicalRemovePath(resourcePath);

			if (!scheduledResourcePaths.Add(normalizedResourcePath))
			{
				DebugLogger.LogOperation(
					"Physical remove skipped duplicate resource target",
					normalizedResourcePath
				);
				continue;
			}

			resourceAttempts.Add(
				CreatePhysicalDeleteAttempt(
					PhysicalDeleteFailureKind.ResourceFile,
					normalizedResourcePath,
					normalizedResourcePath,
					missingBeforeDeleteIsFailure: true
				)
			);
		}

		PhysicalDeleteReconciliation resourceReconciliation =
			ReconcilePhysicalDeleteAttempts(resourceAttempts);
		List<PhysicalDeleteAttempt> uidAttempts = new();

		foreach (string verifiedDeletedResourcePath in resourceReconciliation.VerifiedDeletedPaths)
		{
			string uidPath = $"{verifiedDeletedResourcePath}.uid";
			uidAttempts.Add(
				CreatePhysicalDeleteAttempt(
					PhysicalDeleteFailureKind.UidSidecar,
					verifiedDeletedResourcePath,
					uidPath,
					missingBeforeDeleteIsFailure: false
				)
			);
		}

		PhysicalDeleteReconciliation uidReconciliation =
			ReconcilePhysicalDeleteAttempts(uidAttempts);
		List<PhysicalDeleteFailure> failures = resourceReconciliation.Failures
			.Concat(uidReconciliation.Failures)
			.ToList();
		List<PhysicalDeleteFailure> unclearFailures = resourceReconciliation.UnclearFailures
			.Concat(uidReconciliation.UnclearFailures)
			.ToList();
		List<PhysicalDeleteRecoveredAnomaly> recoveredAnomalies =
			resourceReconciliation.RecoveredAnomalies
				.Concat(uidReconciliation.RecoveredAnomalies)
				.ToList();

		PhysicalDeleteResult result = new(
			resourceReconciliation.VerifiedDeletedPaths,
			uidReconciliation.VerifiedDeletedPaths,
			failures,
			unclearFailures,
			recoveredAnomalies
		);

		LogPhysicalDeleteResultInvariantViolations(result);
		return result;
	}

	private PhysicalDeleteAttempt CreatePhysicalDeleteAttempt(
		PhysicalDeleteFailureKind kind,
		string resourcePath,
		string targetPath,
		bool missingBeforeDeleteIsFailure
	)
	{
		PhysicalPathExistenceObservation preDeleteObservation =
			ObservePhysicalPathExistence(targetPath);
		PhysicalPathExistenceObservation firstPostDeleteObservation =
			CreateUnobservedPhysicalPathExistenceObservation(
				preDeleteObservation.GlobalPath,
				"No post-delete observation was performed because no delete call was attempted."
			);

		if (!preDeleteObservation.VerifiedExistsByBoth)
		{
			return new PhysicalDeleteAttempt(
				kind,
				resourcePath,
				targetPath,
				preDeleteObservation.GlobalPath,
				missingBeforeDeleteIsFailure,
				preDeleteObservation,
				PhysicalDeleteCallOutcome.NotAttempted,
				null,
				"DirAccess.RemoveAbsolute was not called because the strict pre-delete existence requirement was not satisfied.",
				firstPostDeleteObservation
			);
		}

		PhysicalDeleteCallOutcome callOutcome;
		Error? godotError = null;
		string callDetails;

		try
		{
			Error removeError = DirAccess.RemoveAbsolute(preDeleteObservation.GlobalPath);
			godotError = removeError;
			callOutcome = removeError == Error.Ok
				? PhysicalDeleteCallOutcome.ReturnedOk
				: PhysicalDeleteCallOutcome.ReturnedError;
			callDetails = $"DirAccess.RemoveAbsolute returned {removeError}.";
		}
		catch (Exception exception)
		{
			callOutcome = PhysicalDeleteCallOutcome.ThrewException;
			callDetails = $"DirAccess.RemoveAbsolute threw. Exception='{exception}'";
		}

		firstPostDeleteObservation = ObservePhysicalPathExistence(
			targetPath,
			preDeleteObservation.GlobalPath
		);

		return new PhysicalDeleteAttempt(
			kind,
			resourcePath,
			targetPath,
			preDeleteObservation.GlobalPath,
			missingBeforeDeleteIsFailure,
			preDeleteObservation,
			callOutcome,
			godotError,
			callDetails,
			firstPostDeleteObservation
		);
	}

	private PhysicalDeleteReconciliation ReconcilePhysicalDeleteAttempts(
		IReadOnlyList<PhysicalDeleteAttempt> attempts
	)
	{
		List<string> verifiedDeletedPaths = new();
		List<PhysicalDeleteFailure> failures = new();
		List<PhysicalDeleteFailure> unclearFailures = new();
		List<PhysicalDeleteRecoveredAnomaly> recoveredAnomalies = new();
		HashSet<string> recordedVerifiedPaths = new(StringComparer.OrdinalIgnoreCase);
		HashSet<string> recordedIssuePaths = new(StringComparer.OrdinalIgnoreCase);

		foreach (PhysicalDeleteAttempt attempt in attempts ?? Array.Empty<PhysicalDeleteAttempt>())
		{
			PhysicalPathExistenceObservation finalObservation;
			string finalClassification;

			if (!attempt.DeleteCallAttempted)
			{
				finalObservation = attempt.PreDeleteObservation;

				if (attempt.PreDeleteObservation.VerifiedMissingByBoth)
				{
					if (attempt.MissingBeforeDeleteIsFailure)
					{
						PhysicalDeleteFailure failure = new(
							attempt.Kind,
							PhysicalDeleteFailureReason.MissingBeforeDelete,
							attempt.ResourcePath,
							attempt.TargetPath,
							BuildPhysicalDeleteDiagnostic(
								attempt,
								finalObservation,
								"MissingBeforeDelete"
							)
						);
						AddPhysicalDeleteIssue(failures, recordedIssuePaths, failure);
						finalClassification = "MissingBeforeDelete";
					}
					else
					{
						finalClassification = "MissingBeforeDeleteNoAction";
					}
				}
				else
				{
					PhysicalDeleteFailure failure = new(
						attempt.Kind,
						PhysicalDeleteFailureReason.ExistenceVerificationFailed,
						attempt.ResourcePath,
						attempt.TargetPath,
						BuildPhysicalDeleteDiagnostic(
							attempt,
							finalObservation,
							"PreDeleteVerificationUnclear"
						)
					);
					AddPhysicalDeleteIssue(unclearFailures, recordedIssuePaths, failure);
					finalClassification = "PreDeleteVerificationUnclear";
				}

				LogPhysicalDeleteAttemptDiagnostic(
					attempt,
					finalObservation,
					finalClassification
				);
				continue;
			}

			finalObservation = ObservePhysicalPathExistence(
				attempt.TargetPath,
				attempt.GlobalPath
			);

			if (finalObservation.SystemIoCheckSucceeded && !finalObservation.SystemIoExists)
			{
				if (recordedVerifiedPaths.Add(attempt.TargetPath))
					verifiedDeletedPaths.Add(attempt.TargetPath);

				finalClassification = "VerifiedDeleted";
				string anomalyDetails = BuildRecoveredPhysicalDeleteAnomalyDetails(
					attempt,
					finalObservation
				);

				if (!string.IsNullOrWhiteSpace(anomalyDetails))
				{
					PhysicalDeleteRecoveredAnomaly anomaly = new(
						attempt.Kind,
						attempt.ResourcePath,
						attempt.TargetPath,
						anomalyDetails
					);
					recoveredAnomalies.Add(anomaly);
					DebugLogger.LogOperation(
						"Physical remove recovered delete anomaly",
						$"Kind={anomaly.Kind}, ResourcePath='{anomaly.ResourcePath}', AffectedPath='{anomaly.AffectedPath}', {anomaly.Details}"
					);
				}
			}
			else if (finalObservation.SystemIoCheckSucceeded && finalObservation.SystemIoExists)
			{
				PhysicalDeleteFailureReason reason =
					attempt.CallOutcome == PhysicalDeleteCallOutcome.ReturnedOk
						? PhysicalDeleteFailureReason.StillExistsAfterRemove
						: PhysicalDeleteFailureReason.RemoveCallFailed;
				finalClassification = reason == PhysicalDeleteFailureReason.StillExistsAfterRemove
					? "VerifiedStillExists"
					: "RemoveCallFailedAndVerifiedStillExists";
				PhysicalDeleteFailure failure = new(
					attempt.Kind,
					reason,
					attempt.ResourcePath,
					attempt.TargetPath,
					BuildPhysicalDeleteDiagnostic(
						attempt,
						finalObservation,
						finalClassification
					)
				);
				AddPhysicalDeleteIssue(failures, recordedIssuePaths, failure);
			}
			else
			{
				finalClassification = "FinalExistenceUnclear";
				PhysicalDeleteFailure failure = new(
					attempt.Kind,
					PhysicalDeleteFailureReason.ExistenceVerificationFailed,
					attempt.ResourcePath,
					attempt.TargetPath,
					BuildPhysicalDeleteDiagnostic(
						attempt,
						finalObservation,
						finalClassification
					)
				);
				AddPhysicalDeleteIssue(unclearFailures, recordedIssuePaths, failure);
			}

			LogPhysicalDeleteAttemptDiagnostic(
				attempt,
				finalObservation,
				finalClassification
			);
		}

		return new PhysicalDeleteReconciliation(
			verifiedDeletedPaths,
			failures,
			unclearFailures,
			recoveredAnomalies
		);
	}

	private PhysicalPathExistenceObservation ObservePhysicalPathExistence(
		string resourcePath,
		string knownGlobalPath = ""
	)
	{
		List<string> details = new();
		string globalPath = knownGlobalPath ?? "";
		bool globalPathResolutionSucceeded = !string.IsNullOrWhiteSpace(globalPath);

		if (string.IsNullOrWhiteSpace(resourcePath))
		{
			details.Add("ResourcePath='', existence verification could not inspect a Godot resource path.");
		}

		if (!globalPathResolutionSucceeded && !string.IsNullOrWhiteSpace(resourcePath))
		{
			try
			{
				globalPath = ProjectSettings.GlobalizePath(resourcePath);
				globalPathResolutionSucceeded = !string.IsNullOrWhiteSpace(globalPath);

				if (!globalPathResolutionSucceeded)
					details.Add("ProjectSettings.GlobalizePath returned an empty path.");
			}
			catch (Exception exception)
			{
				details.Add($"Phase=globalize, Exception='{exception}'");
			}
		}

		bool godotCheckSucceeded = false;
		bool godotExists = false;

		if (!string.IsNullOrWhiteSpace(resourcePath))
		{
			try
			{
				godotExists = FileAccess.FileExists(resourcePath);
				godotCheckSucceeded = true;
				details.Add($"Godot FileExists returned {godotExists}.");
			}
			catch (Exception exception)
			{
				details.Add($"Phase=godot-existence, Exception='{exception}'");
			}
		}

		bool systemIoCheckSucceeded = false;
		bool systemIoExists = false;

		if (globalPathResolutionSucceeded)
		{
			systemIoCheckSucceeded = TryGetSystemIoFileExistence(
				globalPath,
				out systemIoExists,
				out string systemExistenceDetails
			);
			details.Add(systemExistenceDetails);
		}
		else
		{
			details.Add("System.IO existence verification was not attempted because the global path was unavailable.");
		}

		return new PhysicalPathExistenceObservation(
			globalPathResolutionSucceeded,
			globalPath,
			godotCheckSucceeded,
			godotExists,
			systemIoCheckSucceeded,
			systemIoExists,
			$"ResourcePath='{resourcePath}', GlobalPath='{globalPath}' | {string.Join(" | ", details)}"
		);
	}

	private static PhysicalPathExistenceObservation CreateUnobservedPhysicalPathExistenceObservation(
		string globalPath,
		string details
	)
	{
		return new PhysicalPathExistenceObservation(
			!string.IsNullOrWhiteSpace(globalPath),
			globalPath ?? "",
			false,
			false,
			false,
			false,
			details ?? ""
		);
	}

	private string BuildRecoveredPhysicalDeleteAnomalyDetails(
		PhysicalDeleteAttempt attempt,
		PhysicalPathExistenceObservation finalObservation
	)
	{
		List<string> anomalyDetails = new();

		if (attempt.CallOutcome != PhysicalDeleteCallOutcome.ReturnedOk)
		{
			anomalyDetails.Add(
				$"RemoveCallOutcome={attempt.CallOutcome}, RemoveCallError={attempt.GodotError?.ToString() ?? "<none>"}, RemoveCallDetails='{attempt.CallDetails}', FinalDiskState=VerifiedMissing, FinalClassification=VerifiedDeleted."
			);
		}

		AddGodotDiskAbsenceDisagreementDetail(
			anomalyDetails,
			attempt.FirstPostDeleteObservation,
			"first post-delete observation"
		);
		AddGodotDiskAbsenceDisagreementDetail(
			anomalyDetails,
			finalObservation,
			"final reconciliation"
		);

		if (
			attempt.FirstPostDeleteObservation.ChecksDisagree
			&& !(attempt.FirstPostDeleteObservation.SystemIoCheckSucceeded
				&& !attempt.FirstPostDeleteObservation.SystemIoExists
				&& attempt.FirstPostDeleteObservation.GodotCheckSucceeded
				&& attempt.FirstPostDeleteObservation.GodotExists)
		)
		{
			anomalyDetails.Add(
				"The first Godot and System.IO post-delete observations disagreed before final reconciliation."
			);
		}

		return string.Join(" ", anomalyDetails);
	}

	private static void AddGodotDiskAbsenceDisagreementDetail(
		List<string> details,
		PhysicalPathExistenceObservation observation,
		string phase
	)
	{
		if (
			observation.GodotCheckSucceeded
			&& observation.GodotExists
			&& observation.SystemIoCheckSucceeded
			&& !observation.SystemIoExists
		)
		{
			details.Add(
				$"Godot observation disagreed with verified global disk absence during {phase}."
			);
		}
	}

	private string BuildPhysicalDeleteDiagnostic(
		PhysicalDeleteAttempt attempt,
		PhysicalPathExistenceObservation finalObservation,
		string finalClassification
	)
	{
		return
			$"ResourcePath='{attempt.ResourcePath}', TargetPath='{attempt.TargetPath}', GlobalPath='{attempt.GlobalPath}', PreDeleteGlobalPathResolutionSucceeded={attempt.PreDeleteObservation.GlobalPathResolutionSucceeded}, "
			+ $"PreDeleteGodotCheckSucceeded={attempt.PreDeleteObservation.GodotCheckSucceeded}, PreDeleteGodotExists={attempt.PreDeleteObservation.GodotExists}, "
			+ $"PreDeleteSystemCheckSucceeded={attempt.PreDeleteObservation.SystemIoCheckSucceeded}, PreDeleteSystemExists={attempt.PreDeleteObservation.SystemIoExists}, "
			+ $"RemoveCallOutcome={attempt.CallOutcome}, RemoveCallError={attempt.GodotError?.ToString() ?? "<none>"}, RemoveCallDetails='{attempt.CallDetails}', "
			+ $"FirstPostDeleteGodotCheckSucceeded={attempt.FirstPostDeleteObservation.GodotCheckSucceeded}, FirstPostDeleteGodotExists={attempt.FirstPostDeleteObservation.GodotExists}, "
			+ $"FirstPostDeleteSystemCheckSucceeded={attempt.FirstPostDeleteObservation.SystemIoCheckSucceeded}, FirstPostDeleteSystemExists={attempt.FirstPostDeleteObservation.SystemIoExists}, "
			+ $"FinalGodotCheckSucceeded={finalObservation.GodotCheckSucceeded}, FinalGodotExists={finalObservation.GodotExists}, "
			+ $"FinalSystemCheckSucceeded={finalObservation.SystemIoCheckSucceeded}, FinalSystemExists={finalObservation.SystemIoExists}, "
			+ $"FinalClassification={finalClassification}, "
			+ $"PreDeleteDetails='{attempt.PreDeleteObservation.Details}', FirstPostDeleteDetails='{attempt.FirstPostDeleteObservation.Details}', FinalDetails='{finalObservation.Details}'";
	}

	private void LogPhysicalDeleteAttemptDiagnostic(
		PhysicalDeleteAttempt attempt,
		PhysicalPathExistenceObservation finalObservation,
		string finalClassification
	)
	{
		DebugLogger.LogOperation(
			attempt.Kind == PhysicalDeleteFailureKind.ResourceFile
				? "Physical remove resource reconciled"
				: "Physical remove UID sidecar reconciled",
			BuildPhysicalDeleteDiagnostic(
				attempt,
				finalObservation,
				finalClassification
			)
		);
	}

	private static bool TryGetSystemIoFileExistence(
		string globalPath,
		out bool exists,
		out string details
	)
	{
		exists = false;

		try
		{
			exists = System.IO.File.Exists(globalPath);

			if (exists)
			{
				details = "System.IO.File.Exists returned true.";
				return true;
			}

			try
			{
				System.IO.File.GetAttributes(globalPath);
				exists = true;
				details =
					"System.IO.File.Exists returned false, but File.GetAttributes confirmed that the file exists.";
				return true;
			}
			catch (System.IO.FileNotFoundException)
			{
				details = "System.IO confirmed that the file is missing.";
				return true;
			}
			catch (System.IO.DirectoryNotFoundException)
			{
				details = "System.IO confirmed that the parent directory or file is missing.";
				return true;
			}
			catch (Exception exception)
			{
				details =
					$"System.IO.File.Exists returned false and File.GetAttributes could not verify absence. Exception='{exception}'";
				return false;
			}
		}
		catch (Exception exception)
		{
			details = $"System.IO existence verification threw. Exception='{exception}'";
			return false;
		}
	}

	private static void AddPhysicalDeleteIssue(
		List<PhysicalDeleteFailure> issues,
		HashSet<string> recordedIssuePaths,
		PhysicalDeleteFailure issue
	)
	{
		string issueKey = $"{issue.Kind}|{issue.FailedPath}";

		if (recordedIssuePaths.Add(issueKey))
			issues.Add(issue);
	}

	private void LogPhysicalDeleteResultInvariantViolations(PhysicalDeleteResult result)
	{
		HashSet<string> verifiedResourcePaths = result.VerifiedDeletedResourcePaths
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		HashSet<string> verifiedUidPaths = result.VerifiedDeletedUidSidecarPaths
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		foreach (PhysicalDeleteFailure issue in result.Failures.Concat(result.UnclearFailures))
		{
			bool overlapsVerifiedResult = issue.Kind == PhysicalDeleteFailureKind.ResourceFile
				? verifiedResourcePaths.Contains(issue.FailedPath)
				: verifiedUidPaths.Contains(issue.FailedPath);

			if (!overlapsVerifiedResult)
				continue;

			DebugLogger.LogOperation(
				"Physical remove result invariant violation",
				$"Kind={issue.Kind}, Path='{issue.FailedPath}' appeared in both a verified-deletion list and an issue list."
			);
		}
	}
	private void ShowPhysicalRemoveResultDialog(
		PhysicalDeleteResult result,
		IReadOnlyList<string> requestedResourcePaths,
		string metadataFailureMessage
	)
	{
		if (!IsValidGodotObject(_physicalRemoveIncompleteDialog))
			return;

		bool deletedAnyResource = result.VerifiedDeletedAnyResource;
		List<string> failedResourcePaths = GetPhysicalDeleteIssuePaths(
			result.Failures,
			PhysicalDeleteFailureKind.ResourceFile
		);
		List<string> unclearResourcePaths = GetPhysicalDeleteIssuePaths(
			result.UnclearFailures,
			PhysicalDeleteFailureKind.ResourceFile
		);
		List<string> failedUidPaths = GetPhysicalDeleteIssuePaths(
			result.Failures,
			PhysicalDeleteFailureKind.UidSidecar
		);
		List<string> unclearUidPaths = GetPhysicalDeleteIssuePaths(
			result.UnclearFailures,
			PhysicalDeleteFailureKind.UidSidecar
		);
		List<string> sections = new();

		if (!deletedAnyResource)
		{
			if (failedResourcePaths.Count > 0)
			{
				string noun = failedResourcePaths.Count == 1 ? "file" : "files";
				sections.Add(
					$"System Explorer could not delete the following project {noun}:\n\n{string.Join("\n", failedResourcePaths)}"
				);
			}

			if (unclearResourcePaths.Count > 0)
			{
				sections.Add(
					$"The final state of the following project files could not be verified:\n\n{string.Join("\n", unclearResourcePaths)}"
				);
			}

			if (failedResourcePaths.Count == 0 && unclearResourcePaths.Count == 0)
			{
				List<string> unresolvedResourcePaths = (requestedResourcePaths ?? Array.Empty<string>())
					.Where(path => !string.IsNullOrWhiteSpace(path))
					.Distinct(StringComparer.OrdinalIgnoreCase)
					.ToList();

				if (unresolvedResourcePaths.Count > 0)
				{
					string noun = unresolvedResourcePaths.Count == 1 ? "file" : "files";
					sections.Add(
						$"System Explorer could not delete the following project {noun}:\n\n{string.Join("\n", unresolvedResourcePaths)}"
					);
				}
			}

			sections.Add("No System Explorer metadata was removed.");
		}
		else
		{
			if (failedResourcePaths.Count > 0)
			{
				string noun = failedResourcePaths.Count == 1 ? "file" : "files";
				sections.Add(
					$"Some project files were deleted, but System Explorer could not delete the following {noun}:\n\n{string.Join("\n", failedResourcePaths)}"
				);
			}

			if (unclearResourcePaths.Count > 0)
			{
				sections.Add(
					$"Some project files were deleted, but the final state of the following files could not be verified:\n\n{string.Join("\n", unclearResourcePaths)}"
				);
			}

			if (failedResourcePaths.Count > 0 || unclearResourcePaths.Count > 0)
				sections.Add("The tree was updated for the files that were deleted.");
		}

		if (failedUidPaths.Count > 0)
		{
			sections.Add(
				$"For the resource files that were deleted, System Explorer could not delete the following UID sidecars:\n\n{string.Join("\n", failedUidPaths)}"
			);
		}

		if (unclearUidPaths.Count > 0)
		{
			sections.Add(
				$"For the resource files that were deleted, the final state of the following UID sidecars could not be verified:\n\n{string.Join("\n", unclearUidPaths)}"
			);
		}

		if (!string.IsNullOrWhiteSpace(metadataFailureMessage))
			sections.Add(metadataFailureMessage.Trim());

		if (sections.Count == 0)
			return;

		_physicalRemoveIncompleteDialog.Title = deletedAnyResource
			? "Remove Incomplete"
			: "Remove Failed";
		_physicalRemoveIncompleteDialog.DialogText = string.Join("\n\n", sections);

		foreach (PhysicalDeleteFailure failure in result.Failures.Concat(result.UnclearFailures))
		{
			DebugLogger.LogOperation(
				"Physical remove delete issue",
				$"Kind={failure.Kind}, Reason={failure.Reason}, ResourcePath={failure.ResourcePath}, FailedPath={failure.FailedPath}, Details={failure.Details}"
			);
		}

		SchedulePhysicalRemoveIncompleteDialogPresentationDeferred();
	}

	private void SchedulePhysicalRemoveIncompleteDialogPresentationDeferred()
	{
		string scheduledManagedAssemblyGeneration = ManagedAssemblyGeneration;
		CallDeferred(
			nameof(PresentPhysicalRemoveIncompleteDialogDeferred),
			scheduledManagedAssemblyGeneration
		);
	}

	private void PresentPhysicalRemoveIncompleteDialogDeferred(
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

		if (
			_editorOperationShutdownStarted
			|| !GodotObject.IsInstanceValid(this)
			|| !IsInsideTree()
			|| !IsValidGodotObject(_physicalRemoveIncompleteDialog)
		)
		{
			return;
		}

		_physicalRemoveIncompleteDialog.PopupCentered();
	}

	private static List<string> GetPhysicalDeleteIssuePaths(
		IReadOnlyList<PhysicalDeleteFailure> issues,
		PhysicalDeleteFailureKind kind
	)
	{
		return (issues ?? Array.Empty<PhysicalDeleteFailure>())
			.Where(issue => issue.Kind == kind)
			.Select(issue => issue.FailedPath)
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	private void OnRenameConfirmed()
	{
		string newName = _renameInput.Text.Trim().Trim('/');

		DebugLogger.LogOperation("Rename Confirmed", $"{_pendingRenameMetadata} -> {newName}");

		if (string.IsNullOrWhiteSpace(newName))
		{
			DebugLogger.Log("Rename cancelled: empty name.");
			return;
		}

		if (string.IsNullOrWhiteSpace(_pendingRenameMetadata))
			return;

		if (
			_pendingRenameMetadata.StartsWith("system::", StringComparison.Ordinal)
			&& ContainsReservedSystemNameSeparator(newName)
		)
		{
			_renameInput.Text = "";
			ShowRenameInputWarning(
				"Invalid System Name",
				"System names cannot contain \"::\" because it is reserved by System Explorer."
			);
			DebugLogger.LogOperation(
				"Rename System cancelled: reserved separator",
				newName
			);
			return;
		}

		if (
			_pendingRenameMetadata.StartsWith("folder::", StringComparison.Ordinal)
			&& ContainsReservedVirtualFolderSeparator(newName)
		)
		{
			_renameInput.Text = "";
			ShowRenameInputWarning(
				"Invalid Folder Name",
				"Folder names cannot contain \"::\" or \"|\" because those characters are reserved by System Explorer."
			);
			DebugLogger.LogOperation(
				"Rename Folder cancelled: reserved separator",
				newName
			);
			return;
		}

		bool renamesPhysicalResource =
			_pendingRenameMetadata.StartsWith("script::", StringComparison.Ordinal)
			|| _pendingRenameMetadata.StartsWith("sceneLink::", StringComparison.Ordinal);

		if (
			renamesPhysicalResource
			&& (newName.Contains('/') || newName.Contains('\\'))
		)
		{
			_renameInput.Text = "";
			ShowRenameInputWarning(
				"Invalid File Name",
				"The new file name cannot contain folder separators. Rename only changes the file name, not the folder path."
			);
			DebugLogger.LogOperation(
				"Rename physical resource cancelled: folder separator",
				newName
			);
			return;
		}

		if (renamesPhysicalResource && newName.Contains('|'))
		{
			_renameInput.Text = "";
			ShowRenameInputWarning(
				"Invalid File Name",
				"The new file name cannot contain \"|\" because it is reserved by System Explorer."
			);
			DebugLogger.LogOperation(
				"Rename physical resource cancelled: reserved separator",
				newName
			);
			return;
		}

		using TreeOperationDialogScope operationScope = BeginTreeOperationDialogScope(
			"Rename Failed",
			CloseRenameUiAfterFailure
		);

		RenameMutationResult result;
		RenameConflictItemType itemType;
		bool renameHandledPersistence = false;
		bool folderBindingsChanged = false;
		SystemsAndFolderBindingsSnapshot metadataSnapshot = null;
		string oldSystemName = "";
		string oldFolderMetadata = "";
		string newFolderPath = "";
		string oldScenePathForSelection = "";
		string finalScenePath = "";
		TreeSelectionOperationState renameSelectionState =
			_pendingNonScriptRenameTreeSelectionState;
		PersistentTreeSelection? renamedSelection = null;

		if (_pendingRenameMetadata.StartsWith("system::"))
		{
			itemType = RenameConflictItemType.System;
			oldSystemName = _pendingRenameMetadata.Replace("system::", "");
			metadataSnapshot = CaptureSystemsAndFolderBindingsSnapshot();
			result = RenameSystem(
				oldSystemName,
				newName,
				out folderBindingsChanged
			);

			if (result == RenameMutationResult.Success)
			{
				renamedSelection = new PersistentTreeSelection(
					newName,
					$"system::{newName}"
				);
			}
		}
		else if (_pendingRenameMetadata.StartsWith("folder::"))
		{
			itemType = RenameConflictItemType.Folder;
			oldFolderMetadata = _pendingRenameMetadata;
			metadataSnapshot = CaptureSystemsAndFolderBindingsSnapshot();
			result = RenameFolder(
				_pendingRenameMetadata,
				newName,
				out newFolderPath,
				out folderBindingsChanged
			);

			if (result == RenameMutationResult.Success)
			{
				string systemName = GetSystemNameFromMetadata(oldFolderMetadata);
				renamedSelection = new PersistentTreeSelection(
					systemName,
					$"folder::{systemName}::{newFolderPath}"
				);
			}
		}
		else if (_pendingRenameMetadata.StartsWith("script::"))
		{
			itemType = RenameConflictItemType.Script;
			renameHandledPersistence = true;
			result = RenameScript(_pendingRenameMetadata, newName);
		}
		else if (_pendingRenameMetadata.StartsWith("sceneLink::"))
		{
			itemType = RenameConflictItemType.Scene;
			renameHandledPersistence = true;
			string selectedSceneEntry = GetEntryFromMetadata(_pendingRenameMetadata);
			result = RenameScene(
				_pendingRenameMetadata,
				newName,
				out finalScenePath
			);

			if (
				result == RenameMutationResult.Success
				&& TryGetUnambiguousPhysicalResourcePath(
					selectedSceneEntry,
					true,
					out string selectedOldScenePath
				)
			)
			{
				oldScenePathForSelection = NormalizeRenameResourcePath(
					selectedOldScenePath
				);
			}

			if (
				result == RenameMutationResult.Success
				&& renameSelectionState != null
				&& renameSelectionState.IsValid
			)
			{
				renameSelectionState = RemapTreeSelectionStateAfterSceneRename(
					renameSelectionState,
					oldScenePathForSelection,
					finalScenePath
				);
				renamedSelection = renameSelectionState.OriginalSelection;
			}
		}
		else
		{
			ReportTreeOperationFailure(
				"System Explorer could not identify the selected item for renaming.",
				_pendingRenameMetadata
			);
			DebugLogger.LogOperation(
				"Rename cancelled: unidentified target",
				_pendingRenameMetadata
			);
			return;
		}

		if (result == RenameMutationResult.NameConflict)
		{
			_renameInput.Text = "";
			ShowRenameNameConflictWarning(itemType);
			return;
		}

		if (result == RenameMutationResult.Failed)
		{
			if (!HasActiveTreeOperationFailure)
			{
				ReportTreeOperationFailure(
					"System Explorer could not complete the rename operation.",
					$"{_pendingRenameMetadata} -> {newName}"
				);
			}

			DebugLogger.LogOperation(
				"Rename cancelled: mutation failed",
				$"{_pendingRenameMetadata} -> {newName}"
			);
			return;
		}

		if (result == RenameMutationResult.NoChange)
		{
			_renameDialog.Hide();
			_pendingRenameMetadata = "";
			_pendingScriptRenameTreeState = null;
			_pendingNonScriptRenameTreeSelectionState = null;
			_renameInput.Text = "";
			return;
		}

		if (
			!renameHandledPersistence
			&& !TryPersistReversibleSystemsAndFolderBindingsMutation(
				metadataSnapshot,
				systemsChanged: true,
				folderBindingsChanged: folderBindingsChanged,
				operationName: itemType == RenameConflictItemType.System
					? "Rename System"
					: "Rename Folder"
			)
		)
		{
			if (!HasActiveTreeOperationFailure)
			{
				ReportTreeOperationFailure(
					"System Explorer could not save the rename operation. The in-memory metadata was restored."
				);
			}

			DebugLogger.LogOperation(
				"Rename cancelled: coordinated persistence failed",
				$"{_pendingRenameMetadata} -> {newName}"
			);
			return;
		}

		if (itemType == RenameConflictItemType.System)
			ForceExpandAfterSystemRename(oldSystemName, newName);
		else if (itemType == RenameConflictItemType.Folder)
			ForceExpandAfterFolderRename(oldFolderMetadata, newFolderPath);

		_renameDialog.Hide();
		_pendingRenameMetadata = "";
		_renameInput.Text = "";

		if (renameHandledPersistence)
		{
			if (
				itemType == RenameConflictItemType.Scene
				&& renamedSelection.HasValue
			)
			{
				RestoreNonScriptRenameSelectionAfterRebuild(
					renameSelectionState,
					renamedSelection.Value,
					"Rename Scene"
				);
			}

			_pendingNonScriptRenameTreeSelectionState = null;
			return;
		}

		BuildTree();

		if (renamedSelection.HasValue)
		{
			RestoreNonScriptRenameSelectionAfterRebuild(
				renameSelectionState,
				renamedSelection.Value,
				itemType == RenameConflictItemType.System
					? "Rename System"
					: "Rename Folder"
			);
		}

		_pendingNonScriptRenameTreeSelectionState = null;
	}

	private void ForceExpandAfterSystemRename(string oldSystemName, string newSystemName)
	{
		ForceExpandSystem(newSystemName);

		foreach (string metadata in _expandedItems.ToList())
		{
			if (metadata == $"system::{oldSystemName}")
			{
				_forcedExpandedItems.Add($"system::{newSystemName}");
				continue;
			}

			if (metadata.StartsWith($"folder::{oldSystemName}::"))
			{
				string folderPath = metadata.Replace($"folder::{oldSystemName}::", "");
				_forcedExpandedItems.Add($"folder::{newSystemName}::{folderPath}");
			}
		}
	}

	private void ForceExpandAfterFolderRename(string oldMetadata, string newFolderPath)
	{
		string systemName = GetSystemNameFromMetadata(oldMetadata);
		string oldFolderPath = GetFolderPathFromMetadata(oldMetadata);

		if (string.IsNullOrWhiteSpace(systemName) || string.IsNullOrWhiteSpace(newFolderPath))
			return;

		ForceExpandFolderPath(systemName, newFolderPath);

		foreach (string metadata in _expandedItems.ToList())
		{
			if (!metadata.StartsWith($"folder::{systemName}::"))
				continue;

			string folderPath = metadata.Replace($"folder::{systemName}::", "");

			if (folderPath == oldFolderPath)
			{
				_forcedExpandedItems.Add($"folder::{systemName}::{newFolderPath}");
				continue;
			}

			if (folderPath.StartsWith($"{oldFolderPath}/"))
			{
				string childPath = folderPath.Replace($"{oldFolderPath}/", $"{newFolderPath}/");
				_forcedExpandedItems.Add($"folder::{systemName}::{childPath}");
			}
		}
	}

	private RenameMutationResult RenameSystem(
		string oldName,
		string newName,
		out bool folderBindingsChanged
	)
	{
		folderBindingsChanged = false;

		if (!EnsureSystemsLoadedForTreeOperation("Rename System"))
			return RenameMutationResult.Failed;

		if (!EnsureSystemAvailable(oldName, "Rename System"))
			return RenameMutationResult.Failed;

		if (string.Equals(oldName, newName, StringComparison.Ordinal))
			return RenameMutationResult.NoChange;

		if (_systems.ContainsKey(newName))
		{
			DebugLogger.LogOperation("Rename System failed: name conflict", newName);
			return RenameMutationResult.NameConflict;
		}

		List<string> entries = _systems[oldName];
		_systems.Remove(oldName);
		_systems[newName] = entries;
		folderBindingsChanged = MigrateFolderBindingsForSystemRename(oldName, newName);

		DebugLogger.LogOperation("Rename System Mutated", $"{oldName} -> {newName}");

		return RenameMutationResult.Success;
	}

	private RenameMutationResult RenameFolder(
		string metadata,
		string newFolderName,
		out string newFolderPath,
		out bool folderBindingsChanged
	)
	{
		newFolderPath = "";
		folderBindingsChanged = false;

		string[] parts = metadata.Split("::");

		if (parts.Length < 3)
			return RenameMutationResult.Failed;

		if (!EnsureSystemsLoadedForTreeOperation("Rename Folder"))
			return RenameMutationResult.Failed;

		string systemName = parts[1];
		string oldFolderPath = parts[2];

		if (!EnsureSystemAvailable(systemName, "Rename Folder"))
			return RenameMutationResult.Failed;

		string parentPath = "";

		if (oldFolderPath.Contains("/"))
			parentPath = oldFolderPath.Substring(0, oldFolderPath.LastIndexOf('/'));

		newFolderPath = string.IsNullOrWhiteSpace(parentPath)
			? newFolderName
			: $"{parentPath}/{newFolderName}";

		if (string.Equals(newFolderPath, oldFolderPath, StringComparison.Ordinal))
			return RenameMutationResult.NoChange;

		if (DoesFolderPathExistInSystem(systemName, newFolderPath, oldFolderPath))
		{
			DebugLogger.LogOperation(
				"Rename Folder failed: name conflict",
				$"{systemName}: {oldFolderPath} -> {newFolderPath}"
			);
			return RenameMutationResult.NameConflict;
		}

		List<string> updatedEntries = new();

		foreach (string entry in _systems[systemName])
		{
			if (entry.StartsWith("folder::"))
			{
				string folderEntryPath = GetFolderPathFromFolderEntry(entry);

				if (folderEntryPath == oldFolderPath)
				{
					updatedEntries.Add(BuildFolderEntry(newFolderPath, IsEntryLocked(entry)));
					continue;
				}

				if (folderEntryPath.StartsWith($"{oldFolderPath}/"))
				{
					string childFolderPath = folderEntryPath.Replace(
						$"{oldFolderPath}/",
						$"{newFolderPath}/"
					);
					updatedEntries.Add(BuildFolderEntry(childFolderPath, IsEntryLocked(entry)));
					continue;
				}
			}

			if (entry.StartsWith($"{oldFolderPath}|"))
			{
				updatedEntries.Add(entry.Replace($"{oldFolderPath}|", $"{newFolderPath}|"));
				continue;
			}

			if (entry.StartsWith($"{oldFolderPath}/"))
			{
				updatedEntries.Add(entry.Replace($"{oldFolderPath}/", $"{newFolderPath}/"));
				continue;
			}

			updatedEntries.Add(entry);
		}

		_systems[systemName] = updatedEntries.Distinct().ToList();
		folderBindingsChanged = MigrateFolderBindingsForFolderRename(
			systemName,
			oldFolderPath,
			newFolderPath
		);

		DebugLogger.LogOperation(
			"Rename Folder Mutated",
			$"{systemName}: {oldFolderPath} -> {newFolderPath}"
		);

		return RenameMutationResult.Success;
	}

	private sealed class ScriptRenameTreeState
	{
		public string SystemName { get; init; } = "";
		public string FolderPath { get; init; } = "";
		public string Entry { get; init; } = "";
		public string Metadata { get; init; } = "";
		public bool WasFiltering { get; init; }
		public string FilterText { get; init; } = "";
		public HashSet<string> ExpansionState { get; init; } =
			new(StringComparer.Ordinal);
		public Control FocusOwnerBeforeDialog { get; init; }
		public bool TreeHadFocusBeforeDialog { get; init; }
		public bool IsValid =>
			!string.IsNullOrWhiteSpace(SystemName)
			&& !string.IsNullOrWhiteSpace(Entry)
			&& Metadata.StartsWith("script::", StringComparison.Ordinal);
	}

	private readonly record struct ScriptRenameEditorState(
		string OldScriptPath,
		ulong ScriptInstanceId,
		ulong ScriptEditorBaseInstanceId,
		ulong TextEditorInstanceId,
		bool WasUnsaved,
		string BufferText,
		int FirstVisibleLine,
		int ScrollHorizontal,
		double ScrollVertical,
		int CaretLine,
		int CaretColumn,
		bool HadSelection,
		int SelectionFromLine,
		int SelectionFromColumn,
		int SelectionToLine,
		int SelectionToColumn,
		int SelectionOriginLine,
		int SelectionOriginColumn,
		bool RenamedScriptWasActive,
		Script ActiveScriptBeforeOperation
	);

	private sealed class ScriptRenameOpenResourceBinding
	{
		public Script Script { get; init; }
		public ScriptEditorBase ScriptEditorBase { get; init; }
		public TextEdit TextEditor { get; init; }
		public ulong ScriptInstanceId { get; init; }
		public ulong ScriptEditorBaseInstanceId { get; init; }
		public ulong TextEditorInstanceId { get; init; }
	}

	private sealed class ScriptRenameOriginalResourceSet
	{
		public ScriptRenameOriginalResourceSet(
			string canonicalOldScriptPath,
			string finalTargetScriptPath,
			IEnumerable<Script> scripts
		)
		{
			CanonicalOldScriptPath = ScriptPathUtility.Normalize(canonicalOldScriptPath);
			FinalTargetScriptPath = ScriptPathUtility.Normalize(finalTargetScriptPath);

			List<ulong> instanceIds = new();
			HashSet<ulong> seenInstanceIds = new();
			List<string> reportedPaths = new();
			HashSet<string> seenReportedPaths = new(StringComparer.Ordinal);

			foreach (Script script in scripts ?? Array.Empty<Script>())
			{
				if (script == null || !GodotObject.IsInstanceValid(script))
					continue;

				ulong instanceId = script.GetInstanceId();

				if (instanceId != 0 && seenInstanceIds.Add(instanceId))
					instanceIds.Add(instanceId);

				string reportedPath = ScriptPathUtility.Normalize(script.ResourcePath);

				if (!string.IsNullOrWhiteSpace(reportedPath) && seenReportedPaths.Add(reportedPath))
					reportedPaths.Add(reportedPath);
			}

			OriginalScriptInstanceIds = instanceIds.AsReadOnly();
			OriginalReportedPaths = reportedPaths.AsReadOnly();
		}

		public string CanonicalOldScriptPath { get; }
		public string FinalTargetScriptPath { get; }
		public IReadOnlyList<ulong> OriginalScriptInstanceIds { get; }
		public IReadOnlyList<string> OriginalReportedPaths { get; }
		public int OriginalResourceCount => OriginalScriptInstanceIds.Count;
		public bool IsValid =>
			!string.IsNullOrWhiteSpace(CanonicalOldScriptPath)
			&& !string.IsNullOrWhiteSpace(FinalTargetScriptPath)
			&& OriginalScriptInstanceIds.Count > 0;
		public bool ContainsInstanceId(ulong instanceId) =>
			instanceId != 0 && OriginalScriptInstanceIds.Contains(instanceId);
	}

	private const int ScriptRenameEditorRestoreMaxDeferredAttempts = 3;

	private enum ScriptRenameEditorRestoreMode
	{
		SuccessfulRename,
		RestoreOriginalAfterRenameFailure,
		RestoreOriginalAfterCloseFailure,
	}

	private sealed class PendingScriptRenameEditorRestore
	{
		public string TargetScriptPath { get; init; } = "";
		public string CanonicalOldScriptPath { get; init; } = "";
		public string FinalTargetScriptPath { get; init; } = "";
		public ulong PrimaryOldScriptInstanceId { get; init; }
		public IReadOnlyList<ulong> OriginalScriptInstanceIds { get; init; } =
			Array.Empty<ulong>();
		public IReadOnlyList<string> OriginalReportedPaths { get; init; } =
			Array.Empty<string>();
		public ScriptRenameEditorState EditorState { get; init; }
		public ScriptRenameTreeState TreeState { get; init; }
		public string SelectedEntry { get; init; } = "";
		public Script LoadedScript { get; init; }
		public ulong LoadedScriptInstanceId { get; init; }
		public int DeferredAttemptCount { get; set; }
		public bool IsCompleting { get; set; }
		public bool EndSyncSuppression { get; init; }
		public ScriptRenameEditorRestoreMode Mode { get; init; }
		public DeferredTreeOperationDialogPresentation DeferredFailurePresentation
		{
			get;
			set;
		}
		public bool IsValid =>
			!string.IsNullOrWhiteSpace(TargetScriptPath)
			&& !string.IsNullOrWhiteSpace(CanonicalOldScriptPath)
			&& !string.IsNullOrWhiteSpace(FinalTargetScriptPath)
			&& PrimaryOldScriptInstanceId != 0
			&& OriginalScriptInstanceIds != null
			&& OriginalScriptInstanceIds.Count > 0
			&& OriginalScriptInstanceIds.Contains(PrimaryOldScriptInstanceId)
			&& PrimaryOldScriptInstanceId == EditorState.ScriptInstanceId
			&& OriginalReportedPaths != null
			&& OriginalReportedPaths.Count > 0
			&& TreeState != null
			&& TreeState.IsValid
			&& LoadedScript != null
			&& LoadedScriptInstanceId != 0
			&& !OriginalScriptInstanceIds.Contains(LoadedScriptInstanceId);
	}

	private readonly ScriptEditorTabService _scriptEditorTabService = new();
	private ScriptRenameTreeState _pendingScriptRenameTreeState;
	private ScriptRenameTreeState _deferredScriptRenameTreeState;
	private string _deferredScriptRenameSelectedEntry = "";
	private bool _deferredScriptRenameEndSyncSuppression;
	private PendingScriptRenameEditorRestore _pendingScriptRenameEditorRestore;
	private bool _renameFilesystemFinalStateRefreshQueued;

	private void RequestRenameFilesystemFinalStateRefresh()
	{
		if (_renameFilesystemFinalStateRefreshQueued)
			return;

		_renameFilesystemFinalStateRefreshQueued = true;
		CallDeferred(nameof(RunQueuedRenameFilesystemFinalStateRefresh));
	}

	private void RunQueuedRenameFilesystemFinalStateRefresh()
	{
		if (!_renameFilesystemFinalStateRefreshQueued)
			return;

		if (!EnsureManagedAssemblyStateCurrent("Rename Filesystem Final-State Refresh"))
		{
			_renameFilesystemFinalStateRefreshQueued = false;
			return;
		}

		if (!_renameFilesystemFinalStateRefreshQueued)
			return;

		EditorFileSystem resourceFilesystem =
			EditorInterface.Singleton?.GetResourceFilesystem();

		if (!IsValidGodotObject(resourceFilesystem))
		{
			_renameFilesystemFinalStateRefreshQueued = false;
			DebugLogger.Log(
				"Rename filesystem final-state refresh skipped: EditorFileSystem unavailable."
			);
			return;
		}

		if (resourceFilesystem.IsScanning())
		{
			CallDeferred(nameof(RunQueuedRenameFilesystemFinalStateRefresh));
			return;
		}

		_renameFilesystemFinalStateRefreshQueued = false;
		resourceFilesystem.Scan();
	}

	private RenameMutationResult RenameScript(string metadata, string newName)
	{
		if (_pendingScriptRenameEditorRestore != null)
		{
			ReportTreeOperationFailureOrWarning(
				"System Explorer is still restoring the previous renamed script in Godot's Script Editor. Try again in a moment."
			);
			DebugLogger.LogOperation("Rename Script blocked: previous editor restore pending");
			_pendingScriptRenameTreeState = null;
			return RenameMutationResult.Failed;
		}

		string entry = GetEntryFromMetadata(metadata);

		if (
			!TryGetUnambiguousPhysicalResourcePath(
				entry,
				false,
				out string unambiguousScriptPath
			)
		)
		{
			ReportTreeOperationFailureOrWarning(
				"System Explorer cancelled the script rename because the stored entry could not be interpreted safely. No project file was changed."
			);
			DebugLogger.LogOperation(
				"Rename Script failed: ambiguous stored entry",
				entry
			);
			_pendingScriptRenameTreeState = null;
			return RenameMutationResult.Failed;
		}

		string oldScriptPath = ScriptPathUtility.Normalize(unambiguousScriptPath);
		ScriptRenameTreeState treeState = _pendingScriptRenameTreeState;
		_pendingScriptRenameTreeState = null;

		if (treeState == null || !treeState.IsValid || treeState.Entry != entry)
			treeState = CaptureScriptRenameTreeState(entry);

		if (treeState == null || !treeState.IsValid)
		{
			ReportTreeOperationFailureOrWarning(
				"System Explorer could not identify the exact selected system/folder entry before renaming the script. The rename was cancelled."
			);
			DebugLogger.LogOperation("Rename Script failed: selected tree identity unavailable", entry);
			return RenameMutationResult.Failed;
		}

		if (!FileAccess.FileExists(oldScriptPath))
		{
			ReportTreeOperationFailureOrWarning($"File does not exist: {oldScriptPath}");
			DebugLogger.LogOperation("Rename Script failed: file missing", oldScriptPath);
			return RenameMutationResult.Failed;
		}

		if (newName.Contains("/") || newName.Contains("\\"))
		{
			ReportTreeOperationFailureOrWarning(
				"Script rename only supports changing the file name, not the folder path."
			);
			DebugLogger.LogOperation("Rename Script failed: invalid name", newName);
			return RenameMutationResult.Failed;
		}

		string newFileName = newName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
			? newName
			: $"{newName}.cs";
		string folderPath = ScriptPathUtility.Normalize(oldScriptPath.GetBaseDir());
		string newScriptPath = CombineResourcePath(folderPath, newFileName);
		bool isExactSamePath = string.Equals(
			oldScriptPath,
			newScriptPath,
			StringComparison.Ordinal
		);
		bool isCaseOnlyRename =
			!isExactSamePath
			&& string.Equals(oldScriptPath, newScriptPath, StringComparison.OrdinalIgnoreCase);

		if (isExactSamePath)
			return RenameMutationResult.NoChange;

		if (
			!TryGetMatchingOpenScriptResources(
				oldScriptPath,
				out EditorInterface editorInterface,
				out ScriptEditor scriptEditor,
				out List<Script> matchingOpenScripts
			)
		)
		{
			return RenameMutationResult.Failed;
		}

		if (matchingOpenScripts.Count == 0)
		{
			ReportTreeOperationFailureOrWarning(
				$"System Explorer could not safely rename the script because its Script Editor tab was not open:\n{oldScriptPath}\n\nOpen the script through System Explorer and try again."
			);
			DebugLogger.LogOperation("Rename Script failed: target script tab not open", oldScriptPath);
			return RenameMutationResult.Failed;
		}

		ScriptEditorBufferGroupLookupResult groupLookupResult =
			OpenScriptEditorBufferLocator.LocateOpenScriptEditorGroupsWithoutActivation(
				scriptEditor,
				new[] { oldScriptPath },
				new[] { oldScriptPath }
			);
		bool targetPathUnsafe = groupLookupResult.UnsafeOpenScriptPaths.Any(path =>
			string.Equals(path, oldScriptPath, StringComparison.OrdinalIgnoreCase)
		);
		bool targetPathAmbiguous = groupLookupResult.AmbiguousOpenScriptPaths.Any(path =>
			string.Equals(path, oldScriptPath, StringComparison.OrdinalIgnoreCase)
		);

		bool hasTargetGroup = groupLookupResult.OpenEditorGroupsByPath.TryGetValue(
			oldScriptPath,
			out OpenScriptEditorBufferGroup openEditorGroup
		);

		bool hasMultipleMatchingOpenScripts = matchingOpenScripts.Count >= 2;

		if (
			!groupLookupResult.Success
			|| !hasTargetGroup
			|| openEditorGroup == null
			|| openEditorGroup.Buffers.Count != matchingOpenScripts.Count
			|| targetPathUnsafe
			|| targetPathAmbiguous
		)
		{
			string groupFailureMessage = hasMultipleMatchingOpenScripts
				? $"Rename Script cancelled: Godot reported multiple open script entries for '{oldScriptPath}', but System Explorer could not safely verify every associated editor buffer as the same saved script. Save or close the duplicate entries and try again."
				: $"Rename Script cancelled: System Explorer could not safely verify the open editor buffer for '{oldScriptPath}' against the saved script. Save or reopen the script and try again.";
			ReportTreeOperationFailureOrWarning(groupFailureMessage);
			DebugLogger.LogOperation(
				"Rename Script failed: incomplete or ambiguous editor-buffer group",
				$"path='{oldScriptPath}', resources={matchingOpenScripts.Count}, groupMembers={openEditorGroup?.Buffers.Count ?? 0}, lookupFailure={groupLookupResult.Failure}, unsafe={targetPathUnsafe}, ambiguous={targetPathAmbiguous}"
			);
			return RenameMutationResult.Failed;
		}

		Script activeScriptBeforeOperation = scriptEditor.GetCurrentScript();

		if (
			!TrySelectPrimaryScriptRenameResource(
				matchingOpenScripts,
				activeScriptBeforeOperation,
				oldScriptPath,
				out Script primaryOpenScript
			)
		)
		{
			string primaryFailureMessage =
				$"Rename Script cancelled: multiple verified script entries were open for '{oldScriptPath}', but System Explorer could not identify a unique active or exact-casing entry whose editor state should be preserved.";
			ReportTreeOperationFailureOrWarning(primaryFailureMessage);
			DebugLogger.LogOperation("Rename Script failed: ambiguous primary resource", oldScriptPath);
			return RenameMutationResult.Failed;
		}

		if (isCaseOnlyRename)
		{
			if (
				!TryCheckCaseOnlyRenameTargetConflict(
					oldScriptPath,
					newScriptPath,
					"script",
					"Rename Script",
					out bool hasTargetConflict
				)
			)
			{
				return RenameMutationResult.Failed;
			}

			if (hasTargetConflict)
			{
				DebugLogger.LogOperation(
					"Rename Script failed: case-only name conflict",
					newScriptPath
				);
				return RenameMutationResult.NameConflict;
			}
		}
		else if (FileAccess.FileExists(newScriptPath))
		{
			DebugLogger.LogOperation("Rename Script failed: name conflict", newScriptPath);
			return RenameMutationResult.NameConflict;
		}

		if (!TryCheckUidRenameTargetConflict(oldScriptPath, newScriptPath, isCaseOnlyRename))
			return RenameMutationResult.Failed;

		if (!EnsureSystemsLoadedForTreeOperation("Rename Script"))
			return RenameMutationResult.Failed;

		if (
			!DoesAnySystemContainEntry(entry)
			&& (
				!TryRecoverSystemsFromDisk("Rename Script")
				|| !DoesAnySystemContainEntry(entry)
			)
		)
		{
			ReportTreeOperationFailure(
				"System Explorer could not verify the script metadata entry required for Rename Script. The script file was not renamed.",
				$"Entry='{entry}', Path='{oldScriptPath}'"
			);
			return RenameMutationResult.Failed;
		}

		if (
			!TryPreflightMetadataPersistenceForPhysicalMutation(
				"Rename Script",
				systemsRequired: true,
				folderBindingsRequired: false,
				physicalConsequence: "The script file was not renamed."
			)
		)
		{
			return RenameMutationResult.Failed;
		}

		bool hasValidActiveScriptBeforeOperation =
			activeScriptBeforeOperation != null
			&& GodotObject.IsInstanceValid(activeScriptBeforeOperation);
		bool renamedScriptWasActive =
			hasValidActiveScriptBeforeOperation
			&& matchingOpenScripts.Any(script =>
				IsSameScriptResource(activeScriptBeforeOperation, script)
			);
		ScriptRenameOriginalResourceSet originalResources = new(
			oldScriptPath,
			newScriptPath,
			matchingOpenScripts
		);
		bool syncSuppressionQueuedForDeferredEnd = false;

		BeginScriptEditorSyncSuppression();

		try
		{
			if (
				!TryBindScriptRenameResourcesToVerifiedGroup(
					editorInterface,
					scriptEditor,
					matchingOpenScripts,
					openEditorGroup,
					activeScriptBeforeOperation,
					oldScriptPath,
					out List<ScriptRenameOpenResourceBinding> resourceBindings,
					out string bindingFailureDetail
				)
			)
			{
				string bindingFailureMessage =
					$"Rename Script cancelled: an open Script resource for '{oldScriptPath}' could not be bound to a unique member of the verified editor-buffer group.";
				ReportTreeOperationFailure(
					bindingFailureMessage,
					bindingFailureDetail
				);
				DebugLogger.LogOperation(
					"Rename Script failed: Script/TextEdit binding",
					bindingFailureDetail
				);
				TryRestoreScriptRenamePreCloseEditorContext(
					editorInterface,
					scriptEditor,
					activeScriptBeforeOperation,
					primaryOpenScript,
					primaryBinding: null,
					editorState: null,
					renamedScriptWasActive: renamedScriptWasActive
				);
				QueueScriptRenameTreeRestore(treeState, entry, endSyncSuppression: true);
				syncSuppressionQueuedForDeferredEnd = true;
				return RenameMutationResult.Failed;
			}

			ulong primaryOpenScriptInstanceId = primaryOpenScript.GetInstanceId();
			ScriptRenameOpenResourceBinding primaryBinding = resourceBindings.SingleOrDefault(binding =>
				binding.ScriptInstanceId == primaryOpenScriptInstanceId
			);

			if (primaryBinding == null)
			{
				string primaryBindingFailureMessage =
					$"Rename Script cancelled: the deterministic primary Script resource for '{oldScriptPath}' was not present in the fully verified Script/TextEdit binding set.";
				ReportTreeOperationFailureOrWarning(primaryBindingFailureMessage);
				DebugLogger.LogOperation(
					"Rename Script failed: primary binding missing",
					$"path='{oldScriptPath}', primaryScriptId={primaryOpenScriptInstanceId}, bindings={resourceBindings.Count}"
				);
				TryRestoreScriptRenamePreCloseEditorContext(
					editorInterface,
					scriptEditor,
					activeScriptBeforeOperation,
					primaryOpenScript,
					primaryBinding: null,
					editorState: null,
					renamedScriptWasActive: renamedScriptWasActive
				);
				QueueScriptRenameTreeRestore(treeState, entry, endSyncSuppression: true);
				syncSuppressionQueuedForDeferredEnd = true;
				return RenameMutationResult.Failed;
			}

			ScriptRenameEditorState preAutosaveEditorState = CaptureScriptRenameEditorState(
				oldScriptPath,
				primaryBinding.Script,
				primaryBinding.ScriptEditorBase,
				primaryBinding.TextEditor,
				renamedScriptWasActive,
				activeScriptBeforeOperation
			);

			ScriptEditorBufferAutosaveOperationResult autosaveResult =
				OpenScriptEditorBufferAutosaveCoordinator.TryAutosaveGroupIfNeeded(
					openEditorGroup
				);

			if (!autosaveResult.Success)
			{
				string autosaveFailureMessage =
					ScriptFileOperationAutosaveFailureMessageBuilder.Build(
						autosaveResult.FailedAutosave,
						"Rename Script"
					);

				ReportTreeOperationFailureOrWarning(autosaveFailureMessage);
				DebugLogger.LogOperation(
					"Rename Script failed: group autosave",
					autosaveFailureMessage
				);
				TryRestoreScriptRenamePreCloseEditorContext(
					editorInterface,
					scriptEditor,
					activeScriptBeforeOperation,
					primaryBinding.Script,
					primaryBinding,
					preAutosaveEditorState,
					renamedScriptWasActive
				);
				QueueScriptRenameTreeRestore(treeState, entry, endSyncSuppression: true);
				syncSuppressionQueuedForDeferredEnd = true;
				return RenameMutationResult.Failed;
			}

			ScriptRenameEditorState editorState = CaptureScriptRenameEditorState(
				oldScriptPath,
				primaryBinding.Script,
				primaryBinding.ScriptEditorBase,
				primaryBinding.TextEditor,
				renamedScriptWasActive,
				activeScriptBeforeOperation
			);
			List<ScriptRenameOpenResourceBinding> closeOrder = resourceBindings
				.Where(binding => binding.ScriptInstanceId != primaryBinding.ScriptInstanceId)
				.OrderBy(binding => ScriptPathUtility.Normalize(binding.Script.ResourcePath), StringComparer.Ordinal)
				.ThenBy(binding => binding.ScriptInstanceId)
				.Concat(new[] { primaryBinding })
				.ToList();

			foreach (ScriptRenameOpenResourceBinding binding in closeOrder)
			{
				if (
					!TryReactivateVerifiedScriptRenameBinding(
						editorInterface,
						scriptEditor,
						binding,
						oldScriptPath,
						out string reactivationFailureDetail
					)
				)
				{
					string closeFailureMessage =
						$"The exact Script/TextEdit binding could not be reactivated immediately before close. {reactivationFailureDetail}";
					bool recoveryRestoreQueued = TryRecoverScriptRenameAfterCloseFailure(
						editorInterface,
						scriptEditor,
						oldScriptPath,
						originalResources,
						primaryBinding,
						editorState,
						activeScriptBeforeOperation,
						treeState,
						entry,
						ref closeFailureMessage
					);
					ReportTreeOperationFailure(
						"System Explorer could not safely close every old script tab before renaming. The file was not changed.",
						closeFailureMessage
					);
					DebugLogger.LogOperation("Rename Script failed: close reactivation", closeFailureMessage);

					if (recoveryRestoreQueued)
					{
						TryAttachActiveRenameFailureToPendingEditorRestore();
						syncSuppressionQueuedForDeferredEnd = true;
					}
					else
					{
						QueueScriptRenameTreeRestore(treeState, entry, endSyncSuppression: true);
						syncSuppressionQueuedForDeferredEnd = true;
					}

					return RenameMutationResult.Failed;
				}

				ScriptEditorTabCloseResult closeResult =
					_scriptEditorTabService.TryCloseExactScriptTab(
						scriptEditor,
						binding.Script,
						binding.ScriptEditorBase,
						binding.TextEditor
					);

				if (!closeResult.Success)
				{
					string closeFailureMessage = closeResult.FailureMessage;
					bool recoveryRestoreQueued = TryRecoverScriptRenameAfterCloseFailure(
						editorInterface,
						scriptEditor,
						oldScriptPath,
						originalResources,
						primaryBinding,
						editorState,
						activeScriptBeforeOperation,
						treeState,
						entry,
						ref closeFailureMessage
					);
					ReportTreeOperationFailure(
						"System Explorer could not safely close every old script tab before renaming. The file was not changed.",
						closeFailureMessage
					);
					DebugLogger.LogOperation("Rename Script failed: close tab group", closeFailureMessage);

					if (recoveryRestoreQueued)
					{
						TryAttachActiveRenameFailureToPendingEditorRestore();
						syncSuppressionQueuedForDeferredEnd = true;
					}
					else
					{
						QueueScriptRenameTreeRestore(treeState, entry, endSyncSuppression: true);
						syncSuppressionQueuedForDeferredEnd = true;
					}

					return RenameMutationResult.Failed;
				}
			}

			List<Script> remainingOriginalInstances = GetOpenScriptsByInstanceIds(
				scriptEditor,
				originalResources.OriginalScriptInstanceIds
			);
			List<Script> remainingOldPathResources = GetDistinctOpenScriptsByPath(
				scriptEditor,
				oldScriptPath
			);

			if (remainingOriginalInstances.Count > 0 || remainingOldPathResources.Count > 0)
			{
				string closeFailureMessage =
					$"Godot still reported old target resources after the verified close sequence. Remaining original instances: {FormatScriptInstanceIds(remainingOriginalInstances.Select(script => script.GetInstanceId()))}. Remaining old-path resources: {FormatScriptPaths(remainingOldPathResources.Select(script => ScriptPathUtility.Normalize(script.ResourcePath)))}.";
				bool recoveryRestoreQueued = TryRecoverScriptRenameAfterCloseFailure(
					editorInterface,
					scriptEditor,
					oldScriptPath,
					originalResources,
					primaryBinding,
					editorState,
					activeScriptBeforeOperation,
					treeState,
					entry,
					ref closeFailureMessage
				);
				ReportTreeOperationFailure(
					"System Explorer closed the verified target tabs, but Godot still reported the old script state. The file was not renamed.",
					closeFailureMessage
				);
				DebugLogger.LogOperation(
					"Rename Script failed: old group remained after close verification",
					closeFailureMessage
				);

				if (recoveryRestoreQueued)
				{
					TryAttachActiveRenameFailureToPendingEditorRestore();
					syncSuppressionQueuedForDeferredEnd = true;
				}
				else
				{
					QueueScriptRenameTreeRestore(treeState, entry, endSyncSuppression: true);
					syncSuppressionQueuedForDeferredEnd = true;
				}

				return RenameMutationResult.Failed;
			}

			bool originalPathAvailableAfterFailure = true;
			string temporaryCaseRenamePath = "";
			bool hadUidSidecarBeforeRename = FileAccess.FileExists($"{oldScriptPath}.uid");
			bool filesystemRenameSucceeded = isCaseOnlyRename
				? TryRenameScriptCaseOnly(
					oldScriptPath,
					newScriptPath,
					out originalPathAvailableAfterFailure,
					out temporaryCaseRenamePath
				)
				: TryRenameScriptOnce(
					oldScriptPath,
					newScriptPath,
					out originalPathAvailableAfterFailure
				);

			if (!filesystemRenameSucceeded)
			{
				bool recoveryRestoreQueued = false;

				if (originalPathAvailableAfterFailure && FileAccess.FileExists(oldScriptPath))
				{
					RestoreScriptRenameTreeState(treeState, entry, restoreFocus: false);

					if (
						TryRequestScriptRenameEditorRestore(
							editorInterface,
							scriptEditor,
							oldScriptPath,
							editorState,
							originalResources,
							treeState,
							entry,
							ScriptRenameEditorRestoreMode.RestoreOriginalAfterRenameFailure,
							endSyncSuppression: true,
							out string reopenFailureMessage
						)
					)
					{
						recoveryRestoreQueued = true;
						syncSuppressionQueuedForDeferredEnd = true;
					}
					else
					{
						ReportTreeOperationFailure(
							$"The rename failed, and System Explorer could not request restoration of the original Script Editor tab:\n{oldScriptPath}",
							reopenFailureMessage
						);
						DebugLogger.LogOperation(
							"Rename Script recovery warning: original reopen request failed",
							reopenFailureMessage
						);
					}
				}
				else
				{
					DebugLogger.LogOperation(
						"Rename Script recovery required: file not at original path",
						$"old='{oldScriptPath}', temporary='{temporaryCaseRenamePath}', target='{newScriptPath}'"
					);
				}

				if (!recoveryRestoreQueued)
				{
					QueueScriptRenameTreeRestore(treeState, entry, endSyncSuppression: true);
					syncSuppressionQueuedForDeferredEnd = true;
				}

				RequestRenameFilesystemFinalStateRefresh();
				return RenameMutationResult.Failed;
			}

			if (!FileAccess.FileExists(newScriptPath))
			{
				ReportTreeOperationFailureOrWarning(
					$"System Explorer completed the filesystem rename call, but the final script path could not be verified:\n{newScriptPath}\n\nThe System Explorer data was not updated."
				);
				DebugLogger.LogOperation(
					"Rename Script failed: final path missing after filesystem success",
					newScriptPath
				);
				QueueScriptRenameTreeRestore(treeState, entry, endSyncSuppression: true);
				syncSuppressionQueuedForDeferredEnd = true;
				RequestRenameFilesystemFinalStateRefresh();
				return RenameMutationResult.Failed;
			}

			bool operationIncomplete = false;

			SystemsAndFolderBindingsSnapshot metadataSnapshot =
				CaptureSystemsAndFolderBindingsSnapshot();
			string selectedScriptEntryBeforeMetadataUpdate = _selectedScriptEntryFromFilter;
			bool entriesUpdated = UpdateScriptEntries(oldScriptPath, newScriptPath);
			string updatedSelectedEntry = BuildScriptEntry(
				GetFolderPathFromEntry(treeState.Entry),
				newScriptPath,
				GetLinkedScenePathFromEntry(treeState.Entry),
				IsEntryLocked(treeState.Entry)
			);
			string selectedEntryAfterRename = entriesUpdated ? updatedSelectedEntry : entry;

			if (!entriesUpdated)
			{
				ReportTreeOperationFailure(
					"The script was renamed, but no matching System Explorer tree entry could be updated.",
					$"old='{oldScriptPath}', new='{newScriptPath}'",
					TreeOperationOutcomeSeverity.Incomplete
				);
				operationIncomplete = true;
				DebugLogger.LogOperation(
					"Rename Script warning: no System Explorer entries updated after filesystem success",
					$"{oldScriptPath} -> {newScriptPath}"
				);
			}
			else if (!SaveSystems())
			{
				RenameFilesystemRollbackResult rollbackResult = isCaseOnlyRename
					? RollbackScriptRenameCaseOnlyAfterSaveFailure(
						oldScriptPath,
						newScriptPath,
						hadUidSidecarBeforeRename
					)
					: RollbackScriptRenameOnceAfterSaveFailure(
						oldScriptPath,
						newScriptPath,
						hadUidSidecarBeforeRename
					);

				DebugLogger.LogOperation(
					"Rename Script save-failure rollback completed",
					$"state={rollbackResult.State}, old='{oldScriptPath}', target='{newScriptPath}', temporary='{rollbackResult.TemporaryPath}', verified='{rollbackResult.VerifiedResourcePath}', details='{rollbackResult.Details}'"
				);

				if (rollbackResult.State == RenameFilesystemRollbackState.OriginalRestored)
				{
					RestoreSystemsAndFolderBindingsSnapshot(metadataSnapshot);
					_selectedScriptEntryFromFilter = selectedScriptEntryBeforeMetadataUpdate;
					bool restoredMetadataSaved = SaveSystems();

					if (restoredMetadataSaved)
					{
						ReportTreeOperationFailure(
							$"System Explorer could not save the renamed script metadata, so the physical rename was rolled back and the original script path and metadata were restored:\n{oldScriptPath}",
							$"Original='{oldScriptPath}', Target='{newScriptPath}', MetadataRestoreVerified=true",
							TreeOperationOutcomeSeverity.Failed,
							replaceExistingReport: true
						);
					}
					else
					{
						ReportTreeOperationFailure(
							"The physical script rename was rolled back, but the restored systems.json state could not be verified. Restart Godot and inspect systems.json before continuing.",
							$"Original='{oldScriptPath}', Target='{newScriptPath}', MetadataRestoreVerified=false",
							TreeOperationOutcomeSeverity.FinalStateUnclear
						);
						DebugLogger.LogOperation(
							"Rename Script rollback warning: restored systems save failed",
							oldScriptPath
						);
					}

					RequestRenameFilesystemFinalStateRefresh();
					BuildTree(keepCurrentExpansionState: true);
					RestoreScriptRenameTreeState(treeState, entry, restoreFocus: false);

					if (
						TryRequestScriptRenameEditorRestore(
							editorInterface,
							scriptEditor,
							oldScriptPath,
							editorState,
							originalResources,
							treeState,
							entry,
							ScriptRenameEditorRestoreMode.RestoreOriginalAfterRenameFailure,
							endSyncSuppression: true,
							out string rollbackReopenFailureMessage
						)
					)
					{
						syncSuppressionQueuedForDeferredEnd = true;
					}
					else
					{
						ReportTreeOperationFailure(
							$"The script rename was rolled back, but System Explorer could not request restoration of the original Script Editor tab:\n{oldScriptPath}",
							rollbackReopenFailureMessage
						);
						DebugLogger.LogOperation(
							"Rename Script rollback warning: original reopen request failed",
							rollbackReopenFailureMessage
						);
						QueueScriptRenameTreeRestore(
							treeState,
							entry,
							endSyncSuppression: true
						);
						syncSuppressionQueuedForDeferredEnd = true;
					}

					return RenameMutationResult.Failed;
				}

				if (rollbackResult.State == RenameFilesystemRollbackState.TargetRetained)
				{
					bool metadataStateUnclear = IsActiveTreeOperationFinalStateUnclear;
					ReportTreeOperationFailure(
						metadataStateUnclear
							? "The script was renamed and remains at the new path, but the final state of System Explorer's updated metadata could not be verified."
							: "The script was renamed, but System Explorer could not save the updated metadata or restore the original file path.",
						$"new='{newScriptPath}', rollback='{rollbackResult.Details}'",
						metadataStateUnclear
							? TreeOperationOutcomeSeverity.FinalStateUnclear
							: TreeOperationOutcomeSeverity.Incomplete
					);
					operationIncomplete = true;
					DebugLogger.LogOperation(
						"Rename Script warning: save failed and target rename retained",
						rollbackResult.Details
					);
				}
				else
				{
					string verifiedScriptPath = rollbackResult.VerifiedResourcePath;
					string selectedEntryAfterUnclearRollback = selectedEntryAfterRename;
					ScriptRenameEditorRestoreMode unclearRestoreMode =
						ScriptRenameEditorRestoreMode.SuccessfulRename;
					bool canRequestVerifiedEditorRestore = false;

					if (string.IsNullOrWhiteSpace(verifiedScriptPath))
					{
						RestoreSystemsAndFolderBindingsSnapshot(metadataSnapshot);
						_selectedScriptEntryFromFilter = selectedScriptEntryBeforeMetadataUpdate;
						selectedEntryAfterUnclearRollback = entry;
					}
					else if (
						string.Equals(verifiedScriptPath, oldScriptPath, StringComparison.Ordinal)
					)
					{
						RestoreSystemsAndFolderBindingsSnapshot(metadataSnapshot);
						_selectedScriptEntryFromFilter = selectedScriptEntryBeforeMetadataUpdate;
						selectedEntryAfterUnclearRollback = entry;
						unclearRestoreMode =
							ScriptRenameEditorRestoreMode.RestoreOriginalAfterRenameFailure;
						canRequestVerifiedEditorRestore = true;
						SaveSystems();
					}
					else if (
						string.Equals(verifiedScriptPath, newScriptPath, StringComparison.Ordinal)
					)
					{
						canRequestVerifiedEditorRestore = true;
					}
					else if (!string.IsNullOrWhiteSpace(verifiedScriptPath))
					{
						RestoreSystemsAndFolderBindingsSnapshot(metadataSnapshot);
						_selectedScriptEntryFromFilter = selectedScriptEntryBeforeMetadataUpdate;
						bool temporaryEntriesUpdated = UpdateScriptEntries(
							oldScriptPath,
							verifiedScriptPath
						);
						selectedEntryAfterUnclearRollback = temporaryEntriesUpdated
							? BuildScriptEntry(
								GetFolderPathFromEntry(treeState.Entry),
								verifiedScriptPath,
								GetLinkedScenePathFromEntry(treeState.Entry),
								IsEntryLocked(treeState.Entry)
							)
							: entry;

						if (temporaryEntriesUpdated)
							SaveSystems();
					}

					ReportTreeOperationFailureOrWarning(
						$"System Explorer could not save systems.json and the script rollback ended in an unclear physical state. The operation was not reported as successful.\n\nOriginal: {oldScriptPath}\nTarget: {newScriptPath}\nTemporary: {rollbackResult.TemporaryPath}\nVerified script path: {(string.IsNullOrWhiteSpace(verifiedScriptPath) ? "none" : verifiedScriptPath)}\n\nInspect these paths and systems.json before continuing."
					);
					DebugLogger.LogOperation(
						"Rename Script failed: unclear save-failure rollback state",
						rollbackResult.Details
					);

					RequestRenameFilesystemFinalStateRefresh();
					BuildTree(keepCurrentExpansionState: true);
					RestoreScriptRenameTreeState(
						treeState,
						selectedEntryAfterUnclearRollback,
						restoreFocus: false
					);

					if (
						canRequestVerifiedEditorRestore
						&& TryRequestScriptRenameEditorRestore(
							editorInterface,
							scriptEditor,
							verifiedScriptPath,
							editorState,
							originalResources,
							treeState,
							selectedEntryAfterUnclearRollback,
							unclearRestoreMode,
							endSyncSuppression: true,
							out string unclearReopenFailureMessage
						)
					)
					{
						syncSuppressionQueuedForDeferredEnd = true;
					}
					else
					{
						QueueScriptRenameTreeRestore(
							treeState,
							selectedEntryAfterUnclearRollback,
							endSyncSuppression: true
						);
						syncSuppressionQueuedForDeferredEnd = true;
					}

					return RenameMutationResult.Failed;
				}
			}

			RequestRenameFilesystemFinalStateRefresh();
			BuildTree(keepCurrentExpansionState: true);
			RestoreScriptRenameTreeState(treeState, selectedEntryAfterRename, restoreFocus: false);

			if (
				TryRequestScriptRenameEditorRestore(
					editorInterface,
					scriptEditor,
					newScriptPath,
					editorState,
					originalResources,
					treeState,
					selectedEntryAfterRename,
					ScriptRenameEditorRestoreMode.SuccessfulRename,
					endSyncSuppression: true,
					out string editorRestoreRequestFailureMessage
				)
			)
			{
				syncSuppressionQueuedForDeferredEnd = true;
			}
			else
			{
				ReportTreeOperationFailure(
					"The script was renamed successfully, but Godot's Script Editor could not be fully restored.",
					editorRestoreRequestFailureMessage,
					TreeOperationOutcomeSeverity.Incomplete
				);
				operationIncomplete = true;
				DebugLogger.LogOperation(
					"Rename Script warning: filesystem success; editor reopen request failed",
					editorRestoreRequestFailureMessage
				);
				QueueScriptRenameTreeRestore(
					treeState,
					selectedEntryAfterRename,
					endSyncSuppression: true
				);
				syncSuppressionQueuedForDeferredEnd = true;
			}

			if (operationIncomplete)
			{
				DebugLogger.LogOperation(
					"Rename Script incomplete",
					$"{oldScriptPath} -> {newScriptPath}"
				);
				return RenameMutationResult.Failed;
			}

			DebugLogger.LogOperation("Rename Script Mutated", $"{oldScriptPath} -> {newScriptPath}");
			return RenameMutationResult.Success;
		}
		finally
		{
			if (!syncSuppressionQueuedForDeferredEnd)
				EndScriptEditorSyncSuppression();
		}
	}

	private ScriptRenameTreeState CaptureScriptRenameTreeState(string entry)
	{
		if (_tree == null || string.IsNullOrWhiteSpace(entry))
			return null;

		TreeItem selectedItem = _tree.GetSelected();
		string metadata = selectedItem?.GetMetadata(0).AsString() ?? "";

		if (!metadata.StartsWith("script::", StringComparison.Ordinal))
			metadata = $"script::{entry}";

		bool wasFiltering = IsScriptFilterActive();
		string systemName = "";
		string folderPath = GetFolderPathFromEntry(entry);

		if (wasFiltering)
		{
			if (
				TryGetScriptFilterResultForTreeItem(
					selectedItem,
					out ScriptFilterResult result
				)
				&& string.Equals(result.Entry, entry, StringComparison.Ordinal)
			)
			{
				systemName = result.SystemName;
				folderPath = result.FolderPath;
			}
		}
		else
		{
			TreeItem current = selectedItem;

			while (current != null)
			{
				string currentMetadata = current.GetMetadata(0).AsString();

				if (currentMetadata.StartsWith("system::", StringComparison.Ordinal))
				{
					systemName = GetSystemNameFromMetadata(currentMetadata);
					break;
				}

				current = current.GetParent();
			}
		}

		HashSet<string> expansionState = wasFiltering
			? new HashSet<string>(
				_expandedItemsBeforeScriptFilter,
				StringComparer.Ordinal
			)
			: CaptureTreeExpansionStateSnapshot();

		return new ScriptRenameTreeState
		{
			SystemName = systemName,
			FolderPath = folderPath,
			Entry = entry,
			Metadata = metadata,
			WasFiltering = wasFiltering,
			FilterText = _scriptFilterInput?.Text ?? "",
			ExpansionState = expansionState,
			FocusOwnerBeforeDialog = GetViewport()?.GuiGetFocusOwner(),
			TreeHadFocusBeforeDialog = _tree.HasFocus(),
		};
	}

	private bool TryActivateExactScriptEditorForFileOperation(
		EditorInterface editorInterface,
		ScriptEditor scriptEditor,
		Script targetScript,
		string targetPath,
		string operationName,
		out ScriptEditorBase scriptEditorBase,
		out TextEdit textEditor
	)
	{
		scriptEditorBase = null;
		textEditor = null;

		if (editorInterface == null || scriptEditor == null || targetScript == null)
			return false;

		editorInterface.EditScript(targetScript, -1, 0, false);
		Script currentScript = scriptEditor.GetCurrentScript();
		scriptEditorBase = scriptEditor.GetCurrentEditor();
		Control baseEditor = scriptEditorBase?.GetBaseEditor();

		if (
			!IsSameScriptResource(currentScript, targetScript)
			|| baseEditor is not TextEdit currentTextEditor
		)
		{
			ReportTreeOperationFailureOrWarning(
				$"System Explorer could not safely activate and match the exact Script Editor buffer before {operationName}:\n{targetPath}"
			);
			DebugLogger.LogOperation(
				$"File operation failed during exact editor activation ({operationName})",
				targetPath
			);
			return false;
		}

		textEditor = currentTextEditor;
		return true;
	}

	private static bool TrySelectPrimaryScriptRenameResource(
		IReadOnlyList<Script> matchingOpenScripts,
		Script activeScriptBeforeOperation,
		string canonicalOldScriptPath,
		out Script primaryScript
	)
	{
		primaryScript = null;
		List<Script> validScripts = matchingOpenScripts
			?.Where(script => script != null && GodotObject.IsInstanceValid(script))
			.ToList()
			?? new List<Script>();

		if (
			validScripts.Select(script => script.GetInstanceId()).Distinct().Count()
			!= validScripts.Count
		)
		{
			return false;
		}

		if (
			activeScriptBeforeOperation != null
			&& GodotObject.IsInstanceValid(activeScriptBeforeOperation)
		)
		{
			Script activeTargetScript = validScripts.SingleOrDefault(script =>
				IsSameScriptResource(script, activeScriptBeforeOperation)
			);

			if (activeTargetScript != null)
			{
				primaryScript = activeTargetScript;
				return true;
			}
		}

		List<Script> exactCasingMatches = validScripts
			.Where(script =>
				string.Equals(
					ScriptPathUtility.Normalize(script.ResourcePath),
					canonicalOldScriptPath,
					StringComparison.Ordinal
				)
			)
			.ToList();

		if (exactCasingMatches.Count == 1)
		{
			primaryScript = exactCasingMatches.Single();
			return true;
		}

		if (validScripts.Count == 1)
		{
			primaryScript = validScripts.Single();
			return true;
		}

		return false;
	}

	private bool TryBindScriptRenameResourcesToVerifiedGroup(
		EditorInterface editorInterface,
		ScriptEditor scriptEditor,
		IReadOnlyList<Script> matchingOpenScripts,
		OpenScriptEditorBufferGroup openEditorGroup,
		Script activeScriptBeforeOperation,
		string canonicalOldScriptPath,
		out List<ScriptRenameOpenResourceBinding> resourceBindings,
		out string failureDetail
	)
	{
		resourceBindings = new List<ScriptRenameOpenResourceBinding>();
		failureDetail = "";

		if (
			editorInterface == null
			|| scriptEditor == null
			|| openEditorGroup == null
			|| matchingOpenScripts == null
		)
		{
			failureDetail = "The Script Editor, resource inventory, or verified buffer group was unavailable.";
			return false;
		}

		List<Script> distinctScripts = matchingOpenScripts
			.Where(script => script != null && GodotObject.IsInstanceValid(script))
			.OrderBy(
				script => ScriptPathUtility.Normalize(script.ResourcePath),
				StringComparer.Ordinal
			)
			.ThenBy(script => script.GetInstanceId())
			.ToList();

		if (
			distinctScripts.Count == 0
			|| distinctScripts.Count != matchingOpenScripts.Count
			|| distinctScripts.Select(script => script.GetInstanceId()).Distinct().Count()
				!= distinctScripts.Count
			|| openEditorGroup.Buffers.Count != distinctScripts.Count
		)
		{
			failureDetail =
				$"Resource/group cardinality changed before binding (resources={distinctScripts.Count}, reported={matchingOpenScripts.Count}, groupMembers={openEditorGroup.Buffers.Count}).";
			return false;
		}

		if (
			!string.Equals(
				ScriptPathUtility.Normalize(openEditorGroup.Path),
				canonicalOldScriptPath,
				StringComparison.Ordinal
			)
		)
		{
			failureDetail =
				$"The verified group path did not retain the canonical System Explorer casing. Expected '{canonicalOldScriptPath}', got '{openEditorGroup.Path}'.";
			return false;
		}

		HashSet<ulong> groupTextEditorInstanceIds = new();

		foreach (OpenScriptEditorBuffer groupMember in openEditorGroup.Buffers)
		{
			TextEdit groupTextEditor = groupMember.TextEditor;

			if (
				groupTextEditor == null
				|| !GodotObject.IsInstanceValid(groupTextEditor)
				|| !string.Equals(
					groupMember.Path,
					canonicalOldScriptPath,
					StringComparison.Ordinal
				)
			)
			{
				failureDetail =
					"The verified buffer group no longer contained a valid canonical member.";
				return false;
			}

			ulong groupTextEditorInstanceId = groupTextEditor.GetInstanceId();

			if (
				groupTextEditorInstanceId == 0
				|| !groupTextEditorInstanceIds.Add(groupTextEditorInstanceId)
			)
			{
				failureDetail =
					"The verified buffer group no longer contained a unique valid TextEdit for every member.";
				return false;
			}
		}

		HashSet<ulong> boundScriptInstanceIds = new();
		HashSet<ulong> boundTextEditorInstanceIds = new();

		foreach (Script script in distinctScripts)
		{
			string reportedScriptPath = ScriptPathUtility.Normalize(script.ResourcePath);

			if (
				!string.Equals(
					reportedScriptPath,
					canonicalOldScriptPath,
					StringComparison.OrdinalIgnoreCase
				)
			)
			{
				failureDetail =
					$"Script instance {script.GetInstanceId()} no longer reported a path matching the canonical target: '{reportedScriptPath}'.";
				return false;
			}

			if (
				!TryActivateExactScriptEditorForFileOperation(
					editorInterface,
					scriptEditor,
					script,
					canonicalOldScriptPath,
					"rename buffer binding",
					out ScriptEditorBase scriptEditorBase,
					out TextEdit textEditor
				)
			)
			{
				failureDetail =
					$"Godot could not activate Script instance {script.GetInstanceId()} at '{ScriptPathUtility.Normalize(script.ResourcePath)}' through the exact current-script/current-editor relation.";
				return false;
			}

			if (
				scriptEditorBase == null
				|| textEditor == null
				|| !GodotObject.IsInstanceValid(scriptEditorBase)
				|| !GodotObject.IsInstanceValid(textEditor)
			)
			{
				failureDetail =
					$"Script instance {script.GetInstanceId()} exposed an invalid ScriptEditorBase/TextEdit pair during binding.";
				return false;
			}

			ulong scriptInstanceId = script.GetInstanceId();
			ulong textEditorInstanceId = textEditor.GetInstanceId();

			if (!groupTextEditorInstanceIds.Contains(textEditorInstanceId))
			{
				failureDetail =
					$"Script instance {scriptInstanceId} activated TextEdit instance {textEditorInstanceId}, which was not a member of the verified group.";
				return false;
			}

			if (!boundScriptInstanceIds.Add(scriptInstanceId))
			{
				failureDetail = $"Script instance {scriptInstanceId} was encountered more than once during binding.";
				return false;
			}

			if (!boundTextEditorInstanceIds.Add(textEditorInstanceId))
			{
				failureDetail =
					$"More than one Script resource activated the same TextEdit instance {textEditorInstanceId}.";
				return false;
			}

			resourceBindings.Add(
				new ScriptRenameOpenResourceBinding
				{
					Script = script,
					ScriptEditorBase = scriptEditorBase,
					TextEditor = textEditor,
					ScriptInstanceId = scriptInstanceId,
					ScriptEditorBaseInstanceId = scriptEditorBase.GetInstanceId(),
					TextEditorInstanceId = textEditorInstanceId,
				}
			);
		}

		if (
			resourceBindings.Count != distinctScripts.Count
			|| boundScriptInstanceIds.Count != distinctScripts.Count
			|| boundTextEditorInstanceIds.Count != distinctScripts.Count
			|| boundTextEditorInstanceIds.Count != openEditorGroup.Buffers.Count
			|| !boundTextEditorInstanceIds.SetEquals(groupTextEditorInstanceIds)
		)
		{
			failureDetail =
				$"The completed binding did not account for the exact verified group (bindings={resourceBindings.Count}, scripts={boundScriptInstanceIds.Count}, TextEdits={boundTextEditorInstanceIds.Count}, groupMembers={openEditorGroup.Buffers.Count}).";
			resourceBindings.Clear();
			return false;
		}

		if (
			activeScriptBeforeOperation != null
			&& GodotObject.IsInstanceValid(activeScriptBeforeOperation)
		)
		{
			ScriptRenameOpenResourceBinding activeTargetBinding =
				resourceBindings.SingleOrDefault(binding =>
					binding.ScriptInstanceId == activeScriptBeforeOperation.GetInstanceId()
				);

			if (activeTargetBinding != null)
			{
				TextEdit originalCurrentTextEditor = openEditorGroup.HasCurrentEditorBuffer
					? openEditorGroup.CurrentEditorBuffer.TextEditor
					: null;

				if (
					originalCurrentTextEditor == null
					|| !GodotObject.IsInstanceValid(originalCurrentTextEditor)
					|| originalCurrentTextEditor.GetInstanceId()
						!= activeTargetBinding.TextEditorInstanceId
				)
				{
					failureDetail =
						"The Script resource that was active before rename did not bind back to the TextEdit identified by the verified current-script/current-editor relation.";
					resourceBindings.Clear();
					return false;
				}
			}
		}

		return true;
	}

	private bool TryReactivateVerifiedScriptRenameBinding(
		EditorInterface editorInterface,
		ScriptEditor scriptEditor,
		ScriptRenameOpenResourceBinding binding,
		string canonicalOldScriptPath,
		out string failureDetail
	)
	{
		failureDetail = "";

		if (
			binding == null
			|| binding.Script == null
			|| binding.ScriptEditorBase == null
			|| binding.TextEditor == null
			|| !GodotObject.IsInstanceValid(binding.Script)
			|| !GodotObject.IsInstanceValid(binding.ScriptEditorBase)
			|| !GodotObject.IsInstanceValid(binding.TextEditor)
		)
		{
			failureDetail = "The previously verified Script/TextEdit binding was no longer valid.";
			return false;
		}

		if (
			!TryActivateExactScriptEditorForFileOperation(
				editorInterface,
				scriptEditor,
				binding.Script,
				canonicalOldScriptPath,
				"rename tab close verification",
				out ScriptEditorBase activeScriptEditorBase,
				out TextEdit activeTextEditor
			)
		)
		{
			failureDetail =
				$"Godot could not reactivate Script instance {binding.ScriptInstanceId} through the exact current-script/current-editor relation.";
			return false;
		}

		if (
			activeScriptEditorBase == null
			|| activeTextEditor == null
			|| !GodotObject.IsInstanceValid(activeScriptEditorBase)
			|| !GodotObject.IsInstanceValid(activeTextEditor)
			|| activeScriptEditorBase.GetInstanceId() != binding.ScriptEditorBaseInstanceId
			|| activeTextEditor.GetInstanceId() != binding.TextEditorInstanceId
		)
		{
			failureDetail =
				$"Script instance {binding.ScriptInstanceId} no longer exposed its verified ScriptEditorBase/TextEdit pair immediately before close.";
			return false;
		}

		return true;
	}

	private void TryRestoreScriptRenamePreCloseEditorContext(
		EditorInterface editorInterface,
		ScriptEditor scriptEditor,
		Script activeScriptBeforeOperation,
		Script primaryScript,
		ScriptRenameOpenResourceBinding primaryBinding,
		ScriptRenameEditorState? editorState,
		bool renamedScriptWasActive
	)
	{
		if (editorInterface == null || scriptEditor == null)
			return;

		if (renamedScriptWasActive)
		{
			if (
				primaryBinding != null
				&& TryReactivateVerifiedScriptRenameBinding(
					editorInterface,
					scriptEditor,
					primaryBinding,
					ScriptPathUtility.Normalize(primaryBinding.Script?.ResourcePath),
					out _
				)
			)
			{
				if (editorState.HasValue)
					RestoreScriptRenameEditorState(primaryBinding.TextEditor, editorState.Value);

				return;
			}

			if (primaryScript != null && GodotObject.IsInstanceValid(primaryScript))
			{
				editorInterface.EditScript(primaryScript, -1, 0, false);
			}

			return;
		}

		TryRestorePreviouslyActiveScript(
			editorInterface,
			scriptEditor,
			activeScriptBeforeOperation,
			primaryScript
		);
	}

	private bool TryRecoverScriptRenameAfterCloseFailure(
		EditorInterface editorInterface,
		ScriptEditor scriptEditor,
		string oldScriptPath,
		ScriptRenameOriginalResourceSet originalResources,
		ScriptRenameOpenResourceBinding primaryBinding,
		ScriptRenameEditorState editorState,
		Script activeScriptBeforeOperation,
		ScriptRenameTreeState treeState,
		string entry,
		ref string closeFailureMessage
	)
	{
		List<Script> remainingOriginalInstances = GetOpenScriptsByInstanceIds(
			scriptEditor,
			originalResources.OriginalScriptInstanceIds
		);
		List<Script> remainingOldPathResources = GetDistinctOpenScriptsByPath(
			scriptEditor,
			oldScriptPath
		);
		bool primaryStillOpen = remainingOriginalInstances.Any(script =>
			script.GetInstanceId() == primaryBinding.ScriptInstanceId
		);

		if (primaryStillOpen)
		{
			if (
				TryReactivateVerifiedScriptRenameBinding(
					editorInterface,
					scriptEditor,
					primaryBinding,
					oldScriptPath,
					out string primaryRestoreFailure
				)
			)
			{
				RestoreScriptRenameEditorState(primaryBinding.TextEditor, editorState);
			}
			else
			{
				closeFailureMessage +=
					$"\n\nThe primary old Script resource was still open, but its verified editor state could not be restored safely: {primaryRestoreFailure}";
			}

			if (!editorState.RenamedScriptWasActive)
			{
				TryRestorePreviouslyActiveScript(
					editorInterface,
					scriptEditor,
					activeScriptBeforeOperation,
					primaryBinding.Script
				);
			}

			return false;
		}

		if (remainingOriginalInstances.Count == 0 && remainingOldPathResources.Count == 0)
		{
			RestoreScriptRenameTreeState(treeState, entry, restoreFocus: false);

			if (
				TryRequestScriptRenameEditorRestore(
					editorInterface,
					scriptEditor,
					oldScriptPath,
					editorState,
					originalResources,
					treeState,
					entry,
					ScriptRenameEditorRestoreMode.RestoreOriginalAfterCloseFailure,
					endSyncSuppression: true,
					out string recoveryFailureMessage
				)
			)
			{
				return true;
			}

			closeFailureMessage +=
				$"\n\nNo old target resource remained open, and requesting one fresh canonical tab from the unchanged original path also failed: {recoveryFailureMessage}";
			return false;
		}

		closeFailureMessage +=
			$"\n\nThe primary old resource was gone, but an unsafe intermediate state still contained other old instances or path aliases. System Explorer did not choose one as a replacement. Remaining original instance IDs: {FormatScriptInstanceIds(remainingOriginalInstances.Select(script => script.GetInstanceId()))}. Remaining old paths: {FormatScriptPaths(remainingOldPathResources.Select(script => ScriptPathUtility.Normalize(script.ResourcePath)))}.";

		if (!editorState.RenamedScriptWasActive)
		{
			TryRestorePreviouslyActiveScript(
				editorInterface,
				scriptEditor,
				activeScriptBeforeOperation,
				renamedScript: null
			);
		}

		return false;
	}

	private static List<Script> GetOpenScriptsByInstanceIds(
		ScriptEditor scriptEditor,
		IEnumerable<ulong> instanceIds
	)
	{
		List<Script> result = new();

		if (scriptEditor == null)
			return result;

		HashSet<ulong> targetInstanceIds = (instanceIds ?? Array.Empty<ulong>())
			.Where(instanceId => instanceId != 0)
			.ToHashSet();
		HashSet<ulong> seenInstanceIds = new();

		foreach (Script openScript in scriptEditor.GetOpenScripts())
		{
			if (openScript == null || !GodotObject.IsInstanceValid(openScript))
				continue;

			ulong instanceId = openScript.GetInstanceId();

			if (targetInstanceIds.Contains(instanceId) && seenInstanceIds.Add(instanceId))
				result.Add(openScript);
		}

		Script currentScript = scriptEditor.GetCurrentScript();

		if (currentScript != null && GodotObject.IsInstanceValid(currentScript))
		{
			ulong currentInstanceId = currentScript.GetInstanceId();

			if (
				targetInstanceIds.Contains(currentInstanceId)
				&& seenInstanceIds.Add(currentInstanceId)
			)
			{
				result.Add(currentScript);
			}
		}

		return result;
	}

	private static string FormatScriptInstanceIds(IEnumerable<ulong> instanceIds)
	{
		List<ulong> values = (instanceIds ?? Array.Empty<ulong>())
			.Where(instanceId => instanceId != 0)
			.Distinct()
			.ToList();
		return values.Count == 0 ? "<none>" : string.Join(", ", values);
	}

	private static string FormatScriptPaths(IEnumerable<string> paths)
	{
		List<string> values = (paths ?? Array.Empty<string>())
			.Select(ScriptPathUtility.Normalize)
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Distinct(StringComparer.Ordinal)
			.ToList();
		return values.Count == 0 ? "<none>" : string.Join(", ", values);
	}

	private static ScriptRenameEditorState CaptureScriptRenameEditorState(
		string oldScriptPath,
		Script script,
		ScriptEditorBase scriptEditorBase,
		TextEdit textEditor,
		bool renamedScriptWasActive,
		Script activeScriptBeforeOperation
	)
	{
		bool hadSelection = textEditor.HasSelection(0);

		return new ScriptRenameEditorState(
			ScriptPathUtility.Normalize(oldScriptPath),
			script.GetInstanceId(),
			scriptEditorBase.GetInstanceId(),
			textEditor.GetInstanceId(),
			ScriptEditorBufferStateService.IsUnsaved(textEditor),
			textEditor.Text ?? "",
			Math.Max(0, textEditor.GetFirstVisibleLine()),
			Math.Max(0, textEditor.ScrollHorizontal),
			Math.Max(0.0, textEditor.ScrollVertical),
			Math.Max(0, textEditor.GetCaretLine()),
			Math.Max(0, textEditor.GetCaretColumn()),
			hadSelection,
			hadSelection ? Math.Max(0, textEditor.GetSelectionFromLine(0)) : 0,
			hadSelection ? Math.Max(0, textEditor.GetSelectionFromColumn(0)) : 0,
			hadSelection ? Math.Max(0, textEditor.GetSelectionToLine(0)) : 0,
			hadSelection ? Math.Max(0, textEditor.GetSelectionToColumn(0)) : 0,
			hadSelection ? Math.Max(0, textEditor.GetSelectionOriginLine(0)) : 0,
			hadSelection ? Math.Max(0, textEditor.GetSelectionOriginColumn(0)) : 0,
			renamedScriptWasActive,
			activeScriptBeforeOperation
		);
	}

	private bool TryRequestScriptRenameEditorRestore(
		EditorInterface editorInterface,
		ScriptEditor scriptEditor,
		string scriptPath,
		ScriptRenameEditorState editorState,
		ScriptRenameOriginalResourceSet originalResources,
		ScriptRenameTreeState treeState,
		string selectedEntry,
		ScriptRenameEditorRestoreMode mode,
		bool endSyncSuppression,
		out string failureMessage
	)
	{
		failureMessage = "";

		if (_pendingScriptRenameEditorRestore != null)
		{
			failureMessage =
				"Another script-rename editor restore is already pending. Only one restore operation can run at a time.";
			return false;
		}

		if (editorInterface == null || scriptEditor == null)
		{
			failureMessage =
				"Godot's EditorInterface or ScriptEditor was unavailable when the reopen was requested.";
			return false;
		}

		if (treeState == null || !treeState.IsValid)
		{
			failureMessage =
				"The captured System Explorer tree state was invalid when the reopen was requested.";
			return false;
		}

		if (
			originalResources == null
			|| !originalResources.IsValid
			|| !originalResources.ContainsInstanceId(editorState.ScriptInstanceId)
		)
		{
			failureMessage =
				"The captured original Script-resource identity set was invalid when the reopen was requested.";
			return false;
		}

		string normalizedPath = ScriptPathUtility.Normalize(scriptPath);

		if (!FileAccess.FileExists(normalizedPath))
		{
			failureMessage =
				$"The script file does not exist at the path to reopen: {normalizedPath}";
			return false;
		}

		Script loadedScript;

		try
		{
			loadedScript = ResourceLoader.Load<Script>(
				normalizedPath,
				"",
				ResourceLoader.CacheMode.Ignore
			);
		}
		catch (Exception exception)
		{
			failureMessage = $"Loading '{normalizedPath}' threw: {exception.Message}";
			return false;
		}

		if (loadedScript == null)
		{
			failureMessage = $"Godot did not load a Script resource from '{normalizedPath}'.";
			return false;
		}

		ulong loadedScriptInstanceId = loadedScript.GetInstanceId();

		if (originalResources.ContainsInstanceId(loadedScriptInstanceId))
		{
			failureMessage =
				$"Godot reused one of the {originalResources.OriginalResourceCount} old Script resource instances instead of loading a fresh resource from '{normalizedPath}'.";
			return false;
		}

		string loadedScriptPath = ScriptPathUtility.Normalize(loadedScript.ResourcePath);

		if (!string.Equals(loadedScriptPath, normalizedPath, StringComparison.Ordinal))
		{
			failureMessage =
				$"Godot loaded a Script resource with an unexpected path. Expected '{normalizedPath}', got '{loadedScriptPath}'.";
			return false;
		}

		PendingScriptRenameEditorRestore pendingRestore = new()
		{
			TargetScriptPath = normalizedPath,
			CanonicalOldScriptPath = originalResources.CanonicalOldScriptPath,
			FinalTargetScriptPath = originalResources.FinalTargetScriptPath,
			PrimaryOldScriptInstanceId = editorState.ScriptInstanceId,
			OriginalScriptInstanceIds = new List<ulong>(
				originalResources.OriginalScriptInstanceIds
			).AsReadOnly(),
			OriginalReportedPaths = new List<string>(
				originalResources.OriginalReportedPaths
			).AsReadOnly(),
			EditorState = editorState,
			TreeState = treeState,
			SelectedEntry = selectedEntry ?? "",
			LoadedScript = loadedScript,
			LoadedScriptInstanceId = loadedScriptInstanceId,
			DeferredAttemptCount = 0,
			EndSyncSuppression = endSyncSuppression,
			Mode = mode,
		};

		_pendingScriptRenameEditorRestore = pendingRestore;

		try
		{
			// EditScript is intentionally treated as a request. Godot may not expose
			// the resulting current Script/ScriptEditorBase/TextEdit until a later frame.
			editorInterface.EditScript(loadedScript, -1, 0, false);
		}
		catch (Exception exception)
		{
			_pendingScriptRenameEditorRestore = null;
			failureMessage =
				$"Requesting Godot to open '{normalizedPath}' threw: {exception.Message}";
			return false;
		}

		DebugLogger.LogOperation(
			"Rename Script editor reopen requested",
			$"mode={mode}, target='{normalizedPath}', loadedScriptId={loadedScriptInstanceId}, originalResources={originalResources.OriginalResourceCount}"
		);

		try
		{
			CallDeferred(nameof(VerifyPendingScriptRenameEditorRestoreDeferred));
		}
		catch (Exception exception)
		{
			_pendingScriptRenameEditorRestore = null;
			failureMessage =
				$"Scheduling deferred reopen verification for '{normalizedPath}' threw: {exception.Message}";
			return false;
		}

		TryAttachActiveRenameFailureToPendingEditorRestore();

		return true;
	}

	private void TryAttachActiveRenameFailureToPendingEditorRestore()
	{
		PendingScriptRenameEditorRestore pendingRestore =
			_pendingScriptRenameEditorRestore;

		if (
			pendingRestore == null
			|| pendingRestore.DeferredFailurePresentation != null
		)
		{
			return;
		}

		if (
			TryDeferActiveTreeOperationDialogPresentation(
				out DeferredTreeOperationDialogPresentation presentation
			)
		)
		{
			pendingRestore.DeferredFailurePresentation = presentation;
		}
	}

	private void VerifyPendingScriptRenameEditorRestoreDeferred()
	{
		PendingScriptRenameEditorRestore pendingRestore = _pendingScriptRenameEditorRestore;

		if (pendingRestore == null || pendingRestore.IsCompleting)
			return;

		try
		{
			VerifyPendingScriptRenameEditorRestore(pendingRestore);
		}
		catch (Exception exception)
		{
			CompletePendingScriptRenameEditorRestore(
				succeeded: false,
				BuildScriptRenameEditorRestoreFailure(
					pendingRestore,
					$"Deferred reopen verification threw unexpectedly: {exception.Message}",
					currentScript: null,
					currentEditor: null,
					baseEditor: null,
					finalPathMatchCount: 0
				)
			);
		}
	}

	private void VerifyPendingScriptRenameEditorRestore(
		PendingScriptRenameEditorRestore pendingRestore
	)
	{
		if (!ReferenceEquals(_pendingScriptRenameEditorRestore, pendingRestore))
			return;

		if (!IsInsideTree())
		{
			CancelPendingScriptRenameEditorRestore();
			return;
		}

		pendingRestore.DeferredAttemptCount++;

		EditorInterface editorInterface = EditorInterface.Singleton;
		ScriptEditor scriptEditor = editorInterface?.GetScriptEditor();
		Script currentScript = scriptEditor?.GetCurrentScript();
		ScriptEditorBase currentEditor = scriptEditor?.GetCurrentEditor();

		if (currentScript != null && !GodotObject.IsInstanceValid(currentScript))
			currentScript = null;

		if (currentEditor != null && !GodotObject.IsInstanceValid(currentEditor))
			currentEditor = null;

		Control baseEditor = currentEditor?.GetBaseEditor();
		List<Script> finalPathMatches =
			scriptEditor == null
				? new List<Script>()
				: GetDistinctListedOpenScriptsByPath(
					scriptEditor,
					pendingRestore.TargetScriptPath
				);

		if (!pendingRestore.IsValid)
		{
			CompletePendingScriptRenameEditorRestore(
				succeeded: false,
				BuildScriptRenameEditorRestoreFailure(
					pendingRestore,
					"The pending script-rename editor restore state was inconsistent.",
					currentScript,
					currentEditor,
					baseEditor,
					finalPathMatches.Count
				)
			);
			return;
		}

		if (!FileAccess.FileExists(pendingRestore.TargetScriptPath))
		{
			CompletePendingScriptRenameEditorRestore(
				succeeded: false,
				BuildScriptRenameEditorRestoreFailure(
					pendingRestore,
					$"The target script file no longer exists: {pendingRestore.TargetScriptPath}",
					currentScript,
					currentEditor,
					baseEditor,
					finalPathMatches.Count
				)
			);
			return;
		}

		if (editorInterface == null || scriptEditor == null)
		{
			QueueNextPendingScriptRenameEditorRestoreAttempt(
				pendingRestore,
				editorInterface,
				finalOpenScript: null,
				"Godot's ScriptEditor is not available yet.",
				currentScript,
				currentEditor,
				baseEditor,
				finalPathMatches.Count
			);
			return;
		}

		List<Script> reappearedOldInstances = GetOpenScriptsByInstanceIds(
			scriptEditor,
			pendingRestore.OriginalScriptInstanceIds
		);

		if (reappearedOldInstances.Count > 0)
		{
			Script requestedFreshScript = pendingRestore.LoadedScript;

			if (
				requestedFreshScript != null
				&& !GodotObject.IsInstanceValid(requestedFreshScript)
			)
			{
				requestedFreshScript = null;
			}

			QueueNextPendingScriptRenameEditorRestoreAttempt(
				pendingRestore,
				editorInterface,
				requestedFreshScript,
				$"Godot still exposed one or more old closed Script instances during the editor handoff: {FormatScriptInstanceIds(reappearedOldInstances.Select(script => script.GetInstanceId()))}.",
				currentScript,
				currentEditor,
				baseEditor,
				finalPathMatches.Count
			);
			return;
		}

		if (
			pendingRestore.Mode == ScriptRenameEditorRestoreMode.SuccessfulRename
			&& TryGetRejectedOldOpenScriptPath(
				scriptEditor,
				pendingRestore,
				out string rejectedOldOpenPath
			)
		)
		{
			CompletePendingScriptRenameEditorRestore(
				succeeded: false,
				BuildScriptRenameEditorRestoreFailure(
					pendingRestore,
					$"An old script-path casing still appeared among open scripts: {rejectedOldOpenPath}",
					currentScript,
					currentEditor,
					baseEditor,
					finalPathMatches.Count
				)
			);
			return;
		}

		if (finalPathMatches.Count > 1)
		{
			CompletePendingScriptRenameEditorRestore(
				succeeded: false,
				BuildScriptRenameEditorRestoreFailure(
					pendingRestore,
					$"Godot reported {finalPathMatches.Count} open Script resources for the target path; exactly one was required.",
					currentScript,
					currentEditor,
					baseEditor,
					finalPathMatches.Count
				)
			);
			return;
		}

		if (finalPathMatches.Count == 0)
		{
			Script requestedFreshScript = pendingRestore.LoadedScript;

			if (
				requestedFreshScript != null
				&& !GodotObject.IsInstanceValid(requestedFreshScript)
			)
			{
				requestedFreshScript = null;
			}

			QueueNextPendingScriptRenameEditorRestoreAttempt(
				pendingRestore,
				editorInterface,
				requestedFreshScript,
				"Godot has not exposed the requested fresh target Script among GetOpenScripts() yet.",
				currentScript,
				currentEditor,
				baseEditor,
				finalPathMatches.Count
			);
			return;
		}

		Script finalOpenScript = finalPathMatches[0];
		string finalOpenPath = ScriptPathUtility.Normalize(finalOpenScript.ResourcePath);

		if (
			!string.Equals(finalOpenPath, pendingRestore.TargetScriptPath, StringComparison.Ordinal)
		)
		{
			CompletePendingScriptRenameEditorRestore(
				succeeded: false,
				BuildScriptRenameEditorRestoreFailure(
					pendingRestore,
					$"The unique target Script resource used unexpected path casing. Expected '{pendingRestore.TargetScriptPath}', got '{finalOpenPath}'.",
					currentScript,
					currentEditor,
					baseEditor,
					finalPathMatches.Count
				)
			);
			return;
		}

		if (pendingRestore.OriginalScriptInstanceIds.Contains(finalOpenScript.GetInstanceId()))
		{
			CompletePendingScriptRenameEditorRestore(
				succeeded: false,
				BuildScriptRenameEditorRestoreFailure(
					pendingRestore,
					"The unique target Script resource was one of the old closed Script instances.",
					currentScript,
					currentEditor,
					baseEditor,
					finalPathMatches.Count
				)
			);
			return;
		}

		string currentPath = ScriptPathUtility.Normalize(currentScript?.ResourcePath);
		bool currentIsUniqueFinalScript =
			currentScript != null
			&& currentScript.GetInstanceId() == finalOpenScript.GetInstanceId()
			&& string.Equals(
				currentPath,
				pendingRestore.TargetScriptPath,
				StringComparison.Ordinal
			);

		if (!currentIsUniqueFinalScript)
		{
			QueueNextPendingScriptRenameEditorRestoreAttempt(
				pendingRestore,
				editorInterface,
				finalOpenScript,
				"The unique target Script is open, but Godot has not made it the exact current Script yet.",
				currentScript,
				currentEditor,
				baseEditor,
				finalPathMatches.Count
			);
			return;
		}

		if (currentEditor == null || !GodotObject.IsInstanceValid(currentEditor))
		{
			QueueNextPendingScriptRenameEditorRestoreAttempt(
				pendingRestore,
				editorInterface,
				finalOpenScript,
				"Godot has made the target Script current, but GetCurrentEditor() is still unavailable.",
				currentScript,
				currentEditor,
				baseEditor,
				finalPathMatches.Count
			);
			return;
		}

		if (
			baseEditor is not TextEdit currentTextEditor
			|| !GodotObject.IsInstanceValid(currentTextEditor)
		)
		{
			QueueNextPendingScriptRenameEditorRestoreAttempt(
				pendingRestore,
				editorInterface,
				finalOpenScript,
				"Godot has not exposed a valid TextEdit for the current target Script yet.",
				currentScript,
				currentEditor,
				baseEditor,
				finalPathMatches.Count
			);
			return;
		}

		if (
			!ScriptTextFileService.TextsMatchForDiskVerification(
				currentTextEditor.Text ?? "",
				pendingRestore.EditorState.BufferText
			)
		)
		{
			CompletePendingScriptRenameEditorRestore(
				succeeded: false,
				BuildScriptRenameEditorRestoreFailure(
					pendingRestore,
					$"The reopened editor buffer for '{pendingRestore.TargetScriptPath}' did not match the text captured before close.",
					currentScript,
					currentEditor,
					baseEditor,
					finalPathMatches.Count
				)
			);
			return;
		}

		RestoreScriptRenameEditorState(currentTextEditor, pendingRestore.EditorState);

		if (!pendingRestore.EditorState.RenamedScriptWasActive)
		{
			TryRestorePreviouslyActiveScript(
				editorInterface,
				scriptEditor,
				pendingRestore.EditorState.ActiveScriptBeforeOperation,
				finalOpenScript
			);
		}

		CompletePendingScriptRenameEditorRestore(succeeded: true, failureMessage: "");
	}

	private void QueueNextPendingScriptRenameEditorRestoreAttempt(
		PendingScriptRenameEditorRestore pendingRestore,
		EditorInterface editorInterface,
		Script finalOpenScript,
		string transientReason,
		Script currentScript,
		ScriptEditorBase currentEditor,
		Control baseEditor,
		int finalPathMatchCount
	)
	{
		if (
			pendingRestore == null
			|| !ReferenceEquals(_pendingScriptRenameEditorRestore, pendingRestore)
			|| pendingRestore.IsCompleting
		)
		{
			return;
		}

		if (pendingRestore.DeferredAttemptCount >= ScriptRenameEditorRestoreMaxDeferredAttempts)
		{
			CompletePendingScriptRenameEditorRestore(
				succeeded: false,
				BuildScriptRenameEditorRestoreFailure(
					pendingRestore,
					$"{transientReason} The retry limit was reached.",
					currentScript,
					currentEditor,
					baseEditor,
					finalPathMatchCount
				)
			);
			return;
		}

		if (editorInterface != null && finalOpenScript != null)
		{
			try
			{
				// Re-request only the unique Script resource Godot itself exposed.
				editorInterface.EditScript(finalOpenScript, -1, 0, false);
			}
			catch (Exception exception)
			{
				CompletePendingScriptRenameEditorRestore(
					succeeded: false,
					BuildScriptRenameEditorRestoreFailure(
						pendingRestore,
						$"Re-activating the unique target Script threw: {exception.Message}",
						currentScript,
						currentEditor,
						baseEditor,
						finalPathMatchCount
					)
				);
				return;
			}
		}

		DebugLogger.LogOperation(
			"Rename Script editor restore retry queued",
			$"attempt={pendingRestore.DeferredAttemptCount}/{ScriptRenameEditorRestoreMaxDeferredAttempts}, reason='{transientReason}'"
		);

		try
		{
			CallDeferred(nameof(VerifyPendingScriptRenameEditorRestoreDeferred));
		}
		catch (Exception exception)
		{
			CompletePendingScriptRenameEditorRestore(
				succeeded: false,
				BuildScriptRenameEditorRestoreFailure(
					pendingRestore,
					$"Scheduling the next deferred verification attempt threw: {exception.Message}",
					currentScript,
					currentEditor,
					baseEditor,
					finalPathMatchCount
				)
			);
		}
	}

	private void CompletePendingScriptRenameEditorRestore(bool succeeded, string failureMessage)
	{
		PendingScriptRenameEditorRestore pendingRestore = _pendingScriptRenameEditorRestore;

		if (pendingRestore == null || pendingRestore.IsCompleting)
			return;

		pendingRestore.IsCompleting = true;

		try
		{
			DeferredTreeOperationDialogPresentation deferredFailurePresentation =
				pendingRestore.DeferredFailurePresentation;
			string technicalFailureDetails =
				$"Mode='{pendingRestore.Mode}', Attempts={pendingRestore.DeferredAttemptCount}, {failureMessage}";

			if (!succeeded)
			{
				if (deferredFailurePresentation != null)
				{
					string restoreUserMessage = pendingRestore.Mode
						== ScriptRenameEditorRestoreMode.SuccessfulRename
						? "Godot's Script Editor could not fully restore the renamed script tab. The filesystem and System Explorer metadata remain in the state described above."
						: "Godot's Script Editor could not fully restore the original script tab. The filesystem and System Explorer metadata remain in the state described above.";
					string combinedUserMessage =
						$"{deferredFailurePresentation.UserMessage}\n\n{restoreUserMessage}";

					QueueStandaloneTreeOperationDialog(
						deferredFailurePresentation.Title,
						combinedUserMessage,
						technicalFailureDetails,
						deferredFailurePresentation.PersistentDeduplicationKey
					);
				}
				else
				{
					string dialogTitle;
					string userMessage;

					if (pendingRestore.Mode == ScriptRenameEditorRestoreMode.SuccessfulRename)
					{
						dialogTitle = "Rename Incomplete";
						userMessage =
							$"The script was renamed successfully, but Godot's Script Editor could not be fully restored.\n\nFinal path:\n{pendingRestore.TargetScriptPath}\n\nThe filesystem and System Explorer metadata remain on the new path.";
					}
					else if (
						pendingRestore.Mode
						== ScriptRenameEditorRestoreMode.RestoreOriginalAfterCloseFailure
					)
					{
						dialogTitle = "Rename Incomplete";
						userMessage =
							$"The rename was cancelled before the filesystem changed, but Godot's Script Editor could not fully restore the original tab.\n\nOriginal path:\n{pendingRestore.TargetScriptPath}";
					}
					else
					{
						dialogTitle = "Rename Failed";
						userMessage =
							$"The filesystem rename failed, and Godot's Script Editor could not fully restore the original tab.\n\nOriginal path:\n{pendingRestore.TargetScriptPath}\n\nSystem Explorer metadata remains unchanged.";
					}

					QueueStandaloneTreeOperationDialog(
						dialogTitle,
						userMessage,
						technicalFailureDetails
					);
				}

				DebugLogger.LogOperation(
					"Rename Script deferred editor restore failed",
					technicalFailureDetails
				);
			}
			else
			{
				if (deferredFailurePresentation != null)
				{
					QueueTreeOperationDialogPresentation(
						deferredFailurePresentation.Title,
						deferredFailurePresentation.UserMessage,
						deferredFailurePresentation.PersistentDeduplicationKey
					);
				}

				DebugLogger.LogOperation(
					"Rename Script deferred editor restore completed",
					$"mode={pendingRestore.Mode}, target='{pendingRestore.TargetScriptPath}', attempts={pendingRestore.DeferredAttemptCount}"
				);
			}

			// Tree selection is restored immediately without final focus, then once more
			// on a later deferred pass after any previous-active-script request has settled.
			RestoreScriptRenameTreeState(
				pendingRestore.TreeState,
				pendingRestore.SelectedEntry,
				restoreFocus: false
			);
		}
		catch (Exception exception)
		{
			DebugLogger.LogOperation("Rename Script terminal restore warning", exception.Message);
		}

		try
		{
			CallDeferred(nameof(FinalizePendingScriptRenameTreeRestoreDeferred));
		}
		catch (Exception exception)
		{
			DebugLogger.LogOperation(
				"Rename Script final tree restore scheduling warning",
				exception.Message
			);
			FinalizePendingScriptRenameTreeRestoreDeferred();
		}
	}

	private void FinalizePendingScriptRenameTreeRestoreDeferred()
	{
		PendingScriptRenameEditorRestore pendingRestore = _pendingScriptRenameEditorRestore;

		if (pendingRestore == null || !pendingRestore.IsCompleting)
			return;

		try
		{
			RestoreScriptRenameTreeState(
				pendingRestore.TreeState,
				pendingRestore.SelectedEntry,
				restoreFocus: true
			);
		}
		finally
		{
			_pendingScriptRenameEditorRestore = null;

			if (pendingRestore.EndSyncSuppression)
				EndScriptEditorSyncSuppression();
		}
	}

	private void CancelPendingScriptRenameEditorRestore()
	{
		PendingScriptRenameEditorRestore pendingRestore = _pendingScriptRenameEditorRestore;
		_pendingScriptRenameEditorRestore = null;

		if (pendingRestore?.EndSyncSuppression == true)
			EndScriptEditorSyncSuppression();
	}

	private static bool TryGetRejectedOldOpenScriptPath(
		ScriptEditor scriptEditor,
		PendingScriptRenameEditorRestore pendingRestore,
		out string rejectedOpenPath
	)
	{
		rejectedOpenPath = "";

		if (scriptEditor == null || pendingRestore == null)
			return false;

		string canonicalOldPath = ScriptPathUtility.Normalize(
			pendingRestore.CanonicalOldScriptPath
		);
		string finalTargetPath = ScriptPathUtility.Normalize(
			pendingRestore.FinalTargetScriptPath
		);
		bool isCaseOnlyRename =
			!string.Equals(canonicalOldPath, finalTargetPath, StringComparison.Ordinal)
			&& string.Equals(
				canonicalOldPath,
				finalTargetPath,
				StringComparison.OrdinalIgnoreCase
			);
		HashSet<string> reportedOldCasings = new(
			pendingRestore.OriginalReportedPaths ?? Array.Empty<string>(),
			StringComparer.Ordinal
		);

		if (!string.IsNullOrWhiteSpace(canonicalOldPath))
			reportedOldCasings.Add(canonicalOldPath);

		HashSet<ulong> inspectedScriptInstanceIds = new();

		foreach (Script openScript in scriptEditor.GetOpenScripts())
		{
			if (openScript == null || !GodotObject.IsInstanceValid(openScript))
				continue;

			ulong openScriptInstanceId = openScript.GetInstanceId();

			if (!inspectedScriptInstanceIds.Add(openScriptInstanceId))
				continue;

			string openPath = ScriptPathUtility.Normalize(openScript.ResourcePath);
			bool violatesPolicy = !isCaseOnlyRename
				? string.Equals(
					openPath,
					canonicalOldPath,
					StringComparison.OrdinalIgnoreCase
				)
				: !string.Equals(openPath, finalTargetPath, StringComparison.Ordinal)
					&& reportedOldCasings.Contains(openPath);

			if (!violatesPolicy)
				continue;

			rejectedOpenPath = openPath;
			return true;
		}

		return false;
	}

	private static string BuildScriptRenameEditorRestoreFailure(
		PendingScriptRenameEditorRestore pendingRestore,
		string reason,
		Script currentScript,
		ScriptEditorBase currentEditor,
		Control baseEditor,
		int finalPathMatchCount
	)
	{
		TextEdit currentTextEditor = baseEditor as TextEdit;
		string currentScriptPath = ScriptPathUtility.Normalize(currentScript?.ResourcePath);
		ulong currentScriptInstanceId = currentScript?.GetInstanceId() ?? 0;
		ulong currentEditorInstanceId = currentEditor?.GetInstanceId() ?? 0;
		ulong currentTextEditorInstanceId = currentTextEditor?.GetInstanceId() ?? 0;
		bool reusedEditorControl =
			currentEditorInstanceId != 0
			&& currentEditorInstanceId == pendingRestore.EditorState.ScriptEditorBaseInstanceId;
		bool reusedTextControl =
			currentTextEditorInstanceId != 0
			&& currentTextEditorInstanceId == pendingRestore.EditorState.TextEditorInstanceId;

		return $"{reason}\n\n"
			+ $"Attempt: {pendingRestore.DeferredAttemptCount}/{ScriptRenameEditorRestoreMaxDeferredAttempts}\n"
			+ $"Target path to open: {pendingRestore.TargetScriptPath}\n"
			+ $"Canonical old path: {pendingRestore.CanonicalOldScriptPath}\n"
			+ $"Exact final target path: {pendingRestore.FinalTargetScriptPath}\n"
			+ $"Original resource count: {pendingRestore.OriginalScriptInstanceIds.Count}\n"
			+ $"Original Script instance IDs: {FormatScriptInstanceIds(pendingRestore.OriginalScriptInstanceIds)}\n"
			+ $"Original reported paths: {FormatScriptPaths(pendingRestore.OriginalReportedPaths)}\n"
			+ $"Primary old Script instance ID: {pendingRestore.PrimaryOldScriptInstanceId}\n"
			+ $"Current script path: {currentScriptPath}\n"
			+ $"Final-path resource count: {finalPathMatchCount}\n"
			+ $"Current editor was null: {currentEditor == null}\n"
			+ $"Current base editor was TextEdit: {currentTextEditor != null}\n"
			+ $"Loaded Script instance ID: {pendingRestore.LoadedScriptInstanceId}\n"
			+ $"Current Script instance ID: {currentScriptInstanceId}\n"
			+ $"Old ScriptEditorBase instance ID: {pendingRestore.EditorState.ScriptEditorBaseInstanceId}\n"
			+ $"Current ScriptEditorBase instance ID: {currentEditorInstanceId}\n"
			+ $"ScriptEditorBase control reused: {reusedEditorControl}\n"
			+ $"Old TextEdit instance ID: {pendingRestore.EditorState.TextEditorInstanceId}\n"
			+ $"Current TextEdit instance ID: {currentTextEditorInstanceId}\n"
			+ $"TextEdit control reused: {reusedTextControl}";
	}

	private static void RestoreScriptRenameEditorState(
		TextEdit textEditor,
		ScriptRenameEditorState editorState
	)
	{
		if (textEditor == null || !GodotObject.IsInstanceValid(textEditor))
			return;

		int lineCount = Math.Max(1, textEditor.GetLineCount());
		int caretLine = Math.Clamp(editorState.CaretLine, 0, lineCount - 1);
		int caretColumn = Math.Clamp(
			editorState.CaretColumn,
			0,
			textEditor.GetLine(caretLine).Length
		);

		textEditor.RemoveSecondaryCarets();
		textEditor.Deselect();
		textEditor.SetCaretLine(caretLine, false);
		textEditor.SetCaretColumn(caretColumn, false);

		if (editorState.HadSelection)
		{
			int originLine = Math.Clamp(editorState.SelectionOriginLine, 0, lineCount - 1);
			int originColumn = Math.Clamp(
				editorState.SelectionOriginColumn,
				0,
				textEditor.GetLine(originLine).Length
			);
			textEditor.Select(originLine, originColumn, caretLine, caretColumn, 0);
		}

		int firstVisibleLine = Math.Clamp(editorState.FirstVisibleLine, 0, lineCount - 1);
		textEditor.SetLineAsFirstVisible(firstVisibleLine);
		textEditor.ScrollVertical = Math.Max(0.0, editorState.ScrollVertical);
		textEditor.ScrollHorizontal = Math.Max(0, editorState.ScrollHorizontal);
	}

	private static bool DoesScriptEditorStillContainOldScript(
		ScriptEditor scriptEditor,
		Script oldScript,
		string oldScriptPath
	)
	{
		if (scriptEditor == null)
			return true;

		string normalizedOldPath = ScriptPathUtility.Normalize(oldScriptPath);
		ulong oldScriptInstanceId = oldScript?.GetInstanceId() ?? 0;

		foreach (Script openScript in scriptEditor.GetOpenScripts())
		{
			if (openScript == null)
				continue;

			if (oldScriptInstanceId != 0 && openScript.GetInstanceId() == oldScriptInstanceId)
				return true;

			if (
				string.Equals(
					ScriptPathUtility.Normalize(openScript.ResourcePath),
					normalizedOldPath,
					StringComparison.OrdinalIgnoreCase
				)
			)
			{
				return true;
			}
		}

		return false;
	}

	private static void TryRestorePreviouslyActiveScript(
		EditorInterface editorInterface,
		ScriptEditor scriptEditor,
		Script activeScriptBeforeOperation,
		Script renamedScript
	)
	{
		if (
			editorInterface == null
			|| scriptEditor == null
			|| activeScriptBeforeOperation == null
			|| !GodotObject.IsInstanceValid(activeScriptBeforeOperation)
		)
		{
			return;
		}

		if (
			renamedScript != null
			&& GodotObject.IsInstanceValid(renamedScript)
			&& IsSameScriptResource(activeScriptBeforeOperation, renamedScript)
		)
		{
			return;
		}

		editorInterface.EditScript(activeScriptBeforeOperation, -1, 0, false);
	}

	private void QueueScriptRenameTreeRestore(
		ScriptRenameTreeState treeState,
		string selectedEntry,
		bool endSyncSuppression
	)
	{
		_deferredScriptRenameTreeState = treeState;
		_deferredScriptRenameSelectedEntry = selectedEntry ?? "";
		_deferredScriptRenameEndSyncSuppression = endSyncSuppression;

		RestoreScriptRenameTreeState(
			treeState,
			_deferredScriptRenameSelectedEntry,
			restoreFocus: false
		);
		CallDeferred(nameof(RestoreScriptRenameTreeStateDeferred));
	}

	private void RestoreScriptRenameTreeStateDeferred()
	{
		try
		{
			RestoreScriptRenameTreeState(
				_deferredScriptRenameTreeState,
				_deferredScriptRenameSelectedEntry,
				restoreFocus: true
			);
		}
		finally
		{
			_deferredScriptRenameTreeState = null;
			_deferredScriptRenameSelectedEntry = "";

			if (_deferredScriptRenameEndSyncSuppression)
			{
				_deferredScriptRenameEndSyncSuppression = false;
				EndScriptEditorSyncSuppression();
			}
		}
	}

	private void RestoreScriptRenameTreeState(
		ScriptRenameTreeState treeState,
		string selectedEntry,
		bool restoreFocus
	)
	{
		if (treeState == null || _tree == null || !GodotObject.IsInstanceValid(_tree))
			return;

		if (!treeState.WasFiltering)
			RestoreTreeExpansionStateSnapshot(treeState.ExpansionState);

		if (!string.IsNullOrWhiteSpace(selectedEntry))
			TrySelectExactScriptRenameTreeItem(treeState, selectedEntry);

		// Keep the restored selection visible without leaving keyboard focus on the
		// whole tree. This matches normal script navigation and avoids the tree-wide
		// focus outline after the rename dialog and deferred editor restore complete.
		if (restoreFocus)
			ReleaseTreeFocusAfterNavigation();
	}

	private bool TrySelectExactScriptRenameTreeItem(
		ScriptRenameTreeState treeState,
		string selectedEntry
	)
	{
		var selection = new PersistentTreeSelection(
			treeState.SystemName,
			$"script::{selectedEntry}"
		);

		if (
			TryRestoreTreeSelectionByIdentity(
				selection,
				"Rename Script tree restore"
			)
		)
		{
			return true;
		}

		DebugLogger.LogOperation(
			"Rename Script tree restore warning: exact entry not found",
			$"system='{treeState.SystemName}', folder='{treeState.FolderPath}', entry='{selectedEntry}'"
		);
		return false;
	}

	private bool TryGetMatchingOpenScriptResources(
		string scriptPath,
		out EditorInterface editorInterface,
		out ScriptEditor scriptEditor,
		out List<Script> matchingOpenScripts
	)
	{
		editorInterface = EditorInterface.Singleton;
		scriptEditor = editorInterface?.GetScriptEditor();
		matchingOpenScripts = new List<Script>();

		if (editorInterface == null || scriptEditor == null)
		{
			ReportTreeOperationFailureOrWarning(
                "System Explorer could not safely inspect Godot's Script Editor before renaming the script. The rename was cancelled."
			);
			DebugLogger.LogOperation("Rename Script failed: Script Editor unavailable", scriptPath);
			return false;
		}

		string normalizedScriptPath = ScriptPathUtility.Normalize(scriptPath);
		HashSet<ulong> matchedInstanceIds = new();

		foreach (Script openScript in scriptEditor.GetOpenScripts())
		{
			if (openScript == null || !GodotObject.IsInstanceValid(openScript))
				continue;

			string openScriptPath = ScriptPathUtility.Normalize(openScript.ResourcePath);

			if (
				string.IsNullOrWhiteSpace(openScriptPath)
				|| !string.Equals(
					openScriptPath,
					normalizedScriptPath,
					StringComparison.OrdinalIgnoreCase
				)
			)
			{
				continue;
			}

			if (matchedInstanceIds.Add(openScript.GetInstanceId()))
				matchingOpenScripts.Add(openScript);
		}

		return true;
	}

	private static List<Script> GetDistinctListedOpenScriptsByPath(
		ScriptEditor scriptEditor,
		string scriptPath
	)
	{
		List<Script> matchingScripts = new();

		if (scriptEditor == null || string.IsNullOrWhiteSpace(scriptPath))
			return matchingScripts;

		string normalizedScriptPath = ScriptPathUtility.Normalize(scriptPath);
		HashSet<ulong> matchedInstanceIds = new();

		foreach (Script openScript in scriptEditor.GetOpenScripts())
		{
			if (openScript == null || !GodotObject.IsInstanceValid(openScript))
				continue;

			string openScriptPath = ScriptPathUtility.Normalize(openScript.ResourcePath);

			if (
				!string.Equals(
					openScriptPath,
					normalizedScriptPath,
					StringComparison.OrdinalIgnoreCase
				) || !matchedInstanceIds.Add(openScript.GetInstanceId())
			)
			{
				continue;
			}

			matchingScripts.Add(openScript);
		}

		return matchingScripts;
	}

	private static List<Script> GetDistinctOpenScriptsByPath(
		ScriptEditor scriptEditor,
		string scriptPath
	)
	{
		List<Script> matchingScripts = new();

		if (scriptEditor == null || string.IsNullOrWhiteSpace(scriptPath))
			return matchingScripts;

		string normalizedScriptPath = ScriptPathUtility.Normalize(scriptPath);
		HashSet<ulong> matchedInstanceIds = new();

		foreach (Script openScript in scriptEditor.GetOpenScripts())
		{
			if (openScript == null || !GodotObject.IsInstanceValid(openScript))
				continue;

			string openScriptPath = ScriptPathUtility.Normalize(openScript.ResourcePath);

			if (
				!string.Equals(
					openScriptPath,
					normalizedScriptPath,
					StringComparison.OrdinalIgnoreCase
				) || !matchedInstanceIds.Add(openScript.GetInstanceId())
			)
			{
				continue;
			}

			matchingScripts.Add(openScript);
		}

		Script currentScript = scriptEditor.GetCurrentScript();

		if (currentScript != null && GodotObject.IsInstanceValid(currentScript))
		{
			string currentScriptPath = ScriptPathUtility.Normalize(currentScript.ResourcePath);
			ulong currentScriptInstanceId = currentScript.GetInstanceId();

			if (
				string.Equals(
					currentScriptPath,
					normalizedScriptPath,
					StringComparison.OrdinalIgnoreCase
				)
				&& matchedInstanceIds.Add(currentScriptInstanceId)
			)
			{
				matchingScripts.Add(currentScript);
			}
		}

		return matchingScripts;
	}

	private static bool IsSameScriptResource(Script left, Script right)
	{
		return left != null && right != null && left.GetInstanceId() == right.GetInstanceId();
	}

	private static string NormalizeRenameResourcePath(string path)
	{
		return ScriptPathUtility.Normalize(path);
	}

	private static string CombineResourcePath(string folderPath, string fileName)
	{
		string normalizedFolderPath = ScriptPathUtility.Normalize(folderPath);
		string separator = normalizedFolderPath.EndsWith(
			"/",
			StringComparison.Ordinal
		)
			? ""
			: "/";

		return ScriptPathUtility.Normalize(
			$"{normalizedFolderPath}{separator}{fileName ?? ""}"
		);
	}

	private bool TryCheckCaseOnlyRenameTargetConflict(
		string oldResourcePath,
		string newResourcePath,
		string resourceTypeName,
		string operationName,
		out bool hasTargetConflict
	)
	{
		hasTargetConflict = false;

		string normalizedOldResourcePath = NormalizeRenameResourcePath(oldResourcePath);
		string normalizedNewResourcePath = NormalizeRenameResourcePath(newResourcePath);
		string folderPath = NormalizeRenameResourcePath(
			normalizedOldResourcePath.GetBaseDir()
		);
		string oldFileName = normalizedOldResourcePath.GetFile();
		string newFileName = normalizedNewResourcePath.GetFile();

		using DirAccess directory = DirAccess.Open(folderPath);

		if (directory == null)
		{
			ReportTreeOperationFailureOrWarning(
				$"System Explorer could not inspect the {resourceTypeName} folder before the case-only rename: {folderPath}"
			);
			DebugLogger.LogOperation(
				$"{operationName} failed: could not inspect case-only target directory",
				folderPath
			);
			return false;
		}

		directory.IncludeHidden = true;

		bool hasExactOldEntry = false;
		bool hasExactNewEntry = false;

		foreach (string fileName in directory.GetFiles())
		{
			if (string.Equals(fileName, oldFileName, StringComparison.Ordinal))
				hasExactOldEntry = true;

			if (string.Equals(fileName, newFileName, StringComparison.Ordinal))
				hasExactNewEntry = true;
		}

		hasTargetConflict = hasExactOldEntry && hasExactNewEntry;
		return true;
	}

	private bool TryCheckUidRenameTargetConflict(
		string oldScriptPath,
		string newScriptPath,
		bool isCaseOnlyRename
	)
	{
		string oldUidPath = $"{oldScriptPath}.uid";

		if (!FileAccess.FileExists(oldUidPath))
			return true;

		string newUidPath = $"{newScriptPath}.uid";
		bool destinationUidExists;

		if (isCaseOnlyRename)
		{
			string folderPath = ScriptPathUtility.Normalize(oldScriptPath.GetBaseDir());
			using DirAccess directory = DirAccess.Open(folderPath);

			if (directory == null)
			{
				ReportTreeOperationFailureOrWarning(
					$"System Explorer could not inspect the script folder before checking the UID sidecar rename: {folderPath}"
				);
				DebugLogger.LogOperation(
					"Rename Script failed: could not inspect UID target directory",
					folderPath
				);
				return false;
			}

			directory.IncludeHidden = true;
			string oldUidFileName = oldUidPath.GetFile();
			string newUidFileName = newUidPath.GetFile();
			bool hasExactOldUid = false;
			bool hasExactNewUid = false;

			foreach (string fileName in directory.GetFiles())
			{
				if (string.Equals(fileName, oldUidFileName, StringComparison.Ordinal))
					hasExactOldUid = true;

				if (string.Equals(fileName, newUidFileName, StringComparison.Ordinal))
					hasExactNewUid = true;
			}

			destinationUidExists = hasExactOldUid && hasExactNewUid;
		}
		else
		{
			destinationUidExists = FileAccess.FileExists(newUidPath);
		}

		if (!destinationUidExists)
			return true;

		ReportTreeOperationFailureOrWarning(
			$"System Explorer could not rename the script because the destination UID sidecar already exists and will not be overwritten:\n{newUidPath}"
		);
		DebugLogger.LogOperation("Rename Script failed: destination UID sidecar exists", newUidPath);
		return false;
	}

	private bool TryRenameScriptOnce(
		string oldScriptPath,
		string newScriptPath,
		out bool originalPathAvailableAfterFailure
	)
	{
		originalPathAvailableAfterFailure = true;
		string oldUidPath = $"{oldScriptPath}.uid";
		string newUidPath = $"{newScriptPath}.uid";
		bool hasUidSidecar = FileAccess.FileExists(oldUidPath);

		Error scriptRenameError = DirAccess.RenameAbsolute(oldScriptPath, newScriptPath);

		if (scriptRenameError != Error.Ok)
		{
			ReportTreeOperationFailureOrWarning($"Could not rename script: {oldScriptPath} -> {newScriptPath}");
			DebugLogger.LogOperation(
				"Rename Script failed: filesystem rename error",
				$"{oldScriptPath} -> {newScriptPath} ({scriptRenameError})"
			);
			return false;
		}

		if (!hasUidSidecar)
			return true;

		Error uidRenameError = DirAccess.RenameAbsolute(oldUidPath, newUidPath);

		if (uidRenameError == Error.Ok)
		{
			DebugLogger.LogOperation("Rename Script: moved uid sidecar", $"{oldUidPath} -> {newUidPath}");
			return true;
		}

		Error scriptRollbackError = DirAccess.RenameAbsolute(newScriptPath, oldScriptPath);
		originalPathAvailableAfterFailure = scriptRollbackError == Error.Ok;

		if (scriptRollbackError == Error.Ok)
		{
			ReportTreeOperationFailureOrWarning(
				$"System Explorer could not move the script UID sidecar, so the script rename was rolled back:\n{oldScriptPath}"
			);
			DebugLogger.LogOperation(
				"Rename Script failed: UID move failed; script rollback succeeded",
				$"uid={uidRenameError}, rollback={scriptRollbackError}, old='{oldScriptPath}', new='{newScriptPath}'"
			);
			return false;
		}

		ReportTreeOperationFailureOrWarning(
			$"System Explorer could not move the script UID sidecar and could not roll back the script rename. The script may remain at the target path while its UID remains at the original path.\n\nOriginal: {oldScriptPath}\nTarget: {newScriptPath}"
		);
		DebugLogger.LogOperation(
			"Rename Script failed: UID move and script rollback failed",
			$"uid={uidRenameError}, rollback={scriptRollbackError}, old='{oldScriptPath}', new='{newScriptPath}'"
		);
		return false;
	}

	private bool TryRenameScriptCaseOnly(
		string oldScriptPath,
		string newScriptPath,
		out bool originalPathAvailableAfterFailure,
		out string temporaryScriptPath
	)
	{
		originalPathAvailableAfterFailure = true;
		string folderPath = ScriptPathUtility.Normalize(oldScriptPath.GetBaseDir());
		temporaryScriptPath = CreateUniqueCaseRenameTemporaryPath(folderPath, ".cs");

		if (string.IsNullOrWhiteSpace(temporaryScriptPath))
		{
			ReportTreeOperationFailureOrWarning(
				$"System Explorer could not create a unique temporary path for the case-only rename: {oldScriptPath} -> {newScriptPath}"
			);
			DebugLogger.LogOperation(
				"Rename Script failed: temporary case-only path unavailable",
				$"{oldScriptPath} -> {newScriptPath}"
			);
			return false;
		}

		string oldUidPath = $"{oldScriptPath}.uid";
		string newUidPath = $"{newScriptPath}.uid";
		string temporaryUidPath = $"{temporaryScriptPath}.uid";
		bool hasUidSidecar = FileAccess.FileExists(oldUidPath);

		Error firstScriptRenameError = DirAccess.RenameAbsolute(oldScriptPath, temporaryScriptPath);

		if (firstScriptRenameError != Error.Ok)
		{
			ReportTreeOperationFailureOrWarning(
				$"Could not begin case-only script rename: {oldScriptPath} -> {newScriptPath}"
			);
			DebugLogger.LogOperation(
				"Rename Script failed: first case-only rename step",
				$"{oldScriptPath} -> {temporaryScriptPath} ({firstScriptRenameError})"
			);
			return false;
		}

		if (hasUidSidecar)
		{
			Error firstUidRenameError = DirAccess.RenameAbsolute(oldUidPath, temporaryUidPath);

			if (firstUidRenameError != Error.Ok)
			{
				Error scriptRollbackError = DirAccess.RenameAbsolute(
					temporaryScriptPath,
					oldScriptPath
				);
				originalPathAvailableAfterFailure = scriptRollbackError == Error.Ok;
				ReportTreeOperationFailureOrWarning(
					$"System Explorer could not begin the case-only UID sidecar rename. The script rename was {(scriptRollbackError == Error.Ok ? "rolled back" : "not fully rolled back")}.\n\nOriginal: {oldScriptPath}\nTemporary: {temporaryScriptPath}"
				);
				DebugLogger.LogOperation(
					"Rename Script failed: first case-only UID step",
					$"uid={firstUidRenameError}, scriptRollback={scriptRollbackError}, old='{oldScriptPath}', temporary='{temporaryScriptPath}'"
				);
				return false;
			}
		}

		Error secondScriptRenameError = DirAccess.RenameAbsolute(
			temporaryScriptPath,
			newScriptPath
		);

		if (secondScriptRenameError != Error.Ok)
		{
			Error temporaryUidRollbackError = hasUidSidecar
				? DirAccess.RenameAbsolute(temporaryUidPath, oldUidPath)
				: Error.Ok;
			Error scriptRollbackError = DirAccess.RenameAbsolute(
				temporaryScriptPath,
				oldScriptPath
			);
			originalPathAvailableAfterFailure =
				temporaryUidRollbackError == Error.Ok && scriptRollbackError == Error.Ok;

			ReportTreeOperationFailureOrWarning(
				originalPathAvailableAfterFailure
					? $"System Explorer could not complete the case-only script rename, but the original script and UID sidecar were restored:\n{oldScriptPath}"
					: $"System Explorer could not complete or fully roll back the case-only script rename.\n\nOriginal: {oldScriptPath}\nTemporary: {temporaryScriptPath}\nTarget: {newScriptPath}"
			);
			DebugLogger.LogOperation(
				"Rename Script failed: second case-only script step",
				$"second={secondScriptRenameError}, uidRollback={temporaryUidRollbackError}, scriptRollback={scriptRollbackError}, old='{oldScriptPath}', temporary='{temporaryScriptPath}', new='{newScriptPath}'"
			);
			return false;
		}

		if (!hasUidSidecar)
			return true;

		Error secondUidRenameError = DirAccess.RenameAbsolute(temporaryUidPath, newUidPath);

		if (secondUidRenameError == Error.Ok)
		{
			DebugLogger.LogOperation("Rename Script: moved uid sidecar", $"{oldUidPath} -> {newUidPath}");
			return true;
		}

		Error scriptToTemporaryRollbackError = DirAccess.RenameAbsolute(
			newScriptPath,
			temporaryScriptPath
		);
		Error uidRollbackError = DirAccess.RenameAbsolute(temporaryUidPath, oldUidPath);
		Error scriptToOriginalRollbackError =
			scriptToTemporaryRollbackError == Error.Ok
				? DirAccess.RenameAbsolute(temporaryScriptPath, oldScriptPath)
				: scriptToTemporaryRollbackError;
		originalPathAvailableAfterFailure =
			scriptToTemporaryRollbackError == Error.Ok
			&& uidRollbackError == Error.Ok
			&& scriptToOriginalRollbackError == Error.Ok;

		ReportTreeOperationFailureOrWarning(
			originalPathAvailableAfterFailure
				? $"System Explorer could not complete the case-only UID sidecar rename, so the script and UID were restored:\n{oldScriptPath}"
				: $"System Explorer could not complete or fully roll back the case-only UID sidecar rename.\n\nOriginal: {oldScriptPath}\nTemporary: {temporaryScriptPath}\nTarget: {newScriptPath}"
		);
		DebugLogger.LogOperation(
			"Rename Script failed: second case-only UID step",
			$"uid={secondUidRenameError}, scriptToTemporary={scriptToTemporaryRollbackError}, uidRollback={uidRollbackError}, scriptToOriginal={scriptToOriginalRollbackError}, old='{oldScriptPath}', temporary='{temporaryScriptPath}', new='{newScriptPath}'"
		);
		return false;
	}


	private RenameFilesystemRollbackResult RollbackScriptRenameOnceAfterSaveFailure(
		string oldScriptPath,
		string newScriptPath,
		bool hadUidSidecar
	)
	{
		return RollbackRenamedResourceAfterSaveFailure(
			oldScriptPath,
			newScriptPath,
			".cs",
			isCaseOnlyRename: false,
			sidecarSuffix: ".uid",
			expectedSidecar: hadUidSidecar
		);
	}

	private RenameFilesystemRollbackResult RollbackScriptRenameCaseOnlyAfterSaveFailure(
		string oldScriptPath,
		string newScriptPath,
		bool hadUidSidecar
	)
	{
		return RollbackRenamedResourceAfterSaveFailure(
			oldScriptPath,
			newScriptPath,
			".cs",
			isCaseOnlyRename: true,
			sidecarSuffix: ".uid",
			expectedSidecar: hadUidSidecar
		);
	}

	private RenameFilesystemRollbackResult RollbackSceneRenameOnceAfterSaveFailure(
		string oldScenePath,
		string newScenePath
	)
	{
		return RollbackRenamedResourceAfterSaveFailure(
			oldScenePath,
			newScenePath,
			".tscn",
			isCaseOnlyRename: false,
			sidecarSuffix: "",
			expectedSidecar: false
		);
	}

	private RenameFilesystemRollbackResult RollbackSceneRenameCaseOnlyAfterSaveFailure(
		string oldScenePath,
		string newScenePath
	)
	{
		return RollbackRenamedResourceAfterSaveFailure(
			oldScenePath,
			newScenePath,
			".tscn",
			isCaseOnlyRename: true,
			sidecarSuffix: "",
			expectedSidecar: false
		);
	}

	private RenameFilesystemRollbackResult RollbackRenamedResourceAfterSaveFailure(
		string oldResourcePath,
		string newResourcePath,
		string temporaryExtension,
		bool isCaseOnlyRename,
		string sidecarSuffix,
		bool expectedSidecar
	)
	{
		bool hasSidecarType = !string.IsNullOrWhiteSpace(sidecarSuffix);
		string oldSidecarPath = hasSidecarType ? $"{oldResourcePath}{sidecarSuffix}" : "";
		string newSidecarPath = hasSidecarType ? $"{newResourcePath}{sidecarSuffix}" : "";
		bool oldSidecarExists = false;
		bool newSidecarExists = false;

		if (
			!TryGetExactFilePresence(oldResourcePath, out bool oldResourceExists, out string inspectionFailure)
			|| !TryGetExactFilePresence(newResourcePath, out bool newResourceExists, out inspectionFailure)
			|| (
				hasSidecarType
				&& (
					!TryGetExactFilePresence(oldSidecarPath, out oldSidecarExists, out inspectionFailure)
					|| !TryGetExactFilePresence(newSidecarPath, out newSidecarExists, out inspectionFailure)
				)
			)
		)
		{
			return new RenameFilesystemRollbackResult(
				RenameFilesystemRollbackState.Unclear,
				"",
				"",
				$"Rollback preflight inspection failed: {inspectionFailure}"
			);
		}

		bool oldSidecarConflict = hasSidecarType && oldSidecarExists;
		bool targetSidecarStateMatches = !hasSidecarType || newSidecarExists == expectedSidecar;

		if (oldResourceExists || oldSidecarConflict)
		{
			return InspectRenameRollbackState(
				oldResourcePath,
				newResourcePath,
				"",
				sidecarSuffix,
				expectedSidecar,
				allowOldConflictForRetainedTarget: true,
				operationDetail: $"Rollback was blocked because the original destination was recreated. oldResource={oldResourceExists}, oldSidecar={oldSidecarConflict}."
			);
		}

		if (!newResourceExists || !targetSidecarStateMatches)
		{
			return InspectRenameRollbackState(
				oldResourcePath,
				newResourcePath,
				"",
				sidecarSuffix,
				expectedSidecar,
				allowOldConflictForRetainedTarget: false,
				operationDetail: $"Rollback preflight could not verify the complete target state. targetResource={newResourceExists}, targetSidecarMatches={targetSidecarStateMatches}."
			);
		}

		if (!isCaseOnlyRename)
		{
			Error resourceRollbackError = DirAccess.RenameAbsolute(
				newResourcePath,
				oldResourcePath
			);

			if (resourceRollbackError != Error.Ok)
			{
				return InspectRenameRollbackState(
					oldResourcePath,
					newResourcePath,
					"",
					sidecarSuffix,
					expectedSidecar,
					allowOldConflictForRetainedTarget: false,
					operationDetail: $"Reverse resource rename failed with {resourceRollbackError}."
				);
			}

			if (expectedSidecar)
			{
				if (
					!TryGetExactFilePresence(
						oldSidecarPath,
						out bool lateOldSidecarConflict,
						out string lateSidecarInspectionFailure
					)
					|| lateOldSidecarConflict
				)
				{
					Error resourceReturnError = DirAccess.RenameAbsolute(
						oldResourcePath,
						newResourcePath
					);
					return InspectRenameRollbackState(
						oldResourcePath,
						newResourcePath,
						"",
						sidecarSuffix,
						expectedSidecar,
						allowOldConflictForRetainedTarget: true,
						operationDetail: $"The original sidecar destination became unavailable after the resource rollback step. conflict={lateOldSidecarConflict}, inspection='{lateSidecarInspectionFailure}', resource return={resourceReturnError}."
					);
				}

				Error sidecarRollbackError = DirAccess.RenameAbsolute(
					newSidecarPath,
					oldSidecarPath
				);

				if (sidecarRollbackError != Error.Ok)
				{
					Error resourceReturnError = DirAccess.RenameAbsolute(
						oldResourcePath,
						newResourcePath
					);
					return InspectRenameRollbackState(
						oldResourcePath,
						newResourcePath,
						"",
						sidecarSuffix,
						expectedSidecar,
						allowOldConflictForRetainedTarget: false,
						operationDetail: $"Reverse sidecar rename failed with {sidecarRollbackError}; returning the resource to the target returned {resourceReturnError}."
					);
				}
			}

			return InspectRenameRollbackState(
				oldResourcePath,
				newResourcePath,
				"",
				sidecarSuffix,
				expectedSidecar,
				allowOldConflictForRetainedTarget: false,
				operationDetail: "Reverse resource rename completed."
			);
		}

		string temporaryResourcePath = CreateUniqueRollbackTemporaryPath(
			oldResourcePath.GetBaseDir(),
			temporaryExtension,
			sidecarSuffix
		);

		if (string.IsNullOrWhiteSpace(temporaryResourcePath))
		{
			return InspectRenameRollbackState(
				oldResourcePath,
				newResourcePath,
				"",
				sidecarSuffix,
				expectedSidecar,
				allowOldConflictForRetainedTarget: false,
				operationDetail: "No unique temporary path was available for the case-only rollback."
			);
		}

		string temporarySidecarPath = hasSidecarType
			? $"{temporaryResourcePath}{sidecarSuffix}"
			: "";
		Error firstResourceError = DirAccess.RenameAbsolute(
			newResourcePath,
			temporaryResourcePath
		);

		if (firstResourceError != Error.Ok)
		{
			return InspectRenameRollbackState(
				oldResourcePath,
				newResourcePath,
				temporaryResourcePath,
				sidecarSuffix,
				expectedSidecar,
				allowOldConflictForRetainedTarget: false,
				operationDetail: $"First case-only reverse resource step failed with {firstResourceError}."
			);
		}

		if (expectedSidecar)
		{
			Error firstSidecarError = DirAccess.RenameAbsolute(
				newSidecarPath,
				temporarySidecarPath
			);

			if (firstSidecarError != Error.Ok)
			{
				Error resourceReturnError = DirAccess.RenameAbsolute(
					temporaryResourcePath,
					newResourcePath
				);
				return InspectRenameRollbackState(
					oldResourcePath,
					newResourcePath,
					temporaryResourcePath,
					sidecarSuffix,
					expectedSidecar,
					allowOldConflictForRetainedTarget: false,
					operationDetail: $"First case-only reverse sidecar step failed with {firstSidecarError}; resource return={resourceReturnError}."
				);
			}
		}

		bool lateOriginalConflict =
			!TryGetExactFilePresence(
				oldResourcePath,
				out bool lateOldResourceExists,
				out string lateOriginalInspectionFailure
			)
			|| lateOldResourceExists;

		if (hasSidecarType && !lateOriginalConflict)
		{
			lateOriginalConflict =
				!TryGetExactFilePresence(
					oldSidecarPath,
					out bool lateOldSidecarExists,
					out lateOriginalInspectionFailure
				)
				|| lateOldSidecarExists;
		}

		if (lateOriginalConflict)
		{
			Error sidecarReturnError = expectedSidecar
				? DirAccess.RenameAbsolute(temporarySidecarPath, newSidecarPath)
				: Error.Ok;
			Error resourceReturnError = DirAccess.RenameAbsolute(
				temporaryResourcePath,
				newResourcePath
			);
			return InspectRenameRollbackState(
				oldResourcePath,
				newResourcePath,
				temporaryResourcePath,
				sidecarSuffix,
				expectedSidecar,
				allowOldConflictForRetainedTarget: true,
				operationDetail: $"The exact original destination became unavailable between the case-only rollback steps. inspection='{lateOriginalInspectionFailure}', sidecar return={sidecarReturnError}, resource return={resourceReturnError}."
			);
		}

		Error secondResourceError = DirAccess.RenameAbsolute(
			temporaryResourcePath,
			oldResourcePath
		);

		if (secondResourceError != Error.Ok)
		{
			Error sidecarReturnError = expectedSidecar
				? DirAccess.RenameAbsolute(temporarySidecarPath, newSidecarPath)
				: Error.Ok;
			Error resourceReturnError = DirAccess.RenameAbsolute(
				temporaryResourcePath,
				newResourcePath
			);
			return InspectRenameRollbackState(
				oldResourcePath,
				newResourcePath,
				temporaryResourcePath,
				sidecarSuffix,
				expectedSidecar,
				allowOldConflictForRetainedTarget: false,
				operationDetail: $"Second case-only reverse resource step failed with {secondResourceError}; sidecar return={sidecarReturnError}, resource return={resourceReturnError}."
			);
		}

		if (expectedSidecar)
		{
			if (
				!TryGetExactFilePresence(
					oldSidecarPath,
					out bool secondLateOldSidecarConflict,
					out string secondLateSidecarInspectionFailure
				)
				|| secondLateOldSidecarConflict
			)
			{
				Error resourceToTemporaryError = DirAccess.RenameAbsolute(
					oldResourcePath,
					temporaryResourcePath
				);
				Error sidecarReturnError = DirAccess.RenameAbsolute(
					temporarySidecarPath,
					newSidecarPath
				);
				Error resourceReturnError =
					resourceToTemporaryError == Error.Ok
						? DirAccess.RenameAbsolute(temporaryResourcePath, newResourcePath)
						: resourceToTemporaryError;
				return InspectRenameRollbackState(
					oldResourcePath,
					newResourcePath,
					temporaryResourcePath,
					sidecarSuffix,
					expectedSidecar,
					allowOldConflictForRetainedTarget: true,
					operationDetail: $"The original sidecar destination became unavailable before the final case-only sidecar step. conflict={secondLateOldSidecarConflict}, inspection='{secondLateSidecarInspectionFailure}', resource-to-temporary={resourceToTemporaryError}, sidecar return={sidecarReturnError}, resource return={resourceReturnError}."
				);
			}

			Error secondSidecarError = DirAccess.RenameAbsolute(
				temporarySidecarPath,
				oldSidecarPath
			);

			if (secondSidecarError != Error.Ok)
			{
				Error resourceToTemporaryError = DirAccess.RenameAbsolute(
					oldResourcePath,
					temporaryResourcePath
				);
				Error sidecarReturnError = DirAccess.RenameAbsolute(
					temporarySidecarPath,
					newSidecarPath
				);
				Error resourceReturnError =
					resourceToTemporaryError == Error.Ok
						? DirAccess.RenameAbsolute(temporaryResourcePath, newResourcePath)
						: resourceToTemporaryError;
				return InspectRenameRollbackState(
					oldResourcePath,
					newResourcePath,
					temporaryResourcePath,
					sidecarSuffix,
					expectedSidecar,
					allowOldConflictForRetainedTarget: false,
					operationDetail: $"Second case-only reverse sidecar step failed with {secondSidecarError}; resource-to-temporary={resourceToTemporaryError}, sidecar return={sidecarReturnError}, resource return={resourceReturnError}."
				);
			}
		}

		return InspectRenameRollbackState(
			oldResourcePath,
			newResourcePath,
			temporaryResourcePath,
			sidecarSuffix,
			expectedSidecar,
			allowOldConflictForRetainedTarget: false,
			operationDetail: "Case-only reverse resource steps completed."
		);
	}

	private RenameFilesystemRollbackResult InspectRenameRollbackState(
		string oldResourcePath,
		string newResourcePath,
		string temporaryResourcePath,
		string sidecarSuffix,
		bool expectedSidecar,
		bool allowOldConflictForRetainedTarget,
		string operationDetail
	)
	{
		bool hasSidecarType = !string.IsNullOrWhiteSpace(sidecarSuffix);
		List<string> inspectionFailures = new();
		bool oldResourceExists = GetExactPresenceForRollback(
			oldResourcePath,
			inspectionFailures
		);
		bool newResourceExists = GetExactPresenceForRollback(
			newResourcePath,
			inspectionFailures
		);
		bool temporaryResourceExists = !string.IsNullOrWhiteSpace(temporaryResourcePath)
			&& GetExactPresenceForRollback(temporaryResourcePath, inspectionFailures);
		bool oldSidecarExists = hasSidecarType
			&& GetExactPresenceForRollback($"{oldResourcePath}{sidecarSuffix}", inspectionFailures);
		bool newSidecarExists = hasSidecarType
			&& GetExactPresenceForRollback($"{newResourcePath}{sidecarSuffix}", inspectionFailures);
		bool temporarySidecarExists =
			hasSidecarType
			&& !string.IsNullOrWhiteSpace(temporaryResourcePath)
			&& GetExactPresenceForRollback(
				$"{temporaryResourcePath}{sidecarSuffix}",
				inspectionFailures
			);
		int resourceLocationCount =
			(oldResourceExists ? 1 : 0)
			+ (newResourceExists ? 1 : 0)
			+ (temporaryResourceExists ? 1 : 0);
		string verifiedResourcePath = resourceLocationCount == 1
			? oldResourceExists
				? oldResourcePath
				: newResourceExists
					? newResourcePath
					: temporaryResourcePath
			: "";
		bool originalSidecarStateVerified =
			!hasSidecarType
			|| (oldSidecarExists == expectedSidecar
				&& !newSidecarExists
				&& !temporarySidecarExists);
		bool targetSidecarStateVerified =
			!hasSidecarType
			|| (newSidecarExists == expectedSidecar
				&& !temporarySidecarExists
				&& (allowOldConflictForRetainedTarget || !oldSidecarExists));
		RenameFilesystemRollbackState state =
			inspectionFailures.Count == 0
			&& oldResourceExists
			&& !newResourceExists
			&& !temporaryResourceExists
			&& originalSidecarStateVerified
				? RenameFilesystemRollbackState.OriginalRestored
				: inspectionFailures.Count == 0
					&& newResourceExists
					&& !temporaryResourceExists
					&& (allowOldConflictForRetainedTarget || !oldResourceExists)
					&& targetSidecarStateVerified
						? RenameFilesystemRollbackState.TargetRetained
						: RenameFilesystemRollbackState.Unclear;
		string details =
			$"{operationDetail} oldResource={oldResourceExists}, targetResource={newResourceExists}, temporaryResource={temporaryResourceExists}, oldSidecar={oldSidecarExists}, targetSidecar={newSidecarExists}, temporarySidecar={temporarySidecarExists}, expectedSidecar={expectedSidecar}";

		if (inspectionFailures.Count > 0)
			details += $", inspectionFailures='{string.Join(" | ", inspectionFailures)}'";

		return new RenameFilesystemRollbackResult(
			state,
			verifiedResourcePath,
			temporaryResourcePath ?? "",
			details
		);
	}

	private static bool GetExactPresenceForRollback(
		string resourcePath,
		List<string> inspectionFailures
	)
	{
		bool inspected = TryGetExactFilePresence(
			resourcePath,
			out bool exists,
			out string failureDetail
		);

		if (!inspected && !string.IsNullOrWhiteSpace(failureDetail))
			inspectionFailures.Add(failureDetail);

		return inspected && exists;
	}

	private static bool TryGetExactFilePresence(
		string resourcePath,
		out bool exists,
		out string failureDetail
	)
	{
		exists = false;
		failureDetail = "";
		string normalizedPath = NormalizeRenameResourcePath(resourcePath);

		if (string.IsNullOrWhiteSpace(normalizedPath))
		{
			failureDetail = "The exact-path inspection received an empty path.";
			return false;
		}

		string folderPath = NormalizeRenameResourcePath(normalizedPath.GetBaseDir());
		string fileName = normalizedPath.GetFile();
		using DirAccess directory = DirAccess.Open(folderPath);

		if (directory == null)
		{
			failureDetail = $"Could not inspect directory '{folderPath}' for exact path '{normalizedPath}'.";
			return false;
		}

		directory.IncludeHidden = true;

		foreach (string existingFileName in directory.GetFiles())
		{
			if (!string.Equals(existingFileName, fileName, StringComparison.Ordinal))
				continue;

			exists = true;
			break;
		}

		return true;
	}

	private static string CreateUniqueRollbackTemporaryPath(
		string folderPath,
		string extension,
		string sidecarSuffix
	)
	{
		for (int attempt = 0; attempt < 16; attempt++)
		{
			string temporaryPath = CreateUniqueCaseRenameTemporaryPath(folderPath, extension);

			if (
				!string.IsNullOrWhiteSpace(temporaryPath)
				&& (
					string.IsNullOrWhiteSpace(sidecarSuffix)
					|| !FileAccess.FileExists($"{temporaryPath}{sidecarSuffix}")
				)
			)
			{
				return temporaryPath;
			}
		}

		return "";
	}

	private static string CreateUniqueCaseRenameTemporaryPath(
		string folderPath,
		string extension
	)
	{
		string normalizedFolderPath = NormalizeRenameResourcePath(folderPath);
		string normalizedExtension = extension?.Trim() ?? "";

		if (
			string.IsNullOrWhiteSpace(normalizedFolderPath)
			|| string.IsNullOrWhiteSpace(normalizedExtension)
		)
		{
			return "";
		}

		if (!normalizedExtension.StartsWith(".", StringComparison.Ordinal))
			normalizedExtension = $".{normalizedExtension}";

		for (int attempt = 0; attempt < 16; attempt++)
		{
			string temporaryFileName =
				$".__system_explorer_case_rename_{Guid.NewGuid():N}{normalizedExtension}";
			string temporaryResourcePath = CombineResourcePath(
				normalizedFolderPath,
				temporaryFileName
			);

			if (!FileAccess.FileExists(temporaryResourcePath))
				return temporaryResourcePath;
		}

		return "";
	}

	private readonly record struct SceneRenameMetadataUpdateResult(
		int UpdatedSceneEntryCount,
		int UpdatedLinkedScriptCount
	)
	{
		internal bool Changed =>
			UpdatedSceneEntryCount > 0 || UpdatedLinkedScriptCount > 0;
	}

	private RenameMutationResult RenameScene(
		string metadata,
		string newName,
		out string finalScenePath
	)
	{
		finalScenePath = "";
		string entry = metadata.Substring("sceneLink::".Length);

		if (
			!TryGetUnambiguousPhysicalResourcePath(
				entry,
				true,
				out string unambiguousScenePath
			)
		)
		{
			ReportTreeOperationFailureOrWarning(
				"System Explorer cancelled the scene rename because the stored entry could not be interpreted safely. No project file was changed."
			);
			DebugLogger.LogOperation(
				"Rename Scene failed: ambiguous stored entry",
				entry
			);
			return RenameMutationResult.Failed;
		}

		string oldScenePath = NormalizeRenameResourcePath(unambiguousScenePath);

		if (newName.Contains("/") || newName.Contains("\\"))
		{
			ReportTreeOperationFailureOrWarning(
				"Scene rename only supports changing the file name, not the folder path."
			);
			DebugLogger.LogOperation("Rename Scene failed: invalid name", newName);
			return RenameMutationResult.Failed;
		}

		string folderPath = NormalizeRenameResourcePath(oldScenePath.GetBaseDir());
		string newFileName = newName.EndsWith(
			".tscn",
			StringComparison.OrdinalIgnoreCase
		)
			? newName
			: $"{newName}.tscn";
		string newScenePath = CombineResourcePath(folderPath, newFileName);
		bool isExactSamePath = string.Equals(
			oldScenePath,
			newScenePath,
			StringComparison.Ordinal
		);
		bool isCaseOnlyRename =
			!isExactSamePath
			&& string.Equals(
				oldScenePath,
				newScenePath,
				StringComparison.OrdinalIgnoreCase
			);

		if (isExactSamePath)
			return RenameMutationResult.NoChange;

		if (!EnsureSystemsLoadedForTreeOperation("Rename Scene"))
			return RenameMutationResult.Failed;

		if (!FileAccess.FileExists(oldScenePath))
		{
			ReportTreeOperationFailureOrWarning($"File does not exist: {oldScenePath}");
			DebugLogger.LogOperation("Rename Scene failed: file missing", oldScenePath);
			return RenameMutationResult.Failed;
		}

		if (isCaseOnlyRename)
		{
			if (
				!TryCheckCaseOnlyRenameTargetConflict(
					oldScenePath,
					newScenePath,
					"scene",
					"Rename Scene",
					out bool hasTargetConflict
				)
			)
			{
				return RenameMutationResult.Failed;
			}

			if (hasTargetConflict)
			{
				DebugLogger.LogOperation(
					"Rename Scene failed: case-only name conflict",
					newScenePath
				);
				return RenameMutationResult.NameConflict;
			}
		}
		else if (FileAccess.FileExists(newScenePath))
		{
			DebugLogger.LogOperation("Rename Scene failed: name conflict", newScenePath);
			return RenameMutationResult.NameConflict;
		}

		if (
			!DoesAnySystemContainEntry(entry)
			&& (
				!TryRecoverSystemsFromDisk("Rename Scene")
				|| !DoesAnySystemContainEntry(entry)
			)
		)
		{
			ReportTreeOperationFailure(
				"System Explorer could not verify the scene metadata entry required for Rename Scene. The scene file was not renamed.",
				$"Entry='{entry}', Path='{oldScenePath}'"
			);
			return RenameMutationResult.Failed;
		}

		if (
			!TryPreflightMetadataPersistenceForPhysicalMutation(
				"Rename Scene",
				systemsRequired: true,
				folderBindingsRequired: false,
				physicalConsequence: "The scene file was not renamed."
			)
		)
		{
			return RenameMutationResult.Failed;
		}

		string temporaryScenePath = "";

		if (isCaseOnlyRename)
		{
			temporaryScenePath = CreateUniqueCaseRenameTemporaryPath(folderPath, ".tscn");

			if (string.IsNullOrWhiteSpace(temporaryScenePath))
			{
				ReportTreeOperationFailureOrWarning(
					$"System Explorer could not create a unique temporary path for the case-only scene rename: {oldScenePath} -> {newScenePath}"
				);
				DebugLogger.LogOperation(
					"Rename Scene failed: temporary case-only path unavailable",
					$"old='{oldScenePath}', target='{newScenePath}'"
				);
				return RenameMutationResult.Failed;
			}

			Error firstRenameError = DirAccess.RenameAbsolute(
				oldScenePath,
				temporaryScenePath
			);

			if (firstRenameError != Error.Ok)
			{
				ReportTreeOperationFailureOrWarning(
					$"Could not begin case-only scene rename: {oldScenePath} -> {newScenePath}"
				);
				DebugLogger.LogOperation(
					"Rename Scene failed: first case-only rename step",
					$"error={firstRenameError}, old='{oldScenePath}', temporary='{temporaryScenePath}', target='{newScenePath}'"
				);
				return RenameMutationResult.Failed;
			}

			Error secondRenameError = DirAccess.RenameAbsolute(
				temporaryScenePath,
				newScenePath
			);

			if (secondRenameError != Error.Ok)
			{
				Error rollbackError = DirAccess.RenameAbsolute(
					temporaryScenePath,
					oldScenePath
				);
				bool rollbackRestoredOriginal =
					rollbackError == Error.Ok && FileAccess.FileExists(oldScenePath);

				ReportTreeOperationFailureOrWarning(
					rollbackRestoredOriginal
						? $"System Explorer could not complete the case-only scene rename, but the original scene was restored:\n{oldScenePath}"
						: $"System Explorer could not complete or roll back the case-only scene rename. The scene file may remain at the temporary path.\n\nOriginal: {oldScenePath}\nTemporary: {temporaryScenePath}\nTarget: {newScenePath}"
				);
				DebugLogger.LogOperation(
					"Rename Scene failed: second case-only rename step",
					$"second={secondRenameError}, rollback={rollbackError}, rollbackVerified={rollbackRestoredOriginal}, old='{oldScenePath}', temporary='{temporaryScenePath}', target='{newScenePath}'"
				);

				if (!rollbackRestoredOriginal)
					RequestRenameFilesystemFinalStateRefresh();

				return RenameMutationResult.Failed;
			}
		}
		else
		{
			Error renameError = DirAccess.RenameAbsolute(oldScenePath, newScenePath);

			if (renameError != Error.Ok)
			{
				ReportTreeOperationFailureOrWarning($"Could not rename scene: {oldScenePath} -> {newScenePath}");
				DebugLogger.LogOperation(
					"Rename Scene failed: filesystem rename error",
					$"error={renameError}, old='{oldScenePath}', target='{newScenePath}'"
				);
				return RenameMutationResult.Failed;
			}
		}

		if (!FileAccess.FileExists(newScenePath))
		{
			ReportTreeOperationFailureOrWarning(
				$"System Explorer completed the filesystem rename call, but the final scene path could not be verified:\n{newScenePath}\n\nThe System Explorer data was not updated."
			);
			DebugLogger.LogOperation(
				"Rename Scene failed: final path missing after filesystem success",
				$"old='{oldScenePath}', temporary='{temporaryScenePath}', target='{newScenePath}'"
			);
			RequestRenameFilesystemFinalStateRefresh();
			return RenameMutationResult.Failed;
		}

		SystemsAndFolderBindingsSnapshot metadataSnapshot =
			CaptureSystemsAndFolderBindingsSnapshot();
		string selectedScriptEntryBeforeMetadataUpdate = _selectedScriptEntryFromFilter;
		SceneRenameMetadataUpdateResult metadataUpdateResult = UpdateSceneReferences(
			oldScenePath,
			newScenePath
		);

		if (!metadataUpdateResult.Changed)
		{
			ReportTreeOperationFailure(
				$"The scene file was renamed, but no matching System Explorer scene entry or linked script reference could be updated.\n\nOld: {oldScenePath}\nNew: {newScenePath}",
				$"Old='{oldScenePath}', New='{newScenePath}'",
				TreeOperationOutcomeSeverity.Incomplete
			);
			DebugLogger.LogOperation(
				"Rename Scene warning: no System Explorer references updated after filesystem success",
				$"old='{oldScenePath}', target='{newScenePath}'"
			);
			RequestRenameFilesystemFinalStateRefresh();
			return RenameMutationResult.Failed;
		}
		else if (!SaveSystems())
		{
			RenameFilesystemRollbackResult rollbackResult = isCaseOnlyRename
				? RollbackSceneRenameCaseOnlyAfterSaveFailure(oldScenePath, newScenePath)
				: RollbackSceneRenameOnceAfterSaveFailure(oldScenePath, newScenePath);

			DebugLogger.LogOperation(
				"Rename Scene save-failure rollback completed",
				$"state={rollbackResult.State}, old='{oldScenePath}', target='{newScenePath}', temporary='{rollbackResult.TemporaryPath}', verified='{rollbackResult.VerifiedResourcePath}', details='{rollbackResult.Details}'"
			);

			if (rollbackResult.State == RenameFilesystemRollbackState.OriginalRestored)
			{
				RestoreSystemsAndFolderBindingsSnapshot(metadataSnapshot);
				_selectedScriptEntryFromFilter = selectedScriptEntryBeforeMetadataUpdate;
				bool restoredMetadataSaved = SaveSystems();

				if (restoredMetadataSaved)
				{
					ReportTreeOperationFailure(
						$"System Explorer could not save the renamed scene metadata, so the physical rename was rolled back and the original scene path and metadata were restored:\n{oldScenePath}",
						$"Original='{oldScenePath}', Target='{newScenePath}', MetadataRestoreVerified=true",
						TreeOperationOutcomeSeverity.Failed,
						replaceExistingReport: true
					);
				}
				else
				{
					ReportTreeOperationFailure(
						"The physical scene rename was rolled back, but the restored systems.json state could not be verified. Restart Godot and inspect systems.json before continuing.",
						$"Original='{oldScenePath}', Target='{newScenePath}', MetadataRestoreVerified=false",
						TreeOperationOutcomeSeverity.FinalStateUnclear
					);
					DebugLogger.LogOperation(
						"Rename Scene rollback warning: restored systems save failed",
						oldScenePath
					);
				}

				SaveExpansionState();
				BuildTree(keepCurrentExpansionState: true);
				RequestRenameFilesystemFinalStateRefresh();
				return RenameMutationResult.Failed;
			}

			if (rollbackResult.State == RenameFilesystemRollbackState.TargetRetained)
			{
				bool metadataStateUnclear = IsActiveTreeOperationFinalStateUnclear;
				ReportTreeOperationFailure(
					metadataStateUnclear
						? $"The scene was renamed and remains at the new path, but the final state of System Explorer's updated metadata could not be verified:\n{newScenePath}"
						: $"The scene was renamed, but System Explorer could not save systems.json or roll back the physical rename. The scene remains at the new path:\n{newScenePath}",
					rollbackResult.Details,
					metadataStateUnclear
						? TreeOperationOutcomeSeverity.FinalStateUnclear
						: TreeOperationOutcomeSeverity.Incomplete
				);
				DebugLogger.LogOperation(
					"Rename Scene warning: save failed and target rename retained",
					rollbackResult.Details
				);
				SaveExpansionState();
				BuildTree(keepCurrentExpansionState: true);
				RequestRenameFilesystemFinalStateRefresh();
				return RenameMutationResult.Failed;
			}
			else
			{
				string verifiedScenePath = rollbackResult.VerifiedResourcePath;

				if (string.IsNullOrWhiteSpace(verifiedScenePath))
				{
					RestoreSystemsAndFolderBindingsSnapshot(metadataSnapshot);
					_selectedScriptEntryFromFilter = selectedScriptEntryBeforeMetadataUpdate;
				}
				else if (string.Equals(verifiedScenePath, oldScenePath, StringComparison.Ordinal))
				{
					RestoreSystemsAndFolderBindingsSnapshot(metadataSnapshot);
					_selectedScriptEntryFromFilter = selectedScriptEntryBeforeMetadataUpdate;
					SaveSystems();
				}
				else if (
					!string.IsNullOrWhiteSpace(verifiedScenePath)
					&& !string.Equals(verifiedScenePath, newScenePath, StringComparison.Ordinal)
				)
				{
					RestoreSystemsAndFolderBindingsSnapshot(metadataSnapshot);
					_selectedScriptEntryFromFilter = selectedScriptEntryBeforeMetadataUpdate;
					SceneRenameMetadataUpdateResult temporaryMetadataUpdate =
						UpdateSceneReferences(oldScenePath, verifiedScenePath);

					if (temporaryMetadataUpdate.Changed)
						SaveSystems();
				}

				ReportTreeOperationFailureOrWarning(
					$"System Explorer could not save systems.json and the scene rollback ended in an unclear physical state. The operation was not reported as successful.\n\nOriginal: {oldScenePath}\nTarget: {newScenePath}\nTemporary: {rollbackResult.TemporaryPath}\nVerified scene path: {(string.IsNullOrWhiteSpace(verifiedScenePath) ? "none" : verifiedScenePath)}\n\nInspect these paths and systems.json before continuing."
				);
				DebugLogger.LogOperation(
					"Rename Scene failed: unclear save-failure rollback state",
					rollbackResult.Details
				);

				SaveExpansionState();
				BuildTree(keepCurrentExpansionState: true);
				RequestRenameFilesystemFinalStateRefresh();
				return RenameMutationResult.Failed;
			}
		}

		SaveExpansionState();
		BuildTree(keepCurrentExpansionState: true);
		RequestRenameFilesystemFinalStateRefresh();

		DebugLogger.LogOperation(
			"Rename Scene Mutated",
			$"old='{oldScenePath}', target='{newScenePath}', sceneEntries={metadataUpdateResult.UpdatedSceneEntryCount}, linkedScripts={metadataUpdateResult.UpdatedLinkedScriptCount}"
		);

		finalScenePath = newScenePath;
		return RenameMutationResult.Success;
	}

	private SceneRenameMetadataUpdateResult UpdateSceneReferences(
		string oldScenePath,
		string newScenePath
	)
	{
		string normalizedOldScenePath = NormalizeRenameResourcePath(oldScenePath);
		string normalizedNewScenePath = NormalizeRenameResourcePath(newScenePath);
		int updatedSceneEntryCount = 0;
		int updatedLinkedScriptCount = 0;

		foreach (string systemName in _systems.Keys.ToList())
		{
			List<string> currentEntries = _systems[systemName] ?? new List<string>();
			List<string> updatedEntries = new(currentEntries.Count);

			foreach (string entry in currentEntries)
			{
				if (entry.StartsWith("folder::", StringComparison.Ordinal))
				{
					updatedEntries.Add(entry);
					continue;
				}

				if (IsSceneEntry(entry))
				{
					string scenePath = NormalizeRenameResourcePath(GetScenePathFromEntry(entry));

					if (
						string.Equals(
							scenePath,
							normalizedOldScenePath,
							StringComparison.OrdinalIgnoreCase
						)
					)
					{
						updatedEntries.Add(
							BuildSceneEntry(
								GetFolderPathFromEntry(entry),
								normalizedNewScenePath,
								IsEntryLocked(entry)
							)
						);
						updatedSceneEntryCount++;
						continue;
					}

					updatedEntries.Add(entry);
					continue;
				}

				string linkedScenePath = NormalizeRenameResourcePath(
					GetLinkedScenePathFromEntry(entry)
				);

				if (
					!string.Equals(
						linkedScenePath,
						normalizedOldScenePath,
						StringComparison.OrdinalIgnoreCase
					)
				)
				{
					updatedEntries.Add(entry);
					continue;
				}

				string updatedEntry = BuildScriptEntry(
					GetFolderPathFromEntry(entry),
					GetScriptPathFromEntry(entry),
					normalizedNewScenePath,
					IsEntryLocked(entry)
				);

				UpdateSelectedScriptEntryFromFilter(entry, updatedEntry);
				updatedEntries.Add(updatedEntry);
				updatedLinkedScriptCount++;
			}

			_systems[systemName] = updatedEntries;
		}

		return new SceneRenameMetadataUpdateResult(
			updatedSceneEntryCount,
			updatedLinkedScriptCount
		);
	}

	private bool UpdateScriptEntries(string oldScriptPath, string newScriptPath)
	{
		if (!EnsureSystemsLoadedForTreeOperation("Update Script Entries"))
			return false;

		string normalizedOldScriptPath = ScriptPathUtility.Normalize(oldScriptPath);
		string normalizedNewScriptPath = ScriptPathUtility.Normalize(newScriptPath);
		int updatedEntryCount = 0;

		foreach (string systemName in _systems.Keys.ToList())
		{
			List<string> updatedEntries = new();

			foreach (string entry in _systems[systemName])
			{
				if (entry.StartsWith("folder::"))
				{
					updatedEntries.Add(entry);
					continue;
				}

				string scriptPath = ScriptPathUtility.Normalize(GetScriptPathFromEntry(entry));

				if (
					!string.Equals(
						scriptPath,
						normalizedOldScriptPath,
						StringComparison.OrdinalIgnoreCase
					)
				)
				{
					updatedEntries.Add(entry);
					continue;
				}

				string folderPath = GetFolderPathFromEntry(entry);

				string linkedScenePath = GetLinkedScenePathFromEntry(entry);
				string updatedEntry = BuildScriptEntry(
					folderPath,
					normalizedNewScriptPath,
					linkedScenePath,
					IsEntryLocked(entry)
				);

				UpdateSelectedScriptEntryFromFilter(entry, updatedEntry);
				updatedEntryCount++;

				updatedEntries.Add(updatedEntry);
			}

			_systems[systemName] = updatedEntries.Distinct().ToList();
		}

		return updatedEntryCount > 0;
	}

	#endregion
}
#endif
