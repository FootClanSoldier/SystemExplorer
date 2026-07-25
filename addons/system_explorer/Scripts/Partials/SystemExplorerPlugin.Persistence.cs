#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

public partial class SystemExplorerPlugin
{
	#region Persistence and Save Guards
	private enum SystemsFileReadStatus
	{
		Missing,
		ValidEmpty,
		ValidNonEmpty,
		OpenFailed,
		InvalidJson,
	}

	private sealed class SystemsFileReadResult
	{
		internal SystemsFileReadStatus Status { get; }
		internal Dictionary<string, List<string>> Systems { get; }
		internal string FailureDetail { get; }

		internal bool IsValid =>
			Status == SystemsFileReadStatus.ValidEmpty
			|| Status == SystemsFileReadStatus.ValidNonEmpty;

		internal SystemsFileReadResult(
			SystemsFileReadStatus status,
			Dictionary<string, List<string>> systems = null,
			string failureDetail = ""
		)
		{
			Status = status;
			Systems = systems ?? new Dictionary<string, List<string>>();
			FailureDetail = failureDetail ?? "";
		}
	}

	private sealed class IntentionalEmptySystemsSaveAuthorization
	{
		internal string SystemName { get; }
		internal int VerifiedSystemCount { get; }
		internal string VerifiedDiskState { get; }

		internal IntentionalEmptySystemsSaveAuthorization(
			string systemName,
			int verifiedSystemCount,
			string verifiedDiskState
		)
		{
			SystemName = systemName ?? "";
			VerifiedSystemCount = verifiedSystemCount;
			VerifiedDiskState = verifiedDiskState ?? "";
		}
	}

	private sealed class SystemsAndFolderBindingsSnapshot
	{
		internal Dictionary<string, List<string>> Systems { get; }
		internal Dictionary<string, Dictionary<string, string>> FolderBindings { get; }

		internal SystemsAndFolderBindingsSnapshot(
			Dictionary<string, List<string>> systems,
			Dictionary<string, Dictionary<string, string>> folderBindings
		)
		{
			Systems = systems;
			FolderBindings = folderBindings;
		}
	}

	private SystemsAndFolderBindingsSnapshot CaptureSystemsAndFolderBindingsSnapshot()
	{
		Dictionary<string, List<string>> systemsSnapshot = new(_systems.Comparer);

		foreach (KeyValuePair<string, List<string>> system in _systems)
		{
			systemsSnapshot[system.Key] = system.Value == null
				? new List<string>()
				: new List<string>(system.Value);
		}

		Dictionary<string, Dictionary<string, string>> folderBindingsSnapshot =
			new(_folderBindings.Comparer);

		foreach (
			KeyValuePair<string, Dictionary<string, string>> systemBinding in _folderBindings
		)
		{
			Dictionary<string, string> bindings = systemBinding.Value;
			folderBindingsSnapshot[systemBinding.Key] = bindings == null
				? new Dictionary<string, string>(StringComparer.Ordinal)
				: new Dictionary<string, string>(bindings, bindings.Comparer);
		}

		return new SystemsAndFolderBindingsSnapshot(
			systemsSnapshot,
			folderBindingsSnapshot
		);
	}

	private void RestoreSystemsAndFolderBindingsSnapshot(
		SystemsAndFolderBindingsSnapshot snapshot
	)
	{
		if (snapshot == null)
			throw new ArgumentNullException(nameof(snapshot));

		_systems.Clear();

		foreach (KeyValuePair<string, List<string>> system in snapshot.Systems)
		{
			_systems[system.Key] = system.Value == null
				? new List<string>()
				: new List<string>(system.Value);
		}

		_folderBindings.Clear();

		foreach (
			KeyValuePair<string, Dictionary<string, string>> systemBinding in snapshot.FolderBindings
		)
		{
			Dictionary<string, string> bindings = systemBinding.Value;
			_folderBindings[systemBinding.Key] = bindings == null
				? new Dictionary<string, string>(StringComparer.Ordinal)
				: new Dictionary<string, string>(bindings, bindings.Comparer);
		}
	}

	private bool SaveSystemsForCoordinatedMetadataMutation(
		IntentionalEmptySystemsSaveAuthorization intentionalEmptyAuthorization
	)
	{
		return _systems.Count == 0 && intentionalEmptyAuthorization != null
			? SaveSystems(intentionalEmptyAuthorization)
			: SaveSystems();
	}

	private bool TryPersistReversibleSystemsAndFolderBindingsMutation(
		SystemsAndFolderBindingsSnapshot snapshot,
		bool systemsChanged,
		bool folderBindingsChanged,
		string operationName,
		IntentionalEmptySystemsSaveAuthorization intentionalEmptyAuthorization = null
	)
	{
		if (snapshot == null)
			throw new ArgumentNullException(nameof(snapshot));

		operationName = string.IsNullOrWhiteSpace(operationName)
			? "Metadata Operation"
			: operationName.Trim();

		if (!systemsChanged && !folderBindingsChanged)
		{
			DebugLogger.LogOperation(
				"Coordinated metadata persistence skipped: no changes",
				operationName
			);
			return true;
		}

		DebugLogger.LogOperation(
			"Coordinated metadata persistence requested",
			$"Operation='{operationName}', SystemsChanged={systemsChanged}, FolderBindingsChanged={folderBindingsChanged}"
		);

		if (folderBindingsChanged && !systemsChanged)
		{
			if (SaveFolderBindings())
				return true;

			RestoreSystemsAndFolderBindingsSnapshot(snapshot);
			DebugLogger.LogOperation(
				"Coordinated metadata persistence failed",
				$"Operation='{operationName}', FailedSave=folder_bindings.json, BindingRollbackAttempted=false, BindingRollbackSucceeded=false"
			);
			return false;
		}

		if (systemsChanged && !folderBindingsChanged)
		{
			if (SaveSystemsForCoordinatedMetadataMutation(intentionalEmptyAuthorization))
				return true;

			RestoreSystemsAndFolderBindingsSnapshot(snapshot);
			DebugLogger.LogOperation(
				"Coordinated metadata persistence failed",
				$"Operation='{operationName}', FailedSave=systems.json, BindingRollbackAttempted=false, BindingRollbackSucceeded=false"
			);
			return false;
		}

		if (!SaveFolderBindings())
		{
			RestoreSystemsAndFolderBindingsSnapshot(snapshot);
			DebugLogger.LogOperation(
				"Coordinated metadata persistence failed",
				$"Operation='{operationName}', FailedSave=folder_bindings.json, BindingRollbackAttempted=false, BindingRollbackSucceeded=false"
			);
			return false;
		}

		if (SaveSystemsForCoordinatedMetadataMutation(intentionalEmptyAuthorization))
		{
			DebugLogger.LogOperation(
				"Coordinated metadata persistence completed",
				operationName
			);
			return true;
		}

		RestoreSystemsAndFolderBindingsSnapshot(snapshot);
		bool bindingRollbackSucceeded = SaveFolderBindings();

		DebugLogger.LogOperation(
			"Coordinated metadata persistence failed",
			$"Operation='{operationName}', FailedSave=systems.json, BindingRollbackAttempted=true, BindingRollbackSucceeded={bindingRollbackSucceeded}"
		);

		if (!bindingRollbackSucceeded)
		{
			GD.PushWarning(
				"System Explorer could not fully roll back the metadata operation because folder_bindings.json could not be restored. Restart Godot and inspect the plugin metadata files before continuing."
			);
		}

		return false;
	}

	private enum MetadataWriteFinalState
	{
		Succeeded,
		PreviousTargetPreserved,
		PreviousTargetRestoredAndVerified,
		FinalTargetStateUnclear,
	}

	private sealed class MetadataWriteResult
	{
		internal MetadataWriteFinalState FinalState { get; }
		internal string FailureDetail { get; }
		internal bool Succeeded => FinalState == MetadataWriteFinalState.Succeeded;

		internal MetadataWriteResult(
			MetadataWriteFinalState finalState,
			string failureDetail = ""
		)
		{
			FinalState = finalState;
			FailureDetail = failureDetail ?? "";
		}
	}

	private sealed class MetadataWritePaths
	{
		internal string TargetResourcePath { get; }
		internal string TargetGlobalPath { get; }
		internal string StagingResourcePath { get; }
		internal string StagingGlobalPath { get; }
		internal string BackupResourcePath { get; }
		internal string BackupGlobalPath { get; }

		internal MetadataWritePaths(
			string targetResourcePath,
			string targetGlobalPath,
			string stagingResourcePath,
			string stagingGlobalPath,
			string backupResourcePath,
			string backupGlobalPath
		)
		{
			TargetResourcePath = targetResourcePath;
			TargetGlobalPath = targetGlobalPath;
			StagingResourcePath = stagingResourcePath;
			StagingGlobalPath = stagingGlobalPath;
			BackupResourcePath = backupResourcePath;
			BackupGlobalPath = backupGlobalPath;
		}
	}

	private MetadataWriteResult TryWriteAndVerifyTextFile(
		string path,
		string expectedContent,
		string displayName
	)
	{
		expectedContent ??= "";
		displayName = string.IsNullOrWhiteSpace(displayName)
			? "metadata file"
			: displayName.Trim();

		if (
			!TryCreateMetadataWritePaths(
				path,
				displayName,
				out MetadataWritePaths paths,
				out string pathFailureDetail
			)
		)
		{
			return new MetadataWriteResult(
				MetadataWriteFinalState.FinalTargetStateUnclear,
				pathFailureDetail
			);
		}

		if (
			!TryGetMetadataFileExistence(
				paths.TargetResourcePath,
				paths.TargetGlobalPath,
				out bool previousTargetExisted,
				out string targetExistenceFailure
			)
		)
		{
			return new MetadataWriteResult(
				MetadataWriteFinalState.FinalTargetStateUnclear,
				$"DisplayName='{displayName}', Phase=target-existence, {targetExistenceFailure}"
			);
		}

		string previousContent = "";

		if (
			previousTargetExisted
			&& !TryReadMetadataTextFile(
				paths.TargetResourcePath,
				displayName,
				"previous-target-read",
				out previousContent,
				out string previousReadFailure
			)
		)
		{
			return new MetadataWriteResult(
				MetadataWriteFinalState.FinalTargetStateUnclear,
				JoinMetadataFailureDetails(
					previousReadFailure,
					$"DisplayName='{displayName}', Phase=pre-commit-cancelled, TargetPath='{paths.TargetResourcePath}', Detail='The existing target was not modified, but its previous content could not be captured for verification.'"
				)
			);
		}

		if (
			!TryWriteAndVerifyMetadataStagingFile(
				paths,
				expectedContent,
				displayName,
				out string stagingFailureDetail
			)
		)
		{
			return CreatePreCommitMetadataWriteFailure(
				paths,
				displayName,
				previousTargetExisted,
				previousContent,
				stagingFailureDetail
			);
		}

		string commitFailureDetail = "";

		try
		{
			if (previousTargetExisted)
			{
				System.IO.File.Replace(
					paths.StagingGlobalPath,
					paths.TargetGlobalPath,
					paths.BackupGlobalPath
				);
			}
			else
			{
				System.IO.File.Move(paths.StagingGlobalPath, paths.TargetGlobalPath);
			}
		}
		catch (Exception exception)
		{
			commitFailureDetail =
				$"DisplayName='{displayName}', TargetPath='{paths.TargetResourcePath}', StagingPath='{paths.StagingResourcePath}', BackupPath='{paths.BackupResourcePath}', Phase=commit, Exception='{exception}'";
		}

		if (
			TryVerifyMetadataFileContent(
				paths.TargetResourcePath,
				paths.TargetGlobalPath,
				expectedContent,
				displayName,
				"final-target-verification",
				out string finalVerificationFailure
			)
		)
		{
			if (!string.IsNullOrWhiteSpace(commitFailureDetail))
			{
				DebugLogger.LogOperation(
					"Metadata commit reported an exception but final verification succeeded",
					commitFailureDetail
				);
			}

			CleanupMetadataArtifactBestEffort(
				paths.StagingResourcePath,
				paths.StagingGlobalPath,
				displayName,
				"staging"
			);
			CleanupMetadataArtifactBestEffort(
				paths.BackupResourcePath,
				paths.BackupGlobalPath,
				displayName,
				"backup"
			);

			return new MetadataWriteResult(MetadataWriteFinalState.Succeeded);
		}

		string failureDetail = JoinMetadataFailureDetails(
			commitFailureDetail,
			finalVerificationFailure
		);

		if (
			TryVerifyPreviousMetadataTargetState(
				paths,
				previousTargetExisted,
				previousContent,
				displayName,
				out string preservedVerificationDetail
			)
		)
		{
			CleanupMetadataArtifactBestEffort(
				paths.StagingResourcePath,
				paths.StagingGlobalPath,
				displayName,
				"staging"
			);
			CleanupMetadataArtifactBestEffort(
				paths.BackupResourcePath,
				paths.BackupGlobalPath,
				displayName,
				"backup"
			);

			return new MetadataWriteResult(
				MetadataWriteFinalState.PreviousTargetPreserved,
				JoinMetadataFailureDetails(failureDetail, preservedVerificationDetail)
			);
		}

		failureDetail = JoinMetadataFailureDetails(
			failureDetail,
			preservedVerificationDetail
		);

		if (previousTargetExisted)
		{
			if (
				TryRestorePreviousMetadataTarget(
					paths,
					previousContent,
					displayName,
					out string restoreDetail
				)
			)
			{
				CleanupMetadataArtifactBestEffort(
					paths.StagingResourcePath,
					paths.StagingGlobalPath,
					displayName,
					"staging"
				);
				CleanupMetadataArtifactBestEffort(
					paths.BackupResourcePath,
					paths.BackupGlobalPath,
					displayName,
					"backup"
				);

				return new MetadataWriteResult(
					MetadataWriteFinalState.PreviousTargetRestoredAndVerified,
					JoinMetadataFailureDetails(failureDetail, restoreDetail)
				);
			}

			failureDetail = JoinMetadataFailureDetails(failureDetail, restoreDetail);
		}
		else
		{
			if (
				TryRestoreMissingMetadataTargetState(
					paths,
					displayName,
					out string missingStateRestoreDetail
				)
			)
			{
				CleanupMetadataArtifactBestEffort(
					paths.StagingResourcePath,
					paths.StagingGlobalPath,
					displayName,
					"staging"
				);
				CleanupMetadataArtifactBestEffort(
					paths.BackupResourcePath,
					paths.BackupGlobalPath,
					displayName,
					"backup"
				);

				return new MetadataWriteResult(
					MetadataWriteFinalState.PreviousTargetPreserved,
					JoinMetadataFailureDetails(failureDetail, missingStateRestoreDetail)
				);
			}

			failureDetail = JoinMetadataFailureDetails(
				failureDetail,
				missingStateRestoreDetail
			);
		}

		CleanupMetadataArtifactBestEffort(
			paths.StagingResourcePath,
			paths.StagingGlobalPath,
			displayName,
			"staging"
		);

		failureDetail = JoinMetadataFailureDetails(
			failureDetail,
			$"DisplayName='{displayName}', Phase=unclear-final-state, TargetPath='{paths.TargetResourcePath}', BackupPath='{paths.BackupResourcePath}', BackupRetained='{System.IO.File.Exists(paths.BackupGlobalPath)}'"
		);

		return new MetadataWriteResult(
			MetadataWriteFinalState.FinalTargetStateUnclear,
			failureDetail
		);
	}

	private bool TryCreateMetadataWritePaths(
		string targetResourcePath,
		string displayName,
		out MetadataWritePaths paths,
		out string failureDetail
	)
	{
		paths = null;

		if (string.IsNullOrWhiteSpace(targetResourcePath))
		{
			failureDetail =
				$"DisplayName='{displayName}', TargetPath='', Phase=path-preparation, Detail='Target path was empty.'";
			return false;
		}

		string targetGlobalPath;

		try
		{
			targetGlobalPath = System.IO.Path.GetFullPath(
				ProjectSettings.GlobalizePath(targetResourcePath)
			);
		}
		catch (Exception exception)
		{
			failureDetail =
				$"DisplayName='{displayName}', TargetPath='{targetResourcePath}', Phase=path-globalize, Exception='{exception}'";
			return false;
		}

		string targetDirectory = System.IO.Path.GetDirectoryName(targetGlobalPath);

		if (string.IsNullOrWhiteSpace(targetDirectory))
		{
			failureDetail =
				$"DisplayName='{displayName}', TargetPath='{targetResourcePath}', GlobalTargetPath='{targetGlobalPath}', Phase=path-preparation, Detail='The target directory could not be resolved.'";
			return false;
		}

		for (int attempt = 0; attempt < 16; attempt++)
		{
			string uniqueId = Guid.NewGuid().ToString("N");
			string stagingResourcePath = $"{targetResourcePath}.{uniqueId}.tmp";
			string backupResourcePath = $"{targetResourcePath}.{uniqueId}.bak";
			string stagingGlobalPath;
			string backupGlobalPath;

			try
			{
				stagingGlobalPath = System.IO.Path.GetFullPath(
					ProjectSettings.GlobalizePath(stagingResourcePath)
				);
				backupGlobalPath = System.IO.Path.GetFullPath(
					ProjectSettings.GlobalizePath(backupResourcePath)
				);
			}
			catch (Exception exception)
			{
				failureDetail =
					$"DisplayName='{displayName}', TargetPath='{targetResourcePath}', Phase=sibling-path-globalize, Exception='{exception}'";
				return false;
			}

			string stagingDirectory = System.IO.Path.GetDirectoryName(stagingGlobalPath);
			string backupDirectory = System.IO.Path.GetDirectoryName(backupGlobalPath);

			if (
				!string.Equals(targetDirectory, stagingDirectory, StringComparison.OrdinalIgnoreCase)
				|| !string.Equals(targetDirectory, backupDirectory, StringComparison.OrdinalIgnoreCase)
			)
			{
				failureDetail =
					$"DisplayName='{displayName}', TargetPath='{targetResourcePath}', StagingPath='{stagingResourcePath}', BackupPath='{backupResourcePath}', Phase=sibling-path-validation, Detail='Staging and backup paths were not in the target directory.'";
				return false;
			}

			if (
				string.Equals(targetGlobalPath, stagingGlobalPath, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(targetGlobalPath, backupGlobalPath, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(stagingGlobalPath, backupGlobalPath, StringComparison.OrdinalIgnoreCase)
			)
			{
				continue;
			}

			if (
				!TryGetMetadataFileExistence(
					stagingResourcePath,
					stagingGlobalPath,
					out bool stagingExists,
					out string stagingExistenceFailure
				)
			)
			{
				failureDetail =
					$"DisplayName='{displayName}', Phase=staging-collision-check, {stagingExistenceFailure}";
				return false;
			}

			if (
				!TryGetMetadataFileExistence(
					backupResourcePath,
					backupGlobalPath,
					out bool backupExists,
					out string backupExistenceFailure
				)
			)
			{
				failureDetail =
					$"DisplayName='{displayName}', Phase=backup-collision-check, {backupExistenceFailure}";
				return false;
			}

			if (stagingExists || backupExists)
				continue;

			paths = new MetadataWritePaths(
				targetResourcePath,
				targetGlobalPath,
				stagingResourcePath,
				stagingGlobalPath,
				backupResourcePath,
				backupGlobalPath
			);
			failureDetail = "";
			return true;
		}

		failureDetail =
			$"DisplayName='{displayName}', TargetPath='{targetResourcePath}', Phase=sibling-path-collision, Detail='Could not allocate unique staging and backup paths after 16 attempts.'";
		return false;
	}

	private bool TryWriteAndVerifyMetadataStagingFile(
		MetadataWritePaths paths,
		string expectedContent,
		string displayName,
		out string failureDetail
	)
	{
		FileAccess stagingFile;

		try
		{
			stagingFile = FileAccess.Open(
				paths.StagingResourcePath,
				FileAccess.ModeFlags.Write
			);
		}
		catch (Exception exception)
		{
			failureDetail =
				$"DisplayName='{displayName}', StagingPath='{paths.StagingResourcePath}', Phase=staging-open, Exception='{exception}'";
			return false;
		}

		if (stagingFile == null)
		{
			Error openError = FileAccess.GetOpenError();
			failureDetail =
				$"DisplayName='{displayName}', StagingPath='{paths.StagingResourcePath}', Phase=staging-open, Detail='FileAccess.Open returned null.', Error='{openError}'";
			return false;
		}

		try
		{
			using (stagingFile)
			{
				bool stored = stagingFile.StoreString(expectedContent);
				stagingFile.Flush();
				Error writeError = stagingFile.GetError();

				if (!stored || writeError != Error.Ok)
				{
					failureDetail =
						$"DisplayName='{displayName}', StagingPath='{paths.StagingResourcePath}', Phase=staging-write, StoreSucceeded='{stored}', Error='{writeError}'";
					return false;
				}
			}
		}
		catch (Exception exception)
		{
			failureDetail =
				$"DisplayName='{displayName}', StagingPath='{paths.StagingResourcePath}', Phase=staging-write-close, Exception='{exception}'";
			return false;
		}

		return TryVerifyMetadataFileContent(
			paths.StagingResourcePath,
			paths.StagingGlobalPath,
			expectedContent,
			displayName,
			"staging-verification",
			out failureDetail
		);
	}

	private bool TryReadMetadataTextFile(
		string resourcePath,
		string displayName,
		string phase,
		out string content,
		out string failureDetail
	)
	{
		content = "";
		FileAccess file;

		try
		{
			file = FileAccess.Open(resourcePath, FileAccess.ModeFlags.Read);
		}
		catch (Exception exception)
		{
			failureDetail =
				$"DisplayName='{displayName}', Path='{resourcePath}', Phase={phase}-open, Exception='{exception}'";
			return false;
		}

		if (file == null)
		{
			Error openError = FileAccess.GetOpenError();
			failureDetail =
				$"DisplayName='{displayName}', Path='{resourcePath}', Phase={phase}-open, Detail='FileAccess.Open returned null.', Error='{openError}'";
			return false;
		}

		try
		{
			using (file)
			{
				var fileLength = file.GetLength();
				Error lengthError = file.GetError();

				if (lengthError != Error.Ok)
				{
					failureDetail =
						$"DisplayName='{displayName}', Path='{resourcePath}', Phase={phase}-length, Error='{lengthError}'";
					return false;
				}

				content = file.GetAsText();
				Error readError = file.GetError();

				if (
					readError != Error.Ok
					|| (fileLength > 0 && string.IsNullOrEmpty(content))
				)
				{
					failureDetail =
						$"DisplayName='{displayName}', Path='{resourcePath}', Phase={phase}-read, Error='{readError}', ExpectedBytes='{fileLength}', ActualChars='{content?.Length ?? 0}'";
					return false;
				}
			}
		}
		catch (Exception exception)
		{
			failureDetail =
				$"DisplayName='{displayName}', Path='{resourcePath}', Phase={phase}-read-close, Exception='{exception}'";
			return false;
		}

		failureDetail = "";
		return true;
	}

	private bool TryVerifyMetadataFileContent(
		string resourcePath,
		string globalPath,
		string expectedContent,
		string displayName,
		string phase,
		out string failureDetail
	)
	{
		if (
			!TryGetMetadataFileExistence(
				resourcePath,
				globalPath,
				out bool exists,
				out string existenceFailure
			)
		)
		{
			failureDetail =
				$"DisplayName='{displayName}', Path='{resourcePath}', Phase={phase}-existence, {existenceFailure}";
			return false;
		}

		if (!exists)
		{
			failureDetail =
				$"DisplayName='{displayName}', Path='{resourcePath}', Phase={phase}-existence, Detail='The file did not exist.'";
			return false;
		}

		if (
			!TryReadMetadataTextFile(
				resourcePath,
				displayName,
				phase,
				out string actualContent,
				out failureDetail
			)
		)
		{
			return false;
		}

		if (!string.Equals(actualContent, expectedContent, StringComparison.Ordinal))
		{
			failureDetail =
				$"DisplayName='{displayName}', Path='{resourcePath}', Phase={phase}-content-mismatch, ExpectedChars='{expectedContent?.Length ?? 0}', ActualChars='{actualContent?.Length ?? 0}'";
			return false;
		}

		failureDetail = "";
		return true;
	}

	private bool TryGetMetadataFileExistence(
		string resourcePath,
		string globalPath,
		out bool exists,
		out string failureDetail
	)
	{
		exists = false;
		bool resourceExists;
		bool globalExists;

		try
		{
			if (System.IO.Directory.Exists(globalPath))
			{
				failureDetail =
					$"Path='{resourcePath}', GlobalPath='{globalPath}', Detail='The requested file path was occupied by a directory.'";
				return false;
			}

			resourceExists = FileAccess.FileExists(resourcePath);
			globalExists = System.IO.File.Exists(globalPath);
		}
		catch (Exception exception)
		{
			failureDetail =
				$"Path='{resourcePath}', GlobalPath='{globalPath}', Detail='Existence check threw.', Exception='{exception}'";
			return false;
		}

		if (resourceExists != globalExists)
		{
			failureDetail =
				$"Path='{resourcePath}', GlobalPath='{globalPath}', Detail='Godot and System.IO disagreed about file existence.', GodotExists='{resourceExists}', SystemIOExists='{globalExists}'";
			return false;
		}

		exists = resourceExists;
		failureDetail = "";
		return true;
	}

	private MetadataWriteResult CreatePreCommitMetadataWriteFailure(
		MetadataWritePaths paths,
		string displayName,
		bool previousTargetExisted,
		string previousContent,
		string failureDetail
	)
	{
		CleanupMetadataArtifactBestEffort(
			paths.StagingResourcePath,
			paths.StagingGlobalPath,
			displayName,
			"staging"
		);
		CleanupMetadataArtifactBestEffort(
			paths.BackupResourcePath,
			paths.BackupGlobalPath,
			displayName,
			"backup"
		);

		if (
			TryVerifyPreviousMetadataTargetState(
				paths,
				previousTargetExisted,
				previousContent,
				displayName,
				out string previousStateVerificationDetail
			)
		)
		{
			return new MetadataWriteResult(
				MetadataWriteFinalState.PreviousTargetPreserved,
				JoinMetadataFailureDetails(failureDetail, previousStateVerificationDetail)
			);
		}

		return new MetadataWriteResult(
			MetadataWriteFinalState.FinalTargetStateUnclear,
			JoinMetadataFailureDetails(failureDetail, previousStateVerificationDetail)
		);
	}

	private bool TryVerifyPreviousMetadataTargetState(
		MetadataWritePaths paths,
		bool previousTargetExisted,
		string previousContent,
		string displayName,
		out string verificationDetail
	)
	{
		if (previousTargetExisted)
		{
			bool preserved = TryVerifyMetadataFileContent(
				paths.TargetResourcePath,
				paths.TargetGlobalPath,
				previousContent,
				displayName,
				"previous-target-preservation-verification",
				out string preservedFailure
			);
			verificationDetail = preserved
				? $"DisplayName='{displayName}', Phase=previous-target-preserved, TargetPath='{paths.TargetResourcePath}'"
				: preservedFailure;
			return preserved;
		}

		if (
			!TryGetMetadataFileExistence(
				paths.TargetResourcePath,
				paths.TargetGlobalPath,
				out bool targetExists,
				out string existenceFailure
			)
		)
		{
			verificationDetail =
				$"DisplayName='{displayName}', Phase=previous-missing-state-verification, {existenceFailure}";
			return false;
		}

		verificationDetail = targetExists
			? $"DisplayName='{displayName}', Phase=previous-missing-state-verification, TargetPath='{paths.TargetResourcePath}', Detail='A target file existed even though none existed before the save.'"
			: $"DisplayName='{displayName}', Phase=previous-missing-state-preserved, TargetPath='{paths.TargetResourcePath}'";
		return !targetExists;
	}

	private bool TryRestorePreviousMetadataTarget(
		MetadataWritePaths paths,
		string previousContent,
		string displayName,
		out string restoreDetail
	)
	{
		if (
			!TryVerifyMetadataFileContent(
				paths.BackupResourcePath,
				paths.BackupGlobalPath,
				previousContent,
				displayName,
				"backup-verification",
				out string backupVerificationFailure
			)
		)
		{
			restoreDetail = JoinMetadataFailureDetails(
				backupVerificationFailure,
				$"DisplayName='{displayName}', Phase=restore-skipped, BackupPath='{paths.BackupResourcePath}', Detail='The backup could not be verified against the previous target content.'"
			);
			return false;
		}

		if (
			!TryGetMetadataFileExistence(
				paths.TargetResourcePath,
				paths.TargetGlobalPath,
				out bool targetExists,
				out string targetExistenceFailure
			)
		)
		{
			restoreDetail =
				$"DisplayName='{displayName}', Phase=restore-target-existence, {targetExistenceFailure}";
			return false;
		}

		string restoreExceptionDetail = "";

		try
		{
			if (targetExists)
			{
				System.IO.File.Replace(
					paths.BackupGlobalPath,
					paths.TargetGlobalPath,
					null
				);
			}
			else
			{
				System.IO.File.Move(paths.BackupGlobalPath, paths.TargetGlobalPath);
			}
		}
		catch (Exception exception)
		{
			restoreExceptionDetail =
				$"DisplayName='{displayName}', TargetPath='{paths.TargetResourcePath}', BackupPath='{paths.BackupResourcePath}', Phase=restore-commit, Exception='{exception}'";
		}

		bool restored = TryVerifyMetadataFileContent(
			paths.TargetResourcePath,
			paths.TargetGlobalPath,
			previousContent,
			displayName,
			"restored-target-verification",
			out string restoredVerificationFailure
		);

		restoreDetail = restored
			? JoinMetadataFailureDetails(
				restoreExceptionDetail,
				$"DisplayName='{displayName}', Phase=previous-target-restored-and-verified, TargetPath='{paths.TargetResourcePath}'"
			)
			: JoinMetadataFailureDetails(
				restoreExceptionDetail,
				restoredVerificationFailure
			);
		return restored;
	}

	private bool TryRestoreMissingMetadataTargetState(
		MetadataWritePaths paths,
		string displayName,
		out string restoreDetail
	)
	{
		if (
			!TryGetMetadataFileExistence(
				paths.TargetResourcePath,
				paths.TargetGlobalPath,
				out bool targetExists,
				out string existenceFailure
			)
		)
		{
			restoreDetail =
				$"DisplayName='{displayName}', Phase=missing-state-restore-existence, {existenceFailure}";
			return false;
		}

		if (!targetExists)
		{
			restoreDetail =
				$"DisplayName='{displayName}', Phase=previous-missing-state-preserved, TargetPath='{paths.TargetResourcePath}'";
			return true;
		}

		string deleteExceptionDetail = "";

		try
		{
			System.IO.File.Delete(paths.TargetGlobalPath);
		}
		catch (Exception exception)
		{
			deleteExceptionDetail =
				$"DisplayName='{displayName}', TargetPath='{paths.TargetResourcePath}', Phase=missing-state-restore-delete, Exception='{exception}'";
		}

		if (
			!TryGetMetadataFileExistence(
				paths.TargetResourcePath,
				paths.TargetGlobalPath,
				out bool targetStillExists,
				out string verificationFailure
			)
		)
		{
			restoreDetail = JoinMetadataFailureDetails(
				deleteExceptionDetail,
				$"DisplayName='{displayName}', Phase=missing-state-restore-verification, {verificationFailure}"
			);
			return false;
		}

		restoreDetail = targetStillExists
			? JoinMetadataFailureDetails(
				deleteExceptionDetail,
				$"DisplayName='{displayName}', Phase=missing-state-restore-verification, TargetPath='{paths.TargetResourcePath}', Detail='The newly created target could not be removed.'"
			)
			: JoinMetadataFailureDetails(
				deleteExceptionDetail,
				$"DisplayName='{displayName}', Phase=previous-missing-state-restored, TargetPath='{paths.TargetResourcePath}'"
			);
		return !targetStillExists;
	}

	private void CleanupMetadataArtifactBestEffort(
		string resourcePath,
		string globalPath,
		string displayName,
		string artifactKind
	)
	{
		try
		{
			if (System.IO.File.Exists(globalPath))
				System.IO.File.Delete(globalPath);

			if (System.IO.Directory.Exists(globalPath))
			{
				DebugLogger.LogOperation(
					"Metadata temporary artifact cleanup skipped: path was a directory",
					$"DisplayName='{displayName}', Artifact='{artifactKind}', Path='{resourcePath}', GlobalPath='{globalPath}'"
				);
				return;
			}

			if (System.IO.File.Exists(globalPath))
			{
				DebugLogger.LogOperation(
					"Metadata temporary artifact cleanup incomplete",
					$"DisplayName='{displayName}', Artifact='{artifactKind}', Path='{resourcePath}', GlobalPath='{globalPath}', Detail='The file still existed after delete.'"
				);
			}
		}
		catch (Exception exception)
		{
			DebugLogger.LogOperation(
				"Metadata temporary artifact cleanup failed",
				$"DisplayName='{displayName}', Artifact='{artifactKind}', Path='{resourcePath}', GlobalPath='{globalPath}', Exception='{exception}'"
			);
		}
	}

	private static string JoinMetadataFailureDetails(params string[] details)
	{
		return string.Join(
			" | ",
			(details ?? Array.Empty<string>())
				.Where(detail => !string.IsNullOrWhiteSpace(detail))
		);
	}

	private void PushMetadataWriteFailureWarning(
		string displayName,
		MetadataWriteResult result
	)
	{
		displayName = string.IsNullOrWhiteSpace(displayName)
			? "metadata file"
			: displayName.Trim();

		string warning = result.FinalState switch
		{
			MetadataWriteFinalState.PreviousTargetPreserved =>
				$"System Explorer could not save {displayName}, but the previous metadata state was preserved.",
			MetadataWriteFinalState.PreviousTargetRestoredAndVerified =>
				$"System Explorer could not save {displayName}, but the previous metadata file was restored and verified.",
			_ =>
				$"System Explorer could not safely complete the metadata save, and the final state of {displayName} could not be verified. Restart Godot and inspect the metadata file before continuing.",
		};

		GD.PushWarning(warning);
	}

	private bool SaveSystems()
	{
		return SaveSystemsCore(null);
	}

	private bool SaveSystems(IntentionalEmptySystemsSaveAuthorization authorization)
	{
		return SaveSystemsCore(authorization);
	}

	private bool SaveSystemsCore(IntentionalEmptySystemsSaveAuthorization authorization)
	{
		DebugLogger.LogOperation(
			"Save Systems Requested",
			authorization == null
				? $"{_systems.Count} systems"
				: $"{_systems.Count} systems, intentional empty authorization for '{authorization.SystemName}'"
		);

		SystemsFileReadResult diskReadResult = ReadSystemsFileFromDisk();

		if (
			WouldOverwriteExistingDataWithEmptySystems(
				diskReadResult,
				authorization,
				out string emptySaveBlockReason
			)
		)
		{
			string warning;

			if (authorization != null)
			{
				warning =
					"System Explorer blocked the intentional empty systems save because the verified systems.json state was no longer safe to overwrite.";
			}
			else if (diskReadResult.Status == SystemsFileReadStatus.ValidNonEmpty)
			{
				warning =
					"System Explorer blocked saving an empty systems file because existing data was found on disk.";
			}
			else
			{
				warning =
					"System Explorer blocked saving an empty systems file because systems.json could not be safely read or verified.";
			}

			GD.PushWarning(warning);
			DebugLogger.LogOperation("Save Systems blocked: suspicious empty state", emptySaveBlockReason);
			DebugLogStateSnapshot("Blocked Save");
			return false;
		}

		if (WouldOverwriteExistingDataWithUnrelatedSystems(diskReadResult))
		{
			GD.PushWarning(
				"System Explorer blocked saving because the in-memory systems do not match the existing systems file. Restart Godot to avoid data loss."
			);
			DebugLogger.Log(
				"Save Systems blocked: in-memory systems appear unrelated to existing disk data."
			);
			DebugLogStateSnapshot("Blocked Save");
			return false;
		}

		if (!EnsureResourcesFolderExists())
			return false;

		NormalizeAllSystemEntries();

		string json = SerializeSystems();

		MetadataWriteResult writeResult = TryWriteAndVerifyTextFile(
			SavePath,
			json,
			"systems.json"
		);

		if (!writeResult.Succeeded)
		{
			PushMetadataWriteFailureWarning("systems.json", writeResult);
			DebugLogger.LogOperation(
				"Save Systems failed: staged metadata write failed",
				$"FinalState='{writeResult.FinalState}', {writeResult.FailureDetail}"
			);
			return false;
		}

		DebugLogger.LogOperation(
			"Save Systems Completed",
			$"{_systems.Count} systems, {json.Length} chars"
		);

		return true;
	}

	private bool WouldOverwriteExistingDataWithEmptySystems(
		SystemsFileReadResult diskReadResult,
		IntentionalEmptySystemsSaveAuthorization authorization,
		out string blockReason
	)
	{
		blockReason = "";

		if (_systems.Count > 0)
		{
			if (authorization != null)
			{
				blockReason =
					"An intentional empty-save authorization was supplied while systems still remained in memory.";
				return true;
			}

			return false;
		}

		if (authorization != null)
		{
			if (
				IsIntentionalEmptySystemsSaveAuthorized(
					authorization,
					diskReadResult,
					out string authorizationFailure
				)
			)
			{
				return false;
			}

			blockReason = authorizationFailure;
			return true;
		}

		if (
			diskReadResult.Status == SystemsFileReadStatus.Missing
			|| diskReadResult.Status == SystemsFileReadStatus.ValidEmpty
		)
		{
			return false;
		}

		if (diskReadResult.Status == SystemsFileReadStatus.ValidNonEmpty)
		{
			blockReason = "No verified intentional empty-save authorization was supplied.";
			return true;
		}

		blockReason =
			$"systems.json could not be safely verified ({diskReadResult.Status}): {diskReadResult.FailureDetail}";
		return true;
	}

	private bool WouldOverwriteExistingDataWithUnrelatedSystems(
		SystemsFileReadResult diskReadResult
	)
	{
		if (
			!diskReadResult.IsValid
			|| diskReadResult.Systems.Count <= 1
			|| _systems.Count == 0
		)
		{
			return false;
		}

		return !_systems.Keys.Any(diskReadResult.Systems.ContainsKey);
	}

	private bool TryCreateIntentionalEmptySystemsSaveAuthorization(
		string systemName,
		out IntentionalEmptySystemsSaveAuthorization authorization,
		out string failureMessage
	)
	{
		authorization = null;
		failureMessage =
			"System Explorer could not verify the current system state against systems.json, so removing the last system was cancelled before any files or metadata were changed.";

		if (string.IsNullOrWhiteSpace(systemName))
		{
			DebugLogger.Log("Intentional empty save authorization failed: empty system name.");
			return false;
		}

		if (_systems.Count != 1 || !_systems.ContainsKey(systemName))
		{
			DebugLogger.LogOperation(
				"Intentional empty save authorization failed: in-memory state was not the expected single system",
				$"System='{systemName}', Count={_systems.Count}"
			);
			return false;
		}

		SystemsFileReadResult diskReadResult = ReadSystemsFileFromDisk();

		if (diskReadResult.Status != SystemsFileReadStatus.ValidNonEmpty)
		{
			DebugLogger.LogOperation(
				"Intentional empty save authorization failed: disk data was not valid and non-empty",
				$"Status={diskReadResult.Status}, Detail='{diskReadResult.FailureDetail}'"
			);
			return false;
		}

		Dictionary<string, List<string>> inMemorySnapshot = CreateNormalizedSystemsSnapshot(
			_systems
		);
		Dictionary<string, List<string>> diskSnapshot = CreateNormalizedSystemsSnapshot(
			diskReadResult.Systems
		);

		if (!SystemsSemanticallyMatch(inMemorySnapshot, diskSnapshot))
		{
			DebugLogger.LogOperation(
				"Intentional empty save authorization failed: disk and in-memory systems differed",
				systemName
			);
			return false;
		}

		if (diskSnapshot.Count != 1 || !diskSnapshot.ContainsKey(systemName))
		{
			DebugLogger.LogOperation(
				"Intentional empty save authorization failed: disk did not contain the exact final system",
				$"System='{systemName}', DiskCount={diskSnapshot.Count}"
			);
			return false;
		}

		authorization = new IntentionalEmptySystemsSaveAuthorization(
			systemName,
			diskSnapshot.Count,
			CreateCanonicalSystemsRepresentation(diskSnapshot)
		);

		DebugLogger.LogOperation(
			"Intentional empty save authorization created",
			systemName
		);
		return true;
	}

	private bool IsIntentionalEmptySystemsSaveAuthorized(
		IntentionalEmptySystemsSaveAuthorization authorization,
		SystemsFileReadResult diskReadResult,
		out string failureReason
	)
	{
		failureReason = "No verified intentional empty-save authorization was supplied.";

		if (authorization == null)
			return false;

		if (
			string.IsNullOrWhiteSpace(authorization.SystemName)
			|| authorization.VerifiedSystemCount != 1
			|| string.IsNullOrWhiteSpace(authorization.VerifiedDiskState)
		)
		{
			failureReason = "The intentional empty-save authorization was incomplete or invalid.";
			return false;
		}

		if (diskReadResult.Status != SystemsFileReadStatus.ValidNonEmpty)
		{
			failureReason =
				$"systems.json was no longer valid and non-empty ({diskReadResult.Status}).";
			return false;
		}

		Dictionary<string, List<string>> currentDiskSnapshot = CreateNormalizedSystemsSnapshot(
			diskReadResult.Systems
		);

		if (
			currentDiskSnapshot.Count != 1
			|| !currentDiskSnapshot.ContainsKey(authorization.SystemName)
		)
		{
			failureReason =
				"systems.json no longer contained exactly the system covered by the authorization.";
			return false;
		}

		string currentDiskState = CreateCanonicalSystemsRepresentation(currentDiskSnapshot);

		if (
			!string.Equals(
				currentDiskState,
				authorization.VerifiedDiskState,
				StringComparison.Ordinal
			)
		)
		{
			failureReason =
				"systems.json changed after the last-system removal was verified.";
			return false;
		}

		return true;
	}

	private static Dictionary<string, List<string>> CreateNormalizedSystemsSnapshot(
		Dictionary<string, List<string>> systems
	)
	{
		Dictionary<string, List<string>> snapshot = new();

		foreach (KeyValuePair<string, List<string>> system in systems)
		{
			List<string> copiedEntries = system.Value == null
				? new List<string>()
				: system.Value.Select(entry => entry ?? "").ToList();
			snapshot[system.Key] = NormalizeSystemEntries(copiedEntries);
		}

		return snapshot;
	}

	private static bool SystemsSemanticallyMatch(
		Dictionary<string, List<string>> first,
		Dictionary<string, List<string>> second
	)
	{
		if (first.Count != second.Count)
			return false;

		foreach (KeyValuePair<string, List<string>> system in first)
		{
			if (!second.TryGetValue(system.Key, out List<string> secondEntries))
				return false;

			if (!system.Value.SequenceEqual(secondEntries, StringComparer.Ordinal))
				return false;
		}

		return true;
	}

	private static string CreateCanonicalSystemsRepresentation(
		Dictionary<string, List<string>> systems
	)
	{
		SortedDictionary<string, List<string>> orderedSystems = new(StringComparer.Ordinal);

		foreach (
			KeyValuePair<string, List<string>> system in systems.OrderBy(
				system => system.Key,
				StringComparer.Ordinal
			)
		)
		{
			orderedSystems[system.Key] = new List<string>(system.Value);
		}

		return JsonSerializer.Serialize(orderedSystems);
	}

	private SystemsFileReadResult ReadSystemsFileFromDisk()
	{
		bool fileExists;

		try
		{
			fileExists = FileAccess.FileExists(SavePath);
		}
		catch (Exception exception)
		{
			return new SystemsFileReadResult(
				SystemsFileReadStatus.OpenFailed,
				failureDetail: $"Path='{SavePath}', Phase=existence, Exception='{exception}'"
			);
		}

		if (!fileExists)
			return new SystemsFileReadResult(SystemsFileReadStatus.Missing);

		if (
			!TryReadMetadataTextFile(
				SavePath,
				"systems.json",
				"systems-disk-read",
				out string existingJson,
				out string readFailureDetail
			)
		)
		{
			return new SystemsFileReadResult(
				SystemsFileReadStatus.OpenFailed,
				failureDetail: readFailureDetail
			);
		}

		if (string.IsNullOrWhiteSpace(existingJson))
		{
			return new SystemsFileReadResult(
				SystemsFileReadStatus.InvalidJson,
				failureDetail: "The file was blank."
			);
		}

		try
		{
			Dictionary<string, List<string>> systems = DeserializeSystems(existingJson);
			SystemsFileReadStatus status = systems.Count == 0
				? SystemsFileReadStatus.ValidEmpty
				: SystemsFileReadStatus.ValidNonEmpty;
			return new SystemsFileReadResult(status, systems);
		}
		catch (Exception exception)
		{
			return new SystemsFileReadResult(
				SystemsFileReadStatus.InvalidJson,
				failureDetail: exception.Message
			);
		}
	}

	private bool EnsureSystemsLoadedForTreeOperation(string reason)
	{
		if (_systems.Count > 0)
			return true;

		if (!FileAccess.FileExists(SavePath))
			return false;

		DebugLogger.LogOperation(
			"Tree Operation Recovery Guard",
			$"Reason='{reason}', In-memory systems were empty before a tree operation."
		);

		bool recovered = TryRecoverSystemsFromDisk(reason);

		if (!recovered)
		{
			GD.PushWarning(
				$"System Explorer could not complete '{reason}' because the in-memory system list was empty and recovery from disk failed."
			);
		}

		return recovered;
	}

	private bool EnsureSystemAvailable(string systemName, string reason)
	{
		if (string.IsNullOrWhiteSpace(systemName))
			return false;

		if (_systems.ContainsKey(systemName))
			return true;

		DebugLogger.LogOperation(
			"Recovery Guard: missing system",
			$"Reason='{reason}', System='{systemName}'"
		);

		if (TryRecoverSystemsFromDisk(reason, systemName) && _systems.ContainsKey(systemName))
		{
			DebugLogger.LogOperation("Recovery Guard: recovered system", systemName);
			return true;
		}

		GD.PushWarning(
			$"System Explorer could not find system '{systemName}' for '{reason}'. The tree will be rebuilt from the current in-memory state."
		);
		DebugLogger.LogOperation(
			"Recovery Guard failed: system still missing",
			$"Reason='{reason}', System='{systemName}'"
		);
		DebugLogStateSnapshot("Recovery Failed");
		BuildTree();
		return false;
	}

	private bool EnsureSystemsAvailable(IEnumerable<string> systemNames, string reason)
	{
		List<string> names = systemNames
			.Where(name => !string.IsNullOrWhiteSpace(name))
			.Distinct()
			.ToList();

		if (names.Count == 0)
			return false;

		List<string> missingNames = names.Where(name => !_systems.ContainsKey(name)).ToList();

		if (missingNames.Count == 0)
			return true;

		DebugLogger.LogOperation(
			"Recovery Guard: missing systems",
			$"Reason='{reason}', Systems='{string.Join(", ", missingNames)}'"
		);

		if (TryRecoverSystemsFromDisk(reason))
		{
			missingNames = names.Where(name => !_systems.ContainsKey(name)).ToList();

			if (missingNames.Count == 0)
			{
				DebugLogger.LogOperation("Recovery Guard: recovered systems", string.Join(", ", names));
				return true;
			}
		}

		GD.PushWarning(
			$"System Explorer could not find required system(s) for '{reason}': {string.Join(", ", missingNames)}. The tree will be rebuilt from the current in-memory state."
		);
		DebugLogger.LogOperation(
			"Recovery Guard failed: systems still missing",
			$"Reason='{reason}', Systems='{string.Join(", ", missingNames)}'"
		);
		DebugLogStateSnapshot("Recovery Failed");
		BuildTree();
		return false;
	}

	private bool TryRecoverSystemsFromDisk(string reason, string requiredSystemName = "")
	{
		DebugLogger.LogOperation(
			"Recovery From Disk Requested",
			$"Reason='{reason}', Required='{requiredSystemName}'"
		);

		SystemsFileReadResult diskReadResult = ReadSystemsFileFromDisk();

		if (!diskReadResult.IsValid)
		{
			DebugLogger.LogOperation(
				"Recovery From Disk failed: systems file was unusable",
				$"Status={diskReadResult.Status}, Detail='{diskReadResult.FailureDetail}'"
			);

			if (DebugState)
				GD.PushError(
					$"[SystemExplorer] Recovery failed. Reason='{reason}', Required='{requiredSystemName}', Status='{diskReadResult.Status}'."
				);

			return false;
		}

		Dictionary<string, List<string>> recoveredSystems = diskReadResult.Systems;

		if (
			!string.IsNullOrWhiteSpace(requiredSystemName)
			&& !recoveredSystems.ContainsKey(requiredSystemName)
		)
		{
			DebugLogger.LogOperation(
				"Recovery From Disk failed: required system missing on disk",
				requiredSystemName
			);

			if (DebugState)
				GD.PushError(
					$"[SystemExplorer] Recovery failed. Required system '{requiredSystemName}' was not found on disk. Reason='{reason}'."
				);

			return false;
		}

		_systems.Clear();

		foreach (KeyValuePair<string, List<string>> system in recoveredSystems)
			_systems[system.Key] = system.Value ?? new List<string>();

		NormalizeAllSystemEntries();

		if (_systems.Count == 0)
		{
			DebugLogger.LogOperation(
				"Recovery From Disk Completed",
				"Recovered a valid empty systems state."
			);
		}
		else
		{
			DebugLogger.LogOperation("Recovery From Disk Completed", $"{_systems.Count} systems");
		}

		if (DebugState)
		{
			GD.PushWarning(
				$"[SystemExplorer] Recovery successful. Reason='{reason}', Required='{requiredSystemName}', Recovered Systems={_systems.Count}"
			);
		}

		DebugLogStateSnapshot("Recovered From Disk");

		return true;
	}

	private string SerializeSystems()
	{
		Dictionary<string, List<object>> serializedSystems = new();

		foreach (KeyValuePair<string, List<string>> system in _systems)
		{
			List<object> serializedEntries = new();

			foreach (string entry in system.Value)
			{
				if (entry.StartsWith("folder::") || IsSystemLockEntry(entry))
				{
					serializedEntries.Add(entry);
					continue;
				}

				bool isSceneEntry = IsSceneEntry(entry);
				string path = isSceneEntry
					? GetScenePathFromEntry(entry)
					: GetScriptPathFromEntry(entry);
				string folderPath = GetFolderPathFromEntry(entry);
				string linkedScenePath = GetLinkedScenePathFromEntry(entry);

				Dictionary<string, object> serializedEntry = new()
				{
					["name"] = path.GetFile(),
					["path"] = path,
				};

				if (isSceneEntry)
					serializedEntry["type"] = "scene";

				if (!string.IsNullOrWhiteSpace(folderPath))
					serializedEntry["folderPath"] = folderPath;

				if (!string.IsNullOrWhiteSpace(linkedScenePath))
					serializedEntry["linkedScenePath"] = linkedScenePath;

				if (IsEntryLocked(entry))
					serializedEntry["locked"] = true;

				serializedEntries.Add(serializedEntry);
			}

			serializedSystems[system.Key] = serializedEntries;
		}

		return JsonSerializer.Serialize(
			serializedSystems,
			new JsonSerializerOptions { WriteIndented = true }
		);
	}

	private Dictionary<string, List<string>> DeserializeSystems(string json)
	{
		Dictionary<string, List<string>> systems = new();

		using JsonDocument document = JsonDocument.Parse(json);

		if (document.RootElement.ValueKind != JsonValueKind.Object)
			throw new JsonException("System data root must be a JSON object.");

		foreach (JsonProperty systemProperty in document.RootElement.EnumerateObject())
		{
			List<string> entries = new();

			if (systemProperty.Value.ValueKind != JsonValueKind.Array)
			{
				systems[systemProperty.Name] = entries;
				continue;
			}

			foreach (JsonElement entryElement in systemProperty.Value.EnumerateArray())
			{
				if (entryElement.ValueKind == JsonValueKind.String)
				{
					string entry = entryElement.GetString() ?? "";

					if (!string.IsNullOrWhiteSpace(entry))
						entries.Add(entry);
					continue;
				}

				if (entryElement.ValueKind != JsonValueKind.Object)
					continue;

				string folderPath = GetJsonString(entryElement, "folderPath");
				string path = GetJsonString(entryElement, "path");
				string linkedScenePath = GetJsonString(entryElement, "linkedScenePath");
				string entryType = GetJsonString(entryElement, "type");
				bool locked = GetJsonBool(entryElement, "locked");

				if (string.IsNullOrWhiteSpace(path))
					continue;

				if (entryType == "scene" || path.StartsWith(SceneEntryMarker))
				{
					string scenePath = path.StartsWith(SceneEntryMarker)
						? path.Substring(SceneEntryMarker.Length)
						: path;

					entries.Add(BuildSceneEntry(folderPath, scenePath, locked));
					continue;
				}

				entries.Add(BuildScriptEntry(folderPath, path, linkedScenePath, locked));
			}

			systems[systemProperty.Name] = entries;
		}

		return systems;
	}

	private static bool GetJsonBool(JsonElement element, string propertyName)
	{
		if (TryGetJsonProperty(element, propertyName, out JsonElement value))
		{
			if (value.ValueKind == JsonValueKind.True)
				return true;

			if (value.ValueKind == JsonValueKind.String)
				return value.GetString()?.ToLowerInvariant() == "true";
		}

		return false;
	}

	private static bool TryGetJsonProperty(
		JsonElement element,
		string propertyName,
		out JsonElement value
	)
	{
		if (element.TryGetProperty(propertyName, out value))
			return true;

		string pascalName = char.ToUpperInvariant(propertyName[0]) + propertyName.Substring(1);
		return element.TryGetProperty(pascalName, out value);
	}

	private static string GetJsonString(JsonElement element, string propertyName)
	{
		if (!TryGetJsonProperty(element, propertyName, out JsonElement value))
			return "";

		return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
	}

	private void LoadSystems()
	{
		DebugLogger.LogOperation("Load Systems Requested", SavePath);

		SystemsFileReadResult diskReadResult = ReadSystemsFileFromDisk();

		if (diskReadResult.Status == SystemsFileReadStatus.Missing)
		{
			DebugLogger.Log("Load Systems skipped: save file does not exist.");
			return;
		}

		if (!diskReadResult.IsValid)
		{
			DebugLogger.LogOperation(
				"Load Systems skipped: systems file was unusable",
				$"Status={diskReadResult.Status}, Detail='{diskReadResult.FailureDetail}'"
			);
			return;
		}

		_systems.Clear();

		foreach (KeyValuePair<string, List<string>> system in diskReadResult.Systems)
			_systems[system.Key] = system.Value ?? new List<string>();

		NormalizeAllSystemEntries();

		DebugLogger.LogOperation("Load Systems Completed", $"{_systems.Count} systems");
		DebugLogStateSnapshot("Loaded Systems");
	}

	#endregion
}
#endif
