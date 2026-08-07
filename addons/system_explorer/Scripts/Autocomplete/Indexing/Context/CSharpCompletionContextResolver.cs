#if TOOLS
using System;
using System.Collections.Generic;

namespace SystemExplorer.Autocomplete.Indexing.Context;

internal sealed class CSharpCompletionContextResolver
{
	internal CSharpResolvedCompletionContext Resolve(
		CSharpDocumentCompletionContext documentContext,
		int caretLine,
		IReadOnlyList<CSharpGlobalUsingInfo> projectGlobalUsings
	)
	{
		var importedNamespaces = new HashSet<string>(StringComparer.Ordinal);
		var globalImportedNamespaces = new HashSet<string>(StringComparer.Ordinal);
		string currentNamespace = "";

		AddProjectGlobalUsings(projectGlobalUsings, globalImportedNamespaces);

		if (documentContext == null || caretLine < 0)
		{
			return new CSharpResolvedCompletionContext(
				currentNamespace,
				importedNamespaces,
				globalImportedNamespaces
			);
		}

		var containingScopes = new List<CSharpNamespaceScope>();
		CSharpNamespaceScope innermostScope = null;
		int innermostRange = int.MaxValue;

		for (int index = 0; index < documentContext.NamespaceScopes.Count; index++)
		{
			CSharpNamespaceScope scope = documentContext.NamespaceScopes[index];
			if (
				scope == null
				|| caretLine < scope.StartLine
				|| caretLine > scope.EndLine
			)
			{
				continue;
			}

			containingScopes.Add(scope);
			int range = scope.EndLine - scope.StartLine;
			if (innermostScope == null || range <= innermostRange)
			{
				innermostScope = scope;
				innermostRange = range;
			}
		}

		if (innermostScope == null)
		{
			return new CSharpResolvedCompletionContext(
				currentNamespace,
				importedNamespaces,
				globalImportedNamespaces
			);
		}

		AddApplicableUsings(
			documentContext.CompilationUnitUsings,
			caretLine,
			importedNamespaces,
			globalImportedNamespaces
		);

		foreach (CSharpNamespaceScope scope in containingScopes)
		{
			AddApplicableUsings(
				scope.Usings,
				caretLine,
				importedNamespaces,
				globalImportedNamespaces
			);
		}

		currentNamespace = innermostScope.NamespaceName;

		return new CSharpResolvedCompletionContext(
			currentNamespace,
			importedNamespaces,
			globalImportedNamespaces
		);
	}

	private static void AddProjectGlobalUsings(
		IReadOnlyList<CSharpGlobalUsingInfo> projectGlobalUsings,
		HashSet<string> globalImportedNamespaces
	)
	{
		if (projectGlobalUsings == null)
			return;

		foreach (CSharpGlobalUsingInfo globalUsing in projectGlobalUsings)
		{
			if (!string.IsNullOrWhiteSpace(globalUsing?.NamespaceName))
				globalImportedNamespaces.Add(globalUsing.NamespaceName);
		}
	}

	private static void AddApplicableUsings(
		IReadOnlyList<CSharpUsingDirectiveInfo> usings,
		int caretLine,
		HashSet<string> importedNamespaces,
		HashSet<string> globalImportedNamespaces
	)
	{
		if (usings == null)
			return;

		foreach (CSharpUsingDirectiveInfo usingDirective in usings)
		{
			if (
				usingDirective == null
				|| string.IsNullOrWhiteSpace(usingDirective.Name)
				|| caretLine < usingDirective.ScopeStartLine
				|| caretLine > usingDirective.ScopeEndLine
			)
			{
				continue;
			}

			switch (usingDirective.Kind)
			{
				case CSharpUsingDirectiveKind.Namespace:
					importedNamespaces.Add(usingDirective.Name);
					break;
				case CSharpUsingDirectiveKind.GlobalNamespace:
					globalImportedNamespaces.Add(usingDirective.Name);
					break;
			}
		}
	}
}
#endif
