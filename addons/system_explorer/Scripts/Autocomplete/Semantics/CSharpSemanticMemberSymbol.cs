#if TOOLS
using System;

namespace SystemExplorer.Autocomplete.Semantics;

internal sealed record CSharpSemanticMemberSymbol
{
	internal CSharpSemanticMemberSymbol(
		string name,
		CSharpSemanticMemberKind kind,
		int overloadCount
	)
	{
		Name = name ?? "";
		Kind = kind;
		OverloadCount = Math.Max(1, overloadCount);
	}

	internal string Name { get; }
	internal CSharpSemanticMemberKind Kind { get; }
	internal int OverloadCount { get; }
}
#endif
