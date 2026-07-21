#if TOOLS
using System;
using System.Collections.Generic;

namespace SystemExplorer.EditorIntegration.ScriptEditing;

internal readonly record struct ScriptEditorBufferBatchAutosaveResult(
	bool Success,
	bool DidAutosaveAny,
	ScriptEditorBufferAutosaveResult FailedAutosave
)
{
	internal static ScriptEditorBufferBatchAutosaveResult Succeeded(bool didAutosaveAny) =>
		new(true, didAutosaveAny, default);

	internal static ScriptEditorBufferBatchAutosaveResult Failed(
		bool didAutosaveAny,
		ScriptEditorBufferAutosaveResult failedAutosave
	) => new(false, didAutosaveAny, failedAutosave);
}

internal sealed class ScriptEditorBufferBatchService
{
	private readonly ScriptEditorBufferAutosaveService _autosaveService;

	internal ScriptEditorBufferBatchService(ScriptEditorBufferAutosaveService autosaveService)
	{
		_autosaveService =
			autosaveService ?? throw new ArgumentNullException(nameof(autosaveService));
	}

	internal IReadOnlyList<string> GetUnsavedPaths(IEnumerable<OpenScriptEditorBuffer> openEditors)
	{
		return ScriptEditorBufferStateService.GetUnsavedPaths(openEditors);
	}

	internal ScriptEditorBufferBatchAutosaveResult TryAutosaveIfNeeded(
		IEnumerable<OpenScriptEditorBuffer> openEditors,
		bool failOnSavedDiskMismatch
	)
	{
		bool didAutosaveAny = false;

		if (openEditors == null)
			return ScriptEditorBufferBatchAutosaveResult.Succeeded(didAutosaveAny);

		foreach (OpenScriptEditorBuffer openEditor in openEditors)
		{
			ScriptEditorBufferAutosaveResult autosaveResult = _autosaveService.TryAutosaveIfNeeded(
				openEditor,
				failOnSavedDiskMismatch
			);

			if (autosaveResult.DidAutosave)
				didAutosaveAny = true;

			if (!autosaveResult.Success)
			{
				return ScriptEditorBufferBatchAutosaveResult.Failed(didAutosaveAny, autosaveResult);
			}
		}

		return ScriptEditorBufferBatchAutosaveResult.Succeeded(didAutosaveAny);
	}

	internal void ApplyCommittedTexts(
		IReadOnlyDictionary<string, OpenScriptEditorBuffer> openEditorsByPath,
		IReadOnlyDictionary<string, string> updatedTextsByPath
	)
	{
		if (openEditorsByPath == null || updatedTextsByPath == null || openEditorsByPath.Count == 0)
			return;

		foreach (KeyValuePair<string, OpenScriptEditorBuffer> openEditorPair in openEditorsByPath)
		{
			if (!updatedTextsByPath.TryGetValue(openEditorPair.Key, out string updatedText))
				continue;

			ScriptEditorBufferStateService.ApplyCommittedText(
				openEditorPair.Value.TextEditor,
				updatedText
			);
		}
	}
}
#endif
