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

	internal bool IsEnabled
	{
		get
		{
			try
			{
				return _isEnabled();
			}
			catch
			{
				return false;
			}
		}
	}

	internal void Log(string message)
	{
		if (!IsEnabled)
			return;

		GD.Print($"[SystemExplorer] {message}");
	}

	internal void Log(Func<string> messageFactory)
	{
		if (!IsEnabled)
			return;

		try
		{
			Log(messageFactory?.Invoke() ?? "");
		}
		catch (Exception exception)
		{
			Log($"DiagnosticReadFailed: {exception.GetType().Name}: {exception.Message}");
		}
	}

	internal void LogOperation(string operation, string details = "")
	{
		if (!IsEnabled)
			return;

		if (string.IsNullOrWhiteSpace(details))
			GD.Print($"[SystemExplorer] {operation}");
		else
			GD.Print($"[SystemExplorer] {operation} -> {details}");
	}

	internal void LogOperation(string operation, Func<string> detailsFactory)
	{
		if (!IsEnabled)
			return;

		try
		{
			LogOperation(operation, detailsFactory?.Invoke() ?? "");
		}
		catch (Exception exception)
		{
			LogOperation(
				operation,
				$"DiagnosticReadFailed: {exception.GetType().Name}: {exception.Message}"
			);
		}
	}
}
#endif
