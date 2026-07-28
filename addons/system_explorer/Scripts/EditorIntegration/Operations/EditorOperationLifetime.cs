#if TOOLS
using System;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;

namespace SystemExplorer.EditorIntegration.Operations;

internal enum EditorOperationBeginStatus
{
	Started,
	Rejected,
	BackgroundCancellationRequested,
	Unavailable,
}

internal readonly struct EditorOperationBeginResult
{
	internal EditorOperationBeginResult(
		EditorOperationBeginStatus status,
		EditorOperationLease lease = null,
		Task preemptedOperationCompletion = null,
		long preemptionReservationId = 0
	)
	{
		Status = status;
		Lease = lease;
		PreemptedOperationCompletion = preemptedOperationCompletion ?? Task.CompletedTask;
		PreemptionReservationId = preemptionReservationId;
	}

	internal EditorOperationBeginStatus Status { get; }
	internal EditorOperationLease Lease { get; }
	internal Task PreemptedOperationCompletion { get; }
	internal long PreemptionReservationId { get; }
}

internal sealed class EditorOperationLifetime : IDisposable
{
	private readonly object _sync = new();
	private readonly CancellationTokenSource _shutdownCancellation = new();
	private readonly AssemblyLoadContext _assemblyLoadContext;
	private EditorOperationLease _activeLease;
	private EditorOperationLease _preemptedBackgroundLease;
	private long _nextPreemptionReservationId;
	private long _foregroundPreemptionReservationId;
	private bool _isShuttingDown;
	private bool _disposed;

	internal EditorOperationLifetime()
	{
		_assemblyLoadContext = AssemblyLoadContext.GetLoadContext(typeof(EditorOperationLifetime).Assembly);
		if (_assemblyLoadContext != null)
			_assemblyLoadContext.Unloading += OnAssemblyUnloading;
	}

	internal bool IsActive
	{
		get { lock (_sync) return !_isShuttingDown && !_disposed; }
	}

	internal bool HasActiveOperation
	{
		get { lock (_sync) return _activeLease != null && !_activeLease.IsExecutionCompleted; }
	}

	internal bool HasPendingForegroundPreemption
	{
		get { lock (_sync) return _foregroundPreemptionReservationId != 0; }
	}

	internal Task ActiveOperationCompletion
	{
		get { lock (_sync) return _activeLease?.Completion ?? Task.CompletedTask; }
	}

	internal EditorOperationBeginResult TryBegin(string operationName, bool backgroundOperation)
	{
		lock (_sync)
		{
			if (_isShuttingDown || _disposed)
				return new EditorOperationBeginResult(EditorOperationBeginStatus.Unavailable);

			if (_foregroundPreemptionReservationId != 0)
				return new EditorOperationBeginResult(EditorOperationBeginStatus.Rejected);

			if (_activeLease != null && !_activeLease.IsExecutionCompleted)
			{
				if (backgroundOperation || !_activeLease.IsBackgroundOperation)
					return new EditorOperationBeginResult(EditorOperationBeginStatus.Rejected);

				EditorOperationLease preemptedBackgroundLease = _activeLease;
				Task preemptedOperationCompletion = preemptedBackgroundLease.Completion;
				long reservationId = CreatePreemptionReservation(preemptedBackgroundLease);

				preemptedBackgroundLease.RequestCancellationAndKillRegisteredProcesses();
				return new EditorOperationBeginResult(
					EditorOperationBeginStatus.BackgroundCancellationRequested,
					preemptedOperationCompletion: preemptedOperationCompletion,
					preemptionReservationId: reservationId
				);
			}

			EditorOperationLease lease = CreateLease(operationName, backgroundOperation);
			return new EditorOperationBeginResult(EditorOperationBeginStatus.Started, lease);
		}
	}

	internal EditorOperationBeginResult TryBeginReservedForeground(
		string operationName,
		long preemptionReservationId
	)
	{
		lock (_sync)
		{
			if (_isShuttingDown || _disposed)
			{
				ClearPreemptionReservation(preemptionReservationId);
				return new EditorOperationBeginResult(EditorOperationBeginStatus.Unavailable);
			}

			if (
				preemptionReservationId == 0
				|| _foregroundPreemptionReservationId != preemptionReservationId
			)
			{
				return new EditorOperationBeginResult(EditorOperationBeginStatus.Rejected);
			}

			if (
				_preemptedBackgroundLease == null
				|| !_preemptedBackgroundLease.IsExecutionCompleted
				|| (_activeLease != null && !_activeLease.IsExecutionCompleted)
			)
			{
				ClearPreemptionReservation(preemptionReservationId);
				return new EditorOperationBeginResult(EditorOperationBeginStatus.Rejected);
			}

			ClearPreemptionReservation(preemptionReservationId);
			EditorOperationLease lease = CreateLease(operationName, backgroundOperation: false);
			return new EditorOperationBeginResult(EditorOperationBeginStatus.Started, lease);
		}
	}

	internal bool OwnsForegroundPreemption(long preemptionReservationId)
	{
		lock (_sync)
		{
			return !_isShuttingDown
				&& !_disposed
				&& preemptionReservationId != 0
				&& _foregroundPreemptionReservationId == preemptionReservationId;
		}
	}

	internal void AbandonForegroundPreemption(long preemptionReservationId)
	{
		lock (_sync)
			ClearPreemptionReservation(preemptionReservationId);
	}

	internal bool IsCurrent(EditorOperationLease lease)
	{
		lock (_sync)
			return !_isShuttingDown && !_disposed && ReferenceEquals(_activeLease, lease) && lease != null && !lease.IsExecutionCompleted;
	}

	internal void Complete(EditorOperationLease lease)
	{
		lock (_sync)
		{
			if (ReferenceEquals(_activeLease, lease))
				_activeLease = null;
		}
	}

	internal Task RequestCancellationAndGetCompletion()
	{
		EditorOperationLease activeLease;
		Task completion;
		lock (_sync)
		{
			activeLease = _activeLease;
			completion = activeLease?.Completion ?? Task.CompletedTask;
			ClearPreemptionReservation();
		}

		activeLease?.RequestCancellationAndKillRegisteredProcesses();
		return completion;
	}

	internal void Shutdown()
	{
		ShutdownManaged();
		DetachAssemblyUnloadHandler();
	}

	private EditorOperationLease CreateLease(string operationName, bool backgroundOperation)
	{
		EditorOperationLease lease = new(
			this,
			operationName,
			backgroundOperation,
			_shutdownCancellation.Token
		);
		_activeLease = lease;
		return lease;
	}

	private long CreatePreemptionReservation(EditorOperationLease preemptedBackgroundLease)
	{
		unchecked
		{
			_nextPreemptionReservationId++;
			if (_nextPreemptionReservationId == 0)
				_nextPreemptionReservationId = 1;
		}

		_foregroundPreemptionReservationId = _nextPreemptionReservationId;
		_preemptedBackgroundLease = preemptedBackgroundLease;
		return _foregroundPreemptionReservationId;
	}

	private void ClearPreemptionReservation(long expectedReservationId = 0)
	{
		if (
			expectedReservationId != 0
			&& _foregroundPreemptionReservationId != expectedReservationId
		)
		{
			return;
		}

		_foregroundPreemptionReservationId = 0;
		_preemptedBackgroundLease = null;
	}

	private void OnAssemblyUnloading(AssemblyLoadContext context)
	{
		ShutdownManaged();
	}

	private void ShutdownManaged()
	{
		EditorOperationLease activeLease;
		lock (_sync)
		{
			if (_isShuttingDown || _disposed)
				return;
			_isShuttingDown = true;
			activeLease = _activeLease;
			ClearPreemptionReservation();
		}

		try { _shutdownCancellation.Cancel(); } catch { }
		activeLease?.RequestCancellationAndKillRegisteredProcesses();
	}

	private void DetachAssemblyUnloadHandler()
	{
		if (_assemblyLoadContext != null)
			_assemblyLoadContext.Unloading -= OnAssemblyUnloading;
	}

	public void Dispose()
	{
		Shutdown();
		lock (_sync)
		{
			if (_disposed) return;
			_disposed = true;
			ClearPreemptionReservation();
		}
		_shutdownCancellation.Dispose();
	}
}
#endif
