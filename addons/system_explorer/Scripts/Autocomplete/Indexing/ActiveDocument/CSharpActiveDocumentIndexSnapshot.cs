#if TOOLS
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using SystemExplorer.Autocomplete.Indexing;
using SystemExplorer.Autocomplete.Indexing.Context;

namespace SystemExplorer.Autocomplete.Indexing.ActiveDocument;

internal sealed class CSharpActiveDocumentIndexSnapshot
{
	internal static CSharpActiveDocumentIndexSnapshot Empty { get; } = new(
		revision: 0,
		scriptPath: "",
		types: Array.Empty<CSharpProjectTypeSymbol>(),
		syntaxDiagnosticCount: 0,
		completionContext: CSharpDocumentCompletionContext.Empty,
		hasBuiltAtLeastOnce: false
	);

	internal CSharpActiveDocumentIndexSnapshot(
		long revision,
		string scriptPath,
		IReadOnlyList<CSharpProjectTypeSymbol> types,
		int syntaxDiagnosticCount,
		CSharpDocumentCompletionContext completionContext,
		bool hasBuiltAtLeastOnce
	)
	{
		CSharpProjectTypeSymbol[] typeCopy = (
			types ?? Array.Empty<CSharpProjectTypeSymbol>()
		)
			.Where(type => type != null)
			.ToArray();

		Revision = revision;
		ScriptPath = scriptPath ?? "";
		Types = new ReadOnlyCollection<CSharpProjectTypeSymbol>(typeCopy);
		SyntaxDiagnosticCount = Math.Max(0, syntaxDiagnosticCount);
		CompletionContext =
			completionContext ?? CSharpDocumentCompletionContext.Empty;
		HasBuiltAtLeastOnce = hasBuiltAtLeastOnce;
	}

	internal long Revision { get; }
	internal string ScriptPath { get; }
	internal IReadOnlyList<CSharpProjectTypeSymbol> Types { get; }
	internal int SyntaxDiagnosticCount { get; }
	internal CSharpDocumentCompletionContext CompletionContext { get; }
	internal bool HasBuiltAtLeastOnce { get; }
}
#endif
