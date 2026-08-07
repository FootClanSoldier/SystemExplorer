#if TOOLS
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SystemExplorer.Autocomplete.Indexing.Context;

internal sealed record CSharpResolvedCompletionContext
{
	internal CSharpResolvedCompletionContext(
		string currentNamespace,
		IEnumerable<string> importedNamespaces,
		IEnumerable<string> globalImportedNamespaces
	)
	{
		CurrentNamespace = currentNamespace ?? "";
		ImportedNamespaces = CreateSet(importedNamespaces);
		GlobalImportedNamespaces = CreateSet(globalImportedNamespaces);
	}

	internal string CurrentNamespace { get; }
	internal IReadOnlyDictionary<string, byte> ImportedNamespaces { get; }
	internal IReadOnlyDictionary<string, byte> GlobalImportedNamespaces { get; }

	private static IReadOnlyDictionary<string, byte> CreateSet(
		IEnumerable<string> values
	)
	{
		var set = new Dictionary<string, byte>(StringComparer.Ordinal);

		if (values != null)
		{
			foreach (string value in values)
			{
				if (!string.IsNullOrWhiteSpace(value))
					set[value] = 0;
			}
		}

		return new ReadOnlyDictionary<string, byte>(set);
	}
}
#endif
