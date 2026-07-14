#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal sealed class NamespaceRefactorScriptEntry
{
	internal string ScriptPath { get; }
	internal string FolderPath { get; }

	internal NamespaceRefactorScriptEntry(string scriptPath, string folderPath)
	{
		ScriptPath = scriptPath ?? "";
		FolderPath = folderPath ?? "";
	}
}

internal sealed class NamespaceRefactorProjectInventoryResult
{
	internal IReadOnlyList<string> ScriptPaths { get; }
	internal string FailureMessage { get; }
	internal bool UsedFallback { get; }

	internal NamespaceRefactorProjectInventoryResult(
		IReadOnlyList<string> scriptPaths,
		string failureMessage,
		bool usedFallback
	)
	{
		ScriptPaths = scriptPaths ?? Array.Empty<string>();
		FailureMessage = failureMessage ?? "";
		UsedFallback = usedFallback;
	}
}

internal sealed class NamespaceRefactorScopeResolver
{
	private readonly Func<string, string> _normalizePath;
	private readonly Func<string, string> _globalizePath;
	private readonly Func<string, string> _localizePath;

	internal NamespaceRefactorScopeResolver(
		Func<string, string> normalizePath,
		Func<string, string> globalizePath,
		Func<string, string> localizePath
	)
	{
		_normalizePath = normalizePath ?? throw new ArgumentNullException(nameof(normalizePath));
		_globalizePath = globalizePath ?? throw new ArgumentNullException(nameof(globalizePath));
		_localizePath = localizePath ?? throw new ArgumentNullException(nameof(localizePath));
	}

	internal IReadOnlyList<string> ResolveTargetScriptPaths(
		IEnumerable<NamespaceRefactorScriptEntry> entries,
		string targetFolderPath
	)
	{
		List<string> result = new();
		HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);
		string normalizedTargetFolderPath = NormalizeFolderPath(targetFolderPath);

		if (entries == null)
			return result;

		foreach (NamespaceRefactorScriptEntry entry in entries)
		{
			if (entry == null)
				continue;

			if (
				!string.IsNullOrWhiteSpace(normalizedTargetFolderPath)
				&& !IsFolderOrDescendant(entry.FolderPath, normalizedTargetFolderPath)
			)
			{
				continue;
			}

			AddScriptPath(result, seenPaths, entry.ScriptPath);
		}

		return result;
	}

	internal IReadOnlyList<string> NormalizeScriptPaths(IEnumerable<string> scriptPaths)
	{
		List<string> result = new();
		HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);

		if (scriptPaths == null)
			return result;

		foreach (string scriptPath in scriptPaths)
			AddScriptPath(result, seenPaths, scriptPath);

		return result;
	}

	internal IReadOnlyList<string> CombineScriptPaths(params IEnumerable<string>[] pathGroups)
	{
		List<string> result = new();
		HashSet<string> seenPaths = new(StringComparer.OrdinalIgnoreCase);

		if (pathGroups == null)
			return result;

		foreach (IEnumerable<string> pathGroup in pathGroups)
		{
			if (pathGroup == null)
				continue;

			foreach (string scriptPath in pathGroup)
				AddScriptPath(result, seenPaths, scriptPath);
		}

		return result;
	}

	internal NamespaceRefactorProjectInventoryResult BuildProjectInventory(
		IEnumerable<string> linkedScriptPaths
	)
	{
		IReadOnlyList<string> normalizedLinkedScriptPaths = NormalizeScriptPaths(linkedScriptPaths);
		string projectRoot;

		try
		{
			projectRoot = _globalizePath("res://");
		}
		catch (Exception exception)
		{
			return new NamespaceRefactorProjectInventoryResult(
				normalizedLinkedScriptPaths,
				exception.Message,
				true
			);
		}

		if (string.IsNullOrWhiteSpace(projectRoot) || !Directory.Exists(projectRoot))
		{
			return new NamespaceRefactorProjectInventoryResult(
				normalizedLinkedScriptPaths,
				"",
				true
			);
		}

		try
		{
			IEnumerable<string> projectScriptPaths = Directory.EnumerateFiles(
				projectRoot,
				"*.cs",
				SearchOption.AllDirectories
			);
			List<string> localizedProjectScriptPaths = new();

			foreach (string projectScriptPath in projectScriptPaths)
				localizedProjectScriptPaths.Add(_localizePath(projectScriptPath));

			return new NamespaceRefactorProjectInventoryResult(
				CombineScriptPaths(localizedProjectScriptPaths, normalizedLinkedScriptPaths),
				"",
				false
			);
		}
		catch (Exception exception)
		{
			return new NamespaceRefactorProjectInventoryResult(
				normalizedLinkedScriptPaths,
				exception.Message,
				true
			);
		}
	}

	private bool IsFolderOrDescendant(string entryFolderPath, string targetFolderPath)
	{
		string normalizedEntryFolderPath = NormalizeFolderPath(entryFolderPath);
		string normalizedTargetFolderPath = NormalizeFolderPath(targetFolderPath);

		return normalizedEntryFolderPath.Equals(
				normalizedTargetFolderPath,
				StringComparison.OrdinalIgnoreCase
			)
			|| normalizedEntryFolderPath.StartsWith(
				$"{normalizedTargetFolderPath}/",
				StringComparison.OrdinalIgnoreCase
			);
	}

	private string NormalizeFolderPath(string folderPath)
	{
		return _normalizePath(folderPath ?? "").Trim('/');
	}

	private void AddScriptPath(
		ICollection<string> result,
		ISet<string> seenPaths,
		string scriptPath
	)
	{
		if (string.IsNullOrWhiteSpace(scriptPath))
			return;

		string normalizedScriptPath = _normalizePath(scriptPath);

		if (
			string.IsNullOrWhiteSpace(normalizedScriptPath)
			|| !normalizedScriptPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
			|| !seenPaths.Add(normalizedScriptPath)
		)
		{
			return;
		}

		result.Add(normalizedScriptPath);
	}
}
#endif
