#if TOOLS
using System;
using System.Collections.Generic;

namespace SystemExplorer.Autocomplete.Indexing.Persistence;

internal sealed class CSharpProjectIndexCacheDocument
{
	public CSharpProjectIndexCacheDocument() { }

	public int CacheFormatVersion { get; set; }
	public string ParseProfile { get; set; } = "";
	public DateTime CreatedUtc { get; set; }
	public List<CSharpProjectIndexCacheFileEntry> Files { get; set; } = new();
}
#endif
