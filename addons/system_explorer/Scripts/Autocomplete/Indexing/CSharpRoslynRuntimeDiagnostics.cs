#if TOOLS
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace SystemExplorer.Autocomplete.Indexing;

internal static class CSharpRoslynRuntimeDiagnostics
{
	private const int MaximumContextLength = 1500;
	private const int MaximumFallbackLength = 700;

	internal static string CreateParseFailureContext(
		string sourceText,
		CSharpParseOptions parseOptions
	)
	{
		try
		{
			Assembly pluginAssembly = typeof(CSharpRoslynRuntimeDiagnostics).Assembly;
			Assembly roslynCSharpAssembly = typeof(CSharpSyntaxTree).Assembly;
			Assembly codeAnalysisAssembly = typeof(SyntaxTree).Assembly;
			AssemblyLoadContext pluginLoadContext = AssemblyLoadContext.GetLoadContext(
				pluginAssembly
			);
			AssemblyLoadContext roslynCSharpLoadContext = AssemblyLoadContext.GetLoadContext(
				roslynCSharpAssembly
			);
			AssemblyLoadContext codeAnalysisLoadContext = AssemblyLoadContext.GetLoadContext(
				codeAnalysisAssembly
			);

			string detail =
				$"WorkerThreadId={Environment.CurrentManagedThreadId}, "
				+ $"SourceTextNull={sourceText == null}, "
				+ $"SourceLength={sourceText?.Length ?? 0}, "
				+ $"SourceObjectToken={GetObjectToken(sourceText)}, "
				+ $"ParseOptionsNull={parseOptions == null}, "
				+ $"ParseOptionsObjectToken={GetObjectToken(parseOptions)}, "
				+ $"ParseProfile='{CSharpSyntaxParseProfile.CacheIdentity}', "
				+ $"LanguageVersion='{parseOptions?.LanguageVersion.ToString() ?? "<null>"}', "
				+ $"DocumentationMode='{parseOptions?.DocumentationMode.ToString() ?? "<null>"}', "
				+ $"SourceCodeKind='{parseOptions?.Kind.ToString() ?? "<null>"}', "
				+ $"PluginAssemblyFullName='{DescribeAssemblyFullName(pluginAssembly)}', "
				+ $"PluginAssemblyMvid='{DescribeAssemblyMvid(pluginAssembly)}', "
				+ $"RoslynCSharpAssemblyFullName='{DescribeAssemblyFullName(roslynCSharpAssembly)}', "
				+ $"RoslynCSharpAssemblyMvid='{DescribeAssemblyMvid(roslynCSharpAssembly)}', "
				+ $"CodeAnalysisAssemblyFullName='{DescribeAssemblyFullName(codeAnalysisAssembly)}', "
				+ $"CodeAnalysisAssemblyMvid='{DescribeAssemblyMvid(codeAnalysisAssembly)}', "
				+ $"PluginAssemblyLoadContextName='{DescribeLoadContextName(pluginLoadContext)}', "
				+ $"PluginAssemblyLoadContextObjectToken={GetObjectToken(pluginLoadContext)}, "
				+ $"PluginAssemblyLoadContextCollectible={DescribeLoadContextCollectible(pluginLoadContext)}, "
				+ $"RoslynCSharpAssemblyLoadContextName='{DescribeLoadContextName(roslynCSharpLoadContext)}', "
				+ $"RoslynCSharpAssemblyLoadContextObjectToken={GetObjectToken(roslynCSharpLoadContext)}, "
				+ $"RoslynCSharpAssemblyLoadContextCollectible={DescribeLoadContextCollectible(roslynCSharpLoadContext)}, "
				+ $"CodeAnalysisAssemblyLoadContextName='{DescribeLoadContextName(codeAnalysisLoadContext)}', "
				+ $"CodeAnalysisAssemblyLoadContextObjectToken={GetObjectToken(codeAnalysisLoadContext)}, "
				+ $"CodeAnalysisAssemblyLoadContextCollectible={DescribeLoadContextCollectible(codeAnalysisLoadContext)}, "
				+ $"PluginAndRoslynSameLoadContext={ReferenceEquals(pluginLoadContext, roslynCSharpLoadContext)}, "
				+ $"RoslynAndCodeAnalysisSameLoadContext={ReferenceEquals(roslynCSharpLoadContext, codeAnalysisLoadContext)}";

			return NormalizeSingleLine(detail, MaximumContextLength);
		}
		catch (Exception exception)
		{
			string fallback =
				$"WorkerThreadId={Environment.CurrentManagedThreadId}, "
				+ $"SourceTextNull={sourceText == null}, "
				+ $"SourceLength={sourceText?.Length ?? 0}, "
				+ $"SourceObjectToken={GetObjectToken(sourceText)}, "
				+ $"ParseOptionsNull={parseOptions == null}, "
				+ $"ParseOptionsObjectToken={GetObjectToken(parseOptions)}, "
				+ $"ParseProfile='{CSharpSyntaxParseProfile.CacheIdentity}', "
				+ $"DiagnosticReadFailure='{exception.GetType().Name}: {exception.Message}'";
			return NormalizeSingleLine(fallback, MaximumFallbackLength);
		}
	}

	// Process-local diagnostic correlation only; never persistent identity or correctness state.
	internal static int GetObjectToken(object source)
	{
		return source == null ? 0 : RuntimeHelpers.GetHashCode(source);
	}

	private static string DescribeAssemblyFullName(Assembly assembly)
	{
		return assembly?.FullName ?? "<null>";
	}

	private static string DescribeAssemblyMvid(Assembly assembly)
	{
		try
		{
			return assembly?.ManifestModule?.ModuleVersionId.ToString("D") ?? "<null>";
		}
		catch (Exception exception)
		{
			return $"<read-failed:{exception.GetType().Name}>";
		}
	}

	private static string DescribeLoadContextName(AssemblyLoadContext loadContext)
	{
		if (loadContext == null)
			return "<null>";

		return string.IsNullOrWhiteSpace(loadContext.Name)
			? "<unnamed>"
			: loadContext.Name;
	}

	private static string DescribeLoadContextCollectible(AssemblyLoadContext loadContext)
	{
		return loadContext == null ? "<unknown>" : loadContext.IsCollectible.ToString();
	}

	private static string NormalizeSingleLine(string detail, int maximumLength)
	{
		if (string.IsNullOrWhiteSpace(detail))
			return "Roslyn runtime diagnostic context unavailable.";

		string normalized = detail
			.Replace('\r', ' ')
			.Replace('\n', ' ')
			.Replace('\t', ' ')
			.Trim();
		return normalized.Length <= maximumLength
			? normalized
			: normalized.Substring(0, maximumLength);
	}
}
#endif
