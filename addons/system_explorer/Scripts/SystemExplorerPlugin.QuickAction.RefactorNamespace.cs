#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

public partial class SystemExplorerPlugin
{
	#region Quick Actions - Refactor Namespace
	private static readonly Vector2I RefactorNamespaceDialogSize = new(520, 210);

	private static readonly Regex NamespaceDeclarationRegex = new(
		@"(?m)^(\s*namespace\s+)([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)(\s*(?:;|\{))",
		RegexOptions.Compiled
	);

	private static readonly Regex NamespaceIdentifierRegex = new(
		@"^[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*$",
		RegexOptions.Compiled
	);

	private Dictionary<string, string> _deferredRefactorNamespaceOriginalTextsByPath = new(StringComparer.OrdinalIgnoreCase);

	private readonly struct RefactorNamespaceOpenEditor
	{
		public RefactorNamespaceOpenEditor(string path, TextEdit textEditor)
		{
			Path = NormalizeRefactorNamespacePath(path);
			TextEditor = textEditor;
		}

		public string Path { get; }
		public TextEdit TextEditor { get; }
	}

	private void OpenRefactorNamespaceDialog()
	{
		if (string.IsNullOrWhiteSpace(_pendingRenameMetadata) || !_pendingRenameMetadata.StartsWith("script::"))
			return;

		_pendingRefactorNamespaceMetadata = _pendingRenameMetadata;

		string scriptEntry = GetEntryFromMetadata(_pendingRefactorNamespaceMetadata);
		string scriptPath = GetScriptPathFromEntry(scriptEntry);
		string currentNamespace = ReadNamespaceFromScript(scriptPath);

		if (string.IsNullOrWhiteSpace(currentNamespace))
		{
			GD.PushWarning($"System Explorer could not find a namespace declaration in '{scriptPath}'.");
			return;
		}

		_oldNamespaceInput.Text = currentNamespace;
		_newNamespaceInput.Text = currentNamespace;

		ApplyRefactorNamespaceDialogSize();
		_refactorNamespaceDialog.PopupCentered(RefactorNamespaceDialogSize);
		CallDeferred(nameof(ApplyRefactorNamespaceDialogSize));

		_newNamespaceInput.GrabFocus();
		_newNamespaceInput.SelectAll();
	}

	private void ApplyRefactorNamespaceDialogSize()
	{
		if (_refactorNamespaceDialog == null)
			return;

		_refactorNamespaceDialog.MinSize = RefactorNamespaceDialogSize;
		_refactorNamespaceDialog.Size = RefactorNamespaceDialogSize;
	}

	private void OnRefactorNamespaceConfirmed()
	{
		if (string.IsNullOrWhiteSpace(_pendingRefactorNamespaceMetadata))
			return;

		string oldNamespace = _oldNamespaceInput.Text.Trim();
		string newNamespace = _newNamespaceInput.Text.Trim();

		DebugLogOperation("Refactor Namespace Confirmed", $"{oldNamespace} -> {newNamespace}");

		if (!IsValidNamespaceName(oldNamespace) || !IsValidNamespaceName(newNamespace))
		{
			GD.PushWarning("Refactor Namespace cancelled: namespace values must be valid C# namespace names.");
			return;
		}

		if (oldNamespace == newNamespace)
		{
			DebugLog("Refactor Namespace cancelled: namespace is unchanged.");
			return;
		}

		RefactorNamespace(_pendingRefactorNamespaceMetadata, oldNamespace, newNamespace);
		_pendingRefactorNamespaceMetadata = "";
	}

	private bool RefactorNamespace(string metadata, string oldNamespace, string newNamespace)
	{
		if (!EnsureSystemsLoadedForTreeOperation("Refactor Namespace"))
			return false;

		if (string.IsNullOrWhiteSpace(metadata) || !metadata.StartsWith("script::"))
			return false;

		string selectedEntry = GetEntryFromMetadata(metadata);
		string selectedScriptPath = NormalizeRefactorNamespacePath(GetScriptPathFromEntry(selectedEntry));

		if (!FileAccess.FileExists(selectedScriptPath))
		{
			OpenMissingScriptDialog(selectedEntry, selectedScriptPath);
			return false;
		}

		string selectedScriptText = ReadTextFile(selectedScriptPath);
		string selectedScriptNamespace = GetNamespaceFromText(selectedScriptText);

		if (string.IsNullOrWhiteSpace(selectedScriptNamespace))
		{
			GD.PushWarning($"Refactor Namespace cancelled: no namespace declaration was found in '{selectedScriptPath}'.");
			return false;
		}

		if (selectedScriptNamespace != oldNamespace)
		{
			GD.PushWarning($"Refactor Namespace cancelled: selected script namespace is '{selectedScriptNamespace}', not '{oldNamespace}'.");
			return false;
		}

		Dictionary<string, string> originalTextsByPath = new(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, string> pendingWrites = new(StringComparer.OrdinalIgnoreCase);

		string updatedSelectedScriptText = ReplaceNamespaceDeclaration(selectedScriptText, oldNamespace, newNamespace, out bool namespaceChanged);

		if (!namespaceChanged)
		{
			GD.PushWarning($"Refactor Namespace cancelled: namespace declaration could not be updated in '{selectedScriptPath}'.");
			return false;
		}

		originalTextsByPath[selectedScriptPath] = selectedScriptText;
		pendingWrites[selectedScriptPath] = updatedSelectedScriptText;

		foreach (string linkedScriptPath in GetLinkedCSharpFilePaths())
		{
			string scriptPath = NormalizeRefactorNamespacePath(linkedScriptPath);

			if (!FileAccess.FileExists(scriptPath))
				continue;

			string scriptText = scriptPath == selectedScriptPath
				? updatedSelectedScriptText
				: ReadTextFile(scriptPath);

			string updatedScriptText = ReplaceUsingStatements(scriptText, oldNamespace, newNamespace, out bool usingChanged);

			if (!usingChanged)
				continue;

			if (!originalTextsByPath.ContainsKey(scriptPath))
				originalTextsByPath[scriptPath] = scriptPath == selectedScriptPath ? selectedScriptText : scriptText;

			pendingWrites[scriptPath] = updatedScriptText;
		}

		Dictionary<string, RefactorNamespaceOpenEditor> openEditorsByPath = GetOpenRefactorNamespaceEditorsByPath(
			originalTextsByPath,
			pendingWrites,
			out string unsafeOpenScriptList
		);

		if (!string.IsNullOrWhiteSpace(unsafeOpenScriptList))
		{
			GD.PushWarning($"Refactor Namespace cancelled: System Explorer could not safely match these open script editor buffer(s). Save/reopen them before refactoring:\n{unsafeOpenScriptList}");
			return false;
		}

		if (HasUnsavedRefactorNamespaceFiles(openEditorsByPath, out string unsavedScriptList))
		{
			GD.PushWarning($"Refactor Namespace cancelled: save affected script(s) before refactoring:\n{unsavedScriptList}");
			return false;
		}

		foreach (KeyValuePair<string, string> pendingWrite in pendingWrites)
		{
			if (!WriteTextFile(pendingWrite.Key, pendingWrite.Value))
			{
				GD.PushWarning($"Refactor Namespace failed while writing '{pendingWrite.Key}'. Some files may have already been updated.");
				RefreshGodotAfterRefactorNamespace(pendingWrites.Keys);
				return false;
			}
		}

		ApplyRefactorNamespaceTextToOpenEditors(openEditorsByPath, pendingWrites);
		RefreshGodotAfterRefactorNamespace(pendingWrites.Keys);

		_deferredRefactorNamespaceOriginalTextsByPath = new Dictionary<string, string>(originalTextsByPath, StringComparer.OrdinalIgnoreCase);

		string changedScriptPathPayload = BuildRefactorNamespacePathPayload(pendingWrites.Keys);
		CallDeferred(nameof(RefreshOpenScriptEditorBuffersAfterRefactorNamespaceDeferred), changedScriptPathPayload);
		CallDeferred(nameof(ReleaseTreeFocusAfterNavigation));

		DebugLogOperation("Refactor Namespace Completed", $"Updated {pendingWrites.Count} file(s).");
		return true;
	}

	private void RefreshOpenScriptEditorBuffersAfterRefactorNamespaceDeferred(string scriptPathPayload)
	{
		Dictionary<string, string> updatedTextsByPath = ParseRefactorNamespacePathPayload(scriptPathPayload)
			.Where(FileAccess.FileExists)
			.ToDictionary(
				NormalizeRefactorNamespacePath,
				ReadTextFile,
				StringComparer.OrdinalIgnoreCase
			);

		if (updatedTextsByPath.Count == 0)
		{
			_deferredRefactorNamespaceOriginalTextsByPath.Clear();
			return;
		}

		Dictionary<string, string> originalTextsByPath = new(StringComparer.OrdinalIgnoreCase);

		foreach (string scriptPath in updatedTextsByPath.Keys)
		{
			if (_deferredRefactorNamespaceOriginalTextsByPath.TryGetValue(scriptPath, out string originalText))
				originalTextsByPath[scriptPath] = originalText;
			else
				originalTextsByPath[scriptPath] = updatedTextsByPath[scriptPath];
		}

		_deferredRefactorNamespaceOriginalTextsByPath.Clear();

		Dictionary<string, RefactorNamespaceOpenEditor> openEditorsByPath = GetOpenRefactorNamespaceEditorsByPath(
			originalTextsByPath,
			updatedTextsByPath,
			out _
		);

		ApplyRefactorNamespaceTextToOpenEditors(openEditorsByPath, updatedTextsByPath);
	}

	private static Dictionary<string, RefactorNamespaceOpenEditor> GetOpenRefactorNamespaceEditorsByPath(
		Dictionary<string, string> originalTextsByPath,
		Dictionary<string, string> updatedTextsByPath,
		out string unsafeOpenScriptList
	)
	{
		unsafeOpenScriptList = "";
		Dictionary<string, RefactorNamespaceOpenEditor> result = new(StringComparer.OrdinalIgnoreCase);

		if (originalTextsByPath == null || updatedTextsByPath == null || updatedTextsByPath.Count == 0)
			return result;

		HashSet<string> targetPaths = updatedTextsByPath.Keys
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Select(NormalizeRefactorNamespacePath)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		if (targetPaths.Count == 0)
			return result;

		ScriptEditor scriptEditor = EditorInterface.Singleton?.GetScriptEditor();

		if (scriptEditor == null)
			return result;

		HashSet<string> openTargetPaths = GetOpenRefactorNamespaceScriptPaths(scriptEditor, targetPaths);
		HashSet<TextEdit> usedTextEditors = new();

		Script currentScript = scriptEditor.GetCurrentScript();
		string currentScriptPath = NormalizeRefactorNamespacePath(currentScript?.ResourcePath);

		if (targetPaths.Contains(currentScriptPath))
		{
			ScriptEditorBase currentEditor = scriptEditor.GetCurrentEditor();

			if (currentEditor?.GetBaseEditor() is TextEdit currentTextEditor)
				AddRefactorNamespaceOpenEditor(result, usedTextEditors, currentScriptPath, currentTextEditor);
		}

		List<TextEdit> openTextEditors = GetOpenRefactorNamespaceTextEditors(scriptEditor);
		List<string> unsafeOpenScripts = new();

		List<string> pathsToMatch = openTargetPaths
			.Concat(targetPaths)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		foreach (string targetPath in pathsToMatch)
		{
			if (result.ContainsKey(targetPath))
				continue;

			if (!originalTextsByPath.TryGetValue(targetPath, out string originalText)
				|| !updatedTextsByPath.TryGetValue(targetPath, out string updatedText))
			{
				if (openTargetPaths.Contains(targetPath))
					unsafeOpenScripts.Add(targetPath);

				continue;
			}

			List<TextEdit> matchingEditors = openTextEditors
				.Where(textEditor =>
					textEditor != null
					&& !usedTextEditors.Contains(textEditor)
					&& TextEditorMatchesRefactorNamespaceTexts(textEditor, targetPath, originalTextsByPath, updatedTextsByPath)
				)
				.ToList();

			if (matchingEditors.Count == 1)
			{
				TextEdit matchingEditor = matchingEditors[0];
				int matchingPathCount = pathsToMatch.Count(path =>
					!result.ContainsKey(path)
					&& TextEditorMatchesRefactorNamespaceTexts(matchingEditor, path, originalTextsByPath, updatedTextsByPath)
				);

				if (matchingPathCount == 1)
				{
					AddRefactorNamespaceOpenEditor(result, usedTextEditors, targetPath, matchingEditor);
					continue;
				}
			}

			if (matchingEditors.Count > 0 || openTargetPaths.Contains(targetPath))
				unsafeOpenScripts.Add(targetPath);
		}

		unsafeOpenScriptList = string.Join("\n", unsafeOpenScripts.Distinct(StringComparer.OrdinalIgnoreCase));
		return result;
	}

	private static bool TextEditorMatchesRefactorNamespaceTexts(
		TextEdit textEditor,
		string scriptPath,
		Dictionary<string, string> originalTextsByPath,
		Dictionary<string, string> updatedTextsByPath
	)
	{
		if (textEditor == null || string.IsNullOrWhiteSpace(scriptPath))
			return false;

		if (!originalTextsByPath.TryGetValue(scriptPath, out string originalText)
			|| !updatedTextsByPath.TryGetValue(scriptPath, out string updatedText))
		{
			return false;
		}

		return textEditor.Text == originalText || textEditor.Text == updatedText;
	}

	private static HashSet<string> GetOpenRefactorNamespaceScriptPaths(ScriptEditor scriptEditor, HashSet<string> targetPaths)
	{
		HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);

		if (scriptEditor == null || targetPaths == null || targetPaths.Count == 0)
			return result;

		foreach (Script openScript in scriptEditor.GetOpenScripts())
		{
			if (openScript == null)
				continue;

			string scriptPath = NormalizeRefactorNamespacePath(openScript.ResourcePath);

			if (targetPaths.Contains(scriptPath))
				result.Add(scriptPath);
		}

		return result;
	}

	private static List<TextEdit> GetOpenRefactorNamespaceTextEditors(ScriptEditor scriptEditor)
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

	private static void AddRefactorNamespaceOpenEditor(
		Dictionary<string, RefactorNamespaceOpenEditor> openEditorsByPath,
		HashSet<TextEdit> usedTextEditors,
		string scriptPath,
		TextEdit textEditor
	)
	{
		if (openEditorsByPath == null || usedTextEditors == null || textEditor == null)
			return;

		string normalizedPath = NormalizeRefactorNamespacePath(scriptPath);

		if (string.IsNullOrWhiteSpace(normalizedPath))
			return;

		openEditorsByPath[normalizedPath] = new RefactorNamespaceOpenEditor(normalizedPath, textEditor);
		usedTextEditors.Add(textEditor);
	}

	private static bool HasUnsavedRefactorNamespaceFiles(
		Dictionary<string, RefactorNamespaceOpenEditor> openEditorsByPath,
		out string unsavedScriptList
	)
	{
		unsavedScriptList = "";

		if (openEditorsByPath == null || openEditorsByPath.Count == 0)
			return false;

		List<string> affectedUnsavedFiles = openEditorsByPath.Values
			.Where(openEditor =>
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

	private static void ApplyRefactorNamespaceTextToOpenEditors(
		Dictionary<string, RefactorNamespaceOpenEditor> openEditorsByPath,
		Dictionary<string, string> updatedTextsByPath
	)
	{
		if (openEditorsByPath == null || updatedTextsByPath == null || openEditorsByPath.Count == 0)
			return;

		foreach (KeyValuePair<string, RefactorNamespaceOpenEditor> openEditorPair in openEditorsByPath)
		{
			if (!updatedTextsByPath.TryGetValue(openEditorPair.Key, out string updatedText))
				continue;

			ApplyRefactorNamespaceTextToOpenEditor(openEditorPair.Value.TextEditor, updatedText);
		}
	}

	private static void ApplyRefactorNamespaceTextToOpenEditor(TextEdit textEditor, string updatedText)
	{
		if (textEditor == null)
			return;

		if (textEditor.Text != updatedText)
			textEditor.Text = updatedText;

		textEditor.ClearUndoHistory();
		textEditor.TagSavedVersion();
	}

	private static string BuildRefactorNamespacePathPayload(IEnumerable<string> scriptPaths)
	{
		if (scriptPaths == null)
			return "";

		return string.Join(
			"\n",
			scriptPaths
				.Where(path => !string.IsNullOrWhiteSpace(path))
				.Select(NormalizeRefactorNamespacePath)
				.Distinct(StringComparer.OrdinalIgnoreCase)
		);
	}

	private static string[] ParseRefactorNamespacePathPayload(string scriptPathPayload)
	{
		if (string.IsNullOrWhiteSpace(scriptPathPayload))
			return Array.Empty<string>();

		return scriptPathPayload
			.Split('\n')
			.Select(NormalizeRefactorNamespacePath)
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private static string NormalizeRefactorNamespacePath(string path)
	{
		return path?.Trim().Replace('\\', '/') ?? "";
	}

	private static void RefreshGodotAfterRefactorNamespace(IEnumerable<string> changedScriptPaths)
	{
		List<string> changedPaths = changedScriptPaths
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Select(NormalizeRefactorNamespacePath)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		EditorFileSystem resourceFilesystem = EditorInterface.Singleton?.GetResourceFilesystem();

		if (resourceFilesystem == null)
			return;

		foreach (string scriptPath in changedPaths)
			resourceFilesystem.UpdateFile(scriptPath);
	}

	private IEnumerable<string> GetLinkedCSharpFilePaths()
	{
		return _systems.Values
			.SelectMany(entries => entries)
			.Where(IsScriptEntry)
			.Select(GetScriptPathFromEntry)
			.Where(path => !string.IsNullOrWhiteSpace(path) && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
			.Distinct(StringComparer.OrdinalIgnoreCase);
	}

	private static bool IsScriptEntry(string entry)
	{
		if (string.IsNullOrWhiteSpace(entry))
			return false;

		string entryWithoutLinkedScene = GetEntryWithoutLinkedScene(entry);
		string pathPart = entryWithoutLinkedScene.Contains("|")
			? entryWithoutLinkedScene.Split("|")[1]
			: entryWithoutLinkedScene;

		return !pathPart.StartsWith(SceneEntryMarker) && pathPart.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsValidNamespaceName(string namespaceName)
	{
		return !string.IsNullOrWhiteSpace(namespaceName)
			&& NamespaceIdentifierRegex.IsMatch(namespaceName);
	}

	private static string ReadNamespaceFromScript(string scriptPath)
	{
		if (!FileAccess.FileExists(scriptPath))
			return "";

		return GetNamespaceFromText(ReadTextFile(scriptPath));
	}

	private static string GetNamespaceFromText(string scriptText)
	{
		if (string.IsNullOrWhiteSpace(scriptText))
			return "";

		Match match = NamespaceDeclarationRegex.Match(scriptText);
		return match.Success ? match.Groups[2].Value : "";
	}

	private static string ReplaceNamespaceDeclaration(string scriptText, string oldNamespace, string newNamespace, out bool changed)
	{
		bool didChange = false;

		string updatedText = NamespaceDeclarationRegex.Replace(
			scriptText,
			match =>
			{
				if (didChange || match.Groups[2].Value != oldNamespace)
					return match.Value;

				didChange = true;
				return $"{match.Groups[1].Value}{newNamespace}{match.Groups[3].Value}";
			},
			1
		);

		changed = didChange;
		return updatedText;
	}

	private static string ReplaceUsingStatements(string scriptText, string oldNamespace, string newNamespace, out bool changed)
	{
		bool didChange = false;

		Regex usingRegex = new(
			$@"(?m)^(\s*using\s+){Regex.Escape(oldNamespace)}(\s*;)",
			RegexOptions.Compiled
		);

		string updatedText = usingRegex.Replace(
			scriptText,
			match =>
			{
				didChange = true;
				return $"{match.Groups[1].Value}{newNamespace}{match.Groups[2].Value}";
			}
		);

		changed = didChange;
		return updatedText;
	}

	private static string ReadTextFile(string path)
	{
		using FileAccess file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
		return file?.GetAsText() ?? "";
	}

	private static bool WriteTextFile(string path, string text)
	{
		using FileAccess file = FileAccess.Open(path, FileAccess.ModeFlags.Write);

		if (file == null)
			return false;

		file.StoreString(text);
		return true;
	}

	#endregion
}
#endif
