#if TOOLS
using System.Collections.Generic;

namespace SystemExplorer.Autocomplete;

internal interface IAutocompleteCompletionSource
{
	IReadOnlyList<AutocompleteCompletionItem> GetCompletions(
		AutocompleteRequestContext request
	);
}
#endif
