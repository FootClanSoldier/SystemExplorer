#if TOOLS
using System.Collections.Generic;

namespace SystemExplorer.Autocomplete.Indexing.Persistence;

internal sealed class CSharpProjectIndexCacheTypeEntry
{
	public CSharpProjectIndexCacheTypeEntry() { }

	public string Name { get; set; } = "";
	public string NamespaceName { get; set; } = "";
	public List<string> ContainingTypeNames { get; set; } = new();
	public string ScriptPath { get; set; } = "";
	public int Kind { get; set; }
	public int GenericArity { get; set; }
	public bool IsPartial { get; set; }
	public bool IsStatic { get; set; }
	public bool IsAbstract { get; set; }
}
#endif
