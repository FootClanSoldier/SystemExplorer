#if TOOLS
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SystemExplorer.EditorIntegration.Operations;

namespace SystemExplorer.QuickActions.Beautify.CSharpier;

internal sealed class CSharpierProcessRunner
{
	internal async Task<CSharpierProcessResult> RunAsync(ProcessStartInfo startInfo, int timeoutMilliseconds, EditorOperationLease operation)
	{
		if (startInfo == null) throw new ArgumentNullException(nameof(startInfo));
		if (operation == null) throw new ArgumentNullException(nameof(operation));
		operation.CancellationToken.ThrowIfCancellationRequested();

		using Process process = new() { StartInfo = startInfo };
		if (!process.Start())
			return new CSharpierProcessResult(false, false, -1, "", "");

		if (!operation.TryRegisterProcess(process))
		{
			CSharpierProcessUtility.TryKillProcess(process);
			operation.CancellationToken.ThrowIfCancellationRequested();
			throw new OperationCanceledException(operation.CancellationToken);
		}

		Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
		Task<string> stderrTask = process.StandardError.ReadToEndAsync();
		using CancellationTokenSource timeout = new(timeoutMilliseconds);
		using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(operation.CancellationToken, timeout.Token);

		try
		{
			try
			{
				await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (timeout.IsCancellationRequested && !operation.CancellationToken.IsCancellationRequested)
			{
				CSharpierProcessUtility.TryKillProcess(process);
				await ObserveExitAsync(process).ConfigureAwait(false);
				return new CSharpierProcessResult(true, true, -1, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
			}
			catch (OperationCanceledException)
			{
				CSharpierProcessUtility.TryKillProcess(process);
				await ObserveExitAsync(process).ConfigureAwait(false);
				await ObserveOutputAsync(stdoutTask, stderrTask).ConfigureAwait(false);
				throw;
			}

			return new CSharpierProcessResult(true, false, process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
		}
		finally
		{
			operation.UnregisterProcess(process);
		}
	}

	private static async Task ObserveExitAsync(Process process)
	{
		try { await process.WaitForExitAsync().ConfigureAwait(false); } catch { }
	}

	private static async Task ObserveOutputAsync(Task<string> stdoutTask, Task<string> stderrTask)
	{
		try { await stdoutTask.ConfigureAwait(false); } catch { }
		try { await stderrTask.ConfigureAwait(false); } catch { }
	}
}
#endif
