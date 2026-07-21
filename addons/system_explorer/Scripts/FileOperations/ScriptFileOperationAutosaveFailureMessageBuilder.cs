#if TOOLS
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.FileOperations;

internal static class ScriptFileOperationAutosaveFailureMessageBuilder
{
    internal static string Build(
        ScriptEditorBufferAutosaveResult autosaveResult,
        string operationName
    )
    {
        string scriptPath = autosaveResult.ScriptPath;

        return autosaveResult.Failure switch
        {
            ScriptEditorBufferAutosaveFailure.SavedBufferDiskMismatch =>
                $"{operationName} cancelled: the open editor buffer for '{scriptPath}' does not match the file on disk before the operation. Save/reopen it and try again.",
            ScriptEditorBufferAutosaveFailure.WriteFailed =>
                $"{operationName} cancelled: could not autosave the open script buffer before continuing '{scriptPath}'.",
            ScriptEditorBufferAutosaveFailure.AutosaveVerificationMismatch =>
                $"{operationName} cancelled: autosaved text for '{scriptPath}' did not match the open editor buffer. The operation was not applied.",
            _ => "",
        };
    }
}
#endif
