#if TOOLS
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace SystemExplorer.Autocomplete.Indexing.Context;

internal sealed record CSharpDocumentCompletionContext
{
	internal static CSharpDocumentCompletionContext Empty { get; } = new(
		Array.Empty<CSharpNamespaceScope>(),
		Array.Empty<CSharpUsingDirectiveInfo>()
	);

	internal CSharpDocumentCompletionContext(
		IReadOnlyList<CSharpNamespaceScope> namespaceScopes,
		IReadOnlyList<CSharpUsingDirectiveInfo> compilationUnitUsings
	)
	{
		NamespaceScopes = new ReadOnlyCollection<CSharpNamespaceScope>(
			(namespaceScopes ?? Array.Empty<CSharpNamespaceScope>())
				.Where(scope => scope != null)
				.ToArray()
		);
		CompilationUnitUsings = new ReadOnlyCollection<CSharpUsingDirectiveInfo>(
			(compilationUnitUsings ?? Array.Empty<CSharpUsingDirectiveInfo>())
				.Where(usingDirective => usingDirective != null)
				.ToArray()
		);
	}

	internal IReadOnlyList<CSharpNamespaceScope> NamespaceScopes { get; }
	internal IReadOnlyList<CSharpUsingDirectiveInfo> CompilationUnitUsings { get; }
}
#endif
