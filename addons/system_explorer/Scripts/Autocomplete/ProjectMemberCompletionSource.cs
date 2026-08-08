#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using SystemExplorer.Autocomplete.Indexing;
using SystemExplorer.Autocomplete.Indexing.ActiveDocument;
using SystemExplorer.Autocomplete.Semantics;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.Autocomplete;

internal sealed class ProjectMemberCompletionSource : IAutocompleteCompletionSource
{
	private static readonly IReadOnlyList<AutocompleteCompletionItem> EmptyItems =
		Array.Empty<AutocompleteCompletionItem>();

	private readonly Func<CSharpSemanticMemberIndexSnapshot> _semanticSnapshotProvider;
	private readonly Func<CSharpProjectIndexSnapshot> _projectSnapshotProvider;
	private readonly Func<CSharpActiveDocumentIndexSnapshot> _activeDocumentSnapshotProvider;

	internal ProjectMemberCompletionSource(
		Func<CSharpSemanticMemberIndexSnapshot> semanticSnapshotProvider,
		Func<CSharpProjectIndexSnapshot> projectSnapshotProvider,
		Func<CSharpActiveDocumentIndexSnapshot> activeDocumentSnapshotProvider
	)
	{
		_semanticSnapshotProvider =
			semanticSnapshotProvider
			?? throw new ArgumentNullException(nameof(semanticSnapshotProvider));
		_projectSnapshotProvider =
			projectSnapshotProvider
			?? throw new ArgumentNullException(nameof(projectSnapshotProvider));
		_activeDocumentSnapshotProvider =
			activeDocumentSnapshotProvider
			?? throw new ArgumentNullException(nameof(activeDocumentSnapshotProvider));
	}

	public IReadOnlyList<AutocompleteCompletionItem> GetCompletions(
		AutocompleteRequestContext request
	)
	{
		if (
			request == null
			|| request.Kind != AutocompleteRequestKind.MemberAccess
			|| request.Prefix == null
		)
		{
			return EmptyItems;
		}

		CSharpSemanticMemberIndexSnapshot semanticSnapshot = _semanticSnapshotProvider();
		CSharpProjectIndexSnapshot projectSnapshot = _projectSnapshotProvider();
		CSharpActiveDocumentIndexSnapshot activeDocumentSnapshot =
			_activeDocumentSnapshotProvider();

		string requestScriptPath = ScriptPathUtility.Normalize(request.ScriptPath);
		if (
			semanticSnapshot == null
			|| !semanticSnapshot.HasBuiltAtLeastOnce
			|| projectSnapshot == null
			|| !projectSnapshot.HasBuiltAtLeastOnce
			|| activeDocumentSnapshot == null
			|| !activeDocumentSnapshot.HasBuiltAtLeastOnce
			|| string.IsNullOrWhiteSpace(requestScriptPath)
			|| !string.Equals(
				semanticSnapshot.ScriptPath,
				requestScriptPath,
				StringComparison.OrdinalIgnoreCase
			)
			|| !string.Equals(
				activeDocumentSnapshot.ScriptPath,
				requestScriptPath,
				StringComparison.OrdinalIgnoreCase
			)
			|| semanticSnapshot.ProjectGeneration != projectSnapshot.Generation
			|| semanticSnapshot.ActiveDocumentRevision != activeDocumentSnapshot.Revision
		)
		{
			return EmptyItems;
		}

		if (
			!semanticSnapshot.TryGetMemberAccess(
				request.CaretLine,
				request.PrefixStartColumn,
				out CSharpSemanticMemberAccess matchingAccess
			)
			|| matchingAccess.Members.Count == 0
		)
		{
			return EmptyItems;
		}

		IEnumerable<CSharpSemanticMemberSymbol> candidateQuery = matchingAccess.Members
			.Where(
				member =>
					member != null
					&& !string.IsNullOrWhiteSpace(member.Name)
			);

		if (request.Prefix.Length > 0)
		{
			string initialCharacter = request.Prefix.Substring(0, 1);
			candidateQuery = candidateQuery.Where(
				member =>
					member.Name.StartsWith(
						initialCharacter,
						StringComparison.OrdinalIgnoreCase
					)
			);
		}

		CSharpSemanticMemberSymbol[] candidates = candidateQuery
			.OrderBy(member => member.Name, StringComparer.OrdinalIgnoreCase)
			.ThenBy(member => member.Name, StringComparer.Ordinal)
			.ThenBy(member => member.Kind)
			.ToArray();

		if (candidates.Length == 0)
			return EmptyItems;

		var items = new List<AutocompleteCompletionItem>(candidates.Length);
		foreach (CSharpSemanticMemberSymbol member in candidates)
		{
			items.Add(
				new AutocompleteCompletionItem(
					MapCompletionKind(member.Kind),
					CreateDisplayText(member),
					member.Name,
					member.Name
				)
			);
		}

		return items.AsReadOnly();
	}

	private static string CreateDisplayText(CSharpSemanticMemberSymbol member)
	{
		return member.Kind == CSharpSemanticMemberKind.Method
			? $"{member.Name}()"
			: member.Name;
	}

	private static CodeEdit.CodeCompletionKind MapCompletionKind(
		CSharpSemanticMemberKind kind
	)
	{
		// Class is the verified Godot 4.6 fallback already used by project types.
		return CodeEdit.CodeCompletionKind.Class;
	}
}
#endif
