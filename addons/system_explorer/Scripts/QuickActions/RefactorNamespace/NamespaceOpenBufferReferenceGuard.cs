#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal readonly record struct NamespaceOpenBufferReferenceGuardResult(
	bool HasUnsafeReference,
	string NamespaceName,
	TextEdit UnmatchedTextEditor
);

internal sealed class NamespaceOpenBufferReferenceGuard
{
	internal NamespaceOpenBufferReferenceGuardResult FindUnsafeReference(
		ScriptEditor scriptEditor,
		IReadOnlyDictionary<string, OpenScriptEditorBuffer> matchedOpenEditorsByPath,
		string namespaceName
	)
	{
		if (
			scriptEditor == null
			|| !GodotObject.IsInstanceValid(scriptEditor)
			|| string.IsNullOrWhiteSpace(namespaceName)
		)
		{
			return new NamespaceOpenBufferReferenceGuardResult(false, namespaceName ?? "", null);
		}

		HashSet<TextEdit> matchedTextEditors =
			matchedOpenEditorsByPath
				?.Values.Select(openEditor => openEditor.TextEditor)
				.Where(textEditor => textEditor != null && GodotObject.IsInstanceValid(textEditor))
				.ToHashSet()
			?? new HashSet<TextEdit>();

		foreach (ScriptEditorBase scriptEditorBase in scriptEditor.GetOpenScriptEditors())
		{
			if (scriptEditorBase == null || !GodotObject.IsInstanceValid(scriptEditorBase))
				continue;

			Control baseEditor = scriptEditorBase.GetBaseEditor();

			if (
				baseEditor == null
				|| !GodotObject.IsInstanceValid(baseEditor)
				|| baseEditor is not TextEdit textEditor
				|| !GodotObject.IsInstanceValid(textEditor)
				|| matchedTextEditors.Contains(textEditor)
			)
			{
				continue;
			}

			if (!ScriptTextContainsUsingStatement(textEditor.Text ?? "", namespaceName))
				continue;

			return new NamespaceOpenBufferReferenceGuardResult(true, namespaceName, textEditor);
		}

		return new NamespaceOpenBufferReferenceGuardResult(false, namespaceName, null);
	}

	internal bool TryFindUnsafeReference(
		ScriptEditor scriptEditor,
		IReadOnlyDictionary<string, OpenScriptEditorBuffer> matchedOpenEditorsByPath,
		string namespaceName,
		out string failureMessage
	)
	{
		NamespaceOpenBufferReferenceGuardResult guardResult = FindUnsafeReference(
			scriptEditor,
			matchedOpenEditorsByPath,
			namespaceName
		);

		failureMessage = "";

		if (!guardResult.HasUnsafeReference)
			return false;

		failureMessage =
			$"Refactor Namespace cancelled: an open script editor buffer contains 'using {namespaceName};', but System Explorer could not safely match that buffer to a script path without changing the active editor tab. Save/reopen open scripts before batch refactoring.";
		return true;
	}

	private static bool ScriptTextContainsUsingStatement(string scriptText, string namespaceName)
	{
		if (string.IsNullOrWhiteSpace(scriptText) || string.IsNullOrWhiteSpace(namespaceName))
			return false;

		string[] lines = NormalizeScriptText(scriptText).Split('\n');

		foreach (string line in lines)
		{
			string trimmedLine = line.Trim();

			if (!trimmedLine.StartsWith("using ", StringComparison.Ordinal))
				continue;

			if (!trimmedLine.EndsWith(";", StringComparison.Ordinal))
				continue;

			string usingNamespace = trimmedLine
				.Substring("using ".Length, trimmedLine.Length - "using ".Length - 1)
				.Trim();

			if (string.Equals(usingNamespace, namespaceName, StringComparison.Ordinal))
				return true;
		}

		return false;
	}

	private static string NormalizeScriptText(string text)
	{
		return (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
	}
}
#endif
