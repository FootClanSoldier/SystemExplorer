#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal enum NamespaceRefactorAffectedOpenBufferMatchMode
{
	ActivatingOnly,
	NonActivatingOnly,
	NonActivatingWithActivationFallback,
}

internal enum NamespaceRefactorPendingWriteApplyFailure
{
	None,
	AffectedOpenBufferMatchFailed,
	UnsafeNonActivatingBufferMatch,
	AutosaveFailed,
	RebuildAfterAutosaveFailed,
	AffectedOpenBufferRematchAfterAutosaveFailed,
	StillUnsaved,
	WriteFailed,
}

internal sealed class NamespaceRefactorPendingWriteApplyResult
{
	internal NamespaceRefactorPendingWriteApplyFailure Failure { get; }
	internal bool Success => Failure == NamespaceRefactorPendingWriteApplyFailure.None;
	internal bool DidAutosave { get; }
	internal NamespaceRefactorPendingWriteSet WriteSet { get; }
	internal string AffectedOpenBufferFailureMessage { get; }
	internal IReadOnlyList<string> UnsafeOpenScriptPaths { get; }
	internal IReadOnlyList<string> UnsavedScriptPaths { get; }
	internal ScriptEditorBufferAutosaveResult FailedAutosave { get; }
	internal string FailedWritePath { get; }

	private NamespaceRefactorPendingWriteApplyResult(
		NamespaceRefactorPendingWriteApplyFailure failure,
		bool didAutosave,
		NamespaceRefactorPendingWriteSet writeSet,
		string affectedOpenBufferFailureMessage,
		IEnumerable<string> unsafeOpenScriptPaths,
		IEnumerable<string> unsavedScriptPaths,
		ScriptEditorBufferAutosaveResult failedAutosave,
		string failedWritePath
	)
	{
		Failure = failure;
		DidAutosave = didAutosave;
		WriteSet = writeSet;
		AffectedOpenBufferFailureMessage = affectedOpenBufferFailureMessage ?? "";
		UnsafeOpenScriptPaths = CreateReadOnlyList(unsafeOpenScriptPaths);
		UnsavedScriptPaths = CreateReadOnlyList(unsavedScriptPaths);
		FailedAutosave = failedAutosave;
		FailedWritePath = failedWritePath ?? "";
	}

	internal static NamespaceRefactorPendingWriteApplyResult Succeeded(
		bool didAutosave,
		NamespaceRefactorPendingWriteSet writeSet
	)
	{
		return new NamespaceRefactorPendingWriteApplyResult(
			NamespaceRefactorPendingWriteApplyFailure.None,
			didAutosave,
			writeSet,
			"",
			null,
			null,
			default,
			""
		);
	}

	internal static NamespaceRefactorPendingWriteApplyResult Failed(
		NamespaceRefactorPendingWriteApplyFailure failure,
		bool didAutosave,
		NamespaceRefactorPendingWriteSet writeSet,
		string affectedOpenBufferFailureMessage = "",
		IEnumerable<string> unsafeOpenScriptPaths = null,
		IEnumerable<string> unsavedScriptPaths = null,
		ScriptEditorBufferAutosaveResult failedAutosave = default,
		string failedWritePath = ""
	)
	{
		return new NamespaceRefactorPendingWriteApplyResult(
			failure,
			didAutosave,
			writeSet,
			affectedOpenBufferFailureMessage,
			unsafeOpenScriptPaths,
			unsavedScriptPaths,
			failedAutosave,
			failedWritePath
		);
	}

	private static IReadOnlyList<string> CreateReadOnlyList(IEnumerable<string> source)
	{
		List<string> copy = source == null ? new List<string>() : new List<string>(source);
		return copy.AsReadOnly();
	}
}

internal sealed class NamespaceRefactorPendingWriteApplyService
{
	private readonly NamespaceOpenBufferActivationService _openBufferActivationService;
	private readonly ScriptEditorBufferLocator _bufferLocator;
	private readonly ScriptEditorBufferAutosaveCoordinator _autosaveCoordinator;
	private readonly ScriptEditorBufferBatchService _bufferBatchService;
	private readonly Func<string, string, bool> _writeText;
	private readonly Action<IEnumerable<string>> _refreshChangedScripts;

	internal NamespaceRefactorPendingWriteApplyService(
		NamespaceOpenBufferActivationService openBufferActivationService,
		ScriptEditorBufferLocator bufferLocator,
		ScriptEditorBufferAutosaveCoordinator autosaveCoordinator,
		ScriptEditorBufferBatchService bufferBatchService,
		Func<string, string, bool> writeText,
		Action<IEnumerable<string>> refreshChangedScripts
	)
	{
		_openBufferActivationService =
			openBufferActivationService
			?? throw new ArgumentNullException(nameof(openBufferActivationService));
		_bufferLocator = bufferLocator ?? throw new ArgumentNullException(nameof(bufferLocator));
		_autosaveCoordinator =
			autosaveCoordinator ?? throw new ArgumentNullException(nameof(autosaveCoordinator));
		_bufferBatchService =
			bufferBatchService ?? throw new ArgumentNullException(nameof(bufferBatchService));
		_writeText = writeText ?? throw new ArgumentNullException(nameof(writeText));
		_refreshChangedScripts =
			refreshChangedScripts
			?? throw new ArgumentNullException(nameof(refreshChangedScripts));
	}

	internal NamespaceRefactorPendingWriteApplyResult TryApplyPendingWrites(
		EditorInterface editorInterface,
		ScriptEditor scriptEditor,
		NamespaceRefactorPendingWriteSet initialWriteSet,
		NamespaceRefactorAffectedOpenBufferMatchMode matchMode,
		Func<NamespaceRefactorPendingWriteBuildResult> rebuildAfterAutosave,
		Action<string> debugLog
	)
	{
		if (initialWriteSet == null)
			throw new ArgumentNullException(nameof(initialWriteSet));

		NamespaceRefactorPendingWriteSet finalWriteSet = initialWriteSet;

		if (
			!TryMatchAffectedOpenBuffers(
				editorInterface,
				scriptEditor,
				finalWriteSet,
				matchMode,
				debugLog,
				out Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath,
				out string affectedOpenBufferFailureMessage,
				out IReadOnlyList<string> unsafeOpenScriptPaths
			)
		)
		{
			return NamespaceRefactorPendingWriteApplyResult.Failed(
				failure: GetInitialMatchFailure(matchMode),
				didAutosave: false,
				writeSet: finalWriteSet,
				affectedOpenBufferFailureMessage: affectedOpenBufferFailureMessage,
				unsafeOpenScriptPaths: unsafeOpenScriptPaths
			);
		}

		ScriptEditorBufferAutosaveOperationResult autosaveResult =
			_autosaveCoordinator.TryAutosaveBatchIfNeeded(
				openEditorsByPath,
				failOnSavedDiskMismatch: true
			);

		if (!autosaveResult.Success)
		{
			return NamespaceRefactorPendingWriteApplyResult.Failed(
				failure: NamespaceRefactorPendingWriteApplyFailure.AutosaveFailed,
				didAutosave: autosaveResult.DidAutosave,
				writeSet: finalWriteSet,
				failedAutosave: autosaveResult.FailedAutosave
			);
		}

		bool didAutosave = autosaveResult.DidAutosave;

		if (didAutosave && rebuildAfterAutosave != null)
		{
			NamespaceRefactorPendingWriteBuildResult rebuildResult = rebuildAfterAutosave();

			if (rebuildResult == null || !rebuildResult.Success || rebuildResult.WriteSet == null)
			{
				return NamespaceRefactorPendingWriteApplyResult.Failed(
					failure: NamespaceRefactorPendingWriteApplyFailure.RebuildAfterAutosaveFailed,
					didAutosave: didAutosave,
					writeSet: finalWriteSet
				);
			}

			finalWriteSet = rebuildResult.WriteSet;

			if (
				!TryMatchAffectedOpenBuffers(
					editorInterface,
					scriptEditor,
					finalWriteSet,
					matchMode,
					debugLog,
					out openEditorsByPath,
					out affectedOpenBufferFailureMessage,
					out unsafeOpenScriptPaths
				)
			)
			{
				return NamespaceRefactorPendingWriteApplyResult.Failed(
					failure: NamespaceRefactorPendingWriteApplyFailure.AffectedOpenBufferRematchAfterAutosaveFailed,
					didAutosave: didAutosave,
					writeSet: finalWriteSet,
					affectedOpenBufferFailureMessage: affectedOpenBufferFailureMessage,
					unsafeOpenScriptPaths: unsafeOpenScriptPaths
				);
			}
		}

		IReadOnlyList<string> unsavedPaths = _bufferBatchService.GetUnsavedPaths(
			openEditorsByPath?.Values
		);

		if (unsavedPaths.Count > 0)
		{
			return NamespaceRefactorPendingWriteApplyResult.Failed(
				failure: NamespaceRefactorPendingWriteApplyFailure.StillUnsaved,
				didAutosave: didAutosave,
				writeSet: finalWriteSet,
				unsavedScriptPaths: unsavedPaths
			);
		}

		foreach (KeyValuePair<string, string> pendingWrite in finalWriteSet.PendingWrites)
		{
			if (_writeText(pendingWrite.Key, pendingWrite.Value))
				continue;

			_refreshChangedScripts(finalWriteSet.PendingWrites.Keys);
			return NamespaceRefactorPendingWriteApplyResult.Failed(
				failure: NamespaceRefactorPendingWriteApplyFailure.WriteFailed,
				didAutosave: didAutosave,
				writeSet: finalWriteSet,
				failedWritePath: pendingWrite.Key
			);
		}

		_bufferBatchService.ApplyCommittedTexts(
			openEditorsByPath,
			finalWriteSet.PendingWrites
		);
		_refreshChangedScripts(finalWriteSet.PendingWrites.Keys);

		return NamespaceRefactorPendingWriteApplyResult.Succeeded(didAutosave, finalWriteSet);
	}

	private bool TryMatchAffectedOpenBuffers(
		EditorInterface editorInterface,
		ScriptEditor scriptEditor,
		NamespaceRefactorPendingWriteSet writeSet,
		NamespaceRefactorAffectedOpenBufferMatchMode matchMode,
		Action<string> debugLog,
		out Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath,
		out string affectedOpenBufferFailureMessage,
		out IReadOnlyList<string> unsafeOpenScriptPaths
	)
	{
		affectedOpenBufferFailureMessage = "";
		unsafeOpenScriptPaths = Array.Empty<string>();

		switch (matchMode)
		{
			case NamespaceRefactorAffectedOpenBufferMatchMode.ActivatingOnly:
				return _openBufferActivationService.TryGetOpenScriptEditorsByActivatingPaths(
					editorInterface,
					scriptEditor,
					writeSet.PendingWrites.Keys,
					failIfOpenEditorCannotBeMatched: true,
					debugLog: debugLog,
					openEditorsByPath: out openEditorsByPath,
					failureMessage: out affectedOpenBufferFailureMessage
				);
			case NamespaceRefactorAffectedOpenBufferMatchMode.NonActivatingOnly:
				return TryMatchAffectedOpenBuffersWithoutActivation(
					scriptEditor,
					writeSet,
					out openEditorsByPath,
					out unsafeOpenScriptPaths
				);
			case NamespaceRefactorAffectedOpenBufferMatchMode.NonActivatingWithActivationFallback:
				return TryMatchAffectedOpenBuffersWithActivationFallback(
					editorInterface,
					scriptEditor,
					writeSet,
					debugLog,
					out openEditorsByPath,
					out affectedOpenBufferFailureMessage,
					out unsafeOpenScriptPaths
				);
			default:
				throw new ArgumentOutOfRangeException(nameof(matchMode), matchMode, null);
		}
	}

	private bool TryMatchAffectedOpenBuffersWithoutActivation(
		ScriptEditor scriptEditor,
		NamespaceRefactorPendingWriteSet writeSet,
		out Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath,
		out IReadOnlyList<string> unsafeOpenScriptPaths
	)
	{
		ScriptEditorBufferLookupResult lookupResult =
			_bufferLocator.LocateByScriptTextsWithoutActivation(
				scriptEditor,
				writeSet.OriginalTextsByPath,
				writeSet.PendingWrites
			);

		openEditorsByPath = lookupResult.OpenEditorsByPath;
		unsafeOpenScriptPaths = lookupResult.UnsafeOpenScriptPaths;
		return unsafeOpenScriptPaths.Count == 0;
	}

	private bool TryMatchAffectedOpenBuffersWithActivationFallback(
		EditorInterface editorInterface,
		ScriptEditor scriptEditor,
		NamespaceRefactorPendingWriteSet writeSet,
		Action<string> debugLog,
		out Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath,
		out string affectedOpenBufferFailureMessage,
		out IReadOnlyList<string> unsafeOpenScriptPaths
	)
	{
		ScriptEditorBufferLookupResult lookupResult =
			_bufferLocator.LocateByScriptTextsWithoutActivation(
				scriptEditor,
				writeSet.OriginalTextsByPath,
				writeSet.PendingWrites
			);

		openEditorsByPath = new Dictionary<string, OpenScriptEditorBuffer>(
			StringComparer.OrdinalIgnoreCase
		);
		affectedOpenBufferFailureMessage = "";
		unsafeOpenScriptPaths = lookupResult.UnsafeOpenScriptPaths;

		if (
			!TryMergeOpenEditorBuffers(
				openEditorsByPath,
				lookupResult.OpenEditorsByPath,
				out affectedOpenBufferFailureMessage
			)
		)
		{
			return false;
		}

		if (unsafeOpenScriptPaths.Count == 0)
			return true;

		HashSet<string> unsafeOpenScriptPathSet = new(
			unsafeOpenScriptPaths,
			StringComparer.OrdinalIgnoreCase
		);
		List<string> activationFallbackPaths = new();

		foreach (string pendingWritePath in writeSet.PendingWrites.Keys)
		{
			if (
				!unsafeOpenScriptPathSet.Contains(pendingWritePath)
				|| openEditorsByPath.ContainsKey(pendingWritePath)
				|| !_openBufferActivationService.IsScriptOpen(scriptEditor, pendingWritePath)
			)
			{
				continue;
			}

			activationFallbackPaths.Add(pendingWritePath);
		}

		if (activationFallbackPaths.Count == 0)
			return true;

		debugLog?.Invoke(
			$"Refactor Namespace apply could not match {activationFallbackPaths.Count} affected open buffer(s) without activation; activating only those buffers."
		);

		if (
			!_openBufferActivationService.TryGetOpenScriptEditorsByActivatingPaths(
				editorInterface,
				scriptEditor,
				activationFallbackPaths,
				failIfOpenEditorCannotBeMatched: true,
				debugLog: debugLog,
				openEditorsByPath: out Dictionary<string, OpenScriptEditorBuffer> activatedEditorsByPath,
				failureMessage: out affectedOpenBufferFailureMessage
			)
		)
		{
			return false;
		}

		return TryMergeOpenEditorBuffers(
			openEditorsByPath,
			activatedEditorsByPath,
			out affectedOpenBufferFailureMessage
		);
	}

	private static bool TryMergeOpenEditorBuffers(
		Dictionary<string, OpenScriptEditorBuffer> destination,
		IEnumerable<KeyValuePair<string, OpenScriptEditorBuffer>> source,
		out string failureMessage
	)
	{
		failureMessage = "";

		if (source == null)
			return true;

		foreach (KeyValuePair<string, OpenScriptEditorBuffer> sourcePair in source)
		{
			if (
				destination.TryGetValue(
					sourcePair.Key,
					out OpenScriptEditorBuffer existingPathMatch
				)
			)
			{
				if (ReferenceEquals(existingPathMatch.TextEditor, sourcePair.Value.TextEditor))
					continue;

				failureMessage =
					$"Refactor Namespace cancelled: System Explorer matched '{sourcePair.Key}' to multiple open text editor buffers before refactoring.";
				return false;
			}

			foreach (
				KeyValuePair<string, OpenScriptEditorBuffer> destinationPair in destination
			)
			{
				if (!ReferenceEquals(destinationPair.Value.TextEditor, sourcePair.Value.TextEditor))
					continue;

				failureMessage =
					$"Refactor Namespace cancelled: System Explorer matched the same open text editor buffer to both '{destinationPair.Key}' and '{sourcePair.Key}' before refactoring.";
				return false;
			}

			destination.Add(sourcePair.Key, sourcePair.Value);
		}

		return true;
	}

	private static NamespaceRefactorPendingWriteApplyFailure GetInitialMatchFailure(
		NamespaceRefactorAffectedOpenBufferMatchMode matchMode
	)
	{
		return matchMode == NamespaceRefactorAffectedOpenBufferMatchMode.NonActivatingOnly
			? NamespaceRefactorPendingWriteApplyFailure.UnsafeNonActivatingBufferMatch
			: NamespaceRefactorPendingWriteApplyFailure.AffectedOpenBufferMatchFailed;
	}
}
#endif
