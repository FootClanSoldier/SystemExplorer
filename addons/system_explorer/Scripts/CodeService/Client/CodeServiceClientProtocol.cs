#if TOOLS
namespace SystemExplorer.CodeService.Client;

internal static class CodeServiceClientProtocol
{
	internal const int ProtocolVersion = 1;
	internal const int DescriptorSchemaVersion = 1;
	internal const int HandshakeSchemaVersion = 1;
	internal const int ReadinessSchemaVersion = 1;
	internal const int WorkspaceSchemaVersion = 1;
	internal const int DocumentSynchronizationSchemaVersion = 1;
	internal const int CompletionSchemaVersion = 3;

	internal const string ReadinessRecordType = "codeservice.ready";
	internal const string HandshakePath = "/control/handshake";
	internal const string WorkspaceInitializePath = "/workspace/initialize";
	internal const string WorkspaceStatusPath = "/workspace/status";
	internal const string DocumentEpochPath = "/documents/epoch";
	internal const string DocumentSnapshotPath = "/documents/snapshot";
	internal const string CompletionPath = "/completion";

	internal const string ProtocolVersionHeaderName =
		"X-SystemExplorer-Protocol-Version";
	internal const string SessionIdHeaderName =
		"X-SystemExplorer-Session-Id";
	internal const string RequestIdHeaderName =
		"X-SystemExplorer-Request-Id";

	internal const string HandshakeSuccessOutcome = "Success";
	internal const string HandshakeInvalidRequestOutcome = "InvalidRequest";
	internal const string HandshakeVersionMismatchOutcome = "VersionMismatch";
	internal const string WorkspaceSuccessOutcome = "Success";
	internal const string WorkspaceInvalidRequestOutcome = "InvalidRequest";
	internal const string WorkspaceVersionMismatchOutcome = "VersionMismatch";
	internal const string WorkspaceBusyOutcome = "Busy";
	internal const string WorkspaceMismatchOutcome = "WorkspaceMismatch";
	internal const string WorkspaceUnavailableOutcome = "Unavailable";
	internal const string WorkspaceFaultedOutcome = "Faulted";
	internal const string DocumentSuccessOutcome = "Success";
	internal const string DocumentAlreadyCurrentOutcome = "AlreadyCurrent";
	internal const string DocumentInvalidRequestOutcome = "InvalidRequest";
	internal const string DocumentVersionMismatchOutcome = "VersionMismatch";
	internal const string DocumentBusyOutcome = "Busy";
	internal const string DocumentWorkspaceUnavailableOutcome = "WorkspaceUnavailable";
	internal const string DocumentRoslynUnavailableOutcome = "RoslynUnavailable";
	internal const string DocumentStaleEpochOutcome = "StaleEpoch";
	internal const string DocumentEpochConflictOutcome = "EpochConflict";
	internal const string DocumentStaleVersionOutcome = "StaleVersion";
	internal const string DocumentVersionConflictOutcome = "VersionConflict";
	internal const string DocumentNotOpenOutcome = "DocumentNotOpen";
	internal const string DocumentNotInWorkspaceOutcome = "DocumentNotInWorkspace";
	internal const string DocumentCapacityExceededOutcome = "CapacityExceeded";
	internal const string DocumentUnavailableOutcome = "Unavailable";
	internal const string SemanticUnavailableOutcome = "SemanticUnavailable";
	internal const string DocumentNotSynchronizedOutcome = "DocumentNotSynchronized";
	internal const string CompletionUnavailableOutcome = "CompletionUnavailable";

	internal const int AuthenticationTokenByteCount = 32;
	internal const int AuthenticationTokenBase64Length = 44;
	internal const int MaxDescriptorSizeBytes = 16 * 1024;
	internal const int MaxHandshakeResponseSizeBytes = 16 * 1024;
	internal const int MaxReadinessLineBytes = 16 * 1024;
	internal const int MaxWorkspaceInitializeBodySizeBytes = 8 * 1024;
	internal const int MaxWorkspaceResponseSizeBytes = 16 * 1024;
	internal const int MaxWorkspaceProjectRootLength = 4096;

	internal const string Transport = "http";
	internal const string Address = "127.0.0.1";
}
#endif
