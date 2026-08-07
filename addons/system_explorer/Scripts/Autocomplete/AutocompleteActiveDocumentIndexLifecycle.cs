#if TOOLS
using Godot;
using System;
using SystemExplorer.Autocomplete.Indexing.ActiveDocument;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.Autocomplete;

internal sealed class AutocompleteActiveDocumentIndexLifecycle
{
	private readonly CSharpActiveDocumentIndex _index;
	private readonly CSharpActiveDocumentIndexCoordinator _coordinator;
	private readonly Action<string, string> _debugLog;

	private long _revision;
	private long _lastCapturedRevision;
	private string _lastActiveScriptPath = "";
	private string _lastCapturedScriptPath = "";
	private bool _hasCapturedCurrentBuffer;
	private bool _dirty;
	private bool _shutdown;

	internal AutocompleteActiveDocumentIndexLifecycle(
		CSharpActiveDocumentIndex index,
		CSharpActiveDocumentIndexCoordinator coordinator,
		Action<string, string> debugLog
	)
	{
		_index = index ?? throw new ArgumentNullException(nameof(index));
		_coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
		_debugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
	}

	internal void MarkDirty()
	{
		if (!_shutdown)
			_dirty = true;
	}

	internal bool NeedsCapture(string scriptPath)
	{
		if (_shutdown)
			return false;

		string normalizedPath = ScriptPathUtility.Normalize(scriptPath);
		if (!IsCSharpScriptPath(normalizedPath))
			return false;

		PrepareForActiveScriptPath(normalizedPath);

		if (_dirty)
			return true;

		CSharpActiveDocumentIndexSnapshot snapshot = _index.CurrentSnapshot;
		bool snapshotMatches =
			snapshot != null
			&& snapshot.HasBuiltAtLeastOnce
			&& string.Equals(
				snapshot.ScriptPath,
				normalizedPath,
				StringComparison.OrdinalIgnoreCase
			);

		if (snapshotMatches)
			return false;

		return !_hasCapturedCurrentBuffer
			|| !string.Equals(
				_lastCapturedScriptPath,
				normalizedPath,
				StringComparison.OrdinalIgnoreCase
			);
	}

	internal void CapturePendingText(
		CodeEdit codeEdit,
		string scriptPath,
		string reason
	)
	{
		if (_shutdown || !IsValidGodotObject(codeEdit))
			return;

		string normalizedPath = ScriptPathUtility.Normalize(scriptPath);
		if (!IsCSharpScriptPath(normalizedPath))
			return;

		PrepareForActiveScriptPath(normalizedPath);
		if (!NeedsCapture(normalizedPath))
			return;

		string sourceText;

		try
		{
			sourceText = codeEdit.Text ?? "";
		}
		catch (Exception exception)
		{
			LogCaptureFailure(normalizedPath, exception);
			return;
		}

		long revision = NextRevision();
		var request = new CSharpActiveDocumentIndexRequest(
			revision,
			NormalizeReason(reason),
			normalizedPath,
			sourceText
		);

		if (!_coordinator.RequestIndex(request))
			return;

		_lastActiveScriptPath = normalizedPath;
		_lastCapturedScriptPath = normalizedPath;
		_lastCapturedRevision = revision;
		_hasCapturedCurrentBuffer = true;
		_dirty = false;
	}

	internal void HandleBuildFailure(CSharpActiveDocumentIndexBuildResult result)
	{
		if (
			_shutdown
			|| result == null
			|| result.Revision != _lastCapturedRevision
		)
		{
			return;
		}

		if (
			!string.IsNullOrWhiteSpace(result.ScriptPath)
			&& !string.Equals(
				ScriptPathUtility.Normalize(result.ScriptPath),
				_lastCapturedScriptPath,
				StringComparison.OrdinalIgnoreCase
			)
		)
		{
			return;
		}

		_hasCapturedCurrentBuffer = false;
		_dirty = true;
	}

	internal void ResetForScriptChange()
	{
		if (_shutdown)
			return;

		InvalidateCurrentOverlay(markDirty: true);
	}

	internal void ResetTransientState()
	{
		if (_shutdown)
			return;

		InvalidateCurrentOverlay(markDirty: true);
	}

	internal void Shutdown()
	{
		if (_shutdown)
			return;

		_shutdown = true;
		long invalidationRevision = NextRevision();
		_coordinator.ResetTransientState(invalidationRevision);
		_coordinator.StopAcceptingRequests();
		_index.Clear();
		_lastActiveScriptPath = "";
		_lastCapturedScriptPath = "";
		_lastCapturedRevision = 0;
		_hasCapturedCurrentBuffer = false;
		_dirty = false;
	}

	private void PrepareForActiveScriptPath(string normalizedPath)
	{
		if (
			string.IsNullOrWhiteSpace(_lastActiveScriptPath)
			|| string.Equals(
				_lastActiveScriptPath,
				normalizedPath,
				StringComparison.OrdinalIgnoreCase
			)
		)
		{
			_lastActiveScriptPath = normalizedPath;
			return;
		}

		InvalidateCurrentOverlay(markDirty: true);
		_lastActiveScriptPath = normalizedPath;
	}

	private void InvalidateCurrentOverlay(bool markDirty)
	{
		long invalidationRevision = NextRevision();
		_coordinator.ResetTransientState(invalidationRevision);
		_index.Clear();
		_lastActiveScriptPath = "";
		_lastCapturedScriptPath = "";
		_lastCapturedRevision = 0;
		_hasCapturedCurrentBuffer = false;
		_dirty = markDirty;
	}

	private long NextRevision()
	{
		unchecked
		{
			_revision++;
			if (_revision == 0)
				_revision = 1;
		}

		return _revision;
	}

	private void LogCaptureFailure(string scriptPath, Exception exception)
	{
		try
		{
			_debugLog(
				"C# autocomplete active document capture failed",
				$"ScriptPath='{scriptPath}', "
					+ $"ExceptionType='{exception?.GetType().FullName ?? ""}', "
					+ $"Exception='{exception}'"
			);
		}
		catch
		{
			// Capture diagnostics must never escape the Godot callback.
		}
	}

	private static string NormalizeReason(string reason)
	{
		if (string.IsNullOrWhiteSpace(reason))
			return "Active document capture";

		string normalizedReason = reason.Trim();
		return normalizedReason.Length <= 160
			? normalizedReason
			: normalizedReason.Substring(0, 160);
	}

	private static bool IsCSharpScriptPath(string scriptPath)
	{
		return !string.IsNullOrWhiteSpace(scriptPath)
			&& scriptPath.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
			&& scriptPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsValidGodotObject(GodotObject source)
	{
		return source != null && GodotObject.IsInstanceValid(source);
	}
}
#endif
