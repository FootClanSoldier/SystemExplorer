#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal sealed class NamespaceRefactorOperationRequestCoordinator
{
	private readonly NamespaceRefactorProjectScopeCoordinator _projectScopeCoordinator;
	private readonly NamespaceRefactorPlanBuildCoordinator _planBuildCoordinator;
	private readonly NamespaceRefactorOperationCoordinator _operationCoordinator;
	private readonly Func<string, bool> _ensureSystemsLoadedForTreeOperation;
	private readonly Func<EditorInterface> _editorInterfaceProvider;
	private readonly Func<string, string> _getEntryFromMetadata;
	private readonly Func<string, string> _getScriptPathFromEntry;
	private readonly Func<string, string> _normalizeScriptPath;
	private readonly Func<string, bool> _fileExists;
	private readonly Action<string, string> _openMissingScriptDialog;
	private readonly Action<string> _debugLog;
	private readonly Action _beginBatchScriptEditorContextPreservation;
	private readonly Action _endBatchScriptEditorContextPreservation;

	internal NamespaceRefactorOperationRequestCoordinator(
		NamespaceRefactorProjectScopeCoordinator projectScopeCoordinator,
		NamespaceRefactorPlanBuildCoordinator planBuildCoordinator,
		NamespaceRefactorOperationCoordinator operationCoordinator,
		Func<string, bool> ensureSystemsLoadedForTreeOperation,
		Func<EditorInterface> editorInterfaceProvider,
		Func<string, string> getEntryFromMetadata,
		Func<string, string> getScriptPathFromEntry,
		Func<string, string> normalizeScriptPath,
		Func<string, bool> fileExists,
		Action<string, string> openMissingScriptDialog,
		Action<string> debugLog,
		Action beginBatchScriptEditorContextPreservation,
		Action endBatchScriptEditorContextPreservation
	)
	{
		_projectScopeCoordinator =
			projectScopeCoordinator
			?? throw new ArgumentNullException(nameof(projectScopeCoordinator));
		_planBuildCoordinator =
			planBuildCoordinator
			?? throw new ArgumentNullException(nameof(planBuildCoordinator));
		_operationCoordinator =
			operationCoordinator
			?? throw new ArgumentNullException(nameof(operationCoordinator));
		_ensureSystemsLoadedForTreeOperation =
			ensureSystemsLoadedForTreeOperation
			?? throw new ArgumentNullException(nameof(ensureSystemsLoadedForTreeOperation));
		_editorInterfaceProvider =
			editorInterfaceProvider
			?? throw new ArgumentNullException(nameof(editorInterfaceProvider));
		_getEntryFromMetadata =
			getEntryFromMetadata ?? throw new ArgumentNullException(nameof(getEntryFromMetadata));
		_getScriptPathFromEntry =
			getScriptPathFromEntry
			?? throw new ArgumentNullException(nameof(getScriptPathFromEntry));
		_normalizeScriptPath =
			normalizeScriptPath ?? throw new ArgumentNullException(nameof(normalizeScriptPath));
		_fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
		_openMissingScriptDialog =
			openMissingScriptDialog
			?? throw new ArgumentNullException(nameof(openMissingScriptDialog));
		_debugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
		_beginBatchScriptEditorContextPreservation =
			beginBatchScriptEditorContextPreservation
			?? throw new ArgumentNullException(nameof(beginBatchScriptEditorContextPreservation));
		_endBatchScriptEditorContextPreservation =
			endBatchScriptEditorContextPreservation
			?? throw new ArgumentNullException(nameof(endBatchScriptEditorContextPreservation));
	}

	internal bool ExecuteSingleReplacement(
		NamespaceRefactorDiagnosticContext diagnosticContext,
		string metadata,
		string oldNamespace,
		string newNamespace
	)
	{
		if (!_ensureSystemsLoadedForTreeOperation("Refactor Namespace"))
		{
			diagnosticContext?.Log(
				"Cancellation",
				"Cancelled during Request; systems were unavailable; PlanBuilt=false; WritesStarted=false"
			);
			return false;
		}

		if (string.IsNullOrWhiteSpace(metadata) || !metadata.StartsWith("script::"))
		{
			diagnosticContext?.Log(
				"Cancellation",
				() => $"Cancelled during Request; invalid metadata='{metadata ?? ""}'; PlanBuilt=false; WritesStarted=false"
			);
			return false;
		}

		EditorInterface editorInterface = _editorInterfaceProvider();
		ScriptEditor scriptEditor = editorInterface?.GetScriptEditor();
		HashSet<string> candidatePaths =
			scriptEditor == null
				? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
				: _projectScopeCoordinator.BuildSingleCandidateScriptPaths(metadata);
		string selectedCandidateScriptPath = "";

		if (scriptEditor != null)
		{
			string selectedEntry = _getEntryFromMetadata(metadata);
			selectedCandidateScriptPath = _normalizeScriptPath(
				_getScriptPathFromEntry(selectedEntry)
			);
		}

		HashSet<string> requiredPaths = new(StringComparer.OrdinalIgnoreCase)
		{
			selectedCandidateScriptPath,
		};
		diagnosticContext?.Log(
			"Request",
			() =>
				$"OperationKind={diagnosticContext.OperationKind}; MetadataScope=Script; OldNamespace='{oldNamespace}'; NewNamespace='{newNamespace}'; ApplyMode=Replacement; SelectedSystems=0; SelectedFolders=0; SelectedScripts=1; CandidatePathCount={candidatePaths.Count}; RequiredPathCount={requiredPaths.Count}; TargetPathCount={(string.IsNullOrWhiteSpace(selectedCandidateScriptPath) ? 0 : 1)}; ActivationAllowed=true; PreflightMode={NamespaceRefactorOpenBufferPreflightMode.NonActivatingWithActivationFallback}; CurrentScriptPath='{diagnosticContext.TryGetCurrentScriptPath(scriptEditor)}'; CandidatePaths={diagnosticContext.FormatPaths(candidatePaths)}; RequiredPaths={diagnosticContext.FormatPaths(requiredPaths)}; TargetPaths={diagnosticContext.FormatPaths(new[] { selectedCandidateScriptPath })}"
		);
		Func<NamespaceRefactorPendingWriteBuildResult> buildPendingWriteSet = () =>
			BuildSingleReplacementPendingWriteSet(
				metadata,
				oldNamespace,
				newNamespace,
				diagnosticContext
			);

		return _operationCoordinator.ExecuteSingleReplacement(
			editorInterface,
			scriptEditor,
			candidatePaths,
			requiredPaths,
			buildPendingWriteSet,
			diagnosticContext
		);
	}

	internal bool ExecuteAddNamespace(
		NamespaceRefactorDiagnosticContext diagnosticContext,
		IEnumerable<string> scriptPaths,
		string newNamespace,
		string operationName
	)
	{
		if (!_ensureSystemsLoadedForTreeOperation(operationName))
		{
			diagnosticContext?.Log(
				"Cancellation",
				"Cancelled during Request; systems were unavailable; PlanBuilt=false; WritesStarted=false"
			);
			return false;
		}

		List<string> targetScriptPaths =
			scriptPaths
				?.Where(path => !string.IsNullOrWhiteSpace(path))
				.Select(_normalizeScriptPath)
				.Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList()
			?? new List<string>();

		if (targetScriptPaths.Count == 0)
		{
			diagnosticContext?.Log(
				"Cancellation",
				"Cancelled during Request; no C# target paths; PlanBuilt=false; WritesStarted=false"
			);
			_debugLog($"{operationName} cancelled: no C# scripts were selected.");
			return false;
		}

		bool preserveBatchUiState = operationName.Contains(
			"Batch",
			StringComparison.OrdinalIgnoreCase
		);
		if (preserveBatchUiState)
			_beginBatchScriptEditorContextPreservation();

		try
		{
			HashSet<string> requiredPaths = targetScriptPaths.ToHashSet(
				StringComparer.OrdinalIgnoreCase
			);
			NamespaceRefactorOpenBufferPreflightMode preflightMode = preserveBatchUiState
				? NamespaceRefactorOpenBufferPreflightMode.NonActivatingOnly
				: NamespaceRefactorOpenBufferPreflightMode.ActivatingOnly;
			int selectedFolderCount = diagnosticContext?.OperationKind.Contains(
				"Folder",
				StringComparison.OrdinalIgnoreCase
			) == true
				? 1
				: 0;
			int selectedSystemCount = preserveBatchUiState && selectedFolderCount == 0 ? 1 : 0;
			diagnosticContext?.Log(
				"Request",
				() =>
					$"OperationKind={diagnosticContext.OperationKind}; MetadataScope={(preserveBatchUiState ? (selectedFolderCount == 1 ? "Folder" : "System") : "Script")}; OldNamespace=''; NewNamespace='{newNamespace}'; ApplyMode=AddNamespace; SelectedSystems={selectedSystemCount}; SelectedFolders={selectedFolderCount}; SelectedScripts={targetScriptPaths.Count}; CandidatePathCount={targetScriptPaths.Count}; RequiredPathCount={requiredPaths.Count}; TargetPathCount={targetScriptPaths.Count}; ActivationAllowed={!preserveBatchUiState}; PreflightMode={preflightMode}; CurrentScriptPath='{diagnosticContext.TryGetCurrentScriptPath(_editorInterfaceProvider()?.GetScriptEditor())}'; CandidatePaths={diagnosticContext.FormatPaths(targetScriptPaths)}; RequiredPaths={diagnosticContext.FormatPaths(requiredPaths)}; TargetPaths={diagnosticContext.FormatPaths(targetScriptPaths)}"
			);
			return _operationCoordinator.ExecuteAddNamespace(
				targetScriptPaths,
				requiredPaths,
				newNamespace,
				operationName,
				!preserveBatchUiState,
				diagnosticContext
			);
		}
		finally
		{
			if (preserveBatchUiState)
				_endBatchScriptEditorContextPreservation();
		}
	}

	internal bool ExecuteBatchReplacement(
		NamespaceRefactorDiagnosticContext diagnosticContext,
		IEnumerable<string> scriptPaths,
		string oldNamespace,
		string newNamespace
	)
	{
		if (!_ensureSystemsLoadedForTreeOperation("Refactor Namespace"))
		{
			diagnosticContext?.Log(
				"Cancellation",
				"Cancelled during Request; systems were unavailable; PlanBuilt=false; WritesStarted=false"
			);
			return false;
		}

		List<string> targetScriptPaths =
			scriptPaths
				?.Where(path => !string.IsNullOrWhiteSpace(path))
				.Select(_normalizeScriptPath)
				.Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList()
			?? new List<string>();

		if (targetScriptPaths.Count == 0)
		{
			diagnosticContext?.Log(
				"Cancellation",
				"Cancelled during Request; no C# target paths; PlanBuilt=false; WritesStarted=false"
			);
			_debugLog("Refactor Namespace batch cancelled: no C# scripts were selected.");
			return false;
		}

		_beginBatchScriptEditorContextPreservation();

		try
		{
			HashSet<string> requiredPaths = targetScriptPaths.ToHashSet(
				StringComparer.OrdinalIgnoreCase
			);
			IReadOnlyList<string> projectPaths =
				_projectScopeCoordinator.BuildProjectCSharpFilePaths();
			HashSet<string> candidatePaths = targetScriptPaths
				.Concat(projectPaths)
				.ToHashSet(StringComparer.OrdinalIgnoreCase);
			int selectedFolderCount = diagnosticContext?.OperationKind.Contains(
				"Folder",
				StringComparison.OrdinalIgnoreCase
			) == true
				? 1
				: 0;
			int selectedSystemCount = selectedFolderCount == 0 ? 1 : 0;
			diagnosticContext?.Log(
				"Request",
				() =>
					$"OperationKind={diagnosticContext.OperationKind}; MetadataScope={(selectedFolderCount == 1 ? "Folder" : "System")}; OldNamespace='{oldNamespace}'; NewNamespace='{newNamespace}'; ApplyMode=Replacement; SelectedSystems={selectedSystemCount}; SelectedFolders={selectedFolderCount}; SelectedScripts={targetScriptPaths.Count}; CandidatePathCount={candidatePaths.Count}; RequiredPathCount={requiredPaths.Count}; TargetPathCount={targetScriptPaths.Count}; ActivationAllowed=false; PreflightMode={NamespaceRefactorOpenBufferPreflightMode.NonActivatingOnly}; CurrentScriptPath='{diagnosticContext.TryGetCurrentScriptPath(_editorInterfaceProvider()?.GetScriptEditor())}'; CandidatePaths={diagnosticContext.FormatPaths(candidatePaths)}; RequiredPaths={diagnosticContext.FormatPaths(requiredPaths)}; TargetPaths={diagnosticContext.FormatPaths(targetScriptPaths)}"
			);

			return _operationCoordinator.ExecuteBatchReplacement(
				targetScriptPaths,
				candidatePaths,
				requiredPaths,
				oldNamespace,
				newNamespace,
				() => _projectScopeCoordinator.GetLinkedCSharpFilePaths(),
				() => _projectScopeCoordinator.BuildProjectCSharpFilePaths(),
				diagnosticContext
			);
		}
		finally
		{
			_endBatchScriptEditorContextPreservation();
		}
	}

	private NamespaceRefactorPendingWriteBuildResult BuildSingleReplacementPendingWriteSet(
		string metadata,
		string oldNamespace,
		string newNamespace,
		NamespaceRefactorDiagnosticContext diagnosticContext
	)
	{
		if (string.IsNullOrWhiteSpace(metadata) || !metadata.StartsWith("script::"))
			return NamespaceRefactorPendingWriteBuildResult.Failed();

		string selectedEntry = _getEntryFromMetadata(metadata);
		string targetScriptPath = _normalizeScriptPath(
			_getScriptPathFromEntry(selectedEntry)
		);

		if (!_fileExists(targetScriptPath))
		{
			diagnosticContext?.Log(
				"Plan",
				() => $"Target missing before preparation; Path='{targetScriptPath}'"
			);
			_openMissingScriptDialog(selectedEntry, targetScriptPath);
			return NamespaceRefactorPendingWriteBuildResult.Failed();
		}

		IReadOnlyList<string> linkedCSharpFilePaths =
			_projectScopeCoordinator.GetLinkedCSharpFilePaths();
		IReadOnlyList<string> projectCSharpFilePaths =
			_projectScopeCoordinator.BuildProjectCSharpFilePaths();
		diagnosticContext?.Log(
			"Scope",
			() =>
				$"Single scope resolved; TargetCount=1; ReferencePathCount={linkedCSharpFilePaths.Count}; DeclarationPathCount={projectCSharpFilePaths.Count}; Targets={diagnosticContext.FormatPaths(new[] { targetScriptPath })}; References={diagnosticContext.FormatPaths(linkedCSharpFilePaths)}; Declarations={diagnosticContext.FormatPaths(projectCSharpFilePaths)}"
		);

		return _planBuildCoordinator.BuildSingleReplacement(
			selectedEntry,
			targetScriptPath,
			linkedCSharpFilePaths,
			projectCSharpFilePaths,
			oldNamespace,
			newNamespace,
			diagnosticContext
		);
	}
}
#endif
