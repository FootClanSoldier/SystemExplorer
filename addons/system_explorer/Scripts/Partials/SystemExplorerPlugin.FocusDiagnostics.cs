#if TOOLS
using Godot;

public partial class SystemExplorerPlugin
{
	public override void _Notification(int what)
	{
		if (!DebugState)
			return;

		if (what == MainLoop.NotificationApplicationFocusOut)
		{
			DebugLogger.LogOperation(
				"System Explorer application focus out",
				() =>
					$"HostNull='{_autocompleteHost == null}', HostInstanceToken='{_autocompleteHostInstanceToken}', ManagedAssemblyGeneration='{ManagedAssemblyGeneration}', ManagedRecoveryInProgress='{_isRecoveringManagedAssemblyState}'"
			);
			return;
		}

		if (what == MainLoop.NotificationApplicationFocusIn)
		{
			DebugLogger.LogOperation(
				"System Explorer application focus in",
				() =>
					$"HostNull='{_autocompleteHost == null}', HostInstanceToken='{_autocompleteHostInstanceToken}', ManagedAssemblyGeneration='{ManagedAssemblyGeneration}', ManagedRecoveryInProgress='{_isRecoveringManagedAssemblyState}'"
			);
		}
	}
}
#endif
