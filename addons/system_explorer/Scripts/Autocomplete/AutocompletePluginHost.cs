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
	private readonly AutocompleteProjectTypeConfirmationService _projectTypeConfirmationService;
	private readonly string _managedAssemblyGeneration;
	private readonly bool _semanticMemberPipelineEnabled;
	private readonly bool _cancelNativeCompletionOnRebind;
	private readonly bool _activeDocumentSyntaxOverlayEnabled;
	private readonly bool _cancelNativeCompletionOnTextChangedValidation;
	private readonly bool _automaticUsingInsertTextExecutionEnabled;
	private readonly bool _automaticUsingDeferInsertTextAfterGuiInputEnabled;
	private readonly bool _automaticUsingComplexOperationWrapperEnabled;
	private readonly Action<string, string> _debugLog;
	private readonly Func<bool> _debugEnabled;
	private readonly Func<long> _hostInstanceTokenProvider;
	private readonly ScriptEditorLifecycleCoordinator _scriptEditorLifecycleCoordinator;
	private readonly Action<string> _requestScriptEditorLifecycleRebind;
	private bool _isIssuingForcedMemberCompletionRequest;

	internal AutocompletePluginHost(
		string managedAssemblyGeneration,
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
		Action<string, string> debugLog,
		Action<string, string> persistentWorkerDiagnosticLog,
		Func<bool> debugEnabled,
		Func<long> hostInstanceTokenProvider,
		ScriptEditorLifecycleCoordinator scriptEditorLifecycleCoordinator,
		Action<string> requestScriptEditorLifecycleRebind,
		bool semanticMemberPipelineEnabled,
		bool cancelNativeCompletionOnRebind,
		bool activeDocumentSyntaxOverlayEnabled,
		bool cancelNativeCompletionOnTextChangedValidation,
		bool automaticUsingInsertTextExecutionEnabled,
		bool automaticUsingDeferInsertTextAfterGuiInputEnabled,
		bool automaticUsingComplexOperationWrapperEnabled
	)
	{
		_managedAssemblyGeneration = !string.IsNullOrWhiteSpace(managedAssemblyGeneration)
			? managedAssemblyGeneration
			: throw new ArgumentException(
				"Managed assembly generation is required.",
				nameof(managedAssemblyGeneration)
			);
		_semanticMemberPipelineEnabled = semanticMemberPipelineEnabled;
		_cancelNativeCompletionOnRebind = cancelNativeCompletionOnRebind;
		_activeDocumentSyntaxOverlayEnabled = activeDocumentSyntaxOverlayEnabled;
		_cancelNativeCompletionOnTextChangedValidation =
			cancelNativeCompletionOnTextChangedValidation;
		_automaticUsingInsertTextExecutionEnabled =
			automaticUsingInsertTextExecutionEnabled;
		_automaticUsingDeferInsertTextAfterGuiInputEnabled =
			automaticUsingDeferInsertTextAfterGuiInputEnabled;
		_automaticUsingComplexOperationWrapperEnabled =
			automaticUsingComplexOperationWrapperEnabled;
		_debugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
		_debugEnabled = debugEnabled ?? throw new ArgumentNullException(nameof(debugEnabled));
		_hostInstanceTokenProvider =
			hostInstanceTokenProvider
			?? throw new ArgumentNullException(nameof(hostInstanceTokenProvider));
		_scriptEditorLifecycleCoordinator =
			scriptEditorLifecycleCoordinator
			?? throw new ArgumentNullException(nameof(scriptEditorLifecycleCoordinator));
		_requestScriptEditorLifecycleRebind =
			requestScriptEditorLifecycleRebind
			?? throw new ArgumentNullException(nameof(requestScriptEditorLifecycleRebind));
		Trace("C# autocomplete host constructor begin");
		_prefixExtractor = new AutocompletePrefixExtractor();
		_metadataCodec = new AutocompleteCompletionOptionMetadataCodec();
		_presenter = new AutocompleteCodeEditPresenter(_metadataCodec);
		var completionContextBuilder = new CSharpDocumentCompletionContextBuilder();
		var completionContextResolver = new CSharpCompletionContextResolver();
		_projectTypeConfirmationService = new AutocompleteProjectTypeConfirmationService(
			new CSharpUsingInsertionPlanner(
				completionContextBuilder,
				completionContextResolver
			),
			_debugLog,
			_automaticUsingInsertTextExecutionEnabled,
			_automaticUsingDeferInsertTextAfterGuiInputEnabled,
			_automaticUsingComplexOperationWrapperEnabled
		);
		_confirmationBridge = new AutocompleteCompletionConfirmationBridge(
			_metadataCodec,
			_projectTypeConfirmationService,
			_debugLog
		);
		_matchPolicy = new AutocompleteCompletionMatchPolicy();
		_memberCompletionFollowUp = new AutocompleteMemberCompletionFollowUp();

		_indexLifetime = new AutocompleteIndexLifetime(persistentWorkerDiagnosticLog);
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

		IAutocompleteCompletionSource[] completionSources = _semanticMemberPipelineEnabled
			? new IAutocompleteCompletionSource[]
			{
				_projectTypeCompletionSource,
				_projectMemberCompletionSource,
			}
			: new IAutocompleteCompletionSource[]
			{
				_projectTypeCompletionSource,
			};

		_completionCoordinator = new AutocompleteCompletionCoordinator(
			_prefixExtractor,
			_presenter,
			_matchPolicy,
			completionSources,
			_debugLog,
			_cancelNativeCompletionOnTextChangedValidation
		);

		if (!_semanticMemberPipelineEnabled)
		{
			Trace(
				"C# autocomplete semantic member pipeline isolated",
				$"Enabled='False', Mode='DiagnosticIsolation', ProjectTypeCompletionRetained='True', ActiveDocumentSyntaxOverlayRetained='{_activeDocumentSyntaxOverlayEnabled}'"
			);
		}

		if (!_activeDocumentSyntaxOverlayEnabled)
		{
			Trace(
				"C# autocomplete active document syntax overlay isolated",
				"Enabled='False', Mode='DiagnosticIsolation', ProjectIndexRetained='True', ProjectTypeCompletionRetained='True', ActiveCodeEditValidationRetained='True', ActiveDocumentTextCaptureRetained='False', ActiveDocumentWorkerRetained='False'"
			);
		}

		if (!_cancelNativeCompletionOnTextChangedValidation)
		{
			Trace(
				"C# autocomplete TextChanged validation native completion cancellation isolated",
				"Enabled='False', Mode='DiagnosticIsolation', Scope='AutocompleteCompletionCoordinator.ValidateAfterTextChanged', ManagedSessionInvalidationRetained='True', DormantSessionStateRetained='True', RequestTimeCoordinatorCancellationRetained='True', ShutdownCancellationRetained='True'"
			);
		}

		Trace(
			"C# autocomplete automatic using execution diagnostic mode",
			$"InsertTextExecutionEnabled='{_automaticUsingInsertTextExecutionEnabled}', "
				+ $"DeferInsertTextAfterGuiInputEnabled='{_automaticUsingDeferInsertTextAfterGuiInputEnabled}', "
				+ $"ComplexOperationWrapperEnabled='{_automaticUsingComplexOperationWrapperEnabled}', "
				+ "UsingPlannerRetained='True', NativeConfirmationRetained='True', ConfirmationBridgeRetained='True', "
				+ $"BeginComplexOperationRetained='{_automaticUsingInsertTextExecutionEnabled && !_automaticUsingDeferInsertTextAfterGuiInputEnabled && _automaticUsingComplexOperationWrapperEnabled}', "
				+ $"UsingInsertTextRetained='{_automaticUsingInsertTextExecutionEnabled}', "
				+ $"UsingInsertTextDeferredAfterGuiInput='{_automaticUsingInsertTextExecutionEnabled && _automaticUsingDeferInsertTextAfterGuiInputEnabled}', "
				+ $"EndComplexOperationRetained='{_automaticUsingInsertTextExecutionEnabled && !_automaticUsingDeferInsertTextAfterGuiInputEnabled && _automaticUsingComplexOperationWrapperEnabled}'"
		);

		var themeDefinition = new AutocompleteThemeDefinition
		{
			CompletionExistingColor = Colors.Transparent,
		};
		_themeController = new AutocompleteCodeEditThemeController(themeDefinition);
		var completionPrefixController = new AutocompleteCodeCompletionPrefixController();

		_editorBinding = new AutocompleteEditorBinding(
			_managedAssemblyGeneration,
			_cancelNativeCompletionOnRebind,
			_scriptEditorLifecycleCoordinator,
			scriptEditorProvider,
			connectPluginSignal,
			disconnectPluginSignal,
			scriptChangedMethodName,
			textChangedMethodName,
			completionRequestedMethodName,
			guiInputMethodName,
			InvalidatePendingValidations,
			completionPrefixController,
			_themeController,
			_debugLog,
			_debugEnabled
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
		Trace("C# autocomplete host constructor completed");
	}

	internal bool EnsureLifecycleCurrent()
	{
		Trace("C# autocomplete host EnsureLifecycleCurrent begin");
		bool editorBindingCurrent = _editorBinding.EnsureLifecycleCurrent(
			out bool bindingResolutionRequired
		);
		if (
			editorBindingCurrent
			&& bindingResolutionRequired
			&& _scriptEditorLifecycleCoordinator.Snapshot.State
				== ScriptEditorLifecycleState.Stable
		)
		{
			_requestScriptEditorLifecycleRebind("ScriptEditorIdentityChanged");
		}

		EnsureProjectIndexLifecycleCurrentBestEffort();
		DrainIndexBuildResults();
		EnsureSemanticProjectStateBestEffort();

		if (
			editorBindingCurrent
			&& _scriptEditorLifecycleCoordinator.TryGetCurrentBindingLease(out _)
		)
		{
			CaptureActiveDocumentIfNeededBestEffort("Ensure lifecycle current");
		}

		DrainIndexBuildResults();
		Trace(
			"C# autocomplete host EnsureLifecycleCurrent completed",
			$"EditorBindingCurrent='{editorBindingCurrent}', LifecycleState='{_scriptEditorLifecycleCoordinator.Snapshot.State}', ScriptTransitionId='{_scriptEditorLifecycleCoordinator.Snapshot.ScriptTransitionId}', BindingEpoch='{_scriptEditorLifecycleCoordinator.Snapshot.BindingEpoch}'"
		);
		return editorBindingCurrent;
	}

	internal void HandleProjectFilesystemChanged(
		Action<string, string> diagnosticPhase = null
	)
	{
		InvokeProjectFilesystemDiagnosticPhase(
			diagnosticPhase,
			"ProjectRefreshRequested.Begin"
		);
		try
		{
			_projectIndexLifecycle.HandleFilesystemChanged();
			InvokeProjectFilesystemDiagnosticPhase(
				diagnosticPhase,
				"ProjectRefreshRequested.Returned"
			);
		}
		catch (Exception exception)
		{
			InvokeProjectFilesystemDiagnosticPhase(
				diagnosticPhase,
				"ProjectRefreshRequested.Returned",
				$"Result='Exception', ExceptionType='{exception.GetType().Name}'"
			);
			_debugLog(
				"C# autocomplete project index filesystem handling failed",
				exception.ToString()
			);
		}

		DrainIndexBuildResults(diagnosticPhase);
		EnsureSemanticProjectStateBestEffort(diagnosticPhase);
		InvokeProjectFilesystemDiagnosticPhase(
			diagnosticPhase,
			"HandleProjectFilesystemChanged.Completed"
		);
	}

	internal bool HandleScriptChanged(
		long scriptTransitionId,
		Action<string, ScriptEditor, CodeEdit> diagnosticPhase = null,
		Action<string, string> nativeBoundaryDiagnosticPhase = null
	)
	{
		InvokeScriptChangeDiagnosticPhase(
			diagnosticPhase,
			"HandleScriptChanged.Begin"
		);
		DrainIndexBuildResults();
		_memberCompletionFollowUp.Clear();
		_completionCoordinator.InvalidatePendingValidations();
		_semanticMemberCoordinator.ResetActiveDocument();
		_activeDocumentIndexLifecycle.ResetForScriptChange();

		InvokeScriptChangeDiagnosticPhase(
			diagnosticPhase,
			"ResolveCodeEditBinding"
		);
		bool bindingResolved = _editorBinding.ResolveCodeEditBinding(
			scriptTransitionId,
			_hostInstanceTokenProvider(),
			diagnosticPhase,
			nativeBoundaryDiagnosticPhase
		);
		if (bindingResolved)
		{
			CaptureActiveDocumentIfNeededBestEffort(
				"Active script changed",
				diagnosticPhase
			);
		}

		DrainIndexBuildResults();
		InvokeScriptChangeDiagnosticPhase(
			diagnosticPhase,
			"HandleScriptChanged.Completed"
		);
		return bindingResolved;
	}

	internal void HandleCompletionRequested()
	{
		DrainIndexBuildResults();

		if (!_editorBinding.TryGetActiveCodeEdit(out CodeEdit codeEdit, out string scriptPath))
		{
			_requestScriptEditorLifecycleRebind("HandleCompletionRequested");
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

	internal AutocompleteDeferredUsingInsertionRequest HandleCodeEditGuiInput(
		InputEvent inputEvent
	)
	{
		if (inputEvent == null)
			return null;

		if (
			!_editorBinding.TryGetActiveCodeEdit(
				out CodeEdit codeEdit,
				out string scriptPath
			)
		)
		{
			_requestScriptEditorLifecycleRebind("HandleCodeEditGuiInput");
			return null;
		}

		_confirmationBridge.TryHandleGuiInput(
			codeEdit,
			inputEvent,
			out AutocompleteDeferredUsingInsertionCandidate candidate
		);

		if (candidate == null || candidate.Plan == null)
			return null;

		try
		{
			return new AutocompleteDeferredUsingInsertionRequest(
				candidate.CompletionName ?? "",
				candidate.NamespaceName ?? "",
				ScriptPathUtility.Normalize(scriptPath),
				codeEdit.GetInstanceId(),
				candidate.Plan
			);
		}
		catch (Exception exception)
		{
			_debugLog(
				"C# autocomplete deferred using request capture failed after confirmation",
				$"Name='{candidate.CompletionName ?? ""}', Namespace='{candidate.NamespaceName ?? ""}', ExceptionType='{exception.GetType().FullName}', Exception='{exception}'"
			);
			return null;
		}
	}

	internal AutocompleteDeferredUsingInsertionApplyResult TryApplyDeferredUsingInsertion(
		AutocompleteDeferredUsingInsertionRequest request,
		long hostInstanceToken,
		string managedAssemblyGeneration,
		int guiInputCallbackDepth
	)
	{
		if (request == null)
		{
			return AutocompleteDeferredUsingInsertionApplyResult.Rejected(
				"PluginUnavailable"
			);
		}

		if (
			!_editorBinding.TryGetActiveCodeEdit(
				out CodeEdit codeEdit,
				out string scriptPath
			)
		)
		{
			_requestScriptEditorLifecycleRebind("TryApplyDeferredUsingInsertion");
			return AutocompleteDeferredUsingInsertionApplyResult.Rejected(
				"CodeEditChanged"
			);
		}

		ulong currentCodeEditNativeInstanceId;
		try
		{
			currentCodeEditNativeInstanceId = codeEdit.GetInstanceId();
		}
		catch
		{
			return AutocompleteDeferredUsingInsertionApplyResult.Rejected(
				"CodeEditChanged"
			);
		}

		string normalizedScriptPath = ScriptPathUtility.Normalize(scriptPath);
		if (currentCodeEditNativeInstanceId != request.CodeEditNativeInstanceId)
		{
			return AutocompleteDeferredUsingInsertionApplyResult.Rejected(
				"CodeEditChanged",
				currentCodeEditNativeInstanceId,
				normalizedScriptPath
			);
		}

		if (
			!string.Equals(
				normalizedScriptPath,
				request.ScriptPath,
				StringComparison.OrdinalIgnoreCase
			)
		)
		{
			return AutocompleteDeferredUsingInsertionApplyResult.Rejected(
				"ScriptChanged",
				currentCodeEditNativeInstanceId,
				normalizedScriptPath
			);
		}

		return _projectTypeConfirmationService.ApplyDeferredUsingInsertion(
			codeEdit,
			request,
			new AutocompleteDeferredUsingInsertionExecutionContext(
				currentCodeEditNativeInstanceId,
				normalizedScriptPath,
				hostInstanceToken,
				managedAssemblyGeneration ?? "",
				guiInputCallbackDepth
			)
		);
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
			_requestScriptEditorLifecycleRebind("ValidateAfterTextChanged");
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
		Trace("C# autocomplete host ResetTransientState begin");
		_memberCompletionFollowUp.Clear();
		_completionCoordinator.Reset();
		_semanticMemberCoordinator.ResetTransientState();
		_activeDocumentIndexLifecycle.ResetTransientState();
		_projectIndexLifecycle.ResetTransientState();
		_cacheCoordinator.ResetTransientState();
		Trace("C# autocomplete host ResetTransientState completed");
	}

	internal void Shutdown()
	{
		Trace("C# autocomplete host Shutdown begin");
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
		Trace("C# autocomplete host Shutdown completed");
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
			_requestScriptEditorLifecycleRebind("ProcessPendingCompletionWork");
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


	private void LogAcceptedActiveDocumentCapture(
		CodeEdit codeEdit,
		CSharpActiveDocumentIndexRequest capturedRequest
	)
	{
		try
		{
			if (!_debugEnabled() || capturedRequest == null)
				return;
			if (
				string.Equals(
					capturedRequest.Reason,
					"Active script changed",
					StringComparison.Ordinal
				)
				|| string.Equals(
					capturedRequest.Reason,
					"Deferred TextChanged capture",
					StringComparison.Ordinal
				)
				|| string.Equals(
					capturedRequest.Reason,
					"Ensure lifecycle current",
					StringComparison.Ordinal
				)
			)
			{
				return;
			}

			string codeEditInstanceId = "<null-or-invalid>";
			if (IsValidGodotObject(codeEdit))
				codeEditInstanceId = codeEdit.GetInstanceId().ToString();

			_debugLog(
				"C# autocomplete active document captured",
				$"Revision={capturedRequest.Revision}, Reason='{capturedRequest.Reason}', "
					+ $"ScriptPath='{capturedRequest.ScriptPath}', CodeEditInstanceId='{codeEditInstanceId}', "
					+ $"SourceLength={capturedRequest.SourceText?.Length ?? 0}, "
					+ $"SourceObjectToken={CSharpRoslynRuntimeDiagnostics.GetObjectToken(capturedRequest.SourceText)}, "
					+ $"HostInstanceToken={_hostInstanceTokenProvider()}"
			);
		}
		catch
		{
		}
	}

	private void Trace(string operation, string details = "")
	{
		try
		{
			if (!_debugEnabled())
				return;

			_debugLog(operation ?? "", details ?? "");
		}
		catch
		{
		}
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

	private void EnsureSemanticProjectStateBestEffort(
		Action<string, string> diagnosticPhase = null
	)
	{
		if (!_semanticMemberPipelineEnabled)
			return;

		try
		{
			CSharpProjectIndexSnapshot snapshot = _projectIndex.CurrentSnapshot;
			if (snapshot != null && snapshot.HasBuiltAtLeastOnce)
			{
				InvokeProjectFilesystemDiagnosticPhase(
					diagnosticPhase,
					"SemanticProjectStateEnsure.Begin",
					$"ProjectGeneration='{snapshot.Generation}'"
				);
				bool accepted = _semanticMemberCoordinator.RequestProjectSnapshot(snapshot);
				InvokeProjectFilesystemDiagnosticPhase(
					diagnosticPhase,
					"SemanticProjectStateEnsure.Returned",
					$"ProjectGeneration='{snapshot.Generation}', SemanticProjectSnapshotAccepted='{accepted}'"
				);
			}
		}
		catch (Exception exception)
		{
			InvokeProjectFilesystemDiagnosticPhase(
				diagnosticPhase,
				"SemanticProjectStateEnsure.Returned",
				$"Result='Exception', ExceptionType='{exception.GetType().Name}'"
			);
			_debugLog(
				"C# autocomplete semantic project routing failed",
				exception.ToString()
			);
		}
	}

	private void CaptureActiveDocumentIfNeededBestEffort(
		string reason,
		Action<string, ScriptEditor, CodeEdit> diagnosticPhase = null
	)
	{
		if (
			_editorBinding.TryGetActiveCodeEdit(
				out CodeEdit codeEdit,
				out string scriptPath,
				diagnosticPhase
			)
		)
		{
			CaptureActiveDocumentIfNeededBestEffort(
				codeEdit,
				scriptPath,
				reason,
				diagnosticPhase
			);
		}
	}

	private void CaptureActiveDocumentIfNeededBestEffort(
		CodeEdit codeEdit,
		string scriptPath,
		string reason,
		Action<string, ScriptEditor, CodeEdit> diagnosticPhase = null
	)
	{
		if (!_activeDocumentSyntaxOverlayEnabled)
			return;

		try
		{
			if (_activeDocumentIndexLifecycle.NeedsCapture(scriptPath))
			{
				CSharpActiveDocumentIndexRequest capturedRequest =
					_activeDocumentIndexLifecycle.CapturePendingText(
						codeEdit,
						scriptPath,
						reason,
						diagnosticPhase
					);

				if (capturedRequest != null)
				{
					LogAcceptedActiveDocumentCapture(codeEdit, capturedRequest);

					if (_semanticMemberPipelineEnabled)
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
		}
		catch (Exception exception)
		{
			_debugLog(
				"C# autocomplete active document lifecycle failed",
				exception.ToString()
			);
		}
	}

	private static void InvokeScriptChangeDiagnosticPhase(
		Action<string, ScriptEditor, CodeEdit> diagnosticPhase,
		string phase,
		ScriptEditor scriptEditor = null,
		CodeEdit codeEdit = null
	)
	{
		try
		{
			diagnosticPhase?.Invoke(phase ?? "", scriptEditor, codeEdit);
		}
		catch
		{
			// Diagnostic observation must not affect autocomplete control flow.
		}
	}

	private void DrainIndexBuildResults(Action<string, string> diagnosticPhase = null)
	{
		DrainProjectIndexBuildResult(diagnosticPhase);
		DrainActiveDocumentIndexBuildResult();
		DrainSemanticMemberBuildResult();
		DrainCacheWriteResult(diagnosticPhase);
	}

	private void DrainProjectIndexBuildResult(Action<string, string> diagnosticPhase = null)
	{
		InvokeProjectFilesystemDiagnosticPhase(
			diagnosticPhase,
			"DrainProjectIndexResult.Begin"
		);
		if (!_indexCoordinator.TryTakeLatestBuildResult(out CSharpProjectIndexBuildResult result))
		{
			InvokeProjectFilesystemDiagnosticPhase(
				diagnosticPhase,
				"DrainProjectIndexResult.Returned",
				"HasResult='False'"
			);
			return;
		}

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

		if (
			_semanticMemberPipelineEnabled
			&& result.IsSuccessful
			&& result.Snapshot != null
		)
		{
			InvokeProjectFilesystemDiagnosticPhase(
				diagnosticPhase,
				"SemanticProjectSnapshotRoute.Begin",
				$"ProjectGeneration='{result.Generation}'"
			);
			bool accepted = _semanticMemberCoordinator.RequestProjectSnapshot(result.Snapshot);
			InvokeProjectFilesystemDiagnosticPhase(
				diagnosticPhase,
				"SemanticProjectSnapshotRoute.Returned",
				$"ProjectGeneration='{result.Generation}', SemanticProjectSnapshotAccepted='{accepted}'"
			);
		}

		InvokeProjectFilesystemDiagnosticPhase(
			diagnosticPhase,
			"DrainProjectIndexResult.Returned",
			$"HasResult='True', ProjectGeneration='{result.Generation}', Status='{result.Status}'"
		);
	}

	private void DrainCacheWriteResult(Action<string, string> diagnosticPhase = null)
	{
		InvokeProjectFilesystemDiagnosticPhase(
			diagnosticPhase,
			"DrainCacheResult.Begin"
		);
		if (
			!_cacheCoordinator.TryTakeLatestReportableWriteResult(
				out CSharpProjectIndexCacheWriteResult result
			)
		)
		{
			InvokeProjectFilesystemDiagnosticPhase(
				diagnosticPhase,
				"DrainCacheResult.Returned",
				"HasResult='False'"
			);
			return;
		}

		string operation = result.Status switch
		{
			CSharpProjectIndexCacheWriteStatus.Succeeded =>
				"C# autocomplete project index cache write completed",
			_ => "C# autocomplete project index cache write failed",
		};

		_debugLog(operation, result.CreateDebugSummary());
		InvokeProjectFilesystemDiagnosticPhase(
			diagnosticPhase,
			"DrainCacheResult.Returned",
			$"HasResult='True', ProjectGeneration='{result.Generation}', Status='{result.Status}'"
		);
	}

	private static void InvokeProjectFilesystemDiagnosticPhase(
		Action<string, string> diagnosticPhase,
		string phase,
		string details = ""
	)
	{
		try
		{
			diagnosticPhase?.Invoke(phase ?? "", details ?? "");
		}
		catch
		{
			// Operation-local diagnostic observation must not affect autocomplete control flow.
		}
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
