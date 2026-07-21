#if TOOLS
using Godot;
using System;
using System.Collections.Generic;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal sealed class NamespaceRefactorPreflightCoordinator
{
	private readonly NamespaceRefactorOpenBufferPreflightService _openBufferPreflightService;
	private readonly Func<EditorInterface> _editorInterfaceProvider;
	private readonly Action<string> _debugLog;
	private readonly Action<string> _showWarning;

	internal NamespaceRefactorPreflightCoordinator(
		NamespaceRefactorOpenBufferPreflightService openBufferPreflightService,
		Func<EditorInterface> editorInterfaceProvider,
		Action<string> debugLog,
		Action<string> showWarning
	)
	{
		_openBufferPreflightService =
			openBufferPreflightService
			?? throw new ArgumentNullException(nameof(openBufferPreflightService));
		_editorInterfaceProvider =
			editorInterfaceProvider
			?? throw new ArgumentNullException(nameof(editorInterfaceProvider));
		_debugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
		_showWarning = showWarning ?? throw new ArgumentNullException(nameof(showWarning));
	}

	internal bool PreflightSingleReplacement(
		EditorInterface editorInterface,
		ScriptEditor scriptEditor,
		IEnumerable<string> candidatePaths,
		HashSet<string> requiredPaths
	)
	{
		NamespaceRefactorOpenBufferPreflightResult preflightResult =
			_openBufferPreflightService.TryAutosaveCandidateScriptsBeforeBuild(
				editorInterface,
				scriptEditor,
				candidatePaths,
				requiredPaths,
				mode: NamespaceRefactorOpenBufferPreflightMode.NonActivatingWithActivationFallback,
				namespaceReferenceToProtect: "",
				debugLog: _debugLog
			);

		if (!preflightResult.Success)
		{
			_showWarning(
				string.IsNullOrWhiteSpace(preflightResult.FailureMessage)
					? "Refactor Namespace cancelled: open script buffer(s) could not be autosaved safely before scanning namespace usages."
					: preflightResult.FailureMessage
			);
			return false;
		}

		if (preflightResult.DidAutosave)
			_debugLog("Refactor Namespace save-first pre-scan saved open script buffer(s).");

		return true;
	}

	internal bool PreflightAddNamespace(
		IEnumerable<string> candidatePaths,
		HashSet<string> requiredPaths,
		string operationName,
		bool allowScriptEditorActivation
	)
	{
		NamespaceRefactorOpenBufferPreflightResult preflightResult =
			_openBufferPreflightService.TryAutosaveCandidateScriptsBeforeBuild(
				_editorInterfaceProvider(),
				_editorInterfaceProvider()?.GetScriptEditor(),
				candidatePaths,
				requiredPaths,
				mode: allowScriptEditorActivation
					? NamespaceRefactorOpenBufferPreflightMode.ActivatingOnly
					: NamespaceRefactorOpenBufferPreflightMode.NonActivatingOnly,
				namespaceReferenceToProtect: "",
				debugLog: _debugLog
			);

		if (!preflightResult.Success)
		{
			_debugLog(
				string.IsNullOrWhiteSpace(preflightResult.FailureMessage)
					? $"{operationName} cancelled: open script buffer(s) could not be autosaved safely before adding namespace."
					: preflightResult.FailureMessage
			);
			return false;
		}

		if (preflightResult.DidAutosave)
			_debugLog($"{operationName} save-first pre-scan saved open script buffer(s).");

		return true;
	}

	internal bool PreflightBatchReplacement(
		IEnumerable<string> candidatePaths,
		HashSet<string> requiredPaths,
		string oldNamespace
	)
	{
		NamespaceRefactorOpenBufferPreflightResult preflightResult =
			_openBufferPreflightService.TryAutosaveCandidateScriptsBeforeBuild(
				_editorInterfaceProvider(),
				_editorInterfaceProvider()?.GetScriptEditor(),
				candidatePaths,
				requiredPaths,
				mode: NamespaceRefactorOpenBufferPreflightMode.NonActivatingOnly,
				namespaceReferenceToProtect: oldNamespace,
				debugLog: _debugLog
			);

		if (!preflightResult.Success)
		{
			_showWarning(
				string.IsNullOrWhiteSpace(preflightResult.FailureMessage)
					? "Refactor Namespace cancelled: open script buffer(s) could not be autosaved safely before scanning namespace usages."
					: preflightResult.FailureMessage
			);
			return false;
		}

		if (preflightResult.DidAutosave)
		{
			_debugLog(
				"Refactor Namespace batch save-first pre-scan saved open script buffer(s)."
			);
		}

		return true;
	}
}
#endif
