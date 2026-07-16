#if TOOLS
using Godot;
using System;

namespace SystemExplorer.EditorIntegration.ScriptEditing;

internal enum ScriptEditorBufferAutosaveFailure
{
    None,
    SavedBufferDiskMismatch,
    WriteFailed,
    AutosaveVerificationMismatch,
}

internal readonly record struct ScriptEditorBufferAutosaveResult(
    bool Success,
    bool DidAutosave,
    string ScriptPath,
    ScriptEditorBufferAutosaveFailure Failure
)
{
    internal static ScriptEditorBufferAutosaveResult Succeeded(
        string scriptPath,
        bool didAutosave = false
    ) => new(true, didAutosave, scriptPath ?? "", ScriptEditorBufferAutosaveFailure.None);

    internal static ScriptEditorBufferAutosaveResult Failed(
        string scriptPath,
        ScriptEditorBufferAutosaveFailure failure
    ) => new(false, false, scriptPath ?? "", failure);
}

internal sealed class ScriptEditorBufferAutosaveService
{
    private readonly Func<string, string> _readTextFile;
    private readonly Func<string, string, bool> _writeTextFile;
    private readonly Func<string, string, bool> _textsMatchForDiskVerification;

    internal ScriptEditorBufferAutosaveService(
        Func<string, string> readTextFile,
        Func<string, string, bool> writeTextFile,
        Func<string, string, bool> textsMatchForDiskVerification
    )
    {
        _readTextFile = readTextFile ?? throw new ArgumentNullException(nameof(readTextFile));
        _writeTextFile = writeTextFile ?? throw new ArgumentNullException(nameof(writeTextFile));
        _textsMatchForDiskVerification =
            textsMatchForDiskVerification
            ?? throw new ArgumentNullException(nameof(textsMatchForDiskVerification));
    }

    internal ScriptEditorBufferAutosaveResult TryAutosaveIfNeeded(
        OpenScriptEditorBuffer openEditor,
        bool failOnSavedDiskMismatch
    )
    {
        TextEdit textEditor = openEditor.TextEditor;
        string scriptPath = openEditor.Path;

        if (textEditor == null || string.IsNullOrWhiteSpace(scriptPath))
            return ScriptEditorBufferAutosaveResult.Succeeded(scriptPath);

        string editorText = textEditor.Text ?? "";

        if (!ScriptEditorBufferStateService.IsUnsaved(textEditor))
        {
            string diskText = _readTextFile(scriptPath);

            if (failOnSavedDiskMismatch && !_textsMatchForDiskVerification(editorText, diskText))
            {
                return ScriptEditorBufferAutosaveResult.Failed(
                    scriptPath,
                    ScriptEditorBufferAutosaveFailure.SavedBufferDiskMismatch
                );
            }

            return ScriptEditorBufferAutosaveResult.Succeeded(scriptPath);
        }

        if (!_writeTextFile(scriptPath, editorText))
        {
            return ScriptEditorBufferAutosaveResult.Failed(
                scriptPath,
                ScriptEditorBufferAutosaveFailure.WriteFailed
            );
        }

        string savedEditorText = _readTextFile(scriptPath);

        if (!_textsMatchForDiskVerification(savedEditorText, editorText))
        {
            return ScriptEditorBufferAutosaveResult.Failed(
                scriptPath,
                ScriptEditorBufferAutosaveFailure.AutosaveVerificationMismatch
            );
        }

        ScriptEditorBufferStateService.MarkCurrentVersionSaved(textEditor);
        return ScriptEditorBufferAutosaveResult.Succeeded(scriptPath, didAutosave: true);
    }
}
#endif
