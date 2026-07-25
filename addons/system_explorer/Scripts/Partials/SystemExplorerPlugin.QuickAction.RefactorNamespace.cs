#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using SystemExplorer.QuickActions.RefactorNamespace;

public partial class SystemExplorerPlugin
{
	#region Refactor Namespace UI and Host Composition
	private const string NamespaceRefactorIntegrationWarning =
		"Refactor Namespace could not be opened because its editor dialog integration could not be restored safely after the C# assembly reload. Disable and re-enable System Explorer or restart Godot.";

	private void CreateNamespaceRefactorDialogs()
	{
		_namespaceRefactorDialog = new AcceptDialog
		{
			Title = "Refactor Namespace",
			Unresizable = true,
		};

		_namespaceRefactorIncompleteWriteReportDialog = new AcceptDialog
		{
			Title = "Namespace Refactor Incomplete",
			OkButtonText = "OK",
			MinSize = new Vector2I(460, 160),
			Unresizable = true,
		};

		var container = new VBoxContainer
		{
			CustomMinimumSize = new Vector2(480, 0),
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
			SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
		};

		_namespaceRefactorDescriptionLabel = new Label
		{
			Text =
				"Update the selected script namespace and\nmatching using statements in linked C# files.",
			AutowrapMode = TextServer.AutowrapMode.Off,
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
		};
		container.AddChild(_namespaceRefactorDescriptionLabel);
		container.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) });

		_namespaceRefactorNewNamespaceLabel = new Label { Text = "New namespace" };
		container.AddChild(_namespaceRefactorNewNamespaceLabel);
		_namespaceRefactorNewNamespaceInput = new LineEdit
		{
			PlaceholderText = "New namespace",
		};
		container.AddChild(_namespaceRefactorNewNamespaceInput);

		_namespaceRefactorOldNamespaceLabel = new Label { Text = "Old namespace" };
		container.AddChild(_namespaceRefactorOldNamespaceLabel);
		_namespaceRefactorOldNamespaceInput = new LineEdit
		{
			PlaceholderText = "Old namespace",
			Editable = false,
		};
		container.AddChild(_namespaceRefactorOldNamespaceInput);

		_namespaceRefactorApplyToLabel = new Label { Text = "Apply to:" };
		container.AddChild(_namespaceRefactorApplyToLabel);

		var applyModeGroup = new ButtonGroup();

		_namespaceRefactorExistingNamespaceOption = new CheckBox
		{
			Text = "All scripts with namespace:",
			ButtonPressed = true,
			ButtonGroup = applyModeGroup,
		};
		container.AddChild(_namespaceRefactorExistingNamespaceOption);

		_namespaceRefactorExistingNamespaceDropdown = new OptionButton
		{
			SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
		};
		container.AddChild(_namespaceRefactorExistingNamespaceDropdown);

		_namespaceRefactorWithoutNamespaceOption = new CheckBox
		{
			Text = "Scripts without namespace",
			ButtonGroup = applyModeGroup,
		};
		container.AddChild(_namespaceRefactorWithoutNamespaceOption);

		_namespaceRefactorDialog.AddChild(container);
	}

	private void ConnectNamespaceRefactorDialogSignals()
	{
		_namespaceRefactorDialog.Confirmed += OnNamespaceRefactorConfirmed;
		_namespaceRefactorDialog.WindowInput += OnNamespaceRefactorDialogWindowInput;
		_namespaceRefactorExistingNamespaceOption.Toggled +=
			OnNamespaceRefactorExistingNamespaceOptionToggled;
		_namespaceRefactorExistingNamespaceDropdown.ItemSelected +=
			OnNamespaceRefactorExistingNamespaceSelected;
		_namespaceRefactorWithoutNamespaceOption.Toggled +=
			OnNamespaceRefactorWithoutNamespaceOptionToggled;
		_namespaceRefactorOldNamespaceInput.TextSubmitted +=
			OnNamespaceRefactorOldNamespaceSubmitted;
		_namespaceRefactorNewNamespaceInput.TextSubmitted +=
			OnNamespaceRefactorNewNamespaceSubmitted;
	}

	private NamespaceRefactorPluginHost CreateNamespaceRefactorHost()
	{
		return new NamespaceRefactorPluginHost(
			_namespaceRefactorDialog,
			_namespaceRefactorIncompleteWriteReportDialog,
			_namespaceRefactorDescriptionLabel,
			_namespaceRefactorOldNamespaceLabel,
			_namespaceRefactorOldNamespaceInput,
			_namespaceRefactorNewNamespaceLabel,
			_namespaceRefactorNewNamespaceInput,
			_namespaceRefactorApplyToLabel,
			_namespaceRefactorExistingNamespaceOption,
			_namespaceRefactorExistingNamespaceDropdown,
			_namespaceRefactorWithoutNamespaceOption,
			OpenScriptEditorBufferLocator,
			OpenScriptEditorBufferAutosaveCoordinator,
			OpenScriptEditorBufferBatchService,
			() => _systems,
			GetSystemNameFromMetadata,
			GetFolderPathFromMetadata,
			GetEntryFromMetadata,
			GetScriptPathFromEntry,
			GetFolderPathFromEntry,
			SceneEntryMarker,
			EnsureSystemsLoadedForTreeOperation,
			() => EditorInterface.Singleton,
			(entry, path) => OpenMissingScriptDialog(entry, path),
			DebugLogger.Log,
			() => DebugLogger.IsEnabled,
			message => GD.PushWarning(message),
			(operation, details) => DebugLogger.LogOperation(operation, details),
			BeginBatchScriptEditorContextPreservation,
			EndBatchScriptEditorContextPreservation,
			SyncSelectionToActiveScriptAfterOperation,
			ReleaseTreeFocusAfterNavigation
		);
	}

	private bool TryEnsureNamespaceRefactorHost(
		out NamespaceRefactorPluginHost namespaceRefactorHost
	)
	{
		namespaceRefactorHost = null;
		List<string> invalidComponents = GetInvalidNamespaceRefactorUiComponents();

		if (invalidComponents.Count > 0)
		{
			DebugLogger.LogOperation(
				"Refactor Namespace host recovery failed: invalid UI",
				string.Join(", ", invalidComponents)
			);
			GD.PushWarning(NamespaceRefactorIntegrationWarning);
			return false;
		}

		if (
			_namespaceRefactorHost != null
			&& _namespaceRefactorHost.IsBoundTo(
				_namespaceRefactorDialog,
				_namespaceRefactorIncompleteWriteReportDialog,
				_namespaceRefactorDescriptionLabel,
				_namespaceRefactorOldNamespaceLabel,
				_namespaceRefactorOldNamespaceInput,
				_namespaceRefactorNewNamespaceLabel,
				_namespaceRefactorNewNamespaceInput,
				_namespaceRefactorApplyToLabel,
				_namespaceRefactorExistingNamespaceOption,
				_namespaceRefactorExistingNamespaceDropdown,
				_namespaceRefactorWithoutNamespaceOption
			)
		)
		{
			namespaceRefactorHost = _namespaceRefactorHost;
			return true;
		}

		try
		{
			_namespaceRefactorHost = CreateNamespaceRefactorHost();
			namespaceRefactorHost = _namespaceRefactorHost;
			DebugLogger.LogOperation(
				"Refactor Namespace host restored",
				"Rebuilt managed host and feature graph around existing plugin-owned UI."
			);
			return true;
		}
		catch (Exception exception)
		{
			_namespaceRefactorHost = null;
			DebugLogger.LogOperation(
				"Refactor Namespace host recovery failed: composition",
				exception.ToString()
			);
			GD.PushWarning(NamespaceRefactorIntegrationWarning);
			return false;
		}
	}

	private List<string> GetInvalidNamespaceRefactorUiComponents()
	{
		var invalidComponents = new List<string>();
		AddInvalidGodotObject(invalidComponents, nameof(_namespaceRefactorDialog), _namespaceRefactorDialog);
		AddInvalidGodotObject(invalidComponents, nameof(_namespaceRefactorIncompleteWriteReportDialog), _namespaceRefactorIncompleteWriteReportDialog);
		AddInvalidGodotObject(invalidComponents, nameof(_namespaceRefactorDescriptionLabel), _namespaceRefactorDescriptionLabel);
		AddInvalidGodotObject(invalidComponents, nameof(_namespaceRefactorNewNamespaceLabel), _namespaceRefactorNewNamespaceLabel);
		AddInvalidGodotObject(invalidComponents, nameof(_namespaceRefactorNewNamespaceInput), _namespaceRefactorNewNamespaceInput);
		AddInvalidGodotObject(invalidComponents, nameof(_namespaceRefactorOldNamespaceLabel), _namespaceRefactorOldNamespaceLabel);
		AddInvalidGodotObject(invalidComponents, nameof(_namespaceRefactorOldNamespaceInput), _namespaceRefactorOldNamespaceInput);
		AddInvalidGodotObject(invalidComponents, nameof(_namespaceRefactorApplyToLabel), _namespaceRefactorApplyToLabel);
		AddInvalidGodotObject(invalidComponents, nameof(_namespaceRefactorExistingNamespaceOption), _namespaceRefactorExistingNamespaceOption);
		AddInvalidGodotObject(invalidComponents, nameof(_namespaceRefactorExistingNamespaceDropdown), _namespaceRefactorExistingNamespaceDropdown);
		AddInvalidGodotObject(invalidComponents, nameof(_namespaceRefactorWithoutNamespaceOption), _namespaceRefactorWithoutNamespaceOption);
		return invalidComponents;
	}

	private static void AddInvalidGodotObject(
		List<string> invalidComponents,
		string componentName,
		GodotObject component
	)
	{
		if (component == null)
		{
			invalidComponents.Add($"{componentName}=null");
			return;
		}

		if (!GodotObject.IsInstanceValid(component))
			invalidComponents.Add($"{componentName}=invalid");
	}

	private void OpenNamespaceRefactorDialog(string metadata)
	{
		if (!TryEnsureNamespaceRefactorHost(out NamespaceRefactorPluginHost host))
			return;

		host.Open(metadata);
	}

	private void OnNamespaceRefactorConfirmed()
	{
		if (TryEnsureNamespaceRefactorHost(out NamespaceRefactorPluginHost host))
			host.ConfirmDialog();
	}

	private void OnNamespaceRefactorDialogWindowInput(InputEvent inputEvent)
	{
		if (!IsEnterPressed(inputEvent))
			return;

		ConfirmNamespaceRefactorDialogFromEnter();
	}

	private void OnNamespaceRefactorOldNamespaceSubmitted(string _)
	{
		ConfirmNamespaceRefactorDialogFromEnter();
	}

	private void OnNamespaceRefactorNewNamespaceSubmitted(string _)
	{
		ConfirmNamespaceRefactorDialogFromEnter();
	}

	private void ConfirmNamespaceRefactorDialogFromEnter()
	{
		if (
			_namespaceRefactorDialog == null
			|| !GodotObject.IsInstanceValid(_namespaceRefactorDialog)
			|| !_namespaceRefactorDialog.Visible
		)
		{
			return;
		}

		if (!TryEnsureNamespaceRefactorHost(out NamespaceRefactorPluginHost host))
			return;

		_namespaceRefactorDialog.Hide();
		host.ConfirmDialog();
	}

	private void OnNamespaceRefactorExistingNamespaceOptionToggled(bool pressed)
	{
		if (!pressed)
			return;

		if (TryEnsureNamespaceRefactorHost(out NamespaceRefactorPluginHost host))
			host.SetBatchApplyMode(true);
	}

	private void OnNamespaceRefactorExistingNamespaceSelected(long index)
	{
		if (TryEnsureNamespaceRefactorHost(out NamespaceRefactorPluginHost host))
			host.SelectExistingNamespace(index);
	}

	private void OnNamespaceRefactorWithoutNamespaceOptionToggled(bool pressed)
	{
		if (!pressed)
			return;

		if (TryEnsureNamespaceRefactorHost(out NamespaceRefactorPluginHost host))
			host.SetBatchApplyMode(false);
	}
	#endregion
}
#endif
