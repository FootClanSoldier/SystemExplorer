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
		_pendingScriptRenameTreeState = metadata.StartsWith("script::")
			? CaptureScriptRenameTreeState(GetEntryFromMetadata(metadata))
			: null;
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
		BuildContextMenuForMetadata(metadata, useReversedSubmenuIcons: false);

		if (ShouldUseReversedContextSubmenuIcons())
			BuildContextMenuForMetadata(metadata, useReversedSubmenuIcons: true);
	}

	private void BuildContextMenuForMetadata(string metadata, bool useReversedSubmenuIcons)
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

		bool canShowFileManagerAction = isScript || isScene || HasFolderFileManagerTarget(metadata);

		if (canShowFileManagerAction)
		{
			_contextMenu.AddSeparator();
			AddContextMenuIconItem(
				isFolder ? "Open Folder Path" : "Open File Path",
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
			EnableContextMenuIcons
			|| ShouldForceReversedContextSubmenuDirectionIcon(useReversedIcons);

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
		// Measure the populated menu and use the regular New/Add submenus as the direction
		// reference. Quick Actions is intentionally ignored because it is substantially
		// wider and would otherwise make every submenu icon reverse too early. Godot can
		// still choose the final opening direction for Quick Actions when it is displayed.
		float edgeTolerance = GetContextMenuEdgeTolerance();

		float mainMenuWidth = GetRequiredPopupWidth(_contextMenu);
		float referenceSubmenuWidth = Mathf.Max(
			GetRequiredPopupWidth(_contextNewSubmenu),
			GetRequiredPopupWidth(_contextAddSubmenu)
		);

		float editorLeftEdge = editorRect.Position.X;
		float editorRightEdge = editorRect.End.X;
		float mouseX = _dock.GetGlobalMousePosition().X;
		float mainMenuLeft = Mathf.Clamp(
			mouseX,
			editorLeftEdge,
			Mathf.Max(editorLeftEdge, editorRightEdge - mainMenuWidth)
		);
		float requiredRightEdge = mainMenuLeft + mainMenuWidth + referenceSubmenuWidth;

		return requiredRightEdge <= editorRightEdge + edgeTolerance;
	}

	private static float GetContextMenuEdgeTolerance()
	{
		const float BaseTolerance = 6.0f;

		float editorScale = EditorInterface.Singleton?.GetEditorScale() ?? 1.0f;

		if (editorScale <= 0.0f)
			editorScale = 1.0f;

		return BaseTolerance * editorScale;
	}

	private static float GetRequiredPopupWidth(PopupMenu menu)
	{
		if (menu == null || !GodotObject.IsInstanceValid(menu))
			return 0.0f;

		menu.ChildControlsChanged();

		float contentWidth = menu.GetContentsMinimumSize().X;
		float configuredMinimumWidth = menu.MinSize.X;

		return Mathf.Max(contentWidth, configuredMinimumWidth);
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
				ShowPendingItemInFileManager();
				break;

			case ContextRefactorNamespace:
				_namespaceRefactorHost.Open(_pendingRenameMetadata);
				break;

			case ContextBeautifyScript:
				OpenBeautifyScriptCSharpierCheckDialog();
				break;

			case ContextBeautifyScripts:
				OpenBeautifyScriptsCSharpierCheckDialog();
				break;
		}
	}

	#endregion
}
#endif
