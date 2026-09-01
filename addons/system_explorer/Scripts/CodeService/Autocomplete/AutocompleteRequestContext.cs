#if TOOLS
namespace SystemExplorer.CodeService.Autocomplete;

internal sealed record AutocompleteRequestContext(
	string ScriptPath,
	string Prefix,
	int Line,
	int GodotCaretColumn,
	int LspCharacter,
	int PrefixStartColumn,
	ulong CodeEditInstanceId,
	long ValidationGeneration,
	long RequestGeneration
);
#endif
