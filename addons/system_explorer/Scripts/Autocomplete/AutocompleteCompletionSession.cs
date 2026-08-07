#if TOOLS
using System;
using System.Collections.Generic;

namespace SystemExplorer.Autocomplete;

internal sealed class AutocompleteCompletionSession
{
	private readonly IReadOnlyList<AutocompleteCompletionItem> _publishedItems;
	private readonly AutocompleteCompletionMatchPolicy _matchPolicy;

	internal AutocompleteCompletionSession(
		IReadOnlyList<AutocompleteCompletionItem> publishedItems,
		AutocompleteCompletionMatchPolicy matchPolicy
	)
	{
		if (publishedItems == null)
			throw new ArgumentNullException(nameof(publishedItems));

		_publishedItems = new List<AutocompleteCompletionItem>(publishedItems).AsReadOnly();
		_matchPolicy = matchPolicy ?? throw new ArgumentNullException(nameof(matchPolicy));
	}

	internal bool CanRemainOpen(string currentPrefix)
	{
		return _matchPolicy.CanRemainAvailable(_publishedItems, currentPrefix);
	}
}
#endif
