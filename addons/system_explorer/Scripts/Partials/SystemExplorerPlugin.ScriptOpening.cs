#if TOOLS
using Godot;
using SystemExplorer.EditorIntegration.ScriptEditing;

public partial class SystemExplorerPlugin
{
	#region Script Opening

	private void OpenScriptFromSystemExplorer(
		Script script,
		string scriptPath,
		bool releaseTreeFocusAfterNavigation = true,
		ScriptTreeOccurrence? sourceOccurrence = null
	)
	{
		if (script == null)
			return;

		string normalizedScriptPath = ScriptPathUtility.Normalize(scriptPath);

		if (string.IsNullOrWhiteSpace(normalizedScriptPath))
			normalizedScriptPath = ScriptPathUtility.Normalize(script.ResourcePath);

		long sourceActivationToken;

		if (sourceOccurrence.HasValue)
		{
			sourceActivationToken = RegisterSystemExplorerScriptActivation(
				sourceOccurrence.Value,
				normalizedScriptPath
			);
		}
		else
		{
			ClearPendingSystemExplorerScriptActivation();
			sourceActivationToken = 0;
		}

		EditorInterface.Singleton.EditScript(script);
		QueueSystemExplorerScriptActivationDeferredCheck(sourceActivationToken);

		if (releaseTreeFocusAfterNavigation)
			CallDeferred(nameof(ReleaseTreeFocusAfterNavigation));
	}

	#endregion
}
#endif
