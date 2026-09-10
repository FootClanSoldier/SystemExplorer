#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;

namespace SystemExplorer.Notes;

internal sealed class NoteLifecycleStore
{
	private readonly NoteFilePersistence _filePersistence;

	internal NoteLifecycleStore(string notesDirectoryPath)
	{
		_filePersistence = new NoteFilePersistence(notesDirectoryPath);
	}

	internal bool TryApplySystemRename(
		string oldSystemName,
		string newSystemName,
		out NoteLifecycleMutation mutation,
		out bool finalStateUnclear,
		out string failureDetail
	)
	{
		mutation = NoteLifecycleMutation.NoOp;
		finalStateUnclear = false;
		failureDetail = "";

		if (!TryValidateSystemName(oldSystemName, "old-system-name", out failureDetail))
			return false;

		if (!TryValidateSystemName(newSystemName, "new-system-name", out failureDetail))
			return false;

		if (string.Equals(oldSystemName, newSystemName, StringComparison.Ordinal))
			return true;

		if (!TryCaptureSnapshot(oldSystemName, out NoteLifecycleMutation.SystemFileSnapshot oldSnapshot, out failureDetail))
			return false;

		if (!oldSnapshot.FileExisted)
			return true;

		if (!TryCaptureSnapshot(newSystemName, out NoteLifecycleMutation.SystemFileSnapshot newSnapshot, out failureDetail))
			return false;

		if (!TryDeserializeSnapshot(oldSnapshot, out NoteSystemDocument oldDocument, out failureDetail))
			return false;

		if (newSnapshot.FileExisted)
		{
			failureDetail = NotePersistenceFailure.BuildFailureDetail(
				oldSystemName,
				"system-rename-destination",
				$"DestinationSystemName={NotePersistenceFailure.FormatForDetail(newSystemName)}, Detail='Destination Note file already exists and will not be overwritten.'"
			);
			return false;
		}

		NoteLifecycleMutation appliedMutation = new(
			new[]
			{
				oldSnapshot,
				newSnapshot,
			}
		);

		NoteSystemDocument migratedDocument = new(newSystemName)
		{
			Note = oldDocument.Note,
		};

		foreach (KeyValuePair<string, string> folder in oldDocument.Folders)
			migratedDocument.Folders.Add(folder.Key, folder.Value);

		if (!TrySerializeDocument(migratedDocument, "serialize-system-rename", out byte[] migratedBytes, out failureDetail))
			return false;

		if (!_filePersistence.TryWriteSystemFile(newSystemName, migratedBytes, out string writeFailureDetail))
		{
			return FailAfterMutationAttempt(
				appliedMutation,
				writeFailureDetail,
				out finalStateUnclear,
				out failureDetail
			);
		}

		if (!_filePersistence.TryDeleteSystemFile(oldSystemName, out string deleteFailureDetail))
		{
			return FailAfterMutationAttempt(
				appliedMutation,
				deleteFailureDetail,
				out finalStateUnclear,
				out failureDetail
			);
		}

		mutation = appliedMutation;
		return true;
	}

	internal bool TryApplyFolderRename(
		string systemName,
		string oldFolderPath,
		string newFolderPath,
		out NoteLifecycleMutation mutation,
		out bool finalStateUnclear,
		out string failureDetail
	)
	{
		mutation = NoteLifecycleMutation.NoOp;
		finalStateUnclear = false;
		failureDetail = "";

		if (!TryValidateSystemName(systemName, "system-name", out failureDetail))
			return false;

		if (!TryValidateFolderPath(systemName, oldFolderPath, "old-folder-path", out failureDetail))
			return false;

		if (!TryValidateFolderPath(systemName, newFolderPath, "new-folder-path", out failureDetail))
			return false;

		if (string.Equals(oldFolderPath, newFolderPath, StringComparison.Ordinal))
			return true;

		if (!TryCaptureSnapshot(systemName, out NoteLifecycleMutation.SystemFileSnapshot snapshot, out failureDetail))
			return false;

		if (!snapshot.FileExisted)
			return true;

		if (!TryDeserializeSnapshot(snapshot, out NoteSystemDocument document, out failureDetail))
			return false;

		List<string> sourceKeys = document.Folders.Keys
			.Where(path => IsPathInSubtree(path, oldFolderPath))
			.ToList();

		if (sourceKeys.Count == 0)
			return true;

		HashSet<string> sourceSet = new(sourceKeys, StringComparer.Ordinal);
		Dictionary<string, string> destinationBySource = new(StringComparer.Ordinal);

		foreach (string sourceKey in sourceKeys)
		{
			string destinationKey = BuildRenamedSubtreePath(
				sourceKey,
				oldFolderPath,
				newFolderPath
			);

			if (
				document.Folders.ContainsKey(destinationKey)
				&& !sourceSet.Contains(destinationKey)
			)
			{
				failureDetail = NotePersistenceFailure.BuildFailureDetail(
					systemName,
					"folder-rename-collision",
					$"OldFolderPath={NotePersistenceFailure.FormatForDetail(oldFolderPath)}, NewFolderPath={NotePersistenceFailure.FormatForDetail(newFolderPath)}, DestinationFolderPath={NotePersistenceFailure.FormatForDetail(destinationKey)}, Detail='Destination folder Note already exists outside the renamed source subtree.'"
				);
				return false;
			}

			destinationBySource.Add(sourceKey, destinationKey);
		}

		NoteSystemDocument migratedDocument = new(systemName)
		{
			Note = document.Note,
		};

		foreach (KeyValuePair<string, string> folder in document.Folders)
		{
			if (!sourceSet.Contains(folder.Key))
				migratedDocument.Folders.Add(folder.Key, folder.Value);
		}

		foreach (string sourceKey in sourceKeys)
		{
			migratedDocument.Folders.Add(
				destinationBySource[sourceKey],
				document.Folders[sourceKey]
			);
		}

		if (!TrySerializeDocument(migratedDocument, "serialize-folder-rename", out byte[] migratedBytes, out failureDetail))
			return false;

		NoteLifecycleMutation appliedMutation = new(new[] { snapshot });

		if (!_filePersistence.TryRewriteExistingSystemFile(systemName, migratedBytes, out string writeFailureDetail))
		{
			return FailAfterMutationAttempt(
				appliedMutation,
				writeFailureDetail,
				out finalStateUnclear,
				out failureDetail
			);
		}

		mutation = appliedMutation;
		return true;
	}

	internal bool TryApplySystemRemove(
		string systemName,
		out NoteLifecycleMutation mutation,
		out bool finalStateUnclear,
		out string failureDetail
	)
	{
		mutation = NoteLifecycleMutation.NoOp;
		finalStateUnclear = false;
		failureDetail = "";

		if (!TryValidateSystemName(systemName, "system-name", out failureDetail))
			return false;

		if (!TryCaptureSnapshot(systemName, out NoteLifecycleMutation.SystemFileSnapshot snapshot, out failureDetail))
			return false;

		if (!snapshot.FileExisted)
			return true;

		if (!TryDeserializeSnapshot(snapshot, out _, out failureDetail))
			return false;

		NoteLifecycleMutation appliedMutation = new(new[] { snapshot });

		if (!_filePersistence.TryDeleteSystemFile(systemName, out string deleteFailureDetail))
		{
			return FailAfterMutationAttempt(
				appliedMutation,
				deleteFailureDetail,
				out finalStateUnclear,
				out failureDetail
			);
		}

		mutation = appliedMutation;
		return true;
	}

	internal bool TryApplyFolderRemove(
		string systemName,
		string folderPath,
		out NoteLifecycleMutation mutation,
		out bool finalStateUnclear,
		out string failureDetail
	)
	{
		mutation = NoteLifecycleMutation.NoOp;
		finalStateUnclear = false;
		failureDetail = "";

		if (!TryValidateSystemName(systemName, "system-name", out failureDetail))
			return false;

		if (!TryValidateFolderPath(systemName, folderPath, "folder-path", out failureDetail))
			return false;

		if (!TryCaptureSnapshot(systemName, out NoteLifecycleMutation.SystemFileSnapshot snapshot, out failureDetail))
			return false;

		if (!snapshot.FileExisted)
			return true;

		if (!TryDeserializeSnapshot(snapshot, out NoteSystemDocument document, out failureDetail))
			return false;

		List<string> keysToRemove = document.Folders.Keys
			.Where(path => IsPathInSubtree(path, folderPath))
			.ToList();

		if (keysToRemove.Count == 0)
			return true;

		HashSet<string> removeSet = new(keysToRemove, StringComparer.Ordinal);
		NoteSystemDocument remainingDocument = new(systemName)
		{
			Note = document.Note,
		};

		foreach (KeyValuePair<string, string> folder in document.Folders)
		{
			if (!removeSet.Contains(folder.Key))
				remainingDocument.Folders.Add(folder.Key, folder.Value);
		}

		NoteLifecycleMutation appliedMutation = new(new[] { snapshot });

		if (remainingDocument.Note.Length == 0 && remainingDocument.Folders.Count == 0)
		{
			if (!_filePersistence.TryDeleteSystemFile(systemName, out string deleteFailureDetail))
			{
				return FailAfterMutationAttempt(
					appliedMutation,
					deleteFailureDetail,
					out finalStateUnclear,
					out failureDetail
				);
			}
		}
		else
		{
			if (!TrySerializeDocument(remainingDocument, "serialize-folder-remove", out byte[] remainingBytes, out failureDetail))
				return false;

			if (!_filePersistence.TryRewriteExistingSystemFile(systemName, remainingBytes, out string writeFailureDetail))
			{
				return FailAfterMutationAttempt(
					appliedMutation,
					writeFailureDetail,
					out finalStateUnclear,
					out failureDetail
				);
			}
		}

		mutation = appliedMutation;
		return true;
	}

	internal bool TryRollback(
		NoteLifecycleMutation mutation,
		out bool finalStateUnclear,
		out string failureDetail
	)
	{
		finalStateUnclear = false;
		failureDetail = "";

		if (mutation == null)
		{
			finalStateUnclear = true;
			failureDetail = "Phase='note-lifecycle-rollback', Detail='Rollback mutation token was null.'";
			return false;
		}

		if (mutation.IsNoOp)
			return true;

		List<string> restoreOperationFailures = new();

		foreach (NoteLifecycleMutation.SystemFileSnapshot snapshot in mutation.Snapshots.Where(snapshot => snapshot.FileExisted))
		{
			if (
				!_filePersistence.TryWriteSystemFile(
					snapshot.SystemName,
					snapshot.GetExactOriginalBytesCopy(),
					out string restoreFailureDetail
				)
			)
			{
				restoreOperationFailures.Add(
					$"SystemName={NotePersistenceFailure.FormatForDetail(snapshot.SystemName)}, RestoreOperationFailure=({restoreFailureDetail})"
				);
			}
		}

		foreach (NoteLifecycleMutation.SystemFileSnapshot snapshot in mutation.Snapshots.Where(snapshot => !snapshot.FileExisted))
		{
			if (!_filePersistence.TryDeleteSystemFile(snapshot.SystemName, out string deleteFailureDetail))
			{
				restoreOperationFailures.Add(
					$"SystemName={NotePersistenceFailure.FormatForDetail(snapshot.SystemName)}, RestoreAbsentOperationFailure=({deleteFailureDetail})"
				);
			}
		}

		List<string> verificationFailures = new();

		foreach (NoteLifecycleMutation.SystemFileSnapshot snapshot in mutation.Snapshots)
		{
			if (!TryVerifySnapshotRestored(snapshot, out string verificationFailureDetail))
				verificationFailures.Add(verificationFailureDetail);
		}

		if (verificationFailures.Count == 0)
			return true;

		finalStateUnclear = true;
		List<string> allDetails = new();
		allDetails.AddRange(verificationFailures);

		if (restoreOperationFailures.Count > 0)
			allDetails.AddRange(restoreOperationFailures);

		failureDetail =
			"Phase='note-lifecycle-rollback', Detail='One or more Note system-file states could not be restored and verified.', "
			+ string.Join(" | ", allDetails);
		return false;
	}

	private bool FailAfterMutationAttempt(
		NoteLifecycleMutation mutation,
		string primaryFailureDetail,
		out bool finalStateUnclear,
		out string failureDetail
	)
	{
		if (TryRollback(mutation, out _, out string rollbackFailureDetail))
		{
			finalStateUnclear = false;
			failureDetail =
				$"{primaryFailureDetail} | LifecycleRollback='Previous Note file state restored and verified.'";
			return false;
		}

		finalStateUnclear = true;
		failureDetail =
			$"{primaryFailureDetail} | LifecycleRollbackFailure=({rollbackFailureDetail})";
		return false;
	}

	private bool TryCaptureSnapshot(
		string systemName,
		out NoteLifecycleMutation.SystemFileSnapshot snapshot,
		out string failureDetail
	)
	{
		snapshot = null;
		failureDetail = "";

		if (
			!_filePersistence.TryReadSystemFile(
				systemName,
				out bool fileExists,
				out byte[] bytes,
				out failureDetail
			)
		)
		{
			return false;
		}

		snapshot = new NoteLifecycleMutation.SystemFileSnapshot(
			systemName,
			fileExists,
			fileExists ? bytes : Array.Empty<byte>()
		);
		return true;
	}

	private static bool TryDeserializeSnapshot(
		NoteLifecycleMutation.SystemFileSnapshot snapshot,
		out NoteSystemDocument document,
		out string failureDetail
	)
	{
		document = null;
		failureDetail = "";

		if (snapshot == null || !snapshot.FileExisted)
		{
			failureDetail = "Phase='note-lifecycle-read', Detail='Expected an existing Note system-file snapshot.'";
			return false;
		}

		return NoteDocumentCodec.TryDeserializeAndValidateDocument(
			snapshot.SystemName,
			snapshot.GetExactOriginalBytesCopy(),
			out document,
			out failureDetail
		);
	}

	private static bool TrySerializeDocument(
		NoteSystemDocument document,
		string phase,
		out byte[] bytes,
		out string failureDetail
	)
	{
		bytes = null;
		failureDetail = "";

		try
		{
			bytes = NoteDocumentCodec.SerializeDocument(document);
			return true;
		}
		catch (Exception exception)
		{
			failureDetail = NotePersistenceFailure.BuildFailureDetail(
				document?.SystemName ?? "",
				phase,
				$"Exception='{exception}'"
			);
			return false;
		}
	}

	private bool TryVerifySnapshotRestored(
		NoteLifecycleMutation.SystemFileSnapshot snapshot,
		out string failureDetail
	)
	{
		failureDetail = "";

		if (
			!_filePersistence.TryReadSystemFile(
				snapshot.SystemName,
				out bool exists,
				out byte[] bytes,
				out string readFailureDetail
			)
		)
		{
			failureDetail =
				$"SystemName={NotePersistenceFailure.FormatForDetail(snapshot.SystemName)}, VerificationReadFailure=({readFailureDetail})";
			return false;
		}

		if (!snapshot.FileExisted)
		{
			if (!exists)
				return true;

			failureDetail =
				$"SystemName={NotePersistenceFailure.FormatForDetail(snapshot.SystemName)}, Detail='Rollback expected the Note system file to be absent, but it still exists.'";
			return false;
		}

		if (!exists)
		{
			failureDetail =
				$"SystemName={NotePersistenceFailure.FormatForDetail(snapshot.SystemName)}, Detail='Rollback expected the original Note system file to exist, but it is absent.'";
			return false;
		}

		byte[] expectedBytes = snapshot.GetExactOriginalBytesCopy();
		if (ByteArraysMatch(expectedBytes, bytes))
			return true;

		failureDetail =
			$"SystemName={NotePersistenceFailure.FormatForDetail(snapshot.SystemName)}, Detail='Rollback Note system-file bytes did not match the exact original bytes.'";
		return false;
	}

	private static string BuildRenamedSubtreePath(
		string sourcePath,
		string oldFolderPath,
		string newFolderPath
	)
	{
		if (string.Equals(sourcePath, oldFolderPath, StringComparison.Ordinal))
			return newFolderPath;

		string suffix = sourcePath.Substring(oldFolderPath.Length);
		return newFolderPath + suffix;
	}

	private static bool IsPathInSubtree(string candidatePath, string folderPath)
	{
		return string.Equals(candidatePath, folderPath, StringComparison.Ordinal)
			|| candidatePath.StartsWith(folderPath + "/", StringComparison.Ordinal);
	}

	private static bool TryValidateSystemName(
		string systemName,
		string fieldName,
		out string failureDetail
	)
	{
		failureDetail = "";

		if (!string.IsNullOrWhiteSpace(systemName))
			return true;

		failureDetail =
			$"Phase='note-lifecycle-validation', Field='{fieldName}', Detail='System name was blank.'";
		return false;
	}

	private static bool TryValidateFolderPath(
		string systemName,
		string folderPath,
		string fieldName,
		out string failureDetail
	)
	{
		failureDetail = "";

		if (!string.IsNullOrWhiteSpace(folderPath))
			return true;

		failureDetail = NotePersistenceFailure.BuildFailureDetail(
			systemName,
			"note-lifecycle-validation",
			$"Field='{fieldName}', Detail='Folder path was blank.'"
		);
		return false;
	}

	private static bool ByteArraysMatch(byte[] left, byte[] right)
	{
		if (ReferenceEquals(left, right))
			return true;

		if (left == null || right == null || left.Length != right.Length)
			return false;

		for (int index = 0; index < left.Length; index++)
		{
			if (left[index] != right[index])
				return false;
		}

		return true;
	}
}
#endif
