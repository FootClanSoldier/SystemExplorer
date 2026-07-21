#if TOOLS
using Godot;
using System;

namespace SystemExplorer.EditorIntegration.ScriptEditing;

internal enum ScriptEditorBufferActivationFailure
{
	None,
	InvalidScriptPath,
	ScriptEditorUnavailable,
	EditorInterfaceUnavailable,
	ScriptNotOpen,
	ActivatedScriptPathMismatch,
	ActiveEditorUnavailable,
	ActiveEditorIsNotTextEdit,
}

internal readonly record struct ScriptEditorBufferActivationResult(
	bool Success,
	bool ScriptWasOpen,
	string NormalizedScriptPath,
	OpenScriptEditorBuffer OpenEditor,
	ScriptEditorBufferActivationFailure Failure,
	string CurrentScriptPath,
	ScriptEditorBase CurrentEditor,
	Control BaseEditor,
	bool ActivatedScriptPathMatched
)
{
	internal static ScriptEditorBufferActivationResult Succeeded(
		string normalizedScriptPath,
		OpenScriptEditorBuffer openEditor,
		string currentScriptPath,
		ScriptEditorBase currentEditor,
		Control baseEditor
	) =>
		new(
			true,
			true,
			normalizedScriptPath ?? "",
			openEditor,
			ScriptEditorBufferActivationFailure.None,
			currentScriptPath ?? "",
			currentEditor,
			baseEditor,
			true
		);

	internal static ScriptEditorBufferActivationResult NotOpen(string normalizedScriptPath) =>
		new(
			true,
			false,
			normalizedScriptPath ?? "",
			default,
			ScriptEditorBufferActivationFailure.ScriptNotOpen,
			"",
			null,
			null,
			false
		);

	internal static ScriptEditorBufferActivationResult Failed(
		ScriptEditorBufferActivationFailure failure,
		string normalizedScriptPath,
		bool scriptWasOpen = false,
		string currentScriptPath = "",
		ScriptEditorBase currentEditor = null,
		Control baseEditor = null,
		bool activatedScriptPathMatched = false
	) =>
		new(
			false,
			scriptWasOpen,
			normalizedScriptPath ?? "",
			default,
			failure,
			currentScriptPath ?? "",
			currentEditor,
			baseEditor,
			activatedScriptPathMatched
		);
}

internal sealed class ScriptEditorBufferActivationService
{
	private readonly Func<string, string> _normalizePath;

	internal ScriptEditorBufferActivationService(Func<string, string> normalizePath)
	{
		_normalizePath = normalizePath ?? throw new ArgumentNullException(nameof(normalizePath));
	}

	internal ScriptEditorBufferActivationResult TryActivateOpenBuffer(
		EditorInterface editorInterface,
		ScriptEditor scriptEditor,
		string scriptPath
	)
	{
		string normalizedPath = _normalizePath(scriptPath);

		if (string.IsNullOrWhiteSpace(normalizedPath))
		{
			return ScriptEditorBufferActivationResult.Failed(
				ScriptEditorBufferActivationFailure.InvalidScriptPath,
				normalizedPath
			);
		}

		if (scriptEditor == null || !GodotObject.IsInstanceValid(scriptEditor))
		{
			return ScriptEditorBufferActivationResult.Failed(
				ScriptEditorBufferActivationFailure.ScriptEditorUnavailable,
				normalizedPath
			);
		}

		if (editorInterface == null || !GodotObject.IsInstanceValid(editorInterface))
		{
			return ScriptEditorBufferActivationResult.Failed(
				ScriptEditorBufferActivationFailure.EditorInterfaceUnavailable,
				normalizedPath
			);
		}

		if (!TryGetOpenScript(scriptEditor, normalizedPath, out Script openScript))
			return ScriptEditorBufferActivationResult.NotOpen(normalizedPath);

		editorInterface.EditScript(openScript);

		Script currentScript = scriptEditor.GetCurrentScript();
		string currentScriptPath = _normalizePath(currentScript?.ResourcePath);
		ScriptEditorBase currentEditor = scriptEditor.GetCurrentEditor();
		Control baseEditor = currentEditor?.GetBaseEditor();
		bool pathMatched = string.Equals(
			currentScriptPath,
			normalizedPath,
			StringComparison.OrdinalIgnoreCase
		);

		if (!pathMatched)
		{
			return ScriptEditorBufferActivationResult.Failed(
				ScriptEditorBufferActivationFailure.ActivatedScriptPathMismatch,
				normalizedPath,
				scriptWasOpen: true,
				currentScriptPath: currentScriptPath,
				currentEditor: currentEditor,
				baseEditor: baseEditor
			);
		}

		if (currentEditor == null || !GodotObject.IsInstanceValid(currentEditor))
		{
			return ScriptEditorBufferActivationResult.Failed(
				ScriptEditorBufferActivationFailure.ActiveEditorUnavailable,
				normalizedPath,
				scriptWasOpen: true,
				currentScriptPath: currentScriptPath,
				currentEditor: currentEditor,
				baseEditor: baseEditor,
				activatedScriptPathMatched: true
			);
		}

		if (baseEditor is not TextEdit textEditor || !GodotObject.IsInstanceValid(textEditor))
		{
			return ScriptEditorBufferActivationResult.Failed(
				ScriptEditorBufferActivationFailure.ActiveEditorIsNotTextEdit,
				normalizedPath,
				scriptWasOpen: true,
				currentScriptPath: currentScriptPath,
				currentEditor: currentEditor,
				baseEditor: baseEditor,
				activatedScriptPathMatched: true
			);
		}

		OpenScriptEditorBuffer openEditor = new(normalizedPath, textEditor);
		return ScriptEditorBufferActivationResult.Succeeded(
			normalizedPath,
			openEditor,
			currentScriptPath,
			currentEditor,
			baseEditor
		);
	}

	internal bool IsScriptOpen(ScriptEditor scriptEditor, string scriptPath)
	{
		string normalizedPath = _normalizePath(scriptPath);
		return TryGetOpenScript(scriptEditor, normalizedPath, out _);
	}

	private bool TryGetOpenScript(
		ScriptEditor scriptEditor,
		string normalizedScriptPath,
		out Script openScript
	)
	{
		openScript = null;

		if (
			scriptEditor == null
			|| !GodotObject.IsInstanceValid(scriptEditor)
			|| string.IsNullOrWhiteSpace(normalizedScriptPath)
		)
		{
			return false;
		}

		foreach (Script candidateScript in scriptEditor.GetOpenScripts())
		{
			if (candidateScript == null || !GodotObject.IsInstanceValid(candidateScript))
				continue;

			string candidatePath = _normalizePath(candidateScript.ResourcePath);

			if (
				!string.Equals(
					candidatePath,
					normalizedScriptPath,
					StringComparison.OrdinalIgnoreCase
				)
			)
			{
				continue;
			}

			openScript = candidateScript;
			return true;
		}

		return false;
	}
}
#endif
