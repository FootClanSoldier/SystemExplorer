#if TOOLS
#nullable enable annotations
using SystemExplorer.CodeService.Completion;

namespace SystemExplorer.CodeService.Autocomplete.Hint;

internal sealed record AutocompleteHintContent(
	long RequestGeneration,
	int? ServiceKind,
	string DisplayText,
	string? ContainingNamespace,
	string? ValueType,
	CodeServiceCompletionMethodSignatureSet? MethodSignatureSet,
	CodeServiceCompletionPropertyAccessorSet? PropertyAccessorSet);
#endif
