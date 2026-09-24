#if TOOLS
namespace SystemExplorer.CodeService.Completion;

internal static class CodeServiceCompletionLimits
{
	internal const int MaxRequestBodySizeBytes = 16 * 1024;
	internal const int MaxResponseBodySizeBytes = 4 * 1024 * 1024;
	internal const int MaxPublishedCompletionItems = 256;
	internal const int MaxCompletionPrefixUtf8Bytes = 2048;
	internal const int MaxDisplayTextUtf8Bytes = 2048;
	internal const int MaxInsertTextUtf8Bytes = 4096;
	internal const int MaxFilterTextUtf8Bytes = 2048;
	internal const int MaxSortTextUtf8Bytes = 2048;
	internal const int MaxContainingNamespaceUtf8Bytes = 4096;
	internal const int MaxCompletionValueTypeUtf8Bytes = 4096;
	internal const int MaxCompletionContainingTypeUtf8Bytes = 4096;
	internal const int MaxCompletionPropertyAccessibilityUtf8Bytes = 32;
	internal const int MaxCompletionPropertyAccessorKindUtf8Bytes = 8;
	internal const int MaxCompletionPropertyAccessorAccessibilityUtf8Bytes = 32;
	internal const int MaxCompletionMethodSignatures = 8;
	internal const int MaxCompletionMethodSignatureTotalCount = 4096;
	internal const int MaxCompletionMethodTypeParameters = 64;
	internal const int MaxCompletionMethodParametersPerSignature = 16;
	internal const int MaxCompletionMethodParameterCount = 4096;
	internal const int MaxCompletionMethodSignatureDisplayTextUtf8Bytes = 16 * 1024;
	internal const int MaxCompletionMethodReturnTypeUtf8Bytes = 4096;
	internal const int MaxCompletionMethodTypeParameterUtf8Bytes = 1024;
	internal const int MaxCompletionParameterDisplayTextUtf8Bytes = 4096;
	internal const int MaxCompletionParameterTypeUtf8Bytes = 4096;
	internal const int MaxCompletionParameterNameUtf8Bytes = 1024;
	internal const int MaxNormalizedCompletionTextUtf8Bytes = 1024 * 1024;
	internal const int MaxCompletionLine = 1_000_000;
	internal const int MaxCompletionCharacter = 1_000_000;
	internal const int MaxCompletionResolveRequestBodySizeBytes = 16 * 1024;
	internal const int MaxCompletionResolveResponseBodySizeBytes = 24 * 1024 * 1024;
	internal const int MaxCompletionResolveEditNewTextUtf8Bytes = 4 * 1024 * 1024;
}
#endif
