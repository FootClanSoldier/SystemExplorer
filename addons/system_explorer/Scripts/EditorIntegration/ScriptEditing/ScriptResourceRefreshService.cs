#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SystemExplorer.EditorIntegration.ScriptEditing;

internal static class ScriptResourceRefreshService
{
	internal static void RefreshChangedScripts(IEnumerable<string> changedScriptPaths)
	{
		List<string> changedPaths = changedScriptPaths
			.Where(path => !string.IsNullOrWhiteSpace(path))
			.Select(ScriptPathUtility.Normalize)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		EditorFileSystem resourceFilesystem = EditorInterface.Singleton?.GetResourceFilesystem();

		if (resourceFilesystem == null)
			return;

		foreach (string scriptPath in changedPaths)
			resourceFilesystem.UpdateFile(scriptPath);
	}
}
#endif
