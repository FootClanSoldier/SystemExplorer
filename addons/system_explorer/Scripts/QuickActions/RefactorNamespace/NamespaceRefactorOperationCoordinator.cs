#if TOOLS
using Godot;
using System;
using System.Collections.Generic;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal sealed class NamespaceRefactorOperationCoordinator
{
	private readonly NamespaceRefactorPreflightCoordinator _preflightCoordinator;
	private readonly NamespaceRefactorPlanBuildCoordinator _planBuildCoordinator;
	private readonly NamespaceRefactorApplyCoordinator _applyCoordinator;

	internal NamespaceRefactorOperationCoordinator(
		NamespaceRefactorPreflightCoordinator preflightCoordinator,
		NamespaceRefactorPlanBuildCoordinator planBuildCoordinator,
		NamespaceRefactorApplyCoordinator applyCoordinator
	)
	{
		_preflightCoordinator =
			preflightCoordinator ?? throw new ArgumentNullException(nameof(preflightCoordinator));
		_planBuildCoordinator =
			planBuildCoordinator
			?? throw new ArgumentNullException(nameof(planBuildCoordinator));
		_applyCoordinator =
			applyCoordinator ?? throw new ArgumentNullException(nameof(applyCoordinator));
	}

	internal bool ExecuteSingleReplacement(
		EditorInterface editorInterface,
		ScriptEditor scriptEditor,
		IEnumerable<string> candidatePaths,
		HashSet<string> requiredPaths,
		Func<NamespaceRefactorPendingWriteBuildResult> buildPendingWriteSet,
		NamespaceRefactorDiagnosticContext diagnosticContext
	)
	{
		diagnosticContext?.Log("Request", "Execution entered; Operation=SingleReplacement");

		if (
			!_preflightCoordinator.PreflightSingleReplacement(
				editorInterface,
				scriptEditor,
				candidatePaths,
				requiredPaths,
				diagnosticContext
			)
		)
		{
			return false;
		}

		diagnosticContext?.Log("Plan", "Build started");
		NamespaceRefactorPendingWriteBuildResult buildResult = buildPendingWriteSet();

		if (!buildResult.Success)
		{
			diagnosticContext?.Log(
				"Cancellation",
				"Cancelled during Plan; PlanBuilt=false; WritesStarted=false"
			);
			return false;
		}

		diagnosticContext?.Log(
			"Plan",
			() =>
				$"Build succeeded; PendingWriteCount={buildResult.WriteSet?.PendingWrites.Count ?? 0}; SelectedScriptPath='{buildResult.WriteSet?.SelectedScriptPath ?? ""}'"
		);
		return _applyCoordinator.ApplySingleReplacement(
			editorInterface,
			scriptEditor,
			buildResult.WriteSet,
			buildPendingWriteSet,
			diagnosticContext
		);
	}

	internal bool ExecuteAddNamespace(
		IEnumerable<string> targetScriptPaths,
		HashSet<string> requiredPaths,
		string newNamespace,
		string operationName,
		bool activateAndSyncSelection,
		NamespaceRefactorDiagnosticContext diagnosticContext
	)
	{
		diagnosticContext?.Log(
			"Request",
			() => $"Execution entered; Operation={operationName}; ActivateAndSyncSelection={activateAndSyncSelection}"
		);

		if (
			!_preflightCoordinator.PreflightAddNamespace(
				targetScriptPaths,
				requiredPaths,
				operationName,
				activateAndSyncSelection,
				diagnosticContext
			)
		)
		{
			return false;
		}

		diagnosticContext?.Log("Plan", "Build started");
		NamespaceRefactorPendingWriteBuildResult buildResult =
			_planBuildCoordinator.BuildAddNamespace(
				targetScriptPaths,
				newNamespace,
				diagnosticContext
			);

		if (!buildResult.Success)
		{
			diagnosticContext?.Log(
				"Cancellation",
				"Cancelled during Plan; PlanBuilt=false; WritesStarted=false"
			);
			return false;
		}

		diagnosticContext?.Log(
			"Plan",
			() => $"Build succeeded; PendingWriteCount={buildResult.WriteSet?.PendingWrites.Count ?? 0}"
		);
		return _applyCoordinator.ApplyPendingWriteOperation(
			buildResult.WriteSet,
			operationName,
			"",
			activateAndSyncSelection,
			rebuildAfterAutosave: null,
			diagnosticContext: diagnosticContext
		);
	}

	internal bool ExecuteBatchReplacement(
		IEnumerable<string> targetScriptPaths,
		IEnumerable<string> candidatePaths,
		HashSet<string> requiredPaths,
		string oldNamespace,
		string newNamespace,
		Func<IReadOnlyList<string>> buildReferenceCandidatePaths,
		Func<IReadOnlyList<string>> buildDeclarationCandidatePaths,
		NamespaceRefactorDiagnosticContext diagnosticContext
	)
	{
		diagnosticContext?.Log("Request", "Execution entered; Operation=BatchReplacement");

		if (
			!_preflightCoordinator.PreflightBatchReplacement(
				candidatePaths,
				requiredPaths,
				oldNamespace,
				diagnosticContext
			)
		)
		{
			return false;
		}

		Func<NamespaceRefactorPendingWriteBuildResult> buildPendingWriteSet = () =>
		{
			IReadOnlyList<string> referenceCandidatePaths =
				buildReferenceCandidatePaths();
			IReadOnlyList<string> declarationCandidatePaths =
				buildDeclarationCandidatePaths();

			return _planBuildCoordinator.BuildBatchReplacement(
				targetScriptPaths,
				referenceCandidatePaths,
				declarationCandidatePaths,
				oldNamespace,
				newNamespace,
				diagnosticContext
			);
		};
		diagnosticContext?.Log("Plan", "Build started");
		NamespaceRefactorPendingWriteBuildResult buildResult = buildPendingWriteSet();

		if (!buildResult.Success)
		{
			diagnosticContext?.Log(
				"Cancellation",
				"Cancelled during Plan; PlanBuilt=false; WritesStarted=false"
			);
			return false;
		}

		diagnosticContext?.Log(
			"Plan",
			() => $"Build succeeded; PendingWriteCount={buildResult.WriteSet?.PendingWrites.Count ?? 0}"
		);
		return _applyCoordinator.ApplyPendingWriteOperation(
			buildResult.WriteSet,
			"Refactor Namespace Batch",
			"",
			false,
			buildPendingWriteSet,
			diagnosticContext
		);
	}
}
#endif
