#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

public partial class SystemExplorerPlugin
{
	#region Quick Actions - Refactor Namespace
	private static readonly Vector2I RefactorNamespaceDialogSize = new(520, 285);

	private Dictionary<string, string> _deferredRefactorNamespaceOriginalTextsByPath = new(
		StringComparer.OrdinalIgnoreCase
	);

	private void OpenRefactorNamespaceDialog()
	{
		if (string.IsNullOrWhiteSpace(_pendingRenameMetadata))
			return;

		if (
			_pendingRenameMetadata.StartsWith("system::")
			|| _pendingRenameMetadata.StartsWith("folder::")
		)
		{
			OpenBatchRefactorNamespaceDialog(_pendingRenameMetadata);
			return;
		}

		if (!_pendingRenameMetadata.StartsWith("script::"))
			return;

		_pendingRefactorNamespaceMetadata = _pendingRenameMetadata;
		_pendingRefactorNamespaceIsBatch = false;
		_pendingRefactorNamespaceAddsNamespaceOnly = false;

		string scriptEntry = GetEntryFromMetadata(_pendingRefactorNamespaceMetadata);
		string scriptPath = GetScriptPathFromEntry(scriptEntry);
		string currentNamespace = ReadNamespaceFromScript(scriptPath);

		if (string.IsNullOrWhiteSpace(currentNamespace))
		{
			DebugLog(
				$"Refactor Namespace found no namespace in '{scriptPath}'. Opening add-namespace dialog."
			);
			OpenSingleScriptAddNamespaceDialog(scriptPath);
			return;
		}

		ConfigureRefactorNamespaceDialogForSingleExistingNamespace(currentNamespace);

		ApplyRefactorNamespaceDialogSize();
		_refactorNamespaceDialog.PopupCentered(RefactorNamespaceDialogSize);
		CallDeferred(nameof(ApplyRefactorNamespaceDialogSize));

		_newNamespaceInput.GrabFocus();
		_newNamespaceInput.SelectAll();
	}

	private void ConfigureRefactorNamespaceDialogForSingleExistingNamespace(string currentNamespace)
	{
		_refactorNamespaceDescriptionLabel.Text =
			"Update the selected script namespace and\nmatching using statements in linked C# files.";
		_newNamespaceLabel.Visible = true;
		_newNamespaceInput.Visible = true;
		_oldNamespaceLabel.Visible = true;
		_oldNamespaceInput.Visible = true;
		_refactorNamespaceApplyToLabel.Visible = false;
		_refactorNamespaceExistingNamespaceOption.Visible = false;
		_refactorNamespaceExistingNamespaceDropdown.Visible = false;
		_refactorNamespaceWithoutNamespaceOption.Visible = false;

		_oldNamespaceInput.Text = currentNamespace;
		_oldNamespaceInput.Editable = false;
		_newNamespaceInput.Text = currentNamespace;
	}

	private void OpenSingleScriptAddNamespaceDialog(string scriptPath)
	{
		_pendingRefactorNamespaceAddsNamespaceOnly = true;
		_pendingBatchRefactorNamespaceScriptPaths = new List<string>
		{
			NormalizeScriptPath(scriptPath),
		};

		_refactorNamespaceDescriptionLabel.Text =
			"Add a namespace block to the selected script.\nUsing statements will not be changed.";
		_newNamespaceLabel.Visible = true;
		_newNamespaceInput.Visible = true;
		_oldNamespaceLabel.Visible = false;
		_oldNamespaceInput.Visible = false;
		_refactorNamespaceApplyToLabel.Visible = false;
		_refactorNamespaceExistingNamespaceOption.Visible = false;
		_refactorNamespaceExistingNamespaceDropdown.Visible = false;
		_refactorNamespaceWithoutNamespaceOption.Visible = false;

		_oldNamespaceInput.Text = "";
		_newNamespaceInput.Text = "";

		ApplyRefactorNamespaceDialogSize();
		_refactorNamespaceDialog.PopupCentered(RefactorNamespaceDialogSize);
		CallDeferred(nameof(ApplyRefactorNamespaceDialogSize));

		_newNamespaceInput.GrabFocus();
	}

	private void ApplyRefactorNamespaceDialogSize()
	{
		if (_refactorNamespaceDialog == null)
			return;

		_refactorNamespaceDialog.MinSize = RefactorNamespaceDialogSize;
		_refactorNamespaceDialog.Size = RefactorNamespaceDialogSize;
	}

	private void OnRefactorNamespaceConfirmed()
	{
		if (string.IsNullOrWhiteSpace(_pendingRefactorNamespaceMetadata))
			return;

		string newNamespace = _newNamespaceInput.Text.Trim();

		if (_pendingRefactorNamespaceIsBatch)
		{
			ConfirmBatchRefactorNamespaceDialog(newNamespace);
			return;
		}

		if (_pendingRefactorNamespaceAddsNamespaceOnly)
		{
			DebugLogOperation("Refactor Namespace Add Confirmed", newNamespace);

			if (!IsValidNamespaceName(newNamespace))
			{
				DebugLog(
                    "Refactor Namespace add cancelled: new namespace must be a valid C# namespace name."
				);
				return;
			}

			AddNamespaceToScripts(
				_pendingBatchRefactorNamespaceScriptPaths,
				newNamespace,
                "Refactor Namespace Add"
			);
			ClearPendingRefactorNamespaceState();
			return;
		}

		string oldNamespace = _oldNamespaceInput.Text.Trim();

		DebugLogOperation("Refactor Namespace Confirmed", $"{oldNamespace} -> {newNamespace}");

		if (!IsValidNamespaceName(oldNamespace) || !IsValidNamespaceName(newNamespace))
		{
			GD.PushWarning(
                "Refactor Namespace cancelled: namespace values must be valid C# namespace names."
			);
			return;
		}

		if (oldNamespace == newNamespace)
		{
			DebugLog("Refactor Namespace cancelled: namespace is unchanged.");
			return;
		}

		RefactorNamespace(_pendingRefactorNamespaceMetadata, oldNamespace, newNamespace);
		ClearPendingRefactorNamespaceState();
	}

	private void ClearPendingRefactorNamespaceState()
	{
		_pendingRefactorNamespaceMetadata = "";
		_pendingRefactorNamespaceIsBatch = false;
		_pendingRefactorNamespaceAddsNamespaceOnly = false;
		_pendingBatchRefactorNamespaceScriptPaths.Clear();
		_pendingBatchRefactorNamespaceNamespaces.Clear();
	}

	private bool RefactorNamespace(string metadata, string oldNamespace, string newNamespace)
	{
		if (!EnsureSystemsLoadedForTreeOperation("Refactor Namespace"))
			return false;

		if (string.IsNullOrWhiteSpace(metadata) || !metadata.StartsWith("script::"))
			return false;

		DebugDumpRefactorNamespaceEditorState(metadata, "start");

		if (
			!TryAutosaveOpenRefactorNamespaceCandidateScriptsBeforeBuild(
				metadata,
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
			DebugLog("Refactor Namespace save-first pre-scan saved open script buffer(s).");

		if (
			!TryBuildRefactorNamespacePendingWrites(
				metadata,
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

		Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath;

		if (
			!TryGetOpenScriptEditorsByActivatingPaths(
				pendingWrites.Keys,
				true,
				out openEditorsByPath,
				out string affectedEditorFailureMessage
			)
		)
		{
			GD.PushWarning(
				string.IsNullOrWhiteSpace(affectedEditorFailureMessage)
					? "Refactor Namespace cancelled: affected open script buffer(s) could not be matched safely."
					: affectedEditorFailureMessage
			);
			return false;
		}

		if (
			!TryAutosaveOpenScriptEditorBuffersIfNeeded(
				openEditorsByPath,
				out bool didAutosaveOpenEditors,
				out string autosaveFailureMessage
			)
		)
		{
			GD.PushWarning(
				string.IsNullOrWhiteSpace(autosaveFailureMessage)
					? "Refactor Namespace cancelled: affected open script buffer(s) could not be autosaved safely."
					: autosaveFailureMessage
			);
			return false;
		}

		if (didAutosaveOpenEditors)
		{
			if (
				!TryBuildRefactorNamespacePendingWrites(
					metadata,
					oldNamespace,
					newNamespace,
					out selectedScriptPath,
					out originalTextsByPath,
					out pendingWrites
				)
			)
			{
				return false;
			}

			if (
				!TryGetOpenScriptEditorsByActivatingPaths(
					pendingWrites.Keys,
					true,
					out openEditorsByPath,
					out affectedEditorFailureMessage
				)
			)
			{
				GD.PushWarning(
					string.IsNullOrWhiteSpace(affectedEditorFailureMessage)
						? "Refactor Namespace cancelled: affected open script buffer(s) could not be matched safely after autosaving."
						: affectedEditorFailureMessage
				);
				return false;
			}
		}

		if (HasUnsavedOpenScriptEditorBuffers(openEditorsByPath, out string unsavedScriptList))
		{
			GD.PushWarning(
				$"Refactor Namespace cancelled: affected script(s) are still unsaved after the save-first check. Try again after saving/retrying:\n{unsavedScriptList}"
			);
			return false;
		}

		foreach (KeyValuePair<string, string> pendingWrite in pendingWrites)
		{
			if (!WriteTextFile(pendingWrite.Key, pendingWrite.Value))
			{
				GD.PushWarning(
					$"Refactor Namespace failed while writing '{pendingWrite.Key}'. Some files may have already been updated."
				);
				RefreshGodotAfterScriptTextChanges(pendingWrites.Keys);
				return false;
			}
		}

		ApplyTextToOpenScriptEditors(openEditorsByPath, pendingWrites);
		RefreshGodotAfterScriptTextChanges(pendingWrites.Keys);

		_deferredRefactorNamespaceOriginalTextsByPath = new Dictionary<string, string>(
			originalTextsByPath,
			StringComparer.OrdinalIgnoreCase
		);

		string changedScriptPathPayload = BuildScriptPathPayload(pendingWrites.Keys);
		RestoreRefactorNamespaceTargetScriptEditor(selectedScriptPath);
		SyncSelectionToActiveScriptAfterOperation();

		CallDeferred(
			nameof(RefreshOpenScriptEditorBuffersAfterRefactorNamespaceDeferred),
			changedScriptPathPayload
		);
		CallDeferred(
			nameof(RestoreRefactorNamespaceTargetScriptEditorDeferred),
			selectedScriptPath
		);
		CallDeferred(nameof(SyncSelectionToActiveScriptAfterOperation));
		CallDeferred(nameof(ReleaseTreeFocusAfterNavigation));

		DebugLogOperation(
			"Refactor Namespace Completed",
			$"Updated {pendingWrites.Count} file(s)."
		);
		return true;
	}

	private bool AddNamespaceToScripts(
		IEnumerable<string> scriptPaths,
		string newNamespace,
		string operationName
	)
	{
		if (!EnsureSystemsLoadedForTreeOperation(operationName))
			return false;

		List<string> targetScriptPaths =
			scriptPaths
				?.Where(path => !string.IsNullOrWhiteSpace(path))
				.Select(NormalizeScriptPath)
				.Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList()
			?? new List<string>();

		if (targetScriptPaths.Count == 0)
		{
			DebugLog($"{operationName} cancelled: no C# scripts were selected.");
			return false;
		}

		bool preserveBatchUiState = operationName.Contains(
			"Batch",
			StringComparison.OrdinalIgnoreCase
		);

		if (preserveBatchUiState)
			BeginBatchScriptEditorContextPreservation();

		try
		{
			if (
				!TryAutosaveOpenRefactorNamespaceCandidateScriptsBeforeBuild(
					targetScriptPaths,
					targetScriptPaths.ToHashSet(StringComparer.OrdinalIgnoreCase),
					out bool didAutosaveCandidateScripts,
					out string candidateAutosaveFailureMessage,
					allowScriptEditorActivation: !preserveBatchUiState
				)
			)
			{
				DebugLog(
					string.IsNullOrWhiteSpace(candidateAutosaveFailureMessage)
						? $"{operationName} cancelled: open script buffer(s) could not be autosaved safely before adding namespace."
						: candidateAutosaveFailureMessage
				);
				return false;
			}

			if (didAutosaveCandidateScripts)
				DebugLog($"{operationName} save-first pre-scan saved open script buffer(s).");

			if (
				!TryBuildAddNamespacePendingWrites(
					targetScriptPaths,
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
				operationName,
				"",
				!preserveBatchUiState
			);
		}
		finally
		{
			if (preserveBatchUiState)
				EndBatchScriptEditorContextPreservation();
		}
	}

	private bool TryBuildAddNamespacePendingWrites(
		IEnumerable<string> scriptPaths,
		string newNamespace,
		out string selectedScriptPath,
		out Dictionary<string, string> originalTextsByPath,
		out Dictionary<string, string> pendingWrites
	)
	{
		selectedScriptPath = "";
		originalTextsByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		pendingWrites = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		foreach (
			string scriptPath in scriptPaths
				.Where(path => !string.IsNullOrWhiteSpace(path))
				.Select(NormalizeScriptPath)
				.Distinct(StringComparer.OrdinalIgnoreCase)
		)
		{
			if (!FileAccess.FileExists(scriptPath))
			{
				DebugLog($"Refactor Namespace add skipped missing script '{scriptPath}'.");
				continue;
			}

			string scriptText = ReadTextFile(scriptPath);

			if (!string.IsNullOrWhiteSpace(GetNamespaceFromText(scriptText)))
			{
				DebugLog(
					$"Refactor Namespace add skipped '{scriptPath}' because it already has a namespace."
				);
				continue;
			}

			string updatedScriptText = AddNamespaceBlockToScriptText(
				scriptText,
				newNamespace,
				out bool namespaceAdded
			);

			if (!namespaceAdded)
			{
				DebugLog(
					$"Refactor Namespace add skipped '{scriptPath}' because the namespace block could not be inserted."
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
                "Refactor Namespace add cancelled: no scripts without namespace could be updated."
			);
			return false;
		}

		return true;
	}

	private bool ApplyRefactorNamespacePendingWrites(
		string selectedScriptPath,
		Dictionary<string, string> originalTextsByPath,
		Dictionary<string, string> pendingWrites,
		string operationName,
		string scriptPathToRestoreAfterOperation = "",
		bool syncSelectionAfterOperation = true
	)
	{
		if (pendingWrites == null || pendingWrites.Count == 0)
		{
			DebugLog($"{operationName} cancelled: no file changes were produced.");
			return false;
		}

		Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath;

		if (syncSelectionAfterOperation)
		{
			if (
				!TryGetOpenScriptEditorsByActivatingPaths(
					pendingWrites.Keys,
					true,
					out openEditorsByPath,
					out string affectedEditorFailureMessage
				)
			)
			{
				GD.PushWarning(
					string.IsNullOrWhiteSpace(affectedEditorFailureMessage)
						? $"{operationName} cancelled: affected open script buffer(s) could not be matched safely."
						: affectedEditorFailureMessage
				);
				return false;
			}
		}
		else
		{
			openEditorsByPath = GetOpenScriptEditorsByPath(
				originalTextsByPath,
				pendingWrites,
				out string unsafeOpenScriptList
			);

			if (!string.IsNullOrWhiteSpace(unsafeOpenScriptList))
			{
				GD.PushWarning(
					$"{operationName} cancelled: System Explorer could not safely match open script editor buffer(s) without changing the active editor tab. Save/reopen before refactoring:\n{unsafeOpenScriptList}"
				);
				return false;
			}
		}

		if (
			!TryAutosaveOpenScriptEditorBuffersIfNeeded(
				openEditorsByPath,
				out bool didAutosaveOpenEditors,
				out string autosaveFailureMessage
			)
		)
		{
			GD.PushWarning(
				string.IsNullOrWhiteSpace(autosaveFailureMessage)
					? $"{operationName} cancelled: affected open script buffer(s) could not be autosaved safely."
					: autosaveFailureMessage
			);
			return false;
		}

		if (didAutosaveOpenEditors)
			DebugLog($"{operationName} autosaved affected open script buffer(s) before writing.");

		if (HasUnsavedOpenScriptEditorBuffers(openEditorsByPath, out string unsavedScriptList))
		{
			GD.PushWarning(
				$"{operationName} cancelled: affected script(s) are still unsaved after the save-first check. Try again after saving/retrying:\n{unsavedScriptList}"
			);
			return false;
		}

		foreach (KeyValuePair<string, string> pendingWrite in pendingWrites)
		{
			if (!WriteTextFile(pendingWrite.Key, pendingWrite.Value))
			{
				GD.PushWarning(
					$"{operationName} failed while writing '{pendingWrite.Key}'. Some files may have already been updated."
				);
				RefreshGodotAfterScriptTextChanges(pendingWrites.Keys);
				return false;
			}
		}

		ApplyTextToOpenScriptEditors(openEditorsByPath, pendingWrites);
		RefreshGodotAfterScriptTextChanges(pendingWrites.Keys);

		_deferredRefactorNamespaceOriginalTextsByPath = new Dictionary<string, string>(
			originalTextsByPath,
			StringComparer.OrdinalIgnoreCase
		);

		string changedScriptPathPayload = BuildScriptPathPayload(pendingWrites.Keys);
		string restoreScriptPath = "";

		if (syncSelectionAfterOperation)
		{
			restoreScriptPath = string.IsNullOrWhiteSpace(scriptPathToRestoreAfterOperation)
				? selectedScriptPath
				: scriptPathToRestoreAfterOperation;

			if (!string.IsNullOrWhiteSpace(restoreScriptPath))
				RestoreRefactorNamespaceTargetScriptEditor(restoreScriptPath);
		}

		CallDeferred(
			nameof(RefreshOpenScriptEditorBuffersAfterRefactorNamespaceDeferred),
			changedScriptPathPayload
		);

		if (syncSelectionAfterOperation && !string.IsNullOrWhiteSpace(restoreScriptPath))
		{
			SyncSelectionToActiveScriptAfterOperation();

			CallDeferred(
				nameof(RestoreRefactorNamespaceTargetScriptEditorDeferred),
				restoreScriptPath
			);
			CallDeferred(nameof(SyncSelectionToActiveScriptAfterOperation));
		}

		if (syncSelectionAfterOperation)
			CallDeferred(nameof(ReleaseTreeFocusAfterNavigation));

		DebugLogOperation($"{operationName} Completed", $"Updated {pendingWrites.Count} file(s).");
		return true;
	}

	private void RestoreRefactorNamespaceTargetScriptEditorDeferred(string scriptPath)
	{
		RestoreRefactorNamespaceTargetScriptEditor(scriptPath);
	}

	private void RestoreRefactorNamespaceTargetScriptEditor(string scriptPath)
	{
		string normalizedPath = NormalizeScriptPath(scriptPath);

		if (string.IsNullOrWhiteSpace(normalizedPath))
			return;

		EditorInterface editorInterface = EditorInterface.Singleton;

		if (editorInterface == null)
			return;

		Script script = ResourceLoader.Load<Script>(normalizedPath);

		if (script == null)
		{
			DebugLog(
				$"Refactor Namespace could not restore target script editor because '{normalizedPath}' could not be loaded."
			);
			return;
		}

		editorInterface.EditScript(script);
		DebugLog($"Refactor Namespace restored target script editor '{normalizedPath}'.");
	}

	private bool TryBuildRefactorNamespacePendingWrites(
		string metadata,
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

		if (string.IsNullOrWhiteSpace(metadata) || !metadata.StartsWith("script::"))
			return false;

		string selectedEntry = GetEntryFromMetadata(metadata);
		selectedScriptPath = NormalizeScriptPath(GetScriptPathFromEntry(selectedEntry));

		if (!FileAccess.FileExists(selectedScriptPath))
		{
			OpenMissingScriptDialog(selectedEntry, selectedScriptPath);
			return false;
		}

		string selectedScriptText = ReadTextFile(selectedScriptPath);
		string selectedScriptNamespace = GetNamespaceFromText(selectedScriptText);

		if (string.IsNullOrWhiteSpace(selectedScriptNamespace))
		{
			GD.PushWarning(
				$"Refactor Namespace cancelled: no namespace declaration was found in '{selectedScriptPath}'."
			);
			return false;
		}

		if (selectedScriptNamespace != oldNamespace)
		{
			GD.PushWarning(
				$"Refactor Namespace cancelled: selected script namespace is '{selectedScriptNamespace}', not '{oldNamespace}'."
			);
			return false;
		}

		string updatedSelectedScriptText = ReplaceNamespaceDeclaration(
			selectedScriptText,
			oldNamespace,
			newNamespace,
			out bool namespaceChanged
		);

		if (!namespaceChanged)
		{
			GD.PushWarning(
				$"Refactor Namespace cancelled: namespace declaration could not be updated in '{selectedScriptPath}'."
			);
			return false;
		}

		originalTextsByPath[selectedScriptPath] = selectedScriptText;
		pendingWrites[selectedScriptPath] = updatedSelectedScriptText;

		foreach (string linkedScriptPath in GetLinkedCSharpFilePaths())
		{
			string scriptPath = NormalizeScriptPath(linkedScriptPath);

			if (!FileAccess.FileExists(scriptPath))
				continue;

			string scriptText =
				scriptPath == selectedScriptPath
					? updatedSelectedScriptText
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
				originalTextsByPath[scriptPath] =
					scriptPath == selectedScriptPath ? selectedScriptText : scriptText;

			pendingWrites[scriptPath] = updatedScriptText;
		}

		return true;
	}

	private bool TryAutosaveOpenRefactorNamespaceCandidateScriptsBeforeBuild(
		IEnumerable<string> candidatePaths,
		HashSet<string> requiredPaths,
		out bool didAutosaveCandidateScripts,
		out string failureMessage,
		bool allowScriptEditorActivation = true,
		string namespaceReferenceToProtect = ""
	)
	{
		didAutosaveCandidateScripts = false;
		failureMessage = "";

		ScriptEditor scriptEditor = EditorInterface.Singleton?.GetScriptEditor();

		if (scriptEditor == null)
			return true;

		List<string> normalizedCandidatePaths =
			candidatePaths
				?.Where(path => !string.IsNullOrWhiteSpace(path))
				.Select(NormalizeScriptPath)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList()
			?? new List<string>();

		if (normalizedCandidatePaths.Count == 0)
			return true;

		requiredPaths ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		if (!allowScriptEditorActivation)
		{
			HashSet<string> targetPaths = normalizedCandidatePaths.ToHashSet(
				StringComparer.OrdinalIgnoreCase
			);

			if (
				!TryGetOpenScriptEditorsByIndexedScriptEditorPaths(
					scriptEditor,
					targetPaths,
					out Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath,
					out string editorFailureMessage,
					requiredPaths
				)
			)
			{
				failureMessage = editorFailureMessage;
				return false;
			}

			foreach (OpenScriptEditorBuffer openEditor in openEditorsByPath.Values)
			{
				bool isRequiredScript = requiredPaths.Contains(openEditor.Path);

				if (
					!TryAutosaveOpenScriptEditorBufferIfNeeded(
						openEditor,
						isRequiredScript,
						out bool didAutosaveOpenEditor,
						out string autosaveFailureMessage
					)
				)
				{
					if (isRequiredScript)
					{
						failureMessage = autosaveFailureMessage;
						return false;
					}

					DebugLog(
						$"Refactor Namespace pre-scan skipped autosave for open candidate '{openEditor.Path}': {autosaveFailureMessage}"
					);
					continue;
				}

				if (didAutosaveOpenEditor)
					didAutosaveCandidateScripts = true;
			}

			if (
				TryFindUnmatchedOpenScriptEditorUsingReference(
					scriptEditor,
					openEditorsByPath,
					namespaceReferenceToProtect,
					out string unmatchedUsingFailureMessage
				)
			)
			{
				failureMessage = unmatchedUsingFailureMessage;
				return false;
			}

			return true;
		}

		foreach (string candidatePath in normalizedCandidatePaths)
		{
			bool isRequiredScript = requiredPaths.Contains(candidatePath);

			if (!IsScriptOpen(scriptEditor, candidatePath))
				continue;

			if (
				!TryGetOpenScriptEditorByActivatingPath(
					candidatePath,
					out OpenScriptEditorBuffer openEditor,
					out string editorFailureMessage
				)
			)
			{
				if (isRequiredScript)
				{
					failureMessage = editorFailureMessage;
					return false;
				}

				DebugLog(
					$"Refactor Namespace pre-scan skipped autosave for open candidate '{candidatePath}': {editorFailureMessage}"
				);
				continue;
			}

			if (
				!TryAutosaveOpenScriptEditorBufferIfNeeded(
					openEditor,
					isRequiredScript,
					out bool didAutosaveOpenEditor,
					out string autosaveFailureMessage
				)
			)
			{
				if (isRequiredScript)
				{
					failureMessage = autosaveFailureMessage;
					return false;
				}

				DebugLog(
					$"Refactor Namespace pre-scan skipped autosave for open candidate '{candidatePath}': {autosaveFailureMessage}"
				);
				continue;
			}

			if (didAutosaveOpenEditor)
				didAutosaveCandidateScripts = true;
		}

		return true;
	}

	private bool TryAutosaveOpenRefactorNamespaceCandidateScriptsBeforeBuild(
		string metadata,
		out bool didAutosaveCandidateScripts,
		out string failureMessage
	)
	{
		didAutosaveCandidateScripts = false;
		failureMessage = "";

		ScriptEditor scriptEditor = EditorInterface.Singleton?.GetScriptEditor();

		if (scriptEditor == null)
			return true;

		HashSet<string> candidatePaths = GetRefactorNamespaceCandidateScriptPaths(metadata);
		string selectedScriptPath = "";

		if (!string.IsNullOrWhiteSpace(metadata) && metadata.StartsWith("script::"))
		{
			string selectedEntry = GetEntryFromMetadata(metadata);
			selectedScriptPath = NormalizeScriptPath(GetScriptPathFromEntry(selectedEntry));
		}

		if (candidatePaths.Count == 0)
			return true;

		foreach (string candidatePath in candidatePaths)
		{
			bool isSelectedScript = string.Equals(
				candidatePath,
				selectedScriptPath,
				StringComparison.OrdinalIgnoreCase
			);

			if (!IsScriptOpen(scriptEditor, candidatePath))
				continue;

			if (
				!TryGetOpenScriptEditorByActivatingPath(
					candidatePath,
					out OpenScriptEditorBuffer openEditor,
					out string editorFailureMessage
				)
			)
			{
				if (isSelectedScript)
				{
					failureMessage = editorFailureMessage;
					return false;
				}

				DebugLog(
					$"Refactor Namespace pre-scan skipped autosave for open candidate '{candidatePath}': {editorFailureMessage}"
				);
				continue;
			}

			if (
				!TryAutosaveOpenScriptEditorBufferIfNeeded(
					openEditor,
					isSelectedScript,
					out bool didAutosaveOpenEditor,
					out string autosaveFailureMessage
				)
			)
			{
				if (isSelectedScript)
				{
					failureMessage = autosaveFailureMessage;
					return false;
				}

				DebugLog(
					$"Refactor Namespace pre-scan skipped autosave for open candidate '{candidatePath}': {autosaveFailureMessage}"
				);
				continue;
			}

			if (didAutosaveOpenEditor)
				didAutosaveCandidateScripts = true;
		}

		return true;
	}

	private HashSet<string> GetRefactorNamespaceCandidateScriptPaths(string metadata)
	{
		HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);

		if (!string.IsNullOrWhiteSpace(metadata) && metadata.StartsWith("script::"))
		{
			string selectedEntry = GetEntryFromMetadata(metadata);
			string selectedScriptPath = NormalizeScriptPath(GetScriptPathFromEntry(selectedEntry));

			if (
				!string.IsNullOrWhiteSpace(selectedScriptPath)
				&& selectedScriptPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
			)
			{
				result.Add(selectedScriptPath);
			}
		}

		foreach (string linkedScriptPath in GetLinkedCSharpFilePaths())
		{
			string scriptPath = NormalizeScriptPath(linkedScriptPath);

			if (!string.IsNullOrWhiteSpace(scriptPath))
				result.Add(scriptPath);
		}

		return result;
	}

	private void DebugDumpRefactorNamespaceEditorState(string metadata, string label)
	{
		if (!DebugState)
			return;

		ScriptEditor scriptEditor = EditorInterface.Singleton?.GetScriptEditor();
		string targetPath = "";

		if (!string.IsNullOrWhiteSpace(metadata) && metadata.StartsWith("script::"))
		{
			string selectedEntry = GetEntryFromMetadata(metadata);
			targetPath = NormalizeScriptPath(GetScriptPathFromEntry(selectedEntry));
		}

		if (scriptEditor == null)
		{
			DebugLog(
				$"Refactor Namespace editor diagnostics ({label}): target='{targetPath}', scriptEditor=<null>"
			);
			return;
		}

		Godot.Collections.Array<Script> openScripts = scriptEditor.GetOpenScripts();
		Godot.Collections.Array<ScriptEditorBase> openScriptEditors =
			scriptEditor.GetOpenScriptEditors();
		Script currentScript = scriptEditor.GetCurrentScript();
		ScriptEditorBase currentEditor = scriptEditor.GetCurrentEditor();
		Control currentBaseEditor = currentEditor?.GetBaseEditor();

		DebugLog(
			$"Refactor Namespace editor diagnostics ({label}): target='{targetPath}', openScripts={openScripts.Count}, openScriptEditors={openScriptEditors.Count}, currentScript='{NormalizeScriptPath(currentScript?.ResourcePath)}', currentEditorId={GetGodotInstanceId(currentEditor)}, currentBaseType='{currentBaseEditor?.GetType().Name ?? "<null>"}', currentBaseId={GetGodotInstanceId(currentBaseEditor)}"
		);

		for (int i = 0; i < openScripts.Count; i++)
		{
			Script openScript = openScripts[i];
			DebugLog(
				$"Refactor Namespace open script [{i}]: path='{NormalizeScriptPath(openScript?.ResourcePath)}', id={GetGodotInstanceId(openScript)}"
			);
		}

		for (int i = 0; i < openScriptEditors.Count; i++)
		{
			ScriptEditorBase openScriptEditor = openScriptEditors[i];
			Control baseEditor = openScriptEditor?.GetBaseEditor();
			string textInfo = "";

			if (baseEditor is TextEdit textEditor)
			{
				string text = textEditor.Text ?? "";
				textInfo = $", textLength={text.Length}, textHash={text.GetHashCode()}";
			}

			DebugLog(
				$"Refactor Namespace open editor [{i}]: editorId={GetGodotInstanceId(openScriptEditor)}, baseType='{baseEditor?.GetType().Name ?? "<null>"}', baseId={GetGodotInstanceId(baseEditor)}{textInfo}"
			);
		}
	}

	private void RefreshOpenScriptEditorBuffersAfterRefactorNamespaceDeferred(
		string scriptPathPayload
	)
	{
		Dictionary<string, string> updatedTextsByPath = ParseScriptPathPayload(scriptPathPayload)
			.Where(FileAccess.FileExists)
			.ToDictionary(NormalizeScriptPath, ReadTextFile, StringComparer.OrdinalIgnoreCase);

		if (updatedTextsByPath.Count == 0)
		{
			_deferredRefactorNamespaceOriginalTextsByPath.Clear();
			return;
		}

		Dictionary<string, string> originalTextsByPath = new(StringComparer.OrdinalIgnoreCase);

		foreach (string scriptPath in updatedTextsByPath.Keys)
		{
			if (
				_deferredRefactorNamespaceOriginalTextsByPath.TryGetValue(
					scriptPath,
					out string originalText
				)
			)
				originalTextsByPath[scriptPath] = originalText;
			else
				originalTextsByPath[scriptPath] = updatedTextsByPath[scriptPath];
		}

		_deferredRefactorNamespaceOriginalTextsByPath.Clear();

		Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath = GetOpenScriptEditorsByPath(
			originalTextsByPath,
			updatedTextsByPath,
			out _
		);

		ApplyTextToOpenScriptEditors(openEditorsByPath, updatedTextsByPath);
	}

	private IEnumerable<string> GetLinkedCSharpFilePaths()
	{
		return _systems
			.Values.SelectMany(entries => entries)
			.Where(IsScriptEntry)
			.Select(GetScriptPathFromEntry)
			.Where(path =>
				!string.IsNullOrWhiteSpace(path)
				&& path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
			)
			.Distinct(StringComparer.OrdinalIgnoreCase);
	}

	private static bool IsScriptEntry(string entry)
	{
		if (string.IsNullOrWhiteSpace(entry))
			return false;

		string entryWithoutLinkedScene = GetEntryWithoutLinkedScene(entry);
		string pathPart = entryWithoutLinkedScene.Contains("|")
			? entryWithoutLinkedScene.Split("|")[1]
			: entryWithoutLinkedScene;

		return !pathPart.StartsWith(SceneEntryMarker)
			&& pathPart.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
	}

	#endregion
}
#endif
