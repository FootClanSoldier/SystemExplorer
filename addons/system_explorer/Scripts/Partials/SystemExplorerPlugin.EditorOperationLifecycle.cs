#if TOOLS
using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using SystemExplorer.EditorIntegration.Operations;

public partial class SystemExplorerPlugin
{
	private enum EditorOperationCursorPolicy
	{
		Busy,
		PreserveCurrent,
	}

	private EditorOperationLifetime _editorOperationLifetime;
	private bool _editorOperationShutdownStarted;

	private EditorOperationLifetime EditorOperations =>
		EnsureEditorOperationLifecycleCurrentForManagedAssembly();

	private EditorOperationLifetime EnsureEditorOperationLifecycleCurrentForManagedAssembly()
	{
		if (_editorOperationLifetime != null && _editorOperationLifetime.IsActive && !_editorOperationShutdownStarted)
		{
			if (!_editorOperationLifetime.HasActiveOperation)
				RecoverEditorOperationBusyCursorAfterManagedAssemblyReload();

			return _editorOperationLifetime;
		}

		RecoverEditorOperationBusyCursorAfterManagedAssemblyReload();
		_editorOperationLifetime?.Dispose();
		_editorOperationShutdownStarted = false;
		_editorOperationLifetime = new EditorOperationLifetime();
		return _editorOperationLifetime;
	}

	public override bool _Build()
	{
		EditorOperationLifetime lifetime = EnsureEditorOperationLifecycleCurrentForManagedAssembly();
		if (!lifetime.HasActiveOperation && !lifetime.HasPendingForegroundPreemption)
			return true;

		Task completion = lifetime.RequestCancellationAndGetCompletion();
		CancelBeautifyManagedStateForShutdown();

		if (completion.IsCompleted)
			return true;

		TryLogEditorOperation(
			"Project Run Deferred",
			"An active System Explorer editor operation was cancelled. Press Play again after cancellation completes."
		);
		return false;
	}

	private bool IsEditorOperationAccessValid(EditorOperationLease operation)
	{
		return !_editorOperationShutdownStarted
			&& operation?.IsCurrent == true
			&& GodotObject.IsInstanceValid(this)
			&& IsInsideTree();
	}

	private bool IsManagedForegroundPreemptionRetryValid(
		SystemExplorerPlugin capturedPlugin,
		EditorOperationLifetime capturedLifetime,
		long preemptionReservationId
	)
	{
		if (_editorOperationShutdownStarted)
			return false;

		if (!ReferenceEquals(this, capturedPlugin))
			return false;

		if (!ReferenceEquals(_editorOperationLifetime, capturedLifetime))
			return false;

		if (capturedLifetime?.IsActive != true)
			return false;

		if (!capturedLifetime.OwnsForegroundPreemption(preemptionReservationId))
			return false;

		return string.IsNullOrEmpty(_loadedPersistentTreeStateGeneration)
			|| string.Equals(
				_loadedPersistentTreeStateGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			);
	}

	private bool IsGodotForegroundPreemptionRetryValid()
	{
		return GodotObject.IsInstanceValid(this) && IsInsideTree();
	}

	private void StartObservedEditorOperation(
		string operationName,
		Func<EditorOperationLease, Task> operation,
		bool backgroundOperation = false,
		EditorOperationCursorPolicy cursorPolicy = EditorOperationCursorPolicy.Busy
	)
	{
		_ = RunProtectedEditorOperationAsync(
			operationName,
			operation,
			backgroundOperation,
			cursorPolicy
		);
	}

	private async Task RunProtectedEditorOperationAsync(
		string operationName,
		Func<EditorOperationLease, Task> operation,
		bool backgroundOperation,
		EditorOperationCursorPolicy cursorPolicy = EditorOperationCursorPolicy.Busy
	)
	{
		if (operation == null) throw new ArgumentNullException(nameof(operation));
		if (_editorOperationShutdownStarted) return;

		SystemExplorerPlugin capturedPlugin = this;
		EditorOperationLifetime capturedLifetime = EditorOperations;
		EditorOperationBeginResult beginResult = capturedLifetime.TryBegin(
			operationName,
			backgroundOperation
		);

		if (beginResult.Status == EditorOperationBeginStatus.BackgroundCancellationRequested)
		{
			long reservationId = beginResult.PreemptionReservationId;
			TryLogEditorOperation("Editor Operation Background Preemption Requested", operationName);
			TryLogEditorOperation("Editor Operation Waiting For Background Cleanup", operationName);

			try
			{
				await beginResult.PreemptedOperationCompletion;

				if (!IsManagedForegroundPreemptionRetryValid(
					capturedPlugin,
					capturedLifetime,
					reservationId
				))
				{
					return;
				}

				if (!IsGodotForegroundPreemptionRetryValid())
					return;

				beginResult = capturedLifetime.TryBeginReservedForeground(
					operationName,
					reservationId
				);

				if (beginResult.Status == EditorOperationBeginStatus.Started)
				{
					TryLogEditorOperation(
						"Editor Operation Started After Background Preemption",
						operationName
					);
				}
				else if (beginResult.Status == EditorOperationBeginStatus.Rejected)
				{
					TryLogEditorOperation("Editor Operation Rejected", operationName);
					return;
				}
				else
				{
					TryLogEditorOperation(
						"Editor Operation Background Preemption Abandoned",
						operationName
					);
					return;
				}
			}
			finally
			{
				capturedLifetime.AbandonForegroundPreemption(reservationId);
			}
		}

		if (beginResult.Status != EditorOperationBeginStatus.Started)
		{
			if (
				beginResult.Status == EditorOperationBeginStatus.Rejected
				&& !_editorOperationShutdownStarted
			)
			{
				TryLogEditorOperation("Editor Operation Rejected", operationName);
			}
			return;
		}

		EditorOperationLease lease = beginResult.Lease;
		try
		{
			TryLogEditorOperation("Editor Operation Started", operationName);
			if (cursorPolicy == EditorOperationCursorPolicy.Busy)
				TryEnterEditorOperationBusyCursor(lease, backgroundOperation);

			await operation(lease);
			if (lease.IsCurrent)
				TryLogEditorOperation("Editor Operation Completed", operationName);
		}
		catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested)
		{
			if (!_editorOperationShutdownStarted)
				TryLogEditorOperation("Editor Operation Cancelled", operationName);
		}
		catch (Exception exception)
		{
			if (IsEditorOperationAccessValid(lease))
				TryLogEditorOperation("Editor Operation Failed", $"{operationName}: {exception}");
		}
		finally
		{
			try
			{
				if (cursorPolicy == EditorOperationCursorPolicy.Busy)
					ExitEditorOperationBusyCursor(lease);
			}
			finally
			{
				try
				{
					lease.MarkExecutionCompleted();
				}
				finally
				{
					try
					{
						lease.Dispose();
					}
					finally
					{
						if (!_editorOperationShutdownStarted)
							TryLogEditorOperation("Editor Operation Cleanup Completed", operationName);
					}
				}
			}
		}
	}

	private void TryLogEditorOperation(string operationName, string detail = "")
	{
		try
		{
			DebugLogger.LogOperation(operationName, detail);
		}
		catch
		{
		}
	}

	private void ShutdownEditorOperationLifecycle()
	{
		ForceResetEditorOperationBusyCursor();
		if (_editorOperationShutdownStarted) return;

		_editorOperationShutdownStarted = true;
		try
		{
			_editorOperationLifetime?.Shutdown();
		}
		finally
		{
			CancelBeautifyManagedStateForShutdown();
			TryLogEditorOperation("Plugin Operation Lifetime Shutdown");
		}
	}
}
#endif
