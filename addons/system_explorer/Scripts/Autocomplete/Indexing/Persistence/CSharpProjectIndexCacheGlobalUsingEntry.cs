#if TOOLS
namespace SystemExplorer.Autocomplete.Indexing.Persistence;

internal sealed class CSharpProjectIndexCacheGlobalUsingEntry
{
	public CSharpProjectIndexCacheGlobalUsingEntry() { }

	public int Kind { get; set; }
	public string Name { get; set; } = "";
	public string Alias { get; set; } = "";
	public string ScriptPath { get; set; } = "";
}
#endif
