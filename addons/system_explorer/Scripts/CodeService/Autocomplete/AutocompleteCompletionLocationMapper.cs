#if TOOLS
using Godot;
using SystemExplorer.CodeService.Completion;

namespace SystemExplorer.CodeService.Autocomplete;

internal static class AutocompleteCompletionLocationMapper
{
	private const int MaxRepresentableParentDepth = 255;

	internal static bool TryMap(
		CodeServiceCompletionSemanticOrigin semanticOrigin,
		int? inheritanceDepth,
		out int location
	)
	{
		location = (int)CodeEdit.CodeCompletionLocation.Other;

		switch (semanticOrigin)
		{
			case CodeServiceCompletionSemanticOrigin.Unknown when inheritanceDepth is null:
			case CodeServiceCompletionSemanticOrigin.FrameworkOrOther when inheritanceDepth is null:
				return true;
			case CodeServiceCompletionSemanticOrigin.Local when inheritanceDepth is null:
				location = (int)CodeEdit.CodeCompletionLocation.Local;
				return true;
			case CodeServiceCompletionSemanticOrigin.CurrentType when inheritanceDepth == 0:
				location = (int)CodeEdit.CodeCompletionLocation.ParentMask;
				return true;
			case CodeServiceCompletionSemanticOrigin.BaseType
				when inheritanceDepth is int depth && depth >= 1:
				if (depth <= MaxRepresentableParentDepth)
				{
					location = (int)CodeEdit.CodeCompletionLocation.ParentMask | depth;
				}
				return true;
			case CodeServiceCompletionSemanticOrigin.OtherUserCode when inheritanceDepth is null:
				location = (int)CodeEdit.CodeCompletionLocation.OtherUserCode;
				return true;
			default:
				return false;
		}
	}
}
#endif
