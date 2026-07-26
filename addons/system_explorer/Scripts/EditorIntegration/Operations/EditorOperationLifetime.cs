#if TOOLS
using System;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;

namespace SystemExplorer.EditorIntegration.Operations;

internal sealed class EditorOperationLifetime : IDisposable
{
	private readonly object _sync = new();
	private readonly CancellationTokenSource _shutdownCancellation = new();
	private readonly AssemblyLoadContext _assemblyLoadContext;
	private EditorOperationLease _activeLease;
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

	internal Task ActiveOperationCompletion
	{
		get { lock (_sync) return _activeLease?.Completion ?? Task.CompletedTask; }
	}

	internal bool TryBegin(string operationName, bool backgroundOperation, out EditorOperationLease lease)
	{
		lock (_sync)
		{
			if (_isShuttingDown || _disposed)
			{
				lease = null;
				return false;
			}

			if (_activeLease != null && !_activeLease.IsExecutionCompleted)
			{
				if (backgroundOperation || !_activeLease.IsBackgroundOperation)
				{
					lease = null;
					return false;
				}

				_activeLease.RequestCancellationAndKillRegisteredProcesses();
				lease = null;
				return false;
			}

			lease = new EditorOperationLease(this, operationName, backgroundOperation, _shutdownCancellation.Token);
			_activeLease = lease;
			return true;
		}
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
		lock (_sync) activeLease = _activeLease;
		activeLease?.RequestCancellationAndKillRegisteredProcesses();
		return activeLease?.Completion ?? Task.CompletedTask;
	}

	internal void Shutdown()
	{
		ShutdownManaged();
		DetachAssemblyUnloadHandler();
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
		}
		_shutdownCancellation.Dispose();
	}
}
#endif
