#if TOOLS
using System.Collections.Generic;

namespace SystemExplorer.Autocomplete.Indexing.Persistence;

internal sealed class CSharpProjectIndexCacheFileEntry
{
	private List<CSharpProjectIndexCacheGlobalUsingEntry> _globalUsings;

	public CSharpProjectIndexCacheFileEntry() { }

	public string ResourcePath { get; set; } = "";
	public long Length { get; set; }
	public long LastWriteTimeUtcTicks { get; set; }
	public int SyntaxDiagnosticCount { get; set; }
	public List<CSharpProjectIndexCacheTypeEntry> Types { get; set; } = new();
	public List<CSharpProjectIndexCacheGlobalUsingEntry> GlobalUsings
	{
		get => _globalUsings;
		set
		{
			_globalUsings = value;
			HasGlobalUsingsProperty = true;
		}
	}

	internal bool HasGlobalUsingsProperty { get; private set; }
}
#endif
