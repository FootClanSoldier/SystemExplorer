#if TOOLS
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SystemExplorer.Autocomplete.Indexing.Context;

namespace SystemExplorer.Autocomplete.Indexing;

internal sealed class RoslynProjectTypeScanner
{
	private readonly CSharpDocumentCompletionContextBuilder _completionContextBuilder;

	internal RoslynProjectTypeScanner(
		CSharpDocumentCompletionContextBuilder completionContextBuilder
	)
	{
		_completionContextBuilder =
			completionContextBuilder
			?? throw new ArgumentNullException(nameof(completionContextBuilder));
	}

	internal CSharpFileIndexEntry ScanFile(
		CSharpProjectFileDescriptor file,
		string sourceText,
		CancellationToken cancellationToken
	)
	{
		ArgumentNullException.ThrowIfNull(file);

		CSharpDocumentTypeScanResult documentResult = ScanDocument(
			file.ResourcePath,
			sourceText,
			cancellationToken
		);

		return new CSharpFileIndexEntry(
			file.ResourcePath,
			file.GlobalPath,
			file.Length,
			file.LastWriteTimeUtcTicks,
			documentResult.Types,
			documentResult.SyntaxDiagnosticCount,
			documentResult.GlobalUsings
		);
	}

	internal CSharpDocumentTypeScanResult ScanDocument(
		string scriptPath,
		string sourceText,
		CancellationToken cancellationToken
	)
	{
		scriptPath ??= "";
		sourceText ??= "";

		cancellationToken.ThrowIfCancellationRequested();
		SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(
			sourceText,
			CSharpSyntaxParseProfile.ParseOptions,
			scriptPath,
			cancellationToken: cancellationToken
		);
		cancellationToken.ThrowIfCancellationRequested();

		CompilationUnitSyntax root = (CompilationUnitSyntax)syntaxTree.GetRoot(
			cancellationToken
		);
		BaseTypeDeclarationSyntax[] declarations = root
			.DescendantNodes()
			.OfType<BaseTypeDeclarationSyntax>()
			.OrderBy(declaration => declaration.SpanStart)
			.ToArray();
		var types = new List<CSharpProjectTypeSymbol>(declarations.Length);

		foreach (BaseTypeDeclarationSyntax declaration in declarations)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (!TryGetKind(declaration, out CSharpProjectTypeKind kind))
				continue;

			string namespaceName = string.Join(
				".",
				declaration
					.Ancestors()
					.OfType<BaseNamespaceDeclarationSyntax>()
					.Reverse()
					.Select(namespaceDeclaration => GetNameText(namespaceDeclaration.Name))
					.Where(name => !string.IsNullOrWhiteSpace(name))
			);

			string[] containingTypeNames = declaration
				.Ancestors()
				.OfType<BaseTypeDeclarationSyntax>()
				.Reverse()
				.Select(type => type.Identifier.ValueText)
				.Where(name => !string.IsNullOrWhiteSpace(name))
				.ToArray();

			SyntaxTokenList modifiers = declaration.Modifiers;
			types.Add(
				new CSharpProjectTypeSymbol(
					declaration.Identifier.ValueText,
					namespaceName,
					containingTypeNames,
					scriptPath,
					kind,
					GetGenericArity(declaration),
					modifiers.Any(SyntaxKind.PartialKeyword),
					modifiers.Any(SyntaxKind.StaticKeyword),
					modifiers.Any(SyntaxKind.AbstractKeyword)
				)
			);
		}

		CSharpDocumentCompletionContext completionContext =
			_completionContextBuilder.Build(
				root,
				syntaxTree,
				sourceText.Length == 0,
				cancellationToken
			);
		CSharpUsingDirectiveInfo[] globalUsings = completionContext
			.CompilationUnitUsings
			.Where(IsGlobalUsing)
			.Where(usingDirective => !string.IsNullOrWhiteSpace(usingDirective.Name))
			.GroupBy(CreateUsingIdentity, StringComparer.Ordinal)
			.Select(group => group.First())
			.ToArray();

		int syntaxDiagnosticCount = syntaxTree
			.GetDiagnostics(cancellationToken)
			.Count();

		return new CSharpDocumentTypeScanResult(
			types,
			syntaxDiagnosticCount,
			completionContext,
			globalUsings
		);
	}

	private static string GetNameText(NameSyntax name)
	{
		return name?.WithoutTrivia().ToString() ?? "";
	}

	private static bool IsGlobalUsing(CSharpUsingDirectiveInfo usingDirective)
	{
		return usingDirective?.Kind is CSharpUsingDirectiveKind.GlobalNamespace
			or CSharpUsingDirectiveKind.GlobalAlias
			or CSharpUsingDirectiveKind.GlobalStatic;
	}

	private static string CreateUsingIdentity(CSharpUsingDirectiveInfo usingDirective)
	{
		return $"{(int)usingDirective.Kind}\u001f{usingDirective.Name}\u001f{usingDirective.Alias}";
	}

	private static bool TryGetKind(
		BaseTypeDeclarationSyntax declaration,
		out CSharpProjectTypeKind kind
	)
	{
		switch (declaration)
		{
			case ClassDeclarationSyntax:
				kind = CSharpProjectTypeKind.Class;
				return true;
			case StructDeclarationSyntax:
				kind = CSharpProjectTypeKind.Struct;
				return true;
			case InterfaceDeclarationSyntax:
				kind = CSharpProjectTypeKind.Interface;
				return true;
			case EnumDeclarationSyntax:
				kind = CSharpProjectTypeKind.Enum;
				return true;
			case RecordDeclarationSyntax recordDeclaration:
				kind = recordDeclaration.ClassOrStructKeyword.IsKind(
					SyntaxKind.StructKeyword
				)
					? CSharpProjectTypeKind.RecordStruct
					: CSharpProjectTypeKind.RecordClass;
				return true;
			default:
				kind = default;
				return false;
		}
	}

	private static int GetGenericArity(BaseTypeDeclarationSyntax declaration)
	{
		return declaration is TypeDeclarationSyntax typeDeclaration
			? typeDeclaration.TypeParameterList?.Parameters.Count ?? 0
			: 0;
	}
}
#endif
