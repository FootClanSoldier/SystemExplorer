#if TOOLS
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using SystemExplorer.Autocomplete.Indexing.Persistence;

namespace SystemExplorer.Autocomplete.Indexing;

internal enum CSharpProjectIndexBuildStatus
{
	Succeeded,
	Cancelled,
	Stale,
	Failed,
}

internal sealed class CSharpProjectIndexBuildResult
{
	internal CSharpProjectIndexBuildResult(
		long generation,
		string reason,
		CSharpProjectIndexBuildStatus status,
		TimeSpan elapsed,
		int inventoriedFileCount,
		int reusedFileCount,
		int reparsedFileCount,
		int retainedAfterReadFailureCount,
		int skippedFileCount,
		int totalTypeCount,
		int syntaxDiagnosticCount,
		string failureDetail,
		IReadOnlyList<string> sampleTypeNames,
		CSharpProjectIndexSnapshot snapshot
	)
		: this(
			generation,
			reason,
			status,
			elapsed,
			inventoriedFileCount,
			reusedFileCount,
			reparsedFileCount,
			retainedAfterReadFailureCount,
			skippedFileCount,
			totalTypeCount,
			syntaxDiagnosticCount,
			CSharpProjectIndexCacheLoadStatus.NotAttempted,
			0,
			0,
			"",
			failureDetail,
			sampleTypeNames,
			snapshot
		)
	{
	}

	internal CSharpProjectIndexBuildResult(
		long generation,
		string reason,
		CSharpProjectIndexBuildStatus status,
		TimeSpan elapsed,
		int inventoriedFileCount,
		int reusedFileCount,
		int reparsedFileCount,
		int retainedAfterReadFailureCount,
		int skippedFileCount,
		int totalTypeCount,
		int syntaxDiagnosticCount,
		CSharpProjectIndexCacheLoadStatus cacheLoadStatus,
		int cacheEntriesRead,
		int cacheEntriesReused,
		string cacheLoadDetail,
		string failureDetail,
		IReadOnlyList<string> sampleTypeNames,
		CSharpProjectIndexSnapshot snapshot
	)
	{
		Generation = generation;
		Reason = reason ?? "";
		Status = status;
		Elapsed = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
		InventoriedFileCount = Math.Max(0, inventoriedFileCount);
		ReusedFileCount = Math.Max(0, reusedFileCount);
		ReparsedFileCount = Math.Max(0, reparsedFileCount);
		RetainedAfterReadFailureCount = Math.Max(0, retainedAfterReadFailureCount);
		SkippedFileCount = Math.Max(0, skippedFileCount);
		TotalTypeCount = Math.Max(0, totalTypeCount);
		SyntaxDiagnosticCount = Math.Max(0, syntaxDiagnosticCount);
		CacheLoadStatus = cacheLoadStatus;
		CacheEntriesRead = Math.Max(0, cacheEntriesRead);
		CacheEntriesReused = Math.Max(0, cacheEntriesReused);
		CacheLoadDetail = NormalizeDetail(cacheLoadDetail, 600);
		FailureDetail = NormalizeDetail(failureDetail, 1000);
		SampleTypeNames = new ReadOnlyCollection<string>(
			(sampleTypeNames ?? Array.Empty<string>())
				.Where(name => !string.IsNullOrWhiteSpace(name))
				.Take(20)
				.ToArray()
		);
		Snapshot = snapshot;
	}

	internal long Generation { get; }
	internal string Reason { get; }
	internal CSharpProjectIndexBuildStatus Status { get; }
	internal TimeSpan Elapsed { get; }
	internal int InventoriedFileCount { get; }
	internal int ReusedFileCount { get; }
	internal int ReparsedFileCount { get; }
	internal int RetainedAfterReadFailureCount { get; }
	internal int SkippedFileCount { get; }
	internal int TotalTypeCount { get; }
	internal int SyntaxDiagnosticCount { get; }
	internal CSharpProjectIndexCacheLoadStatus CacheLoadStatus { get; }
	internal int CacheEntriesRead { get; }
	internal int CacheEntriesReused { get; }
	internal string CacheLoadDetail { get; }
	internal string FailureDetail { get; }
	internal IReadOnlyList<string> SampleTypeNames { get; }
	internal CSharpProjectIndexSnapshot Snapshot { get; }

	internal bool IsSuccessful => Status == CSharpProjectIndexBuildStatus.Succeeded;
	internal bool IsCancelled => Status == CSharpProjectIndexBuildStatus.Cancelled;
	internal bool IsStale => Status == CSharpProjectIndexBuildStatus.Stale;
	internal bool IsFailed => Status == CSharpProjectIndexBuildStatus.Failed;

	internal CSharpProjectIndexBuildResult AsStale(string detail)
	{
		return new CSharpProjectIndexBuildResult(
			Generation,
			Reason,
			CSharpProjectIndexBuildStatus.Stale,
			Elapsed,
			InventoriedFileCount,
			ReusedFileCount,
			ReparsedFileCount,
			RetainedAfterReadFailureCount,
			SkippedFileCount,
			TotalTypeCount,
			SyntaxDiagnosticCount,
			CacheLoadStatus,
			CacheEntriesRead,
			CacheEntriesReused,
			CacheLoadDetail,
			detail,
			SampleTypeNames,
			snapshot: null
		);
	}

	internal string CreateDebugSummary()
	{
		string samples = SampleTypeNames.Count == 0
			? "<none>"
			: string.Join(", ", SampleTypeNames);
		string cacheDetail = string.IsNullOrWhiteSpace(CacheLoadDetail)
			? "<none>"
			: CacheLoadDetail;
		string detail = string.IsNullOrWhiteSpace(FailureDetail)
			? "<none>"
			: FailureDetail;

		return
			$"Generation={Generation}, Status={Status}, Reason='{Reason}', "
			+ $"ElapsedMs={Elapsed.TotalMilliseconds:F1}, Files={InventoriedFileCount}, "
			+ $"Reused={ReusedFileCount}, Reparsed={ReparsedFileCount}, "
			+ $"RetainedAfterReadFailure={RetainedAfterReadFailureCount}, "
			+ $"Skipped={SkippedFileCount}, Types={TotalTypeCount}, "
			+ $"SyntaxDiagnostics={SyntaxDiagnosticCount}, "
			+ $"CacheLoadStatus={CacheLoadStatus}, CacheEntriesRead={CacheEntriesRead}, "
			+ $"CacheEntriesReused={CacheEntriesReused}, "
			+ $"CacheLoadDetail='{cacheDetail}', Samples=[{samples}], Detail='{detail}'";
	}

	private static string NormalizeDetail(string detail, int maximumLength)
	{
		if (string.IsNullOrWhiteSpace(detail))
			return "";

		string normalized = detail.Replace('\r', ' ').Replace('\n', ' ').Trim();
		return normalized.Length <= maximumLength
			? normalized
			: normalized.Substring(0, maximumLength);
	}
}
#endif
