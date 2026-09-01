#if TOOLS
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;

namespace SystemExplorer.CodeService.Runtime;

internal sealed class CodeServiceProcessObservation : IDisposable
{
	private readonly object _exitWatchGate = new();
	private Process _process;
	private EventHandler _exitHandler;
	private Action _exitCallback;
	private bool _exitObservationArmed;
	private bool _exitObservationActivated;
	private bool _exitObserved;
	private bool _exitCallbackDispatched;
	private bool _disposed;

	private CodeServiceProcessObservation(
		Process process,
		CodeServiceProcessIdentity identity
	)
	{
		_process = process;
		Identity = identity;
	}

	internal CodeServiceProcessIdentity Identity { get; }

	internal static CodeServiceProcessObservationCreateResult TryCreate(
		CodeServiceProcessIdentity expectedIdentity
	)
	{
		if (expectedIdentity.ProcessId <= 0 || expectedIdentity.StartTimeUtcTicks <= 0)
		{
			return CodeServiceProcessObservationCreateResult.AmbiguousFailure(
				"Expected process identity was invalid."
			);
		}

		Process process;
		try
		{
			process = Process.GetProcessById(expectedIdentity.ProcessId);
		}
		catch (ArgumentException exception)
		{
			return CodeServiceProcessObservationCreateResult.DefinitelyUnavailable(
				ToSingleLine(exception.Message)
			);
		}
		catch (Exception exception) when (IsAmbiguousObservationFailure(exception))
		{
			return CodeServiceProcessObservationCreateResult.AmbiguousFailure(
				ToSingleLine(exception.Message)
			);
		}

		try
		{
			if (process.HasExited)
			{
				process.Dispose();
				return CodeServiceProcessObservationCreateResult.DefinitelyUnavailable(
					"Process had already exited."
				);
			}

			int observedProcessId = process.Id;
			long observedStartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;

			if (observedProcessId <= 0 || observedStartTimeUtcTicks <= 0)
			{
				process.Dispose();
				return CodeServiceProcessObservationCreateResult.AmbiguousFailure(
					"Observed process identity was invalid."
				);
			}

			if (
				observedProcessId != expectedIdentity.ProcessId
				|| observedStartTimeUtcTicks != expectedIdentity.StartTimeUtcTicks
			)
			{
				process.Dispose();
				return CodeServiceProcessObservationCreateResult.DefinitelyUnavailable(
					$"Process identity mismatch. ExpectedPid='{expectedIdentity.ProcessId}', ExpectedStartTimeUtcTicks='{expectedIdentity.StartTimeUtcTicks}', ObservedPid='{observedProcessId}', ObservedStartTimeUtcTicks='{observedStartTimeUtcTicks}'."
				);
			}

			if (process.HasExited)
			{
				process.Dispose();
				return CodeServiceProcessObservationCreateResult.DefinitelyUnavailable(
					"Process exited while exact identity was being verified."
				);
			}

			return CodeServiceProcessObservationCreateResult.Success(
				new CodeServiceProcessObservation(
					process,
					new CodeServiceProcessIdentity(
						observedProcessId,
						observedStartTimeUtcTicks
					)
				)
			);
		}
		catch (Exception exception) when (IsAmbiguousObservationFailure(exception))
		{
			process.Dispose();
			return CodeServiceProcessObservationCreateResult.AmbiguousFailure(
				ToSingleLine(exception.Message)
			);
		}
		catch
		{
			process.Dispose();
			throw;
		}
	}

	internal bool TryArmExitObservation(Action onExited, out string detail)
	{
		if (onExited == null)
			throw new ArgumentNullException(nameof(onExited));

		detail = "";
		Process process;
		EventHandler exitHandler;
		bool handlerAttached = false;
		bool armSucceeded = false;
		string failureDetail = "";

		lock (_exitWatchGate)
		{
			if (_disposed || _process == null)
			{
				detail = "Process observation was already disposed.";
				return false;
			}

			if (_exitObservationArmed)
			{
				detail = "Process exit observation was already armed.";
				return false;
			}

			process = _process;
			exitHandler = OnProcessExited;
			_exitHandler = exitHandler;
			_exitCallback = onExited;
			_exitObservationArmed = true;
			_exitObservationActivated = false;
			_exitObserved = false;
			_exitCallbackDispatched = false;

			try
			{
				process.Exited += exitHandler;
				handlerAttached = true;

				try
				{
					process.EnableRaisingEvents = true;
				}
				catch (Exception enableException)
				{
					if (_exitObserved || TryConfirmExited(process))
					{
						_exitObserved = true;
						armSucceeded = true;
					}
					else
					{
						failureDetail = FormatWatchFailure(
							"EnableRaisingEvents failed",
							enableException
						);
					}
				}

				if (!armSucceeded && string.IsNullOrEmpty(failureDetail))
				{
					try
					{
						if (process.HasExited)
							_exitObserved = true;
						armSucceeded = true;
					}
					catch (Exception hasExitedException)
					{
						if (_exitObserved)
						{
							armSucceeded = true;
						}
						else
						{
							failureDetail = FormatWatchFailure(
								"Post-arm exit verification failed",
								hasExitedException
							);
						}
					}
				}
			}
			catch (Exception setupException)
			{
				if (_exitObserved || TryConfirmExited(process))
				{
					_exitObserved = true;
					armSucceeded = true;
				}
				else
				{
					failureDetail = FormatWatchFailure(
						"Process exit observation setup failed",
						setupException
					);
				}
			}

			if (!armSucceeded)
				ResetExitObservationStateLocked();
		}

		if (armSucceeded)
			return true;

		// BCL event unregistration may interact with an already queued Exited callback.
		// Keep it outside the watch lock so callback retirement cannot deadlock on that lock.
		BestEffortDetachAndDisable(process, handlerAttached ? exitHandler : null);
		detail = failureDetail;
		return false;
	}

	internal void ActivateExitObservation()
	{
		Action callback = null;
		lock (_exitWatchGate)
		{
			if (_disposed || !_exitObservationArmed || _exitObservationActivated)
				return;

			_exitObservationActivated = true;
			callback = TakeExitCallbackForDispatchLocked();
		}

		InvokeExitCallbackSafely(callback);
	}

	internal void StopExitObservation()
	{
		StopExitObservationCore(markDisposed: false);
	}

	public void Dispose()
	{
		StopExitObservationCore(markDisposed: true);
		Process process = Interlocked.Exchange(ref _process, null);
		process?.Dispose();
	}

	private void OnProcessExited(object sender, EventArgs eventArgs)
	{
		Action callback = null;
		lock (_exitWatchGate)
		{
			if (_disposed || !_exitObservationArmed)
				return;

			_exitObserved = true;
			callback = TakeExitCallbackForDispatchLocked();
		}

		InvokeExitCallbackSafely(callback);
	}

	private Action TakeExitCallbackForDispatchLocked()
	{
		if (
			!_exitObservationArmed
			|| !_exitObservationActivated
			|| !_exitObserved
			|| _exitCallbackDispatched
			|| _exitCallback == null
		)
		{
			return null;
		}

		_exitCallbackDispatched = true;
		return _exitCallback;
	}

	private void StopExitObservationCore(bool markDisposed)
	{
		Process process;
		EventHandler exitHandler;
		lock (_exitWatchGate)
		{
			if (markDisposed)
				_disposed = true;

			process = _process;
			exitHandler = _exitHandler;
			ResetExitObservationStateLocked();
		}

		BestEffortDetachAndDisable(process, exitHandler);
	}

	private void ResetExitObservationStateLocked()
	{
		_exitObservationArmed = false;
		_exitObservationActivated = false;
		_exitCallback = null;
		_exitHandler = null;
		_exitObserved = false;
		_exitCallbackDispatched = false;
	}

	private static void BestEffortDetachAndDisable(Process process, EventHandler exitHandler)
	{
		if (process == null)
			return;

		if (exitHandler != null)
		{
			try
			{
				process.Exited -= exitHandler;
			}
			catch
			{
			}
		}

		try
		{
			process.EnableRaisingEvents = false;
		}
		catch
		{
		}
	}

	private static bool TryConfirmExited(Process process)
	{
		try
		{
			return process != null && process.HasExited;
		}
		catch
		{
			return false;
		}
	}

	private static void InvokeExitCallbackSafely(Action callback)
	{
		if (callback == null)
			return;

		try
		{
			callback();
		}
		catch
		{
			// Never allow a coordinator callback failure to escape the BCL Process.Exited thread.
		}
	}

	private static string FormatWatchFailure(string prefix, Exception exception)
	{
		string detail = ToSingleLine(exception?.Message);
		if (detail.Length > 512)
			detail = detail.Substring(0, 512);
		return string.IsNullOrEmpty(detail) ? prefix + "." : prefix + ": " + detail;
	}

	private static bool IsAmbiguousObservationFailure(Exception exception)
	{
		return exception is InvalidOperationException
			or Win32Exception
			or NotSupportedException
			or UnauthorizedAccessException;
	}

	private static string ToSingleLine(string message)
	{
		return (message ?? "").Replace('\r', ' ').Replace('\n', ' ');
	}
}

internal enum CodeServiceProcessObservationCreateStatus
{
	Success,
	DefinitelyUnavailable,
	AmbiguousFailure,
}

internal readonly struct CodeServiceProcessObservationCreateResult
{
	private CodeServiceProcessObservationCreateResult(
		CodeServiceProcessObservationCreateStatus status,
		CodeServiceProcessObservation observation,
		string detail
	)
	{
		Status = status;
		Observation = observation;
		Detail = detail ?? "";
	}

	internal CodeServiceProcessObservationCreateStatus Status { get; }
	internal CodeServiceProcessObservation Observation { get; }
	internal string Detail { get; }
	internal bool IsSuccess => Status == CodeServiceProcessObservationCreateStatus.Success;
	internal bool IsDefinitelyUnavailable =>
		Status == CodeServiceProcessObservationCreateStatus.DefinitelyUnavailable;

	internal static CodeServiceProcessObservationCreateResult Success(
		CodeServiceProcessObservation observation
	)
	{
		return new CodeServiceProcessObservationCreateResult(
			CodeServiceProcessObservationCreateStatus.Success,
			observation,
			""
		);
	}

	internal static CodeServiceProcessObservationCreateResult DefinitelyUnavailable(
		string detail
	)
	{
		return new CodeServiceProcessObservationCreateResult(
			CodeServiceProcessObservationCreateStatus.DefinitelyUnavailable,
			null,
			detail
		);
	}

	internal static CodeServiceProcessObservationCreateResult AmbiguousFailure(string detail)
	{
		return new CodeServiceProcessObservationCreateResult(
			CodeServiceProcessObservationCreateStatus.AmbiguousFailure,
			null,
			detail
		);
	}
}
#endif
