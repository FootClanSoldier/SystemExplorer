#if TOOLS
using System;
using System.Threading;
using System.Threading.Tasks;
using SystemExplorer.Autocomplete.Indexing;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.Autocomplete.Semantics;

internal sealed class CSharpSemanticMemberCoordinator
{
	private const string WorkerKind = "SemanticMember";

	private readonly object _sync = new();
	private readonly AutocompleteIndexLifetime _lifetime;
	private readonly CSharpSemanticMemberIndex _index;
	private readonly CSharpSemanticMemberWorker _worker;

	private Task _workerTask;
	private CancellationTokenSource _activeBuildCancellation;
	private CSharpSemanticMemberBuildRequest _pendingRequest;
	private CSharpSemanticMemberBuildResult _latestReportableBuildResult;
	private CSharpProjectIndexSnapshot _latestProjectSnapshot;
	private CSharpSemanticActiveDocumentRequest _latestActiveDocumentRequest;
	private long _projectStateVersion;
	private long _activeStateVersion;
	private long _latestRequestedActiveRevision;
	private long _workerLoopId;
	private bool _workerRunning;
	private bool _stopAcceptingRequests;

	internal CSharpSemanticMemberCoordinator(
		AutocompleteIndexLifetime lifetime,
		CSharpSemanticMemberIndex index,
		CSharpSemanticMemberWorker worker
	)
	{
		_lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
		_index = index ?? throw new ArgumentNullException(nameof(index));
		_worker = worker ?? throw new ArgumentNullException(nameof(worker));
	}

	internal bool RequestProjectSnapshot(CSharpProjectIndexSnapshot projectSnapshot)
	{
		if (
			projectSnapshot == null
			|| !projectSnapshot.HasBuiltAtLeastOnce
			|| projectSnapshot.Generation <= 0
		)
		{
			return false;
		}

		CancellationTokenSource activeBuildCancellation;

		lock (_sync)
		{
			if (_stopAcceptingRequests || !_lifetime.IsActive)
				return false;

			if (
				_latestProjectSnapshot != null
				&& _latestProjectSnapshot.Generation == projectSnapshot.Generation
			)
			{
				return false;
			}

			_latestProjectSnapshot = projectSnapshot;
			_projectStateVersion = NextVersion(_projectStateVersion);
			_latestReportableBuildResult = null;
			_index.Clear();
			_pendingRequest = CreateCurrentBuildRequestLocked();
			activeBuildCancellation = _activeBuildCancellation;
			StartWorkerLoopLocked();
		}

		RequestCancellation(activeBuildCancellation);
		return true;
	}

	internal bool RequestActiveDocument(CSharpSemanticActiveDocumentRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		string scriptPath = ScriptPathUtility.Normalize(request.ScriptPath);
		if (!IsCSharpScriptPath(scriptPath) || request.Revision <= 0)
			return false;

		var normalizedRequest = new CSharpSemanticActiveDocumentRequest(
			request.Revision,
			NormalizeReason(request.Reason),
			scriptPath,
			request.SourceText ?? ""
		);
		CancellationTokenSource activeBuildCancellation;
		bool shouldCancelActiveBuild;

		lock (_sync)
		{
			if (
				_stopAcceptingRequests
				|| !_lifetime.IsActive
				|| normalizedRequest.Revision <= _latestRequestedActiveRevision
			)
			{
				return false;
			}

			_latestRequestedActiveRevision = normalizedRequest.Revision;
			_latestActiveDocumentRequest = normalizedRequest;
			_activeStateVersion = NextVersion(_activeStateVersion);
			_latestReportableBuildResult = null;
			_index.Clear();
			_pendingRequest = CreateCurrentBuildRequestLocked();
			activeBuildCancellation = _activeBuildCancellation;
			shouldCancelActiveBuild =
				activeBuildCancellation != null
				&& _latestProjectSnapshot != null
				&& _worker.HasBaseFor(
					_projectStateVersion,
					_latestProjectSnapshot.Generation
				);
			StartWorkerLoopLocked();
		}

		if (shouldCancelActiveBuild)
			RequestCancellation(activeBuildCancellation);
		return true;
	}

	internal void ResetActiveDocument()
	{
		CancellationTokenSource activeBuildCancellation;
		bool shouldCancelActiveBuild;

		lock (_sync)
		{
			if (_stopAcceptingRequests)
				return;

			_latestActiveDocumentRequest = null;
			_activeStateVersion = NextVersion(_activeStateVersion);
			_latestReportableBuildResult = null;
			_pendingRequest = null;
			_index.Clear();
			activeBuildCancellation = _activeBuildCancellation;
			shouldCancelActiveBuild =
				activeBuildCancellation != null
				&& _latestProjectSnapshot != null
				&& _worker.HasBaseFor(
					_projectStateVersion,
					_latestProjectSnapshot.Generation
				);
		}

		if (shouldCancelActiveBuild)
			RequestCancellation(activeBuildCancellation);
	}

	internal bool TryTakeLatestReportableBuildResult(
		out CSharpSemanticMemberBuildResult result
	)
	{
		lock (_sync)
		{
			result = _latestReportableBuildResult;
			_latestReportableBuildResult = null;
			return result != null;
		}
	}

	internal void ResetTransientState()
	{
		CancellationTokenSource activeBuildCancellation;

		lock (_sync)
		{
			if (_stopAcceptingRequests)
				return;

			_projectStateVersion = NextVersion(_projectStateVersion);
			_activeStateVersion = NextVersion(_activeStateVersion);
			_latestProjectSnapshot = null;
			_latestActiveDocumentRequest = null;
			_latestRequestedActiveRevision = 0;
			_pendingRequest = null;
			_latestReportableBuildResult = null;
			_index.Clear();
			activeBuildCancellation = _activeBuildCancellation;
		}

		RequestCancellation(activeBuildCancellation);
	}

	internal void StopAcceptingRequests()
	{
		lock (_sync)
			_stopAcceptingRequests = true;
	}

	internal void Shutdown()
	{
		CancellationTokenSource activeBuildCancellation;

		lock (_sync)
		{
			_stopAcceptingRequests = true;
			_projectStateVersion = NextVersion(_projectStateVersion);
			_activeStateVersion = NextVersion(_activeStateVersion);
			_latestProjectSnapshot = null;
			_latestActiveDocumentRequest = null;
			_pendingRequest = null;
			_latestReportableBuildResult = null;
			_index.Clear();
			activeBuildCancellation = _activeBuildCancellation;
		}

		RequestCancellation(activeBuildCancellation);
	}

	private CSharpSemanticMemberBuildRequest CreateCurrentBuildRequestLocked()
	{
		if (_latestProjectSnapshot == null)
			return null;

		return new CSharpSemanticMemberBuildRequest(
			_projectStateVersion,
			_activeStateVersion,
			_latestProjectSnapshot,
			_latestActiveDocumentRequest
		);
	}

	private void StartWorkerLoopLocked()
	{
		if (
			_workerRunning
			|| _pendingRequest == null
			|| _stopAcceptingRequests
			|| !_lifetime.TryBeginWorker(WorkerKind, out CancellationToken shutdownToken)
		)
		{
			return;
		}

		_workerRunning = true;
		long workerLoopId = ++_workerLoopId;

		try
		{
			_workerTask = Task.Run(() => RunWorkerLoop(workerLoopId, shutdownToken));
		}
		catch (Exception exception)
		{
			_workerRunning = false;
			_workerTask = null;
			_pendingRequest = null;
			_latestReportableBuildResult = CreateUnexpectedLoopFailureResult(exception);
			_lifetime.NotifyWorkerStopped(WorkerKind);
		}
	}

	private void StartWorkerLoopIfPending()
	{
		lock (_sync)
			StartWorkerLoopLocked();
	}

	private void RunWorkerLoop(long workerLoopId, CancellationToken shutdownToken)
	{
		CSharpSemanticMemberBuildRequest activeRequest = null;

		try
		{
			while (
				TryTakeNextRequest(
					workerLoopId,
					shutdownToken,
					out activeRequest,
					out CancellationTokenSource buildCancellation,
					out CancellationTokenSource linkedCancellation
				)
			)
			{
				CSharpSemanticMemberBuildResult result;
				bool buildCancellationRequested;

				try
				{
					result = _worker.Build(activeRequest, linkedCancellation.Token);
				}
				catch (OperationCanceledException) when (
					linkedCancellation.IsCancellationRequested
				)
				{
					result = CreateCancelledResult(activeRequest);
				}
				catch (Exception exception)
				{
					result = CreateUnexpectedFailureResult(activeRequest, exception);
				}
				finally
				{
					buildCancellationRequested = linkedCancellation.IsCancellationRequested;

					lock (_sync)
					{
						if (ReferenceEquals(_activeBuildCancellation, buildCancellation))
							_activeBuildCancellation = null;
					}

					linkedCancellation.Dispose();
					buildCancellation.Dispose();
				}

				CompleteBuild(activeRequest, result, buildCancellationRequested);
				activeRequest = null;
			}
		}
		catch (Exception exception)
		{
			CSharpSemanticMemberBuildResult failure = activeRequest != null
				? CreateUnexpectedFailureResult(activeRequest, exception)
				: CreateUnexpectedLoopFailureResult(exception);

			lock (_sync)
				_latestReportableBuildResult = failure;
		}
		finally
		{
			lock (_sync)
			{
				if (_workerLoopId == workerLoopId)
				{
					_workerRunning = false;
					_workerTask = null;
				}
			}

			_lifetime.NotifyWorkerStopped(WorkerKind);
			StartWorkerLoopIfPending();
		}
	}

	private bool TryTakeNextRequest(
		long workerLoopId,
		CancellationToken shutdownToken,
		out CSharpSemanticMemberBuildRequest request,
		out CancellationTokenSource buildCancellation,
		out CancellationTokenSource linkedCancellation
	)
	{
		lock (_sync)
		{
			request = null;
			buildCancellation = null;
			linkedCancellation = null;

			if (_workerLoopId != workerLoopId)
				return false;

			if (
				_stopAcceptingRequests
				|| shutdownToken.IsCancellationRequested
				|| !_lifetime.IsActive
				|| _pendingRequest == null
			)
			{
				// Keep this stateful semantic worker loop marked active until finally.
				// A request arriving during loop teardown is left pending and starts
				// only after NotifyWorkerStopped(), preventing overlapping access
				// to the worker-owned base compilation cache.
				return false;
			}

			request = _pendingRequest;
			_pendingRequest = null;
			buildCancellation = new CancellationTokenSource();
			linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
				shutdownToken,
				buildCancellation.Token
			);
			_activeBuildCancellation = buildCancellation;
			return true;
		}
	}

	private void CompleteBuild(
		CSharpSemanticMemberBuildRequest request,
		CSharpSemanticMemberBuildResult result,
		bool buildCancellationRequested
	)
	{
		lock (_sync)
		{
			bool projectStateCurrent =
				request.ProjectStateVersion == _projectStateVersion
				&& _latestProjectSnapshot != null
				&& request.ProjectSnapshot.Generation == _latestProjectSnapshot.Generation;
			bool activeStateCurrent = request.ActiveStateVersion == _activeStateVersion;
			bool activeRevisionCurrent =
				request.ActiveDocument == null
					? _latestActiveDocumentRequest == null
					: _latestActiveDocumentRequest != null
						&& request.ActiveDocument.Revision
							== _latestActiveDocumentRequest.Revision
						&& string.Equals(
							request.ActiveDocument.ScriptPath,
							_latestActiveDocumentRequest.ScriptPath,
							StringComparison.OrdinalIgnoreCase
						);
			bool hasNewerPendingRequest = _pendingRequest != null;
			bool canPublish =
				!_stopAcceptingRequests
				&& !buildCancellationRequested
				&& projectStateCurrent
				&& activeStateCurrent
				&& activeRevisionCurrent
				&& !hasNewerPendingRequest
				&& result.IsSuccessful
				&& result.Snapshot != null;

			if (canPublish)
			{
				bool published = _lifetime.TryRunWhileActive(
					() => _index.Publish(result.Snapshot)
				);

				if (published)
				{
					RecordDiagnosticsIfNeededLocked(result);
					return;
				}
			}

			if (result.IsCancelled || buildCancellationRequested)
				return;

			if (result.IsSuccessful)
			{
				// Stale active revisions are routine latest-request-wins control flow.
				if (projectStateCurrent)
					RecordDiagnosticsIfNeededLocked(result);
				return;
			}

			if (
				projectStateCurrent
				&& activeStateCurrent
				&& activeRevisionCurrent
				&& !hasNewerPendingRequest
				&& result.IsFailed
			)
			{
				_latestReportableBuildResult = result;
			}
		}
	}

	private void RecordDiagnosticsIfNeededLocked(CSharpSemanticMemberBuildResult result)
	{
		if (result.BaseCompilationBuilt)
			_latestReportableBuildResult = result;
	}

	private static long NextVersion(long version)
	{
		unchecked
		{
			version++;
			return version == 0 ? 1 : version;
		}
	}

	private static void RequestCancellation(CancellationTokenSource cancellation)
	{
		if (cancellation == null)
			return;

		try { cancellation.Cancel(); }
		catch (ObjectDisposedException) { }
	}

	private static CSharpSemanticMemberBuildResult CreateCancelledResult(
		CSharpSemanticMemberBuildRequest request
	)
	{
		return new CSharpSemanticMemberBuildResult(
			request.ProjectSnapshot?.Generation ?? 0,
			request.ActiveDocument?.Revision ?? 0,
			request.ActiveDocument?.ScriptPath ?? "",
			CSharpSemanticMemberBuildStatus.Cancelled,
			TimeSpan.Zero,
			0,
			0,
			baseCompilationBuilt: false,
			metadataReferenceFailureCount: 0,
			projectFingerprintMismatchCount: 0,
			diagnosticDetail: "",
			failureDetail: "Build cancellation was requested.",
			snapshot: null
		);
	}

	private static CSharpSemanticMemberBuildResult CreateUnexpectedFailureResult(
		CSharpSemanticMemberBuildRequest request,
		Exception exception
	)
	{
		return new CSharpSemanticMemberBuildResult(
			request.ProjectSnapshot?.Generation ?? 0,
			request.ActiveDocument?.Revision ?? 0,
			request.ActiveDocument?.ScriptPath ?? "",
			CSharpSemanticMemberBuildStatus.Failed,
			TimeSpan.Zero,
			0,
			0,
			baseCompilationBuilt: false,
			metadataReferenceFailureCount: 0,
			projectFingerprintMismatchCount: 0,
			diagnosticDetail: "",
			failureDetail: CreateExceptionDetail(
				"Unexpected semantic member worker failure",
				exception
			),
			snapshot: null
		);
	}

	private CSharpSemanticMemberBuildResult CreateUnexpectedLoopFailureResult(
		Exception exception
	)
	{
		long projectGeneration;
		long activeRevision;
		string scriptPath;

		lock (_sync)
		{
			projectGeneration = _latestProjectSnapshot?.Generation ?? 0;
			activeRevision = _latestActiveDocumentRequest?.Revision ?? 0;
			scriptPath = _latestActiveDocumentRequest?.ScriptPath ?? "";
		}

		return new CSharpSemanticMemberBuildResult(
			projectGeneration,
			activeRevision,
			scriptPath,
			CSharpSemanticMemberBuildStatus.Failed,
			TimeSpan.Zero,
			0,
			0,
			baseCompilationBuilt: false,
			metadataReferenceFailureCount: 0,
			projectFingerprintMismatchCount: 0,
			diagnosticDetail: "",
			failureDetail: CreateExceptionDetail(
				"Unexpected semantic member worker-loop failure",
				exception
			),
			snapshot: null
		);
	}

	private static string NormalizeReason(string reason)
	{
		if (string.IsNullOrWhiteSpace(reason))
			return "Active document semantic capture";

		string normalized = reason.Trim();
		return normalized.Length <= 160 ? normalized : normalized.Substring(0, 160);
	}

	private static bool IsCSharpScriptPath(string scriptPath)
	{
		return !string.IsNullOrWhiteSpace(scriptPath)
			&& scriptPath.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
			&& scriptPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
	}

	private static string CreateExceptionDetail(string prefix, Exception exception)
	{
		string message = exception?.Message ?? "Unknown error.";
		message = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
		if (message.Length > 500)
			message = message.Substring(0, 500);

		return $"{prefix}: {exception?.GetType().Name ?? "Exception"}: {message}";
	}
}
#endif
