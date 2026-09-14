#if TOOLS
using Godot;

public partial class SystemExplorerPlugin
{
	#region Tree Input and Keyboard Handling
	private void OnTreeGuiInput(InputEvent inputEvent)
	{
		if (inputEvent is InputEventKey keyEvent)
		{
			HandleTreeKeyboardInput(keyEvent);
			return;
		}

		if (inputEvent is InputEventMouseMotion mouseMotion)
		{
			if (RefreshTreeHoverPresentationForActiveNoteFocus())
			{
				ResetInlineTreeNoteCursor();
				return;
			}

			UpdateHoveredTreeItemLockVisibility();
			UpdateInlineTreeNoteCursor(mouseMotion.Position);
			UpdateDragDropTargetHighlight();
			return;
		}

		if (inputEvent is not InputEventMouseButton mouseButton)
			return;

		Vector2 mousePosition = _tree.GetLocalMousePosition();
		TreeItem item = _tree.GetItemAtPosition(mousePosition);

		if (mouseButton.ButtonIndex == MouseButton.Middle)
		{
			if (!mouseButton.Pressed || item == null)
				return;

			ToggleItemLock(item, selectToggledItemAfterBuild: false);
			_tree.AcceptEvent();
			return;
		}

		if (mouseButton.ButtonIndex == MouseButton.Left)
		{
			if (mouseButton.Pressed)
			{
				ClearInlineTreeNotePressState();

				if (
					TryGetInlineTreeNoteHitTarget(
						mousePosition,
						out _,
						out string noteMetadata
					)
				)
				{
					_pendingInlineTreeNoteClickMetadata = noteMetadata;
					_inlineTreeNotePressPosition = mousePosition;
					ClearDragState();
					_tree.AcceptEvent();
					return;
				}
			}
			else if (!string.IsNullOrWhiteSpace(_pendingInlineTreeNoteClickMetadata))
			{
				string pressMetadata = _pendingInlineTreeNoteClickMetadata;
				Vector2 pressPosition = _inlineTreeNotePressPosition;

				bool shouldActivateInlineNote =
					pressPosition.DistanceTo(mousePosition) <= ClickOpenDragThreshold
					&& TryGetInlineTreeNoteHitTarget(
						mousePosition,
						out _,
						out string releaseMetadata
					)
					&& string.Equals(
						pressMetadata,
						releaseMetadata,
						System.StringComparison.Ordinal
					);

				ClearInlineTreeNotePressState();
				ClearDragState();

				if (shouldActivateInlineNote)
				{
					bool noteSessionWasActive = IsNoteDialogSessionActive();
					if (
						TryActivateTreeNoteTarget(pressMetadata)
						&& !noteSessionWasActive
					)
					{
						ResetInlineTreeNoteCursor();
					}
				}

				_tree.AcceptEvent();
				return;
			}

			if (_isFilteringScripts)
			{
				ClearDragState();

				if (mouseButton.Pressed && mouseButton.DoubleClick && IsScriptOrSceneItem(item))
				{
					item.Select(0);
					_selectedScriptEntryFromFilter = GetEntryFromMetadata(
						item.GetMetadata(0).AsString()
					);
					_ignoreNextScriptFilterReleaseOpen = true;

					if (IsSceneItem(item))
						OpenSceneFromTreeItem(item);
					else
						OpenLinkedSceneFromTreeItem(item);

					_tree.AcceptEvent();
					return;
				}

				if (!mouseButton.Pressed && IsScriptOrSceneItem(item))
				{
					item.Select(0);
					_selectedScriptEntryFromFilter = GetEntryFromMetadata(
						item.GetMetadata(0).AsString()
					);

					if (_ignoreNextScriptFilterReleaseOpen)
					{
						_ignoreNextScriptFilterReleaseOpen = false;
						_tree.AcceptEvent();
						return;
					}

					if (IsSceneItem(item))
						OpenSceneFromTreeItem(item);
					else
						OpenScriptFromTreeItem(item);

					_tree.AcceptEvent();
				}

				if (!mouseButton.Pressed)
					_ignoreNextScriptFilterReleaseOpen = false;

				return;
			}

			if (mouseButton.Pressed && mouseButton.DoubleClick)
			{
				if (IsScriptItem(item))
				{
					OpenLinkedSceneFromTreeItem(item);
					_tree.AcceptEvent();
					return;
				}

				if (ToggleExpandedIfSystemOrFolder(item))
				{
					ClearDragState();
					_tree.AcceptEvent();
					return;
				}
			}

			if (IsShiftPressed(mouseButton))
			{
				ClearDragState();

				if (mouseButton.Pressed)
					ToggleExpandedIfSystemOrFolder(item);

				_tree.AcceptEvent();
				return;
			}

			if (mouseButton.Pressed)
			{
				ClearDragDropTargetHighlight();
				_draggedMetadata = item?.GetMetadata(0).AsString() ?? "";
				_draggedSourceSystemName = item == null ? "" : GetSystemNameFromTreeItem(item);
				_draggedSourceFolderPath = item == null ? "" : GetFolderPathFromTreeItem(item);
				_leftMousePressPosition = mousePosition;
				_leftMousePressedMetadata = _draggedMetadata;
				_leftMousePressedOnSelectedScript = IsSelectedScriptOrSceneItem(item);
			}
			else
			{
				if (string.IsNullOrWhiteSpace(_draggedMetadata) || item == null)
				{
					ClearDragState();
					return;
				}

				string releaseMetadata = item.GetMetadata(0).AsString();
				bool isClick =
					_leftMousePressedOnSelectedScript
					&& _leftMousePressedMetadata == releaseMetadata
					&& _leftMousePressPosition.DistanceTo(mousePosition) <= ClickOpenDragThreshold;

				if (isClick)
				{
					OpenScriptFromTreeItem(item);
					OpenSceneFromTreeItem(item);
					ClearDragState();
					return;
				}

				ClearDragDropTargetHighlight();
				MoveDraggedItem(_draggedMetadata, item);

				ClearDragState();
			}

			return;
		}

		if (!mouseButton.Pressed || mouseButton.ButtonIndex != MouseButton.Right)
			return;

		if (item == null)
			return;

		if (_isFilteringScripts)
		{
			if (!IsScriptOrSceneItem(item))
				return;

			item.Select(0);

			string filteredScriptMetadata = item.GetMetadata(0).AsString();
			_selectedScriptEntryFromFilter = GetEntryFromMetadata(filteredScriptMetadata);
			OpenContextMenuForTreeItem(item);
			_tree.AcceptEvent();
			return;
		}

		item.Select(0);
		OpenContextMenuForTreeItem(item);
	}

	private bool TryGetInlineTreeNoteHitTarget(
		Vector2 mousePosition,
		out TreeItem item,
		out string metadata
	)
	{
		item = null;
		metadata = "";

		if (
			!IsValidGodotObject(_tree)
			|| !_tree.IsInsideTree()
			|| _isFilteringScripts
			|| _tree.GetColumnAtPosition(mousePosition) != 0
		)
		{
			return false;
		}

		TreeItem hitItem = _tree.GetItemAtPosition(mousePosition);
		if (hitItem == null)
			return false;

		string hitMetadata = hitItem.GetMetadata(0).AsString();
		if (
			!TryResolveNoteTargetFromMetadata(hitMetadata, out var noteTarget, out _)
			|| !IsCanonicalNoteTargetMetadata(hitMetadata, noteTarget)
			|| !CanActivateTreeNoteTarget(hitMetadata)
			|| !HasNotePresenceForMetadata(hitMetadata)
			|| !TryGetInlineTreeNoteMarkerRect(hitItem, out Rect2 noteMarkerRect)
			|| !noteMarkerRect.HasPoint(mousePosition)
		)
		{
			return false;
		}

		item = hitItem;
		metadata = hitMetadata;
		return true;
	}

	private bool TryGetInlineTreeNoteMarkerRect(TreeItem item, out Rect2 markerRect)
	{
		markerRect = new Rect2();

		if (
			item == null
			|| !IsValidGodotObject(_tree)
			|| !_tree.IsInsideTree()
			|| _tree.IsLayoutRtl()
		)
		{
			return false;
		}

		string rowText = item.GetText(0);
		int markerIndex = rowText.LastIndexOf(TreeNoteMarker, System.StringComparison.Ordinal);
		if (markerIndex < 0)
			return false;

		Rect2 columnRect = _tree.GetItemAreaRect(item, 0);
		if (columnRect.Size.X <= 0.0f || columnRect.Size.Y <= 0.0f)
			return false;

		if (!TryGetInlineTreeItemHierarchyDepth(item, out int hierarchyDepth))
			return false;

		int itemMargin = _tree.GetThemeConstant("item_margin");
		int horizontalSeparation = _tree.GetThemeConstant("h_separation");
		int foldingOffset = item.DisableFolding || _tree.HideFolding
			? horizontalSeparation
			: itemMargin;
		float itemOffset = hierarchyDepth * itemMargin + foldingOffset;
		float itemWidth = columnRect.Size.X - itemOffset;
		if (itemWidth <= 0.0f)
			return false;

		int innerMarginLeft = _tree.GetThemeConstant("inner_item_margin_left");
		int innerMarginRight = _tree.GetThemeConstant("inner_item_margin_right");
		float innerStartX = columnRect.Position.X + itemOffset + innerMarginLeft;
		float innerWidth = itemWidth - innerMarginLeft - innerMarginRight;
		if (innerWidth <= 0.0f)
			return false;

		if (!TryGetInlineTreeItemEffectiveIconWidth(item, out float iconWidth))
			return false;

		Texture2D icon = item.GetIcon(0);
		bool hasIcon = IsValidGodotObject(icon);
		float iconSlotWidth = 0.0f;
		if (hasIcon)
		{
			int iconSeparation = _tree.GetThemeConstant("icon_h_separation");
			iconSlotWidth = iconWidth + iconSeparation;
			if (iconSlotWidth >= innerWidth)
				return false;
		}

		float textAvailableWidth = innerWidth - iconSlotWidth;
		if (textAvailableWidth <= 0.0f)
			return false;

		if (!TryGetInlineTreeItemFont(item, out Font font, out int fontSize))
			return false;

		string textBeforeMarker = rowText.Substring(0, markerIndex);
		string textThroughMarker = rowText.Substring(
			0,
			markerIndex + TreeNoteMarker.Length
		);
		string layoutText = rowText;
		string suffix = item.GetSuffix(0);
		if (!string.IsNullOrEmpty(suffix))
			layoutText += (layoutText.Length == 0 ? "" : " ") + suffix;

		float prefixWidth = font.GetStringSize(
			textBeforeMarker,
			HorizontalAlignment.Left,
			-1.0f,
			fontSize
		).X;
		float throughMarkerWidth = font.GetStringSize(
			textThroughMarker,
			HorizontalAlignment.Left,
			-1.0f,
			fontSize
		).X;
		float naturalTextWidth = font.GetStringSize(
			layoutText,
			HorizontalAlignment.Left,
			-1.0f,
			fontSize
		).X;
		float markerWidth = throughMarkerWidth - prefixWidth;

		if (
			!float.IsFinite(prefixWidth)
			|| !float.IsFinite(throughMarkerWidth)
			|| !float.IsFinite(naturalTextWidth)
			|| !float.IsFinite(markerWidth)
			|| prefixWidth < 0.0f
			|| naturalTextWidth < 0.0f
			|| markerWidth <= 0.0f
		)
		{
			return false;
		}

		float displayedTextWidth = Mathf.Min(naturalTextWidth, textAvailableWidth);
		float displayedWidth = iconSlotWidth + displayedTextWidth;
		float emptyWidth = Mathf.Max(0.0f, innerWidth - displayedWidth);
		float alignmentOffset = item.GetTextAlignment(0) switch
		{
			HorizontalAlignment.Center => Mathf.Floor(emptyWidth * 0.5f),
			HorizontalAlignment.Right => emptyWidth,
			_ => 0.0f,
		};

		float textStartX = innerStartX + alignmentOffset + iconSlotWidth;
		Rect2 rawMarkerRect = new(
			new Vector2(
				textStartX + prefixWidth - InlineTreeNoteHorizontalHitPadding,
				columnRect.Position.Y
			),
			new Vector2(
				markerWidth + InlineTreeNoteHorizontalHitPadding * 2.0f,
				columnRect.Size.Y
			)
		);
		Rect2 visibleTextRect = new(
			new Vector2(textStartX, columnRect.Position.Y),
			new Vector2(displayedTextWidth, columnRect.Size.Y)
		);

		if (!TryIntersectInlineTreeRects(rawMarkerRect, visibleTextRect, out Rect2 clippedRect))
			return false;

		if (!TryIntersectInlineTreeRects(clippedRect, columnRect, out clippedRect))
			return false;

		Rect2 treeLocalRect = new(Vector2.Zero, _tree.Size);
		if (!TryIntersectInlineTreeRects(clippedRect, treeLocalRect, out markerRect))
			return false;

		return markerRect.Size.X > 0.0f && markerRect.Size.Y > 0.0f;
	}

	private bool TryGetInlineTreeItemHierarchyDepth(TreeItem item, out int hierarchyDepth)
	{
		hierarchyDepth = 0;

		TreeItem root = _tree?.GetRoot();
		if (item == null || root == null)
			return false;

		TreeItem current = item;
		while (current != root)
		{
			current = current.GetParent();
			if (current == null)
				return false;

			hierarchyDepth++;
		}

		if (_tree.HideRoot && item != root)
			hierarchyDepth--;

		return hierarchyDepth >= 0;
	}

	private bool TryGetInlineTreeItemEffectiveIconWidth(TreeItem item, out float iconWidth)
	{
		iconWidth = 0.0f;

		Texture2D icon = item?.GetIcon(0);
		if (!IsValidGodotObject(icon))
			return true;

		Rect2 iconRegion = item.GetIconRegion(0);
		iconWidth = iconRegion.Size.X > 0.0f && iconRegion.Size.Y > 0.0f
			? iconRegion.Size.X
			: icon.GetWidth();

		if (!float.IsFinite(iconWidth) || iconWidth <= 0.0f)
			return false;

		int maximumWidth = 0;
		int themeMaximumWidth = _tree.GetThemeConstant("icon_max_width");
		if (themeMaximumWidth > 0)
			maximumWidth = themeMaximumWidth;

		int itemMaximumWidth = item.GetIconMaxWidth(0);
		if (itemMaximumWidth > 0 && (maximumWidth == 0 || itemMaximumWidth < maximumWidth))
			maximumWidth = itemMaximumWidth;

		if (maximumWidth > 0 && iconWidth > maximumWidth)
			iconWidth = maximumWidth;

		return true;
	}

	private bool TryGetInlineTreeItemFont(TreeItem item, out Font font, out int fontSize)
	{
		font = item?.GetCustomFont(0);
		if (!IsValidGodotObject(font))
			font = _tree.GetThemeFont("font");

		fontSize = item?.GetCustomFontSize(0) ?? 0;
		if (fontSize <= 0)
			fontSize = _tree.GetThemeFontSize("font_size");

		return IsValidGodotObject(font) && fontSize > 0;
	}

	private static bool TryIntersectInlineTreeRects(
		Rect2 first,
		Rect2 second,
		out Rect2 intersection
	)
	{
		float left = Mathf.Max(first.Position.X, second.Position.X);
		float top = Mathf.Max(first.Position.Y, second.Position.Y);
		float right = Mathf.Min(first.End.X, second.End.X);
		float bottom = Mathf.Min(first.End.Y, second.End.Y);

		if (right <= left || bottom <= top)
		{
			intersection = new Rect2();
			return false;
		}

		intersection = new Rect2(
			new Vector2(left, top),
			new Vector2(right - left, bottom - top)
		);
		return true;
	}

	private void ClearInlineTreeNotePressState()
	{
		_pendingInlineTreeNoteClickMetadata = "";
		_inlineTreeNotePressPosition = Vector2.Zero;
	}

	private void UpdateInlineTreeNoteCursor(Vector2 mousePosition)
	{
		if (!IsValidGodotObject(_tree))
			return;

		if (IsEditorOperationBusyCursorActive)
		{
			_tree.MouseDefaultCursorShape = Control.CursorShape.Busy;
			return;
		}

		_tree.MouseDefaultCursorShape = TryGetInlineTreeNoteHitTarget(
			mousePosition,
			out _,
			out _
		)
			? Control.CursorShape.PointingHand
			: Control.CursorShape.Arrow;
	}

	private void ResetInlineTreeNoteCursor()
	{
		if (!IsValidGodotObject(_tree))
			return;

		_tree.MouseDefaultCursorShape = IsEditorOperationBusyCursorActive
			? Control.CursorShape.Busy
			: Control.CursorShape.Arrow;
	}

	private bool RefreshTreeHoverPresentationForActiveNoteFocus()
	{
		bool shouldSuppress = false;

		if (
			IsNoteDialogSessionActive()
			&& IsValidGodotObject(_noteDialog)
			&& TryReadNoteDialogHasFocus(out bool noteHasFocus)
		)
		{
			shouldSuppress = noteHasFocus;
		}

		SetTreeHoverPresentationSuppressedForFocusedNote(shouldSuppress);
		return shouldSuppress;
	}

	private void SetTreeHoverPresentationSuppressedForFocusedNote(bool suppressed)
	{
		if (!IsValidGodotObject(_tree))
		{
			_treeHoverPresentationSuppressedForFocusedNote = false;
			_hoveredTreeItemMetadata = "";
			return;
		}

		if (_treeHoverPresentationSuppressedForFocusedNote == suppressed)
		{
			if (suppressed && !string.IsNullOrWhiteSpace(_hoveredTreeItemMetadata))
			{
				_hoveredTreeItemMetadata = "";
				UpdateTreeLockIconVisibility();
			}

			return;
		}

		_treeHoverPresentationSuppressedForFocusedNote = suppressed;

		if (suppressed)
		{
			var emptyHoverStyle = new StyleBoxEmpty();
			_tree.AddThemeStyleboxOverride("hovered", emptyHoverStyle);
			_tree.AddThemeStyleboxOverride("hovered_dimmed", emptyHoverStyle);

			StyleBox selectedStyle = _tree.GetThemeStylebox("selected");
			if (selectedStyle != null)
				_tree.AddThemeStyleboxOverride("hovered_selected", selectedStyle);

			StyleBox selectedFocusStyle = _tree.GetThemeStylebox("selected_focus");
			if (selectedFocusStyle != null)
			{
				_tree.AddThemeStyleboxOverride(
					"hovered_selected_focus",
					selectedFocusStyle
				);
			}

			_tree.AddThemeColorOverride(
				"font_hovered_color",
				_tree.GetThemeColor("font_color")
			);
			_tree.AddThemeColorOverride(
				"font_hovered_dimmed_color",
				_tree.GetThemeColor("font_color")
			);
			_tree.AddThemeColorOverride(
				"font_hovered_selected_color",
				_tree.GetThemeColor("font_selected_color")
			);

			if (!string.IsNullOrWhiteSpace(_hoveredTreeItemMetadata))
			{
				_hoveredTreeItemMetadata = "";
				UpdateTreeLockIconVisibility();
			}

			ClearDragDropTargetHighlight();
			ResetInlineTreeNoteCursor();
		}
		else
		{
			RemoveTreeHoverPresentationOverrides();
		}

		_tree.QueueRedraw();
	}

	private void ResetTreeHoverPresentationSuppression()
	{
		_treeHoverPresentationSuppressedForFocusedNote = false;

		if (!IsValidGodotObject(_tree))
			return;

		RemoveTreeHoverPresentationOverrides();
		_tree.QueueRedraw();
	}

	private void RemoveTreeHoverPresentationOverrides()
	{
		_tree.RemoveThemeStyleboxOverride("hovered");
		_tree.RemoveThemeStyleboxOverride("hovered_dimmed");
		_tree.RemoveThemeStyleboxOverride("hovered_selected");
		_tree.RemoveThemeStyleboxOverride("hovered_selected_focus");
		_tree.RemoveThemeColorOverride("font_hovered_color");
		_tree.RemoveThemeColorOverride("font_hovered_dimmed_color");
		_tree.RemoveThemeColorOverride("font_hovered_selected_color");
	}

	private void OnTreeMouseExited()
	{
		ClearDragDropTargetHighlight();
		ResetInlineTreeNoteCursor();

		if (string.IsNullOrWhiteSpace(_hoveredTreeItemMetadata))
			return;

		_hoveredTreeItemMetadata = "";
		UpdateTreeLockIconVisibility();
	}

	private void UpdateHoveredTreeItemLockVisibility()
	{
		if (_tree == null)
			return;

		TreeItem hoveredItem = _tree.GetItemAtPosition(_tree.GetLocalMousePosition());
		string hoveredMetadata = hoveredItem?.GetMetadata(0).AsString() ?? "";

		if (_hoveredTreeItemMetadata == hoveredMetadata)
			return;

		_hoveredTreeItemMetadata = hoveredMetadata;
		UpdateTreeLockIconVisibility();
	}

	private void HandleTreeKeyboardInput(InputEventKey keyEvent)
	{
		TryTakeKeyboardControlTransitionEcho(keyEvent);

		if (TryHandleActiveScriptFilterTreeEscape(keyEvent))
		{
			_tree.AcceptEvent();
			return;
		}

		if (TryApplyTreeKeyboardNavigation(keyEvent))
		{
			_tree.AcceptEvent();
			return;
		}

		TryDispatchTreeShortcut(
			keyEvent,
			TreeShortcutInputRoute.TreeGuiInput
		);
	}

	private static bool IsShiftPressed(InputEventMouseButton mouseButton)
	{
		return mouseButton.ShiftPressed || Input.IsKeyPressed(Key.Shift);
	}

	private static bool ToggleExpandedIfSystemOrFolder(TreeItem item)
	{
		if (item == null)
			return false;

		string metadata = item.GetMetadata(0).AsString();

		if (!metadata.StartsWith("system::") && !metadata.StartsWith("folder::"))
			return false;

		item.Collapsed = !item.Collapsed;
		return true;
	}

	private bool IsSelectedScriptOrSceneItem(TreeItem item)
	{
		if (item == null)
			return false;

		if (_tree.GetSelected() != item)
			return false;

		string metadata = item.GetMetadata(0).AsString();

		return IsScriptOrSceneMetadata(metadata);
	}

	private static bool IsScriptItem(TreeItem item)
	{
		if (item == null)
			return false;

		string metadata = item.GetMetadata(0).AsString();
		return metadata.StartsWith("script::");
	}

	private static bool IsScriptOrSceneItem(TreeItem item)
	{
		return IsScriptItem(item) || IsSceneItem(item);
	}

	private static bool IsSceneItem(TreeItem item)
	{
		if (item == null)
			return false;

		string metadata = item.GetMetadata(0).AsString();
		return metadata.StartsWith("sceneLink::");
	}
	#endregion
}
#endif
