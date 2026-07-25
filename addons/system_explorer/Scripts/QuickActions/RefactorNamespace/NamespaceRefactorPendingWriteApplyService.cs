#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal enum NamespaceRefactorAffectedOpenBufferMatchMode
{
	ActivatingOnly,
	NonActivatingOnly,
	NonActivatingWithActivationFallback,
}

internal enum NamespaceRefactorPendingWriteApplyFailure
{
	None,
	AffectedOpenBufferMatchFailed,
	UnsafeNonActivatingBufferMatch,
	AutosaveFailed,
	RebuildAfterAutosaveFailed,
	AffectedOpenBufferRematchAfterAutosaveFailed,
	StillUnsaved,
}

internal sealed class NamespaceRefactorPendingWriteApplyResult
{
	internal NamespaceRefactorPendingWriteApplyFailure Failure { get; }
	internal bool Success => Failure == NamespaceRefactorPendingWriteApplyFailure.None;
	internal bool DidAutosave { get; }
	internal NamespaceRefactorPendingWriteSet FinalWriteSet { get; }
	internal NamespaceRefactorPendingWriteSet AppliedWriteSet { get; }
	internal IReadOnlyList<string> FailedWritePaths { get; }
	internal int IntendedWritePathCount { get; }
	internal string AffectedOpenBufferFailureMessage { get; }
	internal IReadOnlyList<string> UnsafeOpenScriptPaths { get; }
	internal IReadOnlyList<string> UnsavedScriptPaths { get; }
	internal ScriptEditorBufferAutosaveResult FailedAutosave { get; }

	private NamespaceRefactorPendingWriteApplyResult(
		NamespaceRefactorPendingWriteApplyFailure failure,
		bool didAutosave,
		NamespaceRefactorPendingWriteSet finalWriteSet,
		NamespaceRefactorPendingWriteSet appliedWriteSet,
		IEnumerable<string> failedWritePaths,
		int intendedWritePathCount,
		string affectedOpenBufferFailureMessage,
		IEnumerable<string> unsafeOpenScriptPaths,
		IEnumerable<string> unsavedScriptPaths,
		ScriptEditorBufferAutosaveResult failedAutosave
	)
	{
		Failure = failure;
		DidAutosave = didAutosave;
		FinalWriteSet = finalWriteSet;
		AppliedWriteSet = appliedWriteSet;
		FailedWritePaths = CreateReadOnlyList(failedWritePaths);
		IntendedWritePathCount = Math.Max(0, intendedWritePathCount);
		AffectedOpenBufferFailureMessage = affectedOpenBufferFailureMessage ?? "";
		UnsafeOpenScriptPaths = CreateReadOnlyList(unsafeOpenScriptPaths);
		UnsavedScriptPaths = CreateReadOnlyList(unsavedScriptPaths);
		FailedAutosave = failedAutosave;
	}

	internal static NamespaceRefactorPendingWriteApplyResult Completed(
		bool didAutosave,
		NamespaceRefactorPendingWriteSet finalWriteSet,
		NamespaceRefactorPendingWriteSet appliedWriteSet,
		IEnumerable<string> failedWritePaths,
		int intendedWritePathCount
	)
	{
		return new NamespaceRefactorPendingWriteApplyResult(
			NamespaceRefactorPendingWriteApplyFailure.None,
			didAutosave,
			finalWriteSet,
			appliedWriteSet,
			failedWritePaths,
			intendedWritePathCount,
			"",
			null,
			null,
			default
		);
	}

	internal static NamespaceRefactorPendingWriteApplyResult Failed(
		NamespaceRefactorPendingWriteApplyFailure failure,
		bool didAutosave,
		NamespaceRefactorPendingWriteSet finalWriteSet,
		string affectedOpenBufferFailureMessage = "",
		IEnumerable<string> unsafeOpenScriptPaths = null,
		IEnumerable<string> unsavedScriptPaths = null,
		ScriptEditorBufferAutosaveResult failedAutosave = default
	)
	{
		return new NamespaceRefactorPendingWriteApplyResult(
			failure,
			didAutosave,
			finalWriteSet,
			null,
			null,
			finalWriteSet?.PendingWrites.Count ?? 0,
			affectedOpenBufferFailureMessage,
			unsafeOpenScriptPaths,
			unsavedScriptPaths,
			failedAutosave
		);
	}

	private static IReadOnlyList<string> CreateReadOnlyList(IEnumerable<string> source)
	{
		List<string> copy = source == null ? new List<string>() : new List<string>(source);
		return copy.AsReadOnly();
	}
}

internal sealed class NamespaceRefactorPendingWriteApplyService
{
	private readonly NamespaceOpenBufferActivationService _openBufferActivationService;
	private readonly ScriptEditorBufferLocator _bufferLocator;
	private readonly ScriptEditorBufferAutosaveCoordinator _autosaveCoordinator;
	private readonly ScriptEditorBufferBatchService _bufferBatchService;
	private readonly Func<string, string, bool> _writeText;
	private readonly Action<IEnumerable<string>> _refreshChangedScripts;

	internal NamespaceRefactorPendingWriteApplyService(
		NamespaceOpenBufferActivationService openBufferActivationService,
		ScriptEditorBufferLocator bufferLocator,
		ScriptEditorBufferAutosaveCoordinator autosaveCoordinator,
		ScriptEditorBufferBatchService bufferBatchService,
		Func<string, string, bool> writeText,
		Action<IEnumerable<string>> refreshChangedScripts
	)
	{
		_openBufferActivationService =
			openBufferActivationService
			?? throw new ArgumentNullException(nameof(openBufferActivationService));
		_bufferLocator = bufferLocator ?? throw new ArgumentNullException(nameof(bufferLocator));
		_autosaveCoordinator =
			autosaveCoordinator ?? throw new ArgumentNullException(nameof(autosaveCoordinator));
		_bufferBatchService =
			bufferBatchService ?? throw new ArgumentNullException(nameof(bufferBatchService));
		_writeText = writeText ?? throw new ArgumentNullException(nameof(writeText));
		_refreshChangedScripts =
			refreshChangedScripts
			?? throw new ArgumentNullException(nameof(refreshChangedScripts));
	}

	internal NamespaceRefactorPendingWriteApplyResult TryApplyPendingWrites(
		EditorInterface editorInterface,
		ScriptEditor scriptEditor,
		NamespaceRefactorPendingWriteSet initialWriteSet,
		NamespaceRefactorAffectedOpenBufferMatchMode matchMode,
		Func<NamespaceRefactorPendingWriteBuildResult> rebuildAfterAutosave,
		Action<string> debugLog,
		NamespaceRefactorDiagnosticContext diagnosticContext
	)
	{
		if (initialWriteSet == null)
			throw new ArgumentNullException(nameof(initialWriteSet));

		diagnosticContext?.Log(
			"Apply",
			() =>
				$"Pending-write apply started; MatchMode={matchMode}; PendingWriteCount={initialWriteSet.PendingWrites.Count}; ReplacePlan={initialWriteSet.ReplaceWritePlan != null}; RebuildAfterAutosave={rebuildAfterAutosave != null}; PendingPaths={diagnosticContext.FormatPaths(initialWriteSet.PendingWrites.Keys)}"
		);

		NamespaceRefactorPendingWriteSet finalWriteSet = initialWriteSet;

		if (
			!TryMatchAffectedOpenBuffers(
				editorInterface,
				scriptEditor,
				finalWriteSet,
				matchMode,
				debugLog,
				diagnosticContext,
				out Dictionary<string, OpenScriptEditorBufferGroup> openEditorGroupsByPath,
				out string affectedOpenBufferFailureMessage,
				out IReadOnlyList<string> unsafeOpenScriptPaths
			)
		)
		{
			diagnosticContext?.Log(
				"BufferLookup",
				() => $"Initial affected-buffer match failed; MatchMode={matchMode}; Failure={GetInitialMatchFailure(matchMode)}; UnsafePaths={diagnosticContext.FormatPaths(unsafeOpenScriptPaths)}; Message='{affectedOpenBufferFailureMessage}'"
			);
			return NamespaceRefactorPendingWriteApplyResult.Failed(
				failure: GetInitialMatchFailure(matchMode),
				didAutosave: false,
				finalWriteSet: finalWriteSet,
				affectedOpenBufferFailureMessage: affectedOpenBufferFailureMessage,
				unsafeOpenScriptPaths: unsafeOpenScriptPaths
			);
		}

		diagnosticContext?.Log(
			"BufferLookup",
			() => $"Initial affected-buffer match succeeded; GroupCount={openEditorGroupsByPath?.Count ?? 0}; Groups={FormatGroupCounts(openEditorGroupsByPath, diagnosticContext)}"
		);

		ScriptEditorBufferAutosaveOperationResult autosaveResult =
			_autosaveCoordinator.TryAutosaveGroupBatchIfNeeded(
				openEditorGroupsByPath,
				diagnosticContext?.BufferDiagnostics
			);
		diagnosticContext?.Log(
			"Autosave",
			() => $"Pending-write autosave completed; Success={autosaveResult.Success}; DidAutosave={autosaveResult.DidAutosave}; Failure={autosaveResult.FailedAutosave.Failure}; DiagnosticReason={autosaveResult.FailedAutosave.DiagnosticReason}; FailurePath='{autosaveResult.FailedAutosave.ScriptPath}'"
		);

		if (!autosaveResult.Success)
		{
			return NamespaceRefactorPendingWriteApplyResult.Failed(
				failure: NamespaceRefactorPendingWriteApplyFailure.AutosaveFailed,
				didAutosave: autosaveResult.DidAutosave,
				finalWriteSet: finalWriteSet,
				failedAutosave: autosaveResult.FailedAutosave
			);
		}

		bool didAutosave = autosaveResult.DidAutosave;

		if (didAutosave && rebuildAfterAutosave != null)
		{
			diagnosticContext?.Log("Plan", "Rebuild after autosave started.");
			NamespaceRefactorPendingWriteBuildResult rebuildResult = rebuildAfterAutosave();

			if (rebuildResult == null || !rebuildResult.Success || rebuildResult.WriteSet == null)
			{
				diagnosticContext?.Log(
					"Plan",
					() => $"Rebuild after autosave failed; ResultNull={rebuildResult == null}; Success={rebuildResult?.Success ?? false}; WriteSetNull={rebuildResult?.WriteSet == null}."
				);
				return NamespaceRefactorPendingWriteApplyResult.Failed(
					failure: NamespaceRefactorPendingWriteApplyFailure.RebuildAfterAutosaveFailed,
					didAutosave: didAutosave,
					finalWriteSet: finalWriteSet
				);
			}

			finalWriteSet = rebuildResult.WriteSet;
			diagnosticContext?.Log(
				"Plan",
				() => $"Rebuild after autosave succeeded; PendingWriteCount={finalWriteSet.PendingWrites.Count}; PendingPaths={diagnosticContext.FormatPaths(finalWriteSet.PendingWrites.Keys)}"
			);

			if (
				!TryMatchAffectedOpenBuffers(
					editorInterface,
					scriptEditor,
					finalWriteSet,
					matchMode,
					debugLog,
					diagnosticContext,
					out openEditorGroupsByPath,
					out affectedOpenBufferFailureMessage,
					out unsafeOpenScriptPaths
				)
			)
			{
				diagnosticContext?.Log(
					"BufferLookup",
					() => $"Affected-buffer rematch after autosave failed; UnsafePaths={diagnosticContext.FormatPaths(unsafeOpenScriptPaths)}; Message='{affectedOpenBufferFailureMessage}'"
				);
				return NamespaceRefactorPendingWriteApplyResult.Failed(
					failure: NamespaceRefactorPendingWriteApplyFailure.AffectedOpenBufferRematchAfterAutosaveFailed,
					didAutosave: didAutosave,
					finalWriteSet: finalWriteSet,
					affectedOpenBufferFailureMessage: affectedOpenBufferFailureMessage,
					unsafeOpenScriptPaths: unsafeOpenScriptPaths
				);
			}
		}

		IReadOnlyList<string> unsavedPaths = _bufferBatchService.GetUnsavedPaths(
			openEditorGroupsByPath?.Values
		);

		diagnosticContext?.Log(
			"Autosave",
			() => $"Post-autosave unsaved verification completed; UnsavedCount={unsavedPaths.Count}; UnsavedPaths={diagnosticContext.FormatPaths(unsavedPaths)}"
		);

		if (unsavedPaths.Count > 0)
		{
			return NamespaceRefactorPendingWriteApplyResult.Failed(
				failure: NamespaceRefactorPendingWriteApplyFailure.StillUnsaved,
				didAutosave: didAutosave,
				finalWriteSet: finalWriteSet,
				unsavedScriptPaths: unsavedPaths
			);
		}

		PendingWriteOutcome writeOutcome = finalWriteSet.ReplaceWritePlan == null
			? ApplyStandardWrites(finalWriteSet, debugLog, diagnosticContext)
			: ApplyStagedReplaceWrites(finalWriteSet, debugLog, diagnosticContext);
		diagnosticContext?.Log(
			"Write",
			() => $"Write phase completed; IntendedWritePathCount={writeOutcome.IntendedWritePathCount}; AppliedPendingWriteCount={writeOutcome.AppliedPendingWrites.Count}; FailedWritePathCount={writeOutcome.FailedWritePaths.Count}; AppliedPaths={diagnosticContext.FormatPaths(writeOutcome.AppliedPendingWrites.Keys)}; FailedPaths={diagnosticContext.FormatPaths(writeOutcome.FailedWritePaths)}"
		);

		var appliedWriteSet = new NamespaceRefactorPendingWriteSet(
			finalWriteSet.SelectedScriptPath,
			writeOutcome.AppliedOriginalTexts,
			writeOutcome.AppliedPendingWrites
		);

		if (writeOutcome.AppliedPendingWrites.Count > 0)
		{
			diagnosticContext?.Log(
				"ImmediateSync",
				() => $"ApplyCommittedTexts started; PathCount={writeOutcome.AppliedPendingWrites.Count}; Groups={FormatGroupCounts(openEditorGroupsByPath, diagnosticContext)}"
			);
			_bufferBatchService.ApplyCommittedTexts(
				openEditorGroupsByPath,
				writeOutcome.AppliedPendingWrites,
				diagnosticContext?.BufferDiagnostics
			);
			diagnosticContext?.Log("ImmediateSync", "ApplyCommittedTexts completed.");
			_refreshChangedScripts(writeOutcome.AppliedPendingWrites.Keys);
			diagnosticContext?.Log(
				"ResourceRefresh",
				() => $"Changed script resource refresh requested; Paths={diagnosticContext.FormatPaths(writeOutcome.AppliedPendingWrites.Keys)}"
			);
		}

		return NamespaceRefactorPendingWriteApplyResult.Completed(
			didAutosave,
			finalWriteSet,
			appliedWriteSet,
			writeOutcome.FailedWritePaths,
			writeOutcome.IntendedWritePathCount
		);
	}

	private sealed class PendingWriteOutcome
	{
		internal Dictionary<string, string> AppliedOriginalTexts { get; } = new(
			StringComparer.OrdinalIgnoreCase
		);
		internal Dictionary<string, string> AppliedPendingWrites { get; } = new(
			StringComparer.OrdinalIgnoreCase
		);
		internal List<string> FailedWritePaths { get; } = new();
		internal HashSet<string> FailedWritePathSet { get; } = new(
			StringComparer.OrdinalIgnoreCase
		);
		internal HashSet<string> IntendedWritePaths { get; } = new(
			StringComparer.OrdinalIgnoreCase
		);
		internal int IntendedWritePathCount => IntendedWritePaths.Count;
	}

	private PendingWriteOutcome ApplyStandardWrites(
		NamespaceRefactorPendingWriteSet writeSet,
		Action<string> debugLog,
		NamespaceRefactorDiagnosticContext diagnosticContext
	)
	{
		var outcome = new PendingWriteOutcome();

		foreach (KeyValuePair<string, string> pendingWrite in writeSet.PendingWrites)
		{
			outcome.IntendedWritePaths.Add(pendingWrite.Key);
			diagnosticContext?.Log("Write", () => $"Standard write started; Path='{pendingWrite.Key}'.");

			if (!_writeText(pendingWrite.Key, pendingWrite.Value))
			{
				AddFailedPath(outcome, pendingWrite.Key);
				debugLog?.Invoke(
					$"Refactor Namespace write failed for '{pendingWrite.Key}'."
				);
				diagnosticContext?.Log("Write", () => $"Standard write failed; Path='{pendingWrite.Key}'.");
				continue;
			}

			RecordAppliedText(
				outcome,
				pendingWrite.Key,
				GetOriginalText(writeSet, pendingWrite.Key),
				pendingWrite.Value
			);
			diagnosticContext?.Log("Write", () => $"Standard write succeeded; Path='{pendingWrite.Key}'.");
		}

		return outcome;
	}

	private PendingWriteOutcome ApplyStagedReplaceWrites(
		NamespaceRefactorPendingWriteSet writeSet,
		Action<string> debugLog,
		NamespaceRefactorDiagnosticContext diagnosticContext
	)
	{
		NamespaceRefactorReplaceWritePlan replacePlan = writeSet.ReplaceWritePlan;
		var outcome = new PendingWriteOutcome();
		HashSet<string> initiallyIncompletePathSet = new(
			replacePlan.InitiallyIncompleteDeclarationPaths,
			StringComparer.OrdinalIgnoreCase
		);
		HashSet<string> plannedDeclarationPathSet = new(
			replacePlan.ConservativeDeclarationWrites.Keys,
			StringComparer.OrdinalIgnoreCase
		);
		HashSet<string> successfulDeclarationPathSet = new(
			StringComparer.OrdinalIgnoreCase
		);
		bool declarationWriteFailed = false;
		diagnosticContext?.Log(
			"Write",
			() => $"Staged replace writes started; DeclarationCount={replacePlan.DeclarationPathsInOrder.Count}; ReferenceCount={replacePlan.ReferenceOriginalTextsByPath.Count}; InitiallyIncomplete={diagnosticContext.FormatPaths(replacePlan.InitiallyIncompleteDeclarationPaths)}"
		);

		// Phase A/B: attempt each declaration once with its conservative text.
		foreach (string declarationPath in replacePlan.DeclarationPathsInOrder)
		{
			outcome.IntendedWritePaths.Add(declarationPath);

			if (initiallyIncompletePathSet.Contains(declarationPath))
			{
				diagnosticContext?.Log(
					"Write",
					() => $"Staged declaration write skipped; Path='{declarationPath}'; Status=PreparationFailed."
				);
				AddFailedPath(outcome, declarationPath);
				debugLog?.Invoke(
					$"Refactor Namespace declaration could not be prepared for '{declarationPath}'."
				);
				continue;
			}

			if (
				!replacePlan.ConservativeDeclarationWrites.TryGetValue(
					declarationPath,
					out string conservativeText
				)
			)
			{
				diagnosticContext?.Log(
					"Write",
					() => $"Staged declaration write skipped; Path='{declarationPath}'; Status=NoConservativeText."
				);
				continue;
			}

			diagnosticContext?.Log(
				"Write",
				() => $"Staged declaration write started; Path='{declarationPath}'; Stage=ConservativeDeclaration."
			);
			if (!_writeText(declarationPath, conservativeText))
			{
				declarationWriteFailed = true;
				AddFailedPath(outcome, declarationPath);
				debugLog?.Invoke(
					$"Refactor Namespace declaration write failed for '{declarationPath}'."
				);
				diagnosticContext?.Log(
					"Write",
					() => $"Staged declaration write failed; Path='{declarationPath}'; Stage=ConservativeDeclaration."
				);
				continue;
			}

			diagnosticContext?.Log(
				"Write",
				() => $"Staged declaration write succeeded; Path='{declarationPath}'; Stage=ConservativeDeclaration."
			);
			successfulDeclarationPathSet.Add(declarationPath);
			RecordAppliedText(
				outcome,
				declarationPath,
				GetOriginalText(writeSet, declarationPath),
				conservativeText
			);
		}

		if (successfulDeclarationPathSet.Count == 0)
		{
			diagnosticContext?.Log(
				"Write",
				"Staged replace writes ended before cleanup/reference stages; no declaration write succeeded."
			);
			return outcome;
		}

		bool oldNamespaceRemains =
			replacePlan.OldNamespaceRemainsWithoutPhysicalWriteFailures
			|| declarationWriteFailed;
		diagnosticContext?.Log(
			"Write",
			() =>
				$"Staged declaration phase completed; SuccessfulDeclarationCount={successfulDeclarationPathSet.Count}; DeclarationWriteFailed={declarationWriteFailed}; OldNamespaceRemainsWithoutPhysicalWriteFailures={replacePlan.OldNamespaceRemainsWithoutPhysicalWriteFailures}; OldNamespaceRemains={oldNamespaceRemains}."
		);

		// Phase C cleanup: only successful declaration writes may receive a second write.
		if (!oldNamespaceRemains)
		{
			foreach (string declarationPath in replacePlan.DeclarationPathsInOrder)
			{
				if (!successfulDeclarationPathSet.Contains(declarationPath))
					continue;

				string conservativeText = outcome.AppliedPendingWrites[declarationPath];
				string cleanupText = NamespaceTextRewriter.ReplaceUsingStatements(
					conservativeText,
					replacePlan.OldNamespace,
					replacePlan.NewNamespace,
					out bool cleanupChanged
				);

				if (!cleanupChanged || cleanupText == conservativeText)
				{
					diagnosticContext?.Log(
						"Write",
						() => $"Staged cleanup write skipped; Path='{declarationPath}'; Status=NoTextChange."
					);
					continue;
				}

				diagnosticContext?.Log(
					"Write",
					() => $"Staged cleanup write started; Path='{declarationPath}'."
				);
				if (!_writeText(declarationPath, cleanupText))
				{
					AddFailedPath(outcome, declarationPath);
					debugLog?.Invoke(
						$"Refactor Namespace using cleanup write failed for '{declarationPath}'."
					);
					diagnosticContext?.Log(
						"Write",
						() => $"Staged cleanup write failed; Path='{declarationPath}'."
					);
					continue;
				}

				diagnosticContext?.Log(
					"Write",
					() => $"Staged cleanup write succeeded; Path='{declarationPath}'."
				);
				outcome.AppliedPendingWrites[declarationPath] = cleanupText;
			}
		}
		else
		{
			diagnosticContext?.Log(
				"Write",
				"Staged cleanup phase skipped because the old namespace remains after declaration planning/write results."
			);
		}

		// Phase C references: derive each reference-only text from the actual declaration result.
		foreach (
			KeyValuePair<string, string> referenceOriginal
			in replacePlan.ReferenceOriginalTextsByPath
		)
		{
			if (
				plannedDeclarationPathSet.Contains(referenceOriginal.Key)
				|| initiallyIncompletePathSet.Contains(referenceOriginal.Key)
			)
			{
				continue;
			}

			bool usingChanged;
			string referenceText = oldNamespaceRemains
				? NamespaceTextRewriter.AddUsingStatementIfMissing(
					referenceOriginal.Value,
					replacePlan.NewNamespace,
					replacePlan.OldNamespace,
					out usingChanged
				)
				: NamespaceTextRewriter.ReplaceUsingStatements(
					referenceOriginal.Value,
					replacePlan.OldNamespace,
					replacePlan.NewNamespace,
					out usingChanged
				);

			if (!usingChanged)
			{
				diagnosticContext?.Log(
					"Write",
					() => $"Staged reference write skipped; Path='{referenceOriginal.Key}'; Status=NoTextChange; OldNamespaceRemains={oldNamespaceRemains}."
				);
				continue;
			}

			outcome.IntendedWritePaths.Add(referenceOriginal.Key);
			diagnosticContext?.Log(
				"Write",
				() => $"Staged reference write started; Path='{referenceOriginal.Key}'; OldNamespaceRemains={oldNamespaceRemains}."
			);

			if (!_writeText(referenceOriginal.Key, referenceText))
			{
				AddFailedPath(outcome, referenceOriginal.Key);
				debugLog?.Invoke(
					$"Refactor Namespace reference write failed for '{referenceOriginal.Key}'."
				);
				diagnosticContext?.Log(
					"Write",
					() => $"Staged reference write failed; Path='{referenceOriginal.Key}'."
				);
				continue;
			}

			diagnosticContext?.Log(
				"Write",
				() => $"Staged reference write succeeded; Path='{referenceOriginal.Key}'."
			);
			RecordAppliedText(
				outcome,
				referenceOriginal.Key,
				referenceOriginal.Value,
				referenceText
			);
		}

		diagnosticContext?.Log(
			"Write",
			() =>
				$"Staged replace writes completed; IntendedWritePathCount={outcome.IntendedWritePathCount}; AppliedPathCount={outcome.AppliedPendingWrites.Count}; FailedPathCount={outcome.FailedWritePaths.Count}; AppliedPaths={diagnosticContext.FormatPaths(outcome.AppliedPendingWrites.Keys)}; FailedPaths={diagnosticContext.FormatPaths(outcome.FailedWritePaths)}."
		);
		return outcome;
	}

	private static void RecordAppliedText(
		PendingWriteOutcome outcome,
		string path,
		string originalText,
		string appliedText
	)
	{
		outcome.AppliedOriginalTexts[path] = originalText ?? "";
		outcome.AppliedPendingWrites[path] = appliedText ?? "";
	}

	private static void AddFailedPath(PendingWriteOutcome outcome, string path)
	{
		if (string.IsNullOrWhiteSpace(path) || !outcome.FailedWritePathSet.Add(path))
			return;

		outcome.FailedWritePaths.Add(path);
	}

	private static string GetOriginalText(
		NamespaceRefactorPendingWriteSet writeSet,
		string path
	)
	{
		if (writeSet.OriginalTextsByPath.TryGetValue(path, out string originalText))
			return originalText;

		if (
			writeSet.ReplaceWritePlan?.ReferenceOriginalTextsByPath.TryGetValue(
				path,
				out originalText
			) == true
		)
		{
			return originalText;
		}

		return "";
	}

	private bool TryMatchAffectedOpenBuffers(
		EditorInterface editorInterface,
		ScriptEditor scriptEditor,
		NamespaceRefactorPendingWriteSet writeSet,
		NamespaceRefactorAffectedOpenBufferMatchMode matchMode,
		Action<string> debugLog,
		NamespaceRefactorDiagnosticContext diagnosticContext,
		out Dictionary<string, OpenScriptEditorBufferGroup> openEditorGroupsByPath,
		out string affectedOpenBufferFailureMessage,
		out IReadOnlyList<string> unsafeOpenScriptPaths
	)
	{
		affectedOpenBufferFailureMessage = "";
		unsafeOpenScriptPaths = Array.Empty<string>();

		switch (matchMode)
		{
			case NamespaceRefactorAffectedOpenBufferMatchMode.ActivatingOnly:
				if (
					!_openBufferActivationService.TryGetOpenScriptEditorsByActivatingPaths(
						editorInterface,
						scriptEditor,
						writeSet.PendingWrites.Keys,
						failIfOpenEditorCannotBeMatched: true,
						debugLog: debugLog,
						openEditorsByPath: out Dictionary<string, OpenScriptEditorBuffer> activatedEditorsByPath,
						failureMessage: out affectedOpenBufferFailureMessage
					)
				)
				{
					openEditorGroupsByPath = new Dictionary<string, OpenScriptEditorBufferGroup>(
						StringComparer.OrdinalIgnoreCase
					);
					return false;
				}

				openEditorGroupsByPath = WrapSingleOpenEditorsAsGroups(
					activatedEditorsByPath
				);
				return true;
			case NamespaceRefactorAffectedOpenBufferMatchMode.NonActivatingOnly:
				return TryMatchAffectedOpenBuffersWithoutActivation(
					scriptEditor,
					writeSet,
					diagnosticContext,
					out openEditorGroupsByPath,
					out affectedOpenBufferFailureMessage,
					out unsafeOpenScriptPaths
				);
			case NamespaceRefactorAffectedOpenBufferMatchMode.NonActivatingWithActivationFallback:
				return TryMatchAffectedOpenBuffersWithActivationFallback(
					editorInterface,
					scriptEditor,
					writeSet,
					debugLog,
					diagnosticContext,
					out openEditorGroupsByPath,
					out affectedOpenBufferFailureMessage,
					out unsafeOpenScriptPaths
				);
			default:
				throw new ArgumentOutOfRangeException(nameof(matchMode), matchMode, null);
		}
	}

	private bool TryMatchAffectedOpenBuffersWithoutActivation(
		ScriptEditor scriptEditor,
		NamespaceRefactorPendingWriteSet writeSet,
		NamespaceRefactorDiagnosticContext diagnosticContext,
		out Dictionary<string, OpenScriptEditorBufferGroup> openEditorGroupsByPath,
		out string affectedOpenBufferFailureMessage,
		out IReadOnlyList<string> unsafeOpenScriptPaths
	)
	{
		ScriptEditorBufferGroupLookupResult lookupResult =
			_bufferLocator.LocateOpenScriptEditorGroupsByScriptTextsWithoutActivation(
				scriptEditor,
				writeSet.OriginalTextsByPath,
				writeSet.PendingWrites,
				writeSet.PendingWrites.Keys,
				diagnosticContext?.BufferDiagnostics
			);

		openEditorGroupsByPath = lookupResult.OpenEditorGroupsByPath;
		unsafeOpenScriptPaths = lookupResult.UnsafeOpenScriptPaths;
		affectedOpenBufferFailureMessage =
			NamespaceOpenBufferLookupService.BuildScriptEditorBufferLookupFailureMessage(
				lookupResult
			);
		return lookupResult.Success && unsafeOpenScriptPaths.Count == 0;
	}

	private bool TryMatchAffectedOpenBuffersWithActivationFallback(
		EditorInterface editorInterface,
		ScriptEditor scriptEditor,
		NamespaceRefactorPendingWriteSet writeSet,
		Action<string> debugLog,
		NamespaceRefactorDiagnosticContext diagnosticContext,
		out Dictionary<string, OpenScriptEditorBufferGroup> openEditorGroupsByPath,
		out string affectedOpenBufferFailureMessage,
		out IReadOnlyList<string> unsafeOpenScriptPaths
	)
	{
		ScriptEditorBufferGroupLookupResult lookupResult =
			_bufferLocator.LocateOpenScriptEditorGroupsByScriptTextsWithoutActivation(
				scriptEditor,
				writeSet.OriginalTextsByPath,
				writeSet.PendingWrites,
				writeSet.PendingWrites.Keys,
				diagnosticContext?.BufferDiagnostics
			);

		openEditorGroupsByPath = new Dictionary<string, OpenScriptEditorBufferGroup>(
			StringComparer.OrdinalIgnoreCase
		);
		affectedOpenBufferFailureMessage =
			NamespaceOpenBufferLookupService.BuildScriptEditorBufferLookupFailureMessage(
				lookupResult
			);
		unsafeOpenScriptPaths = lookupResult.UnsafeOpenScriptPaths;

		if (
			!TryMergeOpenEditorBufferGroups(
				openEditorGroupsByPath,
				lookupResult.OpenEditorGroupsByPath,
				out string mergeFailureMessage
			)
		)
		{
			affectedOpenBufferFailureMessage = mergeFailureMessage;
			return false;
		}

		if (
			lookupResult.Failure
			== ScriptEditorBufferLookupFailure.AmbiguousRequiredOpenBufferGroup
			|| lookupResult.AmbiguousOpenScriptPaths.Count > 0
		)
		{
			if (string.IsNullOrWhiteSpace(affectedOpenBufferFailureMessage))
			{
				string ambiguousPath = lookupResult.AmbiguousOpenScriptPaths[0];
				affectedOpenBufferFailureMessage =
					$"Refactor Namespace cancelled: System Explorer found multiple open script entries for '{ambiguousPath}', but could not safely verify every editor buffer as the same saved script. Save or close the duplicate entries before refactoring.";
			}

			return false;
		}

		if (unsafeOpenScriptPaths.Count == 0)
			return true;

		HashSet<string> unsafeOpenScriptPathSet = new(
			unsafeOpenScriptPaths,
			StringComparer.OrdinalIgnoreCase
		);
		List<string> activationFallbackPaths = new();

		foreach (string pendingWritePath in writeSet.PendingWrites.Keys)
		{
			if (
				!unsafeOpenScriptPathSet.Contains(pendingWritePath)
				|| openEditorGroupsByPath.ContainsKey(pendingWritePath)
				|| !_openBufferActivationService.IsScriptOpen(scriptEditor, pendingWritePath)
			)
			{
				continue;
			}

			activationFallbackPaths.Add(pendingWritePath);
		}

		if (activationFallbackPaths.Count == 0)
			return false;

		debugLog?.Invoke(
			$"Refactor Namespace apply could not match {activationFallbackPaths.Count} affected open single-buffer path(s) without activation; activating only those buffers."
		);

		if (
			!_openBufferActivationService.TryGetOpenScriptEditorsByActivatingPaths(
				editorInterface,
				scriptEditor,
				activationFallbackPaths,
				failIfOpenEditorCannotBeMatched: true,
				debugLog: debugLog,
				openEditorsByPath: out Dictionary<string, OpenScriptEditorBuffer> activatedEditorsByPath,
				failureMessage: out affectedOpenBufferFailureMessage
			)
		)
		{
			return false;
		}

		if (
			!TryMergeOpenEditorBufferGroups(
				openEditorGroupsByPath,
				WrapSingleOpenEditorsAsGroups(activatedEditorsByPath),
				out affectedOpenBufferFailureMessage
			)
		)
		{
			return false;
		}

		var stillUnmatchedPaths = new List<string>();

		foreach (string activationFallbackPath in activationFallbackPaths)
		{
			if (!openEditorGroupsByPath.ContainsKey(activationFallbackPath))
				stillUnmatchedPaths.Add(activationFallbackPath);
		}

		if (stillUnmatchedPaths.Count == 0)
			return true;

		affectedOpenBufferFailureMessage =
			$"Refactor Namespace cancelled: System Explorer could not safely match affected open script editor buffer(s) after activation:\n{string.Join("\n", stillUnmatchedPaths)}";
		return false;
	}

	private static Dictionary<string, OpenScriptEditorBufferGroup> WrapSingleOpenEditorsAsGroups(
		IEnumerable<KeyValuePair<string, OpenScriptEditorBuffer>> openEditorsByPath
	)
	{
		Dictionary<string, OpenScriptEditorBufferGroup> result = new(
			StringComparer.OrdinalIgnoreCase
		);

		if (openEditorsByPath == null)
			return result;

		foreach (KeyValuePair<string, OpenScriptEditorBuffer> pair in openEditorsByPath)
			result.Add(pair.Key, OpenScriptEditorBufferGroup.CreateSingle(pair.Value, true));

		return result;
	}

	private static bool TryMergeOpenEditorBufferGroups(
		Dictionary<string, OpenScriptEditorBufferGroup> destination,
		IEnumerable<KeyValuePair<string, OpenScriptEditorBufferGroup>> source,
		out string failureMessage
	)
	{
		failureMessage = "";

		if (source == null)
			return true;

		foreach (KeyValuePair<string, OpenScriptEditorBufferGroup> sourcePair in source)
		{
			OpenScriptEditorBufferGroup sourceGroup = sourcePair.Value;

			if (destination.TryGetValue(sourcePair.Key, out OpenScriptEditorBufferGroup existingGroup))
			{
				bool sourceIsAlreadyContained = sourceGroup.Buffers.All(sourceMember =>
					existingGroup.Buffers.Any(existingMember =>
						ReferenceEquals(existingMember.TextEditor, sourceMember.TextEditor)
					)
				);

				if (sourceIsAlreadyContained)
					continue;

				failureMessage =
					$"Refactor Namespace cancelled: System Explorer matched '{sourcePair.Key}' to incompatible open editor buffer groups before refactoring.";
				return false;
			}

			foreach (KeyValuePair<string, OpenScriptEditorBufferGroup> destinationPair in destination)
			{
				bool textEditorCollision = sourceGroup.Buffers.Any(sourceMember =>
					destinationPair.Value.Buffers.Any(destinationMember =>
						ReferenceEquals(
							destinationMember.TextEditor,
							sourceMember.TextEditor
						)
					)
				);

				if (!textEditorCollision)
					continue;

				failureMessage =
					$"Refactor Namespace cancelled: System Explorer matched the same open text editor buffer to both '{destinationPair.Key}' and '{sourcePair.Key}' before refactoring.";
				return false;
			}

			destination.Add(sourcePair.Key, sourceGroup);
		}

		return true;
	}

	private static string FormatGroupCounts(
		IReadOnlyDictionary<string, OpenScriptEditorBufferGroup> groupsByPath,
		NamespaceRefactorDiagnosticContext diagnosticContext
	)
	{
		if (diagnosticContext?.IsEnabled != true || groupsByPath == null || groupsByPath.Count == 0)
			return "[]";

		return $"[{string.Join(", ", groupsByPath.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => $"'{pair.Key}'={pair.Value?.Buffers.Count ?? 0}"))}]";
	}

	private static NamespaceRefactorPendingWriteApplyFailure GetInitialMatchFailure(
		NamespaceRefactorAffectedOpenBufferMatchMode matchMode
	)
	{
		return matchMode == NamespaceRefactorAffectedOpenBufferMatchMode.NonActivatingOnly
			? NamespaceRefactorPendingWriteApplyFailure.UnsafeNonActivatingBufferMatch
			: NamespaceRefactorPendingWriteApplyFailure.AffectedOpenBufferMatchFailed;
	}
}
#endif
