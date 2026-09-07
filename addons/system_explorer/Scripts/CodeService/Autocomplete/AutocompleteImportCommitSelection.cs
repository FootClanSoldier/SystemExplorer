#if TOOLS
namespace SystemExplorer.CodeService.Autocomplete;

internal sealed record AutocompleteImportCommitSelection(
	ulong CodeEditInstanceId,
	string ScriptPath,
	long RequestGeneration,
	AutocompleteCompletionItem Item,
	AutocompleteCompletionAuthority Authority);
#endif
