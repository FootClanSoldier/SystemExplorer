#if TOOLS
using Godot;

namespace SystemExplorer.CodeService.Autocomplete;

internal sealed record AutocompleteCompletionItem(
	CodeEdit.CodeCompletionKind Kind,
	string DisplayText,
	string InsertText,
	string FilterText,
	string SortText,
	bool Preselect
);
#endif
