#if TOOLS
using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SystemExplorer.CodeService.Runtime;

internal sealed class CodeServiceProcessLauncher
{
	internal CodeServiceLaunchResult Start(
		string executable,
		string workingDirectory,
		CodeServiceProcessIdentity godotOwnerIdentity,
		bool diagnosticLoggingRequested
	)
	{
		if (string.IsNullOrWhiteSpace(executable))
			return CodeServiceLaunchResult.Failure("CodeService executable was not resolved.");

		ProcessStartInfo startInfo = new()
		{
			FileName = executable,
			WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
				? Environment.CurrentDirectory
				: workingDirectory,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = false,
			StandardOutputEncoding = new UTF8Encoding(false),
		};

		startInfo.ArgumentList.Add("server");
		startInfo.ArgumentList.Add("--godot-pid");
		startInfo.ArgumentList.Add(
			godotOwnerIdentity.ProcessId.ToString(CultureInfo.InvariantCulture)
		);
		startInfo.ArgumentList.Add("--godot-start-time-utc-ticks");
		startInfo.ArgumentList.Add(
			godotOwnerIdentity.StartTimeUtcTicks.ToString(CultureInfo.InvariantCulture)
		);
		if (diagnosticLoggingRequested)
			startInfo.ArgumentList.Add("--diagnostic-log");

		Process process = new() { StartInfo = startInfo };
		try
		{
			if (!process.Start())
			{
				process.Dispose();
				return CodeServiceLaunchResult.Failure("Process.Start returned false.");
			}
		}
		catch (Exception exception)
		{
			process.Dispose();
			return CodeServiceLaunchResult.Failure(exception.Message);
		}

		bool identityAvailable = CodeServiceProcessIdentity.TryRead(
			process,
			out CodeServiceProcessIdentity serviceIdentity,
			out string identityDetail
		);

		return CodeServiceLaunchResult.Success(
			new CodeServiceLaunchedProcess(
				process,
				identityAvailable,
				serviceIdentity,
				identityDetail
			)
		);
	}
}
#endif
