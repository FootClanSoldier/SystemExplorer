#if TOOLS
using Godot;
using System;

namespace SystemExplorer.Diagnostics;

internal sealed class SystemExplorerDebugLogger
{
	private readonly Func<bool> _isEnabled;

	internal SystemExplorerDebugLogger(Func<bool> isEnabled)
	{
		_isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
	}

	internal void Log(string message)
	{
		if (!_isEnabled())
			return;

		GD.Print($"[SystemExplorer] {message}");
	}

	internal void LogOperation(string operation, string details = "")
	{
		if (!_isEnabled())
			return;

		if (string.IsNullOrWhiteSpace(details))
			GD.Print($"[SystemExplorer] {operation}");
		else
			GD.Print($"[SystemExplorer] {operation} -> {details}");
	}
}
#endif
