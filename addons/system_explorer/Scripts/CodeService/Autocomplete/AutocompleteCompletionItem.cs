#if TOOLS
using Godot;
using SystemExplorer.CodeService.Completion;

namespace SystemExplorer.CodeService.Autocomplete;

internal sealed record AutocompleteCompletionItem(
	CodeEdit.CodeCompletionKind Kind,
	string DisplayText,
	string InsertText,
	string FilterText,
	string SortText,
	bool Preselect,
	CodeServiceCompletionSemanticOrigin SemanticOrigin,
	int? InheritanceDepth
);
#endif
