#if TOOLS
namespace SystemExplorer.Autocomplete.Indexing;

internal sealed record CSharpProjectFileDescriptor(
	string ResourcePath,
	string GlobalPath,
	long Length,
	long LastWriteTimeUtcTicks
);
#endif
