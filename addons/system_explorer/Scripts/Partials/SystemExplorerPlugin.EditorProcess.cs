#if TOOLS
using Godot;

public partial class SystemExplorerPlugin
{
	public override void _Process(double delta)
	{
		try
		{
			ProcessPendingPersistentTreeStateSave();
		}
		catch
		{
			ClearPendingPersistentTreeStateSave();
		}

		try
		{
			ProcessPendingNoteDialogOpen();
		}
		catch (System.Exception exception)
		{
			ClearPendingNoteDialogOpenState();

			try
			{
				DebugLogger.LogOperation(
					"Note process open failed unexpectedly",
					$"Exception='{exception}'"
				);
			}
			catch
			{
			}
		}

		try
		{
			ProcessActiveNoteDialogWindowObservation();
		}
		catch (System.Exception exception)
		{
			try
			{
				FaultNoteMinimizedRestoreObservation(
					"Note window observation failed unexpectedly",
					$"Exception='{exception}'"
				);
			}
			catch
			{
			}
		}

		bool shouldReapplyBusyCursor = false;

		try
		{
			shouldReapplyBusyCursor = ShouldReapplyEditorOperationBusyCursor();
			if (shouldReapplyBusyCursor)
				TrySetGlobalEditorOperationCursor(DisplayServer.CursorShape.Busy);
		}
		catch
		{
			shouldReapplyBusyCursor = false;
		}

		RefreshEditorPluginProcessingState(shouldReapplyBusyCursor);
	}

	private void RefreshEditorPluginProcessingState()
	{
		RefreshEditorPluginProcessingState(ShouldReapplyEditorOperationBusyCursor());
	}

	private void RefreshEditorPluginProcessingState(bool busyCursorNeedsProcessing)
	{
		TrySetEditorPluginProcessing(
			busyCursorNeedsProcessing
			|| HasPendingPersistentTreeStateProcessWork()
			|| HasPendingNoteDialogOpenProcessWork()
			|| HasActiveNoteDialogWindowObservationProcessWork()
		);
	}

	private void TrySetEditorPluginProcessing(bool enabled)
	{
		if (!IsValidGodotObject(this))
			return;

		try
		{
			SetProcess(enabled);
		}
		catch
		{
		}
	}
}
#endif
