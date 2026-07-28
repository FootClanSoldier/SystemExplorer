#if TOOLS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SystemExplorer.QuickActions.Beautify.CSharpier;

namespace SystemExplorer.EditorIntegration.Operations;

internal sealed class EditorOperationLease : IDisposable
{
	private readonly object _sync = new();
	private readonly EditorOperationLifetime _owner;
	private readonly CancellationTokenSource _cancellation;
	private readonly HashSet<Process> _processes = new();
	private readonly TaskCompletionSource<bool> _completion = new(
		TaskCreationOptions.RunContinuationsAsynchronously
	);
	private int _cancellationRequested;
	private int _executionCompleted;
	private int _disposed;

	internal EditorOperationLease(EditorOperationLifetime owner, string operationName, bool backgroundOperation, CancellationToken shutdownToken)
	{
		_owner = owner ?? throw new ArgumentNullException(nameof(owner));
		OperationName = string.IsNullOrWhiteSpace(operationName) ? "Editor Operation" : operationName;
		IsBackgroundOperation = backgroundOperation;
		_cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
	}

	internal string OperationName { get; }
	internal bool IsBackgroundOperation { get; }
	internal CancellationToken CancellationToken => _cancellation.Token;
	internal Task Completion => _completion.Task;
	internal bool IsCancellationRequested => Volatile.Read(ref _cancellationRequested) != 0 || _cancellation.IsCancellationRequested;
	internal bool IsExecutionCompleted => Volatile.Read(ref _executionCompleted) != 0;
	internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;
	internal bool IsActive => !IsDisposed && !IsCancellationRequested && !IsExecutionCompleted;
	internal bool IsCurrent => !IsDisposed && _owner.IsCurrent(this);

	internal bool TryRegisterProcess(Process process)
	{
		if (process == null) return false;
		lock (_sync)
		{
			if (!IsActive) return false;
			_processes.Add(process);
			return true;
		}
	}

	internal void UnregisterProcess(Process process)
	{
		if (process == null) return;
		lock (_sync) _processes.Remove(process);
	}

	internal void RequestCancellationAndKillRegisteredProcesses()
	{
		Interlocked.Exchange(ref _cancellationRequested, 1);
		try { _cancellation.Cancel(); } catch { }
		Process[] processes;
		lock (_sync) processes = new List<Process>(_processes).ToArray();
		foreach (Process process in processes)
			CSharpierProcessUtility.TryKillProcess(process);
	}

	internal void MarkExecutionCompleted()
	{
		if (Interlocked.Exchange(ref _executionCompleted, 1) != 0) return;
		try
		{
			_owner.Complete(this);
		}
		finally
		{
			_completion.TrySetResult(true);
		}
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
		RequestCancellationAndKillRegisteredProcesses();
		lock (_sync) _processes.Clear();
		_cancellation.Dispose();
	}
}
#endif
