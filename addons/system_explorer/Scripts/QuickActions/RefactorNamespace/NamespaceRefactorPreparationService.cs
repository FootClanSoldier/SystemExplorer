#if TOOLS
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal sealed class NamespaceRefactorPreparationResult
{
	internal NamespaceRefactorPlanResult PlanResult { get; }
	internal NamespaceSnapshotLoadResult SnapshotLoadResult { get; }
	internal IReadOnlyList<string> TargetPaths { get; }
	internal IReadOnlyList<string> ReferenceCandidatePaths { get; }
	internal IReadOnlyList<string> DeclarationCandidatePaths { get; }
	internal IReadOnlyList<string> MissingTargetPaths { get; }
	internal IReadOnlyList<string> FailedTargetPaths { get; }
	internal bool Success => PlanResult.Success;

	internal NamespaceRefactorPreparationResult(
		NamespaceRefactorPlanResult planResult,
		NamespaceSnapshotLoadResult snapshotLoadResult,
		IEnumerable<string> targetPaths,
		IEnumerable<string> referenceCandidatePaths,
		IEnumerable<string> declarationCandidatePaths
	)
	{
		PlanResult = planResult ?? throw new ArgumentNullException(nameof(planResult));
		SnapshotLoadResult =
			snapshotLoadResult ?? throw new ArgumentNullException(nameof(snapshotLoadResult));
		TargetPaths = CreateReadOnlyList(targetPaths);
		ReferenceCandidatePaths = CreateReadOnlyList(referenceCandidatePaths);
		DeclarationCandidatePaths = CreateReadOnlyList(declarationCandidatePaths);
		MissingTargetPaths = FilterPaths(snapshotLoadResult.MissingPaths, TargetPaths);
		FailedTargetPaths = FilterPaths(snapshotLoadResult.FailedPaths, TargetPaths);
	}

	private static IReadOnlyList<string> FilterPaths(
		IEnumerable<string> paths,
		IEnumerable<string> allowedPaths
	)
	{
		HashSet<string> allowedPathSet = new(
			allowedPaths ?? Array.Empty<string>(),
			StringComparer.OrdinalIgnoreCase
		);
		List<string> result = paths
			?.Where(allowedPathSet.Contains)
			.ToList()
			?? new List<string>();
		return result.AsReadOnly();
	}

	private static IReadOnlyList<string> CreateReadOnlyList(IEnumerable<string> source)
	{
		List<string> copy = source == null ? new List<string>() : new List<string>(source);
		return copy.AsReadOnly();
	}
}

internal sealed class NamespaceRefactorNamespaceScanResult
{
	internal IReadOnlyDictionary<string, string> NamespacesByPath { get; }
	internal IReadOnlyList<string> MissingPaths { get; }
	internal IReadOnlyList<string> FailedPaths { get; }

	internal NamespaceRefactorNamespaceScanResult(
		IDictionary<string, string> namespacesByPath,
		IEnumerable<string> missingPaths,
		IEnumerable<string> failedPaths
	)
	{
		Dictionary<string, string> namespaceCopy = new(StringComparer.OrdinalIgnoreCase);

		if (namespacesByPath != null)
		{
			foreach (KeyValuePair<string, string> pair in namespacesByPath)
				namespaceCopy[pair.Key] = pair.Value;
		}

		NamespacesByPath = new ReadOnlyDictionary<string, string>(namespaceCopy);
		MissingPaths = CreateReadOnlyList(missingPaths);
		FailedPaths = CreateReadOnlyList(failedPaths);
	}

	private static IReadOnlyList<string> CreateReadOnlyList(IEnumerable<string> source)
	{
		List<string> copy = source == null ? new List<string>() : new List<string>(source);
		return copy.AsReadOnly();
	}
}

internal sealed class NamespaceRefactorPreparationService
{
	private readonly NamespaceRefactorScopeResolver _scopeResolver;
	private readonly NamespaceRefactorSnapshotLoader _snapshotLoader;

	internal NamespaceRefactorPreparationService(
		NamespaceRefactorScopeResolver scopeResolver,
		NamespaceRefactorSnapshotLoader snapshotLoader
	)
	{
		_scopeResolver =
			scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
		_snapshotLoader =
			snapshotLoader ?? throw new ArgumentNullException(nameof(snapshotLoader));
	}

	internal NamespaceRefactorPreparationResult PrepareReplace(
		IEnumerable<string> targetPaths,
		IEnumerable<string> referenceCandidatePaths,
		IEnumerable<string> declarationCandidatePaths,
		string oldNamespace,
		string newNamespace
	)
	{
		IReadOnlyList<string> normalizedTargetPaths = _scopeResolver.NormalizeScriptPaths(
			targetPaths
		);
		IReadOnlyList<string> normalizedReferencePaths = _scopeResolver.NormalizeScriptPaths(
			referenceCandidatePaths
		);
		IReadOnlyList<string> normalizedDeclarationPaths =
			_scopeResolver.NormalizeScriptPaths(declarationCandidatePaths);
		IReadOnlyList<string> allPaths = _scopeResolver.CombineScriptPaths(
			normalizedTargetPaths,
			normalizedReferencePaths,
			normalizedDeclarationPaths
		);
		NamespaceSnapshotLoadResult snapshotLoadResult = _snapshotLoader.Load(allPaths);
		IReadOnlyList<NamespaceScriptSnapshot> targetSnapshots = GetSnapshotsInPathOrder(
			normalizedTargetPaths,
			snapshotLoadResult.SnapshotsByPath
		);
		IReadOnlyList<NamespaceScriptSnapshot> referenceSnapshots = GetSnapshotsInPathOrder(
			normalizedReferencePaths,
			snapshotLoadResult.SnapshotsByPath
		);
		IReadOnlyList<NamespaceScriptSnapshot> declarationSnapshots = GetSnapshotsInPathOrder(
			normalizedDeclarationPaths,
			snapshotLoadResult.SnapshotsByPath
		);
		NamespaceRefactorPlanResult planResult = NamespaceRefactorPlanBuilder.BuildReplacePlan(
			targetSnapshots,
			referenceSnapshots,
			declarationSnapshots,
			oldNamespace,
			newNamespace
		);

		return new NamespaceRefactorPreparationResult(
			planResult,
			snapshotLoadResult,
			normalizedTargetPaths,
			normalizedReferencePaths,
			normalizedDeclarationPaths
		);
	}

	internal NamespaceRefactorPreparationResult PrepareAdd(
		IEnumerable<string> targetPaths,
		string newNamespace
	)
	{
		IReadOnlyList<string> normalizedTargetPaths = _scopeResolver.NormalizeScriptPaths(
			targetPaths
		);
		NamespaceSnapshotLoadResult snapshotLoadResult = _snapshotLoader.Load(
			normalizedTargetPaths
		);
		IReadOnlyList<NamespaceScriptSnapshot> targetSnapshots = GetSnapshotsInPathOrder(
			normalizedTargetPaths,
			snapshotLoadResult.SnapshotsByPath
		);
		NamespaceRefactorPlanResult planResult = NamespaceRefactorPlanBuilder.BuildAddPlan(
			targetSnapshots,
			newNamespace
		);

		return new NamespaceRefactorPreparationResult(
			planResult,
			snapshotLoadResult,
			normalizedTargetPaths,
			Array.Empty<string>(),
			Array.Empty<string>()
		);
	}

	internal NamespaceRefactorNamespaceScanResult ScanNamespaces(
		IEnumerable<string> scriptPaths
	)
	{
		IReadOnlyList<string> normalizedScriptPaths = _scopeResolver.NormalizeScriptPaths(
			scriptPaths
		);
		NamespaceSnapshotLoadResult snapshotLoadResult = _snapshotLoader.Load(
			normalizedScriptPaths
		);
		Dictionary<string, string> namespacesByPath = new(StringComparer.OrdinalIgnoreCase);

		foreach (NamespaceScriptSnapshot snapshot in snapshotLoadResult.SnapshotsInRequestOrder)
		{
			namespacesByPath[snapshot.Path] = NamespaceTextRewriter.GetNamespaceFromText(
				snapshot.Text
			);
		}

		return new NamespaceRefactorNamespaceScanResult(
			namespacesByPath,
			snapshotLoadResult.MissingPaths,
			snapshotLoadResult.FailedPaths
		);
	}

	private static IReadOnlyList<NamespaceScriptSnapshot> GetSnapshotsInPathOrder(
		IEnumerable<string> paths,
		IReadOnlyDictionary<string, NamespaceScriptSnapshot> snapshotsByPath
	)
	{
		List<NamespaceScriptSnapshot> result = new();

		if (paths == null || snapshotsByPath == null)
			return result.AsReadOnly();

		foreach (string path in paths)
		{
			if (snapshotsByPath.TryGetValue(path, out NamespaceScriptSnapshot snapshot))
				result.Add(snapshot);
		}

		return result.AsReadOnly();
	}
}
#endif
