#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;

public partial class SystemExplorerPlugin
{
	#region Quick Actions - Beautify
	private bool _isBeautifyingScript;
	private readonly List<BeautifyEditorViewState> _pendingBeautifyEditorViewStates = new();
	private int _pendingBeautifyEditorViewStateRestorePasses;
	private string _pendingBeautifyAfterCSharpierInstallMetadata = "";
	private string[] _pendingBeautifyAfterCSharpierInstallScriptPaths = Array.Empty<string>();
	private bool _pendingBeautifyAfterCSharpierInstallIsBatch;

	private enum BeautifyScriptOperationStatus
	{
		Formatted,
		Unchanged,
		Skipped,
		Failed,
	}

	private readonly struct BeautifyEditorViewState
	{
		public BeautifyEditorViewState(
			string path,
			TextEdit textEditor,
			int firstVisibleLine,
			int scrollHorizontal,
			double scrollVertical,
			int caretLine,
			int caretColumn,
			bool hadFocus
		)
		{
			Path = NormalizeScriptPath(path);
			TextEditor = textEditor;
			FirstVisibleLine = firstVisibleLine;
			ScrollHorizontal = scrollHorizontal;
			ScrollVertical = scrollVertical;
			CaretLine = caretLine;
			CaretColumn = caretColumn;
			HadFocus = hadFocus;
		}

		public string Path { get; }
		public TextEdit TextEditor { get; }
		public int FirstVisibleLine { get; }
		public int ScrollHorizontal { get; }
		public double ScrollVertical { get; }
		public int CaretLine { get; }
		public int CaretColumn { get; }
		public bool HadFocus { get; }
		public bool IsValid => TextEditor != null;
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
		string scriptPath = NormalizeScriptPath(GetScriptPathFromEntry(scriptEntry));

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

	private async Task BeautifyScriptWithCSharpier(
		string scriptPath,
		CSharpierCommand csharpierCommand
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
				showWarnings: true,
				preserveEditorViewState: true
			);

			if (
				result.Status == BeautifyScriptOperationStatus.Formatted
				|| result.Status == BeautifyScriptOperationStatus.Unchanged
			)
			{
				CallDeferred(nameof(ReleaseTreeFocusAfterNavigation));
			}
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
			.Select(NormalizeScriptPath)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		DebugPrintBeautify(
			$"Beautify Scripts batch started: normalizedCount={normalizedScriptPaths.Count}, command='{GetCSharpierCommandDisplayName(csharpierCommand)}'"
		);

		foreach (string normalizedScriptPath in normalizedScriptPaths)
			DebugPrintBeautify($"Beautify Scripts normalized path: {normalizedScriptPath}");

		_isBeautifyingScript = true;
		BeautifyScriptsBatchSummary summary = new();
		List<string> failedDetails = new();

		try
		{
			foreach (string scriptPath in normalizedScriptPaths)
			{
				BeautifyScriptOperationResult result = await BeautifySingleScriptWithCSharpier(
					scriptPath,
					csharpierCommand,
					"Beautify Scripts",
					showWarnings: false,
					preserveEditorViewState: false
				);

				summary = summary.Add(result);

				DebugPrintBeautify(
					$"Beautify Scripts item result: status={result.Status}, path='{result.Path}', message='{GetDebugTextPreview(result.Message)}'"
				);

				if (
					result.Status == BeautifyScriptOperationStatus.Failed
					&& !string.IsNullOrWhiteSpace(result.Message)
				)
				{
					failedDetails.Add(result.Message);
				}
			}

			DebugPrintBeautify($"Beautify Scripts summary: {summary}");
			CallDeferred(nameof(ReleaseTreeFocusAfterNavigation));
			DebugLogOperation("Beautify Scripts Completed", summary.ToString());

			if (summary.Failed > 0)
			{
				string details =
					failedDetails.Count == 0
						? "Some scripts could not be formatted. The affected files were left unchanged."
						: TruncateDialogText(string.Join("\n", failedDetails));

				ShowBeautifyUserMessage(
					"Beautify Scripts",
					$"Beautify Scripts finished with {summary.Failed} failed file(s).\n\n{details}"
				);
			}
		}
		finally
		{
			_isBeautifyingScript = false;
		}
	}

	private async Task<BeautifyScriptOperationResult> BeautifySingleScriptWithCSharpier(
		string scriptPath,
		CSharpierCommand csharpierCommand,
		string operationName,
		bool showWarnings,
		bool preserveEditorViewState
	)
	{
		string normalizedScriptPath = NormalizeScriptPath(scriptPath);

		DebugPrintBeautify(
			$"{operationName} item started: inputPath='{scriptPath}', normalizedPath='{normalizedScriptPath}'"
		);

		if (string.IsNullOrWhiteSpace(normalizedScriptPath))
			return BeautifyScriptSkipped(
				normalizedScriptPath,
				$"{operationName} skipped: empty script path.",
				showWarnings
			);

		if (!normalizedScriptPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
			return BeautifyScriptSkipped(
				normalizedScriptPath,
				$"{operationName} skipped non-C# file '{normalizedScriptPath}'.",
				showWarnings
			);

		if (!FileAccess.FileExists(normalizedScriptPath))
			return BeautifyScriptSkipped(
				normalizedScriptPath,
				$"{operationName} skipped missing script '{normalizedScriptPath}'.",
				showWarnings
			);

		string globalScriptPathForDebug = ProjectSettings.GlobalizePath(normalizedScriptPath);
		DebugPrintBeautify(
			$"{operationName} item path check: resExists={FileAccess.FileExists(normalizedScriptPath)}, globalPath='{globalScriptPathForDebug}', globalExists={System.IO.File.Exists(globalScriptPathForDebug)}"
		);

		string diskTextBeforeSync = ReadTextFile(normalizedScriptPath);
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

		Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath =
			GetOpenScriptEditorsByPath(
				originalTextsByPath,
				pendingTextByPath,
				out string unsafeOpenScriptList
			);

		DebugPrintBeautify(
			$"{operationName} item open editor scan: matchedOpenEditors={openEditorsByPath.Count}, unsafe='{GetDebugTextPreview(unsafeOpenScriptList)}'"
		);

		if (!string.IsNullOrWhiteSpace(unsafeOpenScriptList))
			return BeautifyScriptSkipped(
				normalizedScriptPath,
				$"{operationName} skipped: System Explorer could not safely match this open script editor buffer. Save/reopen it before formatting:\n{unsafeOpenScriptList}",
				showWarnings
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
					: autosaveFailureMessage,
				showWarnings
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
			return BeautifyScriptFailed(normalizedScriptPath, formatResult.Message, showWarnings);

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
				$"{operationName} failed: CSharpier returned empty output for non-empty script '{normalizedScriptPath}'. The file was left unchanged.",
				showWarnings
			);

		string formattedText = NormalizeFormattedTextLineEndings(csharpierOutput, originalText);

		DebugPrintBeautify(
			$"{operationName} item normalized output: formattedLength={GetDebugLength(formatResult.FormattedText)}, normalizedLength={GetDebugLength(formattedText)}"
		);

		if (IsUnsafeEmptyBeautifyOutput(originalText, formattedText))
			return BeautifyScriptFailed(
				normalizedScriptPath,
				$"{operationName} failed: CSharpier produced empty formatted text for non-empty script '{normalizedScriptPath}'. The file was left unchanged.",
				showWarnings
			);

		string currentText = ReadTextFile(normalizedScriptPath);

		if (currentText != originalText)
			return BeautifyScriptFailed(
				normalizedScriptPath,
				$"{operationName} failed: '{normalizedScriptPath}' changed while CSharpier was running. Try again.",
				showWarnings
			);

		Dictionary<string, string> updatedTextsByPath = new(StringComparer.OrdinalIgnoreCase)
		{
			[normalizedScriptPath] = formattedText,
		};

		openEditorsByPath = GetOpenScriptEditorsByPath(
			originalTextsByPath,
			updatedTextsByPath,
			out unsafeOpenScriptList
		);

		DebugPrintBeautify(
			$"{operationName} item apply editor scan: matchedOpenEditors={openEditorsByPath.Count}, unsafe='{GetDebugTextPreview(unsafeOpenScriptList)}'"
		);

		if (!string.IsNullOrWhiteSpace(unsafeOpenScriptList))
			return BeautifyScriptSkipped(
				normalizedScriptPath,
				$"{operationName} skipped: System Explorer could not safely match this open script editor buffer. Save/reopen it before formatting:\n{unsafeOpenScriptList}",
				showWarnings
			);

		if (HasUnsavedOpenScriptEditorBuffers(openEditorsByPath, out string unsavedScriptList))
			return BeautifyScriptSkipped(
				normalizedScriptPath,
				$"{operationName} skipped: the selected script changed while CSharpier was running. Try again after saving/retrying:\n{unsavedScriptList}",
				showWarnings
			);

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
					: editorValidationFailureMessage,
				showWarnings
			);
		}

		if (formattedText == originalText)
		{
			if (didAutosaveOpenEditor)
			{
				BeautifyEditorViewState unchangedEditorViewState = preserveEditorViewState
					? CaptureBeautifyEditorViewState(normalizedScriptPath, openEditorsByPath)
					: default;

				RefreshGodotAfterScriptTextChanges(new[] { normalizedScriptPath });
				RestoreBeautifyEditorViewStateNowAndDeferred(unchangedEditorViewState);
			}

			DebugLogOperation(
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
					: editorApplyFailureMessage,
				showWarnings
			);
		}

		if (!WriteTextFile(normalizedScriptPath, formattedText))
		{
			RestoreBeautifyOpenEditorAfterFailedWrite(
				normalizedScriptPath,
				originalText,
				openEditorsByPath
			);
			RefreshGodotAfterScriptTextChanges(new[] { normalizedScriptPath });
			RestoreBeautifyEditorViewStateNowAndDeferred(editorViewState);
			return BeautifyScriptFailed(
				normalizedScriptPath,
				$"{operationName} failed while writing '{normalizedScriptPath}'.",
				showWarnings
			);
		}

		ApplyTextToOpenScriptEditors(openEditorsByPath, updatedTextsByPath);
		RefreshGodotAfterScriptTextChanges(new[] { normalizedScriptPath });
		RestoreBeautifyEditorViewStateNowAndDeferred(editorViewState);

		DebugLogOperation($"{operationName} Completed", normalizedScriptPath);
		return new BeautifyScriptOperationResult(
			BeautifyScriptOperationStatus.Formatted,
			normalizedScriptPath
		);
	}

	private void StorePendingBeautifyAfterCSharpierInstall(
		string metadata,
		IEnumerable<string> scriptPaths,
		bool isBatch
	)
	{
		List<string> normalizedScriptPaths = (scriptPaths ?? Array.Empty<string>())
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Select(NormalizeScriptPath)
			.Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		_pendingBeautifyAfterCSharpierInstallMetadata = metadata ?? "";
		_pendingBeautifyAfterCSharpierInstallScriptPaths = normalizedScriptPaths.ToArray();
		_pendingBeautifyAfterCSharpierInstallIsBatch = isBatch;

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
		string metadata = _pendingBeautifyAfterCSharpierInstallMetadata;
		string[] scriptPaths = _pendingBeautifyAfterCSharpierInstallScriptPaths.ToArray();

		ClearPendingBeautifyAfterCSharpierInstall("running pending Beautify");

		DebugPrintBeautify(
			$"Running pending Beautify after CSharpier install: metadata='{metadata}', isBatch={isBatch}, scriptCount={scriptPaths.Length}"
		);

		if (isBatch)
			await BeautifyScriptsWithCSharpier(scriptPaths, csharpierCommand);
		else
			await BeautifyScriptWithCSharpier(scriptPaths[0], csharpierCommand);

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
			if (!IsScriptEntry(entry))
				continue;

			string folderPath = GetFolderPathFromEntry(entry);

			if (
				!string.IsNullOrWhiteSpace(targetFolderPath)
				&& !IsEntryInsideBeautifyFolder(folderPath, targetFolderPath)
			)
			{
				continue;
			}

			string scriptPath = NormalizeScriptPath(GetScriptPathFromEntry(entry));

			if (scriptPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
				result.Add(scriptPath);
		}

		return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
	}

	private static bool IsEntryInsideBeautifyFolder(string entryFolderPath, string targetFolderPath)
	{
		string normalizedEntryFolderPath = NormalizeScriptPath(entryFolderPath)
			.Trim('/');
		string normalizedTargetFolderPath = NormalizeScriptPath(targetFolderPath)
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

	private BeautifyScriptOperationResult BeautifyScriptSkipped(
		string scriptPath,
		string message,
		bool showWarning
	)
	{
		if (showWarning && !string.IsNullOrWhiteSpace(message))
			ShowBeautifyUserMessage("Beautify Script", message);

		return new BeautifyScriptOperationResult(
			BeautifyScriptOperationStatus.Skipped,
			scriptPath,
			message
		);
	}

	private BeautifyScriptOperationResult BeautifyScriptFailed(
		string scriptPath,
		string message,
		bool showWarning
	)
	{
		if (showWarning && !string.IsNullOrWhiteSpace(message))
			ShowBeautifyUserMessage("Beautify Script", message);

		return new BeautifyScriptOperationResult(
			BeautifyScriptOperationStatus.Failed,
			scriptPath,
			message
		);
	}

	private void ShowBeautifyUserMessage(string title, string text)
	{
		if (string.IsNullOrWhiteSpace(text))
			return;

		if (_csharpierInstalledDialog == null)
		{
			DebugPrintBeautify($"{title}: {GetDebugTextPreview(text)}");
			return;
		}

		_csharpierInstalledDialog.Title = title;
		_csharpierInstalledDialog.DialogText = text;
		_csharpierInstalledDialog.PopupCentered();
	}

	private static bool TryAutosaveBeautifyOpenEditorIfNeeded(
		string scriptPath,
		string diskText,
		Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath,
		out string originalText,
		out bool didAutosaveOpenEditor,
		out string failureMessage
	)
	{
		originalText = diskText ?? "";
		didAutosaveOpenEditor = false;
		failureMessage = "";

		if (
			openEditorsByPath == null
			|| !openEditorsByPath.TryGetValue(
				scriptPath,
				out OpenScriptEditorBuffer openEditor
			)
			|| openEditor.TextEditor == null
		)
		{
			return true;
		}

		TextEdit textEditor = openEditor.TextEditor;
		bool isUnsaved = textEditor.GetVersion() != textEditor.GetSavedVersion();

		if (!isUnsaved)
		{
			if (textEditor.Text != originalText)
			{
				failureMessage =
					$"Beautify Script cancelled: the open editor buffer for '{scriptPath}' does not match the file on disk. Reload/save the script before formatting.";
				return false;
			}

			return true;
		}

		string editorText = textEditor.Text ?? "";

		if (!WriteTextFile(scriptPath, editorText))
		{
			failureMessage =
				$"Beautify Script cancelled: could not autosave the selected script before formatting '{scriptPath}'.";
			return false;
		}

		string savedEditorText = ReadTextFile(scriptPath);

		if (savedEditorText != editorText)
		{
			failureMessage =
				$"Beautify Script cancelled: autosaved text for '{scriptPath}' did not match the open editor buffer. The script was not formatted.";
			return false;
		}

		textEditor.TagSavedVersion();
		originalText = savedEditorText;
		didAutosaveOpenEditor = true;
		return true;
	}

	private static bool ValidateBeautifyOpenEditorStillMatchesDisk(
		string scriptPath,
		string originalText,
		Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath,
		out string failureMessage
	)
	{
		failureMessage = "";

		if (
			openEditorsByPath == null
			|| !openEditorsByPath.TryGetValue(
				scriptPath,
				out OpenScriptEditorBuffer openEditor
			)
			|| openEditor.TextEditor == null
		)
		{
			return true;
		}

		TextEdit textEditor = openEditor.TextEditor;

		if (textEditor.GetVersion() != textEditor.GetSavedVersion())
		{
			failureMessage =
				$"Beautify Script cancelled: the selected script became unsaved while CSharpier was running. Try again after saving/retrying:\n{scriptPath}";
			return false;
		}

		if (textEditor.Text != originalText)
		{
			failureMessage =
				$"Beautify Script cancelled: the open editor buffer for '{scriptPath}' changed while CSharpier was running. Try again.";
			return false;
		}

		return true;
	}

	private static bool TryApplyBeautifyTextToEditorBeforeDiskWrite(
		string scriptPath,
		string originalText,
		string formattedText,
		Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath,
		out string failureMessage
	)
	{
		failureMessage = "";

		if (
			openEditorsByPath == null
			|| !openEditorsByPath.TryGetValue(
				scriptPath,
				out OpenScriptEditorBuffer openEditor
			)
			|| openEditor.TextEditor == null
		)
		{
			return true;
		}

		TextEdit textEditor = openEditor.TextEditor;

		if (textEditor.GetVersion() != textEditor.GetSavedVersion())
		{
			failureMessage =
				$"Beautify Script cancelled: the selected script is unsaved again before applying formatted text:\n{scriptPath}";
			return false;
		}

		if (textEditor.Text != originalText)
		{
			failureMessage =
				$"Beautify Script cancelled: the open editor buffer for '{scriptPath}' no longer matches the saved text. Try again.";
			return false;
		}

		if (textEditor.Text != formattedText)
			textEditor.Text = formattedText;

		return true;
	}

	private BeautifyEditorViewState CaptureBeautifyEditorViewState(
		string scriptPath,
		Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath
	)
	{
		if (
			openEditorsByPath == null
			|| !openEditorsByPath.TryGetValue(
				scriptPath,
				out OpenScriptEditorBuffer openEditor
			)
			|| !IsBeautifyTextEditorAvailable(openEditor.TextEditor)
		)
		{
			return default;
		}

		TextEdit textEditor = openEditor.TextEditor;

		try
		{
			return new BeautifyEditorViewState(
				scriptPath,
				textEditor,
				Math.Max(0, textEditor.GetFirstVisibleLine()),
				Math.Max(0, textEditor.ScrollHorizontal),
				Math.Max(0.0, textEditor.ScrollVertical),
				Math.Max(0, textEditor.GetCaretLine()),
				Math.Max(0, textEditor.GetCaretColumn()),
				textEditor.HasFocus()
			);
		}
		catch (Exception exception)
		{
			DebugPrintBeautify(
				$"Beautify Script view-state capture skipped for '{scriptPath}': {exception.Message}"
			);
			return default;
		}
	}

	private void RestoreBeautifyEditorViewStateNowAndDeferred(
		BeautifyEditorViewState editorViewState
	)
	{
		if (!editorViewState.IsValid)
			return;

		RestoreBeautifyEditorViewState(editorViewState);

		_pendingBeautifyEditorViewStates.Add(editorViewState);
		_pendingBeautifyEditorViewStateRestorePasses = Math.Max(
			_pendingBeautifyEditorViewStateRestorePasses,
			2
		);
		CallDeferred(nameof(RestorePendingBeautifyEditorViewStates));
	}

	private void RestorePendingBeautifyEditorViewStates()
	{
		if (_pendingBeautifyEditorViewStates.Count == 0)
		{
			_pendingBeautifyEditorViewStateRestorePasses = 0;
			return;
		}

		BeautifyEditorViewState[] pendingStates = _pendingBeautifyEditorViewStates.ToArray();

		foreach (BeautifyEditorViewState editorViewState in pendingStates)
			RestoreBeautifyEditorViewState(editorViewState);

		_pendingBeautifyEditorViewStateRestorePasses--;

		if (_pendingBeautifyEditorViewStateRestorePasses > 0)
		{
			CallDeferred(nameof(RestorePendingBeautifyEditorViewStates));
			return;
		}

		_pendingBeautifyEditorViewStates.Clear();
	}

	private void RestoreBeautifyEditorViewState(BeautifyEditorViewState editorViewState)
	{
		if (!editorViewState.IsValid || !IsBeautifyTextEditorAvailable(editorViewState.TextEditor))
			return;

		TextEdit textEditor = editorViewState.TextEditor;

		try
		{
			int lineCount = Math.Max(1, textEditor.GetLineCount());
			int firstVisibleLine = ClampBeautifyEditorViewStateValue(
				editorViewState.FirstVisibleLine,
				0,
				lineCount - 1
			);
			int caretLine = ClampBeautifyEditorViewStateValue(
				editorViewState.CaretLine,
				0,
				lineCount - 1
			);
			int caretColumn = ClampBeautifyEditorViewStateValue(
				editorViewState.CaretColumn,
				0,
				GetBeautifyEditorLineLength(textEditor, caretLine)
			);
			int scrollHorizontal = Math.Max(0, editorViewState.ScrollHorizontal);
			double scrollVertical = Math.Min(
				Math.Max(0.0, editorViewState.ScrollVertical),
				Math.Max(0.0, lineCount - 1)
			);

			textEditor.SetCaretLine(caretLine, false, true, -1);
			textEditor.SetCaretColumn(caretColumn, false);
			textEditor.SetLineAsFirstVisible(firstVisibleLine);
			textEditor.ScrollVertical = scrollVertical;
			textEditor.ScrollHorizontal = scrollHorizontal;

			if (editorViewState.HadFocus && textEditor.IsVisibleInTree())
				textEditor.GrabFocus();
		}
		catch (Exception exception)
		{
			DebugPrintBeautify(
				$"Beautify Script view-state restore skipped for '{editorViewState.Path}': {exception.Message}"
			);
		}
	}

	private static bool IsBeautifyTextEditorAvailable(TextEdit textEditor)
	{
		return textEditor != null
			&& GodotObject.IsInstanceValid(textEditor)
			&& !textEditor.IsQueuedForDeletion();
	}

	private static int GetBeautifyEditorLineLength(TextEdit textEditor, int line)
	{
		if (textEditor == null)
			return 0;

		try
		{
			if (line < 0 || line >= textEditor.GetLineCount())
				return 0;

			return (textEditor.GetLine(line) ?? "").Length;
		}
		catch
		{
			return 0;
		}
	}

	private static int ClampBeautifyEditorViewStateValue(int value, int min, int max)
	{
		if (max < min)
			return min;

		return Math.Min(Math.Max(value, min), max);
	}

	private static void RestoreBeautifyOpenEditorAfterFailedWrite(
		string scriptPath,
		string originalText,
		Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath
	)
	{
		if (
			openEditorsByPath == null
			|| !openEditorsByPath.TryGetValue(
				scriptPath,
				out OpenScriptEditorBuffer openEditor
			)
			|| openEditor.TextEditor == null
		)
		{
			return;
		}

		ApplyTextToOpenScriptEditor(openEditor.TextEditor, originalText);
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
