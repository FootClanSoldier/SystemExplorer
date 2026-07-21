#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal static class NamespaceScriptPathPayloadCodec
{
	internal static string Build(IEnumerable<string> scriptPaths)
	{
		if (scriptPaths == null)
			return "";

		return string.Join(
			"\n",
			scriptPaths
				.Where(path => !string.IsNullOrWhiteSpace(path))
				.Select(ScriptPathUtility.Normalize)
				.Distinct(StringComparer.OrdinalIgnoreCase)
		);
	}

	internal static string[] Parse(string payload)
	{
		if (string.IsNullOrWhiteSpace(payload))
			return Array.Empty<string>();

		return payload
			.Split('\n')
			.Select(ScriptPathUtility.Normalize)
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}
}
#endif
