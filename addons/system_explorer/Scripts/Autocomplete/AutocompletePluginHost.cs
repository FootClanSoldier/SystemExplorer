#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using SystemExplorer.Autocomplete.Confirmation;
using SystemExplorer.Autocomplete.Indexing;
using SystemExplorer.Autocomplete.Indexing.ActiveDocument;
using SystemExplorer.Autocomplete.Indexing.Context;
using SystemExplorer.Autocomplete.Indexing.Persistence;
using SystemExplorer.Autocomplete.Semantics;
using SystemExplorer.Autocomplete.Styling;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.Autocomplete;

internal sealed class AutocompletePluginHost
{
	private readonly AutocompleteIndexLifetime _indexLifetime;
	private readonly CSharpProjectIndex _projectIndex;
	private readonly CSharpProjectIndexPersistentCacheStore _persistentCacheStore;
	private readonly CSharpProjectIndexCacheCoordinator _cacheCoordinator;
	private readonly CSharpProjectIndexWorker _indexWorker;
	private readonly CSharpProjectIndexCoordinator _indexCoordinator;
	private readonly AutocompleteProjectIndexLifecycle _projectIndexLifecycle;
	private readonly CSharpActiveDocumentIndex _activeDocumentIndex;
	private readonly CSharpActiveDocumentIndexWorker _activeDocumentIndexWorker;
	private readonly CSharpActiveDocumentIndexCoordinator _activeDocumentIndexCoordinator;
	private readonly AutocompleteActiveDocumentIndexLifecycle _activeDocumentIndexLifecycle;
	private readonly CSharpSemanticMemberIndex _semanticMemberIndex;
	private readonly CSharpSemanticMemberWorker _semanticMemberWorker;
	private readonly CSharpSemanticMemberCoordinator _semanticMemberCoordinator;
	private readonly AutocompleteEditorBinding _editorBinding;
	private readonly AutocompleteCompletionCoordinator _completionCoordinator;
	private readonly AutocompleteCodeEditThemeController _themeController;
	private readonly AutocompletePrefixExtractor _prefixExtractor;
	private readonly AutocompleteCodeEditPresenter _presenter;
	private readonly AutocompleteCompletionMatchPolicy _matchPolicy;
	private readonly AutocompleteMemberCompletionFollowUp _memberCompletionFollowUp;
	private readonly ProjectTypeCompletionSource _projectTypeCompletionSource;
	private readonly ProjectMemberCompletionSource _projectMemberCompletionSource;
	private readonly AutocompleteCompletionOptionMetadataCodec _metadataCodec;
	private readonly AutocompleteCompletionConfirmationBridge _confirmationBridge;
	private readonly Action<string, string> _debugLog;
	private bool _isIssuingForcedMemberCompletionRequest;

	internal AutocompletePluginHost(
		Func<ScriptEditor> scriptEditorProvider,
		Func<EditorFileSystem> resourceFilesystemProvider,
		Func<string> globalProjectRootProvider,
		Func<GodotObject, StringName, string, string, bool> connectPluginSignal,
		Action<GodotObject, StringName, string, string> disconnectPluginSignal,
		string scriptChangedMethodName,
		string textChangedMethodName,
		string completionRequestedMethodName,
		string guiInputMethodName,
		string filesystemChangedMethodName,
		Action<string, string> debugLog
	)
	{
		_debugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
		_prefixExtractor = new AutocompletePrefixExtractor();
		_metadataCodec = new AutocompleteCompletionOptionMetadataCodec();
		_presenter = new AutocompleteCodeEditPresenter(_metadataCodec);
		var completionContextBuilder = new CSharpDocumentCompletionContextBuilder();
		var completionContextResolver = new CSharpCompletionContextResolver();
		var projectTypeConfirmationService = new AutocompleteProjectTypeConfirmationService(
			new CSharpUsingInsertionPlanner(
				completionContextBuilder,
				completionContextResolver
			),
			_debugLog
		);
		_confirmationBridge = new AutocompleteCompletionConfirmationBridge(
			_metadataCodec,
			projectTypeConfirmationService,
			_debugLog
		);
		_matchPolicy = new AutocompleteCompletionMatchPolicy();
		_memberCompletionFollowUp = new AutocompleteMemberCompletionFollowUp();

		_indexLifetime = new AutocompleteIndexLifetime();
		var typeScanner = new RoslynProjectTypeScanner(completionContextBuilder);
		var cacheJsonCodec = new CSharpProjectIndexCacheJsonCodec();

		_persistentCacheStore = new CSharpProjectIndexPersistentCacheStore(
			cacheJsonCodec
		);
		_cacheCoordinator = new CSharpProjectIndexCacheCoordinator(
			_indexLifetime,
			_persistentCacheStore
		);

		_projectIndex = new CSharpProjectIndex();
		var inventory = new CSharpProjectFileInventory();
		_indexWorker = new CSharpProjectIndexWorker(
			inventory,
			typeScanner,
			_persistentCacheStore
		);
		_indexCoordinator = new CSharpProjectIndexCoordinator(
			_indexLifetime,
			_projectIndex,
			_indexWorker,
			_cacheCoordinator
		);

		_activeDocumentIndex = new CSharpActiveDocumentIndex();
		_activeDocumentIndexWorker = new CSharpActiveDocumentIndexWorker(typeScanner);
		_activeDocumentIndexCoordinator = new CSharpActiveDocumentIndexCoordinator(
			_indexLifetime,
			_activeDocumentIndex,
			_activeDocumentIndexWorker
		);
		_activeDocumentIndexLifecycle = new AutocompleteActiveDocumentIndexLifecycle(
			_activeDocumentIndex,
			_activeDocumentIndexCoordinator,
			_debugLog
		);

		_semanticMemberIndex = new CSharpSemanticMemberIndex();
		_semanticMemberWorker = new CSharpSemanticMemberWorker(
			new CSharpSemanticMetadataReferenceProvider()
		);
		_semanticMemberCoordinator = new CSharpSemanticMemberCoordinator(
			_indexLifetime,
			_semanticMemberIndex,
			_semanticMemberWorker
		);

		_projectTypeCompletionSource = new ProjectTypeCompletionSource(
			() => _projectIndex.CurrentSnapshot,
			() => _activeDocumentIndex.CurrentSnapshot,
			completionContextResolver
		);
		_projectMemberCompletionSource = new ProjectMemberCompletionSource(
			() => _semanticMemberIndex.CurrentSnapshot,
			() => _projectIndex.CurrentSnapshot,
			() => _activeDocumentIndex.CurrentSnapshot
		);

		IAutocompleteCompletionSource[] completionSources =
		{
			_projectTypeCompletionSource,
			_projectMemberCompletionSource,
		};

		_completionCoordinator = new AutocompleteCompletionCoordinator(
			_prefixExtractor,
			_presenter,
			_matchPolicy,
			completionSources,
			_debugLog
		);

		var themeDefinition = new AutocompleteThemeDefinition
		{
			CompletionExistingColor = Colors.Transparent,
		};
		_themeController = new AutocompleteCodeEditThemeController(themeDefinition);
		var completionPrefixController = new AutocompleteCodeCompletionPrefixController();

		_editorBinding = new AutocompleteEditorBinding(
			scriptEditorProvider,
			connectPluginSignal,
			disconnectPluginSignal,
			scriptChangedMethodName,
			textChangedMethodName,
			completionRequestedMethodName,
			guiInputMethodName,
			InvalidatePendingValidations,
			completionPrefixController,
			_themeController
		);

		_projectIndexLifecycle = new AutocompleteProjectIndexLifecycle(
			resourceFilesystemProvider,
			globalProjectRootProvider,
			connectPluginSignal,
			disconnectPluginSignal,
			filesystemChangedMethodName,
			_indexCoordinator,
			_debugLog
		);
	}

	internal bool EnsureLifecycleCurrent()
	{
		bool editorBindingCurrent = _editorBinding.EnsureLifecycleCurrent();
		EnsureProjectIndexLifecycleCurrentBestEffort();
		DrainIndexBuildResults();
		EnsureSemanticProjectStateBestEffort();

		if (editorBindingCurrent)
			CaptureActiveDocumentIfNeededBestEffort("Ensure lifecycle current");

		DrainIndexBuildResults();
		return editorBindingCurrent;
	}

	internal void HandleProjectFilesystemChanged()
	{
		try
		{
			_projectIndexLifecycle.HandleFilesystemChanged();
		}
		catch (Exception exception)
		{
			_debugLog(
				"C# autocomplete project index filesystem handling failed",
				exception.ToString()
			);
		}

		DrainIndexBuildResults();
		EnsureSemanticProjectStateBestEffort();
	}

	internal void HandleScriptChanged()
	{
		DrainIndexBuildResults();
		_memberCompletionFollowUp.Clear();
		_completionCoordinator.InvalidatePendingValidations();
		_semanticMemberCoordinator.ResetActiveDocument();
		_activeDocumentIndexLifecycle.ResetForScriptChange();

		if (_editorBinding.RefreshCodeEditBinding())
			CaptureActiveDocumentIfNeededBestEffort("Active script changed");

		DrainIndexBuildResults();
	}

	internal void HandleCompletionRequested()
	{
		DrainIndexBuildResults();

		if (!_editorBinding.TryGetActiveCodeEdit(out CodeEdit codeEdit, out string scriptPath))
		{
			_editorBinding.RefreshCodeEditBinding();
			return;
		}

		EnsureProjectIndexLifecycleCurrentBestEffort();
		DrainIndexBuildResults();
		EnsureSemanticProjectStateBestEffort();
		CaptureActiveDocumentIfNeededBestEffort(
			codeEdit,
			scriptPath,
			"Code completion requested"
		);
		DrainIndexBuildResults();

		bool published = _completionCoordinator.HandleCompletionRequested(
			codeEdit,
			scriptPath
		);

		if (published)
			_memberCompletionFollowUp.Clear();
		DrainIndexBuildResults();
	}

	internal void HandleCodeEditGuiInput(InputEvent inputEvent)
	{
		if (inputEvent == null)
			return;

		if (!_editorBinding.TryGetActiveCodeEdit(out CodeEdit codeEdit, out _))
		{
			_editorBinding.RefreshCodeEditBinding();
			return;
		}

		_confirmationBridge.TryHandleGuiInput(codeEdit, inputEvent);
	}

	internal long BeginTextChangedValidation()
	{
		_memberCompletionFollowUp.Clear();
		_activeDocumentIndexLifecycle.MarkDirty();
		return _completionCoordinator.BeginTextChangedValidation();
	}

	internal bool IsValidationCurrent(long generation)
	{
		return _completionCoordinator.IsValidationCurrent(generation);
	}

	internal void ValidateAfterTextChanged(long generation)
	{
		DrainIndexBuildResults();

		if (!_completionCoordinator.IsValidationCurrent(generation))
			return;

		if (
			!_editorBinding.TryGetActiveCodeEdit(
				out CodeEdit codeEdit,
				out string scriptPath
			)
		)
		{
			_editorBinding.RefreshCodeEditBinding();
			return;
		}

		if (!_completionCoordinator.IsValidationCurrent(generation))
			return;

		CaptureActiveDocumentIfNeededBestEffort(
			codeEdit,
			scriptPath,
			"Deferred TextChanged capture"
		);

		if (!_completionCoordinator.IsValidationCurrent(generation))
			return;

		_completionCoordinator.ValidateAfterTextChanged(
			codeEdit,
			scriptPath,
			generation
		);
		DrainIndexBuildResults();
	}

	internal void InvalidatePendingValidations()
	{
		_memberCompletionFollowUp.Clear();
		_completionCoordinator.InvalidatePendingValidations();
	}

	internal void ResetTransientState()
	{
		_memberCompletionFollowUp.Clear();
		_completionCoordinator.Reset();
		_semanticMemberCoordinator.ResetTransientState();
		_activeDocumentIndexLifecycle.ResetTransientState();
		_projectIndexLifecycle.ResetTransientState();
		_cacheCoordinator.ResetTransientState();
	}

	internal void Shutdown()
	{
		_memberCompletionFollowUp.Clear();
		_projectIndexLifecycle.Shutdown();
		_indexCoordinator.Shutdown();
		_activeDocumentIndexLifecycle.Shutdown();
		_activeDocumentIndexCoordinator.Shutdown();
		_semanticMemberCoordinator.Shutdown();
		_cacheCoordinator.Shutdown();
		_indexLifetime.Shutdown();
		_completionCoordinator.InvalidatePendingValidations();
		_editorBinding.Shutdown();
		_themeController.Reset();
	}

	internal bool HasPendingCompletionProcessWork()
	{
		return _memberCompletionFollowUp.HasPendingWork;
	}

	internal void ClearPendingCompletionProcessWork()
	{
		_memberCompletionFollowUp.Clear();
	}

	internal void ProcessPendingCompletionWork()
	{
		if (_isIssuingForcedMemberCompletionRequest)
			return;

		DrainIndexBuildResults();

		if (
			!_memberCompletionFollowUp.TryGetPending(
				out AutocompleteMemberCompletionFollowUp.PendingDemand pending
			)
		)
		{
			return;
		}

		if (
			!_editorBinding.TryGetActiveCodeEdit(
				out CodeEdit codeEdit,
				out string scriptPath
			)
		)
		{
			_memberCompletionFollowUp.Clear();
			return;
		}

		string normalizedScriptPath = ScriptPathUtility.Normalize(scriptPath);
		if (
			!string.Equals(
				pending.ScriptPath,
				normalizedScriptPath,
				StringComparison.OrdinalIgnoreCase
			)
			|| !_prefixExtractor.TryExtract(
				codeEdit,
				out string prefix,
				out int caretLine,
				out int caretColumn,
				out AutocompleteRequestKind kind,
				out int prefixStartColumn
			)
			|| kind != AutocompleteRequestKind.MemberAccess
			|| prefix.Length != 0
			|| caretLine != pending.CaretLine
			|| caretColumn != pending.CaretColumn
			|| prefixStartColumn != pending.PrefixStartColumn
		)
		{
			_memberCompletionFollowUp.Clear();
			return;
		}

		CSharpProjectIndexSnapshot projectSnapshot = _projectIndex.CurrentSnapshot;
		CSharpActiveDocumentIndexSnapshot activeDocumentSnapshot =
			_activeDocumentIndex.CurrentSnapshot;
		CSharpSemanticMemberIndexSnapshot semanticSnapshot =
			_semanticMemberIndex.CurrentSnapshot;

		if (
			projectSnapshot == null
			|| !projectSnapshot.HasBuiltAtLeastOnce
			|| activeDocumentSnapshot == null
			|| !activeDocumentSnapshot.HasBuiltAtLeastOnce
			|| semanticSnapshot == null
			|| !semanticSnapshot.HasBuiltAtLeastOnce
		)
		{
			return;
		}

		if (
			IsNewerSnapshotForPendingDemand(
				activeDocumentSnapshot.ScriptPath,
				activeDocumentSnapshot.Revision,
				pending
			)
			|| IsNewerSnapshotForPendingDemand(
				semanticSnapshot.ScriptPath,
				semanticSnapshot.ActiveDocumentRevision,
				pending
			)
		)
		{
			_memberCompletionFollowUp.Clear();
			return;
		}

		if (
			!string.Equals(
				activeDocumentSnapshot.ScriptPath,
				pending.ScriptPath,
				StringComparison.OrdinalIgnoreCase
			)
			|| activeDocumentSnapshot.Revision != pending.ActiveDocumentRevision
			|| !string.Equals(
				semanticSnapshot.ScriptPath,
				pending.ScriptPath,
				StringComparison.OrdinalIgnoreCase
			)
			|| semanticSnapshot.ActiveDocumentRevision != pending.ActiveDocumentRevision
			|| semanticSnapshot.ProjectGeneration != projectSnapshot.Generation
		)
		{
			return;
		}

		if (
			!semanticSnapshot.TryGetMemberAccess(
				pending.CaretLine,
				pending.PrefixStartColumn,
				out CSharpSemanticMemberAccess matchingAccess
			)
			|| matchingAccess.Members.Count == 0
		)
		{
			_memberCompletionFollowUp.Clear();
			return;
		}

		_memberCompletionFollowUp.Clear();
		_isIssuingForcedMemberCompletionRequest = true;

		try
		{
			codeEdit.RequestCodeCompletion(true);
		}
		catch (Exception exception)
		{
			_debugLog(
				"C# autocomplete forced bare-member request failed",
				exception.ToString()
			);
		}
		finally
		{
			_isIssuingForcedMemberCompletionRequest = false;
		}
	}

	private void TryArmBareMemberCompletionFollowUp(
		CodeEdit codeEdit,
		string scriptPath,
		CSharpActiveDocumentIndexRequest capturedRequest
	)
	{
		if (capturedRequest == null || !IsValidGodotObject(codeEdit))
			return;

		if (
			!string.Equals(
				ScriptPathUtility.Normalize(scriptPath),
				capturedRequest.ScriptPath,
				StringComparison.OrdinalIgnoreCase
			)
			|| !_prefixExtractor.TryExtract(
				codeEdit,
				out string prefix,
				out int caretLine,
				out int caretColumn,
				out AutocompleteRequestKind kind,
				out int prefixStartColumn
			)
			|| kind != AutocompleteRequestKind.MemberAccess
			|| prefix.Length != 0
			|| caretLine < 0
			|| prefixStartColumn != caretColumn
		)
		{
			return;
		}

		_memberCompletionFollowUp.Arm(
			capturedRequest.Revision,
			capturedRequest.ScriptPath,
			caretLine,
			caretColumn,
			prefixStartColumn
		);
	}

	private static bool IsNewerSnapshotForPendingDemand(
		string snapshotScriptPath,
		long snapshotRevision,
		AutocompleteMemberCompletionFollowUp.PendingDemand pending
	)
	{
		return snapshotRevision > pending.ActiveDocumentRevision
			&& string.Equals(
				ScriptPathUtility.Normalize(snapshotScriptPath),
				pending.ScriptPath,
				StringComparison.OrdinalIgnoreCase
			);
	}

	private static bool IsValidGodotObject(GodotObject source)
	{
		return source != null && GodotObject.IsInstanceValid(source);
	}

	private void EnsureProjectIndexLifecycleCurrentBestEffort()
	{
		try
		{
			_projectIndexLifecycle.EnsureLifecycleCurrent();
		}
		catch (Exception exception)
		{
			_debugLog(
				"C# autocomplete project index lifecycle failed",
				exception.ToString()
			);
		}
	}

	private void EnsureSemanticProjectStateBestEffort()
	{
		try
		{
			CSharpProjectIndexSnapshot snapshot = _projectIndex.CurrentSnapshot;
			if (snapshot != null && snapshot.HasBuiltAtLeastOnce)
				_semanticMemberCoordinator.RequestProjectSnapshot(snapshot);
		}
		catch (Exception exception)
		{
			_debugLog(
				"C# autocomplete semantic project routing failed",
				exception.ToString()
			);
		}
	}

	private void CaptureActiveDocumentIfNeededBestEffort(string reason)
	{
		if (
			_editorBinding.TryGetActiveCodeEdit(
				out CodeEdit codeEdit,
				out string scriptPath
			)
		)
		{
			CaptureActiveDocumentIfNeededBestEffort(codeEdit, scriptPath, reason);
		}
	}

	private void CaptureActiveDocumentIfNeededBestEffort(
		CodeEdit codeEdit,
		string scriptPath,
		string reason
	)
	{
		try
		{
			if (_activeDocumentIndexLifecycle.NeedsCapture(scriptPath))
			{
				CSharpActiveDocumentIndexRequest capturedRequest =
					_activeDocumentIndexLifecycle.CapturePendingText(
						codeEdit,
						scriptPath,
						reason
					);

				if (capturedRequest != null)
				{
					bool semanticRequestAccepted =
						_semanticMemberCoordinator.RequestActiveDocument(
							new CSharpSemanticActiveDocumentRequest(
								capturedRequest.Revision,
								capturedRequest.Reason,
								capturedRequest.ScriptPath,
								capturedRequest.SourceText
							)
						);

					if (semanticRequestAccepted)
					{
						TryArmBareMemberCompletionFollowUp(
							codeEdit,
							scriptPath,
							capturedRequest
						);
					}
				}
			}
		}
		catch (Exception exception)
		{
			_debugLog(
				"C# autocomplete active document lifecycle failed",
				exception.ToString()
			);
		}
	}

	private void DrainIndexBuildResults()
	{
		DrainProjectIndexBuildResult();
		DrainActiveDocumentIndexBuildResult();
		DrainSemanticMemberBuildResult();
		DrainCacheWriteResult();
	}

	private void DrainProjectIndexBuildResult()
	{
		if (!_indexCoordinator.TryTakeLatestBuildResult(out CSharpProjectIndexBuildResult result))
			return;

		string operation = result.Status switch
		{
			CSharpProjectIndexBuildStatus.Succeeded =>
				"C# autocomplete project index build completed",
			CSharpProjectIndexBuildStatus.Stale =>
				"C# autocomplete project index build stale",
			CSharpProjectIndexBuildStatus.Cancelled =>
				"C# autocomplete project index build cancelled",
			_ => "C# autocomplete project index build failed",
		};

		_debugLog(operation, result.CreateDebugSummary());

		if (result.IsFailed)
			_memberCompletionFollowUp.Clear();

		if (result.IsSuccessful && result.Snapshot != null)
			_semanticMemberCoordinator.RequestProjectSnapshot(result.Snapshot);
	}

	private void DrainCacheWriteResult()
	{
		if (
			!_cacheCoordinator.TryTakeLatestReportableWriteResult(
				out CSharpProjectIndexCacheWriteResult result
			)
		)
		{
			return;
		}

		string operation = result.Status switch
		{
			CSharpProjectIndexCacheWriteStatus.Succeeded =>
				"C# autocomplete project index cache write completed",
			_ => "C# autocomplete project index cache write failed",
		};

		_debugLog(operation, result.CreateDebugSummary());
	}

	private void DrainSemanticMemberBuildResult()
	{
		if (
			!_semanticMemberCoordinator.TryTakeLatestReportableBuildResult(
				out CSharpSemanticMemberBuildResult result
			)
		)
		{
			return;
		}

		if (result.IsFailed)
		{
			_memberCompletionFollowUp.ClearIfMatches(
				result.ActiveDocumentRevision,
				result.ScriptPath
			);
		}

		string operation = result.IsFailed
			? "C# autocomplete semantic member build failed"
			: result.MetadataReferenceFailureCount > 0
				? "C# autocomplete semantic metadata reference warning"
				: result.ProjectFingerprintMismatchCount > 0
					? "C# autocomplete semantic project fingerprint warning"
					: "C# autocomplete semantic base build completed";
		_debugLog(operation, result.CreateDebugSummary());
	}

	private void DrainActiveDocumentIndexBuildResult()
	{
		if (
			!_activeDocumentIndexCoordinator.TryTakeLatestReportableBuildResult(
				out CSharpActiveDocumentIndexBuildResult result
			)
		)
		{
			return;
		}

		if (result.IsFailed)
		{
			_memberCompletionFollowUp.ClearIfMatches(
				result.Revision,
				result.ScriptPath
			);
		}

		_activeDocumentIndexLifecycle.HandleBuildFailure(result);
		_debugLog(
			"C# autocomplete active document index build failed",
			result.CreateDebugSummary()
		);
	}

}
#endif
