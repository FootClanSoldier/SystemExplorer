#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using SystemExplorer.Autocomplete.Confirmation;
using SystemExplorer.Autocomplete.Indexing;
using SystemExplorer.Autocomplete.Indexing.ActiveDocument;
using SystemExplorer.Autocomplete.Indexing.Context;
using SystemExplorer.Autocomplete.Indexing.Persistence;
using SystemExplorer.Autocomplete.Styling;

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
	private readonly AutocompleteEditorBinding _editorBinding;
	private readonly AutocompleteCompletionCoordinator _completionCoordinator;
	private readonly AutocompleteCodeEditThemeController _themeController;
	private readonly AutocompletePrefixExtractor _prefixExtractor;
	private readonly AutocompleteCodeEditPresenter _presenter;
	private readonly AutocompleteCompletionMatchPolicy _matchPolicy;
	private readonly ProjectTypeCompletionSource _projectTypeCompletionSource;
	private readonly AutocompleteCompletionOptionMetadataCodec _metadataCodec;
	private readonly AutocompleteCompletionConfirmationBridge _confirmationBridge;
	private readonly Action<string, string> _debugLog;

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

		_projectTypeCompletionSource = new ProjectTypeCompletionSource(
			() => _projectIndex.CurrentSnapshot,
			() => _activeDocumentIndex.CurrentSnapshot,
			completionContextResolver
		);

		IAutocompleteCompletionSource[] completionSources =
		{
			_projectTypeCompletionSource,
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

		_editorBinding = new AutocompleteEditorBinding(
			scriptEditorProvider,
			connectPluginSignal,
			disconnectPluginSignal,
			scriptChangedMethodName,
			textChangedMethodName,
			completionRequestedMethodName,
			guiInputMethodName,
			_completionCoordinator.InvalidatePendingValidations,
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
	}

	internal void HandleScriptChanged()
	{
		DrainIndexBuildResults();
		_completionCoordinator.InvalidatePendingValidations();
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
		CaptureActiveDocumentIfNeededBestEffort(
			codeEdit,
			scriptPath,
			"Code completion requested"
		);
		DrainIndexBuildResults();
		_completionCoordinator.HandleCompletionRequested(codeEdit, scriptPath);
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

		_completionCoordinator.ValidateAfterTextChanged(codeEdit, generation);
		DrainIndexBuildResults();
	}

	internal void InvalidatePendingValidations()
	{
		_completionCoordinator.InvalidatePendingValidations();
	}

	internal void ResetTransientState()
	{
		_completionCoordinator.Reset();
		_activeDocumentIndexLifecycle.ResetTransientState();
		_projectIndexLifecycle.ResetTransientState();
		_cacheCoordinator.ResetTransientState();
	}

	internal void Shutdown()
	{
		_projectIndexLifecycle.Shutdown();
		_indexCoordinator.Shutdown();
		_activeDocumentIndexLifecycle.Shutdown();
		_activeDocumentIndexCoordinator.Shutdown();
		_cacheCoordinator.Shutdown();
		_indexLifetime.Shutdown();
		_completionCoordinator.InvalidatePendingValidations();
		_editorBinding.Shutdown();
		_themeController.Reset();
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
				_activeDocumentIndexLifecycle.CapturePendingText(
					codeEdit,
					scriptPath,
					reason
				);
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

		_activeDocumentIndexLifecycle.HandleBuildFailure(result);
		_debugLog(
			"C# autocomplete active document index build failed",
			result.CreateDebugSummary()
		);
	}
}
#endif
