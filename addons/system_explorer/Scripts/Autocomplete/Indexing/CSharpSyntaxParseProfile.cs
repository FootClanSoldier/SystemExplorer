#if TOOLS
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SystemExplorer.Autocomplete.Indexing;

internal static class CSharpSyntaxParseProfile
{
	internal const string CacheIdentity =
		"RoslynSyntax|LanguageVersion.Latest|DocumentationMode.Parse|SourceCodeKind.Regular|Defines=TOOLS";

	internal static CSharpParseOptions ParseOptions { get; } = new(
		LanguageVersion.Latest,
		DocumentationMode.Parse,
		SourceCodeKind.Regular,
		new[] { "TOOLS" }
	);
}
#endif
