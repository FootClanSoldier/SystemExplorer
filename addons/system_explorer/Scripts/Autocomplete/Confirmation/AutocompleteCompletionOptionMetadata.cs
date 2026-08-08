#if TOOLS
namespace SystemExplorer.Autocomplete.Confirmation;

internal sealed record AutocompleteCompletionOptionMetadata(
	int Version,
	string Owner,
	string Source,
	string Identity,
	string Name,
	string NamespaceName,
	string Qualifier,
	int GenericArity,
	int AvailabilityPriority,
	bool HasSimpleNameConflict,
	bool IsNestedType,
	bool UsesQualifiedInsertion
)
{
	internal const int CurrentVersion = 3;
	internal const string SystemExplorerOwner = "SystemExplorer";
	internal const string ProjectTypeSource = "ProjectType";
}
#endif
