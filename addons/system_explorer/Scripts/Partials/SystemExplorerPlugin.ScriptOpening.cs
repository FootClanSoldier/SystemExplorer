#if TOOLS
using Godot;
using System;
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

		bool expectedScriptAlreadyActiveBeforeEdit =
			TryGetActiveScriptPath(out string activeScriptPathBeforeEdit)
			&& string.Equals(
				ScriptPathUtility.Normalize(activeScriptPathBeforeEdit),
				normalizedScriptPath,
				StringComparison.OrdinalIgnoreCase
			);

		ScriptEditorTransition scriptTransition =
			BeginSystemExplorerScriptEditorTransition(normalizedScriptPath);

		long sourceActivationToken;

		if (sourceOccurrence.HasValue)
		{
			sourceActivationToken = RegisterSystemExplorerScriptActivation(
				sourceOccurrence.Value,
				normalizedScriptPath,
				scriptTransition.TransitionId
			);
		}
		else
		{
			ClearPendingSystemExplorerScriptActivation();
			sourceActivationToken = 0;
		}

		ScriptEditorEditBoundaryContext editBoundary = BeginEditScriptDiagnosticBoundary(
			"System Explorer navigation",
			normalizedScriptPath
		);
		EditorInterface.Singleton.EditScript(script);
		CompleteEditScriptDiagnosticBoundary(editBoundary);

		if (expectedScriptAlreadyActiveBeforeEdit)
		{
			QueueDeferredSystemExplorerSameScriptTransitionObservation(
				scriptTransition,
				normalizedScriptPath
			);
		}

		QueueSystemExplorerScriptActivationDeferredCheck(sourceActivationToken);

		if (releaseTreeFocusAfterNavigation)
			CallDeferred(nameof(ReleaseTreeFocusAfterNavigation));
	}

	#endregion
}
#endif
