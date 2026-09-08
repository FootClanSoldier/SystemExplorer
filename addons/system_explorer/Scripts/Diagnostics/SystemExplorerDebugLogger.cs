#if TOOLS
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace SystemExplorer.Diagnostics;

internal sealed class SystemExplorerDebugLogger : IDisposable
{
	private readonly Func<bool> _isEnabled;
	private readonly SystemExplorerPersistentLogFile _persistentLogFile = new();

	private int _disposed;

	internal SystemExplorerDebugLogger(Func<bool> isEnabled)
	{
		_isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
	}

	internal bool IsEnabled
	{
		get
		{
			if (Volatile.Read(ref _disposed) != 0)
				return false;

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

		WriteEntry(CreateOperationMessage(operation, details));
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

	internal Action<string, string> CreatePersistentFileOnlyDiagnosticSink()
	{
		if (!IsEnabled)
			return null;

		EnsurePersistentFileSinkOpen();
		if (!_persistentLogFile.IsOpen)
			return null;

		var sink = new SystemExplorerPersistentDiagnosticSink(_persistentLogFile);
		return sink.LogOperation;
	}

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
			return;

		_persistentLogFile.DisposeBestEffort();
	}

	private void WriteEntry(string message)
	{
		if (Volatile.Read(ref _disposed) != 0)
			return;

		EnsurePersistentFileSinkOpen();
		_persistentLogFile.TryWrite(message ?? "");
	}

	private void EnsurePersistentFileSinkOpen()
	{
		if (Volatile.Read(ref _disposed) != 0 || _persistentLogFile.IsUnavailable)
			return;
		if (_persistentLogFile.IsOpen)
			return;

		try
		{
			_persistentLogFile.EnsureOpen(CreateProcessLogPath());
		}
		catch (Exception exception)
		{
			_persistentLogFile.DisableAfterOpenFailure(exception);
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
		string localApplicationData = Environment.GetFolderPath(
			Environment.SpecialFolder.LocalApplicationData
		);
		if (string.IsNullOrWhiteSpace(localApplicationData))
			throw new IOException("The LocalApplicationData diagnostics root could not be resolved.");

		string absoluteDirectory = Path.Combine(
			localApplicationData,
			"SystemExplorer",
			"Diagnostics",
			"GodotPlugin"
		);
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
			// Captured async diagnostic sinks are logging-only and fail closed.
		}
	}
}

internal sealed class SystemExplorerPersistentLogFile
{
	private const string LogPrefix = "[SystemExplorer]";

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

	internal void EnsureOpen(string filePath)
	{
		lock (_sync)
		{
			if (_disposed || _unavailable || _writer != null)
				return;

			try
			{
				_filePath = filePath ?? "";
				string directoryPath = Path.GetDirectoryName(_filePath) ?? "";
				if (string.IsNullOrWhiteSpace(directoryPath))
					throw new IOException("The debug log directory path could not be resolved.");

				Directory.CreateDirectory(directoryPath);
				FileStream stream = null;
				try
				{
					 stream = new System.IO.FileStream(
					_filePath,
					System.IO.FileMode.Append,
					System.IO.FileAccess.Write,
					System.IO.FileShare.ReadWrite
					);
					_writer = new StreamWriter(stream, new UTF8Encoding(false));
					stream = null;

					WriteFileLineLocked(
						$"System Explorer debug file logging started -> Path='{_filePath}'"
					);
				}
				finally
				{
					stream?.Dispose();
				}
			}
			catch
			{
				DisableAfterFailureLocked();
			}
		}
	}

	internal void DisableAfterOpenFailure(Exception _)
	{
		lock (_sync)
			DisableAfterFailureLocked();
	}

	internal void TryWrite(string message)
	{
		lock (_sync)
		{
			if (_disposed || _unavailable || _writer == null)
				return;

			try
			{
				WriteFileLineLocked(message ?? "");
			}
			catch
			{
				DisableAfterFailureLocked();
			}
		}
	}

	internal void DisposeBestEffort()
	{
		lock (_sync)
		{
			if (_disposed)
				return;

			_disposed = true;
			try
			{
				_writer?.Dispose();
			}
			catch
			{
				// Persistent debug logging is best-effort and has no Godot fallback.
			}
			finally
			{
				_writer = null;
			}
		}
	}

	private void DisableAfterFailureLocked()
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
	}

	private void WriteFileLineLocked(string message)
	{
		string line =
			$"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] "
			+ $"[T{System.Environment.CurrentManagedThreadId}] "
			+ $"{LogPrefix} {NormalizePhysicalLine(message)}";
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
