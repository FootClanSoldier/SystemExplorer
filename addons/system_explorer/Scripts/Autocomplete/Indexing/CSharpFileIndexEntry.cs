#if TOOLS
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using SystemExplorer.Autocomplete.Indexing.Context;

namespace SystemExplorer.Autocomplete.Indexing;

internal sealed record CSharpFileIndexEntry
{
	internal CSharpFileIndexEntry(
		string resourcePath,
		string globalPath,
		long length,
		long lastWriteTimeUtcTicks,
		IReadOnlyList<CSharpProjectTypeSymbol> types,
		int syntaxDiagnosticCount,
		IReadOnlyList<CSharpUsingDirectiveInfo> globalUsings
	)
	{
		ResourcePath = resourcePath ?? "";
		GlobalPath = globalPath ?? "";
		Length = length;
		LastWriteTimeUtcTicks = lastWriteTimeUtcTicks;
		Types = new ReadOnlyCollection<CSharpProjectTypeSymbol>(
			(types ?? Array.Empty<CSharpProjectTypeSymbol>())
				.Where(type => type != null)
				.ToArray()
		);
		SyntaxDiagnosticCount = Math.Max(0, syntaxDiagnosticCount);
		GlobalUsings = new ReadOnlyCollection<CSharpUsingDirectiveInfo>(
			(globalUsings ?? Array.Empty<CSharpUsingDirectiveInfo>())
				.Where(usingDirective => usingDirective != null)
				.ToArray()
		);
	}

	internal string ResourcePath { get; }
	internal string GlobalPath { get; }
	internal long Length { get; }
	internal long LastWriteTimeUtcTicks { get; }
	internal IReadOnlyList<CSharpProjectTypeSymbol> Types { get; }
	internal int SyntaxDiagnosticCount { get; }
	internal IReadOnlyList<CSharpUsingDirectiveInfo> GlobalUsings { get; }
}
#endif
