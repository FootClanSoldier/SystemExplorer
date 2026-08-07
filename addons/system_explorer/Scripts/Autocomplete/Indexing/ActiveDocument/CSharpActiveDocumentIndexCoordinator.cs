#if TOOLS
using System;
using System.Threading;
using System.Threading.Tasks;
using SystemExplorer.Autocomplete.Indexing;

namespace SystemExplorer.Autocomplete.Indexing.ActiveDocument;

internal sealed class CSharpActiveDocumentIndexCoordinator
{
	private readonly object _sync = new();
	private readonly AutocompleteIndexLifetime _lifetime;
	private readonly CSharpActiveDocumentIndex _index;
	private readonly CSharpActiveDocumentIndexWorker _worker;

	private Task _workerTask;
	private CancellationTokenSource _activeBuildCancellation;
	private CSharpActiveDocumentIndexRequest _pendingRequest;
	private CSharpActiveDocumentIndexBuildResult _latestReportableBuildResult;
	private long _latestRequestedRevision;
	private long _workerLoopId;
	private bool _workerRunning;
	private bool _stopAcceptingRequests;

	internal CSharpActiveDocumentIndexCoordinator(
		AutocompleteIndexLifetime lifetime,
		CSharpActiveDocumentIndex index,
		CSharpActiveDocumentIndexWorker worker
	)
	{
		_lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
		_index = index ?? throw new ArgumentNullException(nameof(index));
		_worker = worker ?? throw new ArgumentNullException(nameof(worker));
	}

	internal bool RequestIndex(CSharpActiveDocumentIndexRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		CancellationTokenSource activeBuildCancellation;

		lock (_sync)
		{
			if (
				_stopAcceptingRequests
				|| !_lifetime.IsActive
				|| request.Revision <= _latestRequestedRevision
			)
			{
				return false;
			}

			_latestRequestedRevision = request.Revision;
			_pendingRequest = request;
			_latestReportableBuildResult = null;
			activeBuildCancellation = _activeBuildCancellation;
			StartWorkerLoopLocked();
		}

		RequestCancellation(activeBuildCancellation);
		return true;
	}

	internal bool TryTakeLatestReportableBuildResult(
		out CSharpActiveDocumentIndexBuildResult result
	)
	{
		lock (_sync)
		{
			result = _latestReportableBuildResult;
			_latestReportableBuildResult = null;
			return result != null;
		}
	}

	internal void ResetTransientState(long invalidationRevision)
	{
		CancellationTokenSource activeBuildCancellation;

		lock (_sync)
		{
			if (_stopAcceptingRequests)
				return;

			if (invalidationRevision > _latestRequestedRevision)
				_latestRequestedRevision = invalidationRevision;

			_pendingRequest = null;
			_latestReportableBuildResult = null;
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
			_latestRequestedRevision = NextRevision(_latestRequestedRevision);
			_pendingRequest = null;
			_latestReportableBuildResult = null;
			activeBuildCancellation = _activeBuildCancellation;
		}

		RequestCancellation(activeBuildCancellation);
	}

	private void StartWorkerLoopLocked()
	{
		if (
			_workerRunning
			|| _pendingRequest == null
			|| _stopAcceptingRequests
			|| !_lifetime.TryBeginWorker(out CancellationToken shutdownToken)
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
			_lifetime.NotifyWorkerStopped();
		}
	}

	private void StartWorkerLoopIfPending()
	{
		lock (_sync)
			StartWorkerLoopLocked();
	}

	private void RunWorkerLoop(long workerLoopId, CancellationToken shutdownToken)
	{
		CSharpActiveDocumentIndexRequest activeRequest = null;

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
				CSharpActiveDocumentIndexBuildResult result;
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
			CSharpActiveDocumentIndexBuildResult failure = activeRequest != null
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

			_lifetime.NotifyWorkerStopped();
			StartWorkerLoopIfPending();
		}
	}

	private bool TryTakeNextRequest(
		long workerLoopId,
		CancellationToken shutdownToken,
		out CSharpActiveDocumentIndexRequest request,
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
				_workerRunning = false;
				_workerTask = null;
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
		CSharpActiveDocumentIndexRequest request,
		CSharpActiveDocumentIndexBuildResult result,
		bool buildCancellationRequested
	)
	{
		lock (_sync)
		{
			bool isLatestRevision = request.Revision == _latestRequestedRevision;
			bool hasNewerPendingRequest =
				_pendingRequest != null
				&& _pendingRequest.Revision > request.Revision;
			bool canPublish =
				!_stopAcceptingRequests
				&& !buildCancellationRequested
				&& isLatestRevision
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
					_latestReportableBuildResult = null;
					return;
				}
			}

			if (result.IsCancelled || buildCancellationRequested)
				return;

			if (result.IsSuccessful)
			{
				// Stale successful revisions are routine latest-request-wins control flow.
				return;
			}

			if (isLatestRevision && !hasNewerPendingRequest && result.IsFailed)
				_latestReportableBuildResult = result;
		}
	}

	private static long NextRevision(long revision)
	{
		unchecked
		{
			revision++;
			return revision == 0 ? 1 : revision;
		}
	}

	private static void RequestCancellation(CancellationTokenSource cancellation)
	{
		if (cancellation == null)
			return;

		try { cancellation.Cancel(); }
		catch (ObjectDisposedException) { }
	}

	private static CSharpActiveDocumentIndexBuildResult CreateCancelledResult(
		CSharpActiveDocumentIndexRequest request
	)
	{
		return new CSharpActiveDocumentIndexBuildResult(
			request.Revision,
			request.Reason,
			request.ScriptPath,
			CSharpActiveDocumentIndexBuildStatus.Cancelled,
			TimeSpan.Zero,
			0,
			0,
			"Build cancellation was requested.",
			snapshot: null
		);
	}

	private static CSharpActiveDocumentIndexBuildResult CreateUnexpectedFailureResult(
		CSharpActiveDocumentIndexRequest request,
		Exception exception
	)
	{
		return new CSharpActiveDocumentIndexBuildResult(
			request.Revision,
			request.Reason,
			request.ScriptPath,
			CSharpActiveDocumentIndexBuildStatus.Failed,
			TimeSpan.Zero,
			0,
			0,
			CreateExceptionDetail("Unexpected active-document worker failure", exception),
			snapshot: null
		);
	}

	private CSharpActiveDocumentIndexBuildResult CreateUnexpectedLoopFailureResult(
		Exception exception
	)
	{
		long revision;
		lock (_sync)
			revision = _latestRequestedRevision;

		return new CSharpActiveDocumentIndexBuildResult(
			revision,
			"Worker loop",
			"",
			CSharpActiveDocumentIndexBuildStatus.Failed,
			TimeSpan.Zero,
			0,
			0,
			CreateExceptionDetail("Unexpected active-document worker-loop failure", exception),
			snapshot: null
		);
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
