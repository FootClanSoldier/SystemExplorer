#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal enum NamespaceRefactorOpenBufferPreflightMode
{
	ActivatingOnly,
	NonActivatingOnly,
	NonActivatingWithActivationFallback,
}

internal readonly record struct NamespaceRefactorOpenBufferPreflightResult(
	bool Success,
	bool DidAutosave,
	string FailureMessage
);

internal sealed class NamespaceRefactorOpenBufferPreflightService
{
	private readonly NamespaceOpenBufferActivationService _activationService;
	private readonly NamespaceOpenBufferLookupService _lookupService;
	private readonly NamespaceOpenBufferReferenceGuard _referenceGuard;
	private readonly ScriptEditorBufferAutosaveCoordinator _autosaveCoordinator;

	internal NamespaceRefactorOpenBufferPreflightService(
		NamespaceOpenBufferActivationService activationService,
		NamespaceOpenBufferLookupService lookupService,
		NamespaceOpenBufferReferenceGuard referenceGuard,
		ScriptEditorBufferAutosaveCoordinator autosaveCoordinator
	)
	{
		_activationService =
			activationService ?? throw new ArgumentNullException(nameof(activationService));
		_lookupService = lookupService ?? throw new ArgumentNullException(nameof(lookupService));
		_referenceGuard =
			referenceGuard ?? throw new ArgumentNullException(nameof(referenceGuard));
		_autosaveCoordinator =
			autosaveCoordinator ?? throw new ArgumentNullException(nameof(autosaveCoordinator));
	}

	internal NamespaceRefactorOpenBufferPreflightResult TryAutosaveCandidateScriptsBeforeBuild(
		EditorInterface editorInterface,
		ScriptEditor scriptEditor,
		IEnumerable<string> candidatePaths,
		HashSet<string> requiredPaths,
		NamespaceRefactorOpenBufferPreflightMode mode,
		string namespaceReferenceToProtect,
		Action<string> debugLog
	)
	{
		if (scriptEditor == null)
			return new NamespaceRefactorOpenBufferPreflightResult(true, false, "");

		List<string> normalizedCandidatePaths =
			candidatePaths
				?.Where(path => !string.IsNullOrWhiteSpace(path))
				.Select(ScriptPathUtility.Normalize)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList()
			?? new List<string>();

		if (normalizedCandidatePaths.Count == 0)
			return new NamespaceRefactorOpenBufferPreflightResult(true, false, "");

		HashSet<string> effectiveRequiredPaths =
			requiredPaths ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		return mode switch
		{
			NamespaceRefactorOpenBufferPreflightMode.ActivatingOnly =>
				TryAutosaveByActivatingCandidatePaths(
					editorInterface,
					scriptEditor,
					normalizedCandidatePaths,
					effectiveRequiredPaths,
					debugLog
				),
			NamespaceRefactorOpenBufferPreflightMode.NonActivatingOnly =>
				TryAutosaveWithoutActivation(
					scriptEditor,
					normalizedCandidatePaths,
					effectiveRequiredPaths,
					namespaceReferenceToProtect,
					debugLog
				),
			NamespaceRefactorOpenBufferPreflightMode.NonActivatingWithActivationFallback =>
				TryAutosaveWithoutActivationWithActivationFallback(
					editorInterface,
					scriptEditor,
					normalizedCandidatePaths,
					effectiveRequiredPaths,
					debugLog
				),
			_ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
		};
	}

	private NamespaceRefactorOpenBufferPreflightResult TryAutosaveWithoutActivation(
		ScriptEditor scriptEditor,
		IReadOnlyList<string> normalizedCandidatePaths,
		HashSet<string> requiredPaths,
		string namespaceReferenceToProtect,
		Action<string> debugLog
	)
	{
		HashSet<string> targetPaths = normalizedCandidatePaths.ToHashSet(
			StringComparer.OrdinalIgnoreCase
		);

		if (
			!_lookupService.TryGetOpenScriptEditorsByIndexedScriptEditorPaths(
				scriptEditor,
				targetPaths,
				out Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath,
				out string editorFailureMessage,
				requiredPaths
			)
		)
		{
			return new NamespaceRefactorOpenBufferPreflightResult(
				false,
				false,
				editorFailureMessage
			);
		}

		NamespaceRefactorOpenBufferPreflightResult autosaveResult =
			TryAutosaveMatchedOpenEditors(openEditorsByPath.Values, requiredPaths, debugLog);

		if (!autosaveResult.Success)
			return autosaveResult;

		if (
			_referenceGuard.TryFindUnsafeReference(
				scriptEditor,
				openEditorsByPath,
				namespaceReferenceToProtect,
				out string unmatchedUsingFailureMessage
			)
		)
		{
			return new NamespaceRefactorOpenBufferPreflightResult(
				false,
				autosaveResult.DidAutosave,
				unmatchedUsingFailureMessage
			);
		}

		return autosaveResult;
	}

	private NamespaceRefactorOpenBufferPreflightResult TryAutosaveWithoutActivationWithActivationFallback(
		EditorInterface editorInterface,
		ScriptEditor scriptEditor,
		IReadOnlyList<string> normalizedCandidatePaths,
		HashSet<string> requiredPaths,
		Action<string> debugLog
	)
	{
		HashSet<string> targetPaths = normalizedCandidatePaths.ToHashSet(
			StringComparer.OrdinalIgnoreCase
		);
		HashSet<string> openCandidatePathSet = new(StringComparer.OrdinalIgnoreCase);

		foreach (Script openScript in scriptEditor.GetOpenScripts())
		{
			if (openScript == null)
				continue;

			string openScriptPath = ScriptPathUtility.Normalize(openScript.ResourcePath);

			if (targetPaths.Contains(openScriptPath))
				openCandidatePathSet.Add(openScriptPath);
		}

		List<string> openCandidatePaths = normalizedCandidatePaths
			.Where(openCandidatePathSet.Contains)
			.ToList();

		if (
			!_lookupService.TryGetOpenScriptEditorsByIndexedScriptEditorPaths(
				scriptEditor,
				targetPaths,
				out Dictionary<string, OpenScriptEditorBuffer> openEditorsByPath,
				out _,
				requiredPaths
			)
		)
		{
			debugLog?.Invoke(
				"Refactor Namespace pre-scan could not safely match open buffers without activation; falling back to activating lookup."
			);

			return TryAutosaveByActivatingCandidatePaths(
				editorInterface,
				scriptEditor,
				openCandidatePaths,
				requiredPaths,
				debugLog
			);
		}

		NamespaceRefactorOpenBufferPreflightResult nonActivatingResult =
			TryAutosaveMatchedOpenEditors(openEditorsByPath.Values, requiredPaths, debugLog);

		if (!nonActivatingResult.Success)
			return nonActivatingResult;

		List<string> unmatchedOpenCandidatePaths = openCandidatePaths
			.Where(path => !openEditorsByPath.ContainsKey(path))
			.ToList();

		if (unmatchedOpenCandidatePaths.Count == 0)
			return nonActivatingResult;

		NamespaceRefactorOpenBufferPreflightResult activationFallbackResult =
			TryAutosaveByActivatingCandidatePaths(
				editorInterface,
				scriptEditor,
				unmatchedOpenCandidatePaths,
				requiredPaths,
				debugLog
			);
		bool didAutosave =
			nonActivatingResult.DidAutosave || activationFallbackResult.DidAutosave;

		return new NamespaceRefactorOpenBufferPreflightResult(
			activationFallbackResult.Success,
			didAutosave,
			activationFallbackResult.FailureMessage
		);
	}

	private NamespaceRefactorOpenBufferPreflightResult TryAutosaveMatchedOpenEditors(
		IEnumerable<OpenScriptEditorBuffer> openEditors,
		HashSet<string> requiredPaths,
		Action<string> debugLog
	)
	{
		bool didAutosaveCandidateScripts = false;

		foreach (OpenScriptEditorBuffer openEditor in openEditors)
		{
			bool isRequiredScript = requiredPaths.Contains(openEditor.Path);
			ScriptEditorBufferAutosaveOperationResult autosaveResult =
				_autosaveCoordinator.TryAutosaveIfNeeded(openEditor, isRequiredScript);

			if (!autosaveResult.Success)
			{
				string autosaveFailureMessage =
					NamespaceScriptEditorBufferAutosaveFailureMessageBuilder.Build(
						autosaveResult.FailedAutosave
					);

				if (isRequiredScript)
				{
					return new NamespaceRefactorOpenBufferPreflightResult(
						false,
						didAutosaveCandidateScripts,
						autosaveFailureMessage
					);
				}

				debugLog?.Invoke(
					$"Refactor Namespace pre-scan skipped autosave for open candidate '{openEditor.Path}': {autosaveFailureMessage}"
				);
				continue;
			}

			if (autosaveResult.DidAutosave)
				didAutosaveCandidateScripts = true;
		}

		return new NamespaceRefactorOpenBufferPreflightResult(
			true,
			didAutosaveCandidateScripts,
			""
		);
	}

	private NamespaceRefactorOpenBufferPreflightResult TryAutosaveByActivatingCandidatePaths(
		EditorInterface editorInterface,
		ScriptEditor scriptEditor,
		IReadOnlyList<string> normalizedCandidatePaths,
		HashSet<string> requiredPaths,
		Action<string> debugLog
	)
	{
		bool didAutosaveCandidateScripts = false;

		foreach (string candidatePath in normalizedCandidatePaths)
		{
			bool isRequiredScript = requiredPaths.Contains(candidatePath);

			if (!_activationService.IsScriptOpen(scriptEditor, candidatePath))
				continue;

			if (
				!_activationService.TryGetOpenScriptEditorByActivatingPath(
					editorInterface,
					editorInterface?.GetScriptEditor(),
					candidatePath,
					debugLog,
					out OpenScriptEditorBuffer openEditor,
					out string editorFailureMessage
				)
			)
			{
				if (isRequiredScript)
				{
					return new NamespaceRefactorOpenBufferPreflightResult(
						false,
						didAutosaveCandidateScripts,
						editorFailureMessage
					);
				}

				debugLog?.Invoke(
					$"Refactor Namespace pre-scan skipped autosave for open candidate '{candidatePath}': {editorFailureMessage}"
				);
				continue;
			}

			ScriptEditorBufferAutosaveOperationResult autosaveResult =
				_autosaveCoordinator.TryAutosaveIfNeeded(openEditor, isRequiredScript);

			if (!autosaveResult.Success)
			{
				string autosaveFailureMessage =
					NamespaceScriptEditorBufferAutosaveFailureMessageBuilder.Build(
						autosaveResult.FailedAutosave
					);

				if (isRequiredScript)
				{
					return new NamespaceRefactorOpenBufferPreflightResult(
						false,
						didAutosaveCandidateScripts,
						autosaveFailureMessage
					);
				}

				debugLog?.Invoke(
					$"Refactor Namespace pre-scan skipped autosave for open candidate '{candidatePath}': {autosaveFailureMessage}"
				);
				continue;
			}

			if (autosaveResult.DidAutosave)
				didAutosaveCandidateScripts = true;
		}

		return new NamespaceRefactorOpenBufferPreflightResult(
			true,
			didAutosaveCandidateScripts,
			""
		);
	}
}
#endif
