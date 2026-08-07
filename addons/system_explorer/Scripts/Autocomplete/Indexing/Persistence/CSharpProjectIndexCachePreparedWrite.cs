#if TOOLS
using System;

namespace SystemExplorer.Autocomplete.Indexing.Persistence;

internal sealed class CSharpProjectIndexCachePreparedWrite
{
	internal CSharpProjectIndexCachePreparedWrite(
		long generation,
		string cachePath,
		string temporaryPath,
		int fileCount,
		int typeCount,
		TimeSpan preparationElapsed
	)
	{
		Generation = generation;
		CachePath = cachePath ?? "";
		TemporaryPath = temporaryPath ?? "";
		FileCount = Math.Max(0, fileCount);
		TypeCount = Math.Max(0, typeCount);
		PreparationElapsed = preparationElapsed < TimeSpan.Zero
			? TimeSpan.Zero
			: preparationElapsed;
	}

	internal long Generation { get; }
	internal string CachePath { get; }
	internal string TemporaryPath { get; }
	internal int FileCount { get; }
	internal int TypeCount { get; }
	internal TimeSpan PreparationElapsed { get; }
}
#endif
