#if TOOLS
namespace SystemExplorer.Autocomplete.Indexing.Persistence;

internal sealed record CSharpProjectIndexCacheWriteRequest(
	long Generation,
	string CachePath,
	CSharpProjectIndexSnapshot Snapshot
);
#endif
