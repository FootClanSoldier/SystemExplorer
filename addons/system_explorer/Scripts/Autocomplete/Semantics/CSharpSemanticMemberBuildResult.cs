#if TOOLS
using System;

namespace SystemExplorer.Autocomplete.Semantics;

internal enum CSharpSemanticMemberBuildStatus
{
	Succeeded,
	Cancelled,
	Stale,
	Failed,
}

internal sealed class CSharpSemanticMemberBuildResult
{
	internal CSharpSemanticMemberBuildResult(
		long projectGeneration,
		long activeDocumentRevision,
		string scriptPath,
		CSharpSemanticMemberBuildStatus status,
		TimeSpan elapsed,
		int memberAccessCount,
		int memberCount,
		bool baseCompilationBuilt,
		int metadataReferenceFailureCount,
		int projectFingerprintMismatchCount,
		string diagnosticDetail,
		string failureDetail,
		CSharpSemanticMemberIndexSnapshot snapshot
	)
	{
		ProjectGeneration = projectGeneration;
		ActiveDocumentRevision = activeDocumentRevision;
		ScriptPath = scriptPath ?? "";
		Status = status;
		Elapsed = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
		MemberAccessCount = Math.Max(0, memberAccessCount);
		MemberCount = Math.Max(0, memberCount);
		BaseCompilationBuilt = baseCompilationBuilt;
		MetadataReferenceFailureCount = Math.Max(0, metadataReferenceFailureCount);
		ProjectFingerprintMismatchCount = Math.Max(0, projectFingerprintMismatchCount);
		DiagnosticDetail = NormalizeDetail(diagnosticDetail, 1000);
		FailureDetail = NormalizeDetail(failureDetail, 5000);
		Snapshot = snapshot;
	}

	internal long ProjectGeneration { get; }
	internal long ActiveDocumentRevision { get; }
	internal string ScriptPath { get; }
	internal CSharpSemanticMemberBuildStatus Status { get; }
	internal TimeSpan Elapsed { get; }
	internal int MemberAccessCount { get; }
	internal int MemberCount { get; }
	internal bool BaseCompilationBuilt { get; }
	internal int MetadataReferenceFailureCount { get; }
	internal int ProjectFingerprintMismatchCount { get; }
	internal string DiagnosticDetail { get; }
	internal string FailureDetail { get; }
	internal CSharpSemanticMemberIndexSnapshot Snapshot { get; }

	internal bool IsSuccessful => Status == CSharpSemanticMemberBuildStatus.Succeeded;
	internal bool IsCancelled => Status == CSharpSemanticMemberBuildStatus.Cancelled;
	internal bool IsFailed => Status == CSharpSemanticMemberBuildStatus.Failed;

	internal string CreateDebugSummary()
	{
		string diagnostics = string.IsNullOrWhiteSpace(DiagnosticDetail)
			? "<none>"
			: DiagnosticDetail;
		string failure = string.IsNullOrWhiteSpace(FailureDetail)
			? "<none>"
			: FailureDetail;

		return
			$"ProjectGeneration={ProjectGeneration}, ActiveRevision={ActiveDocumentRevision}, "
			+ $"Status={Status}, ScriptPath='{ScriptPath}', "
			+ $"ElapsedMs={Elapsed.TotalMilliseconds:F1}, "
			+ $"MemberAccesses={MemberAccessCount}, Members={MemberCount}, "
			+ $"BaseCompilationBuilt={BaseCompilationBuilt}, "
			+ $"MetadataReferenceFailures={MetadataReferenceFailureCount}, "
			+ $"FingerprintMismatches={ProjectFingerprintMismatchCount}, "
			+ $"Diagnostics='{diagnostics}', Detail='{failure}'";
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
