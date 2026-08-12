#if TOOLS
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SystemExplorer.Autocomplete.Indexing.Persistence;

internal sealed class CSharpProjectIndexCacheCoordinator
{
	private const string WorkerKind = "ProjectIndexCache";

	private readonly object _sync = new();
	private readonly object _commitGate = new();
	private readonly AutocompleteIndexLifetime _lifetime;
	private readonly CSharpProjectIndexPersistentCacheStore _store;

	private Task _workerTask;
	private CancellationTokenSource _activeWriteCancellation;
	private CSharpProjectIndexCacheWriteRequest _pendingRequest;
	private CSharpProjectIndexCacheWriteResult _latestReportableWriteResult;
	private long _latestRequestedGeneration;
	private long _latestPublicationGeneration;
	private long _workerLoopId;
	private bool _workerRunning;
	private bool _stopAcceptingRequests;

	internal CSharpProjectIndexCacheCoordinator(
		AutocompleteIndexLifetime lifetime,
		CSharpProjectIndexPersistentCacheStore store
	)
	{
		_lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
		_store = store ?? throw new ArgumentNullException(nameof(store));
	}

	internal void MarkGenerationForPublication(long generation)
	{
		if (generation <= 0)
			return;

		CancellationTokenSource activeWriteCancellation = null;

		lock (_commitGate)
		{
			lock (_sync)
			{
				if (_stopAcceptingRequests || generation <= _latestPublicationGeneration)
					return;

				_latestPublicationGeneration = generation;

				if (_pendingRequest != null && _pendingRequest.Generation < generation)
					_pendingRequest = null;

				activeWriteCancellation = _activeWriteCancellation;
			}
		}

		RequestCancellation(activeWriteCancellation);
	}

	internal bool RequestWrite(CSharpProjectIndexCacheWriteRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		CancellationTokenSource activeWriteCancellation;

		lock (_sync)
		{
			if (
				_stopAcceptingRequests
				|| !_lifetime.IsActive
				|| request.Generation != _latestPublicationGeneration
				|| request.Generation <= _latestRequestedGeneration
				|| request.Snapshot == null
			)
			{
				return false;
			}

			_latestRequestedGeneration = request.Generation;
			_pendingRequest = request;
			_latestReportableWriteResult = null;
			activeWriteCancellation = _activeWriteCancellation;
			StartWorkerLoopLocked();
		}

		RequestCancellation(activeWriteCancellation);
		return true;
	}

	internal bool TryTakeLatestReportableWriteResult(
		out CSharpProjectIndexCacheWriteResult result
	)
	{
		lock (_sync)
		{
			result = _latestReportableWriteResult;
			_latestReportableWriteResult = null;
			return result != null;
		}
	}

	internal void ResetTransientState()
	{
		CancellationTokenSource activeWriteCancellation;

		lock (_sync)
		{
			if (_stopAcceptingRequests)
				return;

			_latestRequestedGeneration = NextGeneration(_latestRequestedGeneration);
			_pendingRequest = null;
			_latestReportableWriteResult = null;
			activeWriteCancellation = _activeWriteCancellation;
		}

		RequestCancellation(activeWriteCancellation);
	}

	internal void StopAcceptingRequests()
	{
		lock (_sync)
			_stopAcceptingRequests = true;
	}

	internal void Shutdown()
	{
		CancellationTokenSource activeWriteCancellation;

		lock (_sync)
		{
			_stopAcceptingRequests = true;
			_latestRequestedGeneration = NextGeneration(_latestRequestedGeneration);
			_pendingRequest = null;
			_latestReportableWriteResult = null;
			activeWriteCancellation = _activeWriteCancellation;
		}

		RequestCancellation(activeWriteCancellation);
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
			_latestReportableWriteResult = CreateUnexpectedLoopFailureResult(exception);
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
		CSharpProjectIndexCacheWriteRequest activeRequest = null;

		try
		{
			while (
				TryTakeNextRequest(
					workerLoopId,
					shutdownToken,
					out activeRequest,
					out CancellationTokenSource writeCancellation,
					out CancellationTokenSource linkedCancellation
				)
			)
			{
				var stopwatch = Stopwatch.StartNew();
				CSharpProjectIndexCachePreparedWrite preparedWrite = null;
				CSharpProjectIndexCacheWriteResult result;
				bool writeCancellationRequested;

				try
				{
					preparedWrite = _store.PrepareWrite(
						activeRequest,
						linkedCancellation.Token
					);
					linkedCancellation.Token.ThrowIfCancellationRequested();
					result = CommitPreparedWriteIfCurrent(
						activeRequest,
						preparedWrite,
						linkedCancellation.Token,
						stopwatch
					);
				}
				catch (OperationCanceledException) when (
					linkedCancellation.IsCancellationRequested
				)
				{
					result = CreateCancelledResult(activeRequest, stopwatch.Elapsed);
				}
				catch (Exception exception)
				{
					result = CreateUnexpectedFailureResult(
						activeRequest,
						exception,
						stopwatch.Elapsed
					);
				}
				finally
				{
					if (stopwatch.IsRunning)
						stopwatch.Stop();

					if (preparedWrite != null)
						_store.DiscardPreparedWrite(preparedWrite);

					writeCancellationRequested = linkedCancellation.IsCancellationRequested;

					lock (_sync)
					{
						if (ReferenceEquals(_activeWriteCancellation, writeCancellation))
							_activeWriteCancellation = null;
					}

					linkedCancellation.Dispose();
					writeCancellation.Dispose();
				}

				CompleteWrite(activeRequest, result, writeCancellationRequested);
				activeRequest = null;
			}
		}
		catch (Exception exception)
		{
			CSharpProjectIndexCacheWriteResult failure = activeRequest != null
				? CreateUnexpectedFailureResult(activeRequest, exception, TimeSpan.Zero)
				: CreateUnexpectedLoopFailureResult(exception);

			lock (_sync)
				_latestReportableWriteResult = failure;
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
		out CSharpProjectIndexCacheWriteRequest request,
		out CancellationTokenSource writeCancellation,
		out CancellationTokenSource linkedCancellation
	)
	{
		lock (_sync)
		{
			request = null;
			writeCancellation = null;
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
			writeCancellation = new CancellationTokenSource();
			linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
				shutdownToken,
				writeCancellation.Token
			);
			_activeWriteCancellation = writeCancellation;
			return true;
		}
	}

	private CSharpProjectIndexCacheWriteResult CommitPreparedWriteIfCurrent(
		CSharpProjectIndexCacheWriteRequest request,
		CSharpProjectIndexCachePreparedWrite preparedWrite,
		CancellationToken cancellationToken,
		Stopwatch stopwatch
	)
	{
		lock (_commitGate)
		{
			if (!_lifetime.IsActive)
				return CreateCancelledResult(request, stopwatch.Elapsed);

			if (
				TryGetCommitDenialStatus(
					request,
					cancellationToken,
					out CSharpProjectIndexCacheWriteStatus denialStatus
				)
			)
			{
				return CreateNonCommittedResult(request, denialStatus, stopwatch.Elapsed);
			}

			bool committed = _lifetime.TryRunWhileActive(
				() => _store.CommitPreparedWrite(preparedWrite)
			);

			if (!committed)
				return CreateCancelledResult(request, stopwatch.Elapsed);

			return new CSharpProjectIndexCacheWriteResult(
				request.Generation,
				CSharpProjectIndexCacheWriteStatus.Succeeded,
				stopwatch.Elapsed,
				preparedWrite.FileCount,
				preparedWrite.TypeCount,
				failureDetail: ""
			);
		}
	}


	private bool TryGetCommitDenialStatus(
		CSharpProjectIndexCacheWriteRequest request,
		CancellationToken cancellationToken,
		out CSharpProjectIndexCacheWriteStatus denialStatus
	)
	{
		lock (_sync)
		{
			if (_stopAcceptingRequests)
			{
				denialStatus = CSharpProjectIndexCacheWriteStatus.Cancelled;
				return true;
			}

			bool isLatestPublication =
				request.Generation == _latestPublicationGeneration;
			bool isLatestGeneration = request.Generation == _latestRequestedGeneration;
			bool hasNewerPendingRequest =
				_pendingRequest != null
				&& _pendingRequest.Generation > request.Generation;

			if (!isLatestPublication || !isLatestGeneration || hasNewerPendingRequest)
			{
				denialStatus = CSharpProjectIndexCacheWriteStatus.Stale;
				return true;
			}

			if (cancellationToken.IsCancellationRequested)
			{
				denialStatus = CSharpProjectIndexCacheWriteStatus.Cancelled;
				return true;
			}
		}

		denialStatus = CSharpProjectIndexCacheWriteStatus.Succeeded;
		return false;
	}


	private void CompleteWrite(
		CSharpProjectIndexCacheWriteRequest request,
		CSharpProjectIndexCacheWriteResult result,
		bool writeCancellationRequested
	)
	{
		lock (_sync)
		{
			bool isLatestPublication =
				request.Generation == _latestPublicationGeneration;
			bool isLatestGeneration = request.Generation == _latestRequestedGeneration;
			bool hasNewerPendingRequest =
				_pendingRequest != null
				&& _pendingRequest.Generation > request.Generation;

			if (
				_stopAcceptingRequests
				|| writeCancellationRequested
				|| result.IsCancelled
				|| result.IsStale
				|| !isLatestPublication
				|| !isLatestGeneration
				|| hasNewerPendingRequest
			)
			{
				return;
			}

			if (result.IsSuccessful || result.IsFailed)
				_latestReportableWriteResult = result;
		}
	}

	private static long NextGeneration(long generation)
	{
		unchecked
		{
			generation++;
			return generation == 0 ? 1 : generation;
		}
	}

	private static void RequestCancellation(CancellationTokenSource cancellation)
	{
		if (cancellation == null)
			return;

		try { cancellation.Cancel(); }
		catch (ObjectDisposedException) { }
	}

	private static CSharpProjectIndexCacheWriteResult CreateNonCommittedResult(
		CSharpProjectIndexCacheWriteRequest request,
		CSharpProjectIndexCacheWriteStatus status,
		TimeSpan elapsed
	)
	{
		return status == CSharpProjectIndexCacheWriteStatus.Stale
			? CreateStaleResult(request, elapsed)
			: CreateCancelledResult(request, elapsed);
	}

	private static CSharpProjectIndexCacheWriteResult CreateCancelledResult(
		CSharpProjectIndexCacheWriteRequest request,
		TimeSpan elapsed
	)
	{
		return new CSharpProjectIndexCacheWriteResult(
			request.Generation,
			CSharpProjectIndexCacheWriteStatus.Cancelled,
			elapsed,
			request.Snapshot?.FileCount ?? 0,
			request.Snapshot?.TypeCount ?? 0,
			failureDetail: ""
		);
	}

	private static CSharpProjectIndexCacheWriteResult CreateStaleResult(
		CSharpProjectIndexCacheWriteRequest request,
		TimeSpan elapsed
	)
	{
		return new CSharpProjectIndexCacheWriteResult(
			request.Generation,
			CSharpProjectIndexCacheWriteStatus.Stale,
			elapsed,
			request.Snapshot?.FileCount ?? 0,
			request.Snapshot?.TypeCount ?? 0,
			failureDetail: ""
		);
	}

	private static CSharpProjectIndexCacheWriteResult CreateUnexpectedFailureResult(
		CSharpProjectIndexCacheWriteRequest request,
		Exception exception,
		TimeSpan elapsed
	)
	{
		return new CSharpProjectIndexCacheWriteResult(
			request.Generation,
			CSharpProjectIndexCacheWriteStatus.Failed,
			elapsed,
			request.Snapshot?.FileCount ?? 0,
			request.Snapshot?.TypeCount ?? 0,
			CreateExceptionDetail("Unexpected cache worker failure", exception)
		);
	}

	private CSharpProjectIndexCacheWriteResult CreateUnexpectedLoopFailureResult(
		Exception exception
	)
	{
		long generation;
		lock (_sync)
			generation = _latestRequestedGeneration;

		return new CSharpProjectIndexCacheWriteResult(
			generation,
			CSharpProjectIndexCacheWriteStatus.Failed,
			TimeSpan.Zero,
			0,
			0,
			CreateExceptionDetail("Unexpected cache worker-loop failure", exception)
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
