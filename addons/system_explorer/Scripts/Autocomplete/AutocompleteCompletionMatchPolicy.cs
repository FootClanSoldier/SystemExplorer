#if TOOLS
using System;
using System.Collections.Generic;

namespace SystemExplorer.Autocomplete;

internal sealed class AutocompleteCompletionMatchPolicy
{
	internal bool CanRemainAvailable(
		IReadOnlyList<AutocompleteCompletionItem> items,
		string prefix
	)
	{
		if (string.IsNullOrWhiteSpace(prefix) || items == null)
			return false;

		bool hasAnyMatchingItem = false;
		bool hasStrictlyLongerMatchingItem = false;

		foreach (AutocompleteCompletionItem item in items)
		{
			string insertText = item?.InsertText ?? "";

			if (!insertText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				continue;

			hasAnyMatchingItem = true;

			if (insertText.Length > prefix.Length)
				hasStrictlyLongerMatchingItem = true;
		}

		return hasAnyMatchingItem && hasStrictlyLongerMatchingItem;
	}
}
#endif
