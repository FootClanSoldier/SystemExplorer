#if TOOLS
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace SystemExplorer.Autocomplete.Indexing.Context;

internal sealed class CSharpDocumentCompletionContextBuilder
{
	internal CSharpDocumentCompletionContext Build(
		CompilationUnitSyntax root,
		SyntaxTree syntaxTree,
		bool isEmptyDocument,
		CancellationToken cancellationToken
	)
	{
		ArgumentNullException.ThrowIfNull(root);
		ArgumentNullException.ThrowIfNull(syntaxTree);

		if (isEmptyDocument)
			return CSharpDocumentCompletionContext.Empty;

		int documentEndLine = Math.Max(
			0,
			syntaxTree.GetText(cancellationToken).Lines.Count - 1
		);
		IReadOnlyList<CSharpUsingDirectiveInfo> compilationUnitUsings =
			CreateUsingDirectives(
				root.Usings,
				scopeStartLine: 0,
				scopeEndLine: documentEndLine,
				cancellationToken
			);
		var namespaceScopes = new List<CSharpNamespaceScope>
		{
			new(
				namespaceName: "",
				startLine: 0,
				endLine: documentEndLine,
				usings: Array.Empty<CSharpUsingDirectiveInfo>()
			),
		};

		foreach (
			BaseNamespaceDeclarationSyntax namespaceDeclaration in root
				.DescendantNodes()
				.OfType<BaseNamespaceDeclarationSyntax>()
				.OrderBy(declaration => declaration.SpanStart)
		)
		{
			cancellationToken.ThrowIfCancellationRequested();
			GetNamespaceScopeLines(
				namespaceDeclaration,
				syntaxTree,
				documentEndLine,
				out int scopeStartLine,
				out int scopeEndLine
			);
			IReadOnlyList<CSharpUsingDirectiveInfo> namespaceUsings =
				CreateUsingDirectives(
					namespaceDeclaration.Usings,
					scopeStartLine,
					scopeEndLine,
					cancellationToken
				);

			namespaceScopes.Add(
				new CSharpNamespaceScope(
					GetFullNamespaceName(namespaceDeclaration),
					scopeStartLine,
					scopeEndLine,
					namespaceUsings
				)
			);
		}

		return new CSharpDocumentCompletionContext(
			namespaceScopes,
			compilationUnitUsings
		);
	}

	private static IReadOnlyList<CSharpUsingDirectiveInfo> CreateUsingDirectives(
		SyntaxList<UsingDirectiveSyntax> usingDirectives,
		int scopeStartLine,
		int scopeEndLine,
		CancellationToken cancellationToken
	)
	{
		if (usingDirectives.Count == 0)
			return Array.Empty<CSharpUsingDirectiveInfo>();

		var result = new List<CSharpUsingDirectiveInfo>(usingDirectives.Count);
		foreach (UsingDirectiveSyntax usingDirective in usingDirectives)
		{
			cancellationToken.ThrowIfCancellationRequested();
			result.Add(
				new CSharpUsingDirectiveInfo(
					GetUsingKind(usingDirective),
					GetNameText(usingDirective.Name),
					usingDirective.Alias?.Name.Identifier.ValueText ?? "",
					scopeStartLine,
					scopeEndLine
				)
			);
		}

		return result;
	}

	private static CSharpUsingDirectiveKind GetUsingKind(
		UsingDirectiveSyntax usingDirective
	)
	{
		bool isGlobal = usingDirective.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword);
		bool isAlias = usingDirective.Alias != null;
		bool isStatic = usingDirective.StaticKeyword.IsKind(SyntaxKind.StaticKeyword);

		if (isGlobal)
		{
			if (isAlias)
				return CSharpUsingDirectiveKind.GlobalAlias;
			if (isStatic)
				return CSharpUsingDirectiveKind.GlobalStatic;
			return CSharpUsingDirectiveKind.GlobalNamespace;
		}

		if (isAlias)
			return CSharpUsingDirectiveKind.Alias;
		if (isStatic)
			return CSharpUsingDirectiveKind.Static;
		return CSharpUsingDirectiveKind.Namespace;
	}

	private static void GetNamespaceScopeLines(
		BaseNamespaceDeclarationSyntax namespaceDeclaration,
		SyntaxTree syntaxTree,
		int documentEndLine,
		out int startLine,
		out int endLine
	)
	{
		startLine = syntaxTree.GetLineSpan(
			new TextSpan(namespaceDeclaration.SpanStart, 0)
		).StartLinePosition.Line;

		if (namespaceDeclaration is FileScopedNamespaceDeclarationSyntax)
		{
			endLine = documentEndLine;
			return;
		}

		int inclusiveEndPosition = Math.Max(
			namespaceDeclaration.SpanStart,
			namespaceDeclaration.Span.End - 1
		);
		endLine = syntaxTree.GetLineSpan(
			new TextSpan(inclusiveEndPosition, 0)
		).StartLinePosition.Line;
		endLine = Math.Max(startLine, endLine);
	}

	private static string GetFullNamespaceName(
		BaseNamespaceDeclarationSyntax namespaceDeclaration
	)
	{
		return string.Join(
			".",
			namespaceDeclaration
				.AncestorsAndSelf()
				.OfType<BaseNamespaceDeclarationSyntax>()
				.Reverse()
				.Select(declaration => GetNameText(declaration.Name))
				.Where(name => !string.IsNullOrWhiteSpace(name))
		);
	}

	private static string GetNameText(NameSyntax name)
	{
		return name?.WithoutTrivia().ToString() ?? "";
	}
}
#endif
