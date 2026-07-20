#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SystemExplorer.EditorIntegration.ScriptEditing;

public partial class SystemExplorerPlugin
{
	#region Quick Actions - Beautify
	private bool _isBeautifyingScript;
	private string _pendingBeautifyAfterCSharpierInstallMetadata = "";
	private string[] _pendingBeautifyAfterCSharpierInstallScriptPaths = Array.Empty<string>();
	private bool _pendingBeautifyAfterCSharpierInstallIsBatch;
	private bool _pendingBeautifyAfterCSharpierInstallReleaseTreeFocusAfterNavigation = true;

	private enum BeautifyScriptOperationStatus
	{
		Formatted,
		Unchanged,
		Skipped,
		Failed,
	}

	private readonly struct BeautifyScriptOperationResult
	{
		public BeautifyScriptOperationResult(
			BeautifyScriptOperationStatus status,
			string path,
			string message = ""
		)
		{
			Status = status;
			Path = path;
			Message = message;
		}

		public BeautifyScriptOperationStatus Status { get; }
		public string Path { get; }
		public string Message { get; }
	}

	private readonly struct BeautifyScriptsBatchSummary
	{
		public BeautifyScriptsBatchSummary(int formatted, int unchanged, int skipped, int failed)
		{
			Formatted = formatted;
			Unchanged = unchanged;
			Skipped = skipped;
			Failed = failed;
		}

		public int Formatted { get; }
		public int Unchanged { get; }
		public int Skipped { get; }
		public int Failed { get; }

		public BeautifyScriptsBatchSummary Add(BeautifyScriptOperationResult result)
		{
			return result.Status switch
			{
				BeautifyScriptOperationStatus.Formatted => new BeautifyScriptsBatchSummary(
					Formatted + 1,
					Unchanged,
					Skipped,
					Failed
				),
				BeautifyScriptOperationStatus.Unchanged => new BeautifyScriptsBatchSummary(
					Formatted,
					Unchanged + 1,
					Skipped,
					Failed
				),
				BeautifyScriptOperationStatus.Skipped => new BeautifyScriptsBatchSummary(
					Formatted,
					Unchanged,
					Skipped + 1,
					Failed
				),
				BeautifyScriptOperationStatus.Failed => new BeautifyScriptsBatchSummary(
					Formatted,
					Unchanged,
					Skipped,
					Failed + 1
				),
				_ => this,
			};
		}

		public override string ToString()
		{
			return $"Beautified {Formatted} scripts. {Unchanged} unchanged. {Skipped} skipped. {Failed} failed.";
		}
	}

	private async void OpenBeautifyScriptCSharpierCheckDialog()
	{
		if (_isBeautifyingScript)
			return;

		if (
			string.IsNullOrWhiteSpace(_pendingBeautifyScriptMetadata)
			|| !_pendingBeautifyScriptMetadata.StartsWith("script::")
		)
			return;

		string scriptEntry = GetEntryFromMetadata(_pendingBeautifyScriptMetadata);
		string scriptPath = ScriptPathUtility.Normalize(GetScriptPathFromEntry(scriptEntry));

		if (!FileAccess.FileExists(scriptPath))
		{
			OpenMissingScriptDialog(scriptEntry, scriptPath);
			return;
		}

		if (!TryGetCSharpierCommand(out CSharpierCommand csharpierCommand))
		{
			StorePendingBeautifyAfterCSharpierInstall(
				_pendingBeautifyScriptMetadata,
				new[] { scriptPath },
				isBatch: false
			);
			OpenCSharpierNotInstalledDialogForPendingBeautify();
			return;
		}

		await BeautifyScriptWithCSharpier(scriptPath, csharpierCommand);
	}

	private async void OpenBeautifyScriptPathCSharpierCheckDialog(string scriptPath)
	{
		if (_isBeautifyingScript)
			return;

		string normalizedScriptPath = ScriptPathUtility.Normalize(scriptPath);

		if (string.IsNullOrWhiteSpace(normalizedScriptPath))
			return;

		if (!normalizedScriptPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
			return;

		if (!FileAccess.FileExists(normalizedScriptPath))
			return;

		if (!TryGetCSharpierCommand(out CSharpierCommand csharpierCommand))
		{
			StorePendingBeautifyAfterCSharpierInstall(
				$"editor-script::{normalizedScriptPath}",
				new[] { normalizedScriptPath },
				isBatch: false,
				releaseTreeFocusAfterNavigation: false
			);
			OpenCSharpierNotInstalledDialogForPendingBeautify();
			return;
		}

		await BeautifyScriptWithCSharpier(
			normalizedScriptPath,
			csharpierCommand,
			releaseTreeFocusAfterNavigation: false
		);
	}

	private async void OpenBeautifyScriptsCSharpierCheckDialog()
	{
		if (_isBeautifyingScript)
			return;

		if (
			string.IsNullOrWhiteSpace(_pendingBeautifyScriptMetadata)
			|| (
				!_pendingBeautifyScriptMetadata.StartsWith("system::")
				&& !_pendingBeautifyScriptMetadata.StartsWith("folder::")
			)
		)
			return;

		if (!EnsureSystemsLoadedForTreeOperation("Beautify Scripts"))
			return;

		string systemName = GetSystemNameFromMetadata(_pendingBeautifyScriptMetadata);

		if (!EnsureSystemAvailable(systemName, "Beautify Scripts"))
			return;

		List<string> scriptPaths = GetBeautifyScriptPathsForMetadata(
			_pendingBeautifyScriptMetadata
		);

		DebugPrintBeautify(
			$"Beautify Scripts request: metadata='{_pendingBeautifyScriptMetadata}', system='{systemName}', collected={scriptPaths.Count}"
		);

		foreach (string scriptPath in scriptPaths)
			DebugPrintBeautify($"Beautify Scripts collected path: {scriptPath}");

		if (scriptPaths.Count == 0)
		{
			DebugPrintBeautify(
                "Beautify Scripts summary: Beautified 0 scripts. 0 unchanged. 0 skipped. 0 failed."
			);
			return;
		}

		if (!TryGetCSharpierCommand(out CSharpierCommand csharpierCommand))
		{
			StorePendingBeautifyAfterCSharpierInstall(
				_pendingBeautifyScriptMetadata,
				scriptPaths,
				isBatch: true
			);
			OpenCSharpierNotInstalledDialogForPendingBeautify();
			return;
		}

		await BeautifyScriptsWithCSharpier(scriptPaths, csharpierCommand);
	}

	private Task BeautifyScriptWithCSharpier(string scriptPath, CSharpierCommand csharpierCommand)
	{
		return BeautifyScriptWithCSharpier(
			scriptPath,
			csharpierCommand,
			releaseTreeFocusAfterNavigation: true
		);
	}

	private async Task BeautifyScriptWithCSharpier(
		string scriptPath,
		CSharpierCommand csharpierCommand,
		bool releaseTreeFocusAfterNavigation
	)
	{
		if (_isBeautifyingScript)
			return;

		if (!EnsureSystemsLoadedForTreeOperation("Beautify Script"))
			return;

		_isBeautifyingScript = true;

		try
		{
			BeautifyScriptOperationResult result = await BeautifySingleScriptWithCSharpier(
				scriptPath,
				csharpierCommand,
				"Beautify Script",
				preserveEditorViewState: true
			);

			bool completedWithoutChangesOrFailure =
				result.Status == BeautifyScriptOperationStatus.Formatted
				|| result.Status == BeautifyScriptOperationStatus.Unchanged;

			if (!releaseTreeFocusAfterNavigation && completedWithoutChangesOrFailure)
				SyncSelectionToActiveScriptAfterOperation();

			if (releaseTreeFocusAfterNavigation && completedWithoutChangesOrFailure)
				CallDeferred(nameof(ReleaseTreeFocusAfterNavigation));
		}
		finally
		{
			_isBeautifyingScript = false;
		}
	}

	private async Task BeautifyScriptsWithCSharpier(
		IEnumerable<string> scriptPaths,
		CSharpierCommand csharpierCommand
	)
	{
		if (_isBeautifyingScript)
			return;

		if (!EnsureSystemsLoadedForTreeOperation("Beautify Scripts"))
			return;

		List<string> normalizedScriptPaths = scriptPaths
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Select(ScriptPathUtility.Normalize)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		DebugPrintBeautify(
			$"Beautify Scripts batch started: normalizedCount={normalizedScriptPaths.Count}, command='{GetCSharpierCommandDisplayName(csharpierCommand)}'"
		);

		foreach (string normalizedScriptPath in normalizedScriptPaths)
			DebugPrintBeautify($"Beautify Scripts normalized path: {normalizedScriptPath}");

		_isBeautifyingScript = true;
		BeautifyScriptsBatchSummary summary = new();
		BeginBatchScriptEditorContextPreservation();

		try
		{
			foreach (string scriptPath in normalizedScriptPaths)
			{
				BeautifyScriptOperationResult result = await BeautifySingleScriptWithCSharpier(
					scriptPath,
					csharpierCommand,
					"Beautify Scripts",
					preserveEditorViewState: false
				);

				summary = summary.Add(result);

				DebugPrintBeautify(
					$"Beautify Scripts item result: status={result.Status}, path='{result.Path}', message='{GetDebugTextPreview(result.Message)}'"
				);
			}

			DebugPrintBeautify($"Beautify Scripts summary: {summary}");
			DebugLogger.LogOperation("Beautify Scripts Completed", summary.ToString());
		}
		finally
		{
			EndBatchScriptEditorContextPreservation();
			_isBeautifyingScript = false;
		}
	}

	private async Task<BeautifyScriptOperationResult> BeautifySingleScriptWithCSharpier(
		string scriptPath,
		CSharpierCommand csharpierCommand,
		string operationName,
		bool preserveEditorViewState
	)
	{
		string normalizedScriptPath = ScriptPathUtility.Normalize(scriptPath);

		DebugPrintBeautify(
			$"{operationName} item started: inputPath='{scriptPath}', normalizedPath='{normalizedScriptPath}'"
		);

		if (string.IsNullOrWhiteSpace(normalizedScriptPath))
			return BeautifyScriptSkipped(
				normalizedScriptPath,
				$"{operationName} skipped: empty script path."
			);

		if (!normalizedScriptPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
			return BeautifyScriptSkipped(
				normalizedScriptPath,
				$"{operationName} skipped non-C# file '{normalizedScriptPath}'."
			);

		if (!FileAccess.FileExists(normalizedScriptPath))
			return BeautifyScriptSkipped(
				normalizedScriptPath,
				$"{operationName} skipped missing script '{normalizedScriptPath}'."
			);

		string globalScriptPathForDebug = ProjectSettings.GlobalizePath(normalizedScriptPath);
		DebugPrintBeautify(
			$"{operationName} item path check: resExists={FileAccess.FileExists(normalizedScriptPath)}, globalPath='{globalScriptPathForDebug}', globalExists={System.IO.File.Exists(globalScriptPathForDebug)}"
		);

		string diskTextBeforeSync = ScriptTextFileService.ReadText(normalizedScriptPath);
		DebugPrintBeautify(
			$"{operationName} item disk read: path='{normalizedScriptPath}', originalLength={GetDebugLength(diskTextBeforeSync)}"
		);

		Dictionary<string, string> originalTextsByPath = new(StringComparer.OrdinalIgnoreCase)
		{
			[normalizedScriptPath] = diskTextBeforeSync,
		};
		Dictionary<string, string> pendingTextByPath = new(StringComparer.OrdinalIgnoreCase)
		{
			[normalizedScriptPath] = diskTextBeforeSync,
		};

		ScriptEditorBufferLookupResult lookupResult =
			OpenScriptEditorBufferLocator.LocateByScriptTextsWithoutActivation(
				EditorInterface.Singleton?.GetScriptEditor(),
				originalTextsByPath,
				pendingTextByPath
			);

		Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath =
			lookupResult.OpenEditorsByPath;
		string unsafeOpenScriptList = string.Join("\n", lookupResult.UnsafeOpenScriptPaths);

		DebugPrintBeautify(
			$"{operationName} item open editor scan: matchedOpenEditors={openEditorsByPath.Count}, unsafe='{GetDebugTextPreview(unsafeOpenScriptList)}'"
		);

		if (!string.IsNullOrWhiteSpace(unsafeOpenScriptList))
			return BeautifyScriptSkipped(
				normalizedScriptPath,
				$"{operationName} skipped: System Explorer could not safely match this open script editor buffer. Save/reopen it before formatting:\n{unsafeOpenScriptList}"
			);

		bool didAutosaveOpenEditor = false;

		if (
			!TryAutosaveBeautifyOpenEditorIfNeeded(
				normalizedScriptPath,
				diskTextBeforeSync,
				openEditorsByPath,
				out string originalText,
				out didAutosaveOpenEditor,
				out string autosaveFailureMessage
			)
		)
		{
			return BeautifyScriptSkipped(
				normalizedScriptPath,
				string.IsNullOrWhiteSpace(autosaveFailureMessage)
					? $"{operationName} skipped '{normalizedScriptPath}' because the open editor buffer could not be autosaved safely."
					: autosaveFailureMessage
			);
		}

		DebugPrintBeautify(
			$"{operationName} item autosave result: didAutosave={didAutosaveOpenEditor}, originalLength={GetDebugLength(originalText)}"
		);

		originalTextsByPath[normalizedScriptPath] = originalText;
		pendingTextByPath[normalizedScriptPath] = originalText;

		CSharpierFormatResult formatResult =
			await FormatScriptWithCSharpierUsingCachedCommandFallback(
				csharpierCommand,
				normalizedScriptPath,
				operationName
			);

		DebugPrintBeautify(
			$"{operationName} item format result: success={formatResult.Success}, formattedLength={GetDebugLength(formatResult.FormattedText)}, message='{GetDebugTextPreview(formatResult.Message)}', invalidateCache={formatResult.ShouldInvalidateCachedCommand}"
		);

		if (!formatResult.Success)
			return BeautifyScriptFailed(normalizedScriptPath, formatResult.Message);

		string csharpierOutput = formatResult.FormattedText;

		if (string.IsNullOrWhiteSpace(csharpierOutput))
		{
			DebugPrintBeautify(
				$"{operationName} item CSharpier returned empty stdout with a successful exit code. Treating as unchanged: {normalizedScriptPath}"
			);
			csharpierOutput = originalText;
		}

		if (IsUnsafeEmptyBeautifyOutput(originalText, csharpierOutput))
			return BeautifyScriptFailed(
				normalizedScriptPath,
				$"{operationName} failed: CSharpier returned empty output for non-empty script '{normalizedScriptPath}'. The file was left unchanged."
			);

		string formattedText = NormalizeFormattedTextLineEndings(csharpierOutput, originalText);

		DebugPrintBeautify(
			$"{operationName} item normalized output: formattedLength={GetDebugLength(formatResult.FormattedText)}, normalizedLength={GetDebugLength(formattedText)}"
		);

		if (IsUnsafeEmptyBeautifyOutput(originalText, formattedText))
			return BeautifyScriptFailed(
				normalizedScriptPath,
				$"{operationName} failed: CSharpier produced empty formatted text for non-empty script '{normalizedScriptPath}'. The file was left unchanged."
			);

		string currentText = ScriptTextFileService.ReadText(normalizedScriptPath);

		if (currentText != originalText)
			return BeautifyScriptFailed(
				normalizedScriptPath,
				$"{operationName} failed: '{normalizedScriptPath}' changed while CSharpier was running. Try again."
			);

		Dictionary<string, string> updatedTextsByPath = new(StringComparer.OrdinalIgnoreCase)
		{
			[normalizedScriptPath] = formattedText,
		};

		lookupResult = OpenScriptEditorBufferLocator.LocateByScriptTextsWithoutActivation(
			EditorInterface.Singleton?.GetScriptEditor(),
			originalTextsByPath,
			updatedTextsByPath
		);
		openEditorsByPath = lookupResult.OpenEditorsByPath;
		unsafeOpenScriptList = string.Join("\n", lookupResult.UnsafeOpenScriptPaths);

		DebugPrintBeautify(
			$"{operationName} item apply editor scan: matchedOpenEditors={openEditorsByPath.Count}, unsafe='{GetDebugTextPreview(unsafeOpenScriptList)}'"
		);

		if (!string.IsNullOrWhiteSpace(unsafeOpenScriptList))
			return BeautifyScriptSkipped(
				normalizedScriptPath,
				$"{operationName} skipped: System Explorer could not safely match this open script editor buffer. Save/reopen it before formatting:\n{unsafeOpenScriptList}"
			);

		IReadOnlyList<string> unsavedPaths =
			OpenScriptEditorBufferBatchService.GetUnsavedPaths(openEditorsByPath?.Values);

		if (unsavedPaths.Count > 0)
		{
			string unsavedScriptList = string.Join("\n", unsavedPaths);
			return BeautifyScriptSkipped(
				normalizedScriptPath,
				$"{operationName} skipped: the selected script changed while CSharpier was running. Try again after saving/retrying:\n{unsavedScriptList}"
			);
		}

		if (
			!ValidateBeautifyOpenEditorStillMatchesDisk(
				normalizedScriptPath,
				originalText,
				openEditorsByPath,
				out string editorValidationFailureMessage
			)
		)
		{
			return BeautifyScriptSkipped(
				normalizedScriptPath,
				string.IsNullOrWhiteSpace(editorValidationFailureMessage)
					? $"{operationName} skipped '{normalizedScriptPath}' because the open editor buffer changed before applying formatted text."
					: editorValidationFailureMessage
			);
		}

		if (formattedText == originalText)
		{
			if (didAutosaveOpenEditor)
			{
				BeautifyEditorViewState unchangedEditorViewState = preserveEditorViewState
					? CaptureBeautifyEditorViewState(normalizedScriptPath, openEditorsByPath)
					: default;

				ScriptResourceRefreshService.RefreshChangedScripts(new[] { normalizedScriptPath });
				RestoreBeautifyEditorViewStateNowAndDeferred(unchangedEditorViewState);
			}

			DebugLogger.LogOperation(
				$"{operationName} Completed",
				$"Already formatted: {normalizedScriptPath}"
			);
			return new BeautifyScriptOperationResult(
				BeautifyScriptOperationStatus.Unchanged,
				normalizedScriptPath
			);
		}

		BeautifyEditorViewState editorViewState = preserveEditorViewState
			? CaptureBeautifyEditorViewState(normalizedScriptPath, openEditorsByPath)
			: default;

		if (
			!TryApplyBeautifyTextToEditorBeforeDiskWrite(
				normalizedScriptPath,
				originalText,
				formattedText,
				openEditorsByPath,
				out string editorApplyFailureMessage
			)
		)
		{
			return BeautifyScriptSkipped(
				normalizedScriptPath,
				string.IsNullOrWhiteSpace(editorApplyFailureMessage)
					? $"{operationName} skipped '{normalizedScriptPath}' because the open editor buffer could not be updated safely."
					: editorApplyFailureMessage
			);
		}

		if (!ScriptTextFileService.WriteText(normalizedScriptPath, formattedText))
		{
			RestoreBeautifyOpenEditorAfterFailedWrite(
				normalizedScriptPath,
				originalText,
				openEditorsByPath
			);
			ScriptResourceRefreshService.RefreshChangedScripts(new[] { normalizedScriptPath });
			RestoreBeautifyEditorViewStateNowAndDeferred(editorViewState);
			return BeautifyScriptFailed(
				normalizedScriptPath,
				$"{operationName} failed while writing '{normalizedScriptPath}'."
			);
		}

		OpenScriptEditorBufferBatchService.ApplyCommittedTexts(
			openEditorsByPath,
			updatedTextsByPath
		);
		ScriptResourceRefreshService.RefreshChangedScripts(new[] { normalizedScriptPath });
		RestoreBeautifyEditorViewStateNowAndDeferred(editorViewState);

		DebugLogger.LogOperation($"{operationName} Completed", normalizedScriptPath);
		return new BeautifyScriptOperationResult(
			BeautifyScriptOperationStatus.Formatted,
			normalizedScriptPath
		);
	}

	private void StorePendingBeautifyAfterCSharpierInstall(
		string metadata,
		IEnumerable<string> scriptPaths,
		bool isBatch,
		bool releaseTreeFocusAfterNavigation = true
	)
	{
		List<string> normalizedScriptPaths = (scriptPaths ?? Array.Empty<string>())
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Select(ScriptPathUtility.Normalize)
			.Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		_pendingBeautifyAfterCSharpierInstallMetadata = metadata ?? "";
		_pendingBeautifyAfterCSharpierInstallScriptPaths = normalizedScriptPaths.ToArray();
		_pendingBeautifyAfterCSharpierInstallIsBatch = isBatch;
		_pendingBeautifyAfterCSharpierInstallReleaseTreeFocusAfterNavigation =
			releaseTreeFocusAfterNavigation;

		DebugPrintBeautify(
			$"Stored pending Beautify after CSharpier install: metadata='{_pendingBeautifyAfterCSharpierInstallMetadata}', isBatch={isBatch}, scriptCount={_pendingBeautifyAfterCSharpierInstallScriptPaths.Length}"
		);
	}

	private bool HasPendingBeautifyAfterCSharpierInstall()
	{
		return !string.IsNullOrWhiteSpace(_pendingBeautifyAfterCSharpierInstallMetadata)
			&& _pendingBeautifyAfterCSharpierInstallScriptPaths.Length > 0;
	}

	private void ClearPendingBeautifyAfterCSharpierInstall(string reason)
	{
		if (!HasPendingBeautifyAfterCSharpierInstall())
			return;

		DebugPrintBeautify($"Cleared pending Beautify after CSharpier install: {reason}");

		_pendingBeautifyAfterCSharpierInstallMetadata = "";
		_pendingBeautifyAfterCSharpierInstallScriptPaths = Array.Empty<string>();
		_pendingBeautifyAfterCSharpierInstallIsBatch = false;
		_pendingBeautifyAfterCSharpierInstallReleaseTreeFocusAfterNavigation = true;
	}

	private void OpenCSharpierNotInstalledDialogForPendingBeautify()
	{
		if (_csharpierNotInstalledDialog == null)
			return;

		int scriptCount = _pendingBeautifyAfterCSharpierInstallScriptPaths.Length;
		string targetDescription = _pendingBeautifyAfterCSharpierInstallIsBatch
			? $"the selected target's {scriptCount} C# script(s)"
			: "the selected C# script";

		_csharpierNotInstalledDialog.DialogText =
			$"To Beautify Scripts you need CSharpier installed.\n\nInstall CSharpier and continue Beautify for {targetDescription}?";
		_csharpierNotInstalledDialog.PopupCentered();
	}

	private async Task<bool> TryRunPendingBeautifyAfterCSharpierInstall(
		CSharpierCommand csharpierCommand
	)
	{
		if (!csharpierCommand.IsValid || !HasPendingBeautifyAfterCSharpierInstall())
			return false;

		bool isBatch = _pendingBeautifyAfterCSharpierInstallIsBatch;
		bool releaseTreeFocusAfterNavigation =
			_pendingBeautifyAfterCSharpierInstallReleaseTreeFocusAfterNavigation;
		string metadata = _pendingBeautifyAfterCSharpierInstallMetadata;
		string[] scriptPaths = _pendingBeautifyAfterCSharpierInstallScriptPaths.ToArray();

		ClearPendingBeautifyAfterCSharpierInstall("running pending Beautify");

		DebugPrintBeautify(
			$"Running pending Beautify after CSharpier install: metadata='{metadata}', isBatch={isBatch}, scriptCount={scriptPaths.Length}"
		);

		if (isBatch)
			await BeautifyScriptsWithCSharpier(scriptPaths, csharpierCommand);
		else
			await BeautifyScriptWithCSharpier(
				scriptPaths[0],
				csharpierCommand,
				releaseTreeFocusAfterNavigation
			);

		return true;
	}

	private List<string> GetBeautifyScriptPathsForMetadata(string metadata)
	{
		List<string> result = new();

		if (string.IsNullOrWhiteSpace(metadata))
			return result;

		string systemName = GetSystemNameFromMetadata(metadata);

		if (
			string.IsNullOrWhiteSpace(systemName)
			|| !_systems.TryGetValue(systemName, out List<string> entries)
		)
			return result;

		string targetFolderPath = metadata.StartsWith("folder::")
			? GetFolderPathFromMetadata(metadata)
			: "";

		foreach (string entry in entries)
		{
			if (!IsBeautifyScriptEntry(entry))
				continue;

			string folderPath = GetFolderPathFromEntry(entry);

			if (
				!string.IsNullOrWhiteSpace(targetFolderPath)
				&& !IsEntryInsideBeautifyFolder(folderPath, targetFolderPath)
			)
			{
				continue;
			}

			string scriptPath = ScriptPathUtility.Normalize(GetScriptPathFromEntry(entry));

			if (scriptPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
				result.Add(scriptPath);
		}

		return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
	}

	private static bool IsBeautifyScriptEntry(string entry)
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

	private static bool IsEntryInsideBeautifyFolder(string entryFolderPath, string targetFolderPath)
	{
		string normalizedEntryFolderPath = ScriptPathUtility.Normalize(entryFolderPath).Trim('/');
		string normalizedTargetFolderPath = ScriptPathUtility.Normalize(targetFolderPath).Trim('/');

		return normalizedEntryFolderPath.Equals(
				normalizedTargetFolderPath,
				StringComparison.OrdinalIgnoreCase
			)
			|| normalizedEntryFolderPath.StartsWith(
				$"{normalizedTargetFolderPath}/",
				StringComparison.OrdinalIgnoreCase
			);
	}

	private BeautifyScriptOperationResult BeautifyScriptSkipped(string scriptPath, string message)
	{
		HandleBeautifyFormattingSkipped(scriptPath, message);

		return new BeautifyScriptOperationResult(
			BeautifyScriptOperationStatus.Skipped,
			scriptPath,
			message
		);
	}

	private BeautifyScriptOperationResult BeautifyScriptFailed(string scriptPath, string message)
	{
		HandleBeautifyFormattingFailure(scriptPath, message);

		return new BeautifyScriptOperationResult(
			BeautifyScriptOperationStatus.Failed,
			scriptPath,
			message
		);
	}

	private void HandleBeautifyFormattingSkipped(string scriptPath, string reason)
	{
		HandleBeautifyFormattingIssue("Beautify Formatting Skipped", scriptPath, reason);
	}

	private void HandleBeautifyFormattingFailure(string scriptPath, string reason)
	{
		HandleBeautifyFormattingIssue("Beautify Formatting Failure", scriptPath, reason);
	}

	private void HandleBeautifyFormattingIssue(string operation, string scriptPath, string reason)
	{
		if (string.IsNullOrWhiteSpace(reason))
			return;

		string targetPath = string.IsNullOrWhiteSpace(scriptPath) ? "<unknown>" : scriptPath;
		DebugLogger.LogOperation(operation, $"{targetPath}: {reason}");
	}

	private static bool IsUnsafeEmptyBeautifyOutput(string originalText, string formattedText)
	{
		return !string.IsNullOrWhiteSpace(originalText) && string.IsNullOrWhiteSpace(formattedText);
	}

	private static string NormalizeFormattedTextLineEndings(
		string formattedText,
		string originalText
	)
	{
		if (string.IsNullOrEmpty(formattedText) || string.IsNullOrEmpty(originalText))
			return formattedText ?? "";

		bool originalUsesCrLf = originalText.Contains("\r\n", StringComparison.Ordinal);

		return originalUsesCrLf
			? formattedText.Replace("\r\n", "\n").Replace("\n", "\r\n")
			: formattedText.Replace("\r\n", "\n");
	}

	#endregion
}
#endif
