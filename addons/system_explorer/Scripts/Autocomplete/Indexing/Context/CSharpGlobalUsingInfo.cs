#if TOOLS
namespace SystemExplorer.Autocomplete.Indexing.Context;

internal sealed record CSharpGlobalUsingInfo
{
	internal CSharpGlobalUsingInfo(string namespaceName, string scriptPath)
	{
		NamespaceName = namespaceName ?? "";
		ScriptPath = scriptPath ?? "";
	}

	internal string NamespaceName { get; }
	internal string ScriptPath { get; }
}
#endif
