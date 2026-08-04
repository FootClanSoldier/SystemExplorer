#if TOOLS
namespace SystemExplorer.EditorIntegration.ScriptEditing;

internal static class ScriptPathUtility
{
	internal static string Normalize(string path)
	{
		if (path == null)
			return "";

		string normalizedPath = path.Trim();

		if (normalizedPath.IndexOf('\\') >= 0)
			normalizedPath = normalizedPath.Replace('\\', '/');

		const string resourcePrefix = "res://";
		if (
			!normalizedPath.StartsWith(resourcePrefix, System.StringComparison.Ordinal)
			|| normalizedPath.Length <= resourcePrefix.Length
			|| normalizedPath[resourcePrefix.Length] != '/'
		)
		{
			return normalizedPath;
		}

		int relativePathStart = resourcePrefix.Length;
		while (
			relativePathStart < normalizedPath.Length
			&& normalizedPath[relativePathStart] == '/'
		)
		{
			relativePathStart++;
		}

		return relativePathStart == normalizedPath.Length
			? resourcePrefix
			: resourcePrefix + normalizedPath.Substring(relativePathStart);
	}
}
#endif
