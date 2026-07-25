#if TOOLS
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal sealed class NamespaceRefactorReplaceWritePlan
{
	internal string OldNamespace { get; }
	internal string NewNamespace { get; }
	internal IReadOnlyList<string> DeclarationPathsInOrder { get; }
	internal IReadOnlyDictionary<string, string> ConservativeDeclarationWrites { get; }
	internal IReadOnlyDictionary<string, string> ReferenceOriginalTextsByPath { get; }
	internal IReadOnlyList<string> InitiallyIncompleteDeclarationPaths { get; }
	internal IReadOnlyList<string> OldNamespaceRemainingDeclarationPaths { get; }
	internal bool OldNamespaceRemainsWithoutPhysicalWriteFailures =>
		InitiallyIncompleteDeclarationPaths.Count > 0
		|| OldNamespaceRemainingDeclarationPaths.Count > 0;

	internal NamespaceRefactorReplaceWritePlan(
		string oldNamespace,
		string newNamespace,
		IEnumerable<string> declarationPathsInOrder,
		IDictionary<string, string> conservativeDeclarationWrites,
		IDictionary<string, string> referenceOriginalTextsByPath,
		IEnumerable<string> initiallyIncompleteDeclarationPaths,
		IEnumerable<string> oldNamespaceRemainingDeclarationPaths
	)
	{
		OldNamespace = oldNamespace ?? "";
		NewNamespace = newNamespace ?? "";
		DeclarationPathsInOrder = CreateReadOnlyList(declarationPathsInOrder);
		ConservativeDeclarationWrites = CreateReadOnlyPathDictionary(
			conservativeDeclarationWrites
		);
		ReferenceOriginalTextsByPath = CreateReadOnlyPathDictionary(
			referenceOriginalTextsByPath
		);
		InitiallyIncompleteDeclarationPaths = CreateReadOnlyList(
			initiallyIncompleteDeclarationPaths
		);
		OldNamespaceRemainingDeclarationPaths = CreateReadOnlyList(
			oldNamespaceRemainingDeclarationPaths
		);
	}

	private static IReadOnlyDictionary<string, string> CreateReadOnlyPathDictionary(
		IDictionary<string, string> source
	)
	{
		Dictionary<string, string> copy = new(StringComparer.OrdinalIgnoreCase);

		if (source != null)
		{
			foreach (KeyValuePair<string, string> pair in source)
				copy[pair.Key] = pair.Value;
		}

		return new ReadOnlyDictionary<string, string>(copy);
	}

	private static IReadOnlyList<string> CreateReadOnlyList(IEnumerable<string> source)
	{
		List<string> copy = source == null ? new List<string>() : new List<string>(source);
		return copy.AsReadOnly();
	}
}

internal sealed class NamespaceRefactorPlan
{
	internal string SelectedScriptPath { get; }
	internal IReadOnlyDictionary<string, string> OriginalTextsByPath { get; }
	internal IReadOnlyDictionary<string, string> PendingWrites { get; }
	internal NamespaceRefactorReplaceWritePlan ReplaceWritePlan { get; }

	internal NamespaceRefactorPlan(
		string selectedScriptPath,
		IDictionary<string, string> originalTextsByPath,
		IDictionary<string, string> pendingWrites,
		NamespaceRefactorReplaceWritePlan replaceWritePlan = null
	)
	{
		SelectedScriptPath = selectedScriptPath ?? "";
		OriginalTextsByPath = CreateReadOnlyPathDictionary(originalTextsByPath);
		PendingWrites = CreateReadOnlyPathDictionary(pendingWrites);
		ReplaceWritePlan = replaceWritePlan;
	}

	private static IReadOnlyDictionary<string, string> CreateReadOnlyPathDictionary(
		IDictionary<string, string> source
	)
	{
		Dictionary<string, string> copy = new(StringComparer.OrdinalIgnoreCase);

		if (source != null)
		{
			foreach (KeyValuePair<string, string> pair in source)
				copy[pair.Key] = pair.Value;
		}

		return new ReadOnlyDictionary<string, string>(copy);
	}
}

internal enum NamespaceRefactorPlanFailure
{
	None,
	NoTargetScripts,
	NoMatchingNamespace,
	NoChangesProduced,
}

internal sealed class NamespaceRefactorPlanResult
{
	internal NamespaceRefactorPlanFailure Failure { get; }
	internal NamespaceRefactorPlan Plan { get; }
	internal string FirstTargetNamespace { get; }
	internal IReadOnlyList<string> NamespaceRewriteFailedPaths { get; }
	internal IReadOnlyList<string> AlreadyNamespacedPaths { get; }
	internal IReadOnlyList<string> NamespaceAddFailedPaths { get; }
	internal bool Success => Failure == NamespaceRefactorPlanFailure.None && Plan != null;

	private NamespaceRefactorPlanResult(
		NamespaceRefactorPlanFailure failure,
		NamespaceRefactorPlan plan,
		string firstTargetNamespace,
		IEnumerable<string> namespaceRewriteFailedPaths,
		IEnumerable<string> alreadyNamespacedPaths,
		IEnumerable<string> namespaceAddFailedPaths
	)
	{
		Failure = failure;
		Plan = plan;
		FirstTargetNamespace = firstTargetNamespace ?? "";
		NamespaceRewriteFailedPaths = CreateReadOnlyList(namespaceRewriteFailedPaths);
		AlreadyNamespacedPaths = CreateReadOnlyList(alreadyNamespacedPaths);
		NamespaceAddFailedPaths = CreateReadOnlyList(namespaceAddFailedPaths);
	}

	internal static NamespaceRefactorPlanResult Succeeded(
		NamespaceRefactorPlan plan,
		string firstTargetNamespace = "",
		IEnumerable<string> namespaceRewriteFailedPaths = null,
		IEnumerable<string> alreadyNamespacedPaths = null,
		IEnumerable<string> namespaceAddFailedPaths = null
	)
	{
		return new NamespaceRefactorPlanResult(
			NamespaceRefactorPlanFailure.None,
			plan,
			firstTargetNamespace,
			namespaceRewriteFailedPaths,
			alreadyNamespacedPaths,
			namespaceAddFailedPaths
		);
	}

	internal static NamespaceRefactorPlanResult Failed(
		NamespaceRefactorPlanFailure failure,
		string firstTargetNamespace = "",
		IEnumerable<string> namespaceRewriteFailedPaths = null,
		IEnumerable<string> alreadyNamespacedPaths = null,
		IEnumerable<string> namespaceAddFailedPaths = null
	)
	{
		return new NamespaceRefactorPlanResult(
			failure,
			null,
			firstTargetNamespace,
			namespaceRewriteFailedPaths,
			alreadyNamespacedPaths,
			namespaceAddFailedPaths
		);
	}

	private static IReadOnlyList<string> CreateReadOnlyList(IEnumerable<string> source)
	{
		List<string> copy = source == null ? new List<string>() : new List<string>(source);
		return copy.AsReadOnly();
	}
}

internal static class NamespaceRefactorPlanBuilder
{
	internal static NamespaceRefactorPlanResult BuildReplacePlan(
		IEnumerable<NamespaceScriptSnapshot> targetScripts,
		IEnumerable<NamespaceScriptSnapshot> referenceCandidates,
		IEnumerable<NamespaceScriptSnapshot> namespaceDeclarationCandidates,
		IEnumerable<string> requestedTargetPaths,
		string oldNamespace,
		string newNamespace
	)
	{
		List<NamespaceScriptSnapshot> targets = GetUniqueSnapshots(targetScripts);

		if (targets.Count == 0)
			return NamespaceRefactorPlanResult.Failed(NamespaceRefactorPlanFailure.NoTargetScripts);

		Dictionary<string, NamespaceScriptSnapshot> targetsByPath = targets.ToDictionary(
			target => target.Path,
			StringComparer.OrdinalIgnoreCase
		);
		List<string> targetPathsInOrder = GetUniquePaths(requestedTargetPaths);

		if (targetPathsInOrder.Count == 0)
			targetPathsInOrder.AddRange(targets.Select(target => target.Path));

		Dictionary<string, string> originalTextsByPath = new(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, string> conservativeDeclarationWrites = new(
			StringComparer.OrdinalIgnoreCase
		);
		List<string> declarationPathsInOrder = new();
		List<string> initiallyIncompleteDeclarationPaths = new();
		HashSet<string> initiallyIncompleteDeclarationPathSet = new(
			StringComparer.OrdinalIgnoreCase
		);
		List<string> namespaceRewriteFailedPaths = new();
		string selectedScriptPath = "";
		string firstTargetNamespace = NamespaceTextRewriter.GetNamespaceFromText(targets[0].Text);

		foreach (string targetPath in targetPathsInOrder)
		{
			if (!targetsByPath.TryGetValue(targetPath, out NamespaceScriptSnapshot target))
			{
				declarationPathsInOrder.Add(targetPath);
				AddUniquePath(
					initiallyIncompleteDeclarationPaths,
					initiallyIncompleteDeclarationPathSet,
					targetPath
				);
				continue;
			}

			if (NamespaceTextRewriter.GetNamespaceFromText(target.Text) != oldNamespace)
				continue;

			declarationPathsInOrder.Add(target.Path);
			string declarationUpdatedText = NamespaceTextRewriter.ReplaceNamespaceDeclaration(
				target.Text,
				oldNamespace,
				newNamespace,
				out bool namespaceChanged
			);

			if (!namespaceChanged)
			{
				namespaceRewriteFailedPaths.Add(target.Path);
				AddUniquePath(
					initiallyIncompleteDeclarationPaths,
					initiallyIncompleteDeclarationPathSet,
					target.Path
				);
				continue;
			}

			string conservativeText = NamespaceTextRewriter.AddUsingStatementIfMissing(
				declarationUpdatedText,
				newNamespace,
				oldNamespace,
				out _
			);

			if (string.IsNullOrWhiteSpace(selectedScriptPath))
				selectedScriptPath = target.Path;

			originalTextsByPath[target.Path] = target.Text;
			conservativeDeclarationWrites[target.Path] = conservativeText;
		}

		if (conservativeDeclarationWrites.Count == 0)
		{
			return NamespaceRefactorPlanResult.Failed(
				NamespaceRefactorPlanFailure.NoMatchingNamespace,
				firstTargetNamespace,
				namespaceRewriteFailedPaths
			);
		}

		HashSet<string> plannedDeclarationPathSet = conservativeDeclarationWrites.Keys.ToHashSet(
			StringComparer.OrdinalIgnoreCase
		);
		List<NamespaceScriptSnapshot> declarationSnapshots = GetUniqueSnapshots(
			CombineSnapshots(namespaceDeclarationCandidates, targets)
		);
		List<string> oldNamespaceRemainingDeclarationPaths = declarationSnapshots
			.Where(declarationSnapshot =>
				NamespaceTextRewriter.GetNamespaceFromText(declarationSnapshot.Text) == oldNamespace
				&& !plannedDeclarationPathSet.Contains(declarationSnapshot.Path)
			)
			.Select(declarationSnapshot => declarationSnapshot.Path)
			.ToList();
		bool oldNamespaceRemainsWithoutPhysicalWriteFailures =
			oldNamespaceRemainingDeclarationPaths.Count > 0
			|| initiallyIncompleteDeclarationPaths.Count > 0;

		Dictionary<string, string> referenceOriginalTextsByPath = new(
			StringComparer.OrdinalIgnoreCase
		);
		List<NamespaceScriptSnapshot> referencesNeedingUsingRewrite = new();

		foreach (NamespaceScriptSnapshot reference in GetUniqueSnapshots(referenceCandidates))
		{
			NamespaceTextRewriter.AddUsingStatementIfMissing(
				reference.Text,
				newNamespace,
				oldNamespace,
				out bool partialMoveChangesUsing
			);
			NamespaceTextRewriter.ReplaceUsingStatements(
				reference.Text,
				oldNamespace,
				newNamespace,
				out bool fullMoveChangesUsing
			);

			if (!partialMoveChangesUsing && !fullMoveChangesUsing)
				continue;

			referenceOriginalTextsByPath[reference.Path] = reference.Text;
			referencesNeedingUsingRewrite.Add(reference);
		}

		Dictionary<string, string> pendingWrites = new(StringComparer.OrdinalIgnoreCase);

		foreach (KeyValuePair<string, string> declarationWrite in conservativeDeclarationWrites)
		{
			string intendedText = declarationWrite.Value;

			if (!oldNamespaceRemainsWithoutPhysicalWriteFailures)
			{
				intendedText = NamespaceTextRewriter.ReplaceUsingStatements(
					intendedText,
					oldNamespace,
					newNamespace,
					out _
				);
			}

			pendingWrites[declarationWrite.Key] = intendedText;
		}

		foreach (NamespaceScriptSnapshot candidate in referencesNeedingUsingRewrite)
		{
			if (initiallyIncompleteDeclarationPathSet.Contains(candidate.Path))
				continue;

			string textToRewrite = pendingWrites.TryGetValue(candidate.Path, out string pendingText)
				? pendingText
				: candidate.Text;

			bool usingChanged;
			string updatedText = oldNamespaceRemainsWithoutPhysicalWriteFailures
				? NamespaceTextRewriter.AddUsingStatementIfMissing(
					textToRewrite,
					newNamespace,
					oldNamespace,
					out usingChanged
				)
				: NamespaceTextRewriter.ReplaceUsingStatements(
					textToRewrite,
					oldNamespace,
					newNamespace,
					out usingChanged
				);

			if (!usingChanged)
				continue;

			if (!originalTextsByPath.ContainsKey(candidate.Path))
				originalTextsByPath[candidate.Path] = candidate.Text;

			pendingWrites[candidate.Path] = updatedText;
		}

		var replaceWritePlan = new NamespaceRefactorReplaceWritePlan(
			oldNamespace,
			newNamespace,
			declarationPathsInOrder,
			conservativeDeclarationWrites,
			referenceOriginalTextsByPath,
			initiallyIncompleteDeclarationPaths,
			oldNamespaceRemainingDeclarationPaths
		);
		NamespaceRefactorPlan plan = new(
			selectedScriptPath,
			originalTextsByPath,
			pendingWrites,
			replaceWritePlan
		);

		return NamespaceRefactorPlanResult.Succeeded(
			plan,
			firstTargetNamespace,
			namespaceRewriteFailedPaths
		);
	}

	internal static NamespaceRefactorPlanResult BuildAddPlan(
		IEnumerable<NamespaceScriptSnapshot> targetScripts,
		string newNamespace
	)
	{
		List<NamespaceScriptSnapshot> targets = GetUniqueSnapshots(targetScripts);

		if (targets.Count == 0)
			return NamespaceRefactorPlanResult.Failed(NamespaceRefactorPlanFailure.NoTargetScripts);

		Dictionary<string, string> originalTextsByPath = new(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, string> pendingWrites = new(StringComparer.OrdinalIgnoreCase);
		List<string> alreadyNamespacedPaths = new();
		List<string> namespaceAddFailedPaths = new();
		string selectedScriptPath = "";

		foreach (NamespaceScriptSnapshot target in targets)
		{
			if (!string.IsNullOrWhiteSpace(NamespaceTextRewriter.GetNamespaceFromText(target.Text)))
			{
				alreadyNamespacedPaths.Add(target.Path);
				continue;
			}

			string updatedText = NamespaceTextRewriter.AddNamespaceBlock(
				target.Text,
				newNamespace,
				out bool namespaceAdded
			);

			if (!namespaceAdded)
			{
				namespaceAddFailedPaths.Add(target.Path);
				continue;
			}

			if (string.IsNullOrWhiteSpace(selectedScriptPath))
				selectedScriptPath = target.Path;

			originalTextsByPath[target.Path] = target.Text;
			pendingWrites[target.Path] = updatedText;
		}

		if (pendingWrites.Count == 0)
		{
			return NamespaceRefactorPlanResult.Failed(
				NamespaceRefactorPlanFailure.NoChangesProduced,
				alreadyNamespacedPaths: alreadyNamespacedPaths,
				namespaceAddFailedPaths: namespaceAddFailedPaths
			);
		}

		NamespaceRefactorPlan plan = new(selectedScriptPath, originalTextsByPath, pendingWrites);

		return NamespaceRefactorPlanResult.Succeeded(
			plan,
			alreadyNamespacedPaths: alreadyNamespacedPaths,
			namespaceAddFailedPaths: namespaceAddFailedPaths
		);
	}

	private static void AddUniquePath(
		ICollection<string> orderedPaths,
		ISet<string> pathSet,
		string path
	)
	{
		if (string.IsNullOrWhiteSpace(path) || !pathSet.Add(path))
			return;

		orderedPaths.Add(path);
	}

	private static IEnumerable<NamespaceScriptSnapshot> CombineSnapshots(
		IEnumerable<NamespaceScriptSnapshot> first,
		IEnumerable<NamespaceScriptSnapshot> second
	)
	{
		if (first != null)
		{
			foreach (NamespaceScriptSnapshot snapshot in first)
				yield return snapshot;
		}

		if (second != null)
		{
			foreach (NamespaceScriptSnapshot snapshot in second)
				yield return snapshot;
		}
	}

	private static List<string> GetUniquePaths(IEnumerable<string> paths)
	{
		List<string> result = new();
		HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);

		if (paths == null)
			return result;

		foreach (string path in paths)
		{
			if (string.IsNullOrWhiteSpace(path) || !seenPaths.Add(path))
				continue;

			result.Add(path);
		}

		return result;
	}

	private static List<NamespaceScriptSnapshot> GetUniqueSnapshots(
		IEnumerable<NamespaceScriptSnapshot> snapshots
	)
	{
		List<NamespaceScriptSnapshot> result = new();
		HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);

		if (snapshots == null)
			return result;

		foreach (NamespaceScriptSnapshot snapshot in snapshots)
		{
			if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Path))
				continue;

			if (seenPaths.Add(snapshot.Path))
				result.Add(snapshot);
		}

		return result;
	}
}
#endif
