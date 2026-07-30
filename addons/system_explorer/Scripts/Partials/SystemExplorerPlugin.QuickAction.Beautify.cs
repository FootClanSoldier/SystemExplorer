#if TOOLS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using SystemExplorer.EditorIntegration.Operations;
using Godot;
using SystemExplorer.EditorIntegration.ScriptEditing;
using SystemExplorer.QuickActions.Beautify;
using SystemExplorer.QuickActions.Beautify.CSharpier;

public partial class SystemExplorerPlugin
{
	#region Quick Actions - Beautify
	private enum BeautifyScriptInvocationOrigin
	{
		SystemExplorer,
		FocusedScriptEditor,
	}

	private enum BeautifyContextMenuRefreshPolicy
	{
		Always,
		VisibleOnly,
	}

	private enum BeautifyBufferLookupRoute
	{
		NotRun,
		CapturedEditor,
		GeneralLocator,
	}

	private bool _isBeautifyingScript;
	private string _pendingBeautifyAfterCSharpierInstallMetadata = "";
	private string[] _pendingBeautifyAfterCSharpierInstallScriptPaths = Array.Empty<string>();
	private bool _pendingBeautifyAfterCSharpierInstallIsBatch;
	private BeautifyScriptInvocationOrigin _pendingBeautifyAfterCSharpierInstallInvocationOrigin =
		BeautifyScriptInvocationOrigin.SystemExplorer;
	private BeautifyEditorStateService _beautifyEditorStateService;

	private BeautifyEditorStateService BeautifyEditorState =>
		_beautifyEditorStateService ??= new BeautifyEditorStateService(
			DebugPrintBeautify,
			ScheduleBeautifyDeferred
		);

	private void ScheduleBeautifyDeferred(Action callback)
	{
		if (_editorOperationShutdownStarted || callback == null || !GodotObject.IsInstanceValid(this) || !IsInsideTree()) return;
		Callable.From(callback).CallDeferred();
	}

	private void SetBeautifyingScript(
		bool isBeautifying,
		BeautifyContextMenuRefreshPolicy refreshPolicy =
			BeautifyContextMenuRefreshPolicy.Always
	)
	{
		if (_isBeautifyingScript == isBeautifying)
			return;

		_isBeautifyingScript = isBeautifying;

		if (
			refreshPolicy == BeautifyContextMenuRefreshPolicy.Always
			|| IsQuickActionsContextMenuHierarchyVisible()
		)
		{
			UpdateQuickActionsContextMenuAvailability();
		}
	}

	private void CancelBeautifyManagedStateForShutdown()
	{
		_isBeautifyingScript = false;
		_isInstallingCSharpier = false;
		_isWarmingUpCSharpierCommandCache = false;
		_isDebugUninstallingCSharpier = false;
		ClearPendingBeautifyAfterCSharpierInstallManaged();
		_beautifyEditorStateService?.CancelPendingRestores();
		_csharpierCommandService = null;
		_csharpierProcessRunner = null;
		CancelBatchScriptEditorContextPreservationForShutdown();
	}

	private void ClearPendingBeautifyAfterCSharpierInstallManaged()
	{
		_pendingBeautifyAfterCSharpierInstallMetadata = "";
		_pendingBeautifyAfterCSharpierInstallScriptPaths = Array.Empty<string>();
		_pendingBeautifyAfterCSharpierInstallIsBatch = false;
		_pendingBeautifyAfterCSharpierInstallInvocationOrigin =
			BeautifyScriptInvocationOrigin.SystemExplorer;
	}

	private sealed class FocusedScriptEditorBeautifyTiming
	{
		private readonly long _totalStartedTimestamp;
		private double _commandLookupMilliseconds;
		private double _firstBufferAndAutosaveMilliseconds;
		private double _csharpierProcessMilliseconds;
		private double _verificationAndSecondBufferMilliseconds;
		private double _applyWriteRefreshMilliseconds;
		private BeautifyBufferLookupRoute _firstLookupRoute;
		private BeautifyBufferLookupRoute _secondLookupRoute;

		internal FocusedScriptEditorBeautifyTiming()
		{
			_totalStartedTimestamp = Stopwatch.GetTimestamp();
		}

		internal long BeginPhase()
		{
			return Stopwatch.GetTimestamp();
		}

		internal void CompleteCommandLookup(long startedTimestamp)
		{
			_commandLookupMilliseconds = GetElapsedMilliseconds(startedTimestamp);
		}

		internal void CompleteFirstBufferAndAutosave(
			long startedTimestamp,
			BeautifyBufferLookupRoute route
		)
		{
			_firstBufferAndAutosaveMilliseconds = GetElapsedMilliseconds(startedTimestamp);
			_firstLookupRoute = route;
		}

		internal void CompleteCSharpierProcess(long startedTimestamp)
		{
			_csharpierProcessMilliseconds = GetElapsedMilliseconds(startedTimestamp);
		}

		internal void CompleteVerificationAndSecondBuffer(
			long startedTimestamp,
			BeautifyBufferLookupRoute route
		)
		{
			_verificationAndSecondBufferMilliseconds = GetElapsedMilliseconds(startedTimestamp);
			_secondLookupRoute = route;
		}

		internal void CompleteApplyWriteRefresh(long startedTimestamp)
		{
			_applyWriteRefreshMilliseconds = GetElapsedMilliseconds(startedTimestamp);
		}

		internal void Log(Action<string> log)
		{
			if (log == null)
				return;

			double totalMilliseconds = GetElapsedMilliseconds(_totalStartedTimestamp);
			log(
				$"Focused Script Editor Beautify timing: total={totalMilliseconds:F2} ms; commandLookup={_commandLookupMilliseconds:F2} ms; firstBufferAndAutosave={_firstBufferAndAutosaveMilliseconds:F2} ms ({_firstLookupRoute}); csharpierProcess={_csharpierProcessMilliseconds:F2} ms; verificationAndSecondBuffer={_verificationAndSecondBufferMilliseconds:F2} ms ({_secondLookupRoute}); applyWriteRefresh={_applyWriteRefreshMilliseconds:F2} ms"
			);
		}

		private double GetElapsedMilliseconds(long startedTimestamp)
		{
			if (startedTimestamp == 0L)
				return 0.0;

			return (Stopwatch.GetTimestamp() - startedTimestamp)
				* 1000.0
				/ Stopwatch.Frequency;
		}
	}

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

	private void OpenBeautifyScriptCSharpierCheckDialog() =>
		StartObservedEditorOperation("Beautify Script", OpenBeautifyScriptOperationAsync);

	private async Task OpenBeautifyScriptOperationAsync(EditorOperationLease operation)
	{
		if (string.IsNullOrWhiteSpace(_pendingBeautifyScriptMetadata) || !_pendingBeautifyScriptMetadata.StartsWith("script::")) return;
		string scriptEntry = GetEntryFromMetadata(_pendingBeautifyScriptMetadata);
		string scriptPath = ScriptPathUtility.Normalize(GetScriptPathFromEntry(scriptEntry));
		if (!FileAccess.FileExists(scriptPath)) { OpenMissingScriptDialog(scriptEntry, scriptPath); return; }
		CSharpierCommand command = await GetCSharpierCommandAsync(operation);
		operation.CancellationToken.ThrowIfCancellationRequested();
		if (!IsEditorOperationAccessValid(operation)) return;
		if (!command.IsValid)
		{
			StorePendingBeautifyAfterCSharpierInstall(
				_pendingBeautifyScriptMetadata,
				new[] { scriptPath },
				false,
				BeautifyScriptInvocationOrigin.SystemExplorer
			);
			OpenCSharpierNotInstalledDialogForPendingBeautify();
			return;
		}
		await BeautifyScriptWithCSharpier(
			operation,
			scriptPath,
			command,
			BeautifyScriptInvocationOrigin.SystemExplorer
		);
	}

	private void OpenFocusedScriptEditorBeautifyCSharpierCheck(
		FocusedScriptEditorBeautifyTarget target
	)
	{
		StartObservedEditorOperation(
			"Beautify Script",
			operation => OpenFocusedScriptEditorBeautifyOperationAsync(operation, target),
			cursorPolicy: EditorOperationCursorPolicy.PreserveCurrent
		);
	}

	private async Task OpenFocusedScriptEditorBeautifyOperationAsync(
		EditorOperationLease operation,
		FocusedScriptEditorBeautifyTarget target
	)
	{
		FocusedScriptEditorBeautifyTiming timing = DebugState
			? new FocusedScriptEditorBeautifyTiming()
			: null;

		try
		{
			string normalized = ScriptPathUtility.Normalize(target.ScriptPath);
			if (
				string.IsNullOrWhiteSpace(normalized)
				|| !normalized.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
				|| !FileAccess.FileExists(normalized)
			)
			{
				return;
			}

			long commandLookupStarted = timing?.BeginPhase() ?? 0L;
			CSharpierCommand command;
			try
			{
				command = await GetCSharpierCommandAsync(operation);
			}
			finally
			{
				timing?.CompleteCommandLookup(commandLookupStarted);
			}

			operation.CancellationToken.ThrowIfCancellationRequested();
			if (!IsEditorOperationAccessValid(operation)) return;
			if (!command.IsValid)
			{
				StorePendingBeautifyAfterCSharpierInstall(
					$"editor-script::{normalized}",
					new[] { normalized },
					false,
					BeautifyScriptInvocationOrigin.FocusedScriptEditor
				);
				OpenCSharpierNotInstalledDialogForPendingBeautify();
				return;
			}

			await BeautifyScriptWithCSharpier(
				operation,
				normalized,
				command,
				BeautifyScriptInvocationOrigin.FocusedScriptEditor,
				target,
				timing
			);
		}
		finally
		{
			timing?.Log(DebugPrintBeautify);
		}
	}

	private void OpenBeautifyScriptsCSharpierCheckDialog() =>
		StartObservedEditorOperation("Beautify Scripts", OpenBeautifyScriptsOperationAsync);

	private async Task OpenBeautifyScriptsOperationAsync(EditorOperationLease operation)
	{
		if (string.IsNullOrWhiteSpace(_pendingBeautifyScriptMetadata) || (!_pendingBeautifyScriptMetadata.StartsWith("system::") && !_pendingBeautifyScriptMetadata.StartsWith("folder::"))) return;
		if (!EnsureSystemsLoadedForTreeOperation("Beautify Scripts")) return;
		string systemName = GetSystemNameFromMetadata(_pendingBeautifyScriptMetadata);
		if (!EnsureSystemAvailable(systemName, "Beautify Scripts")) return;
		List<string> scriptPaths = GetBeautifyScriptPathsForMetadata(_pendingBeautifyScriptMetadata);
		if (scriptPaths.Count == 0) return;
		CSharpierCommand command = await GetCSharpierCommandAsync(operation);
		operation.CancellationToken.ThrowIfCancellationRequested();
		if (!IsEditorOperationAccessValid(operation)) return;
		if (!command.IsValid)
		{
			StorePendingBeautifyAfterCSharpierInstall(
				_pendingBeautifyScriptMetadata,
				scriptPaths,
				true,
				BeautifyScriptInvocationOrigin.SystemExplorer
			);
			OpenCSharpierNotInstalledDialogForPendingBeautify();
			return;
		}
		await BeautifyScriptsWithCSharpier(operation, scriptPaths, command);
	}

	private async Task BeautifyScriptWithCSharpier(
		EditorOperationLease operation,
		string scriptPath,
		CSharpierCommand csharpierCommand,
		BeautifyScriptInvocationOrigin invocationOrigin,
		FocusedScriptEditorBeautifyTarget? capturedEditorTarget = null,
		FocusedScriptEditorBeautifyTiming timing = null
	)
	{
		operation.CancellationToken.ThrowIfCancellationRequested();

		if (
			invocationOrigin == BeautifyScriptInvocationOrigin.SystemExplorer
			&& !EnsureSystemsLoadedForTreeOperation("Beautify Script")
		)
		{
			return;
		}

		BeautifyContextMenuRefreshPolicy refreshPolicy =
			invocationOrigin == BeautifyScriptInvocationOrigin.FocusedScriptEditor
				? BeautifyContextMenuRefreshPolicy.VisibleOnly
				: BeautifyContextMenuRefreshPolicy.Always;

		SetBeautifyingScript(true, refreshPolicy);
		try
		{
			BeautifyScriptOperationResult result = await BeautifySingleScriptWithCSharpier(
				operation,
				scriptPath,
				csharpierCommand,
				"Beautify Script",
				preserveEditorViewState: true,
				capturedEditorTarget: capturedEditorTarget,
				timing: timing
			);
			operation.CancellationToken.ThrowIfCancellationRequested();
			if (!IsEditorOperationAccessValid(operation)) return;

			bool completedWithoutChangesOrFailure =
				result.Status == BeautifyScriptOperationStatus.Formatted
				|| result.Status == BeautifyScriptOperationStatus.Unchanged;

			if (
				invocationOrigin == BeautifyScriptInvocationOrigin.SystemExplorer
				&& completedWithoutChangesOrFailure
			)
			{
				CallDeferred(nameof(ReleaseTreeFocusAfterNavigation));
			}
		}
		finally
		{
			SetBeautifyingScript(false, refreshPolicy);
		}
	}

	private async Task BeautifyScriptsWithCSharpier(
		EditorOperationLease operation,
		IEnumerable<string> scriptPaths,
		CSharpierCommand csharpierCommand
	)
	{
		operation.CancellationToken.ThrowIfCancellationRequested();

		if (!EnsureSystemsLoadedForTreeOperation("Beautify Scripts"))
			return;

		List<string> normalizedScriptPaths = scriptPaths
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Select(ScriptPathUtility.Normalize)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		DebugPrintBeautify(
			$"Beautify Scripts batch started: normalizedCount={normalizedScriptPaths.Count}, command='{CSharpierCommandService.GetCommandDisplayName(csharpierCommand)}'"
		);

		foreach (string normalizedScriptPath in normalizedScriptPaths)
			DebugPrintBeautify($"Beautify Scripts normalized path: {normalizedScriptPath}");

		SetBeautifyingScript(true);
		BeautifyScriptsBatchSummary summary = new();
		BeginBatchScriptEditorContextPreservation();

		try
		{
			foreach (string scriptPath in normalizedScriptPaths)
			{
				operation.CancellationToken.ThrowIfCancellationRequested();
				BeautifyScriptOperationResult result = await BeautifySingleScriptWithCSharpier(
					operation,
					scriptPath,
					csharpierCommand,
					"Beautify Scripts",
					preserveEditorViewState: false
				);
				operation.CancellationToken.ThrowIfCancellationRequested();
				if (!IsEditorOperationAccessValid(operation)) return;

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
			CompleteBatchScriptEditorContextPreservation(operation);
			SetBeautifyingScript(false);
		}
	}

	private ScriptEditorBufferLookupResult LocateBeautifyEditorBuffers(
		string normalizedScriptPath,
		Dictionary<string, string> originalTextsByPath,
		Dictionary<string, string> updatedTextsByPath,
		FocusedScriptEditorBeautifyTarget? capturedEditorTarget,
		out BeautifyBufferLookupRoute lookupRoute
	)
	{
		ScriptEditor scriptEditor = EditorInterface.Singleton?.GetScriptEditor();

		if (capturedEditorTarget.HasValue)
		{
			FocusedScriptEditorBeautifyTarget target = capturedEditorTarget.Value;
			if (
				OpenScriptEditorBufferLocator.TryLocateCapturedEditorWithoutActivation(
					scriptEditor,
					normalizedScriptPath,
					target.ScriptPath,
					target.Script,
					target.ScriptEditorBase,
					target.TextEditor,
					out ScriptEditorBufferLookupResult capturedLookupResult
				)
			)
			{
				lookupRoute = BeautifyBufferLookupRoute.CapturedEditor;
				return capturedLookupResult;
			}
		}

		lookupRoute = BeautifyBufferLookupRoute.GeneralLocator;
		return OpenScriptEditorBufferLocator.LocateByScriptTextsWithoutActivation(
			scriptEditor,
			originalTextsByPath,
			updatedTextsByPath
		);
	}

	private async Task<BeautifyScriptOperationResult> BeautifySingleScriptWithCSharpier(
		EditorOperationLease operation,
		string scriptPath,
		CSharpierCommand csharpierCommand,
		string operationName,
		bool preserveEditorViewState,
		FocusedScriptEditorBeautifyTarget? capturedEditorTarget = null,
		FocusedScriptEditorBeautifyTiming timing = null
	)
	{
		operation.CancellationToken.ThrowIfCancellationRequested();
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

		ScriptTextFileReadResult initialReadResult = ScriptTextFileService.TryReadText(
			normalizedScriptPath
		);

		if (!initialReadResult.IsSuccess)
		{
			DebugPrintBeautify(
				$"{operationName} item initial disk read failed: path='{normalizedScriptPath}', status={initialReadResult.Status}, failureDetail='{GetDebugTextPreview(initialReadResult.FailureDetail)}'"
			);

			return initialReadResult.Status == ScriptTextFileReadStatus.MissingFile
				? BeautifyScriptSkipped(
					normalizedScriptPath,
					$"{operationName} skipped missing script '{normalizedScriptPath}'."
				)
				: BeautifyScriptFailed(
					normalizedScriptPath,
					$"{operationName} failed: '{normalizedScriptPath}' could not be read. The file was left unchanged."
				);
		}

		string diskTextBeforeSync = initialReadResult.Text;
		DebugPrintBeautify(
			$"{operationName} item disk read: path='{normalizedScriptPath}', status={initialReadResult.Status}, originalLength={GetDebugLength(diskTextBeforeSync)}"
		);

		Dictionary<string, string> originalTextsByPath = new(StringComparer.OrdinalIgnoreCase)
		{
			[normalizedScriptPath] = diskTextBeforeSync,
		};
		Dictionary<string, string> pendingTextByPath = new(StringComparer.OrdinalIgnoreCase)
		{
			[normalizedScriptPath] = diskTextBeforeSync,
		};

		ScriptEditorBufferLookupResult lookupResult = null;
		Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath = null;
		string unsafeOpenScriptList = "";
		bool didAutosaveOpenEditor = false;
		string originalText = diskTextBeforeSync;
		BeautifyBufferLookupRoute firstLookupRoute = BeautifyBufferLookupRoute.NotRun;
		long firstBufferAndAutosaveStarted = timing?.BeginPhase() ?? 0L;

		try
		{
			lookupResult = LocateBeautifyEditorBuffers(
				normalizedScriptPath,
				originalTextsByPath,
				pendingTextByPath,
				capturedEditorTarget,
				out firstLookupRoute
			);

			openEditorsByPath = lookupResult.OpenEditorsByPath;
			unsafeOpenScriptList = string.Join("\n", lookupResult.UnsafeOpenScriptPaths);

			DebugPrintBeautify(
				$"{operationName} item open editor scan: matchedOpenEditors={openEditorsByPath.Count}, unsafe='{GetDebugTextPreview(unsafeOpenScriptList)}'"
			);

			if (!string.IsNullOrWhiteSpace(unsafeOpenScriptList))
				return BeautifyScriptSkipped(
					normalizedScriptPath,
					$"{operationName} skipped: System Explorer could not safely match this open script editor buffer. Save/reopen it before formatting:\n{unsafeOpenScriptList}"
				);

			if (
				!BeautifyEditorState.TryAutosaveOpenEditorIfNeeded(
					normalizedScriptPath,
					diskTextBeforeSync,
					openEditorsByPath,
					out originalText,
					out didAutosaveOpenEditor,
					out BeautifyEditorAutosaveFailure autosaveFailure,
					out string autosaveFailureMessage
				)
			)
			{
				string resolvedAutosaveFailureMessage = string.IsNullOrWhiteSpace(autosaveFailureMessage)
					? $"{operationName} skipped '{normalizedScriptPath}' because the open editor buffer could not be autosaved safely."
					: autosaveFailureMessage;

				return autosaveFailure == BeautifyEditorAutosaveFailure.AutosaveVerificationReadFailed
					? BeautifyScriptFailed(normalizedScriptPath, resolvedAutosaveFailureMessage)
					: BeautifyScriptSkipped(normalizedScriptPath, resolvedAutosaveFailureMessage);
			}

			DebugPrintBeautify(
				$"{operationName} item autosave result: didAutosave={didAutosaveOpenEditor}, originalLength={GetDebugLength(originalText)}"
			);

			originalTextsByPath[normalizedScriptPath] = originalText;
			pendingTextByPath[normalizedScriptPath] = originalText;
		}
		finally
		{
			timing?.CompleteFirstBufferAndAutosave(
				firstBufferAndAutosaveStarted,
				firstLookupRoute
			);
		}

		long csharpierProcessStarted = timing?.BeginPhase() ?? 0L;
		CSharpierFormatResult formatResult;
		try
		{
			formatResult = await FormatScriptWithCSharpierUsingCachedCommandFallback(
				operation,
				csharpierCommand,
				normalizedScriptPath,
				operationName
			);
		}
		finally
		{
			timing?.CompleteCSharpierProcess(csharpierProcessStarted);
		}

		operation.CancellationToken.ThrowIfCancellationRequested();
		if (!IsEditorOperationAccessValid(operation)) throw new OperationCanceledException(operation.CancellationToken);

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

		Dictionary<string, string> updatedTextsByPath = new(StringComparer.OrdinalIgnoreCase)
		{
			[normalizedScriptPath] = formattedText,
		};
		BeautifyBufferLookupRoute secondLookupRoute = BeautifyBufferLookupRoute.NotRun;
		long verificationAndSecondBufferStarted = timing?.BeginPhase() ?? 0L;

		try
		{
			ScriptTextFileReadResult currentReadResult = ScriptTextFileService.TryReadText(
				normalizedScriptPath
			);

			if (!currentReadResult.IsSuccess)
			{
				DebugPrintBeautify(
					$"{operationName} item verification read failed after CSharpier: path='{normalizedScriptPath}', status={currentReadResult.Status}, failureDetail='{GetDebugTextPreview(currentReadResult.FailureDetail)}'"
				);
				return BeautifyScriptFailed(
					normalizedScriptPath,
					$"{operationName} failed: System Explorer could not read '{normalizedScriptPath}' to verify its current contents. The formatted text was not written."
				);
			}

			string currentText = currentReadResult.Text;

			if (currentText != originalText)
				return BeautifyScriptFailed(
					normalizedScriptPath,
					$"{operationName} failed: '{normalizedScriptPath}' changed while CSharpier was running. Try again."
				);

			lookupResult = LocateBeautifyEditorBuffers(
				normalizedScriptPath,
				originalTextsByPath,
				updatedTextsByPath,
				capturedEditorTarget,
				out secondLookupRoute
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
				!BeautifyEditorState.ValidateOpenEditorStillMatchesDisk(
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
		}
		finally
		{
			timing?.CompleteVerificationAndSecondBuffer(
				verificationAndSecondBufferStarted,
				secondLookupRoute
			);
		}

		long applyWriteRefreshStarted = timing?.BeginPhase() ?? 0L;
		try
		{
			operation.CancellationToken.ThrowIfCancellationRequested();
			if (!IsEditorOperationAccessValid(operation))
				throw new OperationCanceledException(operation.CancellationToken);

			if (formattedText == originalText)
			{
				if (didAutosaveOpenEditor)
				{
					BeautifyEditorViewState unchangedEditorViewState = preserveEditorViewState
						? BeautifyEditorState.CaptureEditorViewState(normalizedScriptPath, openEditorsByPath)
						: default;

					ScriptResourceRefreshService.RefreshChangedScripts(new[] { normalizedScriptPath });
					BeautifyEditorState.RestoreEditorViewStateNowAndDeferred(unchangedEditorViewState);
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
				? BeautifyEditorState.CaptureEditorViewState(normalizedScriptPath, openEditorsByPath)
				: default;

			if (
				!BeautifyEditorState.TryApplyTextToEditorBeforeDiskWrite(
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
				BeautifyEditorState.RestoreOpenEditorAfterFailedWrite(
					normalizedScriptPath,
					originalText,
					openEditorsByPath
				);
				ScriptResourceRefreshService.RefreshChangedScripts(new[] { normalizedScriptPath });
				BeautifyEditorState.RestoreEditorViewStateNowAndDeferred(editorViewState);
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
			BeautifyEditorState.RestoreEditorViewStateNowAndDeferred(editorViewState);

			DebugLogger.LogOperation($"{operationName} Completed", normalizedScriptPath);
			return new BeautifyScriptOperationResult(
				BeautifyScriptOperationStatus.Formatted,
				normalizedScriptPath
			);
		}
		finally
		{
			timing?.CompleteApplyWriteRefresh(applyWriteRefreshStarted);
		}

	}

	private void StorePendingBeautifyAfterCSharpierInstall(
		string metadata,
		IEnumerable<string> scriptPaths,
		bool isBatch,
		BeautifyScriptInvocationOrigin invocationOrigin
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
		_pendingBeautifyAfterCSharpierInstallInvocationOrigin = invocationOrigin;

		DebugPrintBeautify(
			$"Stored pending Beautify after CSharpier install: metadata='{_pendingBeautifyAfterCSharpierInstallMetadata}', isBatch={isBatch}, origin={invocationOrigin}, scriptCount={_pendingBeautifyAfterCSharpierInstallScriptPaths.Length}"
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
		_pendingBeautifyAfterCSharpierInstallInvocationOrigin =
			BeautifyScriptInvocationOrigin.SystemExplorer;
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
		EditorOperationLease operation,
		CSharpierCommand csharpierCommand
	)
	{
		if (!csharpierCommand.IsValid || !HasPendingBeautifyAfterCSharpierInstall())
			return false;

		bool isBatch = _pendingBeautifyAfterCSharpierInstallIsBatch;
		BeautifyScriptInvocationOrigin invocationOrigin =
			_pendingBeautifyAfterCSharpierInstallInvocationOrigin;
		string metadata = _pendingBeautifyAfterCSharpierInstallMetadata;
		string[] scriptPaths = _pendingBeautifyAfterCSharpierInstallScriptPaths.ToArray();

		ClearPendingBeautifyAfterCSharpierInstall("running pending Beautify");

		DebugPrintBeautify(
			$"Running pending Beautify after CSharpier install: metadata='{metadata}', isBatch={isBatch}, origin={invocationOrigin}, scriptCount={scriptPaths.Length}"
		);

		if (isBatch)
			await BeautifyScriptsWithCSharpier(operation, scriptPaths, csharpierCommand);
		else
			await BeautifyScriptWithCSharpier(
				operation,
				scriptPaths[0],
				csharpierCommand,
				invocationOrigin
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
