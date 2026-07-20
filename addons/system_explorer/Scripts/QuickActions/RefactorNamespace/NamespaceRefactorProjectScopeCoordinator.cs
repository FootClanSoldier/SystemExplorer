#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal sealed class NamespaceRefactorProjectScopeCoordinator
{
	private readonly NamespaceRefactorScopeResolver _scopeResolver;
	private readonly Func<IReadOnlyDictionary<string, List<string>>> _systemsProvider;
	private readonly Func<string, string> _getSystemNameFromMetadata;
	private readonly Func<string, string> _getFolderPathFromMetadata;
	private readonly Func<string, string> _getEntryFromMetadata;
	private readonly Func<string, string> _getScriptPathFromEntry;
	private readonly Func<string, string> _getFolderPathFromEntry;
	private readonly string _sceneEntryMarker;
	private readonly Action<string> _debugLog;

	internal NamespaceRefactorProjectScopeCoordinator(
		NamespaceRefactorScopeResolver scopeResolver,
		Func<IReadOnlyDictionary<string, List<string>>> systemsProvider,
		Func<string, string> getSystemNameFromMetadata,
		Func<string, string> getFolderPathFromMetadata,
		Func<string, string> getEntryFromMetadata,
		Func<string, string> getScriptPathFromEntry,
		Func<string, string> getFolderPathFromEntry,
		string sceneEntryMarker,
		Action<string> debugLog
	)
	{
		_scopeResolver = scopeResolver ?? throw new ArgumentNullException(nameof(scopeResolver));
		_systemsProvider =
			systemsProvider ?? throw new ArgumentNullException(nameof(systemsProvider));
		_getSystemNameFromMetadata =
			getSystemNameFromMetadata
			?? throw new ArgumentNullException(nameof(getSystemNameFromMetadata));
		_getFolderPathFromMetadata =
			getFolderPathFromMetadata
			?? throw new ArgumentNullException(nameof(getFolderPathFromMetadata));
		_getEntryFromMetadata =
			getEntryFromMetadata ?? throw new ArgumentNullException(nameof(getEntryFromMetadata));
		_getScriptPathFromEntry =
			getScriptPathFromEntry
			?? throw new ArgumentNullException(nameof(getScriptPathFromEntry));
		_getFolderPathFromEntry =
			getFolderPathFromEntry
			?? throw new ArgumentNullException(nameof(getFolderPathFromEntry));
		_sceneEntryMarker =
			sceneEntryMarker ?? throw new ArgumentNullException(nameof(sceneEntryMarker));
		_debugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
	}

	internal List<string> ResolveBatchTargetScriptPaths(string metadata)
	{
		if (string.IsNullOrWhiteSpace(metadata))
			return new List<string>();

		string systemName = _getSystemNameFromMetadata(metadata);

		if (string.IsNullOrWhiteSpace(systemName))
			return new List<string>();

		IReadOnlyDictionary<string, List<string>> systems = _systemsProvider();

		if (systems == null || !systems.TryGetValue(systemName, out List<string> entries))
			return new List<string>();

		List<NamespaceRefactorScriptEntry> scriptEntries = entries
			.Where(IsScriptEntry)
			.Select(entry => new NamespaceRefactorScriptEntry(
				_getScriptPathFromEntry(entry),
				_getFolderPathFromEntry(entry)
			))
			.ToList();
		string targetFolderPath = metadata.StartsWith("folder::")
			? _getFolderPathFromMetadata(metadata)
			: "";

		return _scopeResolver
			.ResolveTargetScriptPaths(scriptEntries, targetFolderPath)
			.ToList();
	}

	internal IReadOnlyList<string> GetLinkedCSharpFilePaths()
	{
		IReadOnlyDictionary<string, List<string>> systems = _systemsProvider();

		if (systems == null)
			return Array.Empty<string>();

		IEnumerable<string> linkedScriptPaths = systems
			.Values.SelectMany(entries => entries)
			.Where(IsScriptEntry)
			.Select(_getScriptPathFromEntry);

		return _scopeResolver.NormalizeScriptPaths(linkedScriptPaths);
	}

	internal IReadOnlyList<string> BuildProjectCSharpFilePaths()
	{
		IReadOnlyList<string> linkedScriptPaths = GetLinkedCSharpFilePaths();
		NamespaceRefactorProjectInventoryResult inventoryResult =
			_scopeResolver.BuildProjectInventory(linkedScriptPaths);

		if (!string.IsNullOrWhiteSpace(inventoryResult.FailureMessage))
		{
			_debugLog(
				$"Refactor Namespace could not scan project C# files: {inventoryResult.FailureMessage}"
			);
		}

		return inventoryResult.ScriptPaths;
	}

	internal HashSet<string> BuildSingleCandidateScriptPaths(string metadata)
	{
		List<string> targetScriptPaths = new();

		if (!string.IsNullOrWhiteSpace(metadata) && metadata.StartsWith("script::"))
		{
			string selectedEntry = _getEntryFromMetadata(metadata);
			targetScriptPaths.Add(_getScriptPathFromEntry(selectedEntry));
		}

		IReadOnlyList<string> projectScriptPaths = BuildProjectCSharpFilePaths();

		return _scopeResolver
			.CombineScriptPaths(targetScriptPaths, projectScriptPaths)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
	}

	private bool IsScriptEntry(string entry)
	{
		if (string.IsNullOrWhiteSpace(entry))
			return false;

		string pathPart = _getScriptPathFromEntry(entry);

		return !pathPart.StartsWith(_sceneEntryMarker)
			&& pathPart.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
	}
}
#endif
