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
		if (prefix == null || items == null)
			return false;

		bool hasAnyMatchingItem = false;
		bool hasStrictlyLongerMatchingItem = false;

		foreach (AutocompleteCompletionItem item in items)
		{
			string matchText = item?.MatchText ?? "";
			if (string.IsNullOrEmpty(matchText))
				continue;

			if (!matchText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				continue;

			hasAnyMatchingItem = true;

			if (matchText.Length > prefix.Length)
				hasStrictlyLongerMatchingItem = true;
		}

		return hasAnyMatchingItem && hasStrictlyLongerMatchingItem;
	}
}
#endif
