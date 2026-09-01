#if TOOLS
namespace SystemExplorer.CodeService.Completion;

internal static class CodeServiceCompletionLimits
{
	internal const int MaxRequestBodySizeBytes = 16 * 1024;
	internal const int MaxResponseBodySizeBytes = 4 * 1024 * 1024;
	internal const int MaxCompletionItems = 1024;
	internal const int MaxDisplayTextUtf8Bytes = 2048;
	internal const int MaxInsertTextUtf8Bytes = 4096;
	internal const int MaxFilterTextUtf8Bytes = 2048;
	internal const int MaxSortTextUtf8Bytes = 2048;
	internal const int MaxNormalizedCompletionTextUtf8Bytes = 1024 * 1024;
	internal const int MaxCompletionLine = 1_000_000;
	internal const int MaxCompletionCharacter = 1_000_000;
}
#endif
