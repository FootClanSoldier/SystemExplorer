#if TOOLS
using System;

namespace SystemExplorer.Autocomplete;

internal sealed record AutocompleteCompletionSourceRegistration
{
	internal string SourceId { get; }
	internal IAutocompleteCompletionSource Source { get; }

	internal AutocompleteCompletionSourceRegistration(
		string sourceId,
		IAutocompleteCompletionSource source
	)
	{
		SourceId = !string.IsNullOrWhiteSpace(sourceId)
			? sourceId
			: throw new ArgumentException(
				"Completion source ID is required.",
				nameof(sourceId)
			);
		Source = source ?? throw new ArgumentNullException(nameof(source));
	}
}
#endif
