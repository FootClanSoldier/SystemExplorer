#if TOOLS
using System;

namespace SystemExplorer.Autocomplete.Indexing.Context;

internal sealed record CSharpUsingDirectiveInfo
{
	internal CSharpUsingDirectiveInfo(
		CSharpUsingDirectiveKind kind,
		string name,
		string alias,
		int scopeStartLine,
		int scopeEndLine
	)
	{
		Kind = kind;
		Name = name ?? "";
		Alias = alias ?? "";
		ScopeStartLine = Math.Max(0, scopeStartLine);
		ScopeEndLine = Math.Max(ScopeStartLine, scopeEndLine);
	}

	internal CSharpUsingDirectiveKind Kind { get; }
	internal string Name { get; }
	internal string Alias { get; }
	internal int ScopeStartLine { get; }
	internal int ScopeEndLine { get; }
}
#endif
