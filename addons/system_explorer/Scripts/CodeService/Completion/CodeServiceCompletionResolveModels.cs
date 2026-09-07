#if TOOLS
using System;
using System.Collections.Generic;

namespace SystemExplorer.CodeService.Completion;

internal enum CodeServiceCompletionResolveOutcome
{
	Success,
	InvalidRequest,
	VersionMismatch,
	Busy,
	WorkspaceUnavailable,
	RoslynUnavailable,
	CompletionUnavailable,
	CompletionExpired,
	StaleEpoch,
	EpochConflict,
	StaleVersion,
	DocumentNotSynchronized,
	DocumentNotOpen,
	DocumentNotInWorkspace,
	Unavailable,
	CompletionResolveUnavailableForSession,
	AuthenticationFailed,
	TransportUnavailable,
	MalformedResponse,
	StaleSession,
	Disposed,
	LocalInvalidRequest,
}

internal readonly record struct CodeServiceCompletionResolveRequest(
	long ClientGeneration,
	string EpochId,
	string DocumentPath,
	long ClientVersion,
	Guid CompletionHandle);

internal readonly record struct CodeServiceCompletionTextPosition(int Line, int Character);

internal readonly record struct CodeServiceCompletionTextRange(
	CodeServiceCompletionTextPosition Start,
	CodeServiceCompletionTextPosition End);

internal sealed record CodeServiceCompletionTextEdit(
	CodeServiceCompletionTextRange Range,
	string NewText);

internal readonly record struct CodeServiceCompletionResolveResult(
	CodeServiceCompletionResolveOutcome Outcome,
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
	IReadOnlyList<CodeServiceCompletionTextEdit> Edits,
	string Detail)
{
	internal bool IsSuccess => Outcome == CodeServiceCompletionResolveOutcome.Success;

	internal static CodeServiceCompletionResolveResult Failure(
		CodeServiceCompletionResolveOutcome outcome,
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
		Array.Empty<CodeServiceCompletionTextEdit>(),
		detail ?? ""
	);
}
#endif
