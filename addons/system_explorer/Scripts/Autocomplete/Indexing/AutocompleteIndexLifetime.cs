#if TOOLS
using System;
using System.Runtime.Loader;
using System.Threading;

namespace SystemExplorer.Autocomplete.Indexing;

internal sealed class AutocompleteIndexLifetime
{
	private readonly object _sync = new();
	private readonly CancellationTokenSource _shutdownCancellation = new();
	private readonly AssemblyLoadContext _assemblyLoadContext;
	private int _activeWorkerCount;
	private bool _isShuttingDown;
	private bool _resourcesDisposed;
	private bool _unloadHandlerDetached;

	internal AutocompleteIndexLifetime()
	{
		_assemblyLoadContext = AssemblyLoadContext.GetLoadContext(
			typeof(AutocompleteIndexLifetime).Assembly
		);

		if (_assemblyLoadContext != null)
			_assemblyLoadContext.Unloading += OnAssemblyUnloading;
	}

	internal bool IsActive
	{
		get
		{
			lock (_sync)
				return !_isShuttingDown && !_resourcesDisposed;
		}
	}

	internal bool TryBeginWorker(out CancellationToken shutdownToken)
	{
		lock (_sync)
		{
			if (_isShuttingDown || _resourcesDisposed)
			{
				shutdownToken = default;
				return false;
			}

			_activeWorkerCount++;
			shutdownToken = _shutdownCancellation.Token;
			return true;
		}
	}

	internal void NotifyWorkerStopped()
	{
		bool shouldDispose;

		lock (_sync)
		{
			if (_activeWorkerCount > 0)
				_activeWorkerCount--;

			shouldDispose = _isShuttingDown && _activeWorkerCount == 0;
		}

		if (shouldDispose)
			DisposeCancellationResources();
	}

	internal bool TryRunWhileActive(Action action)
	{
		ArgumentNullException.ThrowIfNull(action);

		lock (_sync)
		{
			if (_isShuttingDown || _resourcesDisposed)
				return false;

			action();
			return true;
		}
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
		bool shouldCancel;
		bool shouldDispose;

		lock (_sync)
		{
			if (_isShuttingDown || _resourcesDisposed)
				return;

			_isShuttingDown = true;
			shouldCancel = true;
			shouldDispose = _activeWorkerCount == 0;
		}

		if (shouldCancel)
		{
			try { _shutdownCancellation.Cancel(); }
			catch (ObjectDisposedException) { }
		}

		if (shouldDispose)
			DisposeCancellationResources();
	}

	private void DetachAssemblyUnloadHandler()
	{
		lock (_sync)
		{
			if (_unloadHandlerDetached)
				return;

			_unloadHandlerDetached = true;
		}

		if (_assemblyLoadContext != null)
			_assemblyLoadContext.Unloading -= OnAssemblyUnloading;
	}

	private void DisposeCancellationResources()
	{
		lock (_sync)
		{
			if (_resourcesDisposed || _activeWorkerCount != 0)
				return;

			_resourcesDisposed = true;
		}

		_shutdownCancellation.Dispose();
	}
}
#endif
