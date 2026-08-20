#if TOOLS
using System;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.Autocomplete;

internal static class AutocompleteCompletionAnchorPolicy
{
	internal static bool BelongsToSameAnchor(
		string expectedScriptPath,
		AutocompleteRequestKind expectedRequestKind,
		int expectedCaretLine,
		int expectedPrefixStartColumn,
		string currentScriptPath,
		AutocompleteRequestKind currentRequestKind,
		int currentCaretLine,
		int currentPrefixStartColumn
	)
	{
		return expectedRequestKind == currentRequestKind
			&& expectedCaretLine == currentCaretLine
			&& expectedPrefixStartColumn == currentPrefixStartColumn
			&& string.Equals(
				ScriptPathUtility.Normalize(expectedScriptPath),
				ScriptPathUtility.Normalize(currentScriptPath),
				StringComparison.OrdinalIgnoreCase
			);
	}
}
#endif
