#if TOOLS
using Godot;
using System;
using System.Collections.Generic;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal sealed class NamespaceRefactorApplyCoordinator
{
	private readonly NamespaceRefactorPendingWriteApplyService _pendingWriteApplyService;
	private readonly NamespaceRefactorPostApplyCoordinator _postApplyCoordinator;
	private readonly Func<EditorInterface> _editorInterfaceProvider;
	private readonly Action<string> _debugLog;
	private readonly Action<string> _showWarning;
	private readonly Action<string, string> _logOperation;
	private readonly Action<IReadOnlyList<string>> _showIncompleteWriteReport;

	internal NamespaceRefactorApplyCoordinator(
		NamespaceRefactorPendingWriteApplyService pendingWriteApplyService,
		NamespaceRefactorPostApplyCoordinator postApplyCoordinator,
		Func<EditorInterface> editorInterfaceProvider,
		Action<string> debugLog,
		Action<string> showWarning,
		Action<string, string> logOperation,
		Action<IReadOnlyList<string>> showIncompleteWriteReport
	)
	{
		_pendingWriteApplyService =
			pendingWriteApplyService
			?? throw new ArgumentNullException(nameof(pendingWriteApplyService));
		_postApplyCoordinator =
			postApplyCoordinator ?? throw new ArgumentNullException(nameof(postApplyCoordinator));
		_editorInterfaceProvider =
			editorInterfaceProvider
			?? throw new ArgumentNullException(nameof(editorInterfaceProvider));
		_debugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
		_showWarning = showWarning ?? throw new ArgumentNullException(nameof(showWarning));
		_logOperation = logOperation ?? throw new ArgumentNullException(nameof(logOperation));
		_showIncompleteWriteReport =
			showIncompleteWriteReport
			?? throw new ArgumentNullException(nameof(showIncompleteWriteReport));
	}

	internal bool ApplySingleReplacement(
		EditorInterface editorInterface,
		ScriptEditor scriptEditor,
		NamespaceRefactorPendingWriteSet initialWriteSet,
		Func<NamespaceRefactorPendingWriteBuildResult> rebuildAfterAutosave,
		NamespaceRefactorDiagnosticContext diagnosticContext
	)
	{
		diagnosticContext?.Log("Apply", "Single replacement apply entered; WritesStarted=true.");
		NamespaceRefactorPendingWriteApplyResult applyResult =
			_pendingWriteApplyService.TryApplyPendingWrites(
				editorInterface,
				scriptEditor,
				initialWriteSet,
				matchMode: NamespaceRefactorAffectedOpenBufferMatchMode.NonActivatingWithActivationFallback,
				rebuildAfterAutosave: rebuildAfterAutosave,
				debugLog: _debugLog,
				diagnosticContext: diagnosticContext
			);

		if (!applyResult.Success)
		{
			diagnosticContext?.Log(
				"Cancellation",
				() => $"Cancelled during Apply; Failure={applyResult.Failure}; AutosaveFailure={applyResult.FailedAutosave.Failure}; DiagnosticReason={applyResult.FailedAutosave.DiagnosticReason}; FailurePath='{applyResult.FailedAutosave.ScriptPath}'; UnsafePaths={diagnosticContext.FormatPaths(applyResult.UnsafeOpenScriptPaths)}; UnsavedPaths={diagnosticContext.FormatPaths(applyResult.UnsavedScriptPaths)}"
			);
			string failureMessage =
				NamespaceRefactorPendingWriteApplyFailureMessageBuilder.Build(
					applyResult,
					"Refactor Namespace",
					useAfterAutosaveRematchFallback: true
				);

			if (!string.IsNullOrWhiteSpace(failureMessage))
				_showWarning(failureMessage);

			return false;
		}

		NamespaceRefactorPendingWriteSet appliedWriteSet = applyResult.AppliedWriteSet;
		int appliedCount = appliedWriteSet?.PendingWrites.Count ?? 0;
		int intendedCount = applyResult.IntendedWritePathCount;
		int fullyAppliedCount = Math.Max(
			0,
			intendedCount - applyResult.FailedWritePaths.Count
		);

		if (appliedCount > 0)
		{
			_postApplyCoordinator.CompleteSingleReplacement(
				_editorInterfaceProvider(),
				appliedWriteSet,
				_debugLog,
				diagnosticContext
			);
		}

		_showIncompleteWriteReport(applyResult.FailedWritePaths);

		if (appliedCount == 0)
		{
			diagnosticContext?.Log(
				"Cancellation",
				() => $"Cancelled after Write; no paths were applied; IntendedWritePathCount={intendedCount}; FailedPaths={diagnosticContext.FormatPaths(applyResult.FailedWritePaths)}"
			);
			return false;
		}

		_logOperation(
			"Refactor Namespace Completed",
			applyResult.FailedWritePaths.Count == 0
				? $"Updated {appliedCount} file(s)."
				: $"Updated {fullyAppliedCount} of {intendedCount} file(s)."
		);
		diagnosticContext?.Log(
			"Completion",
			() => $"Completed; AppliedCount={appliedCount}; IntendedCount={intendedCount}; PartialWriteFailure={applyResult.FailedWritePaths.Count > 0}; FailedPaths={diagnosticContext.FormatPaths(applyResult.FailedWritePaths)}"
		);
		return true;
	}

	internal bool ApplyPendingWriteOperation(
		NamespaceRefactorPendingWriteSet writeSet,
		string operationName,
		string explicitRestorePath,
		bool syncSelectionAfterOperation,
		Func<NamespaceRefactorPendingWriteBuildResult> rebuildAfterAutosave,
		NamespaceRefactorDiagnosticContext diagnosticContext
	)
	{
		if (
			writeSet == null
			|| writeSet.PendingWrites == null
			|| writeSet.PendingWrites.Count == 0
		)
		{
			diagnosticContext?.Log(
				"Cancellation",
				"Cancelled before Write; no file changes were produced; WritesStarted=false."
			);
			_debugLog($"{operationName} cancelled: no file changes were produced.");
			return false;
		}

		NamespaceRefactorAffectedOpenBufferMatchMode matchMode =
			syncSelectionAfterOperation
				? NamespaceRefactorAffectedOpenBufferMatchMode.ActivatingOnly
				: NamespaceRefactorAffectedOpenBufferMatchMode.NonActivatingOnly;
		diagnosticContext?.Log(
			"Apply",
			() => $"Pending-write operation apply entered; Operation='{operationName}'; MatchMode={matchMode}; SyncSelection={syncSelectionAfterOperation}; WritesStarted=true."
		);

		NamespaceRefactorPendingWriteApplyResult applyResult =
			_pendingWriteApplyService.TryApplyPendingWrites(
				_editorInterfaceProvider(),
				_editorInterfaceProvider()?.GetScriptEditor(),
				writeSet,
				matchMode: matchMode,
				rebuildAfterAutosave: rebuildAfterAutosave,
				debugLog: _debugLog,
				diagnosticContext: diagnosticContext
			);

		if (
			applyResult.DidAutosave
			&& applyResult.Failure != NamespaceRefactorPendingWriteApplyFailure.AutosaveFailed
		)
		{
			_debugLog($"{operationName} autosaved affected open script buffer(s) before writing.");
		}

		if (!applyResult.Success)
		{
			diagnosticContext?.Log(
				"Cancellation",
				() => $"Cancelled during Apply; Failure={applyResult.Failure}; AutosaveFailure={applyResult.FailedAutosave.Failure}; DiagnosticReason={applyResult.FailedAutosave.DiagnosticReason}; FailurePath='{applyResult.FailedAutosave.ScriptPath}'; UnsafePaths={diagnosticContext.FormatPaths(applyResult.UnsafeOpenScriptPaths)}; UnsavedPaths={diagnosticContext.FormatPaths(applyResult.UnsavedScriptPaths)}"
			);
			string failureMessage =
				NamespaceRefactorPendingWriteApplyFailureMessageBuilder.Build(
					applyResult,
					operationName,
					useAfterAutosaveRematchFallback: rebuildAfterAutosave != null
				);

			if (!string.IsNullOrWhiteSpace(failureMessage))
				_showWarning(failureMessage);

			return false;
		}

		NamespaceRefactorPendingWriteSet appliedWriteSet = applyResult.AppliedWriteSet;
		int appliedCount = appliedWriteSet?.PendingWrites.Count ?? 0;
		int intendedCount = applyResult.IntendedWritePathCount;
		int fullyAppliedCount = Math.Max(
			0,
			intendedCount - applyResult.FailedWritePaths.Count
		);

		if (appliedCount > 0)
		{
			_postApplyCoordinator.CompletePendingWriteOperation(
				_editorInterfaceProvider(),
				appliedWriteSet,
				explicitRestorePath,
				syncSelectionAfterOperation,
				_debugLog,
				diagnosticContext
			);
		}

		_showIncompleteWriteReport(applyResult.FailedWritePaths);

		if (appliedCount == 0)
		{
			diagnosticContext?.Log(
				"Cancellation",
				() => $"Cancelled after Write; no paths were applied; IntendedWritePathCount={intendedCount}; FailedPaths={diagnosticContext.FormatPaths(applyResult.FailedWritePaths)}"
			);
			return false;
		}

		_logOperation(
			$"{operationName} Completed",
			applyResult.FailedWritePaths.Count == 0
				? $"Updated {appliedCount} file(s)."
				: $"Updated {fullyAppliedCount} of {intendedCount} file(s)."
		);
		diagnosticContext?.Log(
			"Completion",
			() => $"Completed; Operation='{operationName}'; AppliedCount={appliedCount}; IntendedCount={intendedCount}; PartialWriteFailure={applyResult.FailedWritePaths.Count > 0}; FailedPaths={diagnosticContext.FormatPaths(applyResult.FailedWritePaths)}"
		);
		return true;
	}
}
#endif
