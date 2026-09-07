#if TOOLS
#nullable enable annotations
using System;
using System.Collections.Generic;

namespace SystemExplorer.CodeService.Completion;

internal enum CodeServiceCompletionOutcome
{
	Success,
	InvalidRequest,
	VersionMismatch,
	Busy,
	WorkspaceUnavailable,
	RoslynUnavailable,
	SemanticUnavailable,
	CompletionUnavailable,
	StaleEpoch,
	EpochConflict,
	StaleVersion,
	DocumentNotSynchronized,
	DocumentNotOpen,
	DocumentNotInWorkspace,
	Unavailable,
	CompletionUnavailableForSession,
	AuthenticationFailed,
	TransportUnavailable,
	MalformedResponse,
	StaleSession,
	Disposed,
	LocalInvalidRequest,
}

internal readonly record struct CodeServiceCompletionRequest(
	long ClientGeneration,
	string EpochId,
	string DocumentPath,
	long ClientVersion,
	int Line,
	int Character,
	string Prefix);

internal enum CodeServiceCompletionSemanticOrigin
{
	Unknown,
	Local,
	CurrentType,
	BaseType,
	OtherUserCode,
	FrameworkOrOther,
}

internal sealed record CodeServiceCompletionItem(
	int? Kind,
	string DisplayText,
	string? InsertText,
	string FilterText,
	string SortText,
	bool Preselect,
	CodeServiceCompletionSemanticOrigin SemanticOrigin,
	int? InheritanceDepth,
	bool RequiresImport,
	Guid? CompletionHandle)
{
	internal bool HasValidCommitContract => RequiresImport
		? InsertText == null && CompletionHandle.HasValue && CompletionHandle.Value != Guid.Empty
		: !string.IsNullOrEmpty(InsertText) && !CompletionHandle.HasValue;
}

internal readonly record struct CodeServiceCompletionResult(
	CodeServiceCompletionOutcome Outcome,
	string RequestId,
	long? ClientGeneration,
	string EpochId,
	string DocumentPath,
	long? AcceptedClientVersion,
	long? WorkspaceGeneration,
	long? WorkspacePublicationVersion,
	long? RoslynGeneration,
	int? RoslynDocumentVersion,
	long? RoslynOverlayRevision,
	bool IsIncomplete,
	IReadOnlyList<CodeServiceCompletionItem> Items,
	string Detail)
{
	internal bool IsSuccess => Outcome == CodeServiceCompletionOutcome.Success;

	internal static CodeServiceCompletionResult Failure(
		CodeServiceCompletionOutcome outcome,
		string detail
	) => new(
		outcome,
		"",
		null,
		"",
		"",
		null,
		null,
		null,
		null,
		null,
		null,
		false,
		Array.Empty<CodeServiceCompletionItem>(),
		detail ?? ""
	);
}
#endif
