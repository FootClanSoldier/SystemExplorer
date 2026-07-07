#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

public partial class SystemExplorerPlugin
{
	#region Quick Actions - Refactor Namespace Batch
	private bool _pendingRefactorNamespaceIsBatch;
	private bool _pendingRefactorNamespaceAddsNamespaceOnly;
	private List<string> _pendingBatchRefactorNamespaceScriptPaths = new();
	private List<string> _pendingBatchRefactorNamespaceNamespaces = new();

	private void OpenBatchRefactorNamespaceDialog(string metadata)
	{
		if (!EnsureSystemsLoadedForTreeOperation("Refactor Namespace"))
			return;

		List<string> scriptPaths = GetRefactorNamespaceScriptPathsForMetadata(metadata);

		if (scriptPaths.Count == 0)
		{
			DebugLog(
				$"Refactor Namespace batch cancelled: no C# scripts found for metadata '{metadata}'."
			);
			return;
		}

		Dictionary<string, string> namespacesByPath = ReadRefactorNamespaceNamespacesByPath(
			scriptPaths
		);
		List<string> namespaces = namespacesByPath
			.Values.Where(namespaceName => !string.IsNullOrWhiteSpace(namespaceName))
			.Distinct(StringComparer.Ordinal)
			.OrderBy(namespaceName => namespaceName, StringComparer.Ordinal)
			.ToList();
		bool hasScriptsWithoutNamespace = namespacesByPath.Values.Any(string.IsNullOrWhiteSpace);

		if (namespaces.Count == 0 && !hasScriptsWithoutNamespace)
		{
			DebugLog(
				$"Refactor Namespace batch cancelled: no namespace candidates found for metadata '{metadata}'."
			);
			return;
		}

		_pendingRefactorNamespaceMetadata = metadata;
		_pendingRefactorNamespaceIsBatch = true;
		_pendingRefactorNamespaceAddsNamespaceOnly = false;
		_pendingBatchRefactorNamespaceScriptPaths = scriptPaths;
		_pendingBatchRefactorNamespaceNamespaces = namespaces;

		ConfigureRefactorNamespaceDialogForBatch(namespaces, hasScriptsWithoutNamespace);

		ApplyRefactorNamespaceDialogSize();
		_refactorNamespaceDialog.PopupCentered(RefactorNamespaceDialogSize);
		CallDeferred(nameof(ApplyRefactorNamespaceDialogSize));

		_newNamespaceInput.GrabFocus();
		_newNamespaceInput.SelectAll();
	}

	private void ConfigureRefactorNamespaceDialogForBatch(
		List<string> namespaces,
		bool hasScriptsWithoutNamespace
	)
	{
		_refactorNamespaceDescriptionLabel.Text =
			"Refactor namespaces for scripts under the selected System or Folder.";
		_newNamespaceLabel.Visible = true;
		_newNamespaceInput.Visible = true;
		_oldNamespaceLabel.Visible = false;
		_oldNamespaceInput.Visible = false;
		_refactorNamespaceApplyToLabel.Visible = true;
		_refactorNamespaceExistingNamespaceOption.Visible = true;
		_refactorNamespaceExistingNamespaceDropdown.Visible = true;
		_refactorNamespaceWithoutNamespaceOption.Visible = true;

		_newNamespaceInput.Text = namespaces.Count > 0 ? namespaces[0] : "";
		_oldNamespaceInput.Text = "";

		_refactorNamespaceExistingNamespaceDropdown.Clear();
		foreach (string namespaceName in namespaces)
			_refactorNamespaceExistingNamespaceDropdown.AddItem(namespaceName);

		_refactorNamespaceExistingNamespaceDropdown.Disabled = namespaces.Count == 0;
		_refactorNamespaceWithoutNamespaceOption.Disabled = !hasScriptsWithoutNamespace;

		bool useExistingNamespaceMode = namespaces.Count > 0;
		SetRefactorNamespaceBatchApplyMode(useExistingNamespaceMode);
	}

	private void ConfirmBatchRefactorNamespaceDialog(string newNamespace)
	{
		if (!IsValidNamespaceName(newNamespace))
		{
			DebugLog(
                "Refactor Namespace batch cancelled: new namespace must be a valid C# namespace name."
			);
			return;
		}

		if (_refactorNamespaceWithoutNamespaceOption.ButtonPressed)
		{
			DebugLogOperation("Refactor Namespace Batch Add Confirmed", newNamespace);
			AddNamespaceToScripts(
				GetBatchRefactorNamespaceScriptsWithoutNamespace(),
				newNamespace,
                "Refactor Namespace Batch Add"
			);
			ClearPendingRefactorNamespaceState();
			return;
		}

		string oldNamespace = GetSelectedBatchRefactorNamespace();

		if (!IsValidNamespaceName(oldNamespace))
		{
			DebugLog("Refactor Namespace batch cancelled: no valid old namespace was selected.");
			return;
		}

		if (oldNamespace == newNamespace)
		{
			DebugLog("Refactor Namespace batch cancelled: namespace is unchanged.");
			return;
		}

		DebugLogOperation(
			"Refactor Namespace Batch Confirmed",
			$"{oldNamespace} -> {newNamespace}"
		);
		RefactorNamespaceForScriptsWithNamespace(
			_pendingBatchRefactorNamespaceScriptPaths,
			oldNamespace,
			newNamespace
		);
		ClearPendingRefactorNamespaceState();
	}

	private void OnRefactorNamespaceExistingNamespaceOptionToggled(bool pressed)
	{
		if (!pressed)
			return;

		SetRefactorNamespaceBatchApplyMode(true);
	}

	private void OnRefactorNamespaceWithoutNamespaceOptionToggled(bool pressed)
	{
		if (!pressed)
			return;

		SetRefactorNamespaceBatchApplyMode(false);
	}

	private void SetRefactorNamespaceBatchApplyMode(bool useExistingNamespaceMode)
	{
		if (_refactorNamespaceExistingNamespaceOption == null)
			return;

		_refactorNamespaceExistingNamespaceOption.SetPressedNoSignal(useExistingNamespaceMode);
		_refactorNamespaceWithoutNamespaceOption.SetPressedNoSignal(!useExistingNamespaceMode);
		_refactorNamespaceExistingNamespaceDropdown.Disabled =
			!useExistingNamespaceMode || _refactorNamespaceExistingNamespaceDropdown.ItemCount == 0;
	}

	private string GetSelectedBatchRefactorNamespace()
	{
		if (_refactorNamespaceExistingNamespaceDropdown == null)
			return "";

		int selectedIndex = _refactorNamespaceExistingNamespaceDropdown.Selected;
		return selectedIndex >= 0
			? _refactorNamespaceExistingNamespaceDropdown.GetItemText(selectedIndex)
			: "";
	}

	private List<string> GetBatchRefactorNamespaceScriptsWithoutNamespace()
	{
		return _pendingBatchRefactorNamespaceScriptPaths
			.Where(path => string.IsNullOrWhiteSpace(ReadNamespaceFromScript(path)))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();
	}

	private bool RefactorNamespaceForScriptsWithNamespace(
		IEnumerable<string> scriptPaths,
		string oldNamespace,
		string newNamespace
	)
	{
		if (!EnsureSystemsLoadedForTreeOperation("Refactor Namespace"))
			return false;

		List<string> targetScriptPaths =
			scriptPaths
				?.Where(path => !string.IsNullOrWhiteSpace(path))
				.Select(NormalizeRefactorNamespacePath)
				.Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList()
			?? new List<string>();

		if (targetScriptPaths.Count == 0)
		{
			DebugLog("Refactor Namespace batch cancelled: no C# scripts were selected.");
			return false;
		}

		HashSet<string> requiredPaths = targetScriptPaths.ToHashSet(
			StringComparer.OrdinalIgnoreCase
		);
		HashSet<string> candidatePaths = targetScriptPaths
			.Concat(GetRefactorNamespaceProjectCSharpFilePaths())
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		if (
			!TryAutosaveOpenRefactorNamespaceCandidateScriptsBeforeBuild(
				candidatePaths,
				requiredPaths,
				out bool didAutosaveCandidateScripts,
				out string candidateAutosaveFailureMessage
			)
		)
		{
			GD.PushWarning(
				string.IsNullOrWhiteSpace(candidateAutosaveFailureMessage)
					? "Refactor Namespace cancelled: open script buffer(s) could not be autosaved safely before scanning namespace usages."
					: candidateAutosaveFailureMessage
			);
			return false;
		}

		if (didAutosaveCandidateScripts)
			DebugLog("Refactor Namespace batch save-first pre-scan saved open script buffer(s).");

		if (
			!TryBuildBatchRefactorNamespacePendingWrites(
				targetScriptPaths,
				oldNamespace,
				newNamespace,
				out string selectedScriptPath,
				out Dictionary<string, string> originalTextsByPath,
				out Dictionary<string, string> pendingWrites
			)
		)
		{
			return false;
		}

		return ApplyRefactorNamespacePendingWrites(
			selectedScriptPath,
			originalTextsByPath,
			pendingWrites,
            "Refactor Namespace Batch"
		);
	}

	private bool TryBuildBatchRefactorNamespacePendingWrites(
		IEnumerable<string> targetScriptPaths,
		string oldNamespace,
		string newNamespace,
		out string selectedScriptPath,
		out Dictionary<string, string> originalTextsByPath,
		out Dictionary<string, string> pendingWrites
	)
	{
		selectedScriptPath = "";
		originalTextsByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		pendingWrites = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		foreach (string scriptPath in targetScriptPaths)
		{
			if (!FileAccess.FileExists(scriptPath))
			{
				DebugLog($"Refactor Namespace batch skipped missing script '{scriptPath}'.");
				continue;
			}

			string scriptText = ReadTextFile(scriptPath);
			string scriptNamespace = GetNamespaceFromText(scriptText);

			if (scriptNamespace != oldNamespace)
				continue;

			string updatedScriptText = ReplaceNamespaceDeclaration(
				scriptText,
				oldNamespace,
				newNamespace,
				out bool namespaceChanged
			);

			if (!namespaceChanged)
			{
				DebugLog(
					$"Refactor Namespace batch skipped '{scriptPath}' because its namespace declaration could not be updated."
				);
				continue;
			}

			if (string.IsNullOrWhiteSpace(selectedScriptPath))
				selectedScriptPath = scriptPath;

			originalTextsByPath[scriptPath] = scriptText;
			pendingWrites[scriptPath] = updatedScriptText;
		}

		if (pendingWrites.Count == 0)
		{
			DebugLog(
				$"Refactor Namespace batch cancelled: no scripts with namespace '{oldNamespace}' could be updated."
			);
			return false;
		}

		foreach (string linkedScriptPath in GetRefactorNamespaceProjectCSharpFilePaths())
		{
			string scriptPath = NormalizeRefactorNamespacePath(linkedScriptPath);

			if (!FileAccess.FileExists(scriptPath))
				continue;

			string scriptText = pendingWrites.TryGetValue(scriptPath, out string pendingText)
				? pendingText
				: ReadTextFile(scriptPath);

			string updatedScriptText = ReplaceUsingStatements(
				scriptText,
				oldNamespace,
				newNamespace,
				out bool usingChanged
			);

			if (!usingChanged)
				continue;

			if (!originalTextsByPath.ContainsKey(scriptPath))
				originalTextsByPath[scriptPath] = scriptText;

			pendingWrites[scriptPath] = updatedScriptText;
		}

		return true;
	}

	private Dictionary<string, string> ReadRefactorNamespaceNamespacesByPath(
		IEnumerable<string> scriptPaths
	)
	{
		Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);

		foreach (
			string scriptPath in scriptPaths
				.Where(path => !string.IsNullOrWhiteSpace(path))
				.Select(NormalizeRefactorNamespacePath)
				.Distinct(StringComparer.OrdinalIgnoreCase)
		)
		{
			if (!FileAccess.FileExists(scriptPath))
			{
				DebugLog(
					$"Refactor Namespace batch skipped missing script while scanning namespaces '{scriptPath}'."
				);
				continue;
			}

			result[scriptPath] = ReadNamespaceFromScript(scriptPath);
		}

		return result;
	}

	private List<string> GetRefactorNamespaceScriptPathsForMetadata(string metadata)
	{
		List<string> result = new();

		if (string.IsNullOrWhiteSpace(metadata))
			return result;

		string systemName = GetSystemNameFromMetadata(metadata);

		if (string.IsNullOrWhiteSpace(systemName)
			|| !_systems.TryGetValue(systemName, out List<string> entries)
		)
		{
			return result;
		}

		string targetFolderPath = metadata.StartsWith("folder::")
			? GetFolderPathFromMetadata(metadata)
			: "";

		foreach (string entry in entries)
		{
			if (!IsScriptEntry(entry))
				continue;

			string folderPath = GetFolderPathFromEntry(entry);

			if (
				!string.IsNullOrWhiteSpace(targetFolderPath)
				&& !IsEntryInsideRefactorNamespaceFolder(folderPath, targetFolderPath)
			)
			{
				continue;
			}

			string scriptPath = NormalizeRefactorNamespacePath(GetScriptPathFromEntry(entry));

			if (scriptPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
				result.Add(scriptPath);
		}

		return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
	}

	private static bool IsEntryInsideRefactorNamespaceFolder(
		string entryFolderPath,
		string targetFolderPath
	)
	{
		string normalizedEntryFolderPath = NormalizeRefactorNamespacePath(entryFolderPath)
			.Trim('/');
		string normalizedTargetFolderPath = NormalizeRefactorNamespacePath(targetFolderPath)
			.Trim('/');

		return normalizedEntryFolderPath.Equals(
				normalizedTargetFolderPath,
				StringComparison.OrdinalIgnoreCase
			)
			|| normalizedEntryFolderPath.StartsWith(
				$"{normalizedTargetFolderPath}/",
				StringComparison.OrdinalIgnoreCase
			);
	}

	private IEnumerable<string> GetRefactorNamespaceProjectCSharpFilePaths()
	{
		string projectRoot = ProjectSettings.GlobalizePath("res://");

		if (string.IsNullOrWhiteSpace(projectRoot) || !System.IO.Directory.Exists(projectRoot))
			return GetLinkedCSharpFilePaths();

		try
		{
			return System
				.IO.Directory.EnumerateFiles(
					projectRoot,
					"*.cs",
					System.IO.SearchOption.AllDirectories
				)
				.Select(ProjectSettings.LocalizePath)
				.Select(NormalizeRefactorNamespacePath)
				.Concat(GetLinkedCSharpFilePaths())
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();
		}
		catch (Exception exception)
		{
			DebugLog($"Refactor Namespace could not scan project C# files: {exception.Message}");
			return GetLinkedCSharpFilePaths();
		}
	}

	#endregion
}
#endif
