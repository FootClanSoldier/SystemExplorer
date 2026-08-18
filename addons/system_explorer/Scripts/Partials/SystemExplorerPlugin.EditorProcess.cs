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
			ProcessAutocompleteReloadStabilization();
		}
		catch
		{
		}

		try
		{
			ProcessAutocompleteScriptTransitionStabilization();
		}
		catch
		{
		}

		try
		{
			ProcessPendingAutocompleteProcessWork();
		}
		catch
		{
			ClearPendingAutocompleteProcessWork();
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
				|| HasPendingAutocompleteReloadStabilizationProcessWork()
				|| HasPendingAutocompleteScriptTransitionStabilizationProcessWork()
				|| HasPendingAutocompleteProcessWork()
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
