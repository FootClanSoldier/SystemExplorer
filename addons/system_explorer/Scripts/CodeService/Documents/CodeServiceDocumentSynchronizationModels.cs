#if TOOLS
using System;
using System.Collections.Generic;
using SystemExplorer.CodeService.Client;

namespace SystemExplorer.CodeService.Documents;

internal enum CodeServiceDocumentOutcome
{
	Success,
	AlreadyCurrent,
	InvalidRequest,
	VersionMismatch,
	Busy,
	WorkspaceUnavailable,
	RoslynUnavailable,
	StaleEpoch,
	EpochConflict,
	StaleVersion,
	VersionConflict,
	DocumentNotOpen,
	DocumentNotInWorkspace,
	CapacityExceeded,
	Unavailable,
	AuthenticationFailed,
	TransportUnavailable,
	MalformedResponse,
	DocumentSynchronizationUnavailableForSession,
	StaleSession,
	Disposed,
	LocalInvalidRequest,
	LocalCapacityExceeded,
}

internal readonly record struct CodeServiceDocumentEpochResult(
	CodeServiceDocumentOutcome Outcome,
	string RequestId,
	long? ClientGeneration,
	string EpochId,
	long? WorkspaceGeneration,
	long? WorkspacePublicationVersion,
	long? RoslynGeneration,
	int DeclaredOpenDocumentCount,
	int RetainedDocumentCount,
	int ClosedDocumentCount,
	string Detail)
{
	internal bool IsAccepted =>
		Outcome is CodeServiceDocumentOutcome.Success or CodeServiceDocumentOutcome.AlreadyCurrent;

	internal static CodeServiceDocumentEpochResult Failure(
		CodeServiceDocumentOutcome outcome,
		string detail
	) => new(outcome, "", null, "", null, null, null, 0, 0, 0, detail ?? "");
}

internal readonly record struct CodeServiceDocumentSnapshotResult(
	CodeServiceDocumentOutcome Outcome,
	string RequestId,
	long? ClientGeneration,
	string EpochId,
	string DocumentPath,
	long? AcceptedClientVersion,
	long? WorkspaceGeneration,
	long? WorkspacePublicationVersion,
	long? RoslynGeneration,
	int? RoslynDocumentVersion,
	string Detail)
{
	internal bool IsAccepted =>
		Outcome is CodeServiceDocumentOutcome.Success or CodeServiceDocumentOutcome.AlreadyCurrent;

	internal static CodeServiceDocumentSnapshotResult Failure(
		CodeServiceDocumentOutcome outcome,
		string detail
	) => new(outcome, "", null, "", "", null, null, null, null, null, detail ?? "");
}

internal readonly record struct CodeServiceDocumentSnapshot(
	string DocumentPath,
	long ClientVersion,
	string Text,
	int Utf8Bytes);

internal readonly record struct CodeServiceDocumentCompletionAdmissionSnapshot(
	long ClientGeneration,
	string EpochId,
	string DocumentPath,
	long ClientVersion,
	long AcceptedClientVersion,
	CodeServiceClientSessionInfo Session,
	bool IsCurrentVersionSynchronized);

internal sealed record CodeServiceDocumentFlightPlan(
	CodeServiceClientSessionInfo Session,
	string ProjectRoot,
	long ClientGeneration,
	string EpochId,
	bool ReconcileEpoch,
	IReadOnlyList<string> OpenDocumentPaths,
	IReadOnlyList<CodeServiceDocumentSnapshot> Snapshots);

internal readonly record struct CodeServiceDocumentFlightResult(
	string SessionId,
	int ServiceProcessId,
	long ServiceStartTimeUtcTicks,
	CodeServiceDocumentOutcome TerminalOutcome,
	bool HasPendingWork,
	bool RetryAfterQuietWindow,
	bool CompositionFailedClosed,
	bool RequestedSessionRecovery,
	int SentSnapshotCount,
	string Detail);
#endif
