#if TOOLS
using Godot;
using System.Collections.Generic;

public partial class SystemExplorerPlugin
{
	#region Context Menu File Manager
	private const string FileManagerOpenOperationLabel = "System Explorer file manager open";

	private long _fileManagerOpenOperationToken;
	private bool _fileManagerOpenRequestPending;
	private string _fileManagerOpenCurrentGlobalPath = "";
	private bool _fileManagerOpenCurrentOpenFolder;
	private long _fileManagerOpenSameTargetCoalescedCount;

	private void ShowPendingItemInFileManager()
	{
		if (string.IsNullOrWhiteSpace(_pendingShowInFileManagerMetadata))
			return;

		if (
			_pendingShowInFileManagerMetadata.StartsWith("folder::")
			&& TryGetFolderBindingFromMetadata(
				_pendingShowInFileManagerMetadata,
				out string boundFolderPath
			)
		)
		{
			ShowBoundFolderInFileManager(boundFolderPath);
			return;
		}

		if (
			!TryGetFileManagerTargetFromMetadata(
				_pendingShowInFileManagerMetadata,
				out string path,
				out string missingEntry,
				out bool isSceneTarget
			)
		)
			return;

		if (!FileAccess.FileExists(path))
		{
			if (isSceneTarget)
				OpenMissingSceneDialog(missingEntry, path);
			else
				OpenMissingScriptDialog(missingEntry, path);

			return;
		}

		string globalPath = ProjectSettings.GlobalizePath(path);

		if (string.IsNullOrWhiteSpace(globalPath))
		{
			GD.PushWarning($"Could not resolve file path: {path}");
			return;
		}

		QueueFileManagerOpenRequest(globalPath, openFolder: false);
	}

	private void ShowBoundFolderInFileManager(string boundFolderPath)
	{
		if (string.IsNullOrWhiteSpace(boundFolderPath))
			return;

		if (!DirAccess.DirExistsAbsolute(boundFolderPath))
		{
			GD.PushWarning($"Bound folder no longer exists: {boundFolderPath}");
			return;
		}

		using DirAccess directory = DirAccess.Open(boundFolderPath);

		if (directory == null)
		{
			GD.PushWarning($"Could not open bound folder: {boundFolderPath}");
			return;
		}

		string globalPath = ProjectSettings.GlobalizePath(boundFolderPath);

		if (string.IsNullOrWhiteSpace(globalPath))
		{
			GD.PushWarning($"Could not resolve bound folder path: {boundFolderPath}");
			return;
		}

		QueueFileManagerOpenRequest(globalPath, openFolder: true);
	}

	private void QueueFileManagerOpenRequest(string globalPath, bool openFolder)
	{
		if (string.IsNullOrWhiteSpace(globalPath))
			return;

		if (
			_fileManagerOpenRequestPending
			&& string.Equals(
				_fileManagerOpenCurrentGlobalPath,
				globalPath,
				System.StringComparison.Ordinal
			)
			&& _fileManagerOpenCurrentOpenFolder == openFolder
		)
		{
			unchecked
			{
				_fileManagerOpenSameTargetCoalescedCount++;
				if (_fileManagerOpenSameTargetCoalescedCount <= 0)
					_fileManagerOpenSameTargetCoalescedCount = 1;
			}

			DebugLogger.LogPersistentFileOnlyOperation(
				"System Explorer file manager open request coalesced",
				$"OperationToken='{_fileManagerOpenOperationToken}', GlobalPath='{_fileManagerOpenCurrentGlobalPath}', OpenFolder='{_fileManagerOpenCurrentOpenFolder}', CoalescedCount='{_fileManagerOpenSameTargetCoalescedCount}', ManagedAssemblyGeneration='{ManagedAssemblyGeneration}'"
			);
			return;
		}

		bool supersedesCurrentRequest = _fileManagerOpenRequestPending;
		long supersededOperationToken = _fileManagerOpenOperationToken;
		string supersededGlobalPath = _fileManagerOpenCurrentGlobalPath;
		bool supersededOpenFolder = _fileManagerOpenCurrentOpenFolder;
		long supersededCoalescedCount = _fileManagerOpenSameTargetCoalescedCount;

		long operationToken = AdvanceFileManagerOpenOperationToken();
		string scheduledManagedAssemblyGeneration = ManagedAssemblyGeneration;

		_fileManagerOpenRequestPending = true;
		_fileManagerOpenCurrentGlobalPath = globalPath;
		_fileManagerOpenCurrentOpenFolder = openFolder;
		_fileManagerOpenSameTargetCoalescedCount = 0;

		if (supersedesCurrentRequest)
		{
			DebugLogger.LogPersistentFileOnlyOperation(
				"System Explorer file manager open request superseded",
				$"SupersededOperationToken='{supersededOperationToken}', SupersededGlobalPath='{supersededGlobalPath}', SupersededOpenFolder='{supersededOpenFolder}', SupersededCoalescedCount='{supersededCoalescedCount}', CurrentOperationToken='{operationToken}', CurrentGlobalPath='{globalPath}', CurrentOpenFolder='{openFolder}', ManagedAssemblyGeneration='{scheduledManagedAssemblyGeneration}'"
			);
		}

		DebugLogger.LogPersistentFileOnlyOperation(
			"System Explorer file manager open request admitted",
			$"OperationToken='{operationToken}', GlobalPath='{globalPath}', OpenFolder='{openFolder}', CoalescedCount='0', ManagedAssemblyGeneration='{scheduledManagedAssemblyGeneration}'"
		);

		try
		{
			CallDeferred(
				nameof(ExecuteFileManagerOpenRequestDeferred),
				operationToken,
				scheduledManagedAssemblyGeneration,
				globalPath,
				openFolder
			);
		}
		catch (System.Exception exception)
		{
			DebugLogger.LogPersistentFileOnlyOperation(
				"System Explorer file manager open request scheduling failed",
				$"OperationToken='{operationToken}', GlobalPath='{globalPath}', OpenFolder='{openFolder}', ManagedAssemblyGeneration='{scheduledManagedAssemblyGeneration}', ExceptionType='{exception.GetType().FullName}', ExceptionMessage='{exception.Message}'"
			);
			InvalidateFileManagerOpenRequest("DeferredSchedulingFailed");
			throw;
		}
	}

	private void ExecuteFileManagerOpenRequestDeferred(
		long operationToken,
		string scheduledManagedAssemblyGeneration,
		string globalPath,
		bool openFolder
	)
	{
		if (
			!string.Equals(
				scheduledManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				System.StringComparison.Ordinal
			)
		)
		{
			LogFileManagerOpenRequestRejected(
				"ManagedAssemblyGenerationChanged",
				operationToken,
				scheduledManagedAssemblyGeneration,
				globalPath,
				openFolder
			);
			return;
		}

		if (operationToken <= 0)
		{
			LogFileManagerOpenRequestRejected(
				"InvalidOperationToken",
				operationToken,
				scheduledManagedAssemblyGeneration,
				globalPath,
				openFolder
			);
			return;
		}

		if (operationToken != _fileManagerOpenOperationToken)
		{
			LogFileManagerOpenRequestRejected(
				"StaleOperationToken",
				operationToken,
				scheduledManagedAssemblyGeneration,
				globalPath,
				openFolder
			);
			return;
		}

		if (!_fileManagerOpenRequestPending)
		{
			LogFileManagerOpenRequestRejected(
				"RequestNoLongerPending",
				operationToken,
				scheduledManagedAssemblyGeneration,
				globalPath,
				openFolder
			);
			return;
		}

		if (
			!string.Equals(
				globalPath,
				_fileManagerOpenCurrentGlobalPath,
				System.StringComparison.Ordinal
			)
		)
		{
			LogFileManagerOpenRequestRejected(
				"GlobalPathAuthorityMismatch",
				operationToken,
				scheduledManagedAssemblyGeneration,
				globalPath,
				openFolder
			);
			return;
		}

		if (openFolder != _fileManagerOpenCurrentOpenFolder)
		{
			LogFileManagerOpenRequestRejected(
				"OpenFolderAuthorityMismatch",
				operationToken,
				scheduledManagedAssemblyGeneration,
				globalPath,
				openFolder
			);
			return;
		}

		if (!GodotObject.IsInstanceValid(this))
		{
			LogFileManagerOpenRequestRejected(
				"PluginInstanceInvalid",
				operationToken,
				scheduledManagedAssemblyGeneration,
				globalPath,
				openFolder
			);
			return;
		}

		if (!IsInsideTree())
		{
			LogFileManagerOpenRequestRejected(
				"PluginOutsideTree",
				operationToken,
				scheduledManagedAssemblyGeneration,
				globalPath,
				openFolder
			);
			return;
		}

		long coalescedCount = _fileManagerOpenSameTargetCoalescedCount;

		try
		{
			LogFileManagerOpenBoundary(
				"Begin",
				operationToken,
				scheduledManagedAssemblyGeneration,
				globalPath,
				openFolder,
				coalescedCount
			);

			var result = OS.ShellShowInFileManager(globalPath, openFolder);

			DebugLogger.LogPersistentFileOnlyOperation(
				FileManagerOpenOperationLabel,
				$"Phase='Returned', OperationToken='{operationToken}', ManagedAssemblyGeneration='{scheduledManagedAssemblyGeneration}', GlobalPath='{globalPath}', OpenFolder='{openFolder}', CoalescedCount='{coalescedCount}', ReturnValue='{result}'"
			);
		}
		catch (System.Exception exception)
		{
			DebugLogger.LogPersistentFileOnlyOperation(
				FileManagerOpenOperationLabel,
				$"Phase='Failed', OperationToken='{operationToken}', ManagedAssemblyGeneration='{scheduledManagedAssemblyGeneration}', GlobalPath='{globalPath}', OpenFolder='{openFolder}', CoalescedCount='{coalescedCount}', ExceptionType='{exception.GetType().FullName}', ExceptionMessage='{exception.Message}'"
			);
			throw;
		}
		finally
		{
			ConsumeCurrentFileManagerOpenRequest(operationToken);
		}
	}

	private void LogFileManagerOpenBoundary(
		string phase,
		long operationToken,
		string managedAssemblyGeneration,
		string globalPath,
		bool openFolder,
		long coalescedCount
	)
	{
		DebugLogger.LogPersistentFileOnlyOperation(
			FileManagerOpenOperationLabel,
			$"Phase='{phase}', OperationToken='{operationToken}', ManagedAssemblyGeneration='{managedAssemblyGeneration}', GlobalPath='{globalPath}', OpenFolder='{openFolder}', CoalescedCount='{coalescedCount}'"
		);
	}

	private void LogFileManagerOpenRequestRejected(
		string reason,
		long operationToken,
		string scheduledManagedAssemblyGeneration,
		string globalPath,
		bool openFolder
	)
	{
		DebugLogger.LogPersistentFileOnlyOperation(
			"System Explorer file manager open request rejected",
			$"Reason='{reason}', OperationToken='{operationToken}', CurrentOperationToken='{_fileManagerOpenOperationToken}', ScheduledManagedAssemblyGeneration='{scheduledManagedAssemblyGeneration ?? ""}', CurrentManagedAssemblyGeneration='{ManagedAssemblyGeneration}', GlobalPath='{globalPath ?? ""}', OpenFolder='{openFolder}', CurrentRequestPending='{_fileManagerOpenRequestPending}', CurrentGlobalPath='{_fileManagerOpenCurrentGlobalPath}', CurrentOpenFolder='{_fileManagerOpenCurrentOpenFolder}', CurrentCoalescedCount='{_fileManagerOpenSameTargetCoalescedCount}'"
		);
	}

	private long AdvanceFileManagerOpenOperationToken()
	{
		unchecked
		{
			_fileManagerOpenOperationToken++;
			if (_fileManagerOpenOperationToken <= 0)
				_fileManagerOpenOperationToken = 1;
		}

		return _fileManagerOpenOperationToken;
	}

	private void ConsumeCurrentFileManagerOpenRequest(long operationToken)
	{
		if (operationToken != _fileManagerOpenOperationToken)
			return;

		_fileManagerOpenRequestPending = false;
		_fileManagerOpenCurrentGlobalPath = "";
		_fileManagerOpenCurrentOpenFolder = false;
		_fileManagerOpenSameTargetCoalescedCount = 0;
	}

	private void InvalidateFileManagerOpenRequest(string reason)
	{
		bool hadCurrentRequest = _fileManagerOpenRequestPending;
		long invalidatedOperationToken = _fileManagerOpenOperationToken;
		string invalidatedGlobalPath = _fileManagerOpenCurrentGlobalPath;
		bool invalidatedOpenFolder = _fileManagerOpenCurrentOpenFolder;
		long invalidatedCoalescedCount = _fileManagerOpenSameTargetCoalescedCount;

		_fileManagerOpenRequestPending = false;
		_fileManagerOpenCurrentGlobalPath = "";
		_fileManagerOpenCurrentOpenFolder = false;
		_fileManagerOpenSameTargetCoalescedCount = 0;
		long currentOperationToken = AdvanceFileManagerOpenOperationToken();

		if (!hadCurrentRequest)
			return;

		DebugLogger.LogPersistentFileOnlyOperation(
			"System Explorer file manager open request invalidated",
			$"Reason='{reason ?? ""}', InvalidatedOperationToken='{invalidatedOperationToken}', CurrentOperationToken='{currentOperationToken}', GlobalPath='{invalidatedGlobalPath}', OpenFolder='{invalidatedOpenFolder}', CoalescedCount='{invalidatedCoalescedCount}', ManagedAssemblyGeneration='{ManagedAssemblyGeneration}'"
		);
	}

	private bool TryGetFileManagerTargetFromMetadata(
		string metadata,
		out string path,
		out string missingEntry,
		out bool isSceneTarget
	)
	{
		path = "";
		missingEntry = "";
		isSceneTarget = false;

		if (metadata.StartsWith("script::"))
		{
			string entry = GetEntryFromMetadata(metadata);
			path = GetScriptPathFromEntry(entry);
			missingEntry = entry;
			return !string.IsNullOrWhiteSpace(path);
		}

		if (metadata.StartsWith("sceneLink::"))
		{
			string entry = metadata.Substring("sceneLink::".Length);
			path = GetScenePathFromEntry(entry);
			missingEntry = entry;
			isSceneTarget = true;
			return !string.IsNullOrWhiteSpace(path);
		}

		if (metadata.StartsWith("folder::"))
			return TryGetFolderFileManagerTarget(
				metadata,
				out path,
				out missingEntry,
				out isSceneTarget
			);

		return false;
	}

	private bool HasFolderFileManagerTarget(string metadata)
	{
		if (!metadata.StartsWith("folder::"))
			return false;

		if (TryGetFolderBindingFromMetadata(metadata, out _))
			return true;

		string systemName = GetSystemNameFromMetadata(metadata);
		string folderPath = GetFolderPathFromMetadata(metadata);

		if (string.IsNullOrWhiteSpace(systemName) || string.IsNullOrWhiteSpace(folderPath))
			return false;

		if (!_systems.TryGetValue(systemName, out var entries) || entries == null)
			return false;

		string entry = FindFirstFileManagerEntryInFolder(entries, folderPath);

		if (string.IsNullOrWhiteSpace(entry))
			return false;

		string path = IsSceneEntry(entry)
			? GetScenePathFromEntry(entry)
			: GetScriptPathFromEntry(entry);
		return !string.IsNullOrWhiteSpace(path);
	}

	private bool TryGetFolderFileManagerTarget(
		string metadata,
		out string path,
		out string missingEntry,
		out bool isSceneTarget
	)
	{
		path = "";
		missingEntry = "";
		isSceneTarget = false;

		string systemName = GetSystemNameFromMetadata(metadata);
		string folderPath = GetFolderPathFromMetadata(metadata);

		if (string.IsNullOrWhiteSpace(systemName) || string.IsNullOrWhiteSpace(folderPath))
			return false;

		if (!_systems.TryGetValue(systemName, out var entries) || entries == null)
			return false;

		string entry = FindFirstFileManagerEntryInFolder(entries, folderPath);

		if (string.IsNullOrWhiteSpace(entry))
		{
			GD.PushWarning(
				$"System Explorer could not find a script or scene in folder: {folderPath}"
			);
			return false;
		}

		missingEntry = entry;
		isSceneTarget = IsSceneEntry(entry);
		path = isSceneTarget ? GetScenePathFromEntry(entry) : GetScriptPathFromEntry(entry);

		return !string.IsNullOrWhiteSpace(path);
	}

	private static string FindFirstFileManagerEntryInFolder(List<string> entries, string folderPath)
	{
		string entry = FindFirstEntryInFolder(
			entries,
			folderPath,
			includeNestedFolders: false,
			sceneEntriesOnly: false
		);
		entry = string.IsNullOrWhiteSpace(entry)
			? FindFirstEntryInFolder(
				entries,
				folderPath,
				includeNestedFolders: true,
				sceneEntriesOnly: false
			)
			: entry;
		entry = string.IsNullOrWhiteSpace(entry)
			? FindFirstEntryInFolder(
				entries,
				folderPath,
				includeNestedFolders: false,
				sceneEntriesOnly: true
			)
			: entry;
		entry = string.IsNullOrWhiteSpace(entry)
			? FindFirstEntryInFolder(
				entries,
				folderPath,
				includeNestedFolders: true,
				sceneEntriesOnly: true
			)
			: entry;

		return entry;
	}

	private static string FindFirstEntryInFolder(
		List<string> entries,
		string folderPath,
		bool includeNestedFolders,
		bool sceneEntriesOnly
	)
	{
		foreach (string entry in entries)
		{
			if (!IsScriptOrSceneEntry(entry))
				continue;

			bool isSceneEntry = IsSceneEntry(entry);

			if (sceneEntriesOnly != isSceneEntry)
				continue;

			string entryFolderPath = GetFolderPathFromEntry(entry);

			if (entryFolderPath == folderPath)
				return entry;

			if (
				includeNestedFolders
				&& entryFolderPath.StartsWith($"{folderPath}/", System.StringComparison.Ordinal)
			)
				return entry;
		}

		return "";
	}
	#endregion
}
#endif
