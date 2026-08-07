#if TOOLS
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using SystemExplorer.Autocomplete.Indexing.Context;

namespace SystemExplorer.Autocomplete.Indexing;

internal sealed class CSharpProjectIndexSnapshot
{
	internal static CSharpProjectIndexSnapshot Empty { get; } = new(
		generation: 0,
		filesByResourcePath: new Dictionary<string, CSharpFileIndexEntry>(
			StringComparer.OrdinalIgnoreCase
		),
		types: Array.Empty<CSharpProjectTypeSymbol>(),
		hasBuiltAtLeastOnce: false
	);

	internal CSharpProjectIndexSnapshot(
		long generation,
		IReadOnlyDictionary<string, CSharpFileIndexEntry> filesByResourcePath,
		IReadOnlyList<CSharpProjectTypeSymbol> types,
		bool hasBuiltAtLeastOnce
	)
	{
		var fileCopy = new Dictionary<string, CSharpFileIndexEntry>(
			StringComparer.OrdinalIgnoreCase
		);

		if (filesByResourcePath != null)
		{
			foreach (KeyValuePair<string, CSharpFileIndexEntry> pair in filesByResourcePath)
			{
				if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value != null)
					fileCopy[pair.Key] = pair.Value;
			}
		}

		CSharpProjectTypeSymbol[] typeCopy = (
			types ?? Array.Empty<CSharpProjectTypeSymbol>()
		)
			.Where(type => type != null)
			.ToArray();

		CSharpGlobalUsingInfo[] globalUsingCopy = CreateGlobalUsings(fileCopy);

		Generation = generation;
		FilesByResourcePath = new ReadOnlyDictionary<string, CSharpFileIndexEntry>(
			fileCopy
		);
		Types = new ReadOnlyCollection<CSharpProjectTypeSymbol>(typeCopy);
		GlobalUsings = new ReadOnlyCollection<CSharpGlobalUsingInfo>(globalUsingCopy);
		FileCount = fileCopy.Count;
		TypeCount = typeCopy.Length;
		SyntaxDiagnosticCount = fileCopy.Values.Sum(
			entry => entry.SyntaxDiagnosticCount
		);
		HasBuiltAtLeastOnce = hasBuiltAtLeastOnce;
	}

	internal long Generation { get; }
	internal IReadOnlyDictionary<string, CSharpFileIndexEntry> FilesByResourcePath { get; }
	internal IReadOnlyList<CSharpProjectTypeSymbol> Types { get; }
	internal IReadOnlyList<CSharpGlobalUsingInfo> GlobalUsings { get; }
	internal int FileCount { get; }
	internal int TypeCount { get; }
	internal int SyntaxDiagnosticCount { get; }
	internal bool HasBuiltAtLeastOnce { get; }

	private static CSharpGlobalUsingInfo[] CreateGlobalUsings(
		IReadOnlyDictionary<string, CSharpFileIndexEntry> filesByResourcePath
	)
	{
		var seenNamespaces = new HashSet<string>(StringComparer.Ordinal);
		var globalUsings = new List<CSharpGlobalUsingInfo>();

		foreach (
			CSharpFileIndexEntry fileEntry in filesByResourcePath.Values
				.OrderBy(entry => entry.ResourcePath, StringComparer.OrdinalIgnoreCase)
				.ThenBy(entry => entry.ResourcePath, StringComparer.Ordinal)
		)
		{
			foreach (CSharpUsingDirectiveInfo globalUsing in fileEntry.GlobalUsings)
			{
				if (
					globalUsing?.Kind != CSharpUsingDirectiveKind.GlobalNamespace
					|| string.IsNullOrWhiteSpace(globalUsing.Name)
					|| !seenNamespaces.Add(globalUsing.Name)
				)
				{
					continue;
				}

				globalUsings.Add(
					new CSharpGlobalUsingInfo(globalUsing.Name, fileEntry.ResourcePath)
				);
			}
		}

		return globalUsings
			.OrderBy(globalUsing => globalUsing.NamespaceName, StringComparer.OrdinalIgnoreCase)
			.ThenBy(globalUsing => globalUsing.NamespaceName, StringComparer.Ordinal)
			.ToArray();
	}
}
#endif
