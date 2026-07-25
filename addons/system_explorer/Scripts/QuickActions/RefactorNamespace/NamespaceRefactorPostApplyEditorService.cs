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
	private string _deferredDebugOperationId = "";

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
		IEnumerable<string> changedScriptPaths,
		NamespaceRefactorDiagnosticContext diagnosticContext
	)
	{
		if (originalTextsByPath == null)
			throw new ArgumentNullException(nameof(originalTextsByPath));

		_deferredOriginalTextsByPath = new Dictionary<string, string>(
			originalTextsByPath,
			StringComparer.OrdinalIgnoreCase
		);
		_deferredDebugOperationId = diagnosticContext?.OperationId ?? "";

		string payload = NamespaceScriptPathPayloadCodec.Build(changedScriptPaths);
		diagnosticContext?.Log(
			"DeferredSync",
			() =>
				$"Deferred buffer refresh prepared; StateOwnerOperationId='{_deferredDebugOperationId}'; StoredOriginalTextCount={_deferredOriginalTextsByPath.Count}; StoredOriginalPaths={diagnosticContext.FormatPaths(_deferredOriginalTextsByPath.Keys)}; ChangedPaths={diagnosticContext.FormatPaths(changedScriptPaths)}; Payload='{payload}'."
		);
		return payload;
	}

	internal void RefreshOpenBuffersAfterDeferredResourceRefresh(
		ScriptEditor scriptEditor,
		string scriptPathPayload,
		NamespaceRefactorDiagnosticContext diagnosticContext
	)
	{
		diagnosticContext?.Log(
			"DeferredSync",
			() =>
				$"Deferred refresh consumption started; CallbackOperationId='{diagnosticContext.OperationId}'; StateOwnerOperationId='{_deferredDebugOperationId}'; StoredOriginalTextCount={_deferredOriginalTextsByPath.Count}; Payload='{scriptPathPayload ?? ""}'."
		);

		if (
			diagnosticContext?.IsEnabled == true
			&& !string.IsNullOrWhiteSpace(_deferredDebugOperationId)
			&& !string.Equals(
				_deferredDebugOperationId,
				diagnosticContext.OperationId,
				StringComparison.Ordinal
			)
		)
		{
			diagnosticContext.Log(
				"DeferredSync",
				() => $"DeferredStateOperationMismatch; CallbackOperationId='{diagnosticContext.OperationId}'; StateOwnerOperationId='{_deferredDebugOperationId}'."
			);
		}

		IReadOnlyList<string> payloadPaths = NamespaceScriptPathPayloadCodec.Parse(scriptPathPayload);
		Dictionary<string, string> updatedTextsByPath = new(StringComparer.OrdinalIgnoreCase);
		List<string> existingPaths = diagnosticContext?.IsEnabled == true ? new List<string>() : null;
		List<string> missingPaths = diagnosticContext?.IsEnabled == true ? new List<string>() : null;

		foreach (string payloadPath in payloadPaths)
		{
			if (!_fileExists(payloadPath))
			{
				missingPaths?.Add(payloadPath);
				continue;
			}

			existingPaths?.Add(payloadPath);
			updatedTextsByPath.Add(
				ScriptPathUtility.Normalize(payloadPath),
				_readText(payloadPath)
			);
		}

		diagnosticContext?.Log(
			"DeferredSync",
			() =>
				$"Deferred payload files inspected; PayloadPathCount={payloadPaths.Count}; ExistingCount={existingPaths?.Count ?? 0}; MissingCount={missingPaths?.Count ?? 0}; ExistingPaths={diagnosticContext.FormatPaths(existingPaths)}; MissingPaths={diagnosticContext.FormatPaths(missingPaths)}; UpdatedTextCount={updatedTextsByPath.Count}."
		);

		if (diagnosticContext?.IsEnabled == true)
		{
			HashSet<string> payloadPathSet = payloadPaths
				.Select(ScriptPathUtility.Normalize)
				.ToHashSet(StringComparer.OrdinalIgnoreCase);
			HashSet<string> storedPathSet = _deferredOriginalTextsByPath.Keys.ToHashSet(
				StringComparer.OrdinalIgnoreCase
			);
			if (!payloadPathSet.SetEquals(storedPathSet))
			{
				diagnosticContext.Log(
					"DeferredSync",
					() =>
						$"Deferred state path mismatch; PayloadCount={payloadPathSet.Count}; StoredCount={storedPathSet.Count}; PayloadOnly={diagnosticContext.FormatPaths(payloadPathSet.Except(storedPathSet, StringComparer.OrdinalIgnoreCase))}; StoredOnly={diagnosticContext.FormatPaths(storedPathSet.Except(payloadPathSet, StringComparer.OrdinalIgnoreCase))}."
				);
			}
		}

		if (updatedTextsByPath.Count == 0)
		{
			diagnosticContext?.Log(
				"DeferredSync",
				() => $"Deferred refresh ended without updated texts; StoredStateMissing={_deferredOriginalTextsByPath.Count == 0}."
			);
			_deferredOriginalTextsByPath.Clear();
			_deferredDebugOperationId = "";
			return;
		}

		Dictionary<string, string> originalTextsByPath = new(StringComparer.OrdinalIgnoreCase);
		List<string> originalTextPaths = diagnosticContext?.IsEnabled == true ? new List<string>() : null;
		List<string> fallbackToUpdatedTextPaths = diagnosticContext?.IsEnabled == true ? new List<string>() : null;

		foreach (string scriptPath in updatedTextsByPath.Keys)
		{
			if (_deferredOriginalTextsByPath.TryGetValue(scriptPath, out string originalText))
			{
				originalTextsByPath[scriptPath] = originalText;
				originalTextPaths?.Add(scriptPath);
			}
			else
			{
				originalTextsByPath[scriptPath] = updatedTextsByPath[scriptPath];
				fallbackToUpdatedTextPaths?.Add(scriptPath);
			}
		}

		diagnosticContext?.Log(
			"DeferredSync",
			() =>
				$"Deferred verification texts built; OriginalTextCount={originalTextsByPath.Count}; UpdatedTextCount={updatedTextsByPath.Count}; OriginalStatePathCount={originalTextPaths?.Count ?? 0}; FallbackPathCount={fallbackToUpdatedTextPaths?.Count ?? 0}; OriginalStatePaths={diagnosticContext.FormatPaths(originalTextPaths)}; FallbackToUpdatedTextPaths={diagnosticContext.FormatPaths(fallbackToUpdatedTextPaths)}."
		);

		_deferredOriginalTextsByPath.Clear();
		_deferredDebugOperationId = "";

		ScriptEditorBufferGroupLookupResult lookupResult =
			_bufferLocator.LocateOpenScriptEditorGroupsByScriptTextsWithoutActivation(
				scriptEditor,
				originalTextsByPath,
				updatedTextsByPath,
				null,
				diagnosticContext?.BufferDiagnostics
			);

		diagnosticContext?.Log(
			"DeferredSync",
			() =>
				$"Deferred lookup completed; Success={lookupResult.Success}; Failure={lookupResult.Failure}; FailurePath='{lookupResult.FailurePath}'; GroupCount={lookupResult.OpenEditorGroupsByPath.Count}; UnsafePaths={diagnosticContext.FormatPaths(lookupResult.UnsafeOpenScriptPaths)}; AmbiguousPaths={diagnosticContext.FormatPaths(lookupResult.AmbiguousOpenScriptPaths)}; UnmatchedRequiredPaths={diagnosticContext.FormatPaths(lookupResult.UnmatchedRequiredPaths)}; ApplyPaths={diagnosticContext.FormatPaths(lookupResult.OpenEditorGroupsByPath.Keys)}."
		);
		if (
			diagnosticContext?.IsEnabled == true
			&& (
				!lookupResult.Success
				|| lookupResult.UnsafeOpenScriptPaths.Count > 0
				|| lookupResult.AmbiguousOpenScriptPaths.Count > 0
				|| lookupResult.UnmatchedRequiredPaths.Count > 0
			)
		)
		{
			diagnosticContext.Log(
				"DeferredSync",
				() =>
					$"Deferred lookup anomaly observed without behavior change; Success={lookupResult.Success}; Failure={lookupResult.Failure}; FailurePath='{lookupResult.FailurePath}'; UnsafePaths={diagnosticContext.FormatPaths(lookupResult.UnsafeOpenScriptPaths)}; AmbiguousPaths={diagnosticContext.FormatPaths(lookupResult.AmbiguousOpenScriptPaths)}; UnmatchedRequiredPaths={diagnosticContext.FormatPaths(lookupResult.UnmatchedRequiredPaths)}."
			);
		}

		_bufferBatchService.ApplyCommittedTexts(
			lookupResult.OpenEditorGroupsByPath,
			updatedTextsByPath,
			diagnosticContext?.BufferDiagnostics
		);
		diagnosticContext?.Log("DeferredSync", "Deferred ApplyCommittedTexts completed.");
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
