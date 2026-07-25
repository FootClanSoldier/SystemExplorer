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
		bool failOnSavedDiskMismatch,
		ScriptEditorBufferDiagnosticSink diagnostics = null
	)
	{
		return ToOperationResult(
			_autosaveService.TryAutosaveIfNeeded(openEditor, failOnSavedDiskMismatch, diagnostics)
		);
	}

	internal ScriptEditorBufferAutosaveOperationResult TryAutosaveGroupIfNeeded(
		OpenScriptEditorBufferGroup openEditorGroup,
		ScriptEditorBufferDiagnosticSink diagnostics = null
	)
	{
		return ToOperationResult(_autosaveService.TryAutosaveGroupIfNeeded(openEditorGroup, diagnostics));
	}

	internal ScriptEditorBufferAutosaveOperationResult TryAutosaveBatchIfNeeded(
		IReadOnlyDictionary<string, OpenScriptEditorBuffer> openEditorsByPath,
		bool failOnSavedDiskMismatch,
		ScriptEditorBufferDiagnosticSink diagnostics = null
	)
	{
		return ToOperationResult(
			_batchService.TryAutosaveIfNeeded(
				openEditorsByPath?.Values,
				failOnSavedDiskMismatch,
				diagnostics
			)
		);
	}

	internal ScriptEditorBufferAutosaveOperationResult TryAutosaveGroupBatchIfNeeded(
		IReadOnlyDictionary<string, OpenScriptEditorBufferGroup> openEditorGroupsByPath,
		ScriptEditorBufferDiagnosticSink diagnostics = null
	)
	{
		return ToOperationResult(
			_batchService.TryAutosaveGroupsIfNeeded(openEditorGroupsByPath?.Values, diagnostics)
		);
	}

	private static ScriptEditorBufferAutosaveOperationResult ToOperationResult(
		ScriptEditorBufferAutosaveResult autosaveResult
	)
	{
		return autosaveResult.Success
			? new ScriptEditorBufferAutosaveOperationResult(
				true,
				autosaveResult.DidAutosave,
				default
			)
			: new ScriptEditorBufferAutosaveOperationResult(
				false,
				autosaveResult.DidAutosave,
				autosaveResult
			);
	}

	private static ScriptEditorBufferAutosaveOperationResult ToOperationResult(
		ScriptEditorBufferBatchAutosaveResult batchResult
	)
	{
		return batchResult.Success
			? new ScriptEditorBufferAutosaveOperationResult(
				true,
				batchResult.DidAutosaveAny,
				default
			)
			: new ScriptEditorBufferAutosaveOperationResult(
				false,
				batchResult.DidAutosaveAny,
				batchResult.FailedAutosave
			);
	}
}
#endif
