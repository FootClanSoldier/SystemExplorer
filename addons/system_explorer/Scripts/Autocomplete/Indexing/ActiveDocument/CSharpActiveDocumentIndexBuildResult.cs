#if TOOLS
using System;

namespace SystemExplorer.Autocomplete.Indexing.ActiveDocument;

internal enum CSharpActiveDocumentIndexBuildStatus
{
	Succeeded,
	Cancelled,
	Stale,
	Failed,
}

internal sealed class CSharpActiveDocumentIndexBuildResult
{
	internal CSharpActiveDocumentIndexBuildResult(
		long revision,
		string reason,
		string scriptPath,
		CSharpActiveDocumentIndexBuildStatus status,
		TimeSpan elapsed,
		int typeCount,
		int syntaxDiagnosticCount,
		string failureDetail,
		CSharpActiveDocumentIndexSnapshot snapshot
	)
	{
		Revision = revision;
		Reason = reason ?? "";
		ScriptPath = scriptPath ?? "";
		Status = status;
		Elapsed = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
		TypeCount = Math.Max(0, typeCount);
		SyntaxDiagnosticCount = Math.Max(0, syntaxDiagnosticCount);
		FailureDetail = NormalizeDetail(failureDetail);
		Snapshot = snapshot;
	}

	internal long Revision { get; }
	internal string Reason { get; }
	internal string ScriptPath { get; }
	internal CSharpActiveDocumentIndexBuildStatus Status { get; }
	internal TimeSpan Elapsed { get; }
	internal int TypeCount { get; }
	internal int SyntaxDiagnosticCount { get; }
	internal string FailureDetail { get; }
	internal CSharpActiveDocumentIndexSnapshot Snapshot { get; }

	internal bool IsSuccessful => Status == CSharpActiveDocumentIndexBuildStatus.Succeeded;
	internal bool IsCancelled => Status == CSharpActiveDocumentIndexBuildStatus.Cancelled;
	internal bool IsFailed => Status == CSharpActiveDocumentIndexBuildStatus.Failed;

	internal CSharpActiveDocumentIndexBuildResult AsStale(string detail)
	{
		return new CSharpActiveDocumentIndexBuildResult(
			Revision,
			Reason,
			ScriptPath,
			CSharpActiveDocumentIndexBuildStatus.Stale,
			Elapsed,
			TypeCount,
			SyntaxDiagnosticCount,
			detail,
			snapshot: null
		);
	}

	internal string CreateDebugSummary()
	{
		string detail = string.IsNullOrWhiteSpace(FailureDetail)
			? "<none>"
			: FailureDetail;

		return
			$"Revision={Revision}, Status={Status}, Reason='{Reason}', "
			+ $"ScriptPath='{ScriptPath}', ElapsedMs={Elapsed.TotalMilliseconds:F1}, "
			+ $"Types={TypeCount}, SyntaxDiagnostics={SyntaxDiagnosticCount}, "
			+ $"Detail='{detail}'";
	}

	private static string NormalizeDetail(string detail)
	{
		if (string.IsNullOrWhiteSpace(detail))
			return "";

		string normalized = detail.Replace('\r', ' ').Replace('\n', ' ').Trim();
		return normalized.Length <= 2600 ? normalized : normalized.Substring(0, 2600);
	}
}
#endif
