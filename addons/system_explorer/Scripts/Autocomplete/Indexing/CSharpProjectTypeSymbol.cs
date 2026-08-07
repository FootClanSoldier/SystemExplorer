#if TOOLS
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace SystemExplorer.Autocomplete.Indexing;

internal sealed record CSharpProjectTypeSymbol
{
	internal CSharpProjectTypeSymbol(
		string name,
		string namespaceName,
		IReadOnlyList<string> containingTypeNames,
		string scriptPath,
		CSharpProjectTypeKind kind,
		int genericArity,
		bool isPartial,
		bool isStatic,
		bool isAbstract
	)
	{
		Name = name ?? "";
		NamespaceName = namespaceName ?? "";
		ContainingTypeNames = new ReadOnlyCollection<string>(
			(containingTypeNames ?? Array.Empty<string>()).ToArray()
		);
		ScriptPath = scriptPath ?? "";
		Kind = kind;
		GenericArity = Math.Max(0, genericArity);
		IsPartial = isPartial;
		IsStatic = isStatic;
		IsAbstract = isAbstract;
	}

	internal string Name { get; }
	internal string NamespaceName { get; }
	internal IReadOnlyList<string> ContainingTypeNames { get; }
	internal string ScriptPath { get; }
	internal CSharpProjectTypeKind Kind { get; }
	internal int GenericArity { get; }
	internal bool IsPartial { get; }
	internal bool IsStatic { get; }
	internal bool IsAbstract { get; }
}
#endif
