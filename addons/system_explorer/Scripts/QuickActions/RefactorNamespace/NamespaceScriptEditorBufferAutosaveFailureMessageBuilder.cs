#if TOOLS
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal static class NamespaceScriptEditorBufferAutosaveFailureMessageBuilder
{
	internal static string Build(ScriptEditorBufferAutosaveResult autosaveResult)
	{
		string scriptPath = autosaveResult.ScriptPath;

		return autosaveResult.Failure switch
		{
			ScriptEditorBufferAutosaveFailure.SavedBufferDiskMismatch =>
				$"Refactor Namespace cancelled: the open editor buffer for '{scriptPath}' does not match the file on disk before scanning namespace usages. Save/reopen it before refactoring.",
			ScriptEditorBufferAutosaveFailure.WriteFailed =>
				$"Refactor Namespace cancelled: could not autosave affected script before refactoring '{scriptPath}'. Some script buffers may already have been saved.",
			ScriptEditorBufferAutosaveFailure.AutosaveVerificationMismatch =>
				$"Refactor Namespace cancelled: autosaved text for '{scriptPath}' did not match the open editor buffer. The namespace refactor was not applied.",
			_ => "",
		};
	}
}
#endif
