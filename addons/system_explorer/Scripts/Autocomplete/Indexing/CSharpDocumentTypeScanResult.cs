#if TOOLS
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using SystemExplorer.Autocomplete.Indexing.Context;

namespace SystemExplorer.Autocomplete.Indexing;

internal sealed class CSharpDocumentTypeScanResult
{
	internal CSharpDocumentTypeScanResult(
		IReadOnlyList<CSharpProjectTypeSymbol> types,
		int syntaxDiagnosticCount,
		CSharpDocumentCompletionContext completionContext,
		IReadOnlyList<CSharpUsingDirectiveInfo> globalUsings
	)
	{
		Types = new ReadOnlyCollection<CSharpProjectTypeSymbol>(
			(types ?? Array.Empty<CSharpProjectTypeSymbol>())
				.Where(type => type != null)
				.ToArray()
		);
		SyntaxDiagnosticCount = Math.Max(0, syntaxDiagnosticCount);
		CompletionContext = completionContext ?? CSharpDocumentCompletionContext.Empty;
		GlobalUsings = new ReadOnlyCollection<CSharpUsingDirectiveInfo>(
			(globalUsings ?? Array.Empty<CSharpUsingDirectiveInfo>())
				.Where(usingDirective => usingDirective != null)
				.ToArray()
		);
	}

	internal IReadOnlyList<CSharpProjectTypeSymbol> Types { get; }
	internal int SyntaxDiagnosticCount { get; }
	internal CSharpDocumentCompletionContext CompletionContext { get; }
	internal IReadOnlyList<CSharpUsingDirectiveInfo> GlobalUsings { get; }
}
#endif
