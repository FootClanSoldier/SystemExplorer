#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal sealed class NamespaceRefactorBatchDialogPreparationResult
{
    internal bool Success { get; }
    internal IReadOnlyList<string> Namespaces { get; }
    internal bool HasScriptsWithoutNamespace { get; }

    private NamespaceRefactorBatchDialogPreparationResult(
        bool success,
        IEnumerable<string> namespaces,
        bool hasScriptsWithoutNamespace
    )
    {
        Success = success;
        List<string> namespaceCopy =
            namespaces == null ? new List<string>() : new List<string>(namespaces);
        Namespaces = namespaceCopy.AsReadOnly();
        HasScriptsWithoutNamespace = hasScriptsWithoutNamespace;
    }

    internal static NamespaceRefactorBatchDialogPreparationResult Succeeded(
        IEnumerable<string> namespaces,
        bool hasScriptsWithoutNamespace
    )
    {
        return new NamespaceRefactorBatchDialogPreparationResult(
            true,
            namespaces,
            hasScriptsWithoutNamespace
        );
    }

    internal static NamespaceRefactorBatchDialogPreparationResult Failed()
    {
        return new NamespaceRefactorBatchDialogPreparationResult(
            false,
            Array.Empty<string>(),
            false
        );
    }
}

internal sealed class NamespaceRefactorBatchDialogPreparationCoordinator
{
    private readonly NamespaceRefactorPreparationService _preparationService;
    private readonly Action<string> _debugLog;

    internal NamespaceRefactorBatchDialogPreparationCoordinator(
        NamespaceRefactorPreparationService preparationService,
        Action<string> debugLog
    )
    {
        _preparationService =
            preparationService ?? throw new ArgumentNullException(nameof(preparationService));
        _debugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
    }

    internal NamespaceRefactorBatchDialogPreparationResult PrepareBatchDialog(
        IEnumerable<string> scriptPaths,
        string metadata
    )
    {
        NamespaceRefactorNamespaceScanResult scanResult = _preparationService.ScanNamespaces(
            scriptPaths
        );

        foreach (string scriptPath in scanResult.MissingPaths)
        {
            _debugLog(
                $"Refactor Namespace batch skipped missing script while scanning namespaces '{scriptPath}'."
            );
        }

        foreach (string scriptPath in scanResult.FailedPaths)
        {
            _debugLog(
                $"Refactor Namespace batch skipped unreadable script while scanning namespaces '{scriptPath}'."
            );
        }

        List<string> namespaces = scanResult
            .NamespacesByPath.Values.Where(namespaceName =>
                !string.IsNullOrWhiteSpace(namespaceName)
            )
            .Distinct(StringComparer.Ordinal)
            .OrderBy(namespaceName => namespaceName, StringComparer.Ordinal)
            .ToList();
        bool hasScriptsWithoutNamespace = scanResult.NamespacesByPath.Values.Any(
            string.IsNullOrWhiteSpace
        );

        if (namespaces.Count == 0 && !hasScriptsWithoutNamespace)
        {
            _debugLog(
                $"Refactor Namespace batch cancelled: no namespace candidates found for metadata '{metadata}'."
            );
            return NamespaceRefactorBatchDialogPreparationResult.Failed();
        }

        return NamespaceRefactorBatchDialogPreparationResult.Succeeded(
            namespaces,
            hasScriptsWithoutNamespace
        );
    }
}
#endif
