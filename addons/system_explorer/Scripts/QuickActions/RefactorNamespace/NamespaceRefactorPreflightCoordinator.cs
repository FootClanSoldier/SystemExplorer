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
		HashSet<string> requiredPaths,
		NamespaceRefactorDiagnosticContext diagnosticContext
	)
	{
		const NamespaceRefactorOpenBufferPreflightMode mode =
			NamespaceRefactorOpenBufferPreflightMode.NonActivatingWithActivationFallback;
		diagnosticContext?.Log("Preflight", () => $"Started; Mode={mode}");
		NamespaceRefactorOpenBufferPreflightResult preflightResult =
			_openBufferPreflightService.TryAutosaveCandidateScriptsBeforeBuild(
				editorInterface,
				scriptEditor,
				candidatePaths,
				requiredPaths,
				mode,
				namespaceReferenceToProtect: "",
				debugLog: _debugLog,
				diagnosticContext: diagnosticContext
			);

		if (!preflightResult.Success)
		{
			LogCancellation(diagnosticContext, preflightResult);
			_showWarning(
				string.IsNullOrWhiteSpace(preflightResult.FailureMessage)
					? "Refactor Namespace cancelled: open script buffer(s) could not be autosaved safely before scanning namespace usages."
					: preflightResult.FailureMessage
			);
			return false;
		}

		if (preflightResult.DidAutosave)
			_debugLog("Refactor Namespace save-first pre-scan saved open script buffer(s).");

		diagnosticContext?.Log(
			"Preflight",
			() => $"Succeeded; Mode={mode}; DidAutosave={preflightResult.DidAutosave}"
		);
		return true;
	}

	internal bool PreflightAddNamespace(
		IEnumerable<string> candidatePaths,
		HashSet<string> requiredPaths,
		string operationName,
		bool allowScriptEditorActivation,
		NamespaceRefactorDiagnosticContext diagnosticContext
	)
	{
		NamespaceRefactorOpenBufferPreflightMode mode = allowScriptEditorActivation
			? NamespaceRefactorOpenBufferPreflightMode.ActivatingOnly
			: NamespaceRefactorOpenBufferPreflightMode.NonActivatingOnly;
		diagnosticContext?.Log("Preflight", () => $"Started; Mode={mode}");
		NamespaceRefactorOpenBufferPreflightResult preflightResult =
			_openBufferPreflightService.TryAutosaveCandidateScriptsBeforeBuild(
				_editorInterfaceProvider(),
				_editorInterfaceProvider()?.GetScriptEditor(),
				candidatePaths,
				requiredPaths,
				mode,
				namespaceReferenceToProtect: "",
				debugLog: _debugLog,
				diagnosticContext: diagnosticContext
			);

		if (!preflightResult.Success)
		{
			LogCancellation(diagnosticContext, preflightResult);
			_debugLog(
				string.IsNullOrWhiteSpace(preflightResult.FailureMessage)
					? $"{operationName} cancelled: open script buffer(s) could not be autosaved safely before adding namespace."
					: preflightResult.FailureMessage
			);
			return false;
		}

		if (preflightResult.DidAutosave)
			_debugLog($"{operationName} save-first pre-scan saved open script buffer(s).");

		diagnosticContext?.Log(
			"Preflight",
			() => $"Succeeded; Mode={mode}; DidAutosave={preflightResult.DidAutosave}"
		);
		return true;
	}

	internal bool PreflightBatchReplacement(
		IEnumerable<string> candidatePaths,
		HashSet<string> requiredPaths,
		string oldNamespace,
		NamespaceRefactorDiagnosticContext diagnosticContext
	)
	{
		const NamespaceRefactorOpenBufferPreflightMode mode =
			NamespaceRefactorOpenBufferPreflightMode.NonActivatingOnly;
		diagnosticContext?.Log("Preflight", () => $"Started; Mode={mode}");
		NamespaceRefactorOpenBufferPreflightResult preflightResult =
			_openBufferPreflightService.TryAutosaveCandidateScriptsBeforeBuild(
				_editorInterfaceProvider(),
				_editorInterfaceProvider()?.GetScriptEditor(),
				candidatePaths,
				requiredPaths,
				mode,
				namespaceReferenceToProtect: oldNamespace,
				debugLog: _debugLog,
				diagnosticContext: diagnosticContext
			);

		if (!preflightResult.Success)
		{
			LogCancellation(diagnosticContext, preflightResult);
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

		diagnosticContext?.Log(
			"Preflight",
			() => $"Succeeded; Mode={mode}; DidAutosave={preflightResult.DidAutosave}"
		);
		return true;
	}

	private static void LogCancellation(
		NamespaceRefactorDiagnosticContext diagnosticContext,
		NamespaceRefactorOpenBufferPreflightResult result
	)
	{
		diagnosticContext?.Log(
			"Cancellation",
			() =>
				$"Phase=Preflight; Result=Cancelled; FailurePath='{result.FailurePath}'; Failure={result.Failure}; LookupFailure={result.LookupFailure}; AutosaveFailure={result.AutosaveFailure}; DiagnosticReason={result.DiagnosticReason}"
		);
	}
}
#endif
