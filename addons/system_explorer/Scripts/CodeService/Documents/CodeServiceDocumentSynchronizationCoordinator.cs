#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SystemExplorer.CodeService.Client;

namespace SystemExplorer.CodeService.Documents;

internal sealed class CodeServiceDocumentSynchronizationCoordinator : IDisposable
{
	private enum CapabilityState
	{
		Unknown,
		Available,
		UnavailableForSession,
		RoslynSuspended,
		UnavailableSuspended,
		FailedClosed,
	}

	private sealed class TrackedDocument
	{
		internal TrackedDocument(string path)
		{
			Path = path;
			ClientVersion = 1;
		}

		internal string Path { get; }
		internal long ClientVersion { get; set; }
		internal bool Dirty { get; set; }
		internal bool HasCapturedSnapshot { get; set; }
		internal long CapturedClientVersion { get; set; }
		internal string LatestCapturedText { get; set; } = "";
		internal int LatestCapturedUtf8Bytes { get; set; }
		internal long LastServerAcceptedClientVersion { get; set; }
		internal bool NeedsReplayForCurrentServiceSession { get; set; }
		internal long BlockedClientVersion { get; set; }
		internal bool VersionConflict { get; set; }
		internal bool WaitForWorkspaceReady { get; set; }
	}

	private readonly object _gate = new();
	private readonly Dictionary<string, TrackedDocument> _documents = new(CodeServiceDocumentPath.PlatformComparer);
	private readonly CancellationTokenSource _lifetimeCancellation = new();
	private readonly long _clientGeneration;
	private readonly string _epochId;
	private CodeServiceClientSessionInfo _session;
	private bool _hasWorkspaceReady;
	private string _projectRoot = "";
	private List<string> _latestOpenPaths = new();
	private HashSet<string> _latestOpenSet = new(CodeServiceDocumentPath.PlatformComparer);
	private HashSet<string> _lastReconciledOpenSet = new(CodeServiceDocumentPath.PlatformComparer);
	private bool _epochNeedsReconcile = true;
	private CapabilityState _capability = CapabilityState.Unknown;
	private bool _activeFlight;
	private bool _disposed;
	private long _totalCachedSnapshotUtf8Bytes;

	internal CodeServiceDocumentSynchronizationCoordinator(long clientGeneration, string epochId)
	{
		if (clientGeneration <= 0)
			throw new ArgumentOutOfRangeException(nameof(clientGeneration));
		if (!CodeServiceDocumentClient.TryGetCanonicalGuid(epochId, out _))
			throw new ArgumentException("epochId must be a canonical lower-case GUID D string.", nameof(epochId));
		_clientGeneration = clientGeneration;
		_epochId = epochId;
	}

	internal long ClientGeneration => _clientGeneration;
	internal string EpochId => _epochId;

	internal bool IsFailedClosed
	{
		get { lock (_gate) return _disposed || _capability == CapabilityState.FailedClosed; }
	}

	internal bool IsDocumentSynchronizationUnavailableForCurrentSession
	{
		get { lock (_gate) return _capability == CapabilityState.UnavailableForSession; }
	}

	internal bool HasPendingWork
	{
		get { lock (_gate) return HasPendingWorkLocked(); }
	}

	internal bool IsFlightActive
	{
		get { lock (_gate) return _activeFlight; }
	}

	internal bool HasCaptureIntent
	{
		get
		{
			lock (_gate)
			{
				foreach (TrackedDocument document in _documents.Values)
				{
					if (_latestOpenSet.Contains(document.Path)
						&& !document.WaitForWorkspaceReady
						&& document.ClientVersion > document.BlockedClientVersion
						&& (document.Dirty || !document.HasCapturedSnapshot))
					{
						return true;
					}
				}
				return false;
			}
		}
	}

	internal bool TrySetWorkspaceReady(
		CodeServiceClientSessionInfo session,
		string normalizedProjectRoot,
		out bool logicalSessionChanged,
		out string detail
	)
	{
		logicalSessionChanged = false;
		detail = "";
		if (string.IsNullOrWhiteSpace(normalizedProjectRoot))
		{
			detail = "Workspace Ready project root is empty.";
			return false;
		}

		lock (_gate)
		{
			if (_disposed || _capability == CapabilityState.FailedClosed)
			{
				detail = "Document synchronization composition is closed.";
				return false;
			}

			bool hadSessionIdentity = !string.IsNullOrEmpty(_session.SessionId);
			logicalSessionChanged = !hadSessionIdentity || !_session.IsSameLogicalSession(session);
			_session = session;
			_projectRoot = normalizedProjectRoot;
			_hasWorkspaceReady = true;
			_epochNeedsReconcile = true;

			// A zero-body HTTP 503 marks the document feature unavailable for the exact
			// logical service session. Repeated Workspace Ready publications from that same
			// session must not clear the marker and restart document endpoint probing.
			if (logicalSessionChanged)
				_capability = CapabilityState.Unknown;
			else if (_capability is CapabilityState.RoslynSuspended or CapabilityState.UnavailableSuspended)
				_capability = CapabilityState.Unknown;

			if (logicalSessionChanged)
			{
				foreach (TrackedDocument document in _documents.Values)
				{
					document.NeedsReplayForCurrentServiceSession =
						document.HasCapturedSnapshot && _latestOpenSet.Contains(document.Path);
					document.WaitForWorkspaceReady = false;
					document.BlockedClientVersion = 0;
				}
			}
			else
			{
				// A same-session Workspace Ready publication may release only documents that
				// explicitly waited for workspace reconciliation. Preserve StaleVersion,
				// VersionConflict and capacity blocks until their own legitimate boundary.
				foreach (TrackedDocument document in _documents.Values)
				{
					if (!document.WaitForWorkspaceReady)
						continue;
					document.WaitForWorkspaceReady = false;
					document.BlockedClientVersion = 0;
				}
			}
			return true;
		}
	}


	internal void MarkExplicitCatchUpBoundary()
	{
		lock (_gate)
		{
			if (_capability is CapabilityState.RoslynSuspended or CapabilityState.UnavailableSuspended)
				_capability = CapabilityState.Unknown;
		}
	}

	internal bool TryUpdateOpenInventory(IReadOnlyList<string> paths, out string detail)
	{
		detail = "";
		if (paths == null)
		{
			detail = "Open C# document inventory is unavailable.";
			return false;
		}
		if (paths.Count > CodeServiceDocumentSynchronizationLimits.MaxTrackedOpenDocuments)
		{
			detail = $"Open C# document count {paths.Count} exceeds {CodeServiceDocumentSynchronizationLimits.MaxTrackedOpenDocuments}.";
			return false;
		}

		List<string> copy = new(paths.Count);
		HashSet<string> set = new(CodeServiceDocumentPath.PlatformComparer);
		foreach (string path in paths)
		{
			if (!CodeServiceDocumentPath.TryValidateWirePath(path, out string pathDetail))
			{
				detail = pathDetail;
				return false;
			}
			if (!set.Add(path))
			{
				detail = "Open C# document inventory contains duplicate platform-equivalent paths.";
				return false;
			}
			copy.Add(path);
		}

		lock (_gate)
		{
			if (_disposed || _capability == CapabilityState.FailedClosed)
			{
				detail = "Document synchronization composition is closed.";
				return false;
			}
			_latestOpenPaths = copy;
			_latestOpenSet = set;
			if (!_lastReconciledOpenSet.SetEquals(set))
				_epochNeedsReconcile = true;
			return true;
		}
	}

	internal bool TryEnsureTracked(string documentPath, out long clientVersion, out string detail)
	{
		lock (_gate)
			return TryEnsureTrackedLocked(documentPath, out _, out clientVersion, out detail);
	}

	internal bool TryGetCompletionAdmissionSnapshot(
		string documentPath,
		out CodeServiceDocumentCompletionAdmissionSnapshot snapshot,
		out string detail
	)
	{
		snapshot = default;
		detail = "";
		lock (_gate)
		{
			if (_disposed || _capability == CapabilityState.FailedClosed)
			{
				detail = "Document synchronization composition is closed.";
				return false;
			}
			if (!CodeServiceDocumentPath.TryValidateWirePath(documentPath, out detail))
				return false;
			if (!_documents.TryGetValue(documentPath, out TrackedDocument document))
			{
				detail = "Document is not tracked by the current document synchronization composition.";
				return false;
			}

			bool hasCurrentSession = !string.IsNullOrEmpty(_session.SessionId);
			bool synchronized =
				_hasWorkspaceReady
				&& hasCurrentSession
				&& _capability == CapabilityState.Available
				&& _latestOpenSet.Contains(document.Path)
				&& document.HasCapturedSnapshot
				&& document.CapturedClientVersion == document.ClientVersion
				&& document.LastServerAcceptedClientVersion == document.ClientVersion
				&& !document.NeedsReplayForCurrentServiceSession
				&& !document.WaitForWorkspaceReady
				&& !document.VersionConflict
				&& document.ClientVersion > document.BlockedClientVersion;

			snapshot = new CodeServiceDocumentCompletionAdmissionSnapshot(
				_clientGeneration,
				_epochId,
				document.Path,
				document.ClientVersion,
				document.LastServerAcceptedClientVersion,
				_session,
				synchronized
			);
			return true;
		}
	}

	internal bool TryRecordTextChanged(string documentPath, out long clientVersion, out string detail)
	{
		lock (_gate)
		{
			if (!TryEnsureTrackedLocked(documentPath, out TrackedDocument document, out _, out detail))
			{
				clientVersion = 0;
				return false;
			}
			try
			{
				document.ClientVersion = checked(document.ClientVersion + 1);
			}
			catch (OverflowException)
			{
				_capability = CapabilityState.FailedClosed;
				clientVersion = document.ClientVersion;
				detail = "Document clientVersion overflowed Int64; document synchronization is closed for this managed composition.";
				return false;
			}
			document.Dirty = true;
			document.VersionConflict = false;
			if (!document.WaitForWorkspaceReady
				&& document.ClientVersion > document.BlockedClientVersion)
			{
				document.BlockedClientVersion = 0;
			}
			clientVersion = document.ClientVersion;
			return true;
		}
	}

	internal bool ShouldCaptureOnBoundary(string documentPath)
	{
		lock (_gate)
		{
			return _documents.TryGetValue(documentPath ?? "", out TrackedDocument document)
				&& (document.Dirty || !document.HasCapturedSnapshot);
		}
	}

	internal bool TryCaptureSnapshot(
		string documentPath,
		string text,
		out CodeServiceDocumentSnapshot snapshot,
		out string detail
	)
	{
		snapshot = default;
		detail = "";
		if (text == null)
		{
			detail = "Document text is null.";
			return false;
		}

		int utf8Bytes;
		try
		{
			utf8Bytes = Encoding.UTF8.GetByteCount(text);
		}
		catch (Exception exception)
		{
			detail = "Document UTF-8 byte count failed: " + exception.Message;
			return false;
		}

		if (utf8Bytes > CodeServiceDocumentSynchronizationLimits.MaxDocumentTextUtf8Bytes)
		{
			detail = $"Document snapshot is {utf8Bytes} UTF-8 bytes; limit is {CodeServiceDocumentSynchronizationLimits.MaxDocumentTextUtf8Bytes}.";
			return false;
		}

		lock (_gate)
		{
			if (!TryEnsureTrackedLocked(documentPath, out TrackedDocument document, out _, out detail))
				return false;

			long nextTotal = _totalCachedSnapshotUtf8Bytes - document.LatestCapturedUtf8Bytes + utf8Bytes;
			if (nextTotal < 0)
			{
				_capability = CapabilityState.FailedClosed;
				detail = "Document snapshot cache byte accounting became negative.";
				return false;
			}
			if (nextTotal > CodeServiceDocumentSynchronizationLimits.MaxTotalTrackedSnapshotUtf8Bytes)
			{
				detail = $"Document snapshot cache would reach {nextTotal} UTF-8 bytes; limit is {CodeServiceDocumentSynchronizationLimits.MaxTotalTrackedSnapshotUtf8Bytes}.";
				return false;
			}

			_totalCachedSnapshotUtf8Bytes = nextTotal;
			document.LatestCapturedText = text;
			document.LatestCapturedUtf8Bytes = utf8Bytes;
			document.CapturedClientVersion = document.ClientVersion;
			document.HasCapturedSnapshot = true;
			document.Dirty = false;
			snapshot = new CodeServiceDocumentSnapshot(document.Path, document.CapturedClientVersion, text, utf8Bytes);
			return true;
		}
	}

	internal bool TryStartFlight(
		CodeServiceClientCoordinator clientCoordinator,
		string activeDocumentPath,
		out Task<CodeServiceDocumentFlightResult> flightTask,
		out string detail
	)
	{
		flightTask = null;
		detail = "";
		if (clientCoordinator == null)
		{
			detail = "CodeService client coordinator is unavailable.";
			return false;
		}

		CodeServiceDocumentFlightPlan plan;
		lock (_gate)
		{
			if (_disposed || _capability == CapabilityState.FailedClosed)
			{
				detail = "Document synchronization composition is closed.";
				return false;
			}
			if (_activeFlight)
			{
				detail = "A document synchronization flight is already active.";
				return false;
			}
			if (!_hasWorkspaceReady)
			{
				detail = "Workspace Ready has not been published for document synchronization.";
				return false;
			}
			if (_capability is CapabilityState.UnavailableForSession or CapabilityState.RoslynSuspended or CapabilityState.UnavailableSuspended)
			{
				detail = "Document synchronization is suspended for the current lifecycle state.";
				return false;
			}
			if (!HasPendingWorkLocked())
			{
				detail = "No document synchronization work is pending.";
				return false;
			}

			List<CodeServiceDocumentSnapshot> snapshots = BuildSnapshotPlanLocked(activeDocumentPath);
			plan = new CodeServiceDocumentFlightPlan(
				_session,
				_projectRoot,
				_clientGeneration,
				_epochId,
				_epochNeedsReconcile,
				_latestOpenPaths.ToArray(),
				snapshots
			);
			_activeFlight = true;
		}

		flightTask = RunFlightAsync(clientCoordinator, plan, _lifetimeCancellation.Token);
		return true;
	}

	private async Task<CodeServiceDocumentFlightResult> RunFlightAsync(
		CodeServiceClientCoordinator clientCoordinator,
		CodeServiceDocumentFlightPlan plan,
		CancellationToken cancellationToken
	)
	{
		CodeServiceDocumentOutcome terminalOutcome = CodeServiceDocumentOutcome.Success;
		bool retryQuiet = false;
		bool failedClosed = false;
		bool requestedRecovery = false;
		int sentSnapshots = 0;
		string detail = "";

		try
		{
			if (plan.ReconcileEpoch)
			{
				CodeServiceDocumentEpochResult epoch = await clientCoordinator.ReconcileDocumentEpochAsync(
					plan.Session,
					plan.ClientGeneration,
					plan.EpochId,
					plan.OpenDocumentPaths,
					cancellationToken
				).ConfigureAwait(false);
				terminalOutcome = epoch.Outcome;
				detail = epoch.Detail;

				if (epoch.IsAccepted)
				{
					CommitAcceptedEpoch(plan);
				}
				else if (!HandleEpochFailure(plan, epoch.Outcome, ref retryQuiet, ref failedClosed))
				{
					if (IsTransportFailure(epoch.Outcome))
						requestedRecovery = await RequestSessionRecoveryAsync(clientCoordinator, plan.Session, cancellationToken).ConfigureAwait(false);
					return CompleteFlight(plan, terminalOutcome, retryQuiet, failedClosed, requestedRecovery, sentSnapshots, detail);
				}
				else if (!epoch.IsAccepted)
				{
					return CompleteFlight(plan, terminalOutcome, retryQuiet, failedClosed, requestedRecovery, sentSnapshots, detail);
				}
			}

			foreach (CodeServiceDocumentSnapshot snapshot in plan.Snapshots)
			{
				cancellationToken.ThrowIfCancellationRequested();
				CodeServiceDocumentSnapshotResult result = await clientCoordinator.SynchronizeDocumentSnapshotAsync(
					plan.Session,
					plan.ClientGeneration,
					plan.EpochId,
					snapshot,
					cancellationToken
				).ConfigureAwait(false);
				sentSnapshots++;
				terminalOutcome = result.Outcome;
				detail = result.Detail;

				if (result.IsAccepted)
				{
					CommitAcceptedSnapshot(plan, snapshot, result);
					continue;
				}

				HandleSnapshotFailure(plan, snapshot, result, ref retryQuiet, ref failedClosed);
				if (IsTransportFailure(result.Outcome))
					requestedRecovery = await RequestSessionRecoveryAsync(clientCoordinator, plan.Session, cancellationToken).ConfigureAwait(false);
				return CompleteFlight(plan, terminalOutcome, retryQuiet, failedClosed, requestedRecovery, sentSnapshots, detail);
			}

			return CompleteFlight(plan, terminalOutcome, retryQuiet, failedClosed, requestedRecovery, sentSnapshots, detail);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			return CompleteFlight(plan, CodeServiceDocumentOutcome.Disposed, false, false, false, sentSnapshots, "Document synchronization flight was canceled by composition retirement.");
		}
		catch (Exception exception)
		{
			SuspendWorkspaceReadyForUnexpectedFlightFailure(plan);
			requestedRecovery = await RequestSessionRecoveryAsync(
				clientCoordinator,
				plan.Session,
				cancellationToken
			).ConfigureAwait(false);
			return CompleteFlight(
				plan,
				CodeServiceDocumentOutcome.TransportUnavailable,
				false,
				false,
				requestedRecovery,
				sentSnapshots,
				"Document synchronization flight failed: " + exception.Message
			);
		}
	}

	private void SuspendWorkspaceReadyForUnexpectedFlightFailure(
		CodeServiceDocumentFlightPlan plan
	)
	{
		lock (_gate)
		{
			if (IsPlanSessionCurrentLocked(plan))
				_hasWorkspaceReady = false;
		}
	}

	private bool HandleEpochFailure(
		CodeServiceDocumentFlightPlan plan,
		CodeServiceDocumentOutcome outcome,
		ref bool retryQuiet,
		ref bool failedClosed
	)
	{
		lock (_gate)
		{
			if (!IsPlanSessionCurrentLocked(plan))
				return false;
			switch (outcome)
			{
				case CodeServiceDocumentOutcome.Busy:
				case CodeServiceDocumentOutcome.WorkspaceUnavailable:
					retryQuiet = true;
					return true;
				case CodeServiceDocumentOutcome.RoslynUnavailable:
					_capability = CapabilityState.RoslynSuspended;
					return true;
				case CodeServiceDocumentOutcome.DocumentSynchronizationUnavailableForSession:
					_capability = CapabilityState.UnavailableForSession;
					return true;
				case CodeServiceDocumentOutcome.Unavailable:
					_capability = CapabilityState.UnavailableSuspended;
					return true;
				case CodeServiceDocumentOutcome.StaleEpoch:
				case CodeServiceDocumentOutcome.EpochConflict:
				case CodeServiceDocumentOutcome.InvalidRequest:
				case CodeServiceDocumentOutcome.VersionMismatch:
				case CodeServiceDocumentOutcome.CapacityExceeded:
				case CodeServiceDocumentOutcome.LocalInvalidRequest:
				case CodeServiceDocumentOutcome.LocalCapacityExceeded:
				case CodeServiceDocumentOutcome.MalformedResponse:
					_capability = CapabilityState.FailedClosed;
					failedClosed = true;
					return true;
				case CodeServiceDocumentOutcome.StaleSession:
				case CodeServiceDocumentOutcome.Disposed:
				case CodeServiceDocumentOutcome.AuthenticationFailed:
				case CodeServiceDocumentOutcome.TransportUnavailable:
					// The old Workspace Ready publication is no longer valid request authority.
					// Session recovery remains owned by CodeServiceClientCoordinator; document sends
					// resume only after a later exact Workspace Ready callback.
					_hasWorkspaceReady = false;
					return false;
				default:
					// An outcome that is impossible for the epoch endpoint is a contract
					// violation, not retryable flow control.
					_capability = CapabilityState.FailedClosed;
					failedClosed = true;
					return true;
			}
		}
	}

	private void HandleSnapshotFailure(
		CodeServiceDocumentFlightPlan plan,
		CodeServiceDocumentSnapshot snapshot,
		CodeServiceDocumentSnapshotResult result,
		ref bool retryQuiet,
		ref bool failedClosed
	)
	{
		lock (_gate)
		{
			if (!IsPlanSessionCurrentLocked(plan)
				|| !_documents.TryGetValue(snapshot.DocumentPath, out TrackedDocument document))
			{
				return;
			}

			switch (result.Outcome)
			{
				case CodeServiceDocumentOutcome.Busy:
				case CodeServiceDocumentOutcome.WorkspaceUnavailable:
					retryQuiet = true;
					break;
				case CodeServiceDocumentOutcome.RoslynUnavailable:
					_capability = CapabilityState.RoslynSuspended;
					break;
				case CodeServiceDocumentOutcome.DocumentSynchronizationUnavailableForSession:
					_capability = CapabilityState.UnavailableForSession;
					break;
				case CodeServiceDocumentOutcome.Unavailable:
					_capability = CapabilityState.UnavailableSuspended;
					break;
				case CodeServiceDocumentOutcome.StaleSession:
				case CodeServiceDocumentOutcome.Disposed:
				case CodeServiceDocumentOutcome.AuthenticationFailed:
				case CodeServiceDocumentOutcome.TransportUnavailable:
					_hasWorkspaceReady = false;
					break;
				case CodeServiceDocumentOutcome.StaleEpoch:
				case CodeServiceDocumentOutcome.EpochConflict:
				case CodeServiceDocumentOutcome.InvalidRequest:
				case CodeServiceDocumentOutcome.VersionMismatch:
				case CodeServiceDocumentOutcome.LocalInvalidRequest:
				case CodeServiceDocumentOutcome.MalformedResponse:
					_capability = CapabilityState.FailedClosed;
					failedClosed = true;
					break;
				case CodeServiceDocumentOutcome.StaleVersion:
				{
					long serverAcceptedVersion = result.AcceptedClientVersion ?? snapshot.ClientVersion;
					document.BlockedClientVersion = Math.Max(
						document.BlockedClientVersion,
						Math.Max(snapshot.ClientVersion, serverAcceptedVersion)
					);
					break;
				}
				case CodeServiceDocumentOutcome.VersionConflict:
					document.BlockedClientVersion = Math.Max(document.BlockedClientVersion, snapshot.ClientVersion);
					document.VersionConflict = true;
					document.Dirty = true;
					break;
				case CodeServiceDocumentOutcome.DocumentNotOpen:
					_epochNeedsReconcile = true;
					retryQuiet = true;
					break;
				case CodeServiceDocumentOutcome.DocumentNotInWorkspace:
					document.WaitForWorkspaceReady = true;
					document.BlockedClientVersion = Math.Max(document.BlockedClientVersion, snapshot.ClientVersion);
					break;
				case CodeServiceDocumentOutcome.CapacityExceeded:
				case CodeServiceDocumentOutcome.LocalCapacityExceeded:
					document.BlockedClientVersion = Math.Max(document.BlockedClientVersion, snapshot.ClientVersion);
					break;
			}
		}
	}

	private void CommitAcceptedEpoch(CodeServiceDocumentFlightPlan plan)
	{
		lock (_gate)
		{
			if (!IsPlanSessionCurrentLocked(plan))
				return;
			_capability = CapabilityState.Available;
			HashSet<string> sent = new(plan.OpenDocumentPaths, CodeServiceDocumentPath.PlatformComparer);
			_lastReconciledOpenSet = sent;
			_epochNeedsReconcile = !_latestOpenSet.SetEquals(sent);

			if (!_epochNeedsReconcile)
			{
				foreach (string path in _documents.Keys.ToArray())
				{
					if (!_latestOpenSet.Contains(path))
						RemoveDocumentLocked(path);
				}
			}
		}
	}

	private void CommitAcceptedSnapshot(
		CodeServiceDocumentFlightPlan plan,
		CodeServiceDocumentSnapshot snapshot,
		CodeServiceDocumentSnapshotResult result
	)
	{
		lock (_gate)
		{
			if (!IsPlanSessionCurrentLocked(plan)
				|| !_documents.TryGetValue(snapshot.DocumentPath, out TrackedDocument document))
				return;

			_capability = CapabilityState.Available;
			document.LastServerAcceptedClientVersion = Math.Max(document.LastServerAcceptedClientVersion, snapshot.ClientVersion);
			if (document.ClientVersion == snapshot.ClientVersion)
			{
				document.NeedsReplayForCurrentServiceSession = false;
				document.VersionConflict = false;
				if (document.BlockedClientVersion <= snapshot.ClientVersion)
					document.BlockedClientVersion = 0;
			}
		}
	}

	private CodeServiceDocumentFlightResult CompleteFlight(
		CodeServiceDocumentFlightPlan plan,
		CodeServiceDocumentOutcome outcome,
		bool retryQuiet,
		bool failedClosed,
		bool requestedRecovery,
		int sentSnapshots,
		string detail
	)
	{
		bool pending;
		lock (_gate)
		{
			_activeFlight = false;
			pending = HasPendingWorkLocked();
		}
		return new CodeServiceDocumentFlightResult(
			plan.Session.SessionId,
			plan.Session.ServiceProcessIdentity.ProcessId,
			plan.Session.ServiceProcessIdentity.StartTimeUtcTicks,
			outcome,
			pending,
			retryQuiet,
			failedClosed,
			requestedRecovery,
			sentSnapshots,
			detail ?? ""
		);
	}

	private async Task<bool> RequestSessionRecoveryAsync(
		CodeServiceClientCoordinator coordinator,
		CodeServiceClientSessionInfo failedSession,
		CancellationToken cancellationToken
	)
	{
		try
		{
			await coordinator.ReportSessionFailureAndEnsureReadyAsync(
				failedSession,
				"Document Synchronization Transport Failure",
				cancellationToken
			).ConfigureAwait(false);
			return true;
		}
		catch
		{
			return false;
		}
	}

	private List<CodeServiceDocumentSnapshot> BuildSnapshotPlanLocked(string activeDocumentPath)
	{
		List<CodeServiceDocumentSnapshot> replay = new();
		List<CodeServiceDocumentSnapshot> pendingNonActive = new();
		CodeServiceDocumentSnapshot? active = null;
		foreach (TrackedDocument document in _documents.Values)
		{
			if (!_latestOpenSet.Contains(document.Path)
				|| !document.HasCapturedSnapshot
				|| document.WaitForWorkspaceReady
				|| document.CapturedClientVersion != document.ClientVersion
				|| document.CapturedClientVersion <= document.BlockedClientVersion)
				continue;

			bool pending = document.NeedsReplayForCurrentServiceSession
				|| document.CapturedClientVersion > document.LastServerAcceptedClientVersion;
			if (!pending)
				continue;

			CodeServiceDocumentSnapshot snapshot = new(
				document.Path,
				document.CapturedClientVersion,
				document.LatestCapturedText,
				document.LatestCapturedUtf8Bytes
			);
			if (CodeServiceDocumentPath.Equals(document.Path, activeDocumentPath))
			{
				active = snapshot;
			}
			else if (document.NeedsReplayForCurrentServiceSession)
			{
				replay.Add(snapshot);
			}
			else
			{
				pendingNonActive.Add(snapshot);
			}
		}

		replay.Sort((left, right) => CodeServiceDocumentPath.PlatformComparer.Compare(left.DocumentPath, right.DocumentPath));
		pendingNonActive.Sort((left, right) => CodeServiceDocumentPath.PlatformComparer.Compare(left.DocumentPath, right.DocumentPath));
		replay.AddRange(pendingNonActive);
		if (active.HasValue)
			replay.Add(active.Value);
		return replay;
	}

	private bool HasPendingWorkLocked()
	{
		if (_disposed
			|| !_hasWorkspaceReady
			|| _capability is CapabilityState.FailedClosed
				or CapabilityState.UnavailableForSession
				or CapabilityState.RoslynSuspended
				or CapabilityState.UnavailableSuspended)
		{
			return false;
		}
		if (_epochNeedsReconcile)
			return true;
		foreach (TrackedDocument document in _documents.Values)
		{
			if (_latestOpenSet.Contains(document.Path)
				&& document.HasCapturedSnapshot
				&& !document.WaitForWorkspaceReady
				&& document.CapturedClientVersion == document.ClientVersion
				&& document.CapturedClientVersion > document.BlockedClientVersion
				&& (document.NeedsReplayForCurrentServiceSession || document.CapturedClientVersion > document.LastServerAcceptedClientVersion))
				return true;
		}
		return false;
	}

	private bool TryEnsureTrackedLocked(
		string documentPath,
		out TrackedDocument document,
		out long clientVersion,
		out string detail
	)
	{
		document = null;
		clientVersion = 0;
		detail = "";
		if (_disposed || _capability == CapabilityState.FailedClosed)
		{
			detail = "Document synchronization composition is closed.";
			return false;
		}
		if (!CodeServiceDocumentPath.TryValidateWirePath(documentPath, out detail))
			return false;
		if (!_documents.TryGetValue(documentPath, out document))
		{
			if (_documents.Count >= CodeServiceDocumentSynchronizationLimits.MaxTrackedOpenDocuments)
			{
				detail = $"Tracked document count exceeds {CodeServiceDocumentSynchronizationLimits.MaxTrackedOpenDocuments}.";
				return false;
			}
			document = new TrackedDocument(documentPath);
			_documents.Add(documentPath, document);
		}
		clientVersion = document.ClientVersion;
		return true;
	}

	private bool IsPlanSessionCurrentLocked(CodeServiceDocumentFlightPlan plan) =>
		_hasWorkspaceReady
		&& _clientGeneration == plan.ClientGeneration
		&& string.Equals(_epochId, plan.EpochId, StringComparison.Ordinal)
		&& CodeServiceWorkspacePath.EqualsNormalized(_projectRoot, plan.ProjectRoot)
		&& _session.IsSameLogicalSession(plan.Session);

	private void RemoveDocumentLocked(string path)
	{
		if (!_documents.Remove(path, out TrackedDocument document))
			return;
		_totalCachedSnapshotUtf8Bytes -= document.LatestCapturedUtf8Bytes;
		if (_totalCachedSnapshotUtf8Bytes < 0)
		{
			_totalCachedSnapshotUtf8Bytes = 0;
			_capability = CapabilityState.FailedClosed;
		}
	}

	private static bool IsTransportFailure(CodeServiceDocumentOutcome outcome) =>
		outcome is CodeServiceDocumentOutcome.AuthenticationFailed
			or CodeServiceDocumentOutcome.TransportUnavailable;

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed)
				return;
			_disposed = true;
			_hasWorkspaceReady = false;
			_capability = CapabilityState.FailedClosed;
		}
		try { _lifetimeCancellation.Cancel(); } catch { }
		_lifetimeCancellation.Dispose();
	}
}
#endif
