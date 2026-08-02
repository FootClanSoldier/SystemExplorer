#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

public partial class SystemExplorerPlugin
{
	#region Shared Tree Operation Dialogs
	private enum TreeOperationOutcomeSeverity
	{
		Failed = 0,
		Incomplete = 1,
		FinalStateUnclear = 2,
	}

	private sealed class TreeOperationFailureReport
	{
		internal string UserMessage { get; set; } = "";
		internal TreeOperationOutcomeSeverity Severity { get; set; }
		internal HashSet<string> TechnicalDetails { get; } = new(StringComparer.Ordinal);
	}

	private sealed class TreeOperationDialogContext
	{
		internal string Title { get; }
		internal Action CloseOriginatingUi { get; set; }
		internal Action RestoreFocus { get; set; }
		internal string PersistentDeduplicationKey { get; }
		internal TreeOperationDialogContext Parent { get; }
		internal TreeOperationFailureReport Failure { get; } = new();
		internal bool HasFailure { get; set; }
		internal bool SuppressPresentation { get; set; }
		internal bool Disposed { get; set; }

		internal TreeOperationDialogContext(
			string title,
			Action closeOriginatingUi,
			Action restoreFocus,
			string persistentDeduplicationKey,
			TreeOperationDialogContext parent
		)
		{
			Title = string.IsNullOrWhiteSpace(title) ? "Operation Failed" : title.Trim();
			CloseOriginatingUi = closeOriginatingUi;
			RestoreFocus = restoreFocus;
			PersistentDeduplicationKey = persistentDeduplicationKey?.Trim() ?? "";
			Parent = parent;
		}
	}

	private sealed class DeferredTreeOperationDialogPresentation
	{
		internal string Title { get; }
		internal string UserMessage { get; }
		internal string PersistentDeduplicationKey { get; }

		internal DeferredTreeOperationDialogPresentation(
			string title,
			string userMessage,
			string persistentDeduplicationKey
		)
		{
			Title = string.IsNullOrWhiteSpace(title) ? "Operation Failed" : title.Trim();
			UserMessage = string.IsNullOrWhiteSpace(userMessage)
				? "System Explorer could not complete the operation."
				: userMessage.Trim();
			PersistentDeduplicationKey = persistentDeduplicationKey?.Trim() ?? "";
		}
	}

	private sealed class TreeOperationDialogScope : IDisposable
	{
		private SystemExplorerPlugin _owner;
		private TreeOperationDialogContext _context;

		internal TreeOperationDialogScope(
			SystemExplorerPlugin owner,
			TreeOperationDialogContext context
		)
		{
			_owner = owner;
			_context = context;
		}

		public void Dispose()
		{
			SystemExplorerPlugin owner = _owner;
			TreeOperationDialogContext context = _context;
			_owner = null;
			_context = null;
			owner?.EndTreeOperationDialogScope(context);
		}
	}

	private sealed class TreeOperationDialogPresentation
	{
		internal string Title { get; }
		internal string UserMessage { get; }
		internal string Fingerprint { get; }
		internal string PersistentDeduplicationKey { get; }

		internal TreeOperationDialogPresentation(
			string title,
			string userMessage,
			string persistentDeduplicationKey
		)
		{
			Title = string.IsNullOrWhiteSpace(title) ? "Operation Failed" : title.Trim();
			UserMessage = string.IsNullOrWhiteSpace(userMessage)
				? "System Explorer could not complete the operation."
				: userMessage.Trim();
			PersistentDeduplicationKey = persistentDeduplicationKey?.Trim() ?? "";
			Fingerprint = $"{Title}\n{UserMessage}";
		}
	}

	private const int WrappedAcceptDialogWidth = 480;
	private const int WrappedAcceptDialogMinimumHeight = 150;

	private static readonly Vector2I WrappedAcceptDialogMinimumSize =
		new(WrappedAcceptDialogWidth, WrappedAcceptDialogMinimumHeight);

	private AcceptDialog _treeOperationDialog;
	private TreeOperationDialogContext _activeTreeOperationDialogContext;
	private readonly Queue<TreeOperationDialogPresentation> _pendingTreeOperationPresentations = new();
	private readonly HashSet<string> _queuedTreeOperationPresentationFingerprints = new(StringComparer.Ordinal);
	private readonly Dictionary<string, string> _persistentTreeOperationFailureFingerprints = new(StringComparer.Ordinal);
	private string _visibleTreeOperationPresentationFingerprint = "";
	private bool _treeOperationDialogDeferredShowScheduled;
	private int _treeOperationDialogLifecycleGeneration;

	private void CreateTreeOperationDialog()
	{
		_treeOperationDialog = new AcceptDialog
		{
			Title = "Operation Failed",
			DialogText = "System Explorer could not complete the operation.",
			OkButtonText = "OK",
			MinSize = WrappedAcceptDialogMinimumSize,
			Unresizable = true,
			DialogAutowrap = true,
		};

		ConfigureWrappedAcceptDialogMessageLabel(_treeOperationDialog);
	}

	private static void ConfigureWrappedAcceptDialogMessageLabel(AcceptDialog dialog)
	{
		if (!IsValidGodotObject(dialog))
			return;

		Label messageLabel = dialog.GetLabel();

		if (!IsValidGodotObject(messageLabel))
			return;

		messageLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart;
	}

	private bool ConnectTreeOperationDialogSignals()
	{
		if (!IsValidGodotObject(_treeOperationDialog))
			return false;

		bool connected = true;
		connected &= TryConnectPluginSignal(
			_treeOperationDialog,
			AcceptDialog.SignalName.Confirmed,
			nameof(OnTreeOperationDialogClosed),
			nameof(_treeOperationDialog)
		);
		connected &= TryConnectPluginSignal(
			_treeOperationDialog,
			AcceptDialog.SignalName.Canceled,
			nameof(OnTreeOperationDialogClosed),
			nameof(_treeOperationDialog)
		);

		return connected;
	}

	private void DisconnectTreeOperationDialogSignals()
	{
		DisconnectPluginSignal(
			_treeOperationDialog,
			AcceptDialog.SignalName.Confirmed,
			nameof(OnTreeOperationDialogClosed),
			nameof(_treeOperationDialog)
		);
		DisconnectPluginSignal(
			_treeOperationDialog,
			AcceptDialog.SignalName.Canceled,
			nameof(OnTreeOperationDialogClosed),
			nameof(_treeOperationDialog)
		);
	}

	private TreeOperationDialogScope BeginTreeOperationDialogScope(
		string title,
		Action closeOriginatingUi = null,
		Action restoreFocus = null,
		string persistentDeduplicationKey = ""
	)
	{
		var context = new TreeOperationDialogContext(
			title,
			closeOriginatingUi,
			restoreFocus,
			persistentDeduplicationKey,
			_activeTreeOperationDialogContext
		);

		_activeTreeOperationDialogContext = context;
		return new TreeOperationDialogScope(this, context);
	}

	private void EndTreeOperationDialogScope(TreeOperationDialogContext context)
	{
		if (context == null || context.Disposed)
			return;

		context.Disposed = true;

		if (ReferenceEquals(_activeTreeOperationDialogContext, context))
			_activeTreeOperationDialogContext = context.Parent;

		RemoveDisposedTreeOperationContextsFromActiveChain();

		if (!context.HasFailure)
			return;

		if (context.Parent != null && !context.Parent.Disposed)
		{
			MergeTreeOperationFailure(context, context.Parent);
			return;
		}

		RunTreeOperationOriginCleanup(context);

		if (context.SuppressPresentation)
			return;

		QueueTreeOperationDialogPresentation(
			ResolveTreeOperationDialogTitle(context.Title, context.Failure.Severity),
			context.Failure.UserMessage,
			context.PersistentDeduplicationKey
		);
	}

	private static string ResolveTreeOperationDialogTitle(
		string requestedTitle,
		TreeOperationOutcomeSeverity severity
	)
	{
		string title = string.IsNullOrWhiteSpace(requestedTitle)
			? "Operation Failed"
			: requestedTitle.Trim();

		if (severity == TreeOperationOutcomeSeverity.Failed)
			return title;

		const string failedSuffix = " Failed";
		string operationTitle = title.EndsWith(failedSuffix, StringComparison.Ordinal)
			? title.Substring(0, title.Length - failedSuffix.Length)
			: title;

		return severity == TreeOperationOutcomeSeverity.Incomplete
			? $"{operationTitle} Incomplete"
			: $"{operationTitle} State Unclear";
	}

	private void RemoveDisposedTreeOperationContextsFromActiveChain()
	{
		TreeOperationDialogContext activeContext = _activeTreeOperationDialogContext;

		while (activeContext != null && activeContext.Disposed)
			activeContext = activeContext.Parent;

		_activeTreeOperationDialogContext = activeContext;
	}

	private static void MergeTreeOperationFailure(
		TreeOperationDialogContext source,
		TreeOperationDialogContext target
	)
	{
		if (!source.HasFailure)
			return;

		if (
			!target.HasFailure
			|| source.Failure.Severity >= target.Failure.Severity
		)
		{
			target.Failure.UserMessage = source.Failure.UserMessage;
			target.Failure.Severity = source.Failure.Severity;
		}

		foreach (string detail in source.Failure.TechnicalDetails)
			target.Failure.TechnicalDetails.Add(detail);

		target.HasFailure = true;
		target.CloseOriginatingUi ??= source.CloseOriginatingUi;
		target.RestoreFocus ??= source.RestoreFocus;
	}

	private void ReportTreeOperationFailure(
		string userMessage,
		string technicalDetails = "",
		TreeOperationOutcomeSeverity severity = TreeOperationOutcomeSeverity.Failed,
		bool replaceExistingReport = false
	)
	{
		severity = InferTreeOperationOutcomeSeverity(userMessage, severity);
		TreeOperationDialogContext context = _activeTreeOperationDialogContext;

		if (context == null)
		{
			QueueTreeOperationDialogPresentation("Operation Failed", userMessage, "");

			if (!string.IsNullOrWhiteSpace(technicalDetails))
				DebugLogger.LogOperation("Tree operation failure", technicalDetails);

			return;
		}

		if (
			replaceExistingReport
			|| !context.HasFailure
			|| severity >= context.Failure.Severity
		)
		{
			context.Failure.UserMessage = string.IsNullOrWhiteSpace(userMessage)
				? "System Explorer could not complete the operation."
				: userMessage.Trim();
			context.Failure.Severity = severity;
		}

		if (!string.IsNullOrWhiteSpace(technicalDetails))
		{
			context.Failure.TechnicalDetails.Add(technicalDetails.Trim());
			DebugLogger.LogOperation("Tree operation failure", technicalDetails.Trim());
		}

		context.HasFailure = true;
	}

	private static TreeOperationOutcomeSeverity InferTreeOperationOutcomeSeverity(
		string userMessage,
		TreeOperationOutcomeSeverity requestedSeverity
	)
	{
		if (requestedSeverity != TreeOperationOutcomeSeverity.Failed)
			return requestedSeverity;

		string normalizedMessage = userMessage?.ToLowerInvariant() ?? "";

		if (
			normalizedMessage.Contains("unclear")
			|| normalizedMessage.Contains("could not be verified")
			|| normalizedMessage.Contains("could not complete or roll back")
			|| normalizedMessage.Contains("may remain at the temporary")
			|| normalizedMessage.Contains("final state")
		)
		{
			return TreeOperationOutcomeSeverity.FinalStateUnclear;
		}

		if (
			normalizedMessage.Contains("was created, but")
			|| normalizedMessage.Contains("was renamed, but")
			|| normalizedMessage.Contains("was renamed successfully, but")
			|| normalizedMessage.Contains("completed the physical deletion, but")
			|| normalizedMessage.Contains("files were deleted, but")
		)
		{
			return TreeOperationOutcomeSeverity.Incomplete;
		}

		return requestedSeverity;
	}

	private void ReportTreeOperationFailureOrWarning(
		string userMessage,
		string technicalDetails = "",
		TreeOperationOutcomeSeverity severity = TreeOperationOutcomeSeverity.Failed
	)
	{
		if (_activeTreeOperationDialogContext != null)
		{
			ReportTreeOperationFailure(userMessage, technicalDetails, severity);
			return;
		}

		GD.PushWarning(userMessage);

		if (!string.IsNullOrWhiteSpace(technicalDetails))
			DebugLogger.LogOperation("Tree operation warning", technicalDetails);
	}

	private bool HasActiveTreeOperationFailure =>
		_activeTreeOperationDialogContext?.HasFailure == true;

	private bool IsActiveTreeOperationFinalStateUnclear =>
		_activeTreeOperationDialogContext?.HasFailure == true
		&& _activeTreeOperationDialogContext.Failure.Severity
			== TreeOperationOutcomeSeverity.FinalStateUnclear;

	private string GetActiveTreeOperationFailureUserMessage()
	{
		return _activeTreeOperationDialogContext?.Failure?.UserMessage ?? "";
	}

	private bool TryDeferActiveTreeOperationDialogPresentation(
		out DeferredTreeOperationDialogPresentation presentation
	)
	{
		presentation = null;
		TreeOperationDialogContext context = _activeTreeOperationDialogContext;

		if (context == null || !context.HasFailure)
			return false;

		presentation = new DeferredTreeOperationDialogPresentation(
			ResolveTreeOperationDialogTitle(context.Title, context.Failure.Severity),
			context.Failure.UserMessage,
			context.PersistentDeduplicationKey
		);
		context.SuppressPresentation = true;
		return true;
	}

	private void SuppressActiveTreeOperationDialogPresentation()
	{
		if (_activeTreeOperationDialogContext != null)
			_activeTreeOperationDialogContext.SuppressPresentation = true;
	}

	private void QueueStandaloneTreeOperationDialog(
		string title,
		string userMessage,
		string technicalDetails = "",
		string persistentDeduplicationKey = ""
	)
	{
		if (!string.IsNullOrWhiteSpace(technicalDetails))
			DebugLogger.LogOperation(title, technicalDetails);

		QueueTreeOperationDialogPresentation(
			title,
			userMessage,
			persistentDeduplicationKey
		);
	}

	private void QueueTreeOperationDialogPresentation(
		string title,
		string userMessage,
		string persistentDeduplicationKey
	)
	{
		var presentation = new TreeOperationDialogPresentation(
			title,
			userMessage,
			persistentDeduplicationKey
		);

		if (
			string.Equals(
				_visibleTreeOperationPresentationFingerprint,
				presentation.Fingerprint,
				StringComparison.Ordinal
			)
			|| _queuedTreeOperationPresentationFingerprints.Contains(presentation.Fingerprint)
		)
		{
			return;
		}

		if (
			!string.IsNullOrWhiteSpace(presentation.PersistentDeduplicationKey)
			&& _persistentTreeOperationFailureFingerprints.TryGetValue(
				presentation.PersistentDeduplicationKey,
				out string existingFingerprint
			)
			&& string.Equals(existingFingerprint, presentation.Fingerprint, StringComparison.Ordinal)
		)
		{
			return;
		}

		if (!string.IsNullOrWhiteSpace(presentation.PersistentDeduplicationKey))
		{
			_persistentTreeOperationFailureFingerprints[
				presentation.PersistentDeduplicationKey
			] = presentation.Fingerprint;
		}

		_pendingTreeOperationPresentations.Enqueue(presentation);
		_queuedTreeOperationPresentationFingerprints.Add(presentation.Fingerprint);
		SchedulePendingTreeOperationDialogPresentation();
	}

	private void ClearPersistentTreeOperationFailure(string persistentDeduplicationKey)
	{
		if (!string.IsNullOrWhiteSpace(persistentDeduplicationKey))
			_persistentTreeOperationFailureFingerprints.Remove(persistentDeduplicationKey.Trim());
	}

	private void SchedulePendingTreeOperationDialogPresentation()
	{
		if (
			_treeOperationDialogDeferredShowScheduled
			|| _pendingTreeOperationPresentations.Count == 0
		)
		{
			return;
		}

		_treeOperationDialogDeferredShowScheduled = true;
		int lifecycleGeneration = _treeOperationDialogLifecycleGeneration;

		Callable
			.From(() => TryShowPendingTreeOperationDialogDeferred(lifecycleGeneration))
			.CallDeferred();
	}

	private void TryShowPendingTreeOperationDialogDeferred(int lifecycleGeneration)
	{
		_treeOperationDialogDeferredShowScheduled = false;

		if (lifecycleGeneration != _treeOperationDialogLifecycleGeneration)
			return;

		if (_pendingTreeOperationPresentations.Count == 0)
			return;

		if (
			_treeOperationDialog == null
			|| !GodotObject.IsInstanceValid(_treeOperationDialog)
			|| _dock == null
			|| !GodotObject.IsInstanceValid(_dock)
			|| !_dock.IsInsideTree()
		)
		{
			return;
		}

		if (_treeOperationDialog.Visible)
			return;

		TreeOperationDialogPresentation presentation =
			_pendingTreeOperationPresentations.Dequeue();
		_queuedTreeOperationPresentationFingerprints.Remove(presentation.Fingerprint);
		_visibleTreeOperationPresentationFingerprint = presentation.Fingerprint;
		_treeOperationDialog.Title = presentation.Title;
		_treeOperationDialog.DialogText = presentation.UserMessage;
		PopupWrappedAcceptDialogForCurrentContent(_treeOperationDialog);
	}

	private static void PopupWrappedAcceptDialogForCurrentContent(AcceptDialog dialog)
	{
		if (!IsValidGodotObject(dialog))
			return;

		dialog.Size = WrappedAcceptDialogMinimumSize;
		dialog.ChildControlsChanged();
		dialog.ResetSize();

		Vector2I fittedSize = dialog.Size;
		fittedSize = new Vector2I(
			WrappedAcceptDialogWidth,
			Math.Max(fittedSize.Y, WrappedAcceptDialogMinimumHeight)
		);

		dialog.Size = fittedSize;
		dialog.PopupCentered(fittedSize);
	}

	private void OnTreeOperationDialogClosed()
	{
		_visibleTreeOperationPresentationFingerprint = "";
		SchedulePendingTreeOperationDialogPresentation();
	}

	private void RunTreeOperationOriginCleanup(TreeOperationDialogContext context)
	{
		try
		{
			context.CloseOriginatingUi?.Invoke();
		}
		catch (Exception exception)
		{
			DebugLogger.LogOperation(
				"Tree operation origin cleanup failed",
				exception.ToString()
			);
		}

		try
		{
			context.RestoreFocus?.Invoke();
		}
		catch (Exception exception)
		{
			DebugLogger.LogOperation(
				"Tree operation focus restore failed",
				exception.ToString()
			);
		}
	}

	private void ResetTreeOperationDialogQueuedStateAfterManagedAssemblyReload()
	{
		_treeOperationDialogLifecycleGeneration++;
		_treeOperationDialogDeferredShowScheduled = false;
		_activeTreeOperationDialogContext = null;
		_pendingTreeOperationPresentations.Clear();
		_queuedTreeOperationPresentationFingerprints.Clear();
		_visibleTreeOperationPresentationFingerprint = "";

		if (IsValidGodotObject(_treeOperationDialog))
			_treeOperationDialog.Hide();
	}

	private void ShutdownTreeOperationDialogs()
	{
		DisconnectTreeOperationDialogSignals();
		_treeOperationDialogLifecycleGeneration++;
		_treeOperationDialogDeferredShowScheduled = false;
		_activeTreeOperationDialogContext = null;
		_pendingTreeOperationPresentations.Clear();
		_queuedTreeOperationPresentationFingerprints.Clear();
		_persistentTreeOperationFailureFingerprints.Clear();
		_visibleTreeOperationPresentationFingerprint = "";

		if (
			_treeOperationDialog != null
			&& GodotObject.IsInstanceValid(_treeOperationDialog)
		)
		{
			_treeOperationDialog.Hide();
		}
	}

	private static void HideTreeOperationOriginWindow(Window window)
	{
		if (window != null && GodotObject.IsInstanceValid(window))
			window.Hide();
	}

	private void CloseAddFolderUiAfterFailure()
	{
		HideTreeOperationOriginWindow(_addFolderInputWarningDialog);
		HideTreeOperationOriginWindow(_addFolderDialog);
		_pendingAddFolderMetadata = "";

		if (_addFolderInput != null && GodotObject.IsInstanceValid(_addFolderInput))
			_addFolderInput.Text = "";

		_isAddFolderInputWarningPopupPending = false;
	}

	private void CloseRenameUiAfterFailure()
	{
		HideTreeOperationOriginWindow(_renameInputWarningDialog);
		HideTreeOperationOriginWindow(_renameDialog);
		_pendingRenameMetadata = "";
		_pendingScriptRenameTreeState = null;
		_pendingNonScriptRenameTreeSelectionState = null;

		if (_renameInput != null && GodotObject.IsInstanceValid(_renameInput))
			_renameInput.Text = "";

		_isRenameInputWarningPopupPending = false;
	}

	private void CloseRemoveUiAfterFailure()
	{
		HideTreeOperationOriginWindow(_removeDialog);
		_pendingRemoveMetadata = "";
		_pendingRemoveScriptOccurrence = null;
		_pendingRemoveTreeSelectionState = null;

		// A rebuilt tree may already have committed its deferred post-remove focus.
		// Preserve that target while this failure scope closes its originating dialog.
		if (!_pendingRemoveSelectionFocusCommitted)
			_pendingRemoveSelectionFocusTarget = RemoveSelectionFocusTarget.None;

		if (
			_removeFromFilesystemCheckBox != null
			&& GodotObject.IsInstanceValid(_removeFromFilesystemCheckBox)
		)
		{
			_removeFromFilesystemCheckBox.ButtonPressed = false;
		}
	}

	private void CloseAddScriptUiAfterFailure()
	{
		HideTreeOperationOriginWindow(_fileDialog);
	}

	private void CloseAddSceneUiAfterFailure()
	{
		HideTreeOperationOriginWindow(_addSceneDialog);
	}

	private void CloseCreateScriptUiAfterFailure()
	{
		HideTreeOperationOriginWindow(_createScriptDialog);
	}

	private void CloseFolderBindingUiAfterFailure()
	{
		HideTreeOperationOriginWindow(_folderBindingDialog);
		_pendingFolderBindingMetadata = "";
	}

	private void CloseLinkSceneUiAfterFailure()
	{
		HideTreeOperationOriginWindow(_linkSceneDialog);
		_pendingSceneLinkEntry = "";
		_pendingSceneLinkSourceOccurrence = null;
	}

	private void CloseMissingScriptRecoveryUiAfterFailure()
	{
		HideTreeOperationOriginWindow(_missingScriptDialog);
		HideTreeOperationOriginWindow(_relinkScriptDialog);
		ClearMissingScriptState();
	}

	private void CloseMissingSceneRecoveryUiAfterFailure()
	{
		HideTreeOperationOriginWindow(_missingSceneDialog);
		HideTreeOperationOriginWindow(_relinkSceneDialog);
		ClearMissingSceneState();
	}
	#endregion
}
#endif
