#if TOOLS
using System;
using System.IO;

namespace SystemExplorer.Autocomplete.Indexing.Persistence;

internal static class CSharpProjectIndexCacheFormat
{
	internal const int CurrentVersion = 2;
	internal const string CurrentParseProfile = global::SystemExplorer.Autocomplete.Indexing.CSharpSyntaxParseProfile.CacheIdentity;
	internal const string CacheFileName = "csharp_project_type_index_v2.json";

	internal static string CreateCachePath(string globalProjectRoot)
	{
		if (string.IsNullOrWhiteSpace(globalProjectRoot))
			return "";

		string trimmedRoot = globalProjectRoot.Trim();
		if (!Path.IsPathFullyQualified(trimmedRoot))
			return "";

		string normalizedRoot = Path.GetFullPath(trimmedRoot);
		return Path.Combine(
			normalizedRoot,
			".godot",
			"system_explorer",
			"autocomplete",
			CacheFileName
		);
	}
}
#endif
