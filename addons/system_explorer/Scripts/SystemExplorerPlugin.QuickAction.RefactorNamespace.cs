#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

public partial class SystemExplorerPlugin
{
	#region Quick Actions - Refactor Namespace
	private static readonly Vector2I RefactorNamespaceDialogSize = new(520, 285);

	private static readonly Regex NamespaceDeclarationRegex = new(
		@"(?m)^(\s*namespace\s+)([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)(\s*(?:;|\{))",
		RegexOptions.Compiled
	);

	private static readonly Regex NamespaceIdentifierRegex = new(
		@"^[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*$",
		RegexOptions.Compiled
	);

	private Dictionary<string, string> _deferredRefactorNamespaceOriginalTextsByPath = new(
		StringComparer.OrdinalIgnoreCase
	);

	private readonly struct RefactorNamespaceOpenEditor
	{
		public RefactorNamespaceOpenEditor(string path, TextEdit textEditor)
		{
			Path = NormalizeRefactorNamespacePath(path);
			TextEditor = textEditor;
		}

		public string Path { get; }
		public TextEdit TextEditor { get; }
	}

	private void OpenRefactorNamespaceDialog()
	{
		if (string.IsNullOrWhiteSpace(_pendingRenameMetadata))
			return;

		if (_pendingRenameMetadata.StartsWith("system::") || _pendingRenameMetadata.StartsWith("folder::"))
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
			DebugLog($"Refactor Namespace found no namespace in '{scriptPath}'. Opening add-namespace dialog.");
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
			NormalizeRefactorNamespacePath(scriptPath),
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
				DebugLog("Refactor Namespace add cancelled: new namespace must be a valid C# namespace name.");
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

		Dictionary<string, RefactorNamespaceOpenEditor> openEditorsByPath;

		if (
			!TryGetOpenRefactorNamespaceEditorsByActivatingPaths(
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
			!TryAutosaveRefactorNamespaceOpenEditorsIfNeeded(
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
				!TryGetOpenRefactorNamespaceEditorsByActivatingPaths(
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

		if (HasUnsavedRefactorNamespaceFiles(openEditorsByPath, out string unsavedScriptList))
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
				RefreshGodotAfterRefactorNamespace(pendingWrites.Keys);
				return false;
			}
		}

		ApplyRefactorNamespaceTextToOpenEditors(openEditorsByPath, pendingWrites);
		RefreshGodotAfterRefactorNamespace(pendingWrites.Keys);

		_deferredRefactorNamespaceOriginalTextsByPath = new Dictionary<string, string>(
			originalTextsByPath,
			StringComparer.OrdinalIgnoreCase
		);

		string changedScriptPathPayload = BuildRefactorNamespacePathPayload(pendingWrites.Keys);
		RestoreRefactorNamespaceTargetScriptEditor(selectedScriptPath);

		CallDeferred(
			nameof(RefreshOpenScriptEditorBuffersAfterRefactorNamespaceDeferred),
			changedScriptPathPayload
		);
		CallDeferred(
			nameof(RestoreRefactorNamespaceTargetScriptEditorDeferred),
			selectedScriptPath
		);
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

		List<string> targetScriptPaths = scriptPaths
			?.Where(path => !string.IsNullOrWhiteSpace(path))
			.Select(NormalizeRefactorNamespacePath)
			.Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList() ?? new List<string>();

		if (targetScriptPaths.Count == 0)
		{
			DebugLog($"{operationName} cancelled: no C# scripts were selected.");
			return false;
		}

		if (
			!TryAutosaveOpenRefactorNamespaceCandidateScriptsBeforeBuild(
				targetScriptPaths,
				targetScriptPaths.ToHashSet(StringComparer.OrdinalIgnoreCase),
				out bool didAutosaveCandidateScripts,
				out string candidateAutosaveFailureMessage
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
			operationName
		);
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
				.Select(NormalizeRefactorNamespacePath)
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
				DebugLog($"Refactor Namespace add skipped '{scriptPath}' because it already has a namespace.");
				continue;
			}

			string updatedScriptText = AddNamespaceBlockToScriptText(
				scriptText,
				newNamespace,
				out bool namespaceAdded
			);

			if (!namespaceAdded)
			{
				DebugLog($"Refactor Namespace add skipped '{scriptPath}' because the namespace block could not be inserted.");
				continue;
			}

			if (string.IsNullOrWhiteSpace(selectedScriptPath))
				selectedScriptPath = scriptPath;

			originalTextsByPath[scriptPath] = scriptText;
			pendingWrites[scriptPath] = updatedScriptText;
		}

		if (pendingWrites.Count == 0)
		{
			DebugLog("Refactor Namespace add cancelled: no scripts without namespace could be updated.");
			return false;
		}

		return true;
	}

	private bool ApplyRefactorNamespacePendingWrites(
		string selectedScriptPath,
		Dictionary<string, string> originalTextsByPath,
		Dictionary<string, string> pendingWrites,
		string operationName
	)
	{
		if (pendingWrites == null || pendingWrites.Count == 0)
		{
			DebugLog($"{operationName} cancelled: no file changes were produced.");
			return false;
		}

		Dictionary<string, RefactorNamespaceOpenEditor> openEditorsByPath;

		if (
			!TryGetOpenRefactorNamespaceEditorsByActivatingPaths(
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

		if (
			!TryAutosaveRefactorNamespaceOpenEditorsIfNeeded(
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

		if (HasUnsavedRefactorNamespaceFiles(openEditorsByPath, out string unsavedScriptList))
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
				RefreshGodotAfterRefactorNamespace(pendingWrites.Keys);
				return false;
			}
		}

		ApplyRefactorNamespaceTextToOpenEditors(openEditorsByPath, pendingWrites);
		RefreshGodotAfterRefactorNamespace(pendingWrites.Keys);

		_deferredRefactorNamespaceOriginalTextsByPath = new Dictionary<string, string>(
			originalTextsByPath,
			StringComparer.OrdinalIgnoreCase
		);

		string changedScriptPathPayload = BuildRefactorNamespacePathPayload(pendingWrites.Keys);

		if (!string.IsNullOrWhiteSpace(selectedScriptPath))
			RestoreRefactorNamespaceTargetScriptEditor(selectedScriptPath);

		CallDeferred(
			nameof(RefreshOpenScriptEditorBuffersAfterRefactorNamespaceDeferred),
			changedScriptPathPayload
		);

		if (!string.IsNullOrWhiteSpace(selectedScriptPath))
		{
			CallDeferred(
				nameof(RestoreRefactorNamespaceTargetScriptEditorDeferred),
				selectedScriptPath
			);
		}

		CallDeferred(nameof(ReleaseTreeFocusAfterNavigation));

		DebugLogOperation(
			$"{operationName} Completed",
			$"Updated {pendingWrites.Count} file(s)."
		);
		return true;
	}

	private void RestoreRefactorNamespaceTargetScriptEditorDeferred(string scriptPath)
	{
		RestoreRefactorNamespaceTargetScriptEditor(scriptPath);
	}

	private void RestoreRefactorNamespaceTargetScriptEditor(string scriptPath)
	{
		string normalizedPath = NormalizeRefactorNamespacePath(scriptPath);

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
		selectedScriptPath = NormalizeRefactorNamespacePath(GetScriptPathFromEntry(selectedEntry));

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
			string scriptPath = NormalizeRefactorNamespacePath(linkedScriptPath);

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
		out string failureMessage
	)
	{
		didAutosaveCandidateScripts = false;
		failureMessage = "";

		ScriptEditor scriptEditor = EditorInterface.Singleton?.GetScriptEditor();

		if (scriptEditor == null)
			return true;

		List<string> normalizedCandidatePaths = candidatePaths
			?.Where(path => !string.IsNullOrWhiteSpace(path))
			.Select(NormalizeRefactorNamespacePath)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList() ?? new List<string>();

		if (normalizedCandidatePaths.Count == 0)
			return true;

		requiredPaths ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (string candidatePath in normalizedCandidatePaths)
		{
			bool isRequiredScript = requiredPaths.Contains(candidatePath);

			if (!IsRefactorNamespaceScriptOpen(scriptEditor, candidatePath))
				continue;

			if (
				!TryGetOpenRefactorNamespaceEditorByActivatingPath(
					candidatePath,
					out RefactorNamespaceOpenEditor openEditor,
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
				!TryAutosaveRefactorNamespaceOpenEditorIfNeeded(
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
			selectedScriptPath = NormalizeRefactorNamespacePath(
				GetScriptPathFromEntry(selectedEntry)
			);
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

			if (!IsRefactorNamespaceScriptOpen(scriptEditor, candidatePath))
				continue;

			if (
				!TryGetOpenRefactorNamespaceEditorByActivatingPath(
					candidatePath,
					out RefactorNamespaceOpenEditor openEditor,
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
				!TryAutosaveRefactorNamespaceOpenEditorIfNeeded(
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
			string selectedScriptPath = NormalizeRefactorNamespacePath(
				GetScriptPathFromEntry(selectedEntry)
			);

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
			string scriptPath = NormalizeRefactorNamespacePath(linkedScriptPath);

			if (!string.IsNullOrWhiteSpace(scriptPath))
				result.Add(scriptPath);
		}

		return result;
	}

	private bool TryGetOpenRefactorNamespaceEditorsByActivatingPaths(
		IEnumerable<string> scriptPaths,
		bool failIfOpenEditorCannotBeMatched,
		out Dictionary<string, RefactorNamespaceOpenEditor> openEditorsByPath,
		out string failureMessage
	)
	{
		openEditorsByPath = new Dictionary<string, RefactorNamespaceOpenEditor>(
			StringComparer.OrdinalIgnoreCase
		);
		failureMessage = "";

		ScriptEditor scriptEditor = EditorInterface.Singleton?.GetScriptEditor();

		if (scriptEditor == null || scriptPaths == null)
			return true;

		foreach (
			string scriptPath in scriptPaths
				.Where(path => !string.IsNullOrWhiteSpace(path))
				.Select(NormalizeRefactorNamespacePath)
				.Distinct(StringComparer.OrdinalIgnoreCase)
		)
		{
			if (!IsRefactorNamespaceScriptOpen(scriptEditor, scriptPath))
				continue;

			if (
				TryGetOpenRefactorNamespaceEditorByActivatingPath(
					scriptPath,
					out RefactorNamespaceOpenEditor openEditor,
					out string editorFailureMessage
				)
			)
			{
				openEditorsByPath[scriptPath] = openEditor;
				continue;
			}

			if (failIfOpenEditorCannotBeMatched)
			{
				failureMessage = editorFailureMessage;
				return false;
			}

			DebugLog(
				$"Refactor Namespace could not match open editor for '{scriptPath}': {editorFailureMessage}"
			);
		}

		return true;
	}

	private bool TryGetOpenRefactorNamespaceEditorByActivatingPath(
		string scriptPath,
		out RefactorNamespaceOpenEditor openEditor,
		out string failureMessage
	)
	{
		openEditor = default;
		failureMessage = "";

		string normalizedPath = NormalizeRefactorNamespacePath(scriptPath);

		if (string.IsNullOrWhiteSpace(normalizedPath))
		{
			failureMessage =
				"Refactor Namespace cancelled: an empty script path could not be matched to an open editor buffer.";
			return false;
		}

		ScriptEditor scriptEditor = EditorInterface.Singleton?.GetScriptEditor();
		EditorInterface editorInterface = EditorInterface.Singleton;

		if (scriptEditor == null || editorInterface == null)
			return true;

		if (!TryGetOpenRefactorNamespaceScript(scriptEditor, normalizedPath, out Script openScript))
			return true;

		editorInterface.EditScript(openScript);

		Script currentScript = scriptEditor.GetCurrentScript();
		string currentScriptPath = NormalizeRefactorNamespacePath(currentScript?.ResourcePath);
		ScriptEditorBase currentEditor = scriptEditor.GetCurrentEditor();
		Control baseEditor = currentEditor?.GetBaseEditor();

		DebugLog(
			$"Refactor Namespace activate '{normalizedPath}': current='{currentScriptPath}', currentEditorId={GetRefactorNamespaceInstanceId(currentEditor)}, baseType='{baseEditor?.GetType().Name ?? "<null>"}', baseId={GetRefactorNamespaceInstanceId(baseEditor)}, matches={string.Equals(currentScriptPath, normalizedPath, StringComparison.OrdinalIgnoreCase)}"
		);

		if (!string.Equals(currentScriptPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
		{
			failureMessage =
				$"Refactor Namespace cancelled: System Explorer could not safely activate the open editor buffer for '{normalizedPath}' before refactoring.";
			return false;
		}

		if (baseEditor is not TextEdit textEditor)
		{
			failureMessage =
				$"Refactor Namespace cancelled: System Explorer could not access the open text editor buffer for '{normalizedPath}' before refactoring.";
			return false;
		}

		openEditor = new RefactorNamespaceOpenEditor(normalizedPath, textEditor);
		return true;
	}

	private static bool TryGetOpenRefactorNamespaceScript(
		ScriptEditor scriptEditor,
		string scriptPath,
		out Script openScript
	)
	{
		openScript = null;

		if (scriptEditor == null || string.IsNullOrWhiteSpace(scriptPath))
			return false;

		foreach (Script candidateScript in scriptEditor.GetOpenScripts())
		{
			if (candidateScript == null)
				continue;

			string candidatePath = NormalizeRefactorNamespacePath(candidateScript.ResourcePath);

			if (!string.Equals(candidatePath, scriptPath, StringComparison.OrdinalIgnoreCase))
				continue;

			openScript = candidateScript;
			return true;
		}

		return false;
	}

	private static bool IsRefactorNamespaceScriptOpen(ScriptEditor scriptEditor, string scriptPath)
	{
		return TryGetOpenRefactorNamespaceScript(scriptEditor, scriptPath, out _);
	}

	private static bool TryAutosaveRefactorNamespaceOpenEditorIfNeeded(
		RefactorNamespaceOpenEditor openEditor,
		bool failOnSavedDiskMismatch,
		out bool didAutosaveOpenEditor,
		out string failureMessage
	)
	{
		didAutosaveOpenEditor = false;
		failureMessage = "";

		TextEdit textEditor = openEditor.TextEditor;
		string scriptPath = openEditor.Path;

		if (textEditor == null || string.IsNullOrWhiteSpace(scriptPath))
			return true;

		string editorText = textEditor.Text ?? "";
		bool isUnsaved = textEditor.GetVersion() != textEditor.GetSavedVersion();

		if (!isUnsaved)
		{
			string diskText = ReadTextFile(scriptPath);

			if (
				failOnSavedDiskMismatch
				&& !RefactorNamespaceTextsMatchForDiskVerification(editorText, diskText)
			)
			{
				failureMessage =
					$"Refactor Namespace cancelled: the open editor buffer for '{scriptPath}' does not match the file on disk before scanning namespace usages. Save/reopen it before refactoring.";
				return false;
			}

			return true;
		}

		if (!WriteTextFile(scriptPath, editorText))
		{
			failureMessage =
				$"Refactor Namespace cancelled: could not autosave affected script before refactoring '{scriptPath}'. Some script buffers may already have been saved.";
			return false;
		}

		string savedEditorText = ReadTextFile(scriptPath);

		if (!RefactorNamespaceTextsMatchForDiskVerification(savedEditorText, editorText))
		{
			failureMessage =
				$"Refactor Namespace cancelled: autosaved text for '{scriptPath}' did not match the open editor buffer. The namespace refactor was not applied.";
			return false;
		}

		textEditor.TagSavedVersion();
		didAutosaveOpenEditor = true;
		return true;
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
			targetPath = NormalizeRefactorNamespacePath(GetScriptPathFromEntry(selectedEntry));
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
			$"Refactor Namespace editor diagnostics ({label}): target='{targetPath}', openScripts={openScripts.Count}, openScriptEditors={openScriptEditors.Count}, currentScript='{NormalizeRefactorNamespacePath(currentScript?.ResourcePath)}', currentEditorId={GetRefactorNamespaceInstanceId(currentEditor)}, currentBaseType='{currentBaseEditor?.GetType().Name ?? "<null>"}', currentBaseId={GetRefactorNamespaceInstanceId(currentBaseEditor)}"
		);

		for (int i = 0; i < openScripts.Count; i++)
		{
			Script openScript = openScripts[i];
			DebugLog(
				$"Refactor Namespace open script [{i}]: path='{NormalizeRefactorNamespacePath(openScript?.ResourcePath)}', id={GetRefactorNamespaceInstanceId(openScript)}"
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
				$"Refactor Namespace open editor [{i}]: editorId={GetRefactorNamespaceInstanceId(openScriptEditor)}, baseType='{baseEditor?.GetType().Name ?? "<null>"}', baseId={GetRefactorNamespaceInstanceId(baseEditor)}{textInfo}"
			);
		}
	}

	private static bool RefactorNamespaceTextsMatchForDiskVerification(string left, string right)
	{
		return NormalizeRefactorNamespaceTextForDiskVerification(left)
			== NormalizeRefactorNamespaceTextForDiskVerification(right);
	}

	private static string NormalizeRefactorNamespaceTextForDiskVerification(string text)
	{
		return (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
	}

	private static string GetRefactorNamespaceInstanceId(GodotObject godotObject)
	{
		return godotObject == null ? "<null>" : godotObject.GetInstanceId().ToString();
	}

	private void RefreshOpenScriptEditorBuffersAfterRefactorNamespaceDeferred(
		string scriptPathPayload
	)
	{
		Dictionary<string, string> updatedTextsByPath = ParseRefactorNamespacePathPayload(
				scriptPathPayload
			)
			.Where(FileAccess.FileExists)
			.ToDictionary(
				NormalizeRefactorNamespacePath,
				ReadTextFile,
				StringComparer.OrdinalIgnoreCase
			);

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

		Dictionary<string, RefactorNamespaceOpenEditor> openEditorsByPath =
			GetOpenRefactorNamespaceEditorsByPath(originalTextsByPath, updatedTextsByPath, out _);

		ApplyRefactorNamespaceTextToOpenEditors(openEditorsByPath, updatedTextsByPath);
	}

	private static Dictionary<
		string,
		RefactorNamespaceOpenEditor
	> GetOpenRefactorNamespaceEditorsByPath(
		Dictionary<string, string> originalTextsByPath,
		Dictionary<string, string> updatedTextsByPath,
		out string unsafeOpenScriptList
	)
	{
		unsafeOpenScriptList = "";
		Dictionary<string, RefactorNamespaceOpenEditor> result = new(
			StringComparer.OrdinalIgnoreCase
		);

		if (
			originalTextsByPath == null
			|| updatedTextsByPath == null
			|| updatedTextsByPath.Count == 0
		)
			return result;

		HashSet<string> targetPaths = updatedTextsByPath
			.Keys.Where(path => !string.IsNullOrWhiteSpace(path))
			.Select(NormalizeRefactorNamespacePath)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		if (targetPaths.Count == 0)
			return result;

		ScriptEditor scriptEditor = EditorInterface.Singleton?.GetScriptEditor();

		if (scriptEditor == null)
			return result;

		HashSet<string> openTargetPaths = GetOpenRefactorNamespaceScriptPaths(
			scriptEditor,
			targetPaths
		);
		HashSet<TextEdit> usedTextEditors = new();

		Script currentScript = scriptEditor.GetCurrentScript();
		string currentScriptPath = NormalizeRefactorNamespacePath(currentScript?.ResourcePath);

		if (targetPaths.Contains(currentScriptPath))
		{
			ScriptEditorBase currentEditor = scriptEditor.GetCurrentEditor();

			if (currentEditor?.GetBaseEditor() is TextEdit currentTextEditor)
				AddRefactorNamespaceOpenEditor(
					result,
					usedTextEditors,
					currentScriptPath,
					currentTextEditor
				);
		}

		List<TextEdit> openTextEditors = GetOpenRefactorNamespaceTextEditors(scriptEditor);
		List<string> unsafeOpenScripts = new();

		List<string> pathsToMatch = openTargetPaths
			.Concat(targetPaths)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		foreach (string targetPath in pathsToMatch)
		{
			if (result.ContainsKey(targetPath))
				continue;

			if (
				!originalTextsByPath.TryGetValue(targetPath, out string originalText)
				|| !updatedTextsByPath.TryGetValue(targetPath, out string updatedText)
			)
			{
				if (openTargetPaths.Contains(targetPath))
					unsafeOpenScripts.Add(targetPath);

				continue;
			}

			List<TextEdit> matchingEditors = openTextEditors
				.Where(textEditor =>
					textEditor != null
					&& !usedTextEditors.Contains(textEditor)
					&& TextEditorMatchesRefactorNamespaceTexts(
						textEditor,
						targetPath,
						originalTextsByPath,
						updatedTextsByPath
					)
				)
				.ToList();

			if (matchingEditors.Count == 1)
			{
				TextEdit matchingEditor = matchingEditors[0];
				int matchingPathCount = pathsToMatch.Count(path =>
					!result.ContainsKey(path)
					&& TextEditorMatchesRefactorNamespaceTexts(
						matchingEditor,
						path,
						originalTextsByPath,
						updatedTextsByPath
					)
				);

				if (matchingPathCount == 1)
				{
					AddRefactorNamespaceOpenEditor(
						result,
						usedTextEditors,
						targetPath,
						matchingEditor
					);
					continue;
				}
			}

			if (matchingEditors.Count > 0 || openTargetPaths.Contains(targetPath))
				unsafeOpenScripts.Add(targetPath);
		}

		unsafeOpenScriptList = string.Join(
			"\n",
			unsafeOpenScripts.Distinct(StringComparer.OrdinalIgnoreCase)
		);
		return result;
	}

	private static bool TextEditorMatchesRefactorNamespaceTexts(
		TextEdit textEditor,
		string scriptPath,
		Dictionary<string, string> originalTextsByPath,
		Dictionary<string, string> updatedTextsByPath
	)
	{
		if (textEditor == null || string.IsNullOrWhiteSpace(scriptPath))
			return false;

		if (
			!originalTextsByPath.TryGetValue(scriptPath, out string originalText)
			|| !updatedTextsByPath.TryGetValue(scriptPath, out string updatedText)
		)
		{
			return false;
		}

		return textEditor.Text == originalText || textEditor.Text == updatedText;
	}

	private static bool TryGetOpenRefactorNamespaceEditorsByIndexedScriptEditorPaths(
		ScriptEditor scriptEditor,
		HashSet<string> targetPaths,
		out Dictionary<string, RefactorNamespaceOpenEditor> openEditorsByPath,
		out string failureMessage
	)
	{
		failureMessage = "";
		openEditorsByPath = new Dictionary<string, RefactorNamespaceOpenEditor>(
			StringComparer.OrdinalIgnoreCase
		);

		if (scriptEditor == null || targetPaths == null || targetPaths.Count == 0)
			return true;

		Godot.Collections.Array<Script> openScripts = scriptEditor.GetOpenScripts();
		Godot.Collections.Array<ScriptEditorBase> openScriptEditors =
			scriptEditor.GetOpenScriptEditors();

		if (openScripts.Count != openScriptEditors.Count)
		{
			List<string> openTargetPaths = new();

			foreach (Script openScript in openScripts)
			{
				if (openScript == null)
					continue;

				string openScriptPath = NormalizeRefactorNamespacePath(openScript.ResourcePath);

				if (
					targetPaths.Contains(openScriptPath)
					&& !openTargetPaths.Contains(openScriptPath, StringComparer.OrdinalIgnoreCase)
				)
				{
					openTargetPaths.Add(openScriptPath);
				}
			}

			if (openTargetPaths.Count == 0)
				return true;

			failureMessage =
				$"Refactor Namespace cancelled: Godot reported {openScripts.Count} open script(s) but {openScriptEditors.Count} open script editor(s), so System Explorer could not safely autosave open script buffer(s) before scanning namespace usages:\n{string.Join("\n", openTargetPaths)}";
			return false;
		}

		HashSet<TextEdit> usedTextEditors = new();

		for (int i = 0; i < openScripts.Count; i++)
		{
			Script openScript = openScripts[i];

			if (openScript == null)
				continue;

			string scriptPath = NormalizeRefactorNamespacePath(openScript.ResourcePath);

			if (!targetPaths.Contains(scriptPath))
				continue;

			if (!scriptPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
				continue;

			Control baseEditor = openScriptEditors[i]?.GetBaseEditor();

			if (baseEditor is not TextEdit textEditor)
			{
				failureMessage =
					$"Refactor Namespace cancelled: System Explorer could not access the open script editor buffer for '{scriptPath}' before scanning namespace usages.";
				return false;
			}

			if (usedTextEditors.Contains(textEditor))
			{
				failureMessage =
					$"Refactor Namespace cancelled: System Explorer found the same open script editor buffer for more than one script while preparing namespace refactor '{scriptPath}'.";
				return false;
			}

			if (
				openEditorsByPath.TryGetValue(
					scriptPath,
					out RefactorNamespaceOpenEditor existingOpenEditor
				)
				&& existingOpenEditor.TextEditor != textEditor
			)
			{
				failureMessage =
					$"Refactor Namespace cancelled: System Explorer found duplicate open script editor buffers for '{scriptPath}'. Save/reopen it before refactoring.";
				return false;
			}

			if (
				!RefactorNamespaceIndexedEditorPairLooksSafe(
					openScript,
					scriptPath,
					textEditor,
					out string unsafePairReason
				)
			)
			{
				failureMessage = unsafePairReason;
				return false;
			}

			openEditorsByPath[scriptPath] = new RefactorNamespaceOpenEditor(scriptPath, textEditor);
			usedTextEditors.Add(textEditor);
		}

		return true;
	}

	private static bool RefactorNamespaceIndexedEditorPairLooksSafe(
		Script openScript,
		string scriptPath,
		TextEdit textEditor,
		out string failureMessage
	)
	{
		failureMessage = "";

		if (openScript == null || textEditor == null || string.IsNullOrWhiteSpace(scriptPath))
		{
			failureMessage =
				"Refactor Namespace cancelled: an open script editor buffer could not be matched safely before scanning namespace usages.";
			return false;
		}

		string editorText = textEditor.Text ?? "";
		string diskText = ReadTextFile(scriptPath);

		if (textEditor.GetVersion() == textEditor.GetSavedVersion())
		{
			if (editorText == diskText)
				return true;

			failureMessage =
				$"Refactor Namespace cancelled: the open editor buffer for '{scriptPath}' does not match the file on disk before scanning namespace usages. Save/reopen it before refactoring.";
			return false;
		}

		return true;
	}

	private static HashSet<string> GetOpenRefactorNamespaceScriptPaths(
		ScriptEditor scriptEditor,
		HashSet<string> targetPaths
	)
	{
		HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);

		if (scriptEditor == null || targetPaths == null || targetPaths.Count == 0)
			return result;

		foreach (Script openScript in scriptEditor.GetOpenScripts())
		{
			if (openScript == null)
				continue;

			string scriptPath = NormalizeRefactorNamespacePath(openScript.ResourcePath);

			if (targetPaths.Contains(scriptPath))
				result.Add(scriptPath);
		}

		return result;
	}

	private static List<TextEdit> GetOpenRefactorNamespaceTextEditors(ScriptEditor scriptEditor)
	{
		List<TextEdit> result = new();

		if (scriptEditor == null)
			return result;

		foreach (ScriptEditorBase scriptEditorBase in scriptEditor.GetOpenScriptEditors())
		{
			Control baseEditor = scriptEditorBase?.GetBaseEditor();

			if (baseEditor is TextEdit textEditor && !result.Contains(textEditor))
				result.Add(textEditor);
		}

		return result;
	}

	private static void AddRefactorNamespaceOpenEditor(
		Dictionary<string, RefactorNamespaceOpenEditor> openEditorsByPath,
		HashSet<TextEdit> usedTextEditors,
		string scriptPath,
		TextEdit textEditor
	)
	{
		if (openEditorsByPath == null || usedTextEditors == null || textEditor == null)
			return;

		string normalizedPath = NormalizeRefactorNamespacePath(scriptPath);

		if (string.IsNullOrWhiteSpace(normalizedPath))
			return;

		openEditorsByPath[normalizedPath] = new RefactorNamespaceOpenEditor(
			normalizedPath,
			textEditor
		);
		usedTextEditors.Add(textEditor);
	}

	private static bool HasUnsavedRefactorNamespaceFiles(
		Dictionary<string, RefactorNamespaceOpenEditor> openEditorsByPath,
		out string unsavedScriptList
	)
	{
		unsavedScriptList = "";

		if (openEditorsByPath == null || openEditorsByPath.Count == 0)
			return false;

		List<string> affectedUnsavedFiles = openEditorsByPath
			.Values.Where(openEditor =>
				openEditor.TextEditor != null
				&& openEditor.TextEditor.GetVersion() != openEditor.TextEditor.GetSavedVersion()
			)
			.Select(openEditor => openEditor.Path)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		if (affectedUnsavedFiles.Count == 0)
			return false;

		unsavedScriptList = string.Join("\n", affectedUnsavedFiles);
		return true;
	}

	private static bool TryAutosaveRefactorNamespaceOpenEditorsIfNeeded(
		Dictionary<string, RefactorNamespaceOpenEditor> openEditorsByPath,
		out bool didAutosaveOpenEditors,
		out string failureMessage
	)
	{
		didAutosaveOpenEditors = false;
		failureMessage = "";

		if (openEditorsByPath == null || openEditorsByPath.Count == 0)
			return true;

		foreach (RefactorNamespaceOpenEditor openEditor in openEditorsByPath.Values)
		{
			if (
				!TryAutosaveRefactorNamespaceOpenEditorIfNeeded(
					openEditor,
					true,
					out bool didAutosaveOpenEditor,
					out string autosaveFailureMessage
				)
			)
			{
				failureMessage = autosaveFailureMessage;
				return false;
			}

			if (didAutosaveOpenEditor)
				didAutosaveOpenEditors = true;
		}

		return true;
	}

	private static void ApplyRefactorNamespaceTextToOpenEditors(
		Dictionary<string, RefactorNamespaceOpenEditor> openEditorsByPath,
		Dictionary<string, string> updatedTextsByPath
	)
	{
		if (openEditorsByPath == null || updatedTextsByPath == null || openEditorsByPath.Count == 0)
			return;

		foreach (
			KeyValuePair<string, RefactorNamespaceOpenEditor> openEditorPair in openEditorsByPath
		)
		{
			if (!updatedTextsByPath.TryGetValue(openEditorPair.Key, out string updatedText))
				continue;

			ApplyRefactorNamespaceTextToOpenEditor(openEditorPair.Value.TextEditor, updatedText);
		}
	}

	private static void ApplyRefactorNamespaceTextToOpenEditor(
		TextEdit textEditor,
		string updatedText
	)
	{
		if (textEditor == null)
			return;

		if (textEditor.Text != updatedText)
			textEditor.Text = updatedText;

		textEditor.ClearUndoHistory();
		textEditor.TagSavedVersion();
	}

	private static string BuildRefactorNamespacePathPayload(IEnumerable<string> scriptPaths)
	{
		if (scriptPaths == null)
			return "";

		return string.Join(
			"\n",
			scriptPaths
				.Where(path => !string.IsNullOrWhiteSpace(path))
				.Select(NormalizeRefactorNamespacePath)
				.Distinct(StringComparer.OrdinalIgnoreCase)
		);
	}

	private static string[] ParseRefactorNamespacePathPayload(string scriptPathPayload)
	{
		if (string.IsNullOrWhiteSpace(scriptPathPayload))
			return Array.Empty<string>();

		return scriptPathPayload
			.Split('\n')
			.Select(NormalizeRefactorNamespacePath)
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private static string NormalizeRefactorNamespacePath(string path)
	{
		return path?.Trim().Replace('\\', '/') ?? "";
	}

	private static void RefreshGodotAfterRefactorNamespace(IEnumerable<string> changedScriptPaths)
	{
		List<string> changedPaths = changedScriptPaths
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Select(NormalizeRefactorNamespacePath)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		EditorFileSystem resourceFilesystem = EditorInterface.Singleton?.GetResourceFilesystem();

		if (resourceFilesystem == null)
			return;

		foreach (string scriptPath in changedPaths)
			resourceFilesystem.UpdateFile(scriptPath);
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

	private static bool IsValidNamespaceName(string namespaceName)
	{
		return !string.IsNullOrWhiteSpace(namespaceName)
			&& NamespaceIdentifierRegex.IsMatch(namespaceName);
	}

	private static string ReadNamespaceFromScript(string scriptPath)
	{
		if (!FileAccess.FileExists(scriptPath))
			return "";

		return GetNamespaceFromText(ReadTextFile(scriptPath));
	}

	private static string GetNamespaceFromText(string scriptText)
	{
		if (string.IsNullOrWhiteSpace(scriptText))
			return "";

		Match match = NamespaceDeclarationRegex.Match(scriptText);
		return match.Success ? match.Groups[2].Value : "";
	}

	private static string ReplaceNamespaceDeclaration(
		string scriptText,
		string oldNamespace,
		string newNamespace,
		out bool changed
	)
	{
		bool didChange = false;

		string updatedText = NamespaceDeclarationRegex.Replace(
			scriptText,
			match =>
			{
				if (didChange || match.Groups[2].Value != oldNamespace)
					return match.Value;

				didChange = true;
				return $"{match.Groups[1].Value}{newNamespace}{match.Groups[3].Value}";
			},
			1
		);

		changed = didChange;
		return updatedText;
	}

	private static string AddNamespaceBlockToScriptText(
		string scriptText,
		string newNamespace,
		out bool changed
	)
	{
		changed = false;

		if (string.IsNullOrWhiteSpace(scriptText) || !IsValidNamespaceName(newNamespace))
			return scriptText ?? "";

		if (!string.IsNullOrWhiteSpace(GetNamespaceFromText(scriptText)))
			return scriptText;

		string normalizedText = (scriptText ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
		int insertionIndex = GetNamespaceInsertionIndexAfterTopUsingDirectives(normalizedText);
		string prefix = normalizedText.Substring(0, insertionIndex).TrimEnd();
		string body = normalizedText.Substring(insertionIndex).TrimStart('\n');

		if (string.IsNullOrWhiteSpace(body))
			return scriptText;

		string indentedBody = IndentNamespaceBody(body);
		StringBuilder builder = new();

		if (!string.IsNullOrWhiteSpace(prefix))
		{
			builder.Append(prefix);
			builder.Append("\n\n");
		}

		builder.Append("namespace ");
		builder.Append(newNamespace);
		builder.Append("\n{\n");
		builder.Append(indentedBody.TrimEnd('\n'));
		builder.Append("\n}\n");

		changed = true;
		return builder.ToString();
	}

	private static int GetNamespaceInsertionIndexAfterTopUsingDirectives(string scriptText)
	{
		if (string.IsNullOrEmpty(scriptText))
			return 0;

		int lineStart = 0;
		int insertionIndex = 0;
		bool foundUsingDirective = false;

		while (lineStart < scriptText.Length)
		{
			int lineEnd = scriptText.IndexOf('\n', lineStart);
			if (lineEnd < 0)
				lineEnd = scriptText.Length;

			string line = scriptText.Substring(lineStart, lineEnd - lineStart);
			string trimmedLine = line.Trim();
			int nextLineStart = lineEnd < scriptText.Length ? lineEnd + 1 : lineEnd;

			if (trimmedLine.Length == 0)
			{
				lineStart = nextLineStart;
				continue;
			}

			if (
				(
					trimmedLine.StartsWith("using ", StringComparison.Ordinal)
					|| trimmedLine.StartsWith("global using ", StringComparison.Ordinal)
				)
				&& trimmedLine.EndsWith(";", StringComparison.Ordinal)
			)
			{
				foundUsingDirective = true;
				insertionIndex = nextLineStart;
				lineStart = nextLineStart;
				continue;
			}

			break;
		}

		return foundUsingDirective ? insertionIndex : 0;
	}

	private static string IndentNamespaceBody(string body)
	{
		string normalizedBody = (body ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
		string[] lines = normalizedBody.Split('\n');
		StringBuilder builder = new();

		for (int i = 0; i < lines.Length; i++)
		{
			string line = lines[i];

			if (line.Length > 0)
				builder.Append('\t');

			builder.Append(line);

			if (i < lines.Length - 1)
				builder.Append('\n');
		}

		return builder.ToString();
	}

	private static string ReplaceUsingStatements(
		string scriptText,
		string oldNamespace,
		string newNamespace,
		out bool changed
	)
	{
		bool didChange = false;

		Regex usingRegex = new(
			$@"(?m)^(\s*using\s+){Regex.Escape(oldNamespace)}(\s*;)",
			RegexOptions.Compiled
		);

		string updatedText = usingRegex.Replace(
			scriptText,
			match =>
			{
				didChange = true;
				return $"{match.Groups[1].Value}{newNamespace}{match.Groups[2].Value}";
			}
		);

		changed = didChange;
		return updatedText;
	}

	private static readonly UTF8Encoding Utf8NoBomEncoding = new(false);

	private static string ReadTextFile(string path)
	{
		string globalPath = GetGlobalTextFilePath(path);

		if (string.IsNullOrWhiteSpace(globalPath) || !System.IO.File.Exists(globalPath))
			return "";

		try
		{
			return System.IO.File.ReadAllText(globalPath, Encoding.UTF8);
		}
		catch
		{
			return "";
		}
	}

	private static bool WriteTextFile(string path, string text)
	{
		string globalPath = GetGlobalTextFilePath(path);

		if (string.IsNullOrWhiteSpace(globalPath))
			return false;

		try
		{
			System.IO.File.WriteAllText(globalPath, text ?? "", Utf8NoBomEncoding);
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static string GetGlobalTextFilePath(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return "";

		if (
			path.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
			|| path.StartsWith("user://", StringComparison.OrdinalIgnoreCase)
		)
		{
			return ProjectSettings.GlobalizePath(path);
		}

		return path;
	}

	#endregion
}
#endif
