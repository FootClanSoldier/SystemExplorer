#if TOOLS
using System;
using System.Collections.Generic;

namespace SystemExplorer.CodeService.Autocomplete;

internal sealed class AutocompleteCompletionSession
{
	private readonly IReadOnlyList<AutocompleteCompletionItem> _publishedItems;
	private readonly string _scriptPath;
	private readonly int _line;
	private readonly int _prefixStartColumn;

	internal AutocompleteCompletionSession(
		string scriptPath,
		int line,
		int prefixStartColumn,
		IReadOnlyList<AutocompleteCompletionItem> publishedItems
	)
	{
		if (publishedItems == null)
			throw new ArgumentNullException(nameof(publishedItems));

		_scriptPath = scriptPath ?? "";
		_line = line;
		_prefixStartColumn = prefixStartColumn;
		_publishedItems = new List<AutocompleteCompletionItem>(publishedItems).AsReadOnly();
	}

	internal IReadOnlyList<AutocompleteCompletionItem> PublishedItems => _publishedItems;

	internal bool CanRemainOpen(
		string scriptPath,
		int line,
		int prefixStartColumn,
		string currentPrefix
	)
	{
		if (!string.Equals(_scriptPath, scriptPath ?? "", StringComparison.Ordinal)
			|| _line != line
			|| _prefixStartColumn != prefixStartColumn
			|| currentPrefix == null)
		{
			return false;
		}

		bool hasMatchingItem = false;
		bool hasActionableMatchingItem = false;
		foreach (AutocompleteCompletionItem item in _publishedItems)
		{
			string filterText = item?.FilterText ?? "";
			if (!filterText.StartsWith(currentPrefix, StringComparison.OrdinalIgnoreCase))
				continue;

			hasMatchingItem = true;
			string insertText = item?.InsertText ?? "";
			if (filterText.Length > currentPrefix.Length || insertText.Length > currentPrefix.Length)
				hasActionableMatchingItem = true;
		}

		return currentPrefix.Length == 0
			? hasMatchingItem
			: hasMatchingItem && hasActionableMatchingItem;
	}
}
#endif
