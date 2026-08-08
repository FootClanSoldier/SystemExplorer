#if TOOLS
using Godot;
using System;
using SystemExplorer.Autocomplete;

public partial class SystemExplorerPlugin
{
	#region C# Autocomplete Integration
	private AutocompletePluginHost _autocompleteHost;
	private bool _autocompleteTextChangedRecoveryQueued;

	private AutocompletePluginHost CreateAutocompleteHost()
	{
		return new AutocompletePluginHost(
			() => EditorInterface.Singleton?.GetScriptEditor(),
			() => EditorInterface.Singleton?.GetResourceFilesystem(),
			() => ProjectSettings.GlobalizePath("res://"),
			TryConnectPluginSignal,
			DisconnectPluginSignal,
			nameof(OnAutocompleteScriptChanged),
			nameof(OnAutocompleteTextChanged),
			nameof(OnAutocompleteCodeCompletionRequested),
			nameof(OnAutocompleteCodeEditGuiInput),
			nameof(OnAutocompleteProjectFilesystemChanged),
			(operation, details) => DebugLogger.LogOperation(operation, details)
		);
	}

	private bool TryEnsureAutocompleteHost(out AutocompletePluginHost host)
	{
		if (_autocompleteHost != null)
		{
			host = _autocompleteHost;
			return true;
		}

		try
		{
			_autocompleteHost = CreateAutocompleteHost();
			host = _autocompleteHost;
			DebugLogger.LogOperation(
				"C# autocomplete host restored",
				"Rebuilt the managed autocomplete feature graph."
			);
			return true;
		}
		catch (Exception exception)
		{
			_autocompleteHost = null;
			host = null;
			DebugLogger.LogOperation(
				"C# autocomplete host recovery failed: composition",
				exception.ToString()
			);
			return false;
		}
	}

	private bool EnsureAutocompleteLifecycleCurrent()
	{
		bool lifecycleCurrent =
			TryEnsureAutocompleteHost(out AutocompletePluginHost host)
			&& host.EnsureLifecycleCurrent();
		RefreshEditorPluginProcessingState();
		return lifecycleCurrent;
	}

	private void ResetAutocompleteTransientStateAfterManagedAssemblyReload()
	{
		_autocompleteTextChangedRecoveryQueued = false;
		_autocompleteHost?.ResetTransientState();
		RefreshEditorPluginProcessingState();
	}

	private void ShutdownAutocomplete()
	{
		_autocompleteTextChangedRecoveryQueued = false;
		_autocompleteHost?.Shutdown();
		_autocompleteHost = null;
	}

	private void OnAutocompleteScriptChanged(Script script)
	{
		_autocompleteHost?.InvalidatePendingValidations();

		if (!EnsureManagedAssemblyStateCurrent("C# Autocomplete Script Changed"))
			return;

		if (TryEnsureAutocompleteHost(out AutocompletePluginHost host))
			host.HandleScriptChanged();

		RefreshEditorPluginProcessingState();
	}

	private void OnAutocompleteCodeCompletionRequested()
	{
		if (!EnsureManagedAssemblyStateCurrent("C# Autocomplete Completion Requested"))
			return;

		if (TryEnsureAutocompleteHost(out AutocompletePluginHost host))
			host.HandleCompletionRequested();

		RefreshEditorPluginProcessingState();
	}

	private void OnAutocompleteCodeEditGuiInput(InputEvent inputEvent)
	{
		if (!EnsureManagedAssemblyStateCurrent("C# Autocomplete CodeEdit Input"))
			return;

		if (TryEnsureAutocompleteHost(out AutocompletePluginHost host))
			host.HandleCodeEditGuiInput(inputEvent);
	}

	private void OnAutocompleteProjectFilesystemChanged()
	{
		if (!EnsureManagedAssemblyStateCurrent("C# Autocomplete Filesystem Changed"))
			return;

		if (TryEnsureAutocompleteHost(out AutocompletePluginHost host))
			host.HandleProjectFilesystemChanged();
	}

	private void OnAutocompleteTextChanged()
	{
		AutocompletePluginHost host = _autocompleteHost;

		if (host != null)
		{
			long generation = host.BeginTextChangedValidation();
			RefreshEditorPluginProcessingState();
			CallDeferred(
				nameof(ValidateAutocompleteAfterTextChangedDeferred),
				generation
			);
			return;
		}

		QueueAutocompleteTextChangedRecovery();
	}

	private void QueueAutocompleteTextChangedRecovery()
	{
		if (_autocompleteTextChangedRecoveryQueued)
			return;

		_autocompleteTextChangedRecoveryQueued = true;
		CallDeferred(nameof(RecoverAutocompleteAfterTextChangedDeferred));
	}

	private void RecoverAutocompleteAfterTextChangedDeferred()
	{
		_autocompleteTextChangedRecoveryQueued = false;

		if (
			_editorOperationShutdownStarted
			|| !GodotObject.IsInstanceValid(this)
			|| !IsInsideTree()
		)
		{
			return;
		}

		EnsureManagedAssemblyStateCurrent(
			"C# Autocomplete Text Changed Recovery"
		);
	}

	private void ValidateAutocompleteAfterTextChangedDeferred(long generation)
	{
		AutocompletePluginHost scheduledHost = _autocompleteHost;

		if (scheduledHost == null || !scheduledHost.IsValidationCurrent(generation))
			return;

		if (!EnsureManagedAssemblyStateCurrent("C# Autocomplete Text Validation"))
			return;

		if (!TryEnsureAutocompleteHost(out AutocompletePluginHost currentHost))
			return;

		if (
			!ReferenceEquals(scheduledHost, currentHost)
			|| !currentHost.IsValidationCurrent(generation)
		)
		{
			return;
		}

		currentHost.ValidateAfterTextChanged(generation);
		RefreshEditorPluginProcessingState();
	}

	private bool HasPendingAutocompleteProcessWork()
	{
		try
		{
			return _autocompleteHost?.HasPendingCompletionProcessWork() == true;
		}
		catch
		{
			return false;
		}
	}

	private void ProcessPendingAutocompleteProcessWork()
	{
		if (!HasPendingAutocompleteProcessWork())
			return;

		if (
			_editorOperationShutdownStarted
			|| !GodotObject.IsInstanceValid(this)
			|| !IsInsideTree()
		)
		{
			ClearPendingAutocompleteProcessWork();
			return;
		}

		if (!EnsureManagedAssemblyStateCurrent("C# Autocomplete Process Follow-up"))
		{
			ClearPendingAutocompleteProcessWork();
			return;
		}

		if (!TryEnsureAutocompleteHost(out AutocompletePluginHost host))
		{
			ClearPendingAutocompleteProcessWork();
			return;
		}

		host.ProcessPendingCompletionWork();
	}

	private void ClearPendingAutocompleteProcessWork()
	{
		try
		{
			_autocompleteHost?.ClearPendingCompletionProcessWork();
		}
		catch
		{
		}
	}
	#endregion
}
#endif
