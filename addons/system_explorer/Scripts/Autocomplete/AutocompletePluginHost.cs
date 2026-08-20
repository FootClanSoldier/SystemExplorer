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
	private const int MaxTrackedCompletionProvenanceCodeEdits = 256;

	private enum CompletionRequestProvenance
	{
		SystemExplorerDormantRecovery,
		SystemExplorerForcedMemberFollowUp,
		SystemExplorerConfirmationNativeFollowUp,
		UnattributedNativeOrExternal,
	}

	private readonly AutocompleteIndexLifetime _indexLifetime;
	private readonly CSharpProjectIndex _projectIndex;
	private readonly CSharpProjectIndexPersistentCacheStore _persistentCacheStore;
	private readonly CSharpProjectIndexCacheCoordinator _cacheCoordinator;
	private readonly CSharpProjectIndexWorker _indexWorker;
	private readonly CSharpProjectIndexCoordinator _indexCoordinator;
	private readonly AutocompleteIndexingQuiescenceCoordinator _indexingQuiescenceCoordinator;
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
	private readonly AutocompleteCodeEditMutationCoordinator _codeEditMutationCoordinator;
	private readonly AutocompleteCompletionMatchPolicy _matchPolicy;
	private readonly AutocompleteMemberCompletionFollowUp _memberCompletionFollowUp;
	private readonly ProjectTypeCompletionSource _projectTypeCompletionSource;
	private readonly ProjectMemberCompletionSource _projectMemberCompletionSource;
	private readonly AutocompleteCompletionOptionMetadataCodec _metadataCodec;
	private readonly AutocompleteCompletionPublicationEnvelopeCodec _publicationEnvelopeCodec;
	private readonly AutocompleteCompletionConfirmationBridge _confirmationBridge;
	private readonly AutocompleteProjectTypeConfirmationService _projectTypeConfirmationService;
	private readonly string _managedAssemblyGeneration;
	private readonly bool _semanticMemberPipelineEnabled;
	private readonly bool _cancelNativeCompletionOnRebind;
	private readonly bool _activeDocumentSyntaxOverlayEnabled;
	private readonly bool _automaticUsingInsertTextExecutionEnabled;
	private readonly bool _automaticUsingDeferInsertTextAfterGuiInputEnabled;
	private readonly bool _automaticUsingComplexOperationWrapperEnabled;
	private readonly Action<string, string> _debugLog;
	private readonly Func<bool> _debugEnabled;
	private readonly Func<long> _hostInstanceTokenProvider;
	private readonly Func<long> _currentReloadReadyEpochProvider;
	private readonly Func<bool> _reloadStabilizationReadyProvider;
	private readonly ScriptEditorLifecycleCoordinator _scriptEditorLifecycleCoordinator;
	private readonly Action<string> _requestScriptEditorLifecycleRebind;
	private readonly HashSet<ulong> _trackedUnattributedCompletionProvenanceCodeEditIds = new();
	private readonly HashSet<ulong> _loggedUnattributedWithoutTextChangedCodeEditIds = new();
	private readonly Dictionary<ulong, long> _lastLoggedUnattributedTextChangedSequenceByCodeEditId = new();
	private bool _isIssuingForcedMemberCompletionRequest;
	private bool _isHandlingSystemExplorerCompletionConfirmation;
	private long _completionRequestObservationSequence;
	private long _textChangedObservationSequence;
	private long _lastTextChangedBindingEpoch;
	private ulong _lastTextChangedCodeEditInstanceId;
	private bool _completionPipelineFaulted;
	private bool _completionPipelineSuppressionLogged;

	internal bool IsCompletionPipelineFaulted => _completionPipelineFaulted;

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
		Func<long> currentReloadReadyEpochProvider,
		Func<bool> reloadStabilizationReadyProvider,
		ScriptEditorLifecycleCoordinator scriptEditorLifecycleCoordinator,
		Action<string> requestScriptEditorLifecycleRebind,
		bool semanticMemberPipelineEnabled,
		bool cancelNativeCompletionOnRebind,
		bool activeDocumentSyntaxOverlayEnabled,
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
		_currentReloadReadyEpochProvider =
			currentReloadReadyEpochProvider
			?? throw new ArgumentNullException(nameof(currentReloadReadyEpochProvider));
		_reloadStabilizationReadyProvider =
			reloadStabilizationReadyProvider
			?? throw new ArgumentNullException(nameof(reloadStabilizationReadyProvider));
		_scriptEditorLifecycleCoordinator =
			scriptEditorLifecycleCoordinator
			?? throw new ArgumentNullException(nameof(scriptEditorLifecycleCoordinator));
		_requestScriptEditorLifecycleRebind =
			requestScriptEditorLifecycleRebind
			?? throw new ArgumentNullException(nameof(requestScriptEditorLifecycleRebind));
		Trace("C# autocomplete host constructor begin");
		_prefixExtractor = new AutocompletePrefixExtractor();
		_metadataCodec = new AutocompleteCompletionOptionMetadataCodec();
		_publicationEnvelopeCodec = new AutocompleteCompletionPublicationEnvelopeCodec(
			_metadataCodec
		);
		_presenter = new AutocompleteCodeEditPresenter(
			_publicationEnvelopeCodec,
			_debugLog
		);
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
		_codeEditMutationCoordinator = new AutocompleteCodeEditMutationCoordinator(
			_managedAssemblyGeneration,
			_hostInstanceTokenProvider,
			_currentReloadReadyEpochProvider,
			_reloadStabilizationReadyProvider,
			_scriptEditorLifecycleCoordinator,
			_prefixExtractor,
			_publicationEnvelopeCodec,
			_presenter,
			_projectTypeConfirmationService,
			IsCompletionBindingCurrent,
			_debugLog
		);
		_confirmationBridge = new AutocompleteCompletionConfirmationBridge(
			_publicationEnvelopeCodec,
			_codeEditMutationCoordinator,
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
		_indexingQuiescenceCoordinator = new AutocompleteIndexingQuiescenceCoordinator();

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

		AutocompleteCompletionSourceRegistration[] completionSources =
			_semanticMemberPipelineEnabled
				? new AutocompleteCompletionSourceRegistration[]
				{
					new("ProjectType", _projectTypeCompletionSource),
					new("ProjectMember", _projectMemberCompletionSource),
				}
				: new AutocompleteCompletionSourceRegistration[]
				{
					new("ProjectType", _projectTypeCompletionSource),
				};

		_completionCoordinator = new AutocompleteCompletionCoordinator(
			_prefixExtractor,
			_codeEditMutationCoordinator,
			_matchPolicy,
			completionSources,
			_debugLog
		);

		Trace(
			"C# autocomplete feature profile",
			$"SemanticMemberPipelineEnabled='{_semanticMemberPipelineEnabled}', "
				+ $"ActiveDocumentSyntaxOverlayEnabled='{_activeDocumentSyntaxOverlayEnabled}', "
				+ "ProjectTypeCompletionEnabled='True', "
				+ $"ProjectMemberCompletionEnabled='{_semanticMemberPipelineEnabled}', "
				+ $"AutomaticUsingInsertTextExecutionEnabled='{_automaticUsingInsertTextExecutionEnabled}', "
				+ $"AutomaticUsingDeferredInsertEnabled='{_automaticUsingDeferInsertTextAfterGuiInputEnabled}', "
				+ $"AutomaticUsingComplexOperationWrapperEnabled='{_automaticUsingComplexOperationWrapperEnabled}', "
				+ $"CancelNativeCompletionOnRebind='{_cancelNativeCompletionOnRebind}', "
				+ "IndexingQuiescenceEnabled='True', "
				+ $"IndexingQuiescenceQuietPeriodMs='{AutocompleteIndexingQuiescenceCoordinator.QuietPeriodMilliseconds}'"
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


		Trace(
			"C# autocomplete automatic using execution mode",
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
			_codeEditMutationCoordinator,
			_currentReloadReadyEpochProvider,
			_reloadStabilizationReadyProvider,
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
			QueueProjectRefreshAdmission,
			_debugLog
		);
		Trace("C# autocomplete host constructor completed");
	}

	internal bool EnsureLifecycleCurrent()
	{
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

		if (editorBindingCurrent)
			EnsureActiveDocumentAdmissionScheduledForCurrentLease("Ensure lifecycle current");

		DrainIndexBuildResults();
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

	internal AutocompleteEditorBindingCandidateObservationKind TryObserveCodeEditBindingCandidate(
		long scriptTransitionId,
		long hostInstanceToken,
		out AutocompleteEditorBindingCandidate candidate
	)
	{
		return _editorBinding.TryObserveCodeEditBindingCandidate(
			scriptTransitionId,
			hostInstanceToken,
			out candidate
		);
	}

	private void ResetManagedStateForScriptChange()
	{
		DrainIndexBuildResults();
		_memberCompletionFollowUp.Clear();
		_completionCoordinator.InvalidatePendingValidations();
		_semanticMemberCoordinator.ResetActiveDocument();
		_activeDocumentIndexLifecycle.ResetForScriptChange();
	}

	internal bool HandleScriptChanged(
		long scriptTransitionId,
		long reloadReadyEpoch,
		AutocompleteEditorBindingCandidate? requiredActivationCandidate,
		Action<string, ScriptEditor, CodeEdit> diagnosticPhase = null,
		Action<string, string> nativeBoundaryDiagnosticPhase = null
	)
	{
		InvokeScriptChangeDiagnosticPhase(
			diagnosticPhase,
			"HandleScriptChanged.Begin"
		);
		ResetManagedStateForScriptChange();

		InvokeScriptChangeDiagnosticPhase(
			diagnosticPhase,
			"ResolveCodeEditBinding"
		);
		bool bindingResolved = _editorBinding.ResolveCodeEditBinding(
			scriptTransitionId,
			_hostInstanceTokenProvider(),
			reloadReadyEpoch,
			requiredActivationCandidate,
			diagnosticPhase,
			nativeBoundaryDiagnosticPhase
		);
		if (bindingResolved)
			EnsureActiveDocumentAdmissionScheduledForCurrentLease("Active script changed");

		DrainIndexBuildResults();
		InvokeScriptChangeDiagnosticPhase(
			diagnosticPhase,
			"HandleScriptChanged.Completed"
		);
		return bindingResolved;
	}

	internal void HandleCompletionRequested()
	{
		if (_completionPipelineFaulted)
		{
			LogCompletionPipelineSuppressionOnce();
			return;
		}

		string stage = "AdvanceObservation";
		long requestObservationSequence = 0;
		EditorBindingLease? capturedBindingLease = null;
		string capturedScriptPath = "";

		try
		{
			requestObservationSequence = AdvancePositiveSequence(
				ref _completionRequestObservationSequence
			);

			stage = "ResolveBinding";
			DrainIndexBuildResults();

			if (
				!_editorBinding.TryGetActiveCodeEdit(
					out CodeEdit codeEdit,
					out string scriptPath,
					out EditorBindingLease requestBindingLease
				)
			)
			{
				_requestScriptEditorLifecycleRebind("HandleCompletionRequested");
				return;
			}

			capturedBindingLease = requestBindingLease;
			capturedScriptPath = scriptPath ?? "";

			stage = "CaptureRequestDispatchChild";
			AutocompleteRequestDispatchChildCaptureResult childCaptureResult =
				_codeEditMutationCoordinator.TryCaptureRequestDispatchChild(
					codeEdit,
					scriptPath,
					requestBindingLease,
					requestObservationSequence,
					out AutocompleteRequestDispatchChildLease capturedChildLease
				);
			if (
				childCaptureResult
				== AutocompleteRequestDispatchChildCaptureResult.RejectedChild
			)
			{
				return;
			}

			AutocompleteRequestDispatchChildLease? requestDispatchChildLease =
				childCaptureResult
				== AutocompleteRequestDispatchChildCaptureResult.AuthorizedChild
					? capturedChildLease
					: null;
			CompletionRequestProvenance requestProvenance =
				CaptureCompletionRequestProvenance(requestDispatchChildLease);

			_codeEditMutationCoordinator.RetireOwnedPublication(
				"CodeCompletionRequested"
			);

			LogCompletionRequestedProvenanceIfNeeded(
				requestProvenance,
				requestObservationSequence,
				requestBindingLease,
				scriptPath,
				requestDispatchChildLease
			);

			stage = "PrepareProjectState";
			EnsureProjectIndexLifecycleCurrentBestEffort();
			DrainIndexBuildResults();
			EnsureSemanticProjectStateBestEffort();
			CaptureActiveDocumentIfNeededBestEffort(
				codeEdit,
				scriptPath,
				requestBindingLease,
				"Code completion requested",
				consumeMatchingQuiescentIntent: true
			);
			DrainIndexBuildResults();

			stage = "CompletionCoordinator";
			bool published = _completionCoordinator.HandleCompletionRequested(
				codeEdit,
				scriptPath,
				requestBindingLease,
				requestObservationSequence,
				requestDispatchChildLease
			);

			if (published)
				_memberCompletionFollowUp.Clear();

			stage = "PostCompletionDrain";
			DrainIndexBuildResults();
		}
		catch (Exception exception)
		{
			FaultCompletionPipeline(
				stage,
				requestObservationSequence,
				capturedBindingLease,
				capturedScriptPath,
				exception
			);
		}
	}

	internal AutocompleteDeferredUsingInsertionRequest HandleCodeEditGuiInput(
		InputEvent inputEvent
	)
	{
		if (inputEvent == null)
			return null;
		if (_completionPipelineFaulted)
			return null;

		if (
			!_editorBinding.TryGetActiveCodeEdit(
				out CodeEdit codeEdit,
				out string scriptPath,
				out EditorBindingLease bindingLease
			)
		)
		{
			_requestScriptEditorLifecycleRebind("HandleCodeEditGuiInput");
			return null;
		}

		AutocompleteDeferredUsingInsertionCandidate candidate;
		bool previousConfirmationScope = _isHandlingSystemExplorerCompletionConfirmation;
		_isHandlingSystemExplorerCompletionConfirmation = true;

		try
		{
			_confirmationBridge.TryHandleGuiInput(
				codeEdit,
				scriptPath,
				bindingLease,
				inputEvent,
				out candidate
			);
		}
		finally
		{
			_isHandlingSystemExplorerCompletionConfirmation = previousConfirmationScope;
		}

		if (candidate == null || candidate.Plan == null)
			return null;

		try
		{
			return new AutocompleteDeferredUsingInsertionRequest(
				candidate.CompletionName ?? "",
				candidate.NamespaceName ?? "",
				candidate.OriginatingCompletionPublicationId,
				candidate.BindingLease,
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
		if (_completionPipelineFaulted)
		{
			return AutocompleteDeferredUsingInsertionApplyResult.Rejected(
				"CompletionPipelineFaulted"
			);
		}

		if (
			!_editorBinding.TryGetActiveCodeEdit(
				out CodeEdit codeEdit,
				out string scriptPath,
				out EditorBindingLease currentBindingLease
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
		string normalizedRequestScriptPath = ScriptPathUtility.Normalize(
			request.BindingLease.ScriptResourcePath
		);
		if (!currentBindingLease.Equals(request.BindingLease))
		{
			return AutocompleteDeferredUsingInsertionApplyResult.Rejected(
				"BindingLeaseChanged",
				currentCodeEditNativeInstanceId,
				normalizedScriptPath,
				currentBindingLease
			);
		}
		if (currentCodeEditNativeInstanceId != request.CodeEditNativeInstanceId)
		{
			return AutocompleteDeferredUsingInsertionApplyResult.Rejected(
				"CodeEditChanged",
				currentCodeEditNativeInstanceId,
				normalizedScriptPath,
				currentBindingLease
			);
		}
		if (
			string.IsNullOrWhiteSpace(normalizedScriptPath)
			|| !string.Equals(
				normalizedScriptPath,
				normalizedRequestScriptPath,
				StringComparison.OrdinalIgnoreCase
			)
		)
		{
			return AutocompleteDeferredUsingInsertionApplyResult.Rejected(
				"BindingLeaseChanged",
				currentCodeEditNativeInstanceId,
				normalizedScriptPath,
				currentBindingLease
			);
		}

		return _codeEditMutationCoordinator.TryExecuteDeferredUsingInsertion(
			codeEdit,
			normalizedScriptPath,
			currentBindingLease,
			request,
			hostInstanceToken,
			managedAssemblyGeneration ?? "",
			guiInputCallbackDepth
		);
	}

	internal long BeginTextChangedValidation()
	{
		if (_completionPipelineFaulted)
			return _completionCoordinator.BeginTextChangedValidation();

		ObserveTextChangedForCurrentBinding();
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
		if (_completionPipelineFaulted)
			return;

		DrainIndexBuildResults();

		if (!_completionCoordinator.IsValidationCurrent(generation))
			return;

		if (
			!_editorBinding.TryGetActiveCodeEdit(
				out CodeEdit codeEdit,
				out string scriptPath,
				out EditorBindingLease bindingLease
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
			bindingLease,
			"Deferred TextChanged capture",
			consumeMatchingQuiescentIntent: true
		);

		if (!_completionCoordinator.IsValidationCurrent(generation))
			return;

		_completionCoordinator.ValidateAfterTextChanged(
			codeEdit,
			scriptPath,
			bindingLease,
			generation
		);
		DrainIndexBuildResults();
	}

	internal bool TryBeginExternalMutation(
		long hostInstanceToken,
		AutocompleteExternalMutationOrigin origin,
		string operationName,
		out AutocompleteExternalMutationLease lease
	)
	{
		if (
			!_codeEditMutationCoordinator.TryBeginExternalMutation(
				hostInstanceToken,
				origin,
				operationName,
				out lease
			)
		)
		{
			return false;
		}

		try
		{
			_memberCompletionFollowUp.Clear();
			_completionCoordinator.InvalidatePendingValidations("ExternalMutationLease");
			_indexingQuiescenceCoordinator.InvalidateSpeculativeActiveDocumentForExternalMutation();
			return true;
		}
		catch (Exception exception)
		{
			_codeEditMutationCoordinator.EndExternalMutation(lease);
			Trace(
				"C# autocomplete external mutation managed-state invalidation failed",
				$"MutationTransactionId='{lease.MutationTransactionId}', Origin='{lease.Origin}', OperationName='{lease.OperationName}', HostInstanceToken='{lease.HostInstanceToken}', ManagedAssemblyGeneration='{lease.ManagedAssemblyGeneration}', Exception='{exception}'"
			);
			lease = default;
			return false;
		}
	}

	internal bool IsExternalMutationAuthorityCurrent(
		AutocompleteExternalMutationLease lease
	)
	{
		return _codeEditMutationCoordinator.IsExternalMutationAuthorityCurrent(lease);
	}

	internal bool EndExternalMutation(AutocompleteExternalMutationLease lease)
	{
		return _codeEditMutationCoordinator.EndExternalMutation(lease);
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
		_indexingQuiescenceCoordinator.ClearPendingWork();
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
		_indexingQuiescenceCoordinator.ClearPendingWork();
		_projectIndexLifecycle.Shutdown();
		_indexCoordinator.Shutdown();
		_activeDocumentIndexLifecycle.Shutdown();
		_activeDocumentIndexCoordinator.Shutdown();
		_semanticMemberCoordinator.Shutdown();
		_cacheCoordinator.Shutdown();
		_indexLifetime.Shutdown();
		_completionCoordinator.InvalidatePendingValidations("Shutdown");
		_editorBinding.Shutdown();
		_themeController.Reset();
		Trace("C# autocomplete host Shutdown completed");
	}

	internal bool HasPendingIndexingQuiescenceWork =>
		_indexingQuiescenceCoordinator.HasPendingWork;

	internal void ClearPendingIndexingQuiescenceWork()
	{
		_indexingQuiescenceCoordinator.ClearPendingWork();
	}

	internal void ProcessPendingIndexingQuiescence(
		double delta,
		bool admissionAllowed
	)
	{
		if (
			!_indexingQuiescenceCoordinator.TryAdvance(
				delta,
				admissionAllowed,
				out AutocompleteIndexingQuiescenceBatch batch
			)
		)
		{
			return;
		}

		LogIndexingQuiescenceAdmission(batch);

		if (batch.ProjectRefreshRequested)
			_projectIndexLifecycle.ExecuteRefresh(batch.ProjectRefreshReason);

		if (batch.ActiveDocumentRequested)
			ProcessActiveDocumentQuiescenceAdmission(batch);

		DrainIndexBuildResults();
		EnsureSemanticProjectStateBestEffort();
	}

	internal bool HasPendingCompletionProcessWork()
	{
		return !_completionPipelineFaulted && _memberCompletionFollowUp.HasPendingWork;
	}

	internal void ClearPendingCompletionProcessWork()
	{
		_memberCompletionFollowUp.Clear();
	}

	internal void ProcessPendingCompletionWork()
	{
		if (_completionPipelineFaulted)
		{
			_memberCompletionFollowUp.Clear();
			return;
		}

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
				out string scriptPath,
				out EditorBindingLease bindingLease
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
			_codeEditMutationCoordinator.TryRequestCodeCompletion(
				codeEdit,
				scriptPath,
				bindingLease,
				force: true,
				origin: AutocompleteRequestDispatchOrigin.ForcedMemberFollowUp,
				retirementReason: "ForcedMemberFollowUpRequest"
			);
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

	private CompletionRequestProvenance CaptureCompletionRequestProvenance(
		AutocompleteRequestDispatchChildLease? requestDispatchChildLease
	)
	{
		if (requestDispatchChildLease.HasValue)
		{
			return requestDispatchChildLease.Value.Origin switch
			{
				AutocompleteRequestDispatchOrigin.DormantRecovery =>
					CompletionRequestProvenance.SystemExplorerDormantRecovery,
				AutocompleteRequestDispatchOrigin.ForcedMemberFollowUp =>
					CompletionRequestProvenance.SystemExplorerForcedMemberFollowUp,
				_ => CompletionRequestProvenance.UnattributedNativeOrExternal,
			};
		}

		if (_isHandlingSystemExplorerCompletionConfirmation)
			return CompletionRequestProvenance.SystemExplorerConfirmationNativeFollowUp;

		return CompletionRequestProvenance.UnattributedNativeOrExternal;
	}

	private void ObserveTextChangedForCurrentBinding()
	{
		ScriptEditorLifecycleSnapshot snapshot = _scriptEditorLifecycleCoordinator.Snapshot;
		if (
			snapshot.State != ScriptEditorLifecycleState.Stable
			|| snapshot.BindingEpoch <= 0
			|| snapshot.CodeEditInstanceId == 0
		)
		{
			return;
		}

		_lastTextChangedBindingEpoch = snapshot.BindingEpoch;
		_lastTextChangedCodeEditInstanceId = snapshot.CodeEditInstanceId;
		AdvancePositiveSequence(ref _textChangedObservationSequence);
	}

	private void LogCompletionRequestedProvenanceIfNeeded(
		CompletionRequestProvenance requestProvenance,
		long requestObservationSequence,
		EditorBindingLease requestBindingLease,
		string resolvedScriptPath,
		AutocompleteRequestDispatchChildLease? requestDispatchChildLease
	)
	{
		string normalizedResolvedScriptPath = ScriptPathUtility.Normalize(resolvedScriptPath);
		string normalizedBoundScriptPath = ScriptPathUtility.Normalize(
			requestBindingLease.ScriptResourcePath
		);

		if (
			requestBindingLease.BindingEpoch <= 0
			|| requestBindingLease.CodeEditInstanceId == 0
			|| !string.Equals(
				normalizedBoundScriptPath,
				normalizedResolvedScriptPath,
				StringComparison.OrdinalIgnoreCase
			)
		)
		{
			return;
		}

		ulong codeEditInstanceId = requestBindingLease.CodeEditInstanceId;
		bool reloadNeutralizedInCurrentGeneration =
			_editorBinding.WasReloadNeutralizedInCurrentGeneration(codeEditInstanceId);
		if (requestBindingLease.ReloadReadyEpoch <= 1 && !reloadNeutralizedInCurrentGeneration)
			return;

		bool textChangedObservedForCurrentBinding =
			_textChangedObservationSequence > 0
			&& _lastTextChangedBindingEpoch == requestBindingLease.BindingEpoch
			&& _lastTextChangedCodeEditInstanceId == codeEditInstanceId;

		if (
			requestProvenance == CompletionRequestProvenance.UnattributedNativeOrExternal
			&& !ShouldLogUnattributedCompletionRequest(
				codeEditInstanceId,
				textChangedObservedForCurrentBinding
			)
		)
		{
			return;
		}

		Trace(
			"C# autocomplete CodeCompletionRequested provenance",
			$"ManagedAssemblyGeneration='{_managedAssemblyGeneration}', "
				+ $"HostInstanceToken='{requestBindingLease.HostInstanceToken}', "
				+ $"RequestObservationSequence='{requestObservationSequence}', "
				+ $"ParentRequestDispatchMutationTransactionId='{requestDispatchChildLease?.ParentRequestDispatchMutationTransactionId ?? 0}', "
				+ $"RequestProvenance='{requestProvenance}', "
				+ $"ScriptTransitionId='{requestBindingLease.ScriptTransitionId}', "
				+ $"BindingEpoch='{requestBindingLease.BindingEpoch}', "
				+ $"ReloadReadyEpoch='{requestBindingLease.ReloadReadyEpoch}', "
				+ $"CodeEditInstanceId='{codeEditInstanceId}', "
				+ $"ScriptPath='{normalizedResolvedScriptPath}', "
				+ $"ReloadNeutralizedInCurrentGeneration='{reloadNeutralizedInCurrentGeneration}', "
				+ $"TextChangedObservedForCurrentBinding='{textChangedObservedForCurrentBinding}', "
				+ $"TextChangedObservationSequence='{_textChangedObservationSequence}', "
				+ $"LastTextChangedBindingEpoch='{_lastTextChangedBindingEpoch}', "
				+ $"LastTextChangedCodeEditInstanceId='{_lastTextChangedCodeEditInstanceId}'"
		);
	}

	private bool ShouldLogUnattributedCompletionRequest(
		ulong codeEditInstanceId,
		bool textChangedObservedForCurrentBinding
	)
	{
		if (!_trackedUnattributedCompletionProvenanceCodeEditIds.Contains(codeEditInstanceId))
		{
			if (
				_trackedUnattributedCompletionProvenanceCodeEditIds.Count
				>= MaxTrackedCompletionProvenanceCodeEdits
			)
			{
				return false;
			}

			_trackedUnattributedCompletionProvenanceCodeEditIds.Add(codeEditInstanceId);
		}

		if (!textChangedObservedForCurrentBinding)
			return _loggedUnattributedWithoutTextChangedCodeEditIds.Add(codeEditInstanceId);

		if (
			_lastLoggedUnattributedTextChangedSequenceByCodeEditId.TryGetValue(
				codeEditInstanceId,
				out long lastLoggedSequence
			)
			&& lastLoggedSequence == _textChangedObservationSequence
		)
		{
			return false;
		}

		_lastLoggedUnattributedTextChangedSequenceByCodeEditId[codeEditInstanceId] =
			_textChangedObservationSequence;
		return true;
	}

	private static long AdvancePositiveSequence(ref long sequence)
	{
		unchecked
		{
			sequence++;
			if (sequence <= 0)
				sequence = 1;
		}

		return sequence;
	}

	private bool IsCompletionBindingCurrent(
		EditorBindingLease expectedLease,
		CodeEdit expectedCodeEdit,
		string expectedScriptPath
	)
	{
		if (
			!_editorBinding.TryGetActiveCodeEdit(
				out CodeEdit currentCodeEdit,
				out string currentScriptPath,
				out EditorBindingLease currentBindingLease
			)
		)
		{
			return false;
		}

		if (!currentBindingLease.Equals(expectedLease))
			return false;
		if (expectedLease.CodeEditInstanceId == 0)
			return false;

		try
		{
			if (
				currentCodeEdit.GetInstanceId() != expectedLease.CodeEditInstanceId
				|| !IsValidGodotObject(expectedCodeEdit)
				|| expectedCodeEdit.GetInstanceId() != expectedLease.CodeEditInstanceId
			)
			{
				return false;
			}
		}
		catch
		{
			return false;
		}

		string normalizedLeasePath = ScriptPathUtility.Normalize(
			expectedLease.ScriptResourcePath
		);
		return string.Equals(
			ScriptPathUtility.Normalize(currentScriptPath),
			normalizedLeasePath,
			StringComparison.OrdinalIgnoreCase
		)
			&& string.Equals(
				ScriptPathUtility.Normalize(expectedScriptPath),
				normalizedLeasePath,
				StringComparison.OrdinalIgnoreCase
			);
	}

	internal void MarkCompletionPipelineFaultedFromCallbackBoundary(Exception exception)
	{
		FaultCompletionPipeline(
			"PluginSignalBoundary",
			_completionRequestObservationSequence,
			null,
			"",
			exception
		);
	}

	private void FaultCompletionPipeline(
		string stage,
		long requestObservationSequence,
		EditorBindingLease? bindingLease,
		string scriptPath,
		Exception exception
	)
	{
		if (_completionPipelineFaulted)
			return;

		_completionPipelineFaulted = true;

		try
		{
			_memberCompletionFollowUp.Clear();
		}
		catch
		{
		}

		try
		{
			_completionCoordinator.InvalidatePendingValidations(
				"CompletionPipelineFault"
			);
		}
		catch
		{
			try
			{
				_codeEditMutationCoordinator.RetireOwnedPublication(
					"CompletionPipelineFault"
				);
			}
			catch
			{
			}
		}

		LogCompletionPipelineFault(
			stage,
			requestObservationSequence,
			bindingLease,
			scriptPath,
			exception
		);
	}

	private void LogCompletionPipelineFault(
		string stage,
		long requestObservationSequence,
		EditorBindingLease? bindingLease,
		string scriptPath,
		Exception exception
	)
	{
		try
		{
			EditorBindingLease capturedLease = bindingLease.GetValueOrDefault();
			string capturedScriptPath = !string.IsNullOrWhiteSpace(scriptPath)
				? scriptPath
				: capturedLease.ScriptResourcePath ?? "";

			_debugLog(
				"C# autocomplete completion pipeline faulted",
				$"HostInstanceToken='{_hostInstanceTokenProvider()}', "
					+ $"ManagedAssemblyGeneration='{_managedAssemblyGeneration}', "
					+ $"Stage='{stage ?? ""}', "
					+ $"RequestObservationSequence='{requestObservationSequence}', "
					+ $"ScriptTransitionId='{capturedLease.ScriptTransitionId}', "
					+ $"BindingEpoch='{capturedLease.BindingEpoch}', "
					+ $"ReloadReadyEpoch='{capturedLease.ReloadReadyEpoch}', "
					+ $"CodeEditInstanceId='{capturedLease.CodeEditInstanceId}', "
					+ $"ScriptPath='{capturedScriptPath}', "
					+ $"ExceptionType='{exception?.GetType().FullName ?? ""}', "
					+ $"Exception='{exception}', "
					+ "Recovery='ColdHostRecreationRequired'"
			);
		}
		catch
		{
			// Fault containment diagnostics must never escape the completion callback.
		}
	}

	private void LogCompletionPipelineSuppressionOnce()
	{
		if (_completionPipelineSuppressionLogged)
			return;

		_completionPipelineSuppressionLogged = true;
		try
		{
			_debugLog(
				"C# autocomplete completion pipeline request suppressed",
				$"HostInstanceToken='{_hostInstanceTokenProvider()}', ManagedAssemblyGeneration='{_managedAssemblyGeneration}', Reason='PipelineFaulted'"
			);
		}
		catch
		{
			// Suppression diagnostics must never affect fail-closed behavior.
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

	private void QueueProjectRefreshAdmission(string reason)
	{
		_indexingQuiescenceCoordinator.RequestProjectRefresh(reason);
	}

	private void EnsureActiveDocumentAdmissionScheduledForCurrentLease(string reason)
	{
		if (!_activeDocumentSyntaxOverlayEnabled)
			return;

		if (
			!_scriptEditorLifecycleCoordinator.TryGetCurrentBindingLease(
				out EditorBindingLease bindingLease
			)
		)
		{
			return;
		}

		if (
			!string.Equals(
				bindingLease.ManagedAssemblyGeneration,
				_managedAssemblyGeneration,
				StringComparison.Ordinal
			)
			|| bindingLease.HostInstanceToken != _hostInstanceTokenProvider()
			|| bindingLease.ReloadReadyEpoch <= 0
		)
		{
			return;
		}

		if (!_activeDocumentIndexLifecycle.NeedsCapture(bindingLease.ScriptResourcePath))
		{
			_indexingQuiescenceCoordinator.ConsumeActiveDocument(bindingLease);
			return;
		}

		_indexingQuiescenceCoordinator.RequestActiveDocument(bindingLease, reason);
	}

	private void ProcessActiveDocumentQuiescenceAdmission(
		AutocompleteIndexingQuiescenceBatch batch
	)
	{
		EditorBindingLease expectedLease = batch.ActiveDocumentBindingLease;
		if (
			!_editorBinding.TryGetActiveCodeEdit(
				out CodeEdit codeEdit,
				out string scriptPath,
				out EditorBindingLease currentLease
			)
		)
		{
			LogStaleIndexingQuiescenceActiveDocumentAdmission(
				"CurrentBindingUnavailable",
				expectedLease,
				default
			);
			_requestScriptEditorLifecycleRebind("IndexingQuiescenceAdmission");
			return;
		}

		if (!currentLease.Equals(expectedLease))
		{
			LogStaleIndexingQuiescenceActiveDocumentAdmission(
				"BindingLeaseChanged",
				expectedLease,
				currentLease
			);
			EnsureActiveDocumentAdmissionScheduledForCurrentLease(
				"Stale quiescence admission recovery"
			);
			return;
		}

		if (
			!string.Equals(
				currentLease.ManagedAssemblyGeneration,
				_managedAssemblyGeneration,
				StringComparison.Ordinal
			)
			|| currentLease.HostInstanceToken != _hostInstanceTokenProvider()
			|| currentLease.ReloadReadyEpoch <= 0
			|| currentLease.ReloadReadyEpoch != _currentReloadReadyEpochProvider()
			|| !_reloadStabilizationReadyProvider()
		)
		{
			LogStaleIndexingQuiescenceActiveDocumentAdmission(
				"CurrentAuthorityChanged",
				expectedLease,
				currentLease
			);
			return;
		}

		if (!_activeDocumentIndexLifecycle.NeedsCapture(scriptPath))
			return;

		CaptureActiveDocumentIfNeededBestEffort(
			codeEdit,
			scriptPath,
			currentLease,
			batch.ActiveDocumentReason,
			consumeMatchingQuiescentIntent: false,
			diagnosticPhase: LogQuiescentActiveDocumentCapturePhase
		);
	}

	private void CaptureActiveDocumentIfNeededBestEffort(
		CodeEdit codeEdit,
		string scriptPath,
		EditorBindingLease bindingLease,
		string reason,
		bool consumeMatchingQuiescentIntent,
		Action<string, ScriptEditor, CodeEdit> diagnosticPhase = null
	)
	{
		if (!_activeDocumentSyntaxOverlayEnabled)
			return;

		try
		{
			if (consumeMatchingQuiescentIntent)
				_indexingQuiescenceCoordinator.ConsumeActiveDocument(bindingLease);

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

	private void LogQuiescentActiveDocumentCapturePhase(
		string phase,
		ScriptEditor scriptEditor,
		CodeEdit codeEdit
	)
	{
		if (!string.Equals(phase, "ReadActiveCodeEditText", StringComparison.Ordinal))
			return;

		Trace(
			"C# autocomplete indexing quiescence ReadActiveCodeEditText",
			$"CodeEditInstanceId='{(IsValidGodotObject(codeEdit) ? codeEdit.GetInstanceId() : 0UL)}', HostInstanceToken='{_hostInstanceTokenProvider()}', ManagedAssemblyGeneration='{_managedAssemblyGeneration}'"
		);
	}

	private void LogIndexingQuiescenceAdmission(
		AutocompleteIndexingQuiescenceBatch batch
	)
	{
		EditorBindingLease activeLease = batch.ActiveDocumentBindingLease;
		Trace(
			"C# autocomplete indexing quiescence admitted",
			$"QuietPeriodMs='{AutocompleteIndexingQuiescenceCoordinator.QuietPeriodMilliseconds}', "
				+ $"QuietDurationMs='{batch.QuietDurationSeconds * 1000.0:F1}', "
				+ $"ActiveDocumentRequested='{batch.ActiveDocumentRequested}', "
				+ $"ActiveDocumentCoalescedCount='{batch.ActiveDocumentCoalescedCount}', "
				+ $"ActiveDocumentBindingEpoch='{activeLease.BindingEpoch}', "
				+ $"ActiveDocumentScriptTransitionId='{activeLease.ScriptTransitionId}', "
				+ $"ActiveDocumentScriptPath='{activeLease.ScriptResourcePath ?? ""}', "
				+ $"ProjectRefreshRequested='{batch.ProjectRefreshRequested}', "
				+ $"ProjectRefreshCoalescedCount='{batch.ProjectRefreshCoalescedCount}', "
				+ $"ProjectRefreshReason='{batch.ProjectRefreshReason ?? ""}', "
				+ $"ManagedAssemblyGeneration='{_managedAssemblyGeneration}', "
				+ $"HostInstanceToken='{_hostInstanceTokenProvider()}', "
				+ $"ReloadReadyEpoch='{_currentReloadReadyEpochProvider()}'"
		);
	}

	private void LogStaleIndexingQuiescenceActiveDocumentAdmission(
		string reason,
		EditorBindingLease expectedLease,
		EditorBindingLease currentLease
	)
	{
		Trace(
			"C# autocomplete indexing quiescence stale active document admission rejected",
			$"Reason='{reason ?? ""}', "
				+ $"ExpectedScriptTransitionId='{expectedLease.ScriptTransitionId}', "
				+ $"ExpectedBindingEpoch='{expectedLease.BindingEpoch}', "
				+ $"ExpectedReloadReadyEpoch='{expectedLease.ReloadReadyEpoch}', "
				+ $"ExpectedCodeEditInstanceId='{expectedLease.CodeEditInstanceId}', "
				+ $"ExpectedScriptPath='{expectedLease.ScriptResourcePath ?? ""}', "
				+ $"CurrentScriptTransitionId='{currentLease.ScriptTransitionId}', "
				+ $"CurrentBindingEpoch='{currentLease.BindingEpoch}', "
				+ $"CurrentReloadReadyEpoch='{currentLease.ReloadReadyEpoch}', "
				+ $"CurrentCodeEditInstanceId='{currentLease.CodeEditInstanceId}', "
				+ $"CurrentScriptPath='{currentLease.ScriptResourcePath ?? ""}', "
				+ $"ManagedAssemblyGeneration='{_managedAssemblyGeneration}', HostInstanceToken='{_hostInstanceTokenProvider()}'"
		);
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
