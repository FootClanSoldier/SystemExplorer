#if TOOLS
namespace SystemExplorer.CodeService.Autocomplete;

internal enum AutocompleteManagedCommitShape
{
	ImportWithUsing,
	QualifiedName
}

internal sealed record AutocompleteManagedCommitSelection(
	ulong CodeEditInstanceId,
	string ScriptPath,
	long RequestGeneration,
	AutocompleteCompletionItem Item,
	AutocompleteCompletionAuthority Authority,
	AutocompletePrefixCapture CommitCapture,
	AutocompleteManagedCommitShape CommitShape);
#endif
