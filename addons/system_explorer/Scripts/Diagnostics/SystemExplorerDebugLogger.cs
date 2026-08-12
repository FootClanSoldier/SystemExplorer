#if TOOLS
using Godot;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace SystemExplorer.Diagnostics;

internal sealed class SystemExplorerDebugLogger : IDisposable
{
	private const string DebugLogDirectory = "user://system_explorer/logs";

	private readonly Func<bool> _isEnabled;
	private readonly SystemExplorerPersistentLogFile _persistentLogFile = new();

	private bool _disposed;

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

		WriteEntry(message ?? "");
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

		string message = CreateOperationMessage(operation, details);
		WriteEntry(message);
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

	internal void LogPersistentFileOnlyOperation(string operation, string details = "")
	{
		if (!IsEnabled || _disposed)
			return;

		try
		{
			string message = CreateOperationMessage(operation, details);
			TryWriteFileEntry(message);
		}
		catch
		{
			// Timing-sensitive crash breadcrumbs are best-effort and must fail closed.
		}
	}

	internal Action<string, string> CreatePersistentFileOnlyDiagnosticSink()
	{
		if (!IsEnabled || _disposed)
			return null;

		EnsurePersistentFileSinkOpen();

		if (!_persistentLogFile.IsOpen)
			return null;

		var sink = new SystemExplorerPersistentDiagnosticSink(_persistentLogFile);
		return sink.LogOperation;
	}

	public void Dispose()
	{
		if (_disposed)
			return;

		_disposed = true;
		_persistentLogFile.DisposeBestEffort();
	}

	private void WriteEntry(string message)
	{
		TryWriteFileEntry(message ?? "");
	}

	private string TryWriteFileEntry(string message)
	{
		if (_disposed)
			return "";

		string openStatus = EnsurePersistentFileSinkOpen();
		if (!_persistentLogFile.IsOpen)
			return openStatus;

		string writeStatus = _persistentLogFile.TryWrite(message);
		if (!string.IsNullOrWhiteSpace(writeStatus))
			return writeStatus;

		return openStatus;
	}

	private string EnsurePersistentFileSinkOpen()
	{
		if (_disposed || _persistentLogFile.IsUnavailable)
			return "";
		if (_persistentLogFile.IsOpen)
			return "";

		try
		{
			string filePath = CreateProcessLogPath();
			return _persistentLogFile.EnsureOpen(filePath);
		}
		catch (Exception exception)
		{
			return _persistentLogFile.DisableAfterOpenFailure(exception);
		}
	}

	private static string CreateOperationMessage(string operation, string details)
	{
		return string.IsNullOrWhiteSpace(details)
			? operation ?? ""
			: $"{operation} -> {details}";
	}

	private static string CreateProcessLogPath()
	{
		string absoluteDirectory = ProjectSettings.GlobalizePath(DebugLogDirectory);
		using Process process = Process.GetCurrentProcess();
		DateTime processStartTime = process.StartTime;
		string fileName =
			$"system_explorer_debug_{processStartTime:yyyyMMdd_HHmmss_fffffff}_pid{System.Environment.ProcessId}.log";
		return Path.Combine(absoluteDirectory, fileName);
	}

}

internal sealed class SystemExplorerPersistentDiagnosticSink
{
	private readonly SystemExplorerPersistentLogFile _persistentLogFile;

	internal SystemExplorerPersistentDiagnosticSink(
		SystemExplorerPersistentLogFile persistentLogFile
	)
	{
		_persistentLogFile =
			persistentLogFile
			?? throw new ArgumentNullException(nameof(persistentLogFile));
	}

	internal void LogOperation(string operation, string details)
	{
		try
		{
			string message = string.IsNullOrWhiteSpace(details)
				? operation ?? ""
				: $"{operation} -> {details}";
			_persistentLogFile.TryWrite(message);
		}
		catch
		{
		}
	}
}

internal sealed class SystemExplorerPersistentLogFile
{
	private const string ConsolePrefix = "[SystemExplorer]";

	private readonly object _sync = new();

	private StreamWriter _writer;
	private string _filePath = "";
	private bool _unavailable;
	private bool _disposed;

	internal bool IsOpen
	{
		get
		{
			lock (_sync)
				return !_disposed && !_unavailable && _writer != null;
		}
	}

	internal bool IsUnavailable
	{
		get
		{
			lock (_sync)
				return _unavailable || _disposed;
		}
	}

	internal string EnsureOpen(string filePath)
	{
		lock (_sync)
		{
			if (_disposed || _unavailable || _writer != null)
				return "";

			try
			{
				_filePath = filePath ?? "";
				string directoryPath = Path.GetDirectoryName(_filePath) ?? "";
				if (string.IsNullOrWhiteSpace(directoryPath))
					throw new IOException("The debug log directory path could not be resolved.");

				Directory.CreateDirectory(directoryPath);
				var stream = new System.IO.FileStream(
					_filePath,
					System.IO.FileMode.Append,
					System.IO.FileAccess.Write,
					System.IO.FileShare.ReadWrite
				);
				_writer = new StreamWriter(stream, new UTF8Encoding(false));

				string startedMessage =
					$"System Explorer debug file logging started -> Path='{_filePath}'";
				WriteFileLineLocked(startedMessage);
				return startedMessage;
			}
			catch (Exception exception)
			{
				return DisableAfterFailureLocked(exception);
			}
		}
	}

	internal string DisableAfterOpenFailure(Exception exception)
	{
		lock (_sync)
			return DisableAfterFailureLocked(exception);
	}

	internal string TryWrite(string message)
	{
		lock (_sync)
		{
			if (_disposed || _unavailable || _writer == null)
				return "";

			try
			{
				WriteFileLineLocked(message ?? "");
				return "";
			}
			catch (Exception exception)
			{
				return DisableAfterFailureLocked(exception);
			}
		}
	}

	internal string DisposeBestEffort()
	{
		lock (_sync)
		{
			if (_disposed)
				return "";

			_disposed = true;

			try
			{
				_writer?.Dispose();
				return "";
			}
			catch (Exception exception)
			{
				return
					$"System Explorer debug file logging dispose failed -> Path='{_filePath}', Exception='{exception.GetType().Name}: {exception.Message}'";
			}
			finally
			{
				_writer = null;
			}
		}
	}

	private string DisableAfterFailureLocked(Exception exception)
	{
		_unavailable = true;
		try
		{
			_writer?.Dispose();
		}
		catch
		{
		}
		finally
		{
			_writer = null;
		}

		return
			$"System Explorer debug file logging disabled after failure -> Path='{_filePath}', Exception='{exception?.GetType().Name ?? "Unknown"}: {exception?.Message ?? "Unknown error."}'";
	}

	private void WriteFileLineLocked(string message)
	{
		string line =
			$"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] "
			+ $"[T{System.Environment.CurrentManagedThreadId}] "
			+ $"{ConsolePrefix} {NormalizePhysicalLine(message)}";
		_writer.WriteLine(line);
		_writer.Flush();
	}

	private static string NormalizePhysicalLine(string value)
	{
		if (string.IsNullOrEmpty(value))
			return "";

		return value.Replace("\r\n", " | ").Replace("\n", " | ").Replace("\r", " | ");
	}
}
#endif
