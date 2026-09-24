#if TOOLS
#nullable enable annotations
using Godot;
using System;
using SystemExplorer.CodeService.Completion;

namespace SystemExplorer.CodeService.Autocomplete;

internal sealed record AutocompleteCompletionItem(
	CodeEdit.CodeCompletionKind Kind,
	int? ServiceKind,
	string DisplayText,
	string? InsertText,
	string FilterText,
	string SortText,
	bool Preselect,
	CodeServiceCompletionSemanticOrigin SemanticOrigin,
	int? InheritanceDepth,
	string? ContainingNamespace,
	string? ValueType,
	string? ContainingType,
	CodeServiceCompletionMethodSignatureSet? MethodSignatureSet,
	CodeServiceCompletionPropertyAccessorSet? PropertyAccessorSet,
	string? NamespaceDisambiguation,
	bool RequiresImport,
	Guid? CompletionHandle)
{
	internal bool HasValidCommitContract => RequiresImport
		? InsertText == null && CompletionHandle.HasValue && CompletionHandle.Value != Guid.Empty
		: !string.IsNullOrEmpty(InsertText) && !CompletionHandle.HasValue;

	internal bool HasValidNamespaceContract => NamespaceDisambiguation == null
		|| (ContainingNamespace != null
			&& string.Equals(NamespaceDisambiguation, ContainingNamespace, StringComparison.Ordinal));
}
#endif
