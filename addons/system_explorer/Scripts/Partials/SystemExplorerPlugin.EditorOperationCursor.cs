#if TOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using SystemExplorer.EditorIntegration.Operations;

public partial class SystemExplorerPlugin
{
	private const string EditorOperationBusyCursorMetadata =
		"_system_explorer_editor_operation_busy_cursor";
	private const char EditorOperationBusyCursorMetadataSeparator = '|';

	private readonly record struct EditorOperationControlCursorSnapshot(
		Control Control,
		Control.CursorShape PreviousCursorShape
	);

	private sealed class EditorOperationBusyCursorSnapshot
	{
		internal EditorOperationBusyCursorSnapshot(
			EditorOperationLease owner,
			string ownerToken,
			DisplayServer.CursorShape previousGlobalCursorShape,
			EditorOperationControlCursorSnapshot[] controlCursors
		)
		{
			Owner = owner;
			OwnerToken = ownerToken;
			PreviousGlobalCursorShape = previousGlobalCursorShape;
			ControlCursors = controlCursors ?? Array.Empty<EditorOperationControlCursorSnapshot>();
		}

		internal EditorOperationLease Owner { get; }
		internal string OwnerToken { get; }
		internal DisplayServer.CursorShape PreviousGlobalCursorShape { get; }
		internal EditorOperationControlCursorSnapshot[] ControlCursors { get; }
	}

	private readonly record struct NativeEditorOperationBusyCursorMarker(
		string OwnerToken,
		DisplayServer.CursorShape PreviousGlobalCursorShape
	);

	private EditorOperationBusyCursorSnapshot _editorOperationBusyCursorSnapshot;

	private bool IsEditorOperationBusyCursorActive =>
		_editorOperationBusyCursorSnapshot != null
		|| HasNativeEditorOperationBusyCursorMarker();

	public override void _Process(double delta)
	{
		try
		{
			if (!ShouldReapplyEditorOperationBusyCursor())
			{
				TrySetEditorOperationBusyCursorProcessing(false);
				return;
			}

			TrySetGlobalEditorOperationCursor(DisplayServer.CursorShape.Busy);
		}
		catch
		{
			TrySetEditorOperationBusyCursorProcessing(false);
		}
	}

	private bool ShouldReapplyEditorOperationBusyCursor()
	{
		try
		{
			EditorOperationBusyCursorSnapshot snapshot =
				_editorOperationBusyCursorSnapshot;

			return snapshot != null
				&& snapshot.Owner != null
				&& !snapshot.Owner.IsBackgroundOperation
				&& snapshot.Owner.IsCurrent
				&& !_editorOperationShutdownStarted
				&& IsValidGodotObject(this)
				&& IsInsideTree();
		}
		catch
		{
			return false;
		}
	}

	private bool TryEnterEditorOperationBusyCursor(
		EditorOperationLease operation,
		bool backgroundOperation
	)
	{
		if (
			backgroundOperation
			|| operation == null
			|| !operation.IsCurrent
			|| !IsValidGodotObject(this)
		)
		{
			return false;
		}

		if (_editorOperationBusyCursorSnapshot != null)
		{
			if (ReferenceEquals(_editorOperationBusyCursorSnapshot.Owner, operation))
				return true;

			ForceResetEditorOperationBusyCursor();
		}
		else if (HasNativeEditorOperationBusyCursorMarker())
		{
			RestoreEditorOperationBusyCursorFromNativeMarker();
		}

		EditorOperationBusyCursorSnapshot snapshot;

		try
		{
			snapshot = new EditorOperationBusyCursorSnapshot(
				operation,
				Guid.NewGuid().ToString("N"),
				DisplayServer.CursorGetShape(),
				CaptureEditorOperationControlCursors()
			);
		}
		catch
		{
			return false;
		}

		if (!TryWriteNativeEditorOperationBusyCursorMarker(snapshot))
			return false;

		_editorOperationBusyCursorSnapshot = snapshot;

		try
		{
			DisplayServer.CursorSetShape(DisplayServer.CursorShape.Busy);
			SetEditorOperationDockCursorShapes(Control.CursorShape.Busy);
			TrySetEditorOperationBusyCursorProcessing(true);
			return true;
		}
		catch
		{
			ForceResetEditorOperationBusyCursor();
			return false;
		}
	}

	private void ExitEditorOperationBusyCursor(EditorOperationLease operation)
	{
		try
		{
			EditorOperationBusyCursorSnapshot snapshot =
				_editorOperationBusyCursorSnapshot;

			if (
				snapshot == null
				|| operation == null
				|| !ReferenceEquals(snapshot.Owner, operation)
			)
			{
				return;
			}

			RestoreEditorOperationBusyCursorSnapshot(snapshot);
		}
		catch
		{
			EmergencyResetEditorOperationBusyCursor();
		}
	}

	private void ForceResetEditorOperationBusyCursor()
	{
		TrySetEditorOperationBusyCursorProcessing(false);

		try
		{
			if (_editorOperationBusyCursorSnapshot != null)
			{
				RestoreEditorOperationBusyCursorSnapshot(
					_editorOperationBusyCursorSnapshot
				);
				return;
			}

			RestoreEditorOperationBusyCursorFromNativeMarker();
		}
		catch
		{
			EmergencyResetEditorOperationBusyCursor();
		}
	}

	private void RecoverEditorOperationBusyCursorAfterManagedAssemblyReload()
	{
		TrySetEditorOperationBusyCursorProcessing(false);

		if (_editorOperationBusyCursorSnapshot == null)
			RestoreEditorOperationBusyCursorFromNativeMarker();
	}

	private void RestoreEditorOperationBusyCursorSnapshot(
		EditorOperationBusyCursorSnapshot snapshot
	)
	{
		if (snapshot == null)
			return;

		try
		{
			if (NativeEditorOperationBusyCursorBelongsToDifferentOwner(snapshot.OwnerToken))
				return;

			if (ReferenceEquals(_editorOperationBusyCursorSnapshot, snapshot))
				TrySetEditorOperationBusyCursorProcessing(false);

			TrySetGlobalEditorOperationCursor(snapshot.PreviousGlobalCursorShape);
			foreach (EditorOperationControlCursorSnapshot controlCursor in snapshot.ControlCursors)
			{
				TrySetEditorOperationControlCursor(
					controlCursor.Control,
					controlCursor.PreviousCursorShape
				);
			}
		}
		finally
		{
			if (ReferenceEquals(_editorOperationBusyCursorSnapshot, snapshot))
				_editorOperationBusyCursorSnapshot = null;

			TryClearNativeEditorOperationBusyCursorMarker(snapshot.OwnerToken);
		}
	}

	private void RestoreEditorOperationBusyCursorFromNativeMarker()
	{
		TrySetEditorOperationBusyCursorProcessing(false);

		if (!HasNativeEditorOperationBusyCursorMarker())
			return;

		try
		{
			if (
				TryReadNativeEditorOperationBusyCursorMarker(
					out NativeEditorOperationBusyCursorMarker marker
				)
			)
			{
				TrySetGlobalEditorOperationCursor(marker.PreviousGlobalCursorShape);
			}
			else
			{
				TrySetGlobalEditorOperationCursor(DisplayServer.CursorShape.Arrow);
			}

			RestoreNormalEditorOperationDockCursors();
		}
		finally
		{
			_editorOperationBusyCursorSnapshot = null;
			TryClearNativeEditorOperationBusyCursorMarker();
		}
	}

	private void EmergencyResetEditorOperationBusyCursor()
	{
		TrySetEditorOperationBusyCursorProcessing(false);
		TrySetGlobalEditorOperationCursor(DisplayServer.CursorShape.Arrow);
		RestoreNormalEditorOperationDockCursors();
		_editorOperationBusyCursorSnapshot = null;
		TryClearNativeEditorOperationBusyCursorMarker();
	}

	private EditorOperationControlCursorSnapshot[] CaptureEditorOperationControlCursors()
	{
		List<EditorOperationControlCursorSnapshot> snapshots = new(4);
		CaptureEditorOperationControlCursor(_dock, snapshots);
		CaptureEditorOperationControlCursor(_tree, snapshots);
		CaptureEditorOperationControlCursor(_systemNameInput, snapshots);
		CaptureEditorOperationControlCursor(_scriptFilterInput, snapshots);
		return snapshots.ToArray();
	}

	private static void CaptureEditorOperationControlCursor(
		Control control,
		List<EditorOperationControlCursorSnapshot> snapshots
	)
	{
		if (!IsValidGodotObject(control))
			return;

		snapshots.Add(
			new EditorOperationControlCursorSnapshot(
				control,
				control.MouseDefaultCursorShape
			)
		);
	}

	private void SetEditorOperationDockCursorShapes(Control.CursorShape cursorShape)
	{
		TrySetEditorOperationControlCursor(_dock, cursorShape);
		TrySetEditorOperationControlCursor(_tree, cursorShape);
		TrySetEditorOperationControlCursor(_systemNameInput, cursorShape);
		TrySetEditorOperationControlCursor(_scriptFilterInput, cursorShape);
	}

	private void RestoreNormalEditorOperationDockCursors()
	{
		TrySetEditorOperationControlCursor(_dock, Control.CursorShape.Arrow);
		TrySetEditorOperationControlCursor(_tree, Control.CursorShape.Arrow);
		TrySetEditorOperationControlCursor(_systemNameInput, Control.CursorShape.Ibeam);
		TrySetEditorOperationControlCursor(_scriptFilterInput, Control.CursorShape.Ibeam);
	}

	private bool TryWriteNativeEditorOperationBusyCursorMarker(
		EditorOperationBusyCursorSnapshot snapshot
	)
	{
		if (snapshot == null || !IsValidGodotObject(this))
			return false;

		try
		{
			SetMeta(
				EditorOperationBusyCursorMetadata,
				$"{snapshot.OwnerToken}{EditorOperationBusyCursorMetadataSeparator}{ToCursorMetadataValue(snapshot.PreviousGlobalCursorShape)}"
			);
			return true;
		}
		catch
		{
			TryClearNativeEditorOperationBusyCursorMarker(snapshot.OwnerToken);
			return false;
		}
	}

	private bool TryReadNativeEditorOperationBusyCursorMarker(
		out NativeEditorOperationBusyCursorMarker marker
	)
	{
		marker = default;

		if (!HasNativeEditorOperationBusyCursorMarker())
			return false;

		try
		{
			string[] values = GetMeta(EditorOperationBusyCursorMetadata)
				.AsString()
				.Split(EditorOperationBusyCursorMetadataSeparator);

			if (
				values.Length != 2
				|| string.IsNullOrWhiteSpace(values[0])
				|| !TryParseGlobalEditorOperationCursorShape(values[1], out DisplayServer.CursorShape cursorShape)
			)
			{
				return false;
			}

			marker = new NativeEditorOperationBusyCursorMarker(values[0], cursorShape);
			return true;
		}
		catch
		{
			return false;
		}
	}

	private bool NativeEditorOperationBusyCursorBelongsToDifferentOwner(
		string ownerToken
	)
	{
		return TryReadNativeEditorOperationBusyCursorMarker(
			out NativeEditorOperationBusyCursorMarker marker
		)
			&& !string.Equals(marker.OwnerToken, ownerToken, StringComparison.Ordinal);
	}

	private bool HasNativeEditorOperationBusyCursorMarker()
	{
		if (!IsValidGodotObject(this))
			return false;

		try
		{
			return HasMeta(EditorOperationBusyCursorMetadata);
		}
		catch
		{
			return false;
		}
	}

	private void TryClearNativeEditorOperationBusyCursorMarker(
		string expectedOwnerToken = ""
	)
	{
		if (!IsValidGodotObject(this))
			return;

		try
		{
			if (
				!string.IsNullOrWhiteSpace(expectedOwnerToken)
				&& TryReadNativeEditorOperationBusyCursorMarker(
					out NativeEditorOperationBusyCursorMarker marker
				)
				&& !string.Equals(
					marker.OwnerToken,
					expectedOwnerToken,
					StringComparison.Ordinal
				)
			)
			{
				return;
			}

			RemoveMeta(EditorOperationBusyCursorMetadata);
		}
		catch
		{
		}
	}

	private static string ToCursorMetadataValue(DisplayServer.CursorShape cursorShape) =>
		((int)cursorShape).ToString(CultureInfo.InvariantCulture);

	private static bool TryParseGlobalEditorOperationCursorShape(
		string value,
		out DisplayServer.CursorShape cursorShape
	)
	{
		cursorShape = DisplayServer.CursorShape.Arrow;
		if (
			!int.TryParse(
				value,
				NumberStyles.Integer,
				CultureInfo.InvariantCulture,
				out int cursorShapeValue
			)
			|| !Enum.IsDefined(typeof(DisplayServer.CursorShape), cursorShapeValue)
		)
		{
			return false;
		}

		cursorShape = (DisplayServer.CursorShape)cursorShapeValue;
		return true;
	}

	private void TrySetEditorOperationBusyCursorProcessing(bool enabled)
	{
		if (!IsValidGodotObject(this))
			return;

		try
		{
			SetProcess(enabled);
		}
		catch
		{
		}
	}

	private static void TrySetGlobalEditorOperationCursor(DisplayServer.CursorShape cursorShape)
	{
		try
		{
			DisplayServer.CursorSetShape(cursorShape);
		}
		catch
		{
		}
	}

	private static void TrySetEditorOperationControlCursor(
		Control control,
		Control.CursorShape cursorShape
	)
	{
		if (!IsValidGodotObject(control))
			return;

		try
		{
			control.MouseDefaultCursorShape = cursorShape;
		}
		catch
		{
		}
	}
}
#endif
