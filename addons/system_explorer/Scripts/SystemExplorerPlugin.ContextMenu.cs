#if TOOLS
using Godot;

public partial class SystemExplorerPlugin
{
	#region Context Menu Constants and Fields
	private const int ContextAddFolder = 0;
	private const int ContextAddScript = 1;
	private const int ContextNewScript = 2;
	private const int ContextRename = 3;
	private const int ContextRemove = 4;
	private const int ContextLinkScene = 5;
	private const int ContextUnlinkScene = 6;
	private const int ContextShowInFileManager = 7;
	private const int ContextAddScene = 8;
	private const int ContextRefactorNamespace = 9;
	private const int ContextBeautifyScript = 10;
	private const int ContextBeautifyScripts = 11;

	private Texture2D _contextHiddenSubmenuIcon;
	#endregion

	#region Context Menu
	private void OpenContextMenuForMetadata(string metadata)
	{
		if (string.IsNullOrWhiteSpace(metadata))
			return;

		_pendingRemoveMetadata = metadata;
		_pendingRenameMetadata = metadata;
		_pendingAddFolderMetadata = GetAddFolderTargetMetadata(metadata);
		_pendingShowInFileManagerMetadata = metadata;
		_pendingBeautifyScriptMetadata = metadata;

		BuildContextMenuForMetadata(metadata);

		_contextMenu.Position = DisplayServer.MouseGetPosition();
		_contextMenu.Popup();
	}

	private string GetAddFolderTargetMetadata(string metadata)
	{
		if (string.IsNullOrWhiteSpace(metadata) || !metadata.StartsWith("script::"))
			return metadata;

		string entry = GetEntryFromMetadata(metadata);
		string systemName = FindSystemNameForEntry(entry);

		if (string.IsNullOrWhiteSpace(systemName))
			return metadata;

		string folderPath = GetFolderPathFromEntry(entry);
		return string.IsNullOrWhiteSpace(folderPath)
			? $"system::{systemName}"
			: $"folder::{systemName}::{folderPath}";
	}

	private void BuildContextMenuForMetadata(string metadata)
	{
		_contextMenu.Clear();
		_contextNewSubmenu.Clear();
		_contextAddSubmenu.Clear();
		_contextQuickActionsSubmenu.Clear();

		bool isSystem = metadata.StartsWith("system::");
		bool isFolder = metadata.StartsWith("folder::");
		bool isScript = metadata.StartsWith("script::");
		bool isScene = metadata.StartsWith("sceneLink::");
		bool canShowNewAndAdd = !_isFilteringScripts;
		bool canShowQuickActions = EnableQuickActions && (isScript || isSystem || isFolder);
		bool useReversedSubmenuIcons = ShouldUseReversedContextSubmenuIcons();

		UpdateContextSubmenuDirectionIcons(useReversedSubmenuIcons);

		if (canShowNewAndAdd)
		{
			AddContextSubmenuItem("New", _contextNewSubmenu, useReversedSubmenuIcons);
			AddContextSubmenuIconItem(
				_contextNewSubmenu,
				"Script",
				ContextNewScript,
				_contextNewScriptIcon
			);
			AddContextSubmenuIconItem(
				_contextNewSubmenu,
				"Folder",
				ContextAddFolder,
				_contextFolderIcon
			);
			AddContextSubmenuItem("Add", _contextAddSubmenu, useReversedSubmenuIcons);
			AddContextSubmenuIconItem(
				_contextAddSubmenu,
				"Scripts",
				ContextAddScript,
				_contextAddScriptIcon
			);
			AddContextSubmenuIconItem(_contextAddSubmenu, "Scenes", ContextAddScene, _sceneIcon);
		}

		if (canShowQuickActions)
		{
			AddContextSubmenuItem(
				"Quick Actions",
				_contextQuickActionsSubmenu,
				useReversedSubmenuIcons,
				GetContextQuickActionsSubmenuItemIcon(useReversedSubmenuIcons)
			);

			if (isScript)
			{
				AddContextSubmenuIconItem(
					_contextQuickActionsSubmenu,
					"Beautify Script",
					ContextBeautifyScript,
					_contextBeautifyScriptIcon
				);
				AddContextSubmenuIconItem(
					_contextQuickActionsSubmenu,
					"Refactor Namespace",
					ContextRefactorNamespace,
					_contextRefactorNamespaceIcon
				);
			}
			else if (isSystem || isFolder)
			{
				AddContextSubmenuIconItem(
					_contextQuickActionsSubmenu,
					"Beautify Scripts",
					ContextBeautifyScripts,
					_contextBeautifyScriptIcon
				);
				AddContextSubmenuIconItem(
					_contextQuickActionsSubmenu,
					"Refactor Namespace",
					ContextRefactorNamespace,
					_contextRefactorNamespaceIcon
				);
			}
		}

		if (isScript)
		{
			if (canShowNewAndAdd || canShowQuickActions)
				_contextMenu.AddSeparator();

			string entry = GetEntryFromMetadata(metadata);

			if (string.IsNullOrWhiteSpace(GetLinkedScenePathFromEntry(entry)))
			{
				AddContextMenuIconItem("Link to Scene", ContextLinkScene, _contextLinkSceneIcon);
			}
			else
			{
				AddContextMenuIconItem(
					"Unlink from Scene",
					ContextUnlinkScene,
					_contextUnlinkSceneIcon
				);
			}
		}
		if (!(_isFilteringScripts && isScene))
		{
			_contextMenu.AddSeparator();
		}
		AddContextMenuIconItem("Rename", ContextRename, _contextRenameIcon);
		AddContextMenuIconItem("Remove", ContextRemove, _contextRemoveIcon);

		if (isScript || isScene)
		{
			_contextMenu.AddSeparator();
			AddContextMenuIconItem(
				"Open File Path",
				ContextShowInFileManager,
				_contextShowInFileSystemIcon
			);
		}
	}

	private void AddContextSubmenuItem(string label, PopupMenu submenu, bool useReversedIcons)
	{
		AddContextSubmenuItem(
			label,
			submenu,
			useReversedIcons,
			GetContextSubmenuItemIcon(useReversedIcons)
		);
	}

	private void AddContextSubmenuItem(
		string label,
		PopupMenu submenu,
		bool useReversedIcons,
		Texture2D icon
	)
	{
		_contextMenu.AddSubmenuNodeItem(label, submenu);

		int index = _contextMenu.ItemCount - 1;
		bool shouldShowSubmenuItemIcon =
			EnableContextMenuIcons || ShouldForceReversedContextSubmenuDirectionIcon(useReversedIcons);

		if (shouldShowSubmenuItemIcon && icon != null)
			_contextMenu.SetItemIcon(index, icon);
	}

	private Texture2D GetContextSubmenuItemIcon(bool useReversedIcons)
	{
		if (useReversedIcons && _contextCategoryArrowLeftIcon != null)
			return _contextCategoryArrowLeftIcon;

		return _contextCategoryAddIcon;
	}

	private Texture2D GetContextQuickActionsSubmenuItemIcon(bool useReversedIcons)
	{
		if (useReversedIcons && _contextCategoryArrowLeftIcon != null)
			return _contextCategoryArrowLeftIcon;

		return _contextQuickActionsIcon;
	}

	private Texture2D GetHiddenContextSubmenuIcon()
	{
		if (_contextHiddenSubmenuIcon != null)
			return _contextHiddenSubmenuIcon;

		Image image = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
		image.SetPixel(0, 0, new Color(1, 1, 1, 0));
		_contextHiddenSubmenuIcon = ImageTexture.CreateFromImage(image);

		return _contextHiddenSubmenuIcon;
	}

	private Texture2D GetContextSubmenuDirectionIcon(bool hasCreationSubmenus)
	{
		if (hasCreationSubmenus && _contextCategoryAddIcon != null)
			return _contextCategoryAddIcon;

		return _contextQuickActionsIcon;
	}

	private void UpdateContextSubmenuDirectionIcons(bool useReversedIcons)
	{
		_contextMenu.RemoveThemeIconOverride("submenu");
		_contextMenu.RemoveThemeIconOverride("submenu_mirrored");

		if (!ShouldForceReversedContextSubmenuDirectionIcon(useReversedIcons))
			return;

		Texture2D hiddenSubmenuIcon = GetHiddenContextSubmenuIcon();

		if (hiddenSubmenuIcon == null)
			return;

		_contextMenu.AddThemeIconOverride("submenu", hiddenSubmenuIcon);
		_contextMenu.AddThemeIconOverride("submenu_mirrored", hiddenSubmenuIcon);
	}

	private bool ShouldForceReversedContextSubmenuDirectionIcon(bool useReversedIcons)
	{
		return useReversedIcons && _contextCategoryArrowLeftIcon != null;
	}

	private bool ShouldUseReversedContextSubmenuIcons()
	{
		if (!IsDockOnRightSide())
			return false;

		return !HasEnoughRoomForContextSubmenuToOpenRight();
	}

	private bool HasEnoughRoomForContextSubmenuToOpenRight()
	{
		Control baseControl = EditorInterface.Singleton?.GetBaseControl();

		if (
			_dock == null
			|| baseControl == null
			|| !_dock.IsInsideTree()
			|| !baseControl.IsInsideTree()
		)
			return false;

		Rect2 editorRect = baseControl.GetGlobalRect();

		if (editorRect.Size.X <= 0.0f)
			return false;

		// PopupMenu does not expose the final submenu opening direction before it is shown.
		// Estimate the space required by the main context menu plus one submenu so the
		// right-dock icon layout only reverses when Godot is likely to open the submenu left.
		const float EstimatedMainMenuWidth = 180.0f;
		const float EstimatedSubmenuWidth = 170.0f;
		const float SafetyPadding = 16.0f;

		float mouseX = _dock.GetGlobalMousePosition().X;
		float requiredRightEdge =
			mouseX + EstimatedMainMenuWidth + EstimatedSubmenuWidth + SafetyPadding;
		float editorRightEdge = editorRect.Position.X + editorRect.Size.X;

		return requiredRightEdge <= editorRightEdge;
	}

	private bool IsDockOnRightSide()
	{
		Control baseControl = EditorInterface.Singleton?.GetBaseControl();

		if (
			_dock == null
			|| baseControl == null
			|| !_dock.IsInsideTree()
			|| !baseControl.IsInsideTree()
		)
			return false;

		Rect2 dockRect = _dock.GetGlobalRect();
		Rect2 editorRect = baseControl.GetGlobalRect();

		if (dockRect.Size.X <= 0.0f || editorRect.Size.X <= 0.0f)
			return false;

		return dockRect.GetCenter().X > editorRect.GetCenter().X;
	}

	private void AddContextSubmenuIconItem(PopupMenu submenu, string label, int id, Texture2D icon)
	{
		if (!EnableContextMenuIcons || icon == null)
		{
			submenu.AddItem(label, id);
			return;
		}

		submenu.AddIconItem(icon, label, id);
	}

	private void SetContextMenuItemDisabled(int id, bool disabled)
	{
		int index = _contextMenu.GetItemIndex(id);

		if (index < 0)
			return;

		_contextMenu.SetItemDisabled(index, disabled);
	}

	private void OnContextMenuIdPressed(long id)
	{
		switch (id)
		{
			case ContextAddFolder:
				OpenAddFolderDialog();
				break;

			case ContextAddScript:
				OnAddScriptPressed();
				break;

			case ContextAddScene:
				OnAddScenePressed();
				break;

			case ContextNewScript:
				_createScriptDialog.CurrentFile = "";
				_createScriptDialog.PopupCenteredRatio(0.8f);
				break;

			case ContextRename:
				OpenRenameDialog();
				break;

			case ContextRemove:
				OpenRemoveDialog();
				break;

			case ContextLinkScene:
				OpenLinkSceneDialog();
				break;

			case ContextUnlinkScene:
				UnlinkSceneFromPendingScript();
				break;

			case ContextShowInFileManager:
				ShowPendingScriptInFileManager();
				break;

			case ContextRefactorNamespace:
				OpenRefactorNamespaceDialog();
				break;

			case ContextBeautifyScript:
				OpenBeautifyScriptCSharpierCheckDialog();
				break;

			case ContextBeautifyScripts:
				OpenBeautifyScriptsCSharpierCheckDialog();
				break;
		}
	}

	private void ShowPendingScriptInFileManager()
	{
		if (string.IsNullOrWhiteSpace(_pendingShowInFileManagerMetadata))
			return;

		string path = "";
		string missingEntry = "";

		if (_pendingShowInFileManagerMetadata.StartsWith("script::"))
		{
			string entry = GetEntryFromMetadata(_pendingShowInFileManagerMetadata);
			path = GetScriptPathFromEntry(entry);
			missingEntry = entry;
		}
		else if (_pendingShowInFileManagerMetadata.StartsWith("sceneLink::"))
		{
			string entry = _pendingShowInFileManagerMetadata.Substring("sceneLink::".Length);
			path = GetScenePathFromEntry(entry);
			missingEntry = entry;
		}
		else
		{
			return;
		}

		if (!FileAccess.FileExists(path))
		{
			if (_pendingShowInFileManagerMetadata.StartsWith("sceneLink::"))
				OpenMissingSceneDialog(missingEntry, path);
			else
				OpenMissingScriptDialog(missingEntry, path);

			return;
		}

		string globalPath = ProjectSettings.GlobalizePath(path);

		if (string.IsNullOrWhiteSpace(globalPath))
		{
			GD.PushWarning($"Could not resolve file path: {path}");
			return;
		}

		OS.ShellShowInFileManager(globalPath, false);
	}
	#endregion
}
#endif
