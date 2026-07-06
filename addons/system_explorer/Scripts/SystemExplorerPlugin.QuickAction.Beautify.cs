#if TOOLS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using IOPath = System.IO.Path;
using System.Text;
using System.Threading.Tasks;
using Godot;

public partial class SystemExplorerPlugin
{
	#region Quick Actions - Beautify
	private const int CSharpierDetectionTimeoutMilliseconds = 3000;
	private const int CSharpierWarmUpTimeoutMilliseconds = 10000;
	private const int CSharpierFormatTimeoutMilliseconds = 30000;
	private const int CSharpierInstallTimeoutMilliseconds = 120000;
	private const int CSharpierDebugPreviewLength = 500;

	private bool _isInstallingCSharpier;
	private bool _isDebugUninstallingCSharpier;
	private bool _isBeautifyingScript;
	private bool _isWarmingUpCSharpierCommandCache;
	private CSharpierCommand _cachedCSharpierCommand;
	private readonly List<BeautifyEditorViewState> _pendingBeautifyEditorViewStates = new();
	private int _pendingBeautifyEditorViewStateRestorePasses;
	private string _pendingBeautifyAfterCSharpierInstallMetadata = "";
	private string[] _pendingBeautifyAfterCSharpierInstallScriptPaths = Array.Empty<string>();
	private bool _pendingBeautifyAfterCSharpierInstallIsBatch;

	// Dev-only debug switch for testing the CSharpier install flow.
	// Keep false for normal use and releases.
	private const bool DebugUninstallCSharpierOnStartup = false;

	private readonly struct CSharpierInstallResult
	{
		public CSharpierInstallResult(
			bool success,
			string message,
			CSharpierCommand command = default
		)
		{
			Success = success;
			Message = message;
			Command = command;
		}

		public bool Success { get; }
		public string Message { get; }
		public CSharpierCommand Command { get; }
	}

	private enum CSharpierProbeStatus
	{
		Failed,
		Succeeded,
		TimedOut,
	}

	private readonly struct CSharpierCommand
	{
		public CSharpierCommand(string executable, params string[] baseArguments)
		{
			Executable = executable;
			BaseArguments = baseArguments ?? Array.Empty<string>();
		}

		public string Executable { get; }
		public string[] BaseArguments { get; }
		public bool IsValid => !string.IsNullOrWhiteSpace(Executable);
	}

	private readonly struct CSharpierProbeResult
	{
		public CSharpierProbeResult(bool success, CSharpierCommand command, bool timedOut)
		{
			Success = success;
			Command = command;
			TimedOut = timedOut;
		}

		public bool Success { get; }
		public CSharpierCommand Command { get; }
		public bool TimedOut { get; }
	}

	private readonly struct CSharpierFormatResult
	{
		public CSharpierFormatResult(
			bool success,
			string formattedText,
			string message,
			bool shouldInvalidateCachedCommand = false
		)
		{
			Success = success;
			FormattedText = formattedText;
			Message = message;
			ShouldInvalidateCachedCommand = shouldInvalidateCachedCommand;
		}

		public bool Success { get; }
		public string FormattedText { get; }
		public string Message { get; }
		public bool ShouldInvalidateCachedCommand { get; }
	}

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
			Path = NormalizeRefactorNamespacePath(path);
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
		string scriptPath = NormalizeRefactorNamespacePath(GetScriptPathFromEntry(scriptEntry));

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
			.Select(NormalizeRefactorNamespacePath)
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
		string normalizedScriptPath = NormalizeRefactorNamespacePath(scriptPath);

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

		Dictionary<string, RefactorNamespaceOpenEditor> openEditorsByPath =
			GetOpenRefactorNamespaceEditorsByPath(
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

		openEditorsByPath = GetOpenRefactorNamespaceEditorsByPath(
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

		if (HasUnsavedRefactorNamespaceFiles(openEditorsByPath, out string unsavedScriptList))
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

				RefreshGodotAfterRefactorNamespace(new[] { normalizedScriptPath });
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
			RefreshGodotAfterRefactorNamespace(new[] { normalizedScriptPath });
			RestoreBeautifyEditorViewStateNowAndDeferred(editorViewState);
			return BeautifyScriptFailed(
				normalizedScriptPath,
				$"{operationName} failed while writing '{normalizedScriptPath}'.",
				showWarnings
			);
		}

		ApplyRefactorNamespaceTextToOpenEditors(openEditorsByPath, updatedTextsByPath);
		RefreshGodotAfterRefactorNamespace(new[] { normalizedScriptPath });
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
			.Select(NormalizeRefactorNamespacePath)
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

			string scriptPath = NormalizeRefactorNamespacePath(GetScriptPathFromEntry(entry));

			if (scriptPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
				result.Add(scriptPath);
		}

		return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
	}

	private static bool IsEntryInsideBeautifyFolder(string entryFolderPath, string targetFolderPath)
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
		Dictionary<string, RefactorNamespaceOpenEditor> openEditorsByPath,
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
				out RefactorNamespaceOpenEditor openEditor
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
		Dictionary<string, RefactorNamespaceOpenEditor> openEditorsByPath,
		out string failureMessage
	)
	{
		failureMessage = "";

		if (
			openEditorsByPath == null
			|| !openEditorsByPath.TryGetValue(
				scriptPath,
				out RefactorNamespaceOpenEditor openEditor
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
		Dictionary<string, RefactorNamespaceOpenEditor> openEditorsByPath,
		out string failureMessage
	)
	{
		failureMessage = "";

		if (
			openEditorsByPath == null
			|| !openEditorsByPath.TryGetValue(
				scriptPath,
				out RefactorNamespaceOpenEditor openEditor
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
		Dictionary<string, RefactorNamespaceOpenEditor> openEditorsByPath
	)
	{
		if (
			openEditorsByPath == null
			|| !openEditorsByPath.TryGetValue(
				scriptPath,
				out RefactorNamespaceOpenEditor openEditor
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
		Dictionary<string, RefactorNamespaceOpenEditor> openEditorsByPath
	)
	{
		if (
			openEditorsByPath == null
			|| !openEditorsByPath.TryGetValue(
				scriptPath,
				out RefactorNamespaceOpenEditor openEditor
			)
			|| openEditor.TextEditor == null
		)
		{
			return;
		}

		ApplyRefactorNamespaceTextToOpenEditor(openEditor.TextEditor, originalText);
	}

	private static bool IsUnsafeEmptyBeautifyOutput(string originalText, string formattedText)
	{
		return !string.IsNullOrWhiteSpace(originalText) && string.IsNullOrWhiteSpace(formattedText);
	}

	private void OnCSharpierInstallConfirmed()
	{
		if (_isInstallingCSharpier)
			return;

		_ = InstallCSharpierAsync();
	}

	private async Task InstallCSharpierAsync()
	{
		_isInstallingCSharpier = true;
		SetCSharpierInstallButtonDisabled(true);

		CSharpierInstallResult installResult = await Task.Run(InstallCSharpierGlobalTool);

		_isInstallingCSharpier = false;
		SetCSharpierInstallButtonDisabled(false);

		if (!installResult.Success)
		{
			ClearPendingBeautifyAfterCSharpierInstall("CSharpier install failed");
			ShowCSharpierInstallResultDialog(installResult);
			return;
		}

		if (installResult.Command.IsValid)
			CacheCSharpierCommand(installResult.Command, "install");

		if (await TryRunPendingBeautifyAfterCSharpierInstall(installResult.Command))
			return;

		ShowCSharpierInstallResultDialog(installResult);
	}

	private void ShowCSharpierInstallResultDialog(CSharpierInstallResult installResult)
	{
		if (_csharpierInstallResultDialog == null)
		{
			DebugPrintBeautify(
				$"CSharpier install result: success={installResult.Success}, message='{GetDebugTextPreview(installResult.Message)}'"
			);
			return;
		}

		_csharpierInstallResultDialog.Title = installResult.Success
			? "CSharpier Installed"
			: "CSharpier Install Failed";
		_csharpierInstallResultDialog.DialogText = installResult.Message;
		_csharpierInstallResultDialog.PopupCentered();
	}

	private void StartCSharpierStartupWarmUp()
	{
		if (DebugState && DebugUninstallCSharpierOnStartup)
		{
			CallDeferred(nameof(DebugUninstallCSharpierOnStartupThenWarmUp));
		}
		else
		{
			CallDeferred(nameof(WarmUpCSharpierCommandCache));
		}
	}

	private void DebugUninstallCSharpierOnStartupThenWarmUp()
	{
		_ = DebugUninstallCSharpierOnStartupThenWarmUpAsync();
	}

	private async Task DebugUninstallCSharpierOnStartupThenWarmUpAsync()
	{
		if (_isDebugUninstallingCSharpier)
			return;

		_isDebugUninstallingCSharpier = true;
		ClearCachedCSharpierCommand("startup debug uninstall started");
		DebugPrintBeautify("Startup debug uninstall of CSharpier started.");

		try
		{
			CSharpierInstallResult uninstallResult = await Task.Run(
				ExecuteCSharpierUninstallCommandForDebug
			);

			DebugPrintBeautify(
				$"Startup debug uninstall of CSharpier finished: success={uninstallResult.Success}, message='{GetDebugTextPreview(uninstallResult.Message)}'"
			);
		}
		finally
		{
			ClearCachedCSharpierCommand("startup debug uninstall finished");
			_isDebugUninstallingCSharpier = false;
		}

		WarmUpCSharpierCommandCache();
	}

	private async void WarmUpCSharpierCommandCache()
	{
		if (_isWarmingUpCSharpierCommandCache || _cachedCSharpierCommand.IsValid)
			return;

		_isWarmingUpCSharpierCommandCache = true;
		DebugLogOperation("CSharpier Warm-up Started");

		try
		{
			string workingDirectory = GetProjectWorkingDirectory();
			CSharpierProbeResult probeResult = await Task.Run(() =>
				ProbeCSharpierCommand(CSharpierWarmUpTimeoutMilliseconds, workingDirectory)
			);

			if (probeResult.Success)
			{
				CacheCSharpierCommand(probeResult.Command, "warm-up");
				DebugLogOperation(
					"CSharpier Warm-up Completed",
					GetCSharpierCommandDisplayName(probeResult.Command)
				);
				return;
			}

			DebugLogOperation(
				"CSharpier Warm-up Failed",
				probeResult.TimedOut ? "probe timed out" : "command not found"
			);
		}
		finally
		{
			_isWarmingUpCSharpierCommandCache = false;
		}
	}

	private bool IsCSharpierInstalled()
	{
		return TryGetCSharpierCommand(out _);
	}

	private bool TryGetCSharpierCommand(
		out CSharpierCommand command,
		bool allowCachedCommand = true
	)
	{
		if (allowCachedCommand && _cachedCSharpierCommand.IsValid)
		{
			command = _cachedCSharpierCommand;
			DebugLogOperation(
				"CSharpier Command Cache Hit",
				GetCSharpierCommandDisplayName(command)
			);
			return true;
		}

		CSharpierProbeResult probeResult = ProbeCSharpierCommand(
			CSharpierDetectionTimeoutMilliseconds,
			GetProjectWorkingDirectory()
		);

		if (probeResult.Success)
		{
			command = probeResult.Command;
			CacheCSharpierCommand(command, "manual probe");
			return true;
		}

		DebugLogOperation(
			"CSharpier Detection Failed",
			probeResult.TimedOut ? "probe timed out" : "command not found"
		);

		command = default;
		return false;
	}

	private static CSharpierProbeResult ProbeCSharpierCommand(
		int timeoutMilliseconds,
		string workingDirectory
	)
	{
		bool timedOut = false;

		foreach (CSharpierCommand candidate in GetCSharpierCommandCandidates())
		{
			CSharpierProbeStatus status = CanExecuteCSharpierCommand(
				candidate.Executable,
				candidate.BaseArguments.Concat(new[] { "--version" }),
				timeoutMilliseconds,
				workingDirectory
			);

			if (status == CSharpierProbeStatus.Succeeded)
				return new CSharpierProbeResult(true, candidate, false);

			if (status == CSharpierProbeStatus.TimedOut)
				timedOut = true;
		}

		return new CSharpierProbeResult(false, default, timedOut);
	}

	private void CacheCSharpierCommand(CSharpierCommand command, string source)
	{
		if (!command.IsValid)
			return;

		_cachedCSharpierCommand = command;
		DebugLogOperation(
			"CSharpier Command Cached",
			$"{source}: {GetCSharpierCommandDisplayName(command)}"
		);
	}

	private bool IsCachedCSharpierCommand(CSharpierCommand command)
	{
		return _cachedCSharpierCommand.IsValid
			&& string.Equals(
				_cachedCSharpierCommand.Executable,
				command.Executable,
				StringComparison.OrdinalIgnoreCase
			)
			&& _cachedCSharpierCommand.BaseArguments.SequenceEqual(
				command.BaseArguments ?? Array.Empty<string>()
			);
	}

	private void ClearCachedCSharpierCommand(string reason)
	{
		if (!_cachedCSharpierCommand.IsValid)
			return;

		DebugLogOperation(
			"CSharpier Command Cache Cleared",
			$"{GetCSharpierCommandDisplayName(_cachedCSharpierCommand)} ({reason})"
		);

		_cachedCSharpierCommand = default;
	}

	private static string GetCSharpierCommandDisplayName(CSharpierCommand command)
	{
		if (!command.IsValid)
			return "<invalid>";

		string[] baseArguments = command.BaseArguments ?? Array.Empty<string>();

		return baseArguments.Length == 0
			? command.Executable
			: $"{command.Executable} {string.Join(" ", baseArguments)}";
	}

	private static IEnumerable<CSharpierCommand> GetCSharpierCommandCandidates()
	{
		yield return new CSharpierCommand("dotnet", "csharpier");
		yield return new CSharpierCommand("csharpier");

		string globalToolPath = GetGlobalCSharpierToolPath();

		if (!string.IsNullOrWhiteSpace(globalToolPath))
			yield return new CSharpierCommand(globalToolPath);
	}

	private async Task<CSharpierFormatResult> FormatScriptWithCSharpierUsingCachedCommandFallback(
		CSharpierCommand command,
		string scriptPath,
		string operationName
	)
	{
		bool usedCachedCommand = IsCachedCSharpierCommand(command);
		bool debugState = DebugState;
		CSharpierFormatResult formatResult = await Task.Run(() =>
			FormatScriptWithCSharpier(command, scriptPath, operationName, debugState)
		);

		if (
			formatResult.Success
			|| !formatResult.ShouldInvalidateCachedCommand
			|| !usedCachedCommand
		)
			return formatResult;

		ClearCachedCSharpierCommand("cached command failed during format");

		if (
			!TryGetCSharpierCommand(out CSharpierCommand fallbackCommand, allowCachedCommand: false)
		)
			return formatResult;

		DebugLogOperation(
			"CSharpier Command Retry",
			$"{GetCSharpierCommandDisplayName(command)} -> {GetCSharpierCommandDisplayName(fallbackCommand)}"
		);

		return await Task.Run(() =>
			FormatScriptWithCSharpier(fallbackCommand, scriptPath, operationName, debugState)
		);
	}

	private static CSharpierFormatResult FormatScriptWithCSharpier(
		CSharpierCommand command,
		string scriptPath,
		string operationName,
		bool debugState
	)
	{
		if (!command.IsValid)
			return new CSharpierFormatResult(
				false,
				"",
				"Beautify Script failed: CSharpier command is invalid.",
				shouldInvalidateCachedCommand: true
			);

		string globalScriptPath = ProjectSettings.GlobalizePath(scriptPath);

		if (string.IsNullOrWhiteSpace(globalScriptPath))
			return new CSharpierFormatResult(
				false,
				"",
				$"Beautify Script failed: could not resolve '{scriptPath}'."
			);

		string workingDirectory = GetProjectWorkingDirectory();
		DebugPrintBeautify(
			debugState,
			$"{operationName} CSharpier start: command='{GetCSharpierCommandDisplayName(command)}', scriptPath='{scriptPath}', globalPath='{globalScriptPath}', globalExists={System.IO.File.Exists(globalScriptPath)}, workingDirectory='{workingDirectory}'"
		);

		try
		{
			using Process process = new()
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = command.Executable,
					WorkingDirectory = workingDirectory,
					UseShellExecute = false,
					CreateNoWindow = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					StandardOutputEncoding = Encoding.UTF8,
					StandardErrorEncoding = Encoding.UTF8,
				},
			};

			foreach (string argument in command.BaseArguments)
				process.StartInfo.ArgumentList.Add(argument);

			process.StartInfo.ArgumentList.Add("format");
			process.StartInfo.ArgumentList.Add(globalScriptPath);
			process.StartInfo.ArgumentList.Add("--write-stdout");
			process.StartInfo.ArgumentList.Add("--log-level");
			process.StartInfo.ArgumentList.Add("None");

			DebugPrintBeautify(
				debugState,
				$"{operationName} CSharpier args: {GetDebugProcessArguments(process.StartInfo.ArgumentList)}"
			);

			if (!process.Start())
				return new CSharpierFormatResult(
					false,
					"",
					"Beautify Script failed: could not start CSharpier.",
					shouldInvalidateCachedCommand: true
				);

			Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
			Task<string> errorOutputTask = process.StandardError.ReadToEndAsync();

			if (!process.WaitForExit(CSharpierFormatTimeoutMilliseconds))
			{
				DebugPrintBeautify(
					debugState,
					$"{operationName} CSharpier timed out after {CSharpierFormatTimeoutMilliseconds} ms."
				);
				TryKillProcess(process);
				return new CSharpierFormatResult(
					false,
					"",
                    "Beautify Script failed: CSharpier timed out."
				);
			}

			string standardOutput = standardOutputTask.Result;
			string errorOutput = errorOutputTask.Result.Trim();

			DebugPrintBeautify(
				debugState,
				$"{operationName} CSharpier exit: exitCode={process.ExitCode}, stdoutLength={GetDebugLength(standardOutput)}, stderrLength={GetDebugLength(errorOutput)}, stdoutPreview='{GetDebugTextPreview(standardOutput)}', stderrPreview='{GetDebugTextPreview(errorOutput)}'"
			);

			if (process.ExitCode == 0)
				return new CSharpierFormatResult(true, standardOutput, "");

			string details = !string.IsNullOrWhiteSpace(errorOutput)
				? errorOutput
				: standardOutput.Trim();

			return new CSharpierFormatResult(
				false,
				"",
				$"Beautify Script failed: CSharpier could not format '{scriptPath}'.",
				shouldInvalidateCachedCommand: LooksLikeUnavailableCSharpierCommandDetails(details)
			);
		}
		catch (Exception exception)
		{
			DebugPrintBeautify(debugState, $"{operationName} CSharpier exception: {exception}");

			return new CSharpierFormatResult(
				false,
				"",
				"Beautify Script failed: CSharpier could not be started.",
				shouldInvalidateCachedCommand: true
			);
		}
	}

	private CSharpierInstallResult InstallCSharpierGlobalTool()
	{
		CSharpierInstallResult installResult = ExecuteCSharpierInstallCommand();

		if (!installResult.Success)
			return installResult;

		CSharpierCommand installedCommand = GetCSharpierCommandAfterSuccessfulGlobalInstall();

		return new CSharpierInstallResult(true, "CSharpier is now installed.", installedCommand);
	}

	private static CSharpierCommand GetCSharpierCommandAfterSuccessfulGlobalInstall()
	{
		return new CSharpierCommand("dotnet", "csharpier");
	}

	private static CSharpierInstallResult ExecuteCSharpierInstallCommand()
	{
		try
		{
			using Process process = new()
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = "dotnet",
					UseShellExecute = false,
					CreateNoWindow = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					StandardOutputEncoding = Encoding.UTF8,
					StandardErrorEncoding = Encoding.UTF8,
				},
			};

			process.StartInfo.ArgumentList.Add("tool");
			process.StartInfo.ArgumentList.Add("install");
			process.StartInfo.ArgumentList.Add("csharpier");
			process.StartInfo.ArgumentList.Add("-g");

			if (!process.Start())
				return new CSharpierInstallResult(
					false,
                    "Could not start dotnet to install CSharpier."
				);

			Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
			Task<string> errorOutputTask = process.StandardError.ReadToEndAsync();

			if (!process.WaitForExit(CSharpierInstallTimeoutMilliseconds))
			{
				TryKillProcess(process);
				return new CSharpierInstallResult(false, "CSharpier installation timed out.");
			}

			string errorOutput = errorOutputTask.Result.Trim();
			string standardOutput = standardOutputTask.Result.Trim();

			if (process.ExitCode == 0)
				return new CSharpierInstallResult(true, "CSharpier is now installed.");

			string details = !string.IsNullOrWhiteSpace(errorOutput) ? errorOutput : standardOutput;

			return new CSharpierInstallResult(
				false,
				string.IsNullOrWhiteSpace(details)
					? "CSharpier could not be installed. Make sure the .NET SDK is installed and try again."
					: $"CSharpier could not be installed:\n{TruncateDialogText(details)}"
			);
		}
		catch
		{
			return new CSharpierInstallResult(
				false,
                "CSharpier could not be installed. Make sure the .NET SDK is installed and try again."
			);
		}
	}

	private static CSharpierInstallResult ExecuteCSharpierUninstallCommandForDebug()
	{
		try
		{
			using Process process = new()
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = "dotnet",
					UseShellExecute = false,
					CreateNoWindow = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					StandardOutputEncoding = Encoding.UTF8,
					StandardErrorEncoding = Encoding.UTF8,
				},
			};

			process.StartInfo.ArgumentList.Add("tool");
			process.StartInfo.ArgumentList.Add("uninstall");
			process.StartInfo.ArgumentList.Add("csharpier");
			process.StartInfo.ArgumentList.Add("-g");

			if (!process.Start())
				return new CSharpierInstallResult(
					false,
                    "Could not start dotnet to uninstall CSharpier."
				);

			Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
			Task<string> errorOutputTask = process.StandardError.ReadToEndAsync();

			if (!process.WaitForExit(CSharpierInstallTimeoutMilliseconds))
			{
				TryKillProcess(process);
				return new CSharpierInstallResult(false, "CSharpier uninstall timed out.");
			}

			string errorOutput = errorOutputTask.Result.Trim();
			string standardOutput = standardOutputTask.Result.Trim();
			string details = !string.IsNullOrWhiteSpace(errorOutput) ? errorOutput : standardOutput;

			if (process.ExitCode == 0)
				return new CSharpierInstallResult(true, "CSharpier was uninstalled.");

			if (LooksLikeCSharpierAlreadyUninstalledDetails(details))
				return new CSharpierInstallResult(true, "CSharpier was already not installed.");

			return new CSharpierInstallResult(
				false,
				string.IsNullOrWhiteSpace(details)
					? "CSharpier could not be uninstalled."
					: $"CSharpier could not be uninstalled:\n{TruncateDialogText(details)}"
			);
		}
		catch
		{
			return new CSharpierInstallResult(
				false,
                "CSharpier could not be uninstalled. Make sure the .NET SDK is installed and try again."
			);
		}
	}

	private static bool LooksLikeCSharpierAlreadyUninstalledDetails(string details)
	{
		if (string.IsNullOrWhiteSpace(details))
			return false;

		string normalizedDetails = details.ToLowerInvariant();

		return normalizedDetails.Contains("not currently installed", StringComparison.Ordinal)
			|| normalizedDetails.Contains("is not installed", StringComparison.Ordinal)
			|| normalizedDetails.Contains(
				"package 'csharpier' is not found",
				StringComparison.Ordinal
			)
			|| normalizedDetails.Contains("tool 'csharpier'", StringComparison.Ordinal)
				&& normalizedDetails.Contains("not found", StringComparison.Ordinal);
	}

	private static bool LooksLikeUnavailableCSharpierCommandDetails(string details)
	{
		if (string.IsNullOrWhiteSpace(details))
			return false;

		string normalizedDetails = details.ToLowerInvariant();

		return normalizedDetails.Contains(
				"could not execute because the specified command or file was not found",
				StringComparison.Ordinal
			)
			|| normalizedDetails.Contains(
				"no executable found matching command",
				StringComparison.Ordinal
			)
			|| normalizedDetails.Contains("not recognized", StringComparison.Ordinal)
			|| (
				normalizedDetails.Contains("csharpier", StringComparison.Ordinal)
				&& (
					normalizedDetails.Contains("not found", StringComparison.Ordinal)
					|| normalizedDetails.Contains("not installed", StringComparison.Ordinal)
					|| normalizedDetails.Contains("does not exist", StringComparison.Ordinal)
				)
			);
	}

	private static CSharpierProbeStatus CanExecuteCSharpierCommand(
		string executable,
		IEnumerable<string> arguments,
		int timeoutMilliseconds,
		string workingDirectory
	)
	{
		if (string.IsNullOrWhiteSpace(executable))
			return CSharpierProbeStatus.Failed;

		try
		{
			using Process process = new()
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = executable,
					WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
						? System.Environment.CurrentDirectory
						: workingDirectory,
					UseShellExecute = false,
					CreateNoWindow = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					StandardOutputEncoding = Encoding.UTF8,
					StandardErrorEncoding = Encoding.UTF8,
				},
			};

			foreach (string argument in arguments ?? Array.Empty<string>())
				process.StartInfo.ArgumentList.Add(argument);

			if (!process.Start())
				return CSharpierProbeStatus.Failed;

			if (!process.WaitForExit(timeoutMilliseconds))
			{
				TryKillProcess(process);
				return CSharpierProbeStatus.TimedOut;
			}

			return process.ExitCode == 0
				? CSharpierProbeStatus.Succeeded
				: CSharpierProbeStatus.Failed;
		}
		catch
		{
			return CSharpierProbeStatus.Failed;
		}
	}

	private void SetCSharpierInstallButtonDisabled(bool disabled)
	{
		Button installButton = _csharpierNotInstalledDialog?.GetOkButton();

		if (installButton != null)
			installButton.Disabled = disabled;
	}

	private void DebugPrintBeautify(string message)
	{
		DebugPrintBeautify(DebugState, message);
	}

	private static void DebugPrintBeautify(bool debugState, string message)
	{
		if (!debugState)
			return;

		GD.Print($"System Explorer Beautify: {message}");
	}

	private static int GetDebugLength(string text)
	{
		return text?.Length ?? -1;
	}

	private static string GetDebugTextPreview(string text)
	{
		if (string.IsNullOrEmpty(text))
			return "";

		string normalizedText = text.Replace("\r", "\\r", StringComparison.Ordinal)
			.Replace("\n", "\\n", StringComparison.Ordinal)
			.Replace("\t", "\\t", StringComparison.Ordinal);

		return normalizedText.Length <= CSharpierDebugPreviewLength
			? normalizedText
			: normalizedText[..CSharpierDebugPreviewLength] + "...";
	}

	private static string GetDebugProcessArguments(
		System.Collections.ObjectModel.Collection<string> arguments
	)
	{
		if (arguments == null || arguments.Count == 0)
			return "<none>";

		return string.Join(" ", arguments.Select(GetDebugQuotedArgument));
	}

	private static string GetDebugQuotedArgument(string argument)
	{
		if (argument == null)
			return "<null>";

		return argument.Contains(' ', StringComparison.Ordinal) ? $"\"{argument}\"" : argument;
	}

	private static string TruncateDialogText(string text)
	{
		const int maximumLength = 1200;

		if (string.IsNullOrWhiteSpace(text) || text.Length <= maximumLength)
			return text;

		return text[..maximumLength] + "...";
	}

	private static string GetGlobalCSharpierToolPath()
	{
		string userProfilePath = System.Environment.GetFolderPath(
			System.Environment.SpecialFolder.UserProfile
		);

		if (string.IsNullOrWhiteSpace(userProfilePath))
			return string.Empty;

		string executableName = OperatingSystem.IsWindows() ? "csharpier.exe" : "csharpier";

		return IOPath.Combine(userProfilePath, ".dotnet", "tools", executableName);
	}

	private static string GetProjectWorkingDirectory()
	{
		string projectPath = ProjectSettings.GlobalizePath("res://");

		return string.IsNullOrWhiteSpace(projectPath)
			? System.Environment.CurrentDirectory
			: projectPath;
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

	private static void TryKillProcess(Process process)
	{
		try
		{
			process.Kill(entireProcessTree: true);
		}
		catch
		{
			// Best effort cleanup only.
		}
	}
	#endregion
}
#endif
