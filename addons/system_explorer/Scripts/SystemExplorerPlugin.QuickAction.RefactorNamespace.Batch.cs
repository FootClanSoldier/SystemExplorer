#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using SystemExplorer.EditorIntegration.ScriptEditing;
using SystemExplorer.QuickActions.RefactorNamespace;

public partial class SystemExplorerPlugin
{
    #region Quick Actions - Refactor Namespace Batch
    private bool _pendingRefactorNamespaceIsBatch;
    private bool _pendingRefactorNamespaceAddsNamespaceOnly;
    private List<string> _pendingBatchRefactorNamespaceScriptPaths = new();
    private List<string> _pendingBatchRefactorNamespaceNamespaces = new();

    private void OpenBatchRefactorNamespaceDialog(string metadata)
    {
        if (!EnsureSystemsLoadedForTreeOperation("Refactor Namespace"))
            return;

        List<string> scriptPaths = GetRefactorNamespaceScriptPathsForMetadata(metadata);

        if (scriptPaths.Count == 0)
        {
            DebugLog(
                $"Refactor Namespace batch cancelled: no C# scripts found for metadata '{metadata}'."
            );
            return;
        }

        Dictionary<string, string> namespacesByPath = ReadRefactorNamespaceNamespacesByPath(
            scriptPaths
        );
        List<string> namespaces = namespacesByPath
            .Values.Where(namespaceName => !string.IsNullOrWhiteSpace(namespaceName))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(namespaceName => namespaceName, StringComparer.Ordinal)
            .ToList();
        bool hasScriptsWithoutNamespace = namespacesByPath.Values.Any(string.IsNullOrWhiteSpace);

        if (namespaces.Count == 0 && !hasScriptsWithoutNamespace)
        {
            DebugLog(
                $"Refactor Namespace batch cancelled: no namespace candidates found for metadata '{metadata}'."
            );
            return;
        }

        _pendingRefactorNamespaceMetadata = metadata;
        _pendingRefactorNamespaceIsBatch = true;
        _pendingRefactorNamespaceAddsNamespaceOnly = false;
        _pendingBatchRefactorNamespaceScriptPaths = scriptPaths;
        _pendingBatchRefactorNamespaceNamespaces = namespaces;

        ConfigureRefactorNamespaceDialogForBatch(namespaces, hasScriptsWithoutNamespace);

        ApplyRefactorNamespaceDialogSize();
        _refactorNamespaceDialog.PopupCentered(RefactorNamespaceDialogSize);
        CallDeferred(nameof(ApplyRefactorNamespaceDialogSize));

        _newNamespaceInput.GrabFocus();
        _newNamespaceInput.SelectAll();
    }

    private void ConfigureRefactorNamespaceDialogForBatch(
        List<string> namespaces,
        bool hasScriptsWithoutNamespace
    )
    {
        _refactorNamespaceDescriptionLabel.Text =
            "Refactor namespaces for scripts under the selected System or Folder.";
        _newNamespaceLabel.Visible = true;
        _newNamespaceInput.Visible = true;
        _oldNamespaceLabel.Visible = false;
        _oldNamespaceInput.Visible = false;
        _refactorNamespaceApplyToLabel.Visible = true;
        _refactorNamespaceExistingNamespaceOption.Visible = true;
        _refactorNamespaceExistingNamespaceDropdown.Visible = true;
        _refactorNamespaceWithoutNamespaceOption.Visible = true;

        _newNamespaceInput.Text = namespaces.Count > 0 ? namespaces[0] : "";
        _oldNamespaceInput.Text = "";

        _refactorNamespaceExistingNamespaceDropdown.Clear();
        foreach (string namespaceName in namespaces)
            _refactorNamespaceExistingNamespaceDropdown.AddItem(namespaceName);

        bool hasExistingNamespaces = namespaces.Count > 0;
        _refactorNamespaceExistingNamespaceOption.Disabled = !hasExistingNamespaces;
        _refactorNamespaceExistingNamespaceDropdown.Disabled = !hasExistingNamespaces;
        _refactorNamespaceWithoutNamespaceOption.Disabled = !hasScriptsWithoutNamespace;

        bool useExistingNamespaceMode = hasExistingNamespaces;
        SetRefactorNamespaceBatchApplyMode(useExistingNamespaceMode);
    }

    private void ConfirmBatchRefactorNamespaceDialog(string newNamespace)
    {
        if (!NamespaceTextRewriter.IsValidNamespaceName(newNamespace))
        {
            DebugLog(
                "Refactor Namespace batch cancelled: new namespace must be a valid C# namespace name."
            );
            return;
        }

        if (_refactorNamespaceWithoutNamespaceOption.ButtonPressed)
        {
            DebugLogOperation("Refactor Namespace Batch Add Confirmed", newNamespace);
            AddNamespaceToScripts(
                GetBatchRefactorNamespaceScriptsWithoutNamespace(),
                newNamespace,
                "Refactor Namespace Batch Add"
            );
            ClearPendingRefactorNamespaceState();
            return;
        }

        string oldNamespace = GetSelectedBatchRefactorNamespace();

        if (!NamespaceTextRewriter.IsValidNamespaceName(oldNamespace))
        {
            DebugLog("Refactor Namespace batch cancelled: no valid old namespace was selected.");
            return;
        }

        if (oldNamespace == newNamespace)
        {
            DebugLog("Refactor Namespace batch cancelled: namespace is unchanged.");
            return;
        }

        DebugLogOperation(
            "Refactor Namespace Batch Confirmed",
            $"{oldNamespace} -> {newNamespace}"
        );
        RefactorNamespaceForScriptsWithNamespace(
            _pendingBatchRefactorNamespaceScriptPaths,
            oldNamespace,
            newNamespace
        );
        ClearPendingRefactorNamespaceState();
    }

    private void OnRefactorNamespaceExistingNamespaceOptionToggled(bool pressed)
    {
        if (!pressed)
            return;

        SetRefactorNamespaceBatchApplyMode(true);
    }

    private void OnRefactorNamespaceExistingNamespaceSelected(long index)
    {
        if (_newNamespaceInput == null || _refactorNamespaceExistingNamespaceDropdown == null)
            return;

        if (index < 0 || index >= _refactorNamespaceExistingNamespaceDropdown.ItemCount)
            return;

        _newNamespaceInput.Text = _refactorNamespaceExistingNamespaceDropdown.GetItemText(
            (int)index
        );
        _newNamespaceInput.GrabFocus();
        _newNamespaceInput.SelectAll();
    }

    private void OnRefactorNamespaceWithoutNamespaceOptionToggled(bool pressed)
    {
        if (!pressed)
            return;

        SetRefactorNamespaceBatchApplyMode(false);
    }

    private void SetRefactorNamespaceBatchApplyMode(bool useExistingNamespaceMode)
    {
        if (_refactorNamespaceExistingNamespaceOption == null)
            return;

        _refactorNamespaceExistingNamespaceOption.SetPressedNoSignal(useExistingNamespaceMode);
        _refactorNamespaceWithoutNamespaceOption.SetPressedNoSignal(!useExistingNamespaceMode);
        _refactorNamespaceExistingNamespaceDropdown.Disabled =
            !useExistingNamespaceMode || _refactorNamespaceExistingNamespaceDropdown.ItemCount == 0;
    }

    private string GetSelectedBatchRefactorNamespace()
    {
        if (_refactorNamespaceExistingNamespaceDropdown == null)
            return "";

        int selectedIndex = _refactorNamespaceExistingNamespaceDropdown.Selected;
        return selectedIndex >= 0
            ? _refactorNamespaceExistingNamespaceDropdown.GetItemText(selectedIndex)
            : "";
    }

    private List<string> GetBatchRefactorNamespaceScriptsWithoutNamespace()
    {
        return _pendingBatchRefactorNamespaceScriptPaths
            .Where(path => string.IsNullOrWhiteSpace(ReadNamespaceFromScript(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool RefactorNamespaceForScriptsWithNamespace(
        IEnumerable<string> scriptPaths,
        string oldNamespace,
        string newNamespace
    )
    {
        if (!EnsureSystemsLoadedForTreeOperation("Refactor Namespace"))
            return false;

        List<string> targetScriptPaths =
            scriptPaths
                ?.Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(ScriptPathUtility.Normalize)
                .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            ?? new List<string>();

        if (targetScriptPaths.Count == 0)
        {
            DebugLog("Refactor Namespace batch cancelled: no C# scripts were selected.");
            return false;
        }

        BeginBatchScriptEditorContextPreservation();

        try
        {
            HashSet<string> requiredPaths = targetScriptPaths.ToHashSet(
                StringComparer.OrdinalIgnoreCase
            );
            HashSet<string> candidatePaths = targetScriptPaths
                .Concat(GetRefactorNamespaceProjectCSharpFilePaths())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (
                !TryAutosaveOpenRefactorNamespaceCandidateScriptsBeforeBuild(
                    candidatePaths,
                    requiredPaths,
                    out bool didAutosaveCandidateScripts,
                    out string candidateAutosaveFailureMessage,
                    allowScriptEditorActivation: false,
                    namespaceReferenceToProtect: oldNamespace
                )
            )
            {
                GD.PushWarning(
                    string.IsNullOrWhiteSpace(candidateAutosaveFailureMessage)
                        ? "Refactor Namespace cancelled: open script buffer(s) could not be autosaved safely before scanning namespace usages."
                        : candidateAutosaveFailureMessage
                );
                return false;
            }

            if (didAutosaveCandidateScripts)
                DebugLog(
                    "Refactor Namespace batch save-first pre-scan saved open script buffer(s)."
                );

            if (
                !TryBuildBatchRefactorNamespacePendingWrites(
                    targetScriptPaths,
                    oldNamespace,
                    newNamespace,
                    out string selectedScriptPath,
                    out Dictionary<string, string> originalTextsByPath,
                    out Dictionary<string, string> pendingWrites
                )
            )
            {
                return false;
            }

            return ApplyRefactorNamespacePendingWrites(
                selectedScriptPath,
                originalTextsByPath,
                pendingWrites,
                "Refactor Namespace Batch",
                "",
                false
            );
        }
        finally
        {
            EndBatchScriptEditorContextPreservation();
        }
    }

    private bool TryBuildBatchRefactorNamespacePendingWrites(
        IEnumerable<string> targetScriptPaths,
        string oldNamespace,
        string newNamespace,
        out string selectedScriptPath,
        out Dictionary<string, string> originalTextsByPath,
        out Dictionary<string, string> pendingWrites
    )
    {
        selectedScriptPath = "";
        originalTextsByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        pendingWrites = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        List<string> projectScriptPaths = GetRefactorNamespaceProjectCSharpFilePaths().ToList();
        NamespaceRefactorPreparationResult preparationResult =
            RefactorNamespacePreparationService.PrepareReplace(
                targetScriptPaths,
                projectScriptPaths,
                projectScriptPaths,
                oldNamespace,
                newNamespace
            );

        foreach (string scriptPath in preparationResult.MissingTargetPaths)
            DebugLog($"Refactor Namespace batch skipped missing script '{scriptPath}'.");

        foreach (string scriptPath in preparationResult.FailedTargetPaths)
            DebugLog($"Refactor Namespace batch skipped unreadable script '{scriptPath}'.");

        NamespaceRefactorPlanResult result = preparationResult.PlanResult;

        foreach (string scriptPath in result.NamespaceRewriteFailedPaths)
        {
            DebugLog(
                $"Refactor Namespace batch skipped '{scriptPath}' because its namespace declaration could not be updated."
            );
        }

        if (!preparationResult.Success)
        {
            DebugLog(
                $"Refactor Namespace batch cancelled: no scripts with namespace '{oldNamespace}' could be updated."
            );
            return false;
        }

        CopyNamespaceRefactorPlanToPendingWrites(
            result.Plan,
            out selectedScriptPath,
            out originalTextsByPath,
            out pendingWrites
        );
        return true;
    }

    private Dictionary<string, string> ReadRefactorNamespaceNamespacesByPath(
        IEnumerable<string> scriptPaths
    )
    {
        NamespaceRefactorNamespaceScanResult scanResult =
            RefactorNamespacePreparationService.ScanNamespaces(scriptPaths);

        foreach (string scriptPath in scanResult.MissingPaths)
        {
            DebugLog(
                $"Refactor Namespace batch skipped missing script while scanning namespaces '{scriptPath}'."
            );
        }

        foreach (string scriptPath in scanResult.FailedPaths)
        {
            DebugLog(
                $"Refactor Namespace batch skipped unreadable script while scanning namespaces '{scriptPath}'."
            );
        }

        return new Dictionary<string, string>(
            scanResult.NamespacesByPath,
            StringComparer.OrdinalIgnoreCase
        );
    }

    private List<string> GetRefactorNamespaceScriptPathsForMetadata(string metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
            return new List<string>();

        string systemName = GetSystemNameFromMetadata(metadata);

        if (
            string.IsNullOrWhiteSpace(systemName)
            || !_systems.TryGetValue(systemName, out List<string> entries)
        )
        {
            return new List<string>();
        }

        List<NamespaceRefactorScriptEntry> scriptEntries = entries
            .Where(IsScriptEntry)
            .Select(entry => new NamespaceRefactorScriptEntry(
                GetScriptPathFromEntry(entry),
                GetFolderPathFromEntry(entry)
            ))
            .ToList();
        string targetFolderPath = metadata.StartsWith("folder::")
            ? GetFolderPathFromMetadata(metadata)
            : "";

        return RefactorNamespaceScopeResolver
            .ResolveTargetScriptPaths(scriptEntries, targetFolderPath)
            .ToList();
    }

    private IEnumerable<string> GetRefactorNamespaceProjectCSharpFilePaths()
    {
        NamespaceRefactorProjectInventoryResult inventoryResult =
            RefactorNamespaceScopeResolver.BuildProjectInventory(GetLinkedCSharpFilePaths());

        if (!string.IsNullOrWhiteSpace(inventoryResult.FailureMessage))
        {
            DebugLog(
                $"Refactor Namespace could not scan project C# files: {inventoryResult.FailureMessage}"
            );
        }

        return inventoryResult.ScriptPaths;
    }

    #endregion
}
#endif
