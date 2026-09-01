#if TOOLS
namespace SystemExplorer.CodeService.Documents;

internal static class CodeServiceDocumentSynchronizationLimits
{
	internal const int MaxTrackedOpenDocuments = 256;
	internal const int MaxDocumentPathLength = 2048;
	internal const int MaxDocumentTextUtf8Bytes = 3 * 1024 * 1024;
	internal const long MaxTotalTrackedSnapshotUtf8Bytes = 64L * 1024 * 1024;
	internal const int MaxEpochRequestBodySizeBytes = 1 * 1024 * 1024;
	internal const int MaxSnapshotRequestBodySizeBytes = 16 * 1024 * 1024;
	internal const int MaxDocumentResponseSizeBytes = 32 * 1024;
}
#endif
