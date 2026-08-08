#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using SystemExplorer.Autocomplete.Confirmation;
using SystemExplorer.Autocomplete.Indexing;
using SystemExplorer.Autocomplete.Indexing.ActiveDocument;
using SystemExplorer.Autocomplete.Indexing.Context;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.Autocomplete;

internal sealed class ProjectTypeCompletionSource : IAutocompleteCompletionSource
{
	private static readonly IReadOnlyList<AutocompleteCompletionItem> EmptyItems =
		Array.Empty<AutocompleteCompletionItem>();

	private readonly Func<CSharpProjectIndexSnapshot> _projectSnapshotProvider;
	private readonly Func<CSharpActiveDocumentIndexSnapshot> _activeDocumentSnapshotProvider;
	private readonly CSharpCompletionContextResolver _contextResolver;

	internal ProjectTypeCompletionSource(
		Func<CSharpProjectIndexSnapshot> projectSnapshotProvider,
		Func<CSharpActiveDocumentIndexSnapshot> activeDocumentSnapshotProvider,
		CSharpCompletionContextResolver contextResolver
	)
	{
		_projectSnapshotProvider =
			projectSnapshotProvider
			?? throw new ArgumentNullException(nameof(projectSnapshotProvider));
		_activeDocumentSnapshotProvider =
			activeDocumentSnapshotProvider
			?? throw new ArgumentNullException(nameof(activeDocumentSnapshotProvider));
		_contextResolver =
			contextResolver ?? throw new ArgumentNullException(nameof(contextResolver));
	}

	public IReadOnlyList<AutocompleteCompletionItem> GetCompletions(
		AutocompleteRequestContext request
	)
	{
		if (request == null || request.Kind != AutocompleteRequestKind.Identifier)
			return EmptyItems;

		CSharpProjectIndexSnapshot projectSnapshot = _projectSnapshotProvider();
		CSharpActiveDocumentIndexSnapshot activeDocumentSnapshot =
			_activeDocumentSnapshotProvider();

		string prefix = request?.Prefix;
		if (string.IsNullOrWhiteSpace(prefix))
			return EmptyItems;

		string requestScriptPath = ScriptPathUtility.Normalize(request?.ScriptPath);
		bool projectSnapshotAvailable =
			projectSnapshot != null && projectSnapshot.HasBuiltAtLeastOnce;
		bool activeDocumentOverlayApplies =
			activeDocumentSnapshot != null
			&& activeDocumentSnapshot.HasBuiltAtLeastOnce
			&& !string.IsNullOrWhiteSpace(activeDocumentSnapshot.ScriptPath)
			&& string.Equals(
				activeDocumentSnapshot.ScriptPath,
				requestScriptPath,
				StringComparison.OrdinalIgnoreCase
			);

		if (!projectSnapshotAvailable && !activeDocumentOverlayApplies)
			return EmptyItems;

		CSharpDocumentCompletionContext documentContext =
			activeDocumentOverlayApplies
				? activeDocumentSnapshot.CompletionContext
				: CSharpDocumentCompletionContext.Empty;
		IReadOnlyList<CSharpGlobalUsingInfo> projectGlobalUsings =
			CreateEffectiveProjectGlobalUsings(
				projectSnapshotAvailable ? projectSnapshot : null,
				requestScriptPath,
				activeDocumentOverlayApplies
			);
		CSharpResolvedCompletionContext resolvedContext = _contextResolver.Resolve(
			documentContext,
			request?.CaretLine ?? -1,
			projectGlobalUsings
		);

		string initialCharacter = prefix.Substring(0, 1);
		var candidatesByIdentity = new Dictionary<string, TypeCandidate>(
			StringComparer.Ordinal
		);

		if (projectSnapshotAvailable)
		{
			foreach (CSharpProjectTypeSymbol type in projectSnapshot.Types)
			{
				bool belongsToActiveScript = IsActiveScriptType(
					type,
					requestScriptPath
				);

				if (activeDocumentOverlayApplies && belongsToActiveScript)
					continue;

				TryAddCandidate(
					type,
					belongsToActiveScript,
					initialCharacter,
					candidatesByIdentity
				);
			}
		}

		if (activeDocumentOverlayApplies)
		{
			foreach (CSharpProjectTypeSymbol type in activeDocumentSnapshot.Types)
			{
				TryAddCandidate(
					type,
					belongsToActiveScript: true,
					initialCharacter,
					candidatesByIdentity
				);
			}
		}

		if (candidatesByIdentity.Count == 0)
			return EmptyItems;

		var simpleNameCounts = new Dictionary<string, int>(StringComparer.Ordinal);
		var orderedCandidates = candidatesByIdentity.Values.ToList();

		foreach (TypeCandidate candidate in orderedCandidates)
		{
			candidate.Priority = DeterminePriority(candidate, resolvedContext);
			simpleNameCounts.TryGetValue(candidate.Symbol.Name, out int count);
			simpleNameCounts[candidate.Symbol.Name] = count + 1;
		}

		orderedCandidates.Sort(TypeCandidateComparer.Instance);
		var qualifiedInsertionCounts = new Dictionary<string, int>(StringComparer.Ordinal);

		foreach (TypeCandidate candidate in orderedCandidates)
		{
			string qualifiedInsertion = CreateQualifiedTopLevelInsertion(
				candidate.Symbol
			);
			if (string.IsNullOrWhiteSpace(qualifiedInsertion))
				continue;

			qualifiedInsertionCounts.TryGetValue(qualifiedInsertion, out int count);
			qualifiedInsertionCounts[qualifiedInsertion] = count + 1;
		}

		var completionItems = new List<AutocompleteCompletionItem>(
			orderedCandidates.Count
		);

		foreach (TypeCandidate candidate in orderedCandidates)
		{
			CSharpProjectTypeSymbol symbol = candidate.Symbol;
			bool hasSimpleNameConflict = simpleNameCounts[symbol.Name] > 1;
			bool isNestedType = symbol.ContainingTypeNames.Count > 0;
			string qualifiedInsertion = CreateQualifiedTopLevelInsertion(symbol);
			bool usesQualifiedInsertion =
				hasSimpleNameConflict
				&& !isNestedType
				&& !string.IsNullOrWhiteSpace(qualifiedInsertion)
				&& qualifiedInsertionCounts.TryGetValue(
					qualifiedInsertion,
					out int qualifiedInsertionCount
				)
				&& qualifiedInsertionCount == 1;
			string insertText = usesQualifiedInsertion
				? qualifiedInsertion
				: symbol.Name;
			string displayText = CreateDisplayText(symbol, hasSimpleNameConflict);
			string identity = CreateTypeIdentity(symbol);
			string qualifier = CreateQualifier(symbol);
			var metadata = new AutocompleteCompletionOptionMetadata(
				AutocompleteCompletionOptionMetadata.CurrentVersion,
				AutocompleteCompletionOptionMetadata.SystemExplorerOwner,
				AutocompleteCompletionOptionMetadata.ProjectTypeSource,
				identity,
				symbol.Name,
				symbol.NamespaceName,
				qualifier,
				symbol.GenericArity,
				candidate.Priority,
				hasSimpleNameConflict,
				isNestedType,
				usesQualifiedInsertion
			);

			completionItems.Add(
				new AutocompleteCompletionItem(
					MapCompletionKind(symbol.Kind),
					displayText,
					insertText,
					symbol.Name,
					metadata
				)
			);
		}

		return completionItems.AsReadOnly();
	}

	private static void TryAddCandidate(
		CSharpProjectTypeSymbol type,
		bool belongsToActiveScript,
		string initialCharacter,
		Dictionary<string, TypeCandidate> candidatesByIdentity
	)
	{
		if (
			!IsSupportedVisibleProjectType(type)
			|| string.IsNullOrWhiteSpace(type.Name)
			|| !type.Name.StartsWith(
				initialCharacter,
				StringComparison.OrdinalIgnoreCase
			)
		)
		{
			return;
		}

		string identity = CreateTypeIdentity(type);
		if (!candidatesByIdentity.TryGetValue(identity, out TypeCandidate existing))
		{
			candidatesByIdentity.Add(
				identity,
				new TypeCandidate(type, belongsToActiveScript)
			);
			return;
		}

		if (belongsToActiveScript && !existing.BelongsToActiveScript)
		{
			existing.Symbol = type;
			existing.BelongsToActiveScript = true;
		}
	}

	private static bool IsSupportedVisibleProjectType(CSharpProjectTypeSymbol type)
	{
		return type != null && type.ContainingTypeNames.Count == 0;
	}

	private static IReadOnlyList<CSharpGlobalUsingInfo> CreateEffectiveProjectGlobalUsings(
		CSharpProjectIndexSnapshot projectSnapshot,
		string requestScriptPath,
		bool activeDocumentOverlayApplies
	)
	{
		if (projectSnapshot == null)
			return Array.Empty<CSharpGlobalUsingInfo>();

		if (
			!activeDocumentOverlayApplies
			|| string.IsNullOrWhiteSpace(requestScriptPath)
		)
		{
			return projectSnapshot.GlobalUsings;
		}

		var seenNamespaces = new HashSet<string>(StringComparer.Ordinal);
		var effectiveGlobalUsings = new List<CSharpGlobalUsingInfo>();

		foreach (
			CSharpFileIndexEntry fileEntry in projectSnapshot.FilesByResourcePath.Values
				.OrderBy(entry => entry.ResourcePath, StringComparer.OrdinalIgnoreCase)
				.ThenBy(entry => entry.ResourcePath, StringComparer.Ordinal)
		)
		{
			if (
				string.Equals(
					fileEntry.ResourcePath,
					requestScriptPath,
					StringComparison.OrdinalIgnoreCase
				)
			)
			{
				continue;
			}

			foreach (CSharpUsingDirectiveInfo globalUsing in fileEntry.GlobalUsings)
			{
				if (
					globalUsing?.Kind != CSharpUsingDirectiveKind.GlobalNamespace
					|| string.IsNullOrWhiteSpace(globalUsing.Name)
					|| !seenNamespaces.Add(globalUsing.Name)
				)
				{
					continue;
				}

				effectiveGlobalUsings.Add(
					new CSharpGlobalUsingInfo(
						globalUsing.Name,
						fileEntry.ResourcePath
					)
				);
			}
		}

		return effectiveGlobalUsings
			.OrderBy(
				globalUsing => globalUsing.NamespaceName,
				StringComparer.OrdinalIgnoreCase
			)
			.ThenBy(
				globalUsing => globalUsing.NamespaceName,
				StringComparer.Ordinal
			)
			.ToArray();
	}

	private static int DeterminePriority(
		TypeCandidate candidate,
		CSharpResolvedCompletionContext resolvedContext
	)
	{
		if (candidate.BelongsToActiveScript)
			return 0;

		string namespaceName = candidate.Symbol.NamespaceName ?? "";
		if (
			string.IsNullOrEmpty(namespaceName)
			|| string.Equals(
				namespaceName,
				resolvedContext?.CurrentNamespace ?? "",
				StringComparison.Ordinal
			)
		)
		{
			return 1;
		}

		if (
			resolvedContext?.ImportedNamespaces?.ContainsKey(namespaceName)
			== true
		)
		{
			return 2;
		}

		if (
			resolvedContext?.GlobalImportedNamespaces?.ContainsKey(namespaceName)
			== true
		)
		{
			return 3;
		}

		return 4;
	}

	private static string CreateDisplayText(
		CSharpProjectTypeSymbol symbol,
		bool includeQualifier
	)
	{
		if (!includeQualifier)
			return symbol.Name;

		string qualifier = CreateQualifier(symbol);
		return string.IsNullOrWhiteSpace(qualifier)
			? symbol.Name
			: $"{symbol.Name}  {qualifier}";
	}

	private static string CreateQualifiedTopLevelInsertion(
		CSharpProjectTypeSymbol symbol
	)
	{
		if (
			symbol == null
			|| string.IsNullOrWhiteSpace(symbol.Name)
			|| symbol.ContainingTypeNames.Count > 0
		)
		{
			return "";
		}

		return string.IsNullOrWhiteSpace(symbol.NamespaceName)
			? $"global::{symbol.Name}"
			: $"global::{symbol.NamespaceName}.{symbol.Name}";
	}

	private static string CreateQualifier(CSharpProjectTypeSymbol symbol)
	{
		var parts = new List<string>();
		if (!string.IsNullOrWhiteSpace(symbol.NamespaceName))
			parts.Add(symbol.NamespaceName);

		foreach (string containingTypeName in symbol.ContainingTypeNames)
		{
			if (!string.IsNullOrWhiteSpace(containingTypeName))
				parts.Add(containingTypeName);
		}

		return string.Join(".", parts);
	}

	private static string CreateTypeIdentity(CSharpProjectTypeSymbol type)
	{
		return $"{type.NamespaceName}\u001f"
			+ $"{string.Join("\u001e", type.ContainingTypeNames)}\u001f"
			+ $"{type.Name}\u001f{type.GenericArity}";
	}

	private static string CreateFullSortIdentity(CSharpProjectTypeSymbol type)
	{
		string qualifier = CreateQualifier(type);
		return string.IsNullOrWhiteSpace(qualifier)
			? $"{type.Name}`{type.GenericArity}"
			: $"{qualifier}.{type.Name}`{type.GenericArity}";
	}

	private static bool IsActiveScriptType(
		CSharpProjectTypeSymbol type,
		string requestScriptPath
	)
	{
		return type != null
			&& !string.IsNullOrWhiteSpace(requestScriptPath)
			&& string.Equals(
				ScriptPathUtility.Normalize(type.ScriptPath),
				requestScriptPath,
				StringComparison.OrdinalIgnoreCase
			);
	}

	private static CodeEdit.CodeCompletionKind MapCompletionKind(
		CSharpProjectTypeKind kind
	)
	{
		return kind == CSharpProjectTypeKind.Enum
			? CodeEdit.CodeCompletionKind.Enum
			: CodeEdit.CodeCompletionKind.Class;
	}

	private sealed class TypeCandidate
	{
		internal TypeCandidate(
			CSharpProjectTypeSymbol symbol,
			bool belongsToActiveScript
		)
		{
			Symbol = symbol;
			BelongsToActiveScript = belongsToActiveScript;
			FullSortIdentity = CreateFullSortIdentity(symbol);
			Priority = 4;
		}

		internal CSharpProjectTypeSymbol Symbol { get; set; }
		internal string FullSortIdentity { get; }
		internal bool BelongsToActiveScript { get; set; }
		internal int Priority { get; set; }
	}

	private sealed class TypeCandidateComparer : IComparer<TypeCandidate>
	{
		internal static TypeCandidateComparer Instance { get; } = new();

		public int Compare(TypeCandidate left, TypeCandidate right)
		{
			if (ReferenceEquals(left, right))
				return 0;
			if (left == null)
				return -1;
			if (right == null)
				return 1;

			int priorityComparison = left.Priority.CompareTo(right.Priority);
			if (priorityComparison != 0)
				return priorityComparison;

			int ignoreCaseNameComparison = StringComparer.OrdinalIgnoreCase.Compare(
				left.Symbol.Name,
				right.Symbol.Name
			);
			if (ignoreCaseNameComparison != 0)
				return ignoreCaseNameComparison;

			int ordinalNameComparison = StringComparer.Ordinal.Compare(
				left.Symbol.Name,
				right.Symbol.Name
			);
			if (ordinalNameComparison != 0)
				return ordinalNameComparison;

			return StringComparer.Ordinal.Compare(
				left.FullSortIdentity,
				right.FullSortIdentity
			);
		}
	}
}
#endif
