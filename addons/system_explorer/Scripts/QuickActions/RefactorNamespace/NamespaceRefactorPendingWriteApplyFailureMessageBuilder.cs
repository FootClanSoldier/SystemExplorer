#if TOOLS
using System;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal static class NamespaceRefactorPendingWriteApplyFailureMessageBuilder
{
	internal static string Build(
		NamespaceRefactorPendingWriteApplyResult applyResult,
		string operationName,
		bool useAfterAutosaveRematchFallback
	)
	{
		return applyResult.Failure switch
		{
			NamespaceRefactorPendingWriteApplyFailure.None => "",
			NamespaceRefactorPendingWriteApplyFailure.AffectedOpenBufferMatchFailed =>
				BuildAffectedOpenBufferMatchFailureMessage(applyResult, operationName),
			NamespaceRefactorPendingWriteApplyFailure.UnsafeNonActivatingBufferMatch =>
				$"{operationName} cancelled: System Explorer could not safely match open script editor buffer(s) without changing the active editor tab. Save/reopen before refactoring:\n{string.Join("\n", applyResult.UnsafeOpenScriptPaths)}",
			NamespaceRefactorPendingWriteApplyFailure.AutosaveFailed =>
				BuildAutosaveFailureMessage(applyResult, operationName),
			NamespaceRefactorPendingWriteApplyFailure.RebuildAfterAutosaveFailed => "",
			NamespaceRefactorPendingWriteApplyFailure.AffectedOpenBufferRematchAfterAutosaveFailed =>
				BuildAffectedOpenBufferRematchAfterAutosaveFailureMessage(
					applyResult,
					operationName,
					useAfterAutosaveRematchFallback
				),
			NamespaceRefactorPendingWriteApplyFailure.StillUnsaved =>
				$"{operationName} cancelled: affected script(s) are still unsaved after the save-first check. Try again after saving/retrying:\n{string.Join("\n", applyResult.UnsavedScriptPaths)}",
			NamespaceRefactorPendingWriteApplyFailure.WriteFailed =>
				$"{operationName} failed while writing '{applyResult.FailedWritePath}'. Some files may have already been updated.",
			_ => "",
		};
	}

	private static string BuildAffectedOpenBufferMatchFailureMessage(
		NamespaceRefactorPendingWriteApplyResult applyResult,
		string operationName
	)
	{
		return string.IsNullOrWhiteSpace(applyResult.AffectedOpenBufferFailureMessage)
			? $"{operationName} cancelled: affected open script buffer(s) could not be matched safely."
			: applyResult.AffectedOpenBufferFailureMessage;
	}

	private static string BuildAutosaveFailureMessage(
		NamespaceRefactorPendingWriteApplyResult applyResult,
		string operationName
	)
	{
		string autosaveFailureMessage =
			NamespaceScriptEditorBufferAutosaveFailureMessageBuilder.Build(
				applyResult.FailedAutosave
			);

		return string.IsNullOrWhiteSpace(autosaveFailureMessage)
			? $"{operationName} cancelled: affected open script buffer(s) could not be autosaved safely."
			: autosaveFailureMessage;
	}

	private static string BuildAffectedOpenBufferRematchAfterAutosaveFailureMessage(
		NamespaceRefactorPendingWriteApplyResult applyResult,
		string operationName,
		bool useAfterAutosaveRematchFallback
	)
	{
		if (!string.IsNullOrWhiteSpace(applyResult.AffectedOpenBufferFailureMessage))
			return applyResult.AffectedOpenBufferFailureMessage;

		return useAfterAutosaveRematchFallback
			? $"{operationName} cancelled: affected open script buffer(s) could not be matched safely after autosaving."
			: $"{operationName} cancelled: affected open script buffer(s) could not be matched safely.";
	}
}
#endif
