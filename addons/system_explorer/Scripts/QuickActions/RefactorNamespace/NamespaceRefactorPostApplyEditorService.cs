#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal sealed class NamespaceRefactorPostApplyEditorService
{
	private readonly ScriptEditorBufferLocator _bufferLocator;
	private readonly ScriptEditorBufferBatchService _bufferBatchService;
	private readonly Func<string, bool> _fileExists;
	private readonly Func<string, string> _readText;
	private readonly Func<string, Script> _loadScript;
	private Dictionary<string, string> _deferredOriginalTextsByPath = new(
		StringComparer.OrdinalIgnoreCase
	);

	internal NamespaceRefactorPostApplyEditorService(
		ScriptEditorBufferLocator bufferLocator,
		ScriptEditorBufferBatchService bufferBatchService,
		Func<string, bool> fileExists,
		Func<string, string> readText,
		Func<string, Script> loadScript
	)
	{
		_bufferLocator = bufferLocator ?? throw new ArgumentNullException(nameof(bufferLocator));
		_bufferBatchService =
			bufferBatchService ?? throw new ArgumentNullException(nameof(bufferBatchService));
		_fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
		_readText = readText ?? throw new ArgumentNullException(nameof(readText));
		_loadScript = loadScript ?? throw new ArgumentNullException(nameof(loadScript));
	}

	internal string PrepareDeferredBufferRefresh(
		Dictionary<string, string> originalTextsByPath,
		IEnumerable<string> changedScriptPaths
	)
	{
		if (originalTextsByPath == null)
			throw new ArgumentNullException(nameof(originalTextsByPath));

		_deferredOriginalTextsByPath = new Dictionary<string, string>(
			originalTextsByPath,
			StringComparer.OrdinalIgnoreCase
		);

		return NamespaceScriptPathPayloadCodec.Build(changedScriptPaths);
	}

	internal void RefreshOpenBuffersAfterDeferredResourceRefresh(
		ScriptEditor scriptEditor,
		string scriptPathPayload
	)
	{
		Dictionary<string, string> updatedTextsByPath = NamespaceScriptPathPayloadCodec
			.Parse(scriptPathPayload)
			.Where(_fileExists)
			.ToDictionary(
				ScriptPathUtility.Normalize,
				_readText,
				StringComparer.OrdinalIgnoreCase
			);

		if (updatedTextsByPath.Count == 0)
		{
			_deferredOriginalTextsByPath.Clear();
			return;
		}

		Dictionary<string, string> originalTextsByPath = new(StringComparer.OrdinalIgnoreCase);

		foreach (string scriptPath in updatedTextsByPath.Keys)
		{
			if (
				_deferredOriginalTextsByPath.TryGetValue(scriptPath, out string originalText)
			)
				originalTextsByPath[scriptPath] = originalText;
			else
				originalTextsByPath[scriptPath] = updatedTextsByPath[scriptPath];
		}

		_deferredOriginalTextsByPath.Clear();

		ScriptEditorBufferLookupResult lookupResult =
			_bufferLocator.LocateByScriptTextsWithoutActivation(
				scriptEditor,
				originalTextsByPath,
				updatedTextsByPath
			);

		_bufferBatchService.ApplyCommittedTexts(
			lookupResult.OpenEditorsByPath,
			updatedTextsByPath
		);
	}

	internal void RestoreTargetScriptEditor(
		EditorInterface editorInterface,
		string scriptPath,
		Action<string> debugLog
	)
	{
		string normalizedPath = ScriptPathUtility.Normalize(scriptPath);

		if (string.IsNullOrWhiteSpace(normalizedPath))
			return;

		if (editorInterface == null)
			return;

		if (IsCurrentScriptPath(editorInterface, normalizedPath))
			return;

		Script script = _loadScript(normalizedPath);

		if (script == null)
		{
			debugLog?.Invoke(
				$"Refactor Namespace could not restore target script editor because '{normalizedPath}' could not be loaded."
			);
			return;
		}

		editorInterface.EditScript(script);
		debugLog?.Invoke($"Refactor Namespace restored target script editor '{normalizedPath}'.");
	}

	private static bool IsCurrentScriptPath(
		EditorInterface editorInterface,
		string normalizedScriptPath
	)
	{
		if (
			editorInterface == null
			|| !GodotObject.IsInstanceValid(editorInterface)
		)
			return false;

		ScriptEditor scriptEditor = editorInterface.GetScriptEditor();

		if (scriptEditor == null || !GodotObject.IsInstanceValid(scriptEditor))
			return false;

		Script currentScript = scriptEditor.GetCurrentScript();

		if (currentScript == null || !GodotObject.IsInstanceValid(currentScript))
			return false;

		string currentScriptPath = ScriptPathUtility.Normalize(currentScript.ResourcePath);

		return string.Equals(
			currentScriptPath,
			normalizedScriptPath,
			StringComparison.OrdinalIgnoreCase
		);
	}
}
#endif
