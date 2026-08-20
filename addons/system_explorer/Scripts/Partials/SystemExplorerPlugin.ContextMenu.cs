#if TOOLS
using Godot;
using System;

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
	private const int ContextBindFolder = 12;
	private const int ContextUnbindFolder = 13;
	private const string BeautifyUnavailableTooltip = "Beautify is busy.";
	private const string RefactorNamespaceBeautifyRunningTooltip =
		"Beautify is running.";
	private const string QuickActionsNoScriptsTooltip = "No scripts found";
	private const string ContextMenuOpenOperationLabel = "System Explorer context menu open";

	private Texture2D _contextHiddenSubmenuIcon;
	private bool _pendingQuickActionsNoScriptsFound;
	private long _contextMenuOpenOperationToken;
	private bool _contextMenuOpenRequestPending;
	private bool _contextMenuOpenWaitingForNextProcessFrame;
	private string _contextMenuOpenScheduledManagedAssemblyGeneration = "";
	private string _contextMenuOpenMetadata = "";
	private bool _contextMenuOpenFilteringScripts;
	private Vector2I _contextMenuOpenPopupScreenPosition;
	private float _contextMenuOpenDockGlobalMouseX;
	private long _contextMenuOpenSupersededCount;

	private readonly record struct ContextMenuOpenRequestSnapshot(
		long OperationToken,
		string ScheduledManagedAssemblyGeneration,
		string Metadata,
		bool FilteringScripts,
		Vector2I PopupScreenPosition,
		float DockGlobalMouseX,
		long SupersededCount
	);
	#endregion

	#region Context Menu
	private void OpenContextMenuForTreeItem(TreeItem item)
	{
		if (item == null || !GodotObject.IsInstanceValid(item))
			return;

		if (IsAnyContextMenuHierarchyVisible())
		{
			LogContextMenuOpenCaptureRejected("ContextMenuHierarchyVisible");
			return;
		}

		string metadata = item.GetMetadata(0).AsString();

		_pendingQuickActionsNoScriptsFound = false;

		if (CanShowQuickActionsForMetadata(metadata))
		{
			bool isScript = metadata.StartsWith("script::", StringComparison.Ordinal);

			if (!isScript)
			{
				bool isBatchQuickActionsTarget =
					metadata.StartsWith("system::", StringComparison.Ordinal)
					|| metadata.StartsWith("folder::", StringComparison.Ordinal);

				if (isBatchQuickActionsTarget)
				{
					_pendingQuickActionsNoScriptsFound =
						!TreeItemSubtreeContainsScript(item);
				}
			}
		}

		_pendingRemoveMetadata = metadata;
		CapturePendingRemoveTreeSelectionState(item);
		CapturePendingRemoveScriptOccurrence(item);
		_pendingRenameMetadata = metadata;
		CapturePendingNonScriptRenameTreeSelectionState(item, metadata);
		_pendingScriptRenameTreeState = metadata.StartsWith("script::")
			? CaptureScriptRenameTreeState(GetEntryFromMetadata(metadata))
			: null;
		_pendingAddFolderMetadata = TryResolveAddFolderTargetMetadata(
			item,
			out string addFolderTargetMetadata
		)
			? addFolderTargetMetadata
			: "";
		_pendingShowInFileManagerMetadata = metadata;
		_pendingBeautifyScriptMetadata = metadata;
		_pendingFolderBindingMetadata = metadata.StartsWith("folder::") ? metadata : "";

		QueueContextMenuOpenRequest(
			metadata,
			_isFilteringScripts,
			DisplayServer.MouseGetPosition(),
			_dock != null && GodotObject.IsInstanceValid(_dock)
				? _dock.GetGlobalMousePosition().X
				: 0.0f
		);
	}

	private bool IsAnyContextMenuHierarchyVisible()
	{
		try
		{
			return IsContextPopupVisible(_contextMenu)
				|| IsContextPopupVisible(_contextNewSubmenu)
				|| IsContextPopupVisible(_contextAddSubmenu)
				|| IsContextPopupVisible(_contextQuickActionsSubmenu);
		}
		catch
		{
			// If visibility cannot be proven safely, do not mutate a possibly published menu.
			return true;
		}
	}

	private static bool IsContextPopupVisible(PopupMenu menu)
	{
		return menu != null && GodotObject.IsInstanceValid(menu) && menu.Visible;
	}

	private void QueueContextMenuOpenRequest(
		string metadata,
		bool filteringScripts,
		Vector2I popupScreenPosition,
		float dockGlobalMouseX
	)
	{
		bool supersedesCurrentRequest = _contextMenuOpenRequestPending;
		long supersededOperationToken = _contextMenuOpenOperationToken;
		string supersededMetadata = _contextMenuOpenMetadata;
		long supersededCount = _contextMenuOpenSupersededCount;

		long operationToken = AdvanceContextMenuOpenOperationToken();
		string scheduledManagedAssemblyGeneration = ManagedAssemblyGeneration;
		long currentSupersededCount = supersedesCurrentRequest
			? AdvanceContextMenuSupersededCount(supersededCount)
			: 0;

		_contextMenuOpenRequestPending = true;
		_contextMenuOpenWaitingForNextProcessFrame = true;
		_contextMenuOpenScheduledManagedAssemblyGeneration = scheduledManagedAssemblyGeneration;
		_contextMenuOpenMetadata = metadata ?? "";
		_contextMenuOpenFilteringScripts = filteringScripts;
		_contextMenuOpenPopupScreenPosition = popupScreenPosition;
		_contextMenuOpenDockGlobalMouseX = dockGlobalMouseX;
		_contextMenuOpenSupersededCount = currentSupersededCount;

		if (supersedesCurrentRequest)
		{
			DebugLogger.LogPersistentFileOnlyOperation(
				"System Explorer context menu open request superseded",
				$"SupersededOperationToken='{supersededOperationToken}', SupersededMetadata='{supersededMetadata}', CurrentOperationToken='{operationToken}', CurrentMetadata='{_contextMenuOpenMetadata}', FilterMode='{filteringScripts}', PopupPosition='{popupScreenPosition}', DockGlobalMouseX='{dockGlobalMouseX}', SupersededCount='{currentSupersededCount}', ManagedAssemblyGeneration='{scheduledManagedAssemblyGeneration}'"
			);
		}

		DebugLogger.LogPersistentFileOnlyOperation(
			"System Explorer context menu open request admitted",
			$"OperationToken='{operationToken}', ManagedAssemblyGeneration='{scheduledManagedAssemblyGeneration}', Metadata='{_contextMenuOpenMetadata}', FilterMode='{filteringScripts}', PopupPosition='{popupScreenPosition}', DockGlobalMouseX='{dockGlobalMouseX}', SupersededCount='{currentSupersededCount}'"
		);

		RefreshEditorPluginProcessingState();
	}

	private static long AdvanceContextMenuSupersededCount(long currentCount)
	{
		unchecked
		{
			currentCount++;
			if (currentCount <= 0)
				currentCount = 1;
		}

		return currentCount;
	}

	private bool HasPendingContextMenuOpenProcessWork() =>
		_contextMenuOpenRequestPending;

	private void ProcessPendingContextMenuOpen()
	{
		if (!_contextMenuOpenRequestPending)
			return;

		if (_contextMenuOpenWaitingForNextProcessFrame)
		{
			_contextMenuOpenWaitingForNextProcessFrame = false;
			ContextMenuOpenRequestSnapshot anchoredRequest =
				CaptureCurrentContextMenuOpenRequestSnapshot();
			LogContextMenuOpenBoundary("ProcessFrameAnchor", anchoredRequest);
			return;
		}

		ContextMenuOpenRequestSnapshot request =
			CaptureCurrentContextMenuOpenRequestSnapshot();
		string rejectionReason = GetContextMenuOpenRequestRejectionReason(request);

		if (!string.IsNullOrEmpty(rejectionReason))
		{
			RejectCurrentContextMenuOpenRequest(rejectionReason, request);
			return;
		}

		try
		{
			LogContextMenuOpenBoundary("BuildBegin", request);
			BuildContextMenuForMetadata(request.Metadata, request.DockGlobalMouseX);
			LogContextMenuOpenBoundary("BuildReturned", request);
		}
		catch (Exception exception)
		{
			LogContextMenuOpenFailure("Build", request, exception);
			ConsumeCurrentContextMenuOpenRequest(request.OperationToken);
			throw;
		}

		try
		{
			_contextMenu.Position = request.PopupScreenPosition;
			LogContextMenuOpenBoundary("PopupBegin", request);
			_contextMenu.Popup();
			LogContextMenuOpenBoundary("PopupReturned", request);
		}
		catch (Exception exception)
		{
			LogContextMenuOpenFailure("Popup", request, exception);
			ConsumeCurrentContextMenuOpenRequest(request.OperationToken);
			throw;
		}

		ConsumeCurrentContextMenuOpenRequest(request.OperationToken);
	}

	private ContextMenuOpenRequestSnapshot CaptureCurrentContextMenuOpenRequestSnapshot()
	{
		return new ContextMenuOpenRequestSnapshot(
			_contextMenuOpenOperationToken,
			_contextMenuOpenScheduledManagedAssemblyGeneration,
			_contextMenuOpenMetadata,
			_contextMenuOpenFilteringScripts,
			_contextMenuOpenPopupScreenPosition,
			_contextMenuOpenDockGlobalMouseX,
			_contextMenuOpenSupersededCount
		);
	}

	private string GetContextMenuOpenRequestRejectionReason(
		ContextMenuOpenRequestSnapshot request
	)
	{
		if (
			!string.Equals(
				request.ScheduledManagedAssemblyGeneration,
				ManagedAssemblyGeneration,
				StringComparison.Ordinal
			)
		)
		{
			return "ManagedAssemblyGenerationChanged";
		}

		if (request.OperationToken <= 0)
			return "InvalidOperationToken";

		if (request.OperationToken != _contextMenuOpenOperationToken)
			return "StaleOperationToken";

		if (!_contextMenuOpenRequestPending)
			return "RequestNoLongerPending";

		if (
			!string.Equals(
				request.Metadata,
				_contextMenuOpenMetadata,
				StringComparison.Ordinal
			)
		)
		{
			return "MetadataAuthorityMismatch";
		}

		if (request.FilteringScripts != _contextMenuOpenFilteringScripts)
			return "FilterAuthorityMismatch";

		if (request.FilteringScripts != _isFilteringScripts)
			return "FilterModeChanged";

		if (!IsValidGodotObject(this))
			return "PluginInstanceInvalid";

		if (!IsInsideTree())
			return "PluginOutsideTree";

		if (!IsValidContextMenuOpenControl(_dock))
			return "DockUnavailable";

		if (!IsValidContextMenuOpenControl(_tree))
			return "TreeUnavailable";

		if (!IsValidContextMenuOpenPopup(_contextMenu))
			return "ContextMenuUnavailable";

		if (!IsValidContextMenuOpenPopup(_contextNewSubmenu))
			return "ContextNewSubmenuUnavailable";

		if (!IsValidContextMenuOpenPopup(_contextAddSubmenu))
			return "ContextAddSubmenuUnavailable";

		if (!IsValidContextMenuOpenPopup(_contextQuickActionsSubmenu))
			return "ContextQuickActionsSubmenuUnavailable";

		TreeItem selectedItem = _tree.GetSelected();
		if (selectedItem == null || !GodotObject.IsInstanceValid(selectedItem))
			return "SelectionUnavailable";

		string selectedMetadata = selectedItem.GetMetadata(0).AsString();
		if (!string.Equals(selectedMetadata, request.Metadata, StringComparison.Ordinal))
			return "SelectionMetadataChanged";

		if (IsAnyContextMenuHierarchyVisible())
			return "ContextMenuHierarchyVisibleBeforeBuild";

		return "";
	}

	private static bool IsValidContextMenuOpenControl(Control control)
	{
		return control != null
			&& GodotObject.IsInstanceValid(control)
			&& control.IsInsideTree();
	}

	private static bool IsValidContextMenuOpenPopup(PopupMenu menu)
	{
		return menu != null
			&& GodotObject.IsInstanceValid(menu)
			&& menu.IsInsideTree();
	}

	private void RejectCurrentContextMenuOpenRequest(
		string reason,
		ContextMenuOpenRequestSnapshot request
	)
	{
		LogContextMenuOpenRequestRejected(reason, request);
		ConsumeCurrentContextMenuOpenRequest(request.OperationToken);
	}

	private void LogContextMenuOpenCaptureRejected(string reason)
	{
		DebugLogger.LogPersistentFileOnlyOperation(
			"System Explorer context menu open request rejected",
			$"Reason='{reason}', Stage='Capture', CurrentOperationToken='{_contextMenuOpenOperationToken}', CurrentRequestPending='{_contextMenuOpenRequestPending}', CurrentMetadata='{_contextMenuOpenMetadata}', CurrentManagedAssemblyGeneration='{ManagedAssemblyGeneration}'"
		);
	}

	private void LogContextMenuOpenRequestRejected(
		string reason,
		ContextMenuOpenRequestSnapshot request
	)
	{
		DebugLogger.LogPersistentFileOnlyOperation(
			"System Explorer context menu open request rejected",
			$"Reason='{reason}', Stage='Process', OperationToken='{request.OperationToken}', CurrentOperationToken='{_contextMenuOpenOperationToken}', ScheduledManagedAssemblyGeneration='{request.ScheduledManagedAssemblyGeneration ?? ""}', CurrentManagedAssemblyGeneration='{ManagedAssemblyGeneration}', Metadata='{request.Metadata ?? ""}', CurrentMetadata='{_contextMenuOpenMetadata}', FilterMode='{request.FilteringScripts}', CurrentFilterMode='{_isFilteringScripts}', PopupPosition='{request.PopupScreenPosition}', DockGlobalMouseX='{request.DockGlobalMouseX}', SupersededCount='{request.SupersededCount}', CurrentRequestPending='{_contextMenuOpenRequestPending}'"
		);
	}

	private void LogContextMenuOpenBoundary(
		string phase,
		ContextMenuOpenRequestSnapshot request
	)
	{
		DebugLogger.LogPersistentFileOnlyOperation(
			ContextMenuOpenOperationLabel,
			$"Phase='{phase}', OperationToken='{request.OperationToken}', ScheduledManagedAssemblyGeneration='{request.ScheduledManagedAssemblyGeneration}', CurrentManagedAssemblyGeneration='{ManagedAssemblyGeneration}', Metadata='{request.Metadata}', FilterMode='{request.FilteringScripts}', PopupPosition='{request.PopupScreenPosition}', DockGlobalMouseX='{request.DockGlobalMouseX}', SupersededCount='{request.SupersededCount}'"
		);
	}

	private void LogContextMenuOpenFailure(
		string failurePhase,
		ContextMenuOpenRequestSnapshot request,
		Exception exception
	)
	{
		DebugLogger.LogPersistentFileOnlyOperation(
			ContextMenuOpenOperationLabel,
			$"Phase='Failed', FailurePhase='{failurePhase}', OperationToken='{request.OperationToken}', ScheduledManagedAssemblyGeneration='{request.ScheduledManagedAssemblyGeneration}', CurrentManagedAssemblyGeneration='{ManagedAssemblyGeneration}', Metadata='{request.Metadata}', FilterMode='{request.FilteringScripts}', PopupPosition='{request.PopupScreenPosition}', DockGlobalMouseX='{request.DockGlobalMouseX}', SupersededCount='{request.SupersededCount}', ExceptionType='{exception.GetType().FullName}', ExceptionMessage='{exception.Message}'"
		);
	}

	private long AdvanceContextMenuOpenOperationToken()
	{
		unchecked
		{
			_contextMenuOpenOperationToken++;
			if (_contextMenuOpenOperationToken <= 0)
				_contextMenuOpenOperationToken = 1;
		}

		return _contextMenuOpenOperationToken;
	}

	private void ConsumeCurrentContextMenuOpenRequest(long operationToken)
	{
		if (operationToken != _contextMenuOpenOperationToken)
			return;

		_contextMenuOpenRequestPending = false;
		_contextMenuOpenWaitingForNextProcessFrame = false;
		_contextMenuOpenScheduledManagedAssemblyGeneration = "";
		_contextMenuOpenMetadata = "";
		_contextMenuOpenFilteringScripts = false;
		_contextMenuOpenPopupScreenPosition = default;
		_contextMenuOpenDockGlobalMouseX = 0.0f;
		_contextMenuOpenSupersededCount = 0;
	}

	private void InvalidateContextMenuOpenRequest(string reason)
	{
		ContextMenuOpenRequestSnapshot invalidatedRequest =
			CaptureCurrentContextMenuOpenRequestSnapshot();
		bool hadCurrentRequest = _contextMenuOpenRequestPending;

		_contextMenuOpenRequestPending = false;
		_contextMenuOpenWaitingForNextProcessFrame = false;
		_contextMenuOpenScheduledManagedAssemblyGeneration = "";
		_contextMenuOpenMetadata = "";
		_contextMenuOpenFilteringScripts = false;
		_contextMenuOpenPopupScreenPosition = default;
		_contextMenuOpenDockGlobalMouseX = 0.0f;
		_contextMenuOpenSupersededCount = 0;
		long currentOperationToken = AdvanceContextMenuOpenOperationToken();

		if (!hadCurrentRequest)
			return;

		DebugLogger.LogPersistentFileOnlyOperation(
			"System Explorer context menu open request invalidated",
			$"Reason='{reason ?? ""}', InvalidatedOperationToken='{invalidatedRequest.OperationToken}', CurrentOperationToken='{currentOperationToken}', ScheduledManagedAssemblyGeneration='{invalidatedRequest.ScheduledManagedAssemblyGeneration}', CurrentManagedAssemblyGeneration='{ManagedAssemblyGeneration}', Metadata='{invalidatedRequest.Metadata}', FilterMode='{invalidatedRequest.FilteringScripts}', PopupPosition='{invalidatedRequest.PopupScreenPosition}', DockGlobalMouseX='{invalidatedRequest.DockGlobalMouseX}', SupersededCount='{invalidatedRequest.SupersededCount}'"
		);
	}

	private bool CanShowQuickActionsForMetadata(string metadata)
	{
		if (!EnableQuickActions || string.IsNullOrWhiteSpace(metadata))
			return false;

		return metadata.StartsWith("script::", StringComparison.Ordinal)
			|| metadata.StartsWith("system::", StringComparison.Ordinal)
			|| metadata.StartsWith("folder::", StringComparison.Ordinal);
	}

	private static bool TreeItemSubtreeContainsScript(TreeItem parent)
	{
		if (parent == null)
			return false;

		TreeItem child = parent.GetFirstChild();

		while (child != null)
		{
			if (IsScriptTreeItem(child))
				return true;

			if (TreeItemSubtreeContainsScript(child))
				return true;

			child = child.GetNext();
		}

		return false;
	}

	private static bool IsScriptTreeItem(TreeItem item)
	{
		if (item == null)
			return false;

		string metadata = item.GetMetadata(0).AsString();

		return metadata.StartsWith("script::", StringComparison.Ordinal);
	}

	private void BuildContextMenuForMetadata(string metadata, float dockGlobalMouseX)
	{
		BuildContextMenuForMetadata(metadata, useReversedSubmenuIcons: false);

		if (ShouldUseReversedContextSubmenuIcons(dockGlobalMouseX))
			BuildContextMenuForMetadata(metadata, useReversedSubmenuIcons: true);

		if (CanShowQuickActionsForMetadata(metadata))
			UpdateQuickActionsContextMenuAvailability();
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
		bool canShowQuickActions = CanShowQuickActionsForMetadata(metadata);

		UpdateContextSubmenuDirectionIcons(useReversedSubmenuIcons);

		if (canShowNewAndAdd)
		{
			AddContextSubmenuItem("New", _contextNewSubmenu, useReversedSubmenuIcons);
			AddContextSubmenuIconItem(
				_contextNewSubmenu,
				"Script",
				ContextNewScript,
				_contextNewScriptIcon,
				NewScriptEditorShortcutPath
			);
			AddContextSubmenuIconItem(
				_contextNewSubmenu,
				"Folder",
				ContextAddFolder,
				_contextFolderIcon,
				NewFolderEditorShortcutPath
			);
			AddContextSubmenuItem("Add", _contextAddSubmenu, useReversedSubmenuIcons);
			AddContextSubmenuIconItem(
				_contextAddSubmenu,
				"Scripts",
				ContextAddScript,
				_contextAddScriptIcon,
				AddExistingScriptsEditorShortcutPath
			);
			AddContextSubmenuIconItem(
				_contextAddSubmenu,
				"Scenes",
				ContextAddScene,
				_sceneIcon,
				AddExistingScenesEditorShortcutPath
			);
		}

		if (canShowQuickActions)
		{
			AddContextSubmenuItem(
				"Quick Actions",
				_contextQuickActionsSubmenu,
				useReversedSubmenuIcons,
				GetContextQuickActionsSubmenuItemIcon(useReversedSubmenuIcons)
			);

			int beautifyContextId = isScript
				? ContextBeautifyScript
				: ContextBeautifyScripts;

			AddContextSubmenuIconItem(
				_contextQuickActionsSubmenu,
				"Beautify",
				beautifyContextId,
				_contextBeautifyScriptIcon,
				BeautifyEditorShortcutPath
			);
			AddContextSubmenuIconItem(
				_contextQuickActionsSubmenu,
				"Refactor Namespace",
				ContextRefactorNamespace,
				_contextRefactorNamespaceIcon,
				RefactorNamespaceEditorShortcutPath
			);
		}

		if (isFolder)
		{
			if (canShowNewAndAdd || canShowQuickActions)
				_contextMenu.AddSeparator();

			if (TryGetFolderBindingFromMetadata(metadata, out _))
			{
				AddContextMenuIconItem(
					"Unbind Folder",
					ContextUnbindFolder,
					_contextUnlinkSceneIcon
				);
			}
			else
			{
				AddContextMenuIconItem(
					"Bind To Folder",
					ContextBindFolder,
					_contextFolderIcon
				);
			}

			_contextMenu.AddSeparator();
		}
		else
		{
			if (isScript)
			{
				if (canShowNewAndAdd || canShowQuickActions)
					_contextMenu.AddSeparator();

				string entry = GetEntryFromMetadata(metadata);

				if (string.IsNullOrWhiteSpace(GetLinkedScenePathFromEntry(entry)))
				{
					AddContextMenuIconItem(
						"Link to Scene",
						ContextLinkScene,
						_contextLinkSceneIcon
					);
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
				_contextMenu.AddSeparator();
		}

		AddContextMenuIconItem(
			"Rename",
			ContextRename,
			_contextRenameIcon
		);
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

		if (icon != null)
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

	private bool ShouldUseReversedContextSubmenuIcons(float dockGlobalMouseX)
	{
		if (!IsDockOnRightSide())
			return false;

		return !HasEnoughRoomForContextSubmenuToOpenRight(dockGlobalMouseX);
	}

	private bool HasEnoughRoomForContextSubmenuToOpenRight(float dockGlobalMouseX)
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
		float mouseX = dockGlobalMouseX;
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

	private void AddContextSubmenuIconItem(
		PopupMenu submenu,
		string label,
		int id,
		Texture2D icon,
		string editorShortcutPath = ""
	)
	{
		AddContextPopupMenuItem(
			submenu,
			label,
			id,
			icon,
			editorShortcutPath
		);
	}

	private void SetContextMenuItemDisabled(int id, bool disabled)
	{
		int index = _contextMenu.GetItemIndex(id);

		if (index < 0)
			return;

		_contextMenu.SetItemDisabled(index, disabled);
	}

	private void UpdateQuickActionsContextMenuAvailability()
	{
		if (!CanShowQuickActionsForMetadata(_pendingBeautifyScriptMetadata))
			return;

		UpdateBeautifyContextMenuAvailability(_pendingQuickActionsNoScriptsFound);
		UpdateRefactorNamespaceContextMenuAvailability(
			_pendingQuickActionsNoScriptsFound
		);
	}

	private bool IsQuickActionsContextMenuHierarchyVisible()
	{
		try
		{
			bool mainMenuVisible = _contextMenu != null
				&& GodotObject.IsInstanceValid(_contextMenu)
				&& _contextMenu.Visible;
			bool quickActionsSubmenuVisible = _contextQuickActionsSubmenu != null
				&& GodotObject.IsInstanceValid(_contextQuickActionsSubmenu)
				&& _contextQuickActionsSubmenu.Visible;

			return mainMenuVisible || quickActionsSubmenuVisible;
		}
		catch
		{
			return false;
		}
	}

	private void UpdateBeautifyContextMenuAvailability(bool noScriptsFound)
	{
		bool disabled = noScriptsFound || _isBeautifyingScript;
		string tooltip = noScriptsFound
			? QuickActionsNoScriptsTooltip
			: _isBeautifyingScript
				? BeautifyUnavailableTooltip
				: string.Empty;

		UpdateQuickActionContextMenuItemAvailability(
			ContextBeautifyScript,
			disabled,
			tooltip
		);
		UpdateQuickActionContextMenuItemAvailability(
			ContextBeautifyScripts,
			disabled,
			tooltip
		);
	}

	private void UpdateQuickActionContextMenuItemAvailability(
		int id,
		bool disabled,
		string tooltip
	)
	{
		if (
			_contextQuickActionsSubmenu == null
			|| !GodotObject.IsInstanceValid(_contextQuickActionsSubmenu)
		)
		{
			return;
		}

		int index = _contextQuickActionsSubmenu.GetItemIndex(id);

		if (index < 0)
			return;

		if (_contextQuickActionsSubmenu.IsItemDisabled(index) != disabled)
			_contextQuickActionsSubmenu.SetItemDisabled(index, disabled);

		if (_contextQuickActionsSubmenu.GetItemTooltip(index) != tooltip)
			_contextQuickActionsSubmenu.SetItemTooltip(index, tooltip);
	}

	private void UpdateRefactorNamespaceContextMenuAvailability(bool noScriptsFound)
	{
		bool disabled = noScriptsFound || _isBeautifyingScript;
		string tooltip = noScriptsFound
			? QuickActionsNoScriptsTooltip
			: _isBeautifyingScript
				? RefactorNamespaceBeautifyRunningTooltip
				: string.Empty;

		UpdateQuickActionContextMenuItemAvailability(
			ContextRefactorNamespace,
			disabled,
			tooltip
		);
	}

	private void OnContextMenuIdPressed(long id)
	{
		switch (id)
		{
			case ContextAddFolder:
				OpenAddFolderDialog();
				break;

			case ContextAddScript:
				TryOpenAddExistingScriptsDialogForSelectedItem();
				break;

			case ContextAddScene:
				TryOpenAddExistingScenesDialogForSelectedItem();
				break;

			case ContextNewScript:
				TryOpenCreateScriptDialogForSelectedItem();
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

			case ContextBindFolder:
				OpenFolderBindingDialog();
				break;

			case ContextUnbindFolder:
				UnbindPendingFolder();
				break;

			case ContextRefactorNamespace:
				if (_pendingQuickActionsNoScriptsFound)
					return;

				TryOpenNamespaceRefactorDialog(_pendingRenameMetadata);
				break;

			case ContextBeautifyScript:
				if (_isBeautifyingScript)
					return;

				OpenBeautifyScriptCSharpierCheckDialog();
				break;

			case ContextBeautifyScripts:
				if (
					_isBeautifyingScript
					|| _pendingQuickActionsNoScriptsFound
				)
				{
					return;
				}

				OpenBeautifyScriptsCSharpierCheckDialog();
				break;
		}
	}

	#endregion
}
#endif
