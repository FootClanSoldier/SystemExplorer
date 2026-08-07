#if TOOLS
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace SystemExplorer.Autocomplete.Indexing.Context;

internal sealed record CSharpNamespaceScope
{
	internal CSharpNamespaceScope(
		string namespaceName,
		int startLine,
		int endLine,
		IReadOnlyList<CSharpUsingDirectiveInfo> usings
	)
	{
		NamespaceName = namespaceName ?? "";
		StartLine = Math.Max(0, startLine);
		EndLine = Math.Max(StartLine, endLine);
		Usings = new ReadOnlyCollection<CSharpUsingDirectiveInfo>(
			(usings ?? Array.Empty<CSharpUsingDirectiveInfo>())
				.Where(usingDirective => usingDirective != null)
				.ToArray()
		);
	}

	internal string NamespaceName { get; }
	internal int StartLine { get; }
	internal int EndLine { get; }
	internal IReadOnlyList<CSharpUsingDirectiveInfo> Usings { get; }
}
#endif
