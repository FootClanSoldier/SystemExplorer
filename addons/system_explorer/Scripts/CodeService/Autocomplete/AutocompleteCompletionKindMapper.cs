#if TOOLS
using Godot;

namespace SystemExplorer.CodeService.Autocomplete;

internal static class AutocompleteCompletionKindMapper
{
	internal static CodeEdit.CodeCompletionKind Map(int? lspKind)
	{
		return lspKind switch
		{
			2 or 3 or 4 or 24 => CodeEdit.CodeCompletionKind.Function,
			5 or 10 => CodeEdit.CodeCompletionKind.Member,
			6 => CodeEdit.CodeCompletionKind.Variable,
			7 or 8 or 22 => CodeEdit.CodeCompletionKind.Class,
			13 or 20 => CodeEdit.CodeCompletionKind.Enum,
			21 => CodeEdit.CodeCompletionKind.Constant,
			14 => CodeEdit.CodeCompletionKind.PlainText,
			17 or 19 => CodeEdit.CodeCompletionKind.FilePath,
			_ => CodeEdit.CodeCompletionKind.PlainText,
		};
	}
}
#endif
