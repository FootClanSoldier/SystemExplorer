#if TOOLS
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal sealed class NamespaceRefactorPlan
{
	internal string SelectedScriptPath { get; }
	internal IReadOnlyDictionary<string, string> OriginalTextsByPath { get; }
	internal IReadOnlyDictionary<string, string> PendingWrites { get; }

	internal NamespaceRefactorPlan(
		string selectedScriptPath,
		IDictionary<string, string> originalTextsByPath,
		IDictionary<string, string> pendingWrites
	)
	{
		SelectedScriptPath = selectedScriptPath ?? "";
		OriginalTextsByPath = CreateReadOnlyPathDictionary(originalTextsByPath);
		PendingWrites = CreateReadOnlyPathDictionary(pendingWrites);
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
		string oldNamespace,
		string newNamespace
	)
	{
		List<NamespaceScriptSnapshot> targets = GetUniqueSnapshots(targetScripts);

		if (targets.Count == 0)
			return NamespaceRefactorPlanResult.Failed(NamespaceRefactorPlanFailure.NoTargetScripts);

		Dictionary<string, string> originalTextsByPath = new(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, string> pendingWrites = new(StringComparer.OrdinalIgnoreCase);
		List<string> namespaceRewriteFailedPaths = new();
		string selectedScriptPath = "";
		string firstTargetNamespace = NamespaceTextRewriter.GetNamespaceFromText(targets[0].Text);

		foreach (NamespaceScriptSnapshot target in targets)
		{
			if (NamespaceTextRewriter.GetNamespaceFromText(target.Text) != oldNamespace)
				continue;

			string updatedText = NamespaceTextRewriter.ReplaceNamespaceDeclaration(
				target.Text,
				oldNamespace,
				newNamespace,
				out bool namespaceChanged
			);

			if (!namespaceChanged)
			{
				namespaceRewriteFailedPaths.Add(target.Path);
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
				NamespaceRefactorPlanFailure.NoMatchingNamespace,
				firstTargetNamespace,
				namespaceRewriteFailedPaths
			);
		}

		HashSet<string> successfullyRewrittenTargetPaths = pendingWrites.Keys.ToHashSet(
			StringComparer.OrdinalIgnoreCase
		);
		List<NamespaceScriptSnapshot> declarationSnapshots = GetUniqueSnapshots(
			CombineSnapshots(namespaceDeclarationCandidates, targets)
		);
		bool oldNamespaceRemainsAfterRefactor = false;

		foreach (NamespaceScriptSnapshot declarationSnapshot in declarationSnapshots)
		{
			if (
				NamespaceTextRewriter.GetNamespaceFromText(declarationSnapshot.Text) == oldNamespace
				&& !successfullyRewrittenTargetPaths.Contains(declarationSnapshot.Path)
			)
			{
				oldNamespaceRemainsAfterRefactor = true;
				break;
			}
		}

		foreach (NamespaceScriptSnapshot candidate in GetUniqueSnapshots(referenceCandidates))
		{
			string textToRewrite = pendingWrites.TryGetValue(candidate.Path, out string pendingText)
				? pendingText
				: candidate.Text;

			bool usingChanged;
			string updatedText = oldNamespaceRemainsAfterRefactor
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

		NamespaceRefactorPlan plan = new(
			selectedScriptPath,
			originalTextsByPath,
			pendingWrites
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

		NamespaceRefactorPlan plan = new(
			selectedScriptPath,
			originalTextsByPath,
			pendingWrites
		);

		return NamespaceRefactorPlanResult.Succeeded(
			plan,
			alreadyNamespacedPaths: alreadyNamespacedPaths,
			namespaceAddFailedPaths: namespaceAddFailedPaths
		);
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
