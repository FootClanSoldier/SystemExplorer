#if TOOLS
using System;
using System.Diagnostics;

namespace SystemExplorer.CodeService.Runtime;

internal readonly struct CodeServiceProcessIdentity
{
	internal CodeServiceProcessIdentity(int processId, long startTimeUtcTicks)
	{
		ProcessId = processId;
		StartTimeUtcTicks = startTimeUtcTicks;
	}

	internal int ProcessId { get; }
	internal long StartTimeUtcTicks { get; }

	internal static bool TryGetCurrent(
		out CodeServiceProcessIdentity identity,
		out string detail
	)
	{
		try
		{
			using Process process = Process.GetCurrentProcess();
			return TryRead(process, out identity, out detail);
		}
		catch (Exception exception)
		{
			identity = default;
			detail = exception.Message;
			return false;
		}
	}

	internal static bool TryGetByProcessId(
		int processId,
		out CodeServiceProcessIdentity identity,
		out bool definitelyUnavailable,
		out string detail
	)
	{
		identity = default;
		definitelyUnavailable = false;
		detail = "";

		if (processId <= 0)
		{
			detail = "Process ID must be greater than zero.";
			return false;
		}

		Process process;
		try
		{
			process = Process.GetProcessById(processId);
		}
		catch (ArgumentException exception)
		{
			definitelyUnavailable = true;
			detail = exception.Message;
			return false;
		}
		catch (Exception exception)
		{
			detail = exception.Message;
			return false;
		}

		using (process)
		{
			try
			{
				if (process.HasExited)
				{
					definitelyUnavailable = true;
					detail = "Process has already exited.";
					return false;
				}

				int observedProcessId = process.Id;
				long startTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;

				if (process.HasExited)
				{
					definitelyUnavailable = true;
					detail = "Process exited while its identity was being observed.";
					return false;
				}

				if (observedProcessId <= 0 || startTimeUtcTicks <= 0)
				{
					detail = "Process identity was not valid.";
					return false;
				}

				identity = new CodeServiceProcessIdentity(observedProcessId, startTimeUtcTicks);
				return true;
			}
			catch (Exception exception)
			{
				detail = exception.Message;
				return false;
			}
		}
	}

	internal static bool TryRead(
		Process process,
		out CodeServiceProcessIdentity identity,
		out string detail
	)
	{
		identity = default;
		detail = "";

		if (process == null)
		{
			detail = "Process is unavailable.";
			return false;
		}

		try
		{
			if (process.HasExited)
			{
				detail = "Process has already exited.";
				return false;
			}

			int processId = process.Id;
			long startTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;

			if (processId <= 0 || startTimeUtcTicks <= 0)
			{
				detail = "Process identity was not valid.";
				return false;
			}

			if (process.HasExited)
			{
				detail = "Process exited while its identity was being observed.";
				return false;
			}

			identity = new CodeServiceProcessIdentity(processId, startTimeUtcTicks);
			return true;
		}
		catch (Exception exception)
		{
			detail = exception.Message;
			return false;
		}
	}
}
#endif
