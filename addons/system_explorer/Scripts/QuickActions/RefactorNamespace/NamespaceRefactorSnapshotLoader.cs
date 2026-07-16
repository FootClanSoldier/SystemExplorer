#if TOOLS
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal sealed class NamespaceSnapshotLoadResult
{
    internal IReadOnlyDictionary<string, NamespaceScriptSnapshot> SnapshotsByPath { get; }
    internal IReadOnlyList<NamespaceScriptSnapshot> SnapshotsInRequestOrder { get; }
    internal IReadOnlyList<string> MissingPaths { get; }
    internal IReadOnlyList<string> FailedPaths { get; }

    internal NamespaceSnapshotLoadResult(
        IDictionary<string, NamespaceScriptSnapshot> snapshotsByPath,
        IEnumerable<NamespaceScriptSnapshot> snapshotsInRequestOrder,
        IEnumerable<string> missingPaths,
        IEnumerable<string> failedPaths
    )
    {
        Dictionary<string, NamespaceScriptSnapshot> snapshotCopy = new(
            StringComparer.OrdinalIgnoreCase
        );

        if (snapshotsByPath != null)
        {
            foreach (KeyValuePair<string, NamespaceScriptSnapshot> pair in snapshotsByPath)
                snapshotCopy[pair.Key] = pair.Value;
        }

        SnapshotsByPath = new ReadOnlyDictionary<string, NamespaceScriptSnapshot>(snapshotCopy);
        SnapshotsInRequestOrder = CreateReadOnlyList(snapshotsInRequestOrder);
        MissingPaths = CreateReadOnlyList(missingPaths);
        FailedPaths = CreateReadOnlyList(failedPaths);
    }

    private static IReadOnlyList<T> CreateReadOnlyList<T>(IEnumerable<T> source)
    {
        List<T> copy = source == null ? new List<T>() : new List<T>(source);
        return copy.AsReadOnly();
    }
}

internal sealed class NamespaceRefactorSnapshotLoader
{
    private readonly Func<string, string> _normalizePath;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, string> _readText;

    internal NamespaceRefactorSnapshotLoader(
        Func<string, string> normalizePath,
        Func<string, bool> fileExists,
        Func<string, string> readText
    )
    {
        _normalizePath = normalizePath ?? throw new ArgumentNullException(nameof(normalizePath));
        _fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
        _readText = readText ?? throw new ArgumentNullException(nameof(readText));
    }

    internal NamespaceSnapshotLoadResult Load(IEnumerable<string> scriptPaths)
    {
        return LoadInto(scriptPaths, null);
    }

    internal NamespaceSnapshotLoadResult LoadInto(
        IEnumerable<string> scriptPaths,
        IReadOnlyDictionary<string, NamespaceScriptSnapshot> existingSnapshots
    )
    {
        Dictionary<string, NamespaceScriptSnapshot> snapshotsByPath = new(
            StringComparer.OrdinalIgnoreCase
        );

        if (existingSnapshots != null)
        {
            foreach (KeyValuePair<string, NamespaceScriptSnapshot> pair in existingSnapshots)
            {
                NamespaceScriptSnapshot snapshot = pair.Value;

                if (snapshot == null)
                    continue;

                string normalizedSnapshotPath;

                try
                {
                    normalizedSnapshotPath = _normalizePath(snapshot.Path);
                }
                catch
                {
                    continue;
                }

                if (
                    string.IsNullOrWhiteSpace(normalizedSnapshotPath)
                    || !normalizedSnapshotPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                )
                {
                    continue;
                }

                NamespaceScriptSnapshot normalizedSnapshot = string.Equals(
                    snapshot.Path,
                    normalizedSnapshotPath,
                    StringComparison.Ordinal
                )
                    ? snapshot
                    : new NamespaceScriptSnapshot(normalizedSnapshotPath, snapshot.Text);

                snapshotsByPath[normalizedSnapshotPath] = normalizedSnapshot;
            }
        }

        List<NamespaceScriptSnapshot> snapshotsInRequestOrder = new();
        List<string> missingPaths = new();
        List<string> failedPaths = new();
        HashSet<string> requestedPaths = new(StringComparer.OrdinalIgnoreCase);

        if (scriptPaths != null)
        {
            foreach (string sourcePath in scriptPaths)
            {
                string scriptPath = _normalizePath(sourcePath ?? "");

                if (
                    string.IsNullOrWhiteSpace(scriptPath)
                    || !scriptPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    || !requestedPaths.Add(scriptPath)
                )
                {
                    continue;
                }

                if (snapshotsByPath.TryGetValue(scriptPath, out NamespaceScriptSnapshot existing))
                {
                    snapshotsInRequestOrder.Add(existing);
                    continue;
                }

                bool exists;

                try
                {
                    exists = _fileExists(scriptPath);
                }
                catch
                {
                    failedPaths.Add(scriptPath);
                    continue;
                }

                if (!exists)
                {
                    missingPaths.Add(scriptPath);
                    continue;
                }

                try
                {
                    NamespaceScriptSnapshot snapshot = new(scriptPath, _readText(scriptPath));
                    snapshotsByPath[scriptPath] = snapshot;
                    snapshotsInRequestOrder.Add(snapshot);
                }
                catch
                {
                    failedPaths.Add(scriptPath);
                }
            }
        }

        return new NamespaceSnapshotLoadResult(
            snapshotsByPath,
            snapshotsInRequestOrder,
            missingPaths,
            failedPaths
        );
    }
}
#endif
