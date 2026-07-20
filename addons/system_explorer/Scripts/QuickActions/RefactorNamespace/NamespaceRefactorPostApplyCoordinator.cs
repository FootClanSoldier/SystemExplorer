#if TOOLS
using Godot;
using System;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal sealed class NamespaceRefactorPostApplyCoordinator
{
	private readonly NamespaceRefactorPostApplyEditorService _postApplyEditorService;
	private readonly Action<string> _scheduleDeferredBufferRefresh;
	private readonly Action _syncSelectionAfterOperation;
	private readonly Action<string> _scheduleDeferredTargetScriptRestoration;
	private readonly Action _scheduleDeferredSelectionSync;
	private readonly Action _scheduleDeferredTreeFocusRelease;

	internal NamespaceRefactorPostApplyCoordinator(
		NamespaceRefactorPostApplyEditorService postApplyEditorService,
		Action<string> scheduleDeferredBufferRefresh,
		Action syncSelectionAfterOperation,
		Action<string> scheduleDeferredTargetScriptRestoration,
		Action scheduleDeferredSelectionSync,
		Action scheduleDeferredTreeFocusRelease
	)
	{
		_postApplyEditorService =
			postApplyEditorService
			?? throw new ArgumentNullException("postApplyEditorService");
		_scheduleDeferredBufferRefresh =
			scheduleDeferredBufferRefresh
			?? throw new ArgumentNullException("scheduleDeferredBufferRefresh");
		_syncSelectionAfterOperation =
			syncSelectionAfterOperation
			?? throw new ArgumentNullException("syncSelectionAfterOperation");
		_scheduleDeferredTargetScriptRestoration =
			scheduleDeferredTargetScriptRestoration
			?? throw new ArgumentNullException("scheduleDeferredTargetScriptRestoration");
		_scheduleDeferredSelectionSync =
			scheduleDeferredSelectionSync
			?? throw new ArgumentNullException("scheduleDeferredSelectionSync");
		_scheduleDeferredTreeFocusRelease =
			scheduleDeferredTreeFocusRelease
			?? throw new ArgumentNullException("scheduleDeferredTreeFocusRelease");
	}

	internal void CompleteSingleReplacement(
		EditorInterface editorInterface,
		NamespaceRefactorPendingWriteSet writeSet,
		Action<string> debugLog
	)
	{
		string changedScriptPathPayload =
			_postApplyEditorService.PrepareDeferredBufferRefresh(
				writeSet.OriginalTextsByPath,
				writeSet.PendingWrites.Keys
			);
		_postApplyEditorService.RestoreTargetScriptEditor(
			editorInterface,
			writeSet.SelectedScriptPath,
			debugLog
		);
		_syncSelectionAfterOperation();
		_scheduleDeferredBufferRefresh(changedScriptPathPayload);
		_scheduleDeferredTargetScriptRestoration(writeSet.SelectedScriptPath);
		_scheduleDeferredSelectionSync();
		_scheduleDeferredTreeFocusRelease();
	}

	internal void CompletePendingWriteOperation(
		EditorInterface editorInterface,
		NamespaceRefactorPendingWriteSet writeSet,
		string explicitRestorePath,
		bool syncSelectionAfterOperation,
		Action<string> debugLog
	)
	{
		string changedScriptPathPayload =
			_postApplyEditorService.PrepareDeferredBufferRefresh(
				writeSet.OriginalTextsByPath,
				writeSet.PendingWrites.Keys
			);
		string restoreScriptPath = "";

		if (syncSelectionAfterOperation)
		{
			restoreScriptPath = string.IsNullOrWhiteSpace(explicitRestorePath)
				? writeSet.SelectedScriptPath
				: explicitRestorePath;

			if (!string.IsNullOrWhiteSpace(restoreScriptPath))
			{
				_postApplyEditorService.RestoreTargetScriptEditor(
					editorInterface,
					restoreScriptPath,
					debugLog
				);
			}
		}

		_scheduleDeferredBufferRefresh(changedScriptPathPayload);

		if (syncSelectionAfterOperation && !string.IsNullOrWhiteSpace(restoreScriptPath))
		{
			_syncSelectionAfterOperation();
			_scheduleDeferredTargetScriptRestoration(restoreScriptPath);
			_scheduleDeferredSelectionSync();
		}

		if (syncSelectionAfterOperation)
			_scheduleDeferredTreeFocusRelease();
	}
}
#endif
