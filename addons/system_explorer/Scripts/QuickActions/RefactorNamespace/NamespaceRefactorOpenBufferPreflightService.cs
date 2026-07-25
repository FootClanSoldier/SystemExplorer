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

internal enum NamespaceRefactorOpenBufferPreflightFailure
{
	None,
	LookupFailed,
	AutosaveFailed,
	ReferenceGuardFailed,
	ActivationLookupFailed,
}

internal readonly record struct NamespaceRefactorOpenBufferPreflightResult(
	bool Success,
	bool DidAutosave,
	string FailureMessage,
	NamespaceRefactorOpenBufferPreflightFailure Failure,
	string FailurePath,
	ScriptEditorBufferLookupFailure LookupFailure,
	ScriptEditorBufferAutosaveFailure AutosaveFailure,
	ScriptEditorBufferAutosaveDiagnosticReason DiagnosticReason
)
{
	internal static NamespaceRefactorOpenBufferPreflightResult Succeeded(
		bool didAutosave = false
	) => new(
		true,
		didAutosave,
		"",
		NamespaceRefactorOpenBufferPreflightFailure.None,
		"",
		ScriptEditorBufferLookupFailure.None,
		ScriptEditorBufferAutosaveFailure.None,
		ScriptEditorBufferAutosaveDiagnosticReason.None
	);

	internal static NamespaceRefactorOpenBufferPreflightResult Failed(
		NamespaceRefactorOpenBufferPreflightFailure failure,
		bool didAutosave,
		string failureMessage,
		string failurePath = "",
		ScriptEditorBufferLookupFailure lookupFailure = ScriptEditorBufferLookupFailure.None,
		ScriptEditorBufferAutosaveFailure autosaveFailure =
			ScriptEditorBufferAutosaveFailure.None,
		ScriptEditorBufferAutosaveDiagnosticReason diagnosticReason =
			ScriptEditorBufferAutosaveDiagnosticReason.None
	) => new(
		false,
		didAutosave,
		failureMessage ?? "",
		failure,
		failurePath ?? "",
		lookupFailure,
		autosaveFailure,
		diagnosticReason
	);
}

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
		Action<string> debugLog,
		NamespaceRefactorDiagnosticContext diagnosticContext = null
	)
	{
		diagnosticContext?.Log(
			"Preflight",
			() =>
				$"Open-buffer preflight started; Mode={mode}; NamespaceReferenceToProtect='{namespaceReferenceToProtect ?? ""}'; ScriptEditorNull={scriptEditor == null}"
		);

		if (scriptEditor == null)
		{
			diagnosticContext?.Log(
				"Preflight",
				"Open-buffer preflight completed; no ScriptEditor was available and no open buffers were inspected."
			);
			return NamespaceRefactorOpenBufferPreflightResult.Succeeded();
		}

		List<string> normalizedCandidatePaths =
			candidatePaths
				?.Where(path => !string.IsNullOrWhiteSpace(path))
				.Select(ScriptPathUtility.Normalize)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList()
			?? new List<string>();

		if (normalizedCandidatePaths.Count == 0)
		{
			diagnosticContext?.Log(
				"Preflight",
				"Open-buffer preflight completed; candidate path set was empty."
			);
			return NamespaceRefactorOpenBufferPreflightResult.Succeeded();
		}

		HashSet<string> effectiveRequiredPaths =
			requiredPaths
				?.Where(path => !string.IsNullOrWhiteSpace(path))
				.Select(ScriptPathUtility.Normalize)
				.ToHashSet(StringComparer.OrdinalIgnoreCase)
			?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		diagnosticContext?.Log(
			"Preflight",
			() =>
				$"Open-buffer preflight policy resolved; Mode={mode}; CandidateCount={normalizedCandidatePaths.Count}; RequiredCount={effectiveRequiredPaths.Count}; Candidates={diagnosticContext.FormatPaths(normalizedCandidatePaths)}; Required={diagnosticContext.FormatPaths(effectiveRequiredPaths)}"
		);

		NamespaceRefactorOpenBufferPreflightResult result = mode switch
		{
			NamespaceRefactorOpenBufferPreflightMode.ActivatingOnly =>
				TryAutosaveByActivatingCandidatePaths(
					editorInterface,
					scriptEditor,
					normalizedCandidatePaths,
					effectiveRequiredPaths,
					debugLog,
					diagnosticContext
				),
			NamespaceRefactorOpenBufferPreflightMode.NonActivatingOnly =>
				TryAutosaveWithoutActivation(
					scriptEditor,
					normalizedCandidatePaths,
					effectiveRequiredPaths,
					namespaceReferenceToProtect,
					debugLog,
					diagnosticContext
				),
			NamespaceRefactorOpenBufferPreflightMode.NonActivatingWithActivationFallback =>
				TryAutosaveWithoutActivationWithActivationFallback(
					editorInterface,
					scriptEditor,
					normalizedCandidatePaths,
					effectiveRequiredPaths,
					debugLog,
					diagnosticContext
				),
			_ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
		};

		diagnosticContext?.Log(
			"Preflight",
			() =>
				$"Open-buffer preflight completed; Success={result.Success}; DidAutosave={result.DidAutosave}; Failure={result.Failure}; FailurePath='{result.FailurePath}'; LookupFailure={result.LookupFailure}; AutosaveFailure={result.AutosaveFailure}; DiagnosticReason={result.DiagnosticReason}"
		);
		return result;
	}

	private NamespaceRefactorOpenBufferPreflightResult TryAutosaveWithoutActivation(
		ScriptEditor scriptEditor,
		IReadOnlyList<string> normalizedCandidatePaths,
		HashSet<string> requiredPaths,
		string namespaceReferenceToProtect,
		Action<string> debugLog,
		NamespaceRefactorDiagnosticContext diagnosticContext
	)
	{
		ScriptEditorBufferGroupLookupResult lookupResult =
			_lookupService.GetOpenScriptEditorGroupsWithoutActivation(
				scriptEditor,
				normalizedCandidatePaths,
				requiredPaths,
				diagnosticContext
			);

		if (!lookupResult.Success)
		{
			return CreateLookupFailureResult(lookupResult, didAutosave: false);
		}

		NamespaceRefactorOpenBufferPreflightResult autosaveResult =
			TryAutosaveMatchedOpenEditorGroups(
				lookupResult.OpenEditorGroupsByPath,
				requiredPaths,
				debugLog,
				diagnosticContext,
				out Dictionary<string, OpenScriptEditorBufferGroup> verifiedGroupsByPath,
				out _
			);

		if (!autosaveResult.Success)
			return autosaveResult;

		bool hasUnsafeReference = _referenceGuard.TryFindUnsafeReference(
			scriptEditor,
			verifiedGroupsByPath,
			namespaceReferenceToProtect,
			out string unmatchedUsingFailureMessage
		);
		diagnosticContext?.Log(
			"ReferenceGuard",
			() =>
				$"Reference guard completed; Namespace='{namespaceReferenceToProtect ?? ""}'; HasUnsafeReference={hasUnsafeReference}; MatchedGroupCount={verifiedGroupsByPath.Count}"
		);

		if (hasUnsafeReference)
		{
			return NamespaceRefactorOpenBufferPreflightResult.Failed(
				NamespaceRefactorOpenBufferPreflightFailure.ReferenceGuardFailed,
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
		Action<string> debugLog,
		NamespaceRefactorDiagnosticContext diagnosticContext
	)
	{
		ScriptEditorBufferGroupLookupResult lookupResult =
			_lookupService.GetOpenScriptEditorGroupsWithoutActivation(
				scriptEditor,
				normalizedCandidatePaths,
				requiredPaths,
				diagnosticContext
			);

		if (
			lookupResult.Failure
			== ScriptEditorBufferLookupFailure.AmbiguousRequiredOpenBufferGroup
		)
		{
			return CreateLookupFailureResult(lookupResult, didAutosave: false);
		}

		NamespaceRefactorOpenBufferPreflightResult nonActivatingResult =
			TryAutosaveMatchedOpenEditorGroups(
				lookupResult.OpenEditorGroupsByPath,
				requiredPaths,
				debugLog,
				diagnosticContext,
				out Dictionary<string, OpenScriptEditorBufferGroup> verifiedGroupsByPath,
				out List<string> optionalSingleGroupAutosaveFailures
			);

		if (!nonActivatingResult.Success)
			return nonActivatingResult;

		HashSet<string> unsafePathSet = new(
			lookupResult.UnsafeOpenScriptPaths,
			StringComparer.OrdinalIgnoreCase
		);

		foreach (string failedOptionalPath in optionalSingleGroupAutosaveFailures)
			unsafePathSet.Add(failedOptionalPath);

		HashSet<string> ambiguousDuplicatePathSet = new(
			lookupResult.AmbiguousOpenScriptPaths,
			StringComparer.OrdinalIgnoreCase
		);
		List<string> activationFallbackPaths = normalizedCandidatePaths
			.Where(path =>
				unsafePathSet.Contains(path)
				&& !ambiguousDuplicatePathSet.Contains(path)
				&& !verifiedGroupsByPath.ContainsKey(path)
				&& _activationService.IsScriptOpen(scriptEditor, path)
			)
			.ToList();

		diagnosticContext?.Log(
			"Preflight",
			() =>
				$"Activation fallback evaluated; Unsafe={diagnosticContext.FormatPaths(unsafePathSet)}; OptionalAutosaveFailures={diagnosticContext.FormatPaths(optionalSingleGroupAutosaveFailures)}; Ambiguous={diagnosticContext.FormatPaths(ambiguousDuplicatePathSet)}; FallbackPaths={diagnosticContext.FormatPaths(activationFallbackPaths)}"
		);

		if (activationFallbackPaths.Count == 0)
		{
			if (lookupResult.Failure == ScriptEditorBufferLookupFailure.UnmatchedRequiredOpenScripts)
			{
				return CreateLookupFailureResult(
					lookupResult,
					nonActivatingResult.DidAutosave
				);
			}

			return nonActivatingResult;
		}

		debugLog?.Invoke(
			"Refactor Namespace pre-scan could not safely match some open single-buffer paths without activation; falling back to activating lookup for those paths."
		);

		NamespaceRefactorOpenBufferPreflightResult activationFallbackResult =
			TryAutosaveByActivatingCandidatePaths(
				editorInterface,
				scriptEditor,
				activationFallbackPaths,
				requiredPaths,
				debugLog,
				diagnosticContext
			);
		bool didAutosave =
			nonActivatingResult.DidAutosave || activationFallbackResult.DidAutosave;

		return activationFallbackResult.Success
			? NamespaceRefactorOpenBufferPreflightResult.Succeeded(didAutosave)
			: activationFallbackResult with { DidAutosave = didAutosave };
	}

	private NamespaceRefactorOpenBufferPreflightResult TryAutosaveMatchedOpenEditorGroups(
		IReadOnlyDictionary<string, OpenScriptEditorBufferGroup> openEditorGroupsByPath,
		HashSet<string> requiredPaths,
		Action<string> debugLog,
		NamespaceRefactorDiagnosticContext diagnosticContext,
		out Dictionary<string, OpenScriptEditorBufferGroup> verifiedGroupsByPath,
		out List<string> optionalSingleGroupAutosaveFailures
	)
	{
		verifiedGroupsByPath = new Dictionary<string, OpenScriptEditorBufferGroup>(
			StringComparer.OrdinalIgnoreCase
		);
		optionalSingleGroupAutosaveFailures = new List<string>();
		bool didAutosaveCandidateScripts = false;

		if (openEditorGroupsByPath == null)
			return NamespaceRefactorOpenBufferPreflightResult.Succeeded();

		foreach (
			KeyValuePair<string, OpenScriptEditorBufferGroup> groupPair
			in openEditorGroupsByPath
		)
		{
			OpenScriptEditorBufferGroup group = groupPair.Value;
			bool isRequiredScript = requiredPaths.Contains(group.Path);
			diagnosticContext?.Log(
				"Autosave",
				() =>
					$"Preflight group autosave started; Path='{group.Path}'; Required={isRequiredScript}; MemberCount={group.Buffers.Count}; HasCurrentEditorBuffer={group.HasCurrentEditorBuffer}"
			);
			ScriptEditorBufferAutosaveOperationResult autosaveResult =
				_autosaveCoordinator.TryAutosaveGroupIfNeeded(
					group,
					diagnosticContext?.BufferDiagnostics
				);

			diagnosticContext?.Log(
				"Autosave",
				() =>
					$"Preflight group autosave completed; Path='{group.Path}'; Success={autosaveResult.Success}; DidAutosave={autosaveResult.DidAutosave}; Failure={autosaveResult.FailedAutosave.Failure}; DiagnosticReason={autosaveResult.FailedAutosave.DiagnosticReason}"
			);

			if (!autosaveResult.Success)
			{
				string autosaveFailureMessage =
					NamespaceScriptEditorBufferAutosaveFailureMessageBuilder.Build(
						autosaveResult.FailedAutosave
					);

				if (isRequiredScript)
				{
					return NamespaceRefactorOpenBufferPreflightResult.Failed(
						NamespaceRefactorOpenBufferPreflightFailure.AutosaveFailed,
						didAutosaveCandidateScripts,
						autosaveFailureMessage,
						autosaveResult.FailedAutosave.ScriptPath,
						autosaveFailure: autosaveResult.FailedAutosave.Failure,
						diagnosticReason: autosaveResult.FailedAutosave.DiagnosticReason
					);
				}

				debugLog?.Invoke(
					$"Refactor Namespace pre-scan excluded open candidate group '{group.Path}': {autosaveFailureMessage}"
				);

				if (group.Buffers.Count == 1)
					optionalSingleGroupAutosaveFailures.Add(group.Path);

				continue;
			}

			verifiedGroupsByPath.Add(groupPair.Key, group);

			if (autosaveResult.DidAutosave)
				didAutosaveCandidateScripts = true;
		}

		Dictionary<string, OpenScriptEditorBufferGroup> verifiedGroupsForDiagnostics =
			verifiedGroupsByPath;
		List<string> optionalAutosaveFailuresForDiagnostics =
			optionalSingleGroupAutosaveFailures;

		diagnosticContext?.Log(
			"Preflight",
			() =>
				$"Matched group verification completed; VerifiedGroupCount={verifiedGroupsForDiagnostics.Count}; OptionalSingleGroupAutosaveFailures={diagnosticContext.FormatPaths(optionalAutosaveFailuresForDiagnostics)}; DidAutosave={didAutosaveCandidateScripts}"
		);
		return NamespaceRefactorOpenBufferPreflightResult.Succeeded(
			didAutosaveCandidateScripts
		);
	}

	private NamespaceRefactorOpenBufferPreflightResult TryAutosaveByActivatingCandidatePaths(
		EditorInterface editorInterface,
		ScriptEditor scriptEditor,
		IReadOnlyList<string> normalizedCandidatePaths,
		HashSet<string> requiredPaths,
		Action<string> debugLog,
		NamespaceRefactorDiagnosticContext diagnosticContext
	)
	{
		bool didAutosaveCandidateScripts = false;

		foreach (string candidatePath in normalizedCandidatePaths)
		{
			bool isRequiredScript = requiredPaths.Contains(candidatePath);

			if (!_activationService.IsScriptOpen(scriptEditor, candidatePath))
			{
				diagnosticContext?.Log(
					"BufferLookup",
					() => $"Activating lookup skipped closed script; Path='{candidatePath}'; Required={isRequiredScript}"
				);
				continue;
			}

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
				diagnosticContext?.Log(
					"BufferLookup",
					() => $"Activating lookup failed; Path='{candidatePath}'; Required={isRequiredScript}; FailureMessage='{editorFailureMessage}'"
				);

				if (isRequiredScript)
				{
					return NamespaceRefactorOpenBufferPreflightResult.Failed(
						NamespaceRefactorOpenBufferPreflightFailure.ActivationLookupFailed,
						didAutosaveCandidateScripts,
						editorFailureMessage,
						candidatePath
					);
				}

				debugLog?.Invoke(
					$"Refactor Namespace pre-scan skipped autosave for open candidate '{candidatePath}': {editorFailureMessage}"
				);
				continue;
			}

			diagnosticContext?.Log(
				"BufferLookup",
				() =>
					$"Activating lookup matched editor; Path='{candidatePath}'; Required={isRequiredScript}; EditorPath='{openEditor.Path}'; TextEditNull={openEditor.TextEditor == null}; ActivationVerified=true"
			);
			diagnosticContext?.Log(
				"Autosave",
				() => $"Activating autosave started; Path='{candidatePath}'; Required={isRequiredScript}"
			);
			ScriptEditorBufferAutosaveOperationResult autosaveResult =
				_autosaveCoordinator.TryAutosaveIfNeeded(
					openEditor,
					isRequiredScript,
					diagnosticContext?.BufferDiagnostics
				);
			diagnosticContext?.Log(
				"Autosave",
				() =>
					$"Activating autosave completed; Path='{candidatePath}'; Success={autosaveResult.Success}; DidAutosave={autosaveResult.DidAutosave}; Failure={autosaveResult.FailedAutosave.Failure}; DiagnosticReason={autosaveResult.FailedAutosave.DiagnosticReason}; FailurePath='{autosaveResult.FailedAutosave.ScriptPath}'"
			);

			if (!autosaveResult.Success)
			{
				string autosaveFailureMessage =
					NamespaceScriptEditorBufferAutosaveFailureMessageBuilder.Build(
						autosaveResult.FailedAutosave
					);

				if (isRequiredScript)
				{
					return NamespaceRefactorOpenBufferPreflightResult.Failed(
						NamespaceRefactorOpenBufferPreflightFailure.AutosaveFailed,
						didAutosaveCandidateScripts,
						autosaveFailureMessage,
						autosaveResult.FailedAutosave.ScriptPath,
						autosaveFailure: autosaveResult.FailedAutosave.Failure,
						diagnosticReason: autosaveResult.FailedAutosave.DiagnosticReason
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

		return NamespaceRefactorOpenBufferPreflightResult.Succeeded(
			didAutosaveCandidateScripts
		);
	}

	private static NamespaceRefactorOpenBufferPreflightResult CreateLookupFailureResult(
		ScriptEditorBufferGroupLookupResult lookupResult,
		bool didAutosave
	)
	{
		return NamespaceRefactorOpenBufferPreflightResult.Failed(
			NamespaceRefactorOpenBufferPreflightFailure.LookupFailed,
			didAutosave,
			NamespaceOpenBufferLookupService.BuildScriptEditorBufferLookupFailureMessage(
				lookupResult
			),
			lookupResult?.FailurePath ?? "",
			lookupResult?.Failure ?? ScriptEditorBufferLookupFailure.None
		);
	}
}
#endif
