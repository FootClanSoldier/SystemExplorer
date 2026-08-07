#if TOOLS
namespace SystemExplorer.Autocomplete;

internal sealed record AutocompleteRequestContext(
	string ScriptPath,
	string Prefix,
	int CaretLine,
	int CaretColumn
);
#endif
