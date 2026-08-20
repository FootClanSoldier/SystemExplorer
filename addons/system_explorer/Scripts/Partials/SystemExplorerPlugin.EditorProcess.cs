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
			ProcessPendingContextMenuOpen();
		}
		catch
		{
			InvalidateContextMenuOpenRequest("ProcessException");
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
			ProcessAutocompleteScriptTransitionStabilization(delta);
		}
		catch
		{
		}

		try
		{
			ProcessPendingAutocompleteIndexingQuiescence(delta);
		}
		catch
		{
			ClearPendingAutocompleteIndexingQuiescenceProcessWork();
		}

		try
		{
			ProcessPendingAutocompleteProcessWork(delta);
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
				|| HasPendingContextMenuOpenProcessWork()
				|| HasPendingAutocompleteReloadStabilizationProcessWork()
				|| HasPendingAutocompleteScriptTransitionStabilizationProcessWork()
				|| HasPendingAutocompleteIndexingQuiescenceProcessWork()
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
