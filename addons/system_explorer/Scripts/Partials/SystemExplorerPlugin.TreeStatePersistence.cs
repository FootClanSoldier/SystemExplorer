#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

public partial class SystemExplorerPlugin
{
	#region Persistent Tree View State
	private const string TreeStateDirectoryPath = "res://.godot/system_explorer";
	private const string TreeStatePath = TreeStateDirectoryPath + "/tree_state.json";
	private const string TreeStateTemporaryPath = TreeStatePath + ".tmp";
	private const int TreeStateFormatVersion = 1;

	private bool _isRestoringOrRebuildingPersistentTreeExpansion;
	private bool _treeStateSaveDirty;
	private bool _treeStateSaveQueued;
	private bool _treeStateSaveWaitingForNextProcessFrame;
	private bool _treeStatePersistenceShutdown;
	private string _lastTreeStateLoadFailure = "";
	private string _lastTreeStateSaveFailure = "";

	private void LoadPersistentTreeExpansionStateBestEffort(string reason)
	{
		_expandedItems.Clear();

		try
		{
			if (!FileAccess.FileExists(TreeStatePath))
			{
				_lastTreeStateLoadFailure = "";
				return;
			}

			using FileAccess file = FileAccess.Open(TreeStatePath, FileAccess.ModeFlags.Read);
			if (file == null)
			{
				LogTreeStateLoadFailureOnce(reason, $"Open failed with '{FileAccess.GetOpenError()}'.");
				return;
			}

			using JsonDocument document = JsonDocument.Parse(file.GetAsText());
			JsonElement root = document.RootElement;

			if (root.ValueKind != JsonValueKind.Object)
				throw new JsonException("Tree-state root must be a JSON object.");

			if (
				!root.TryGetProperty("format_version", out JsonElement versionElement)
				|| versionElement.ValueKind != JsonValueKind.Number
				|| !versionElement.TryGetInt32(out int formatVersion)
			)
			{
				throw new JsonException("Tree-state format_version must be an integer.");
			}

			if (formatVersion != TreeStateFormatVersion)
			{
				LogTreeStateLoadFailureOnce(
					reason,
					$"Unsupported or missing format_version '{formatVersion}'."
				);
				return;
			}

			if (
				!root.TryGetProperty("expanded_items", out JsonElement expandedItemsElement)
				|| expandedItemsElement.ValueKind != JsonValueKind.Array
			)
			{
				throw new JsonException("Tree-state expanded_items must be an array.");
			}

			var expandedItems = new List<string>();
			foreach (JsonElement itemElement in expandedItemsElement.EnumerateArray())
			{
				if (itemElement.ValueKind != JsonValueKind.String)
					throw new JsonException("Tree-state expanded_items may only contain strings.");

				expandedItems.Add(itemElement.GetString() ?? "");
			}

			HashSet<string> normalizedExpandedItems =
				NormalizePersistentExpansionMetadata(expandedItems);

			foreach (string metadata in normalizedExpandedItems)
				_expandedItems.Add(metadata);

			_lastTreeStateLoadFailure = "";
			DebugLogger.LogOperation(
				"Persistent tree expansion loaded",
				$"Reason='{reason}', ExpandedItems={_expandedItems.Count}"
			);
		}
		catch (Exception exception)
		{
			_expandedItems.Clear();
			LogTreeStateLoadFailureOnce(reason, exception.Message);
		}
	}

	private void QueuePersistentTreeExpansionSave()
	{
		if (_treeStatePersistenceShutdown)
			return;

		_treeStateSaveDirty = true;
		if (_isRestoringOrRebuildingPersistentTreeExpansion)
			return;

		if (!_treeStateSaveQueued)
		{
			_treeStateSaveQueued = true;
			_treeStateSaveWaitingForNextProcessFrame = true;
		}

		RefreshEditorPluginProcessingState();
	}

	private bool HasPendingPersistentTreeStateProcessWork() =>
		!_treeStatePersistenceShutdown && _treeStateSaveQueued;

	private void ProcessPendingPersistentTreeExpansionSave()
	{
		if (!_treeStateSaveQueued)
			return;

		if (_treeStatePersistenceShutdown)
		{
			ClearPendingPersistentTreeExpansionSave();
			return;
		}

		if (_treeStateSaveWaitingForNextProcessFrame)
		{
			// The first process pass only arms the save for a later editor frame.
			_treeStateSaveWaitingForNextProcessFrame = false;
			return;
		}

		if (_isRestoringOrRebuildingPersistentTreeExpansion)
			return;

		if (!IsValidGodotObject(this))
		{
			ClearPendingPersistentTreeExpansionSave();
			return;
		}

		try
		{
			if (!IsInsideTree())
			{
				ClearPendingPersistentTreeExpansionSave();
				return;
			}
		}
		catch
		{
			ClearPendingPersistentTreeExpansionSave();
			return;
		}

		if (!IsValidGodotObject(_tree))
			return;

		bool shouldSave = _treeStateSaveDirty;
		_treeStateSaveQueued = false;
		_treeStateSaveDirty = false;
		_treeStateSaveWaitingForNextProcessFrame = false;

		if (shouldSave)
			SavePersistentTreeExpansionStateBestEffort("Frame-pumped tree expansion change");
	}

	private void ClearPendingPersistentTreeExpansionSave()
	{
		_treeStateSaveDirty = false;
		_treeStateSaveQueued = false;
		_treeStateSaveWaitingForNextProcessFrame = false;
	}

	private void SavePersistentTreeExpansionStateBestEffort(string reason)
	{
		try
		{
			HashSet<string> expansionSource = CaptureNormalTreeExpansionForPersistence();
			List<string> orderedExpandedItems = expansionSource
				.OrderBy(metadata => metadata, StringComparer.OrdinalIgnoreCase)
				.ToList();

			using var stream = new System.IO.MemoryStream();
			using (
				var writer = new Utf8JsonWriter(
					stream,
					new JsonWriterOptions
					{
						Indented = true,
					}
				)
			)
			{
				writer.WriteStartObject();
				writer.WriteNumber("format_version", TreeStateFormatVersion);
				writer.WritePropertyName("expanded_items");
				writer.WriteStartArray();

				foreach (string metadata in orderedExpandedItems)
					writer.WriteStringValue(metadata);

				writer.WriteEndArray();
				writer.WriteEndObject();
				writer.Flush();
			}

			string json = Encoding.UTF8.GetString(stream.ToArray());

			Error directoryError = DirAccess.MakeDirRecursiveAbsolute(
				ProjectSettings.GlobalizePath(TreeStateDirectoryPath)
			);
			if (directoryError != Error.Ok && directoryError != Error.AlreadyExists)
				throw new InvalidOperationException($"Directory creation failed with '{directoryError}'.");

			using (FileAccess temporaryFile = FileAccess.Open(TreeStateTemporaryPath, FileAccess.ModeFlags.Write))
			{
				if (temporaryFile == null)
					throw new InvalidOperationException($"Temporary open failed with '{FileAccess.GetOpenError()}'.");

				if (!temporaryFile.StoreString(json))
					throw new InvalidOperationException("Writing the temporary tree-state file failed.");
			}

			string targetGlobalPath = ProjectSettings.GlobalizePath(TreeStatePath);
			string temporaryGlobalPath = ProjectSettings.GlobalizePath(TreeStateTemporaryPath);

			if (FileAccess.FileExists(TreeStatePath))
			{
				Error removeError = DirAccess.RemoveAbsolute(targetGlobalPath);
				if (removeError != Error.Ok)
					throw new InvalidOperationException($"Replacing the existing tree-state file failed with '{removeError}'.");
			}

			Error renameError = DirAccess.RenameAbsolute(temporaryGlobalPath, targetGlobalPath);
			if (renameError != Error.Ok)
				throw new InvalidOperationException($"Promoting the temporary tree-state file failed with '{renameError}'.");

			_lastTreeStateSaveFailure = "";
			DebugLogger.LogOperation(
				"Persistent tree expansion saved",
				$"Reason='{reason}', ExpandedItems={orderedExpandedItems.Count}"
			);
		}
		catch (Exception exception)
		{
			LogTreeStateSaveFailureOnce(reason, exception.Message);
		}
	}

	private HashSet<string> CaptureNormalTreeExpansionForPersistence()
	{
		IEnumerable<string> source;

		if (_isFilteringScripts || IsScriptFilterActive())
		{
			source = _expandedItemsBeforeScriptFilter;
		}
		else
		{
			SaveExpansionState();
			source = _expandedItems;
		}

		return NormalizePersistentExpansionMetadata(source);
	}

	private static HashSet<string> NormalizePersistentExpansionMetadata(
		IEnumerable<string> metadataEntries
	)
	{
		var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (metadataEntries == null)
			return normalized;

		foreach (string metadata in metadataEntries)
		{
			if (string.IsNullOrWhiteSpace(metadata))
				continue;

			string trimmed = metadata.Trim();
			if (trimmed.StartsWith("system::", StringComparison.OrdinalIgnoreCase))
			{
				if (!string.IsNullOrWhiteSpace(trimmed.Substring("system::".Length)))
					normalized.Add(trimmed);
				continue;
			}

			if (trimmed.StartsWith("folder::", StringComparison.OrdinalIgnoreCase))
			{
				string remainder = trimmed.Substring("folder::".Length);
				int separatorIndex = remainder.IndexOf("::", StringComparison.Ordinal);
				if (separatorIndex > 0 && separatorIndex < remainder.Length - 2)
					normalized.Add(trimmed);
			}
		}

		return normalized;
	}

	private void PrepareTreeStatePersistenceForManagedAssemblyRecovery()
	{
		ClearPendingPersistentTreeExpansionSave();
		_treeStatePersistenceShutdown = false;
		_isRestoringOrRebuildingPersistentTreeExpansion = false;
		RefreshEditorPluginProcessingState();
	}

	private void FlushAndShutdownTreeStatePersistence()
	{
		if (!_treeStatePersistenceShutdown && IsValidGodotObject(_tree))
			SavePersistentTreeExpansionStateBestEffort("Plugin Exit");

		_treeStatePersistenceShutdown = true;
		_isRestoringOrRebuildingPersistentTreeExpansion = false;
		ClearPendingPersistentTreeExpansionSave();
		RefreshEditorPluginProcessingState();
	}

	private void LogTreeStateLoadFailureOnce(string reason, string detail)
	{
		string failure = $"Reason='{reason}', Detail='{detail}'";
		if (string.Equals(_lastTreeStateLoadFailure, failure, StringComparison.Ordinal))
			return;

		_lastTreeStateLoadFailure = failure;
		DebugLogger.LogOperation("Persistent tree expansion load ignored", failure);
	}

	private void LogTreeStateSaveFailureOnce(string reason, string detail)
	{
		string failure = $"Reason='{reason}', Detail='{detail}'";
		if (string.Equals(_lastTreeStateSaveFailure, failure, StringComparison.Ordinal))
			return;

		_lastTreeStateSaveFailure = failure;
		DebugLogger.LogOperation("Persistent tree expansion save ignored", failure);
	}
	#endregion
}
#endif
