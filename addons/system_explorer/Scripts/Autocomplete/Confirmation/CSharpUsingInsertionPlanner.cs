#if TOOLS
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Threading;
using SystemExplorer.Autocomplete.Indexing;
using SystemExplorer.Autocomplete.Indexing.Context;

namespace SystemExplorer.Autocomplete.Confirmation;

internal sealed class CSharpUsingInsertionPlanner
{
	private readonly CSharpDocumentCompletionContextBuilder _completionContextBuilder;
	private readonly CSharpCompletionContextResolver _contextResolver;

	internal CSharpUsingInsertionPlanner(
		CSharpDocumentCompletionContextBuilder completionContextBuilder,
		CSharpCompletionContextResolver contextResolver
	)
	{
		_completionContextBuilder =
			completionContextBuilder
			?? throw new ArgumentNullException(nameof(completionContextBuilder));
		_contextResolver =
			contextResolver ?? throw new ArgumentNullException(nameof(contextResolver));
	}

	internal CSharpUsingInsertionPlan Plan(
		string sourceText,
		string targetNamespace,
		int caretLine
	)
	{
		sourceText ??= "";
		targetNamespace = targetNamespace?.Trim() ?? "";

		if (!IsValidNamespaceName(targetNamespace))
			return CSharpUsingInsertionPlan.Unsafe(targetNamespace, "InvalidNamespace");
		if (caretLine < 0)
			return CSharpUsingInsertionPlan.Unsafe(targetNamespace, "InvalidCaretLine");

		SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(
			sourceText,
			CSharpSyntaxParseProfile.ParseOptions
		);
		CompilationUnitSyntax root = (CompilationUnitSyntax)syntaxTree.GetRoot();
		SourceText text = syntaxTree.GetText();
		CSharpDocumentCompletionContext documentContext =
			_completionContextBuilder.Build(
				root,
				syntaxTree,
				sourceText.Length == 0,
				CancellationToken.None
			);
		CSharpResolvedCompletionContext resolvedContext = _contextResolver.Resolve(
			documentContext,
			caretLine,
			Array.Empty<CSharpGlobalUsingInfo>()
		);

		if (IsNamespaceAvailable(resolvedContext, targetNamespace))
			return CSharpUsingInsertionPlan.NotRequired(targetNamespace);

		string lineEnding = DetectLineEnding(sourceText);
		string usingText = $"using {targetNamespace};";

		if (root.Usings.Count > 0)
		{
			return CreatePlanAfterDirectiveLine(
				text,
				root.Usings[root.Usings.Count - 1],
				targetNamespace,
				usingText,
				lineEnding
			);
		}

		if (root.Externs.Count > 0)
		{
			return CreatePlanAfterDirectiveLine(
				text,
				root.Externs[root.Externs.Count - 1],
				targetNamespace,
				usingText,
				lineEnding
			);
		}

		return CreatePlanAfterLeadingTrivia(
			root,
			text,
			targetNamespace,
			usingText,
			lineEnding
		);
	}

	private static bool IsNamespaceAvailable(
		CSharpResolvedCompletionContext resolvedContext,
		string targetNamespace
	)
	{
		if (resolvedContext == null)
			return false;

		return string.Equals(
				resolvedContext.CurrentNamespace,
				targetNamespace,
				StringComparison.Ordinal
			)
			|| resolvedContext.ImportedNamespaces.ContainsKey(targetNamespace)
			|| resolvedContext.GlobalImportedNamespaces.ContainsKey(targetNamespace);
	}

	private static CSharpUsingInsertionPlan CreatePlanAfterDirectiveLine(
		SourceText text,
		CSharpSyntaxNode directive,
		string targetNamespace,
		string usingText,
		string lineEnding
	)
	{
		if (text == null || directive == null || text.Lines.Count == 0)
			return CSharpUsingInsertionPlan.Unsafe(targetNamespace, "MissingSourceLine");

		int directiveEnd = Math.Clamp(directive.Span.End, 0, text.Length);
		TextLine line = text.Lines.GetLineFromPosition(directiveEnd);

		if (line.End > directive.FullSpan.End)
		{
			return CSharpUsingInsertionPlan.Unsafe(
				targetNamespace,
				"DeclarationSharesDirectiveLine"
			);
		}

		if (line.EndIncludingLineBreak > line.End)
		{
			return CSharpUsingInsertionPlan.Insert(
				targetNamespace,
				line.LineNumber + 1,
				0,
				usingText + lineEnding
			);
		}

		return CSharpUsingInsertionPlan.Insert(
			targetNamespace,
			line.LineNumber,
			line.End - line.Start,
			lineEnding + usingText
		);
	}

	private static CSharpUsingInsertionPlan CreatePlanAfterLeadingTrivia(
		CompilationUnitSyntax root,
		SourceText text,
		string targetNamespace,
		string usingText,
		string lineEnding
	)
	{
		if (root == null || text == null || text.Lines.Count == 0)
			return CSharpUsingInsertionPlan.Unsafe(targetNamespace, "MissingCompilationUnit");

		SyntaxToken firstToken = root.GetFirstToken(includeZeroWidth: true);
		int tokenStart = Math.Clamp(firstToken.SpanStart, 0, text.Length);
		TextLine line = text.Lines.GetLineFromPosition(tokenStart);

		if (!ContainsOnlyWhitespace(text, line.Start, tokenStart))
		{
			return CSharpUsingInsertionPlan.Unsafe(
				targetNamespace,
				"HeaderSharesDeclarationLine"
			);
		}

		return CSharpUsingInsertionPlan.Insert(
			targetNamespace,
			line.LineNumber,
			0,
			usingText + lineEnding
		);
	}

	private static bool IsValidNamespaceName(string targetNamespace)
	{
		if (
			string.IsNullOrWhiteSpace(targetNamespace)
			|| targetNamespace.IndexOfAny(new[] { '\r', '\n', ';' }) >= 0
		)
		{
			return false;
		}

		NameSyntax parsedName = SyntaxFactory.ParseName(targetNamespace);
		return !parsedName.ContainsDiagnostics
			&& parsedName.FullSpan.Length == targetNamespace.Length
			&& string.Equals(
				GetNameText(parsedName),
				targetNamespace,
				StringComparison.Ordinal
			);
	}

	private static bool ContainsOnlyWhitespace(SourceText text, int start, int end)
	{
		start = Math.Clamp(start, 0, text.Length);
		end = Math.Clamp(end, start, text.Length);

		for (int index = start; index < end; index++)
		{
			if (!char.IsWhiteSpace(text[index]))
				return false;
		}

		return true;
	}

	private static string GetNameText(NameSyntax name)
	{
		return name?.WithoutTrivia().ToString() ?? "";
	}

	private static string DetectLineEnding(string sourceText)
	{
		for (int index = 0; index < sourceText.Length; index++)
		{
			if (sourceText[index] == '\r')
			{
				return index + 1 < sourceText.Length && sourceText[index + 1] == '\n'
					? "\r\n"
					: "\r";
			}

			if (sourceText[index] == '\n')
				return "\n";
		}

		return "\n";
	}
}
#endif
