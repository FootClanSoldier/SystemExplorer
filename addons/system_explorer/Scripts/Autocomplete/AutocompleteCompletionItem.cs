#if TOOLS
using Godot;
using SystemExplorer.Autocomplete.Confirmation;

namespace SystemExplorer.Autocomplete;

internal sealed record AutocompleteCompletionItem(
	CodeEdit.CodeCompletionKind Kind,
	string DisplayText,
	string InsertText,
	AutocompleteCompletionOptionMetadata Metadata = null
);
#endif
