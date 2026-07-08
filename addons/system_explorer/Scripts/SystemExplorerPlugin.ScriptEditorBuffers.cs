#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

public partial class SystemExplorerPlugin
{
	#region Shared Script Editor Buffers

	private readonly struct OpenScriptEditorBuffer
	{
		public OpenScriptEditorBuffer(string path, TextEdit textEditor)
		{
			Path = NormalizeScriptPath(path);
			TextEditor = textEditor;
		}

		public string Path { get; }
		public TextEdit TextEditor { get; }
	}

	private static readonly UTF8Encoding Utf8NoBomEncoding = new(false);

	private static string ReadTextFile(string path)
	{
		string globalPath = GetGlobalTextFilePath(path);

		if (string.IsNullOrWhiteSpace(globalPath) || !System.IO.File.Exists(globalPath))
			return "";

		try
		{
			return System.IO.File.ReadAllText(globalPath, Encoding.UTF8);
		}
		catch
		{
			return "";
		}
	}

	private static bool WriteTextFile(string path, string text)
	{
		string globalPath = GetGlobalTextFilePath(path);

		if (string.IsNullOrWhiteSpace(globalPath))
			return false;

		try
		{
			System.IO.File.WriteAllText(globalPath, text ?? "", Utf8NoBomEncoding);
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static string GetGlobalTextFilePath(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
			return "";

		if (
			path.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
			|| path.StartsWith("user://", StringComparison.OrdinalIgnoreCase)
		)
		{
			return ProjectSettings.GlobalizePath(path);
		}

		return path;
	}

	private bool TryGetOpenScriptEditorsByActivatingPaths(
		IEnumerable<string> scriptPaths,
		bool failIfOpenEditorCannotBeMatched,
		out Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath,
		out string failureMessage
	)
	{
		openEditorsByPath = new Dictionary<string, OpenScriptEditorBuffer>(
			StringComparer.OrdinalIgnoreCase
		);
		failureMessage = "";

		ScriptEditor scriptEditor = EditorInterface.Singleton?.GetScriptEditor();

		if (scriptEditor == null || scriptPaths == null)
			return true;

		foreach (
			string scriptPath in scriptPaths
				.Where(path => !string.IsNullOrWhiteSpace(path))
				.Select(NormalizeScriptPath)
				.Distinct(StringComparer.OrdinalIgnoreCase)
		)
		{
			if (!IsScriptOpen(scriptEditor, scriptPath))
				continue;

			if (
				TryGetOpenScriptEditorByActivatingPath(
					scriptPath,
					out OpenScriptEditorBuffer openEditor,
					out string editorFailureMessage
				)
			)
			{
				openEditorsByPath[scriptPath] = openEditor;
				continue;
			}

			if (failIfOpenEditorCannotBeMatched)
			{
				failureMessage = editorFailureMessage;
				return false;
			}

			DebugLog(
				$"Refactor Namespace could not match open editor for '{scriptPath}': {editorFailureMessage}"
			);
		}

		return true;
	}

	private bool TryGetOpenScriptEditorByActivatingPath(
		string scriptPath,
		out OpenScriptEditorBuffer openEditor,
		out string failureMessage
	)
	{
		openEditor = default;
		failureMessage = "";

		string normalizedPath = NormalizeScriptPath(scriptPath);

		if (string.IsNullOrWhiteSpace(normalizedPath))
		{
			failureMessage =
				"Refactor Namespace cancelled: an empty script path could not be matched to an open editor buffer.";
			return false;
		}

		ScriptEditor scriptEditor = EditorInterface.Singleton?.GetScriptEditor();
		EditorInterface editorInterface = EditorInterface.Singleton;

		if (scriptEditor == null || editorInterface == null)
			return true;

		if (!TryGetOpenScript(scriptEditor, normalizedPath, out Script openScript))
			return true;

		editorInterface.EditScript(openScript);

		Script currentScript = scriptEditor.GetCurrentScript();
		string currentScriptPath = NormalizeScriptPath(currentScript?.ResourcePath);
		ScriptEditorBase currentEditor = scriptEditor.GetCurrentEditor();
		Control baseEditor = currentEditor?.GetBaseEditor();

		DebugLog(
			$"Refactor Namespace activate '{normalizedPath}': current='{currentScriptPath}', currentEditorId={GetGodotInstanceId(currentEditor)}, baseType='{baseEditor?.GetType().Name ?? "<null>"}', baseId={GetGodotInstanceId(baseEditor)}, matches={string.Equals(currentScriptPath, normalizedPath, StringComparison.OrdinalIgnoreCase)}"
		);

		if (!string.Equals(currentScriptPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
		{
			failureMessage =
				$"Refactor Namespace cancelled: System Explorer could not safely activate the open editor buffer for '{normalizedPath}' before refactoring.";
			return false;
		}

		if (baseEditor is not TextEdit textEditor)
		{
			failureMessage =
				$"Refactor Namespace cancelled: System Explorer could not access the open text editor buffer for '{normalizedPath}' before refactoring.";
			return false;
		}

		openEditor = new OpenScriptEditorBuffer(normalizedPath, textEditor);
		return true;
	}

	private static bool TryGetOpenScript(
		ScriptEditor scriptEditor,
		string scriptPath,
		out Script openScript
	)
	{
		openScript = null;

		if (scriptEditor == null || string.IsNullOrWhiteSpace(scriptPath))
			return false;

		foreach (Script candidateScript in scriptEditor.GetOpenScripts())
		{
			if (candidateScript == null)
				continue;

			string candidatePath = NormalizeScriptPath(candidateScript.ResourcePath);

			if (!string.Equals(candidatePath, scriptPath, StringComparison.OrdinalIgnoreCase))
				continue;

			openScript = candidateScript;
			return true;
		}

		return false;
	}

	private static bool IsScriptOpen(ScriptEditor scriptEditor, string scriptPath)
	{
		return TryGetOpenScript(scriptEditor, scriptPath, out _);
	}

	private static bool TryAutosaveOpenScriptEditorBufferIfNeeded(
		OpenScriptEditorBuffer openEditor,
		bool failOnSavedDiskMismatch,
		out bool didAutosaveOpenEditor,
		out string failureMessage
	)
	{
		didAutosaveOpenEditor = false;
		failureMessage = "";

		TextEdit textEditor = openEditor.TextEditor;
		string scriptPath = openEditor.Path;

		if (textEditor == null || string.IsNullOrWhiteSpace(scriptPath))
			return true;

		string editorText = textEditor.Text ?? "";
		bool isUnsaved = textEditor.GetVersion() != textEditor.GetSavedVersion();

		if (!isUnsaved)
		{
			string diskText = ReadTextFile(scriptPath);

			if (
				failOnSavedDiskMismatch
				&& !ScriptTextsMatchForDiskVerification(editorText, diskText)
			)
			{
				failureMessage =
					$"Refactor Namespace cancelled: the open editor buffer for '{scriptPath}' does not match the file on disk before scanning namespace usages. Save/reopen it before refactoring.";
				return false;
			}

			return true;
		}

		if (!WriteTextFile(scriptPath, editorText))
		{
			failureMessage =
				$"Refactor Namespace cancelled: could not autosave affected script before refactoring '{scriptPath}'. Some script buffers may already have been saved.";
			return false;
		}

		string savedEditorText = ReadTextFile(scriptPath);

		if (!ScriptTextsMatchForDiskVerification(savedEditorText, editorText))
		{
			failureMessage =
				$"Refactor Namespace cancelled: autosaved text for '{scriptPath}' did not match the open editor buffer. The namespace refactor was not applied.";
			return false;
		}

		textEditor.TagSavedVersion();
		didAutosaveOpenEditor = true;
		return true;
	}

	private static bool ScriptTextsMatchForDiskVerification(string left, string right)
	{
		return NormalizeScriptTextForDiskVerification(left)
			== NormalizeScriptTextForDiskVerification(right);
	}

	private static string NormalizeScriptTextForDiskVerification(string text)
	{
		return (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
	}

	private static string GetGodotInstanceId(GodotObject godotObject)
	{
		return godotObject == null ? "<null>" : godotObject.GetInstanceId().ToString();
	}

	private static Dictionary<string, OpenScriptEditorBuffer> GetOpenScriptEditorsByPath(
		Dictionary<string, string> originalTextsByPath,
		Dictionary<string, string> updatedTextsByPath,
		out string unsafeOpenScriptList
	)
	{
		unsafeOpenScriptList = "";
		Dictionary<string, OpenScriptEditorBuffer> result = new(StringComparer.OrdinalIgnoreCase);

		if (
			originalTextsByPath == null
			|| updatedTextsByPath == null
			|| updatedTextsByPath.Count == 0
		)
			return result;

		HashSet<string> targetPaths = updatedTextsByPath
			.Keys.Where(path => !string.IsNullOrWhiteSpace(path))
			.Select(NormalizeScriptPath)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		if (targetPaths.Count == 0)
			return result;

		ScriptEditor scriptEditor = EditorInterface.Singleton?.GetScriptEditor();

		if (scriptEditor == null)
			return result;

		HashSet<string> openTargetPaths = GetOpenScriptPaths(scriptEditor, targetPaths);
		HashSet<TextEdit> usedTextEditors = new();

		Script currentScript = scriptEditor.GetCurrentScript();
		string currentScriptPath = NormalizeScriptPath(currentScript?.ResourcePath);

		if (targetPaths.Contains(currentScriptPath))
		{
			ScriptEditorBase currentEditor = scriptEditor.GetCurrentEditor();

			if (currentEditor?.GetBaseEditor() is TextEdit currentTextEditor)
				AddOpenScriptEditorBuffer(
					result,
					usedTextEditors,
					currentScriptPath,
					currentTextEditor
				);
		}

		List<TextEdit> openTextEditors = GetOpenScriptTextEditors(scriptEditor);
		List<string> unsafeOpenScripts = new();

		List<string> pathsToMatch = openTargetPaths
			.Concat(targetPaths)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		foreach (string targetPath in pathsToMatch)
		{
			if (result.ContainsKey(targetPath))
				continue;

			if (
				!originalTextsByPath.TryGetValue(targetPath, out string originalText)
				|| !updatedTextsByPath.TryGetValue(targetPath, out string updatedText)
			)
			{
				if (openTargetPaths.Contains(targetPath))
					unsafeOpenScripts.Add(targetPath);

				continue;
			}

			List<TextEdit> matchingEditors = openTextEditors
				.Where(textEditor =>
					textEditor != null
					&& !usedTextEditors.Contains(textEditor)
					&& TextEditorMatchesScriptTexts(
						textEditor,
						targetPath,
						originalTextsByPath,
						updatedTextsByPath
					)
				)
				.ToList();

			if (matchingEditors.Count == 1)
			{
				TextEdit matchingEditor = matchingEditors[0];
				int matchingPathCount = pathsToMatch.Count(path =>
					!result.ContainsKey(path)
					&& TextEditorMatchesScriptTexts(
						matchingEditor,
						path,
						originalTextsByPath,
						updatedTextsByPath
					)
				);

				if (matchingPathCount == 1)
				{
					AddOpenScriptEditorBuffer(result, usedTextEditors, targetPath, matchingEditor);
					continue;
				}
			}

			if (matchingEditors.Count > 0 || openTargetPaths.Contains(targetPath))
				unsafeOpenScripts.Add(targetPath);
		}

		unsafeOpenScriptList = string.Join(
			"\n",
			unsafeOpenScripts.Distinct(StringComparer.OrdinalIgnoreCase)
		);
		return result;
	}

	private static bool TextEditorMatchesScriptTexts(
		TextEdit textEditor,
		string scriptPath,
		Dictionary<string, string> originalTextsByPath,
		Dictionary<string, string> updatedTextsByPath
	)
	{
		if (textEditor == null || string.IsNullOrWhiteSpace(scriptPath))
			return false;

		if (
			!originalTextsByPath.TryGetValue(scriptPath, out string originalText)
			|| !updatedTextsByPath.TryGetValue(scriptPath, out string updatedText)
		)
		{
			return false;
		}

		return textEditor.Text == originalText || textEditor.Text == updatedText;
	}

	private static bool TryGetOpenScriptEditorsByIndexedScriptEditorPaths(
		ScriptEditor scriptEditor,
		HashSet<string> targetPaths,
		out Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath,
		out string failureMessage
	)
	{
		failureMessage = "";
		openEditorsByPath = new Dictionary<string, OpenScriptEditorBuffer>(
			StringComparer.OrdinalIgnoreCase
		);

		if (scriptEditor == null || targetPaths == null || targetPaths.Count == 0)
			return true;

		Godot.Collections.Array<Script> openScripts = scriptEditor.GetOpenScripts();
		Godot.Collections.Array<ScriptEditorBase> openScriptEditors =
			scriptEditor.GetOpenScriptEditors();

		if (openScripts.Count != openScriptEditors.Count)
		{
			List<string> openTargetPaths = new();

			foreach (Script openScript in openScripts)
			{
				if (openScript == null)
					continue;

				string openScriptPath = NormalizeScriptPath(openScript.ResourcePath);

				if (
					targetPaths.Contains(openScriptPath)
					&& !openTargetPaths.Contains(openScriptPath, StringComparer.OrdinalIgnoreCase)
				)
				{
					openTargetPaths.Add(openScriptPath);
				}
			}

			if (openTargetPaths.Count == 0)
				return true;

			failureMessage =
				$"Refactor Namespace cancelled: Godot reported {openScripts.Count} open script(s) but {openScriptEditors.Count} open script editor(s), so System Explorer could not safely autosave open script buffer(s) before scanning namespace usages:\n{string.Join("\n", openTargetPaths)}";
			return false;
		}

		HashSet<TextEdit> usedTextEditors = new();

		for (int i = 0; i < openScripts.Count; i++)
		{
			Script openScript = openScripts[i];

			if (openScript == null)
				continue;

			string scriptPath = NormalizeScriptPath(openScript.ResourcePath);

			if (!targetPaths.Contains(scriptPath))
				continue;

			if (!scriptPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
				continue;

			Control baseEditor = openScriptEditors[i]?.GetBaseEditor();

			if (baseEditor is not TextEdit textEditor)
			{
				failureMessage =
					$"Refactor Namespace cancelled: System Explorer could not access the open script editor buffer for '{scriptPath}' before scanning namespace usages.";
				return false;
			}

			if (usedTextEditors.Contains(textEditor))
			{
				failureMessage =
					$"Refactor Namespace cancelled: System Explorer found the same open script editor buffer for more than one script while preparing namespace refactor '{scriptPath}'.";
				return false;
			}

			if (
				openEditorsByPath.TryGetValue(
					scriptPath,
					out OpenScriptEditorBuffer existingOpenEditor
				)
				&& existingOpenEditor.TextEditor != textEditor
			)
			{
				failureMessage =
					$"Refactor Namespace cancelled: System Explorer found duplicate open script editor buffers for '{scriptPath}'. Save/reopen it before refactoring.";
				return false;
			}

			if (
				!IndexedScriptEditorPairLooksSafe(
					openScript,
					scriptPath,
					textEditor,
					out string unsafePairReason
				)
			)
			{
				failureMessage = unsafePairReason;
				return false;
			}

			openEditorsByPath[scriptPath] = new OpenScriptEditorBuffer(scriptPath, textEditor);
			usedTextEditors.Add(textEditor);
		}

		return true;
	}

	private static bool IndexedScriptEditorPairLooksSafe(
		Script openScript,
		string scriptPath,
		TextEdit textEditor,
		out string failureMessage
	)
	{
		failureMessage = "";

		if (openScript == null || textEditor == null || string.IsNullOrWhiteSpace(scriptPath))
		{
			failureMessage =
				"Refactor Namespace cancelled: an open script editor buffer could not be matched safely before scanning namespace usages.";
			return false;
		}

		string editorText = textEditor.Text ?? "";
		string diskText = ReadTextFile(scriptPath);

		if (textEditor.GetVersion() == textEditor.GetSavedVersion())
		{
			if (editorText == diskText)
				return true;

			failureMessage =
				$"Refactor Namespace cancelled: the open editor buffer for '{scriptPath}' does not match the file on disk before scanning namespace usages. Save/reopen it before refactoring.";
			return false;
		}

		return true;
	}

	private static HashSet<string> GetOpenScriptPaths(
		ScriptEditor scriptEditor,
		HashSet<string> targetPaths
	)
	{
		HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);

		if (scriptEditor == null || targetPaths == null || targetPaths.Count == 0)
			return result;

		foreach (Script openScript in scriptEditor.GetOpenScripts())
		{
			if (openScript == null)
				continue;

			string scriptPath = NormalizeScriptPath(openScript.ResourcePath);

			if (targetPaths.Contains(scriptPath))
				result.Add(scriptPath);
		}

		return result;
	}

	private static List<TextEdit> GetOpenScriptTextEditors(ScriptEditor scriptEditor)
	{
		List<TextEdit> result = new();

		if (scriptEditor == null)
			return result;

		foreach (ScriptEditorBase scriptEditorBase in scriptEditor.GetOpenScriptEditors())
		{
			Control baseEditor = scriptEditorBase?.GetBaseEditor();

			if (baseEditor is TextEdit textEditor && !result.Contains(textEditor))
				result.Add(textEditor);
		}

		return result;
	}

	private static void AddOpenScriptEditorBuffer(
		Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath,
		HashSet<TextEdit> usedTextEditors,
		string scriptPath,
		TextEdit textEditor
	)
	{
		if (openEditorsByPath == null || usedTextEditors == null || textEditor == null)
			return;

		string normalizedPath = NormalizeScriptPath(scriptPath);

		if (string.IsNullOrWhiteSpace(normalizedPath))
			return;

		openEditorsByPath[normalizedPath] = new OpenScriptEditorBuffer(normalizedPath, textEditor);
		usedTextEditors.Add(textEditor);
	}

	private static bool HasUnsavedOpenScriptEditorBuffers(
		Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath,
		out string unsavedScriptList
	)
	{
		unsavedScriptList = "";

		if (openEditorsByPath == null || openEditorsByPath.Count == 0)
			return false;

		List<string> affectedUnsavedFiles = openEditorsByPath
			.Values.Where(openEditor =>
				openEditor.TextEditor != null
				&& openEditor.TextEditor.GetVersion() != openEditor.TextEditor.GetSavedVersion()
			)
			.Select(openEditor => openEditor.Path)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		if (affectedUnsavedFiles.Count == 0)
			return false;

		unsavedScriptList = string.Join("\n", affectedUnsavedFiles);
		return true;
	}

	private static bool TryAutosaveOpenScriptEditorBuffersIfNeeded(
		Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath,
		out bool didAutosaveOpenEditors,
		out string failureMessage
	)
	{
		didAutosaveOpenEditors = false;
		failureMessage = "";

		if (openEditorsByPath == null || openEditorsByPath.Count == 0)
			return true;

		foreach (OpenScriptEditorBuffer openEditor in openEditorsByPath.Values)
		{
			if (
				!TryAutosaveOpenScriptEditorBufferIfNeeded(
					openEditor,
					true,
					out bool didAutosaveOpenEditor,
					out string autosaveFailureMessage
				)
			)
			{
				failureMessage = autosaveFailureMessage;
				return false;
			}

			if (didAutosaveOpenEditor)
				didAutosaveOpenEditors = true;
		}

		return true;
	}

	private static void ApplyTextToOpenScriptEditors(
		Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath,
		Dictionary<string, string> updatedTextsByPath
	)
	{
		if (openEditorsByPath == null || updatedTextsByPath == null || openEditorsByPath.Count == 0)
			return;

		foreach (KeyValuePair<string, OpenScriptEditorBuffer> openEditorPair in openEditorsByPath)
		{
			if (!updatedTextsByPath.TryGetValue(openEditorPair.Key, out string updatedText))
				continue;

			ApplyTextToOpenScriptEditor(openEditorPair.Value.TextEditor, updatedText);
		}
	}

	private static void ApplyTextToOpenScriptEditor(TextEdit textEditor, string updatedText)
	{
		if (textEditor == null)
			return;

		if (textEditor.Text != updatedText)
			textEditor.Text = updatedText;

		textEditor.ClearUndoHistory();
		textEditor.TagSavedVersion();
	}

	private static string BuildScriptPathPayload(IEnumerable<string> scriptPaths)
	{
		if (scriptPaths == null)
			return "";

		return string.Join(
			"\n",
			scriptPaths
				.Where(path => !string.IsNullOrWhiteSpace(path))
				.Select(NormalizeScriptPath)
				.Distinct(StringComparer.OrdinalIgnoreCase)
		);
	}

	private static string[] ParseScriptPathPayload(string scriptPathPayload)
	{
		if (string.IsNullOrWhiteSpace(scriptPathPayload))
			return Array.Empty<string>();

		return scriptPathPayload
			.Split('\n')
			.Select(NormalizeScriptPath)
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private static string NormalizeScriptPath(string path)
	{
		return path?.Trim().Replace('\\', '/') ?? "";
	}

	private static void RefreshGodotAfterScriptTextChanges(IEnumerable<string> changedScriptPaths)
	{
		List<string> changedPaths = changedScriptPaths
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Select(NormalizeScriptPath)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		EditorFileSystem resourceFilesystem = EditorInterface.Singleton?.GetResourceFilesystem();

		if (resourceFilesystem == null)
			return;

		foreach (string scriptPath in changedPaths)
			resourceFilesystem.UpdateFile(scriptPath);
	}

	#endregion
}
#endif
