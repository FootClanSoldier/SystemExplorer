#if TOOLS
using Godot;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

public partial class SystemExplorerPlugin
{
	#region Persistence and Save Guards
	private bool SaveSystems()
	{
		DebugLogger.LogOperation("Save Systems Requested", $"{_systems.Count} systems");

		if (WouldOverwriteExistingDataWithEmptySystems())
		{
			GD.PushWarning(
                "System Explorer blocked saving an empty systems file because existing data was found on disk."
			);
			DebugLogger.Log(
                "Save Systems blocked: empty in-memory systems would overwrite existing data."
			);
			DebugLogStateSnapshot("Blocked Save");
			return false;
		}

		if (WouldOverwriteExistingDataWithUnrelatedSystems())
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

		using FileAccess file = FileAccess.Open(SavePath, FileAccess.ModeFlags.Write);
		file.StoreString(json);

		DebugLogger.LogOperation(
			"Save Systems Completed",
			$"{_systems.Count} systems, {json.Length} chars"
		);

		return true;
	}

	private bool WouldOverwriteExistingDataWithEmptySystems()
	{
		if (_systems.Count > 0)
			return false;

		if (!FileAccess.FileExists(SavePath))
			return false;

		using FileAccess file = FileAccess.Open(SavePath, FileAccess.ModeFlags.Read);
		string existingJson = file.GetAsText();

		if (string.IsNullOrWhiteSpace(existingJson))
			return false;

		return existingJson.Trim() != "{}";
	}

	private bool WouldOverwriteExistingDataWithUnrelatedSystems()
	{
		Dictionary<string, List<string>> existingSystems = LoadSystemsFromDiskForSaveGuard();

		if (existingSystems == null || existingSystems.Count <= 1 || _systems.Count == 0)
			return false;

		return !_systems.Keys.Any(existingSystems.ContainsKey);
	}

	private Dictionary<string, List<string>> LoadSystemsFromDiskForSaveGuard()
	{
		if (!FileAccess.FileExists(SavePath))
			return null;

		using FileAccess file = FileAccess.Open(SavePath, FileAccess.ModeFlags.Read);
		string existingJson = file.GetAsText();

		if (string.IsNullOrWhiteSpace(existingJson))
			return null;

		try
		{
			return DeserializeSystems(existingJson);
		}
		catch
		{
			return null;
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

		Dictionary<string, List<string>> recoveredSystems = LoadSystemsFromDiskForRecovery();

		if (recoveredSystems == null || recoveredSystems.Count == 0)
		{
			DebugLogger.Log("Recovery From Disk failed: no usable systems found on disk.");

			if (DebugState)
				GD.PushError(
					$"[SystemExplorer] Recovery failed. Reason='{reason}', Required='{requiredSystemName}'."
				);

			return false;
		}

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

		DebugLogger.LogOperation("Recovery From Disk Completed", $"{_systems.Count} systems");

		if (DebugState)
		{
			GD.PushWarning(
				$"[SystemExplorer] Recovery successful. Reason='{reason}', Required='{requiredSystemName}', Recovered Systems={_systems.Count}"
			);
		}

		DebugLogStateSnapshot("Recovered From Disk");

		return true;
	}

	private Dictionary<string, List<string>> LoadSystemsFromDiskForRecovery()
	{
		if (!FileAccess.FileExists(SavePath))
			return null;

		using FileAccess file = FileAccess.Open(SavePath, FileAccess.ModeFlags.Read);
		string existingJson = file.GetAsText();

		if (string.IsNullOrWhiteSpace(existingJson))
			return null;

		try
		{
			return DeserializeSystems(existingJson);
		}
		catch (System.Exception exception)
		{
			DebugLogger.LogOperation(
				"Recovery From Disk failed: JSON deserialize error",
				exception.Message
			);
			return null;
		}
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
			return systems;

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

		if (!FileAccess.FileExists(SavePath))
		{
			DebugLogger.Log("Load Systems skipped: save file does not exist.");
			return;
		}

		using FileAccess file = FileAccess.Open(SavePath, FileAccess.ModeFlags.Read);
		string json = file.GetAsText();

		if (string.IsNullOrWhiteSpace(json))
		{
			DebugLogger.Log("Load Systems skipped: save file is empty.");
			return;
		}

		Dictionary<string, List<string>> loaded = DeserializeSystems(json);

		if (loaded == null)
		{
			DebugLogger.Log("Load Systems skipped: deserialized data was null.");
			return;
		}

		_systems.Clear();

		foreach (KeyValuePair<string, List<string>> system in loaded)
			_systems[system.Key] = system.Value ?? new List<string>();

		NormalizeAllSystemEntries();

		DebugLogger.LogOperation("Load Systems Completed", $"{_systems.Count} systems");
		DebugLogStateSnapshot("Loaded Systems");
	}

	#endregion
}
#endif
