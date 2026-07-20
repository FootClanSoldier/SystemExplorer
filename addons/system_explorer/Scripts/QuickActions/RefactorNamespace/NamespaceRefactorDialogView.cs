#if TOOLS
using Godot;
using System;
using System.Collections.Generic;

namespace SystemExplorer.QuickActions.RefactorNamespace;

internal sealed class NamespaceRefactorDialogView
{
	private static readonly Vector2I DialogSize = new(520, 285);
	private readonly AcceptDialog _dialog;
	private readonly Label _descriptionLabel;
	private readonly Label _oldNamespaceLabel;
	private readonly LineEdit _oldNamespaceInput;
	private readonly Label _newNamespaceLabel;
	private readonly LineEdit _newNamespaceInput;
	private readonly Label _applyToLabel;
	private readonly CheckBox _existingNamespaceOption;
	private readonly OptionButton _existingNamespaceDropdown;
	private readonly CheckBox _withoutNamespaceOption;

	internal NamespaceRefactorDialogView(
		AcceptDialog dialog,
		Label descriptionLabel,
		Label oldNamespaceLabel,
		LineEdit oldNamespaceInput,
		Label newNamespaceLabel,
		LineEdit newNamespaceInput,
		Label applyToLabel,
		CheckBox existingNamespaceOption,
		OptionButton existingNamespaceDropdown,
		CheckBox withoutNamespaceOption
	)
	{
		_dialog = dialog ?? throw new ArgumentNullException(nameof(dialog));
		_descriptionLabel =
			descriptionLabel ?? throw new ArgumentNullException(nameof(descriptionLabel));
		_oldNamespaceLabel =
			oldNamespaceLabel ?? throw new ArgumentNullException(nameof(oldNamespaceLabel));
		_oldNamespaceInput =
			oldNamespaceInput ?? throw new ArgumentNullException(nameof(oldNamespaceInput));
		_newNamespaceLabel =
			newNamespaceLabel ?? throw new ArgumentNullException(nameof(newNamespaceLabel));
		_newNamespaceInput =
			newNamespaceInput ?? throw new ArgumentNullException(nameof(newNamespaceInput));
		_applyToLabel = applyToLabel ?? throw new ArgumentNullException(nameof(applyToLabel));
		_existingNamespaceOption =
			existingNamespaceOption
			?? throw new ArgumentNullException(nameof(existingNamespaceOption));
		_existingNamespaceDropdown =
			existingNamespaceDropdown
			?? throw new ArgumentNullException(nameof(existingNamespaceDropdown));
		_withoutNamespaceOption =
			withoutNamespaceOption
			?? throw new ArgumentNullException(nameof(withoutNamespaceOption));
	}

	internal string OldNamespaceText => _oldNamespaceInput.Text;

	internal string NewNamespaceText => _newNamespaceInput.Text;

	internal bool IsWithoutNamespaceSelected => _withoutNamespaceOption.ButtonPressed;

	internal void ConfigureSingleExistingNamespace(string currentNamespace)
	{
		_descriptionLabel.Text =
			"Update the selected script namespace and\nmatching using statements in linked C# files.";
		_newNamespaceLabel.Visible = true;
		_newNamespaceInput.Visible = true;
		_oldNamespaceLabel.Visible = true;
		_oldNamespaceInput.Visible = true;
		_applyToLabel.Visible = false;
		_existingNamespaceOption.Visible = false;
		_existingNamespaceDropdown.Visible = false;
		_withoutNamespaceOption.Visible = false;

		_oldNamespaceInput.Text = currentNamespace;
		_oldNamespaceInput.Editable = false;
		_newNamespaceInput.Text = currentNamespace;
	}

	internal void ConfigureSingleAddNamespace()
	{
		_descriptionLabel.Text =
			"Add a namespace block to the selected script.\nUsing statements will not be changed.";
		_newNamespaceLabel.Visible = true;
		_newNamespaceInput.Visible = true;
		_oldNamespaceLabel.Visible = false;
		_oldNamespaceInput.Visible = false;
		_applyToLabel.Visible = false;
		_existingNamespaceOption.Visible = false;
		_existingNamespaceDropdown.Visible = false;
		_withoutNamespaceOption.Visible = false;

		_oldNamespaceInput.Text = "";
		_newNamespaceInput.Text = "";
	}

	internal void ConfigureBatch(
		IReadOnlyList<string> namespaces,
		bool hasScriptsWithoutNamespace
	)
	{
		if (namespaces == null)
			throw new ArgumentNullException(nameof(namespaces));

		_descriptionLabel.Text =
			"Refactor namespaces for scripts under the selected System or Folder.";
		_newNamespaceLabel.Visible = true;
		_newNamespaceInput.Visible = true;
		_oldNamespaceLabel.Visible = false;
		_oldNamespaceInput.Visible = false;
		_applyToLabel.Visible = true;
		_existingNamespaceOption.Visible = true;
		_existingNamespaceDropdown.Visible = true;
		_withoutNamespaceOption.Visible = true;

		_newNamespaceInput.Text = namespaces.Count > 0 ? namespaces[0] : "";
		_oldNamespaceInput.Text = "";

		_existingNamespaceDropdown.Clear();
		foreach (string namespaceName in namespaces)
			_existingNamespaceDropdown.AddItem(namespaceName);

		bool hasExistingNamespaces = namespaces.Count > 0;
		_existingNamespaceOption.Disabled = !hasExistingNamespaces;
		_existingNamespaceDropdown.Disabled = !hasExistingNamespaces;
		_withoutNamespaceOption.Disabled = !hasScriptsWithoutNamespace;

		bool useExistingNamespaceMode = hasExistingNamespaces;
		SetBatchApplyMode(useExistingNamespaceMode);
	}

	internal void SetBatchApplyMode(bool useExistingNamespaceMode)
	{
		_existingNamespaceOption.SetPressedNoSignal(useExistingNamespaceMode);
		_withoutNamespaceOption.SetPressedNoSignal(!useExistingNamespaceMode);
		_existingNamespaceDropdown.Disabled =
			!useExistingNamespaceMode || _existingNamespaceDropdown.ItemCount == 0;
	}

	internal void SelectExistingNamespace(long index)
	{
		if (index < 0 || index >= _existingNamespaceDropdown.ItemCount)
			return;

		_newNamespaceInput.Text = _existingNamespaceDropdown.GetItemText((int)index);
		_newNamespaceInput.GrabFocus();
		_newNamespaceInput.SelectAll();
	}

	internal string GetSelectedExistingNamespace()
	{
		int selectedIndex = _existingNamespaceDropdown.Selected;
		return selectedIndex >= 0
			? _existingNamespaceDropdown.GetItemText(selectedIndex)
			: "";
	}

	internal void ApplySize()
	{
		_dialog.MinSize = DialogSize;
		_dialog.Size = DialogSize;
	}

	internal void PopupCentered()
	{
		_dialog.PopupCentered(DialogSize);
	}

	internal void FocusNewNamespace(bool selectAll)
	{
		_newNamespaceInput.GrabFocus();

		if (selectAll)
			_newNamespaceInput.SelectAll();
	}
}
#endif
