#if TOOLS
using System;
using System.Collections.Generic;

namespace SystemExplorer.EditorIntegration.ScriptEditing;

internal readonly record struct ScriptEditorBufferAutosaveOperationResult(
	bool Success,
	bool DidAutosave,
	ScriptEditorBufferAutosaveResult FailedAutosave
);

internal sealed class ScriptEditorBufferAutosaveCoordinator
{
	private readonly ScriptEditorBufferAutosaveService _autosaveService;
	private readonly ScriptEditorBufferBatchService _batchService;

	internal ScriptEditorBufferAutosaveCoordinator(
		ScriptEditorBufferAutosaveService autosaveService,
		ScriptEditorBufferBatchService batchService
	)
	{
		_autosaveService =
			autosaveService ?? throw new ArgumentNullException(nameof(autosaveService));
		_batchService = batchService ?? throw new ArgumentNullException(nameof(batchService));
	}

	internal ScriptEditorBufferAutosaveOperationResult TryAutosaveIfNeeded(
		OpenScriptEditorBuffer openEditor,
		bool failOnSavedDiskMismatch
	)
	{
		ScriptEditorBufferAutosaveResult autosaveResult = _autosaveService.TryAutosaveIfNeeded(
			openEditor,
			failOnSavedDiskMismatch
		);

		if (autosaveResult.Success)
		{
			return new ScriptEditorBufferAutosaveOperationResult(
				true,
				autosaveResult.DidAutosave,
				default
			);
		}

		return new ScriptEditorBufferAutosaveOperationResult(
			false,
			autosaveResult.DidAutosave,
			autosaveResult
		);
	}

	internal ScriptEditorBufferAutosaveOperationResult TryAutosaveBatchIfNeeded(
		IReadOnlyDictionary<string, OpenScriptEditorBuffer> openEditorsByPath,
		bool failOnSavedDiskMismatch
	)
	{
		ScriptEditorBufferBatchAutosaveResult batchResult = _batchService.TryAutosaveIfNeeded(
			openEditorsByPath?.Values,
			failOnSavedDiskMismatch
		);

		if (batchResult.Success)
		{
			return new ScriptEditorBufferAutosaveOperationResult(
				true,
				batchResult.DidAutosaveAny,
				default
			);
		}

		return new ScriptEditorBufferAutosaveOperationResult(
			false,
			batchResult.DidAutosaveAny,
			batchResult.FailedAutosave
		);
	}
}
#endif
