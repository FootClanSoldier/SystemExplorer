#if TOOLS
using System;

namespace SystemExplorer.Autocomplete.Indexing.Persistence;

internal enum CSharpProjectIndexCacheWriteStatus
{
	Succeeded,
	Cancelled,
	Stale,
	Failed,
}

internal sealed class CSharpProjectIndexCacheWriteResult
{
	internal CSharpProjectIndexCacheWriteResult(
		long generation,
		CSharpProjectIndexCacheWriteStatus status,
		TimeSpan elapsed,
		int fileCount,
		int typeCount,
		string failureDetail
	)
	{
		Generation = generation;
		Status = status;
		Elapsed = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
		FileCount = Math.Max(0, fileCount);
		TypeCount = Math.Max(0, typeCount);
		FailureDetail = NormalizeDetail(failureDetail);
	}

	internal long Generation { get; }
	internal CSharpProjectIndexCacheWriteStatus Status { get; }
	internal TimeSpan Elapsed { get; }
	internal int FileCount { get; }
	internal int TypeCount { get; }
	internal string FailureDetail { get; }
	internal bool IsSuccessful => Status == CSharpProjectIndexCacheWriteStatus.Succeeded;
	internal bool IsCancelled => Status == CSharpProjectIndexCacheWriteStatus.Cancelled;
	internal bool IsStale => Status == CSharpProjectIndexCacheWriteStatus.Stale;
	internal bool IsFailed => Status == CSharpProjectIndexCacheWriteStatus.Failed;

	internal CSharpProjectIndexCacheWriteResult AsStale()
	{
		return new CSharpProjectIndexCacheWriteResult(
			Generation,
			CSharpProjectIndexCacheWriteStatus.Stale,
			Elapsed,
			FileCount,
			TypeCount,
			failureDetail: ""
		);
	}

	internal string CreateDebugSummary()
	{
		string detail = string.IsNullOrWhiteSpace(FailureDetail)
			? "<none>"
			: FailureDetail;

		return
			$"Generation={Generation}, Status={Status}, "
			+ $"ElapsedMs={Elapsed.TotalMilliseconds:F1}, Files={FileCount}, "
			+ $"Types={TypeCount}, Detail='{detail}'";
	}

	private static string NormalizeDetail(string detail)
	{
		if (string.IsNullOrWhiteSpace(detail))
			return "";

		string normalized = detail.Replace('\r', ' ').Replace('\n', ' ').Trim();
		return normalized.Length <= 600 ? normalized : normalized.Substring(0, 600);
	}
}
#endif
