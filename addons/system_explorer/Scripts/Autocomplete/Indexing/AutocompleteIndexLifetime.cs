#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Loader;
using System.Threading;

namespace SystemExplorer.Autocomplete.Indexing;

internal sealed class AutocompleteIndexLifetime
{
	private readonly object _sync = new();
	private readonly CancellationTokenSource _shutdownCancellation = new();
	private readonly AssemblyLoadContext _assemblyLoadContext;
	private readonly Action<string, string> _persistentDiagnosticLog;
	private readonly Dictionary<string, int> _activeWorkerKinds = new(StringComparer.Ordinal);
	private readonly string _lifetimeToken = Guid.NewGuid().ToString("N");
	private readonly int _assemblyLoadContextObjectToken;
	private readonly string _assemblyLoadContextName;
	private readonly string _assemblyLoadContextCollectible;
	private int _activeWorkerCount;
	private bool _isShuttingDown;
	private bool _resourcesDisposed;
	private bool _unloadHandlerDetached;

	internal AutocompleteIndexLifetime(Action<string, string> persistentDiagnosticLog = null)
	{
		_persistentDiagnosticLog = persistentDiagnosticLog;
		_assemblyLoadContext = AssemblyLoadContext.GetLoadContext(
			typeof(AutocompleteIndexLifetime).Assembly
		);
		_assemblyLoadContextObjectToken = CSharpRoslynRuntimeDiagnostics.GetObjectToken(
			_assemblyLoadContext
		);
		_assemblyLoadContextName = DescribeLoadContextName(_assemblyLoadContext);
		_assemblyLoadContextCollectible = DescribeLoadContextCollectible(
			_assemblyLoadContext
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

	internal bool TryBeginWorker(
		string workerKind,
		out CancellationToken shutdownToken
	)
	{
		lock (_sync)
		{
			if (_isShuttingDown || _resourcesDisposed)
			{
				shutdownToken = default;
				return false;
			}

			_activeWorkerCount++;
			IncrementWorkerKindLocked(NormalizeWorkerKind(workerKind));
			shutdownToken = _shutdownCancellation.Token;
			return true;
		}
	}

	internal void NotifyWorkerStopped(string workerKind)
	{
		bool workerWasCounted;
		bool shouldLogDrain;
		bool shouldDispose;
		int remainingWorkers;
		string remainingWorkerKinds;
		string normalizedWorkerKind = NormalizeWorkerKind(workerKind);

		lock (_sync)
		{
			workerWasCounted = _activeWorkerCount > 0;
			if (workerWasCounted)
			{
				_activeWorkerCount--;
				DecrementWorkerKindLocked(normalizedWorkerKind);
			}

			remainingWorkers = _activeWorkerCount;
			remainingWorkerKinds = DescribeActiveWorkerKindsLocked();
			shouldLogDrain = _isShuttingDown && workerWasCounted;
			shouldDispose = _isShuttingDown && _activeWorkerCount == 0;
		}

		if (shouldLogDrain)
		{
			Trace(
				"C# autocomplete index lifetime worker drained",
				$"{CreateIdentityDetail()}, WorkerKind='{normalizedWorkerKind}', "
					+ $"RemainingWorkers={remainingWorkers}, RemainingWorkerKinds='{remainingWorkerKinds}'"
			);
		}

		if (shouldDispose && DisposeCancellationResources())
			TraceDrainCompleted();
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
		ShutdownManaged("ExplicitHostShutdown");
		DetachAssemblyUnloadHandler();
	}

	private void OnAssemblyUnloading(AssemblyLoadContext context)
	{
		ShutdownManaged("AssemblyLoadContext.Unloading");
	}

	private void ShutdownManaged(string trigger)
	{
		bool shouldCancel;
		bool shouldDispose;
		int activeWorkers;
		string activeWorkerKinds;

		lock (_sync)
		{
			if (_isShuttingDown || _resourcesDisposed)
				return;

			_isShuttingDown = true;
			shouldCancel = true;
			shouldDispose = _activeWorkerCount == 0;
			activeWorkers = _activeWorkerCount;
			activeWorkerKinds = DescribeActiveWorkerKindsLocked();
		}

		Trace(
			"C# autocomplete index lifetime shutdown begin",
			$"Trigger='{trigger}', {CreateIdentityDetail()}, ActiveWorkers={activeWorkers}, "
				+ $"ActiveWorkerKinds='{activeWorkerKinds}'"
		);

		bool cancellationIssued = false;
		if (shouldCancel)
		{
			try
			{
				_shutdownCancellation.Cancel();
				cancellationIssued = true;
			}
			catch (ObjectDisposedException)
			{
			}
		}

		Trace(
			"C# autocomplete index lifetime shutdown cancellation issued",
			$"Trigger='{trigger}', {CreateIdentityDetail()}, CancellationIssued={cancellationIssued}"
		);

		if (shouldDispose && DisposeCancellationResources())
			TraceDrainCompleted();
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

	private bool DisposeCancellationResources()
	{
		lock (_sync)
		{
			if (_resourcesDisposed || _activeWorkerCount != 0)
				return false;

			_resourcesDisposed = true;
		}

		_shutdownCancellation.Dispose();
		return true;
	}

	private void IncrementWorkerKindLocked(string workerKind)
	{
		_activeWorkerKinds.TryGetValue(workerKind, out int count);
		_activeWorkerKinds[workerKind] = count + 1;
	}

	private void DecrementWorkerKindLocked(string workerKind)
	{
		if (!_activeWorkerKinds.TryGetValue(workerKind, out int count))
			return;

		if (count <= 1)
			_activeWorkerKinds.Remove(workerKind);
		else
			_activeWorkerKinds[workerKind] = count - 1;
	}

	private string DescribeActiveWorkerKindsLocked()
	{
		if (_activeWorkerKinds.Count == 0)
			return "<none>";

		return string.Join(
			", ",
			_activeWorkerKinds
				.OrderBy(pair => pair.Key, StringComparer.Ordinal)
				.Select(pair => $"{pair.Key}={pair.Value}")
		);
	}

	private string CreateIdentityDetail()
	{
		return
			$"LifetimeToken='{_lifetimeToken}', "
			+ $"AssemblyLoadContextObjectToken={_assemblyLoadContextObjectToken}, "
			+ $"AssemblyLoadContextName='{_assemblyLoadContextName}', "
			+ $"AssemblyLoadContextCollectible={_assemblyLoadContextCollectible}";
	}

	private void TraceDrainCompleted()
	{
		Trace(
			"C# autocomplete index lifetime drain completed",
			$"{CreateIdentityDetail()}, RemainingWorkers=0, RemainingWorkerKinds='<none>'"
		);
	}

	private void Trace(string operation, string details)
	{
		try
		{
			_persistentDiagnosticLog?.Invoke(operation ?? "", details ?? "");
		}
		catch
		{
		}
	}

	private static string NormalizeWorkerKind(string workerKind)
	{
		return string.IsNullOrWhiteSpace(workerKind) ? "<unknown>" : workerKind.Trim();
	}

	private static string DescribeLoadContextName(AssemblyLoadContext loadContext)
	{
		try
		{
			if (loadContext == null)
				return "<null>";

			return string.IsNullOrWhiteSpace(loadContext.Name)
				? "<unnamed>"
				: loadContext.Name;
		}
		catch (Exception exception)
		{
			return $"<read-failed:{exception.GetType().Name}>";
		}
	}

	private static string DescribeLoadContextCollectible(AssemblyLoadContext loadContext)
	{
		try
		{
			return loadContext == null ? "<unknown>" : loadContext.IsCollectible.ToString();
		}
		catch (Exception exception)
		{
			return $"<read-failed:{exception.GetType().Name}>";
		}
	}
}
#endif
