#if TOOLS
using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using SystemExplorer.EditorIntegration.Operations;

public partial class SystemExplorerPlugin
{
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
		if (!lifetime.HasActiveOperation)
			return true;

		Task completion = lifetime.RequestCancellationAndGetCompletion();
		CancelBeautifyManagedStateForShutdown();

		if (completion.IsCompleted)
			return true;

		TryLogEditorOperation(
			"Project Run Deferred",
			"An active Beautify/CSharpier operation was cancelled. Press Play again after cancellation completes."
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

	private void StartObservedEditorOperation(string operationName, Func<EditorOperationLease, Task> operation, bool backgroundOperation = false)
	{
		_ = RunProtectedEditorOperationAsync(operationName, operation, backgroundOperation);
	}

	private async Task RunProtectedEditorOperationAsync(string operationName, Func<EditorOperationLease, Task> operation, bool backgroundOperation)
	{
		if (operation == null) throw new ArgumentNullException(nameof(operation));
		if (_editorOperationShutdownStarted || !EditorOperations.TryBegin(operationName, backgroundOperation, out EditorOperationLease lease))
		{
			if (!_editorOperationShutdownStarted)
				TryLogEditorOperation("Editor Operation Rejected", operationName);
			return;
		}

		try
		{
			TryLogEditorOperation("Editor Operation Started", operationName);
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
