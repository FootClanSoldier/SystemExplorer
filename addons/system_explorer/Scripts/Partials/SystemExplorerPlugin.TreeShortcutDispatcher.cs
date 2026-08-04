#if TOOLS
using Godot;
using System;
using System.Collections.Generic;

public partial class SystemExplorerPlugin
{
	#region Tree Shortcut Dispatcher
	private enum TreeShortcutCommandId
	{
		Beautify,
		NewScript,
		RemoveSelectedItem,
		ToggleTreeScriptEditorFocus,
		NewFolder,
		RenameSelectedItem,
		CollapseTree,
		AddExistingScripts,
		AddExistingScenes,
		RefactorNamespace,
	}

	private enum TreeShortcutInputRoute
	{
		HiddenFocusTarget,
		TreeGuiInput,
	}

	private readonly record struct TreeShortcutCommandDefinition(
		TreeShortcutCommandId CommandId,
		string ShortcutPath,
		string DisplayName
	);

	private static readonly TreeShortcutCommandDefinition[] TreeShortcutCommands =
	{
		new(
			TreeShortcutCommandId.Beautify,
			BeautifyEditorShortcutPath,
			BeautifyEditorShortcutDisplayName
		),
		new(
			TreeShortcutCommandId.NewScript,
			NewScriptEditorShortcutPath,
			NewScriptEditorShortcutDisplayName
		),
		new(
			TreeShortcutCommandId.RemoveSelectedItem,
			RemoveSelectedItemEditorShortcutPath,
			RemoveSelectedItemEditorShortcutDisplayName
		),
		new(
			TreeShortcutCommandId.ToggleTreeScriptEditorFocus,
			ToggleTreeScriptEditorFocusShortcutPath,
			ToggleTreeScriptEditorFocusShortcutDisplayName
		),
		new(
			TreeShortcutCommandId.NewFolder,
			NewFolderEditorShortcutPath,
			NewFolderEditorShortcutDisplayName
		),
		new(
			TreeShortcutCommandId.RenameSelectedItem,
			RenameSelectedItemEditorShortcutPath,
			RenameSelectedItemEditorShortcutDisplayName
		),
		new(
			TreeShortcutCommandId.CollapseTree,
			CollapseTreeEditorShortcutPath,
			CollapseTreeEditorShortcutDisplayName
		),
		new(
			TreeShortcutCommandId.AddExistingScripts,
			AddExistingScriptsEditorShortcutPath,
			AddExistingScriptsEditorShortcutDisplayName
		),
		new(
			TreeShortcutCommandId.AddExistingScenes,
			AddExistingScenesEditorShortcutPath,
			AddExistingScenesEditorShortcutDisplayName
		),
		new(
			TreeShortcutCommandId.RefactorNamespace,
			RefactorNamespaceEditorShortcutPath,
			RefactorNamespaceEditorShortcutDisplayName
		),
	};

	private void CreateTreeShortcutConflictDialog()
	{
		_treeShortcutConflictDialog = new AcceptDialog
		{
			Title = "Shortcut Conflict",
			OkButtonText = "OK",
			MinSize = new Vector2I(500, 200),
		};
	}

	private bool HandleGlobalTreeShortcutDispatch(InputEvent inputEvent)
	{
		return inputEvent is InputEventKey keyEvent
			&& TryDispatchTreeShortcut(
				keyEvent,
				TreeShortcutInputRoute.HiddenFocusTarget
			);
	}

	private bool TryDispatchTreeShortcut(
		InputEventKey keyEvent,
		TreeShortcutInputRoute route
	)
	{
		if (keyEvent == null || !keyEvent.Pressed || keyEvent.Echo)
			return false;

		if (TryGetTreeKeyboardNavigationCommand(keyEvent, out _))
			return false;

		if (!IsTreeShortcutInputRouteActive(route))
			return false;

		var matches = new List<TreeShortcutCommandDefinition>(
			TreeShortcutCommands.Length
		);

		if (!TryCollectMatchingTreeShortcutCommands(keyEvent, matches))
			return false;

		if (matches.Count == 0)
			return false;

		if (!TryConsumeTreeShortcutInput(route))
			return false;

		if (matches.Count > 1)
		{
			ShowTreeShortcutConflictDialog(matches);
			return true;
		}

		bool commandExecuted = TryExecuteTreeShortcutCommand(
			matches[0].CommandId
		);

		if (!commandExecuted)
		{
			DebugLogger.LogOperation(
				"Tree shortcut command unavailable",
				$"Command='{matches[0].DisplayName}', Route='{route}'"
			);
		}

		return true;
	}

	private bool IsTreeShortcutInputRouteActive(TreeShortcutInputRoute route)
	{
		switch (route)
		{
			case TreeShortcutInputRoute.HiddenFocusTarget:
			{
				Control focusedControl = GetTree()?.Root?.GuiGetFocusOwner();
				return IsSystemExplorerFocusReleaseTarget(focusedControl);
			}

			case TreeShortcutInputRoute.TreeGuiInput:
				return _tree != null
					&& GodotObject.IsInstanceValid(_tree)
					&& !_tree.IsQueuedForDeletion()
					&& _tree.IsInsideTree();

			default:
				return false;
		}
	}

	private bool TryCollectMatchingTreeShortcutCommands(
		InputEventKey keyEvent,
		List<TreeShortcutCommandDefinition> matches
	)
	{
		if (keyEvent == null || matches == null)
			return false;

		matches.Clear();

		try
		{
			if (!EnsureEditorShortcutsRegistered())
				return false;

			EditorSettings editorSettings =
				EditorInterface.Singleton?.GetEditorSettings();

			if (editorSettings == null)
			{
				DebugLogger.LogOperation(
					"Tree shortcut matching unavailable",
					"EditorSettings was null."
				);
				return false;
			}

			var matchedCommandIds = new HashSet<TreeShortcutCommandId>();

			foreach (TreeShortcutCommandDefinition definition in TreeShortcutCommands)
			{
				if (!editorSettings.HasShortcut(definition.ShortcutPath))
				{
					matches.Clear();
					DebugLogger.LogOperation(
						"Tree shortcut matching failed",
						$"Path='{definition.ShortcutPath}' was not registered."
					);
					return false;
				}

				bool isMatch = editorSettings.IsShortcut(
					definition.ShortcutPath,
					keyEvent
				);

				if (!isMatch)
					continue;

				Shortcut currentShortcut = editorSettings.GetShortcut(
					definition.ShortcutPath
				);

				if (currentShortcut == null || !currentShortcut.HasValidEvent())
					continue;

				if (matchedCommandIds.Add(definition.CommandId))
					matches.Add(definition);
			}

			return true;
		}
		catch (Exception exception)
		{
			matches.Clear();
			DebugLogger.LogOperation(
				"Tree shortcut matching failed",
				exception.ToString()
			);
			return false;
		}
	}

	private bool TryConsumeTreeShortcutInput(TreeShortcutInputRoute route)
	{
		try
		{
			switch (route)
			{
				case TreeShortcutInputRoute.HiddenFocusTarget:
					GetViewport().SetInputAsHandled();
					return true;

				case TreeShortcutInputRoute.TreeGuiInput:
					_tree.AcceptEvent();
					return true;

				default:
					return false;
			}
		}
		catch (Exception exception)
		{
			DebugLogger.LogOperation(
				"Tree shortcut input consumption failed",
				$"Route='{route}', Exception='{exception}'"
			);
			return false;
		}
	}

	private bool TryExecuteTreeShortcutCommand(
		TreeShortcutCommandId commandId
	)
	{
		try
		{
			return commandId switch
			{
				TreeShortcutCommandId.Beautify =>
					TryHandleBeautifyShortcutForSelectedItem(),
				TreeShortcutCommandId.NewScript =>
					!_isFilteringScripts
					&& TryOpenCreateScriptDialogForSelectedItem(),
				TreeShortcutCommandId.RemoveSelectedItem =>
					!_isFilteringScripts
					&& TryOpenRemoveDialogForSelectedItem(),
				TreeShortcutCommandId.ToggleTreeScriptEditorFocus =>
					TryFocusCurrentScriptEditorCursorFromSystemExplorer(
						SystemExplorerToggleFocusReturnTarget.Tree
					),
				TreeShortcutCommandId.NewFolder =>
					!_isFilteringScripts
					&& TryOpenAddFolderDialogForSelectedItem(),
				TreeShortcutCommandId.RenameSelectedItem =>
					TryOpenRenameDialogForSelectedItem(),
				TreeShortcutCommandId.CollapseTree =>
					!_isFilteringScripts
					&& TryCollapseEntireTreeFromShortcut(),
				TreeShortcutCommandId.AddExistingScripts =>
					!_isFilteringScripts
					&& TryOpenAddExistingScriptsDialogForSelectedItem(),
				TreeShortcutCommandId.AddExistingScenes =>
					!_isFilteringScripts
					&& TryOpenAddExistingScenesDialogForSelectedItem(),
				TreeShortcutCommandId.RefactorNamespace =>
					TryOpenNamespaceRefactorDialogForSelectedItem(),
				_ => false,
			};
		}
		catch (Exception exception)
		{
			DebugLogger.LogOperation(
				"Tree shortcut command execution failed",
				$"Command='{commandId}', Exception='{exception}'"
			);
			return false;
		}
	}

	private void ShowTreeShortcutConflictDialog(
		IReadOnlyList<TreeShortcutCommandDefinition> matches
	)
	{
		var displayNames = new List<string>();
		var seenCommandIds = new HashSet<TreeShortcutCommandId>();

		if (matches != null)
		{
			foreach (TreeShortcutCommandDefinition match in matches)
			{
				if (!seenCommandIds.Add(match.CommandId))
					continue;

				displayNames.Add(
					string.IsNullOrWhiteSpace(match.DisplayName)
						? match.CommandId.ToString()
						: match.DisplayName
				);
			}
		}

		string commandList = displayNames.Count == 0
			? "• Unknown System Explorer command"
			: $"• {string.Join("\n• ", displayNames)}";
		string message =
			"This shortcut is assigned to multiple System Explorer commands:\n\n"
			+ commandList
			+ "\n\nNo command was run. Change one of these bindings under "
			+ "Editor Settings > Shortcuts > System Explorer, then try again.";
		string details = $"Commands='{string.Join(", ", displayNames)}'";

		if (
			_treeShortcutConflictDialog != null
			&& GodotObject.IsInstanceValid(_treeShortcutConflictDialog)
			&& !_treeShortcutConflictDialog.IsQueuedForDeletion()
			&& _treeShortcutConflictDialog.IsInsideTree()
		)
		{
			try
			{
				_treeShortcutConflictDialog.Title = "Shortcut Conflict";
				_treeShortcutConflictDialog.DialogText = message;
				_treeShortcutConflictDialog.PopupCentered();
				return;
			}
			catch (Exception exception)
			{
				DebugLogger.LogOperation(
					"Shortcut conflict dialog presentation failed",
					$"{details}, Exception='{exception}'"
				);
			}
		}
		else
		{
			DebugLogger.LogOperation(
				"Shortcut conflict dialog unavailable",
				details
			);
		}

		GD.PushWarning($"Shortcut Conflict: {message}");
	}
	#endregion
}
#endif
