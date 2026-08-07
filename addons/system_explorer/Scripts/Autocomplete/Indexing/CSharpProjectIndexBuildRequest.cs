#if TOOLS
namespace SystemExplorer.Autocomplete.Indexing;

internal sealed record CSharpProjectIndexBuildRequest(
	long Generation,
	string Reason,
	string GlobalProjectRoot,
	string CachePath,
	CSharpProjectIndexSnapshot PreviousSnapshot
);
#endif
