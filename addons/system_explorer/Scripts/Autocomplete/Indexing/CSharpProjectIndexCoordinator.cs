#if TOOLS
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SystemExplorer.Autocomplete.Indexing.Persistence;

namespace SystemExplorer.Autocomplete.Indexing;

internal sealed class CSharpProjectIndexCoordinator
{
	private readonly object _sync = new();
	private readonly object _publicationGate = new();
	private readonly AutocompleteIndexLifetime _lifetime;
	private readonly CSharpProjectIndex _index;
	private readonly CSharpProjectIndexWorker _worker;
	private readonly CSharpProjectIndexCacheCoordinator _cacheCoordinator;

	private Task _workerTask;
	private CancellationTokenSource _activeBuildCancellation;
	private CSharpProjectIndexBuildRequest _pendingRequest;
	private CSharpProjectIndexBuildResult _latestCompletedBuildResult;
	private long _latestRequestedGeneration;
	private long _latestPublishedGeneration;
	private long _workerLoopId;
	private bool _workerRunning;
	private bool _stopAcceptingRequests;

	internal CSharpProjectIndexCoordinator(
		AutocompleteIndexLifetime lifetime,
		CSharpProjectIndex index,
		CSharpProjectIndexWorker worker,
		CSharpProjectIndexCacheCoordinator cacheCoordinator
	)
	{
		_lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
		_index = index ?? throw new ArgumentNullException(nameof(index));
		_worker = worker ?? throw new ArgumentNullException(nameof(worker));
		_cacheCoordinator =
			cacheCoordinator ?? throw new ArgumentNullException(nameof(cacheCoordinator));
	}

	internal bool RequestRefresh(
		string reason,
		string globalProjectRoot,
		string cachePath
	)
	{
		CancellationTokenSource activeBuildCancellation;
		bool accepted;

		lock (_publicationGate)
		{
			lock (_sync)
			{
				if (_stopAcceptingRequests || !_lifetime.IsActive)
					return false;

				long generation = NextGenerationLocked();
				string normalizedReason = string.IsNullOrWhiteSpace(reason)
					? "Unspecified refresh"
					: reason.Trim();
				activeBuildCancellation = _activeBuildCancellation;

				if (
					!TryValidateGlobalProjectRoot(
						globalProjectRoot,
						out string normalizedRoot,
						out string failureDetail
					)
				)
				{
					_pendingRequest = null;
					_latestCompletedBuildResult = new CSharpProjectIndexBuildResult(
						generation,
						normalizedReason,
						CSharpProjectIndexBuildStatus.Failed,
						TimeSpan.Zero,
						0,
						0,
						0,
						0,
						0,
						0,
						0,
						failureDetail,
						Array.Empty<string>(),
						snapshot: null
					);
					accepted = false;
				}
				else
				{
					string normalizedCachePath = NormalizeCachePath(cachePath, normalizedRoot);
					_pendingRequest = new CSharpProjectIndexBuildRequest(
						generation,
						normalizedReason,
						normalizedRoot,
						normalizedCachePath,
						_index.CurrentSnapshot
					);
					StartWorkerLoopLocked();
					accepted = true;
				}
			}
		}

		RequestCancellation(activeBuildCancellation);
		return accepted;
	}

	internal bool TryTakeLatestBuildResult(
		out CSharpProjectIndexBuildResult result
	)
	{
		lock (_sync)
		{
			result = _latestCompletedBuildResult;
			_latestCompletedBuildResult = null;
			return result != null;
		}
	}

	internal void ResetTransientState()
	{
		CancellationTokenSource activeBuildCancellation;

		lock (_publicationGate)
		{
			lock (_sync)
			{
				if (_stopAcceptingRequests)
					return;

				NextGenerationLocked();
				_pendingRequest = null;
				_latestCompletedBuildResult = null;
				activeBuildCancellation = _activeBuildCancellation;
			}
		}

		RequestCancellation(activeBuildCancellation);
	}

	internal void StopAcceptingRequests()
	{
		lock (_publicationGate)
		{
			lock (_sync)
				_stopAcceptingRequests = true;
		}
	}

	internal void Shutdown()
	{
		CancellationTokenSource activeBuildCancellation;

		lock (_publicationGate)
		{
			lock (_sync)
			{
				_stopAcceptingRequests = true;
				NextGenerationLocked();
				_pendingRequest = null;
				_latestCompletedBuildResult = null;
				activeBuildCancellation = _activeBuildCancellation;
			}
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
			_latestCompletedBuildResult = CreateUnexpectedLoopFailureResult(exception);
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
		CSharpProjectIndexBuildRequest activeRequest = null;

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
				CSharpProjectIndexBuildResult result;
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
			CSharpProjectIndexBuildResult failure = activeRequest != null
				? CreateUnexpectedFailureResult(activeRequest, exception)
				: CreateUnexpectedLoopFailureResult(exception);

			lock (_sync)
				_latestCompletedBuildResult = failure;
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
		out CSharpProjectIndexBuildRequest request,
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
		CSharpProjectIndexBuildRequest request,
		CSharpProjectIndexBuildResult result,
		bool buildCancellationRequested
	)
	{
		CSharpProjectIndexCacheWriteRequest cacheWriteRequest = null;

		lock (_publicationGate)
		{
			bool canPublish;

			lock (_sync)
			{
				bool isLatestGeneration = request.Generation == _latestRequestedGeneration;
				bool hasNewerPendingRequest =
					_pendingRequest != null
					&& _pendingRequest.Generation > request.Generation;
				canPublish =
					!_stopAcceptingRequests
					&& !buildCancellationRequested
					&& isLatestGeneration
					&& !hasNewerPendingRequest
					&& result.IsSuccessful
					&& result.Snapshot != null;

				if (!canPublish)
				{
					RecordNonPublishedResultLocked(
						request,
						result,
						buildCancellationRequested
					);
				}
			}

			if (canPublish)
			{
				if (!string.IsNullOrWhiteSpace(request.CachePath))
					_cacheCoordinator.MarkGenerationForPublication(request.Generation);

				bool published = _lifetime.TryRunWhileActive(
					() => _index.Publish(result.Snapshot)
				);

				lock (_sync)
				{
					if (published)
					{
						_latestPublishedGeneration = request.Generation;
						_latestCompletedBuildResult = result;

						if (!string.IsNullOrWhiteSpace(request.CachePath))
						{
							cacheWriteRequest = new CSharpProjectIndexCacheWriteRequest(
								request.Generation,
								request.CachePath,
								result.Snapshot
							);
						}
					}
					else
					{
						RecordNonPublishedResultLocked(
							request,
							result,
							buildCancellationRequested
						);
					}
				}
			}
		}

		if (cacheWriteRequest != null)
			_cacheCoordinator.RequestWrite(cacheWriteRequest);
	}

	private void RecordNonPublishedResultLocked(
		CSharpProjectIndexBuildRequest request,
		CSharpProjectIndexBuildResult result,
		bool buildCancellationRequested
	)
	{
		if (result.IsCancelled || buildCancellationRequested)
			return;

		if (result.IsSuccessful)
		{
			_latestCompletedBuildResult = result.AsStale(
				$"Build was not published because generation {request.Generation} "
				+ $"was stale. LatestRequested={_latestRequestedGeneration}, "
				+ $"LatestPublished={_latestPublishedGeneration}."
			);
			return;
		}

		_latestCompletedBuildResult = result;
	}

	private long NextGenerationLocked()
	{
		unchecked
		{
			_latestRequestedGeneration++;
			if (_latestRequestedGeneration == 0)
				_latestRequestedGeneration = 1;
		}

		return _latestRequestedGeneration;
	}

	private static bool TryValidateGlobalProjectRoot(
		string globalProjectRoot,
		out string normalizedRoot,
		out string failureDetail
	)
	{
		normalizedRoot = "";
		failureDetail = "";

		if (string.IsNullOrWhiteSpace(globalProjectRoot))
		{
			failureDetail = "Global project root is empty.";
			return false;
		}

		try
		{
			string trimmedRoot = globalProjectRoot.Trim();
			if (!Path.IsPathFullyQualified(trimmedRoot))
			{
				failureDetail = "Global project root is not fully qualified.";
				return false;
			}

			normalizedRoot = Path.GetFullPath(trimmedRoot);
			return true;
		}
		catch (Exception exception) when (
			exception is ArgumentException
			or NotSupportedException
			or PathTooLongException
		)
		{
			failureDetail = CreateExceptionDetail("Global project root is invalid", exception);
			return false;
		}
	}

	private static string NormalizeCachePath(string cachePath, string normalizedRoot)
	{
		if (string.IsNullOrWhiteSpace(cachePath))
			return "";

		try
		{
			string normalizedCachePath = Path.GetFullPath(cachePath.Trim());
			string expectedCachePath = CSharpProjectIndexCacheFormat.CreateCachePath(
				normalizedRoot
			);

			return string.Equals(
				normalizedCachePath,
				expectedCachePath,
				StringComparison.OrdinalIgnoreCase
			)
				? normalizedCachePath
				: "";
		}
		catch (Exception exception) when (
			exception is ArgumentException
			or NotSupportedException
			or PathTooLongException
		)
		{
			return "";
		}
	}

	private static void RequestCancellation(CancellationTokenSource cancellation)
	{
		if (cancellation == null)
			return;

		try { cancellation.Cancel(); }
		catch (ObjectDisposedException) { }
	}

	private static CSharpProjectIndexBuildResult CreateCancelledResult(
		CSharpProjectIndexBuildRequest request
	)
	{
		return new CSharpProjectIndexBuildResult(
			request.Generation,
			request.Reason,
			CSharpProjectIndexBuildStatus.Cancelled,
			TimeSpan.Zero,
			0,
			0,
			0,
			0,
			0,
			0,
			0,
			"Build cancellation was requested.",
			Array.Empty<string>(),
			snapshot: null
		);
	}

	private static CSharpProjectIndexBuildResult CreateUnexpectedFailureResult(
		CSharpProjectIndexBuildRequest request,
		Exception exception
	)
	{
		return new CSharpProjectIndexBuildResult(
			request.Generation,
			request.Reason,
			CSharpProjectIndexBuildStatus.Failed,
			TimeSpan.Zero,
			0,
			0,
			0,
			0,
			0,
			0,
			0,
			CreateExceptionDetail("Unexpected worker failure", exception),
			Array.Empty<string>(),
			snapshot: null
		);
	}

	private CSharpProjectIndexBuildResult CreateUnexpectedLoopFailureResult(
		Exception exception
	)
	{
		long generation;
		lock (_sync)
			generation = _latestRequestedGeneration;

		return new CSharpProjectIndexBuildResult(
			generation,
			"Worker loop",
			CSharpProjectIndexBuildStatus.Failed,
			TimeSpan.Zero,
			0,
			0,
			0,
			0,
			0,
			0,
			0,
			CreateExceptionDetail("Unexpected worker-loop failure", exception),
			Array.Empty<string>(),
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
