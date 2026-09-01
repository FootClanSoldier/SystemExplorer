#if TOOLS
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SystemExplorer.EditorIntegration.Operations;

namespace SystemExplorer.CodeService.Installation;

internal sealed class CodeServiceProcessRunner
{
	internal async Task<CodeServiceProcessResult> RunAsync(
		ProcessStartInfo startInfo,
		int timeoutMilliseconds,
		EditorOperationLease operation
	)
	{
		if (startInfo == null)
			throw new ArgumentNullException(nameof(startInfo));
		if (timeoutMilliseconds <= 0)
			throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
		if (operation == null)
			throw new ArgumentNullException(nameof(operation));

		operation.CancellationToken.ThrowIfCancellationRequested();

		using Process process = new() { StartInfo = startInfo };
		try
		{
			if (!process.Start())
				return new CodeServiceProcessResult(false, false, -1, "", "");
		}
		catch (Exception exception) when (!operation.CancellationToken.IsCancellationRequested)
		{
			return new CodeServiceProcessResult(
				false,
				false,
				-1,
				"",
				exception.Message
			);
		}

		if (!operation.TryRegisterProcess(process))
		{
			TryKillProcess(process);
			await ObserveExitAsync(process).ConfigureAwait(false);
			operation.CancellationToken.ThrowIfCancellationRequested();
			throw new OperationCanceledException(operation.CancellationToken);
		}

		Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
		Task<string> stderrTask = process.StandardError.ReadToEndAsync();
		using CancellationTokenSource timeout = new(timeoutMilliseconds);
		using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
			operation.CancellationToken,
			timeout.Token
		);

		try
		{
			try
			{
				await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (
				timeout.IsCancellationRequested
				&& !operation.CancellationToken.IsCancellationRequested
			)
			{
				TryKillProcess(process);
				await ObserveExitAsync(process).ConfigureAwait(false);
				return new CodeServiceProcessResult(
					true,
					true,
					-1,
					await ObserveOutputAsync(stdoutTask).ConfigureAwait(false),
					await ObserveOutputAsync(stderrTask).ConfigureAwait(false)
				);
			}
			catch (OperationCanceledException)
			{
				TryKillProcess(process);
				await ObserveExitAsync(process).ConfigureAwait(false);
				await ObserveOutputAsync(stdoutTask, stderrTask).ConfigureAwait(false);
				throw;
			}

			return new CodeServiceProcessResult(
				true,
				false,
				process.ExitCode,
				await stdoutTask.ConfigureAwait(false),
				await stderrTask.ConfigureAwait(false)
			);
		}
		finally
		{
			operation.UnregisterProcess(process);
		}
	}

	private static void TryKillProcess(Process process)
	{
		if (process == null)
			return;

		try
		{
			process.Kill(entireProcessTree: true);
		}
		catch
		{
			// Best-effort cleanup only.
		}
	}

	private static async Task ObserveExitAsync(Process process)
	{
		try
		{
			await process.WaitForExitAsync().ConfigureAwait(false);
		}
		catch
		{
		}
	}

	private static async Task<string> ObserveOutputAsync(Task<string> outputTask)
	{
		try
		{
			return await outputTask.ConfigureAwait(false);
		}
		catch
		{
			return "";
		}
	}

	private static async Task ObserveOutputAsync(
		Task<string> stdoutTask,
		Task<string> stderrTask
	)
	{
		await ObserveOutputAsync(stdoutTask).ConfigureAwait(false);
		await ObserveOutputAsync(stderrTask).ConfigureAwait(false);
	}
}
#endif
