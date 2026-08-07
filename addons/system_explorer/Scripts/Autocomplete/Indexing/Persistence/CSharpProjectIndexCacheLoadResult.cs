#if TOOLS
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SystemExplorer.Autocomplete.Indexing.Persistence;

internal enum CSharpProjectIndexCacheLoadStatus
{
	NotAttempted,
	Missing,
	Loaded,
	Ignored,
}

internal sealed class CSharpProjectIndexCacheLoadResult
{
	private CSharpProjectIndexCacheLoadResult(
		CSharpProjectIndexCacheLoadStatus status,
		IReadOnlyDictionary<string, CSharpFileIndexEntry> seedEntriesByResourcePath,
		int entriesRead,
		string detail
	)
	{
		var entryCopy = new Dictionary<string, CSharpFileIndexEntry>(
			StringComparer.OrdinalIgnoreCase
		);

		if (seedEntriesByResourcePath != null)
		{
			foreach (
				KeyValuePair<string, CSharpFileIndexEntry> pair in seedEntriesByResourcePath
			)
			{
				if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value != null)
					entryCopy[pair.Key] = pair.Value;
			}
		}

		Status = status;
		SeedEntriesByResourcePath = new ReadOnlyDictionary<string, CSharpFileIndexEntry>(
			entryCopy
		);
		EntriesRead = Math.Max(0, entriesRead);
		Detail = NormalizeDetail(detail);
	}

	internal CSharpProjectIndexCacheLoadStatus Status { get; }
	internal IReadOnlyDictionary<string, CSharpFileIndexEntry> SeedEntriesByResourcePath { get; }
	internal int EntriesRead { get; }
	internal string Detail { get; }
	internal bool IsLoaded => Status == CSharpProjectIndexCacheLoadStatus.Loaded;

	internal static CSharpProjectIndexCacheLoadResult NotAttempted()
	{
		return new CSharpProjectIndexCacheLoadResult(
			CSharpProjectIndexCacheLoadStatus.NotAttempted,
			seedEntriesByResourcePath: null,
			entriesRead: 0,
			detail: ""
		);
	}

	internal static CSharpProjectIndexCacheLoadResult Missing()
	{
		return new CSharpProjectIndexCacheLoadResult(
			CSharpProjectIndexCacheLoadStatus.Missing,
			seedEntriesByResourcePath: null,
			entriesRead: 0,
			detail: ""
		);
	}

	internal static CSharpProjectIndexCacheLoadResult Loaded(
		IReadOnlyDictionary<string, CSharpFileIndexEntry> seedEntriesByResourcePath
	)
	{
		int entryCount = seedEntriesByResourcePath?.Count ?? 0;
		return new CSharpProjectIndexCacheLoadResult(
			CSharpProjectIndexCacheLoadStatus.Loaded,
			seedEntriesByResourcePath,
			entryCount,
			detail: ""
		);
	}

	internal static CSharpProjectIndexCacheLoadResult Ignored(string detail)
	{
		return new CSharpProjectIndexCacheLoadResult(
			CSharpProjectIndexCacheLoadStatus.Ignored,
			seedEntriesByResourcePath: null,
			entriesRead: 0,
			detail: detail
		);
	}

	private static string NormalizeDetail(string detail)
	{
		if (string.IsNullOrWhiteSpace(detail))
			return "";

		string normalized = detail.Replace('\r', ' ').Replace('\n', ' ').Trim();
		return normalized.Length <= 500 ? normalized : normalized.Substring(0, 500);
	}
}
#endif
