#if TOOLS
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SystemExplorer.CodeService.Client;

namespace SystemExplorer.CodeService.Runtime;

internal sealed class CodeServiceLaunchedProcess : IDisposable
{
	private static readonly UTF8Encoding StrictUtf8 = new(false, true);

	private Process _process;

	internal CodeServiceLaunchedProcess(
		Process process,
		bool serviceIdentityAvailable,
		CodeServiceProcessIdentity serviceIdentity,
		string identityDetail
	)
	{
		_process = process ?? throw new ArgumentNullException(nameof(process));
		ServiceIdentityAvailable = serviceIdentityAvailable;
		ServiceIdentity = serviceIdentity;
		IdentityDetail = identityDetail ?? "";
	}

	internal bool ServiceIdentityAvailable { get; }
	internal CodeServiceProcessIdentity ServiceIdentity { get; }
	internal string IdentityDetail { get; }

	internal async Task<CodeServiceBootstrapWaitResult> WaitForReadinessAsync(
		CodeServiceProcessIdentity ownerIdentity,
		string expectedServiceVersion,
		string expectedDescriptorPath,
		TimeSpan startupDeadline,
		CancellationToken cancellationToken
	)
	{
		if (startupDeadline <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(startupDeadline));

		Process process = Volatile.Read(ref _process)
			?? throw new ObjectDisposedException(nameof(CodeServiceLaunchedProcess));

		using CancellationTokenSource observationCancellation =
			CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

		Task<BoundedStdoutLineResult> lineTask = ReadOneBoundedUtf8LineAsync(
			process.StandardOutput.BaseStream,
			observationCancellation.Token
		);
		Task exitTask = process.WaitForExitAsync(observationCancellation.Token);
		Task deadlineTask = Task.Delay(startupDeadline, observationCancellation.Token);

		Task winner;
		try
		{
			winner = await Task.WhenAny(lineTask, exitTask, deadlineTask).ConfigureAwait(false);
		}
		catch
		{
			observationCancellation.Cancel();
			await ObserveTaskAsync(lineTask).ConfigureAwait(false);
			await ObserveTaskAsync(exitTask).ConfigureAwait(false);
			await ObserveTaskAsync(deadlineTask).ConfigureAwait(false);
			throw;
		}

		CodeServiceBootstrapWaitResult result;
		if (winner == lineTask)
		{
			BoundedStdoutLineResult lineResult;
			try
			{
				lineResult = await lineTask.ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				observationCancellation.Cancel();
				await ObserveTaskAsync(exitTask).ConfigureAwait(false);
				await ObserveTaskAsync(deadlineTask).ConfigureAwait(false);
				throw;
			}
			catch (Exception exception)
			{
				result = CodeServiceBootstrapWaitResult.Invalid(
					"bootstrap stdout could not be read: " + ToSingleLine(exception.Message)
				);
				observationCancellation.Cancel();
				await ObserveTaskAsync(exitTask).ConfigureAwait(false);
				await ObserveTaskAsync(deadlineTask).ConfigureAwait(false);
				return result;
			}

			if (!lineResult.IsSuccess)
			{
				result = CodeServiceBootstrapWaitResult.Invalid(lineResult.Detail);
			}
			else
			{
				CodeServiceBootstrapReadinessParseResult parseResult =
					CodeServiceBootstrapReadiness.TryParse(
						lineResult.Line,
						ownerIdentity,
						expectedServiceVersion,
						expectedDescriptorPath,
						ServiceIdentityAvailable ? ServiceIdentity : null
					);

				result = parseResult.IsSuccess
					? CodeServiceBootstrapWaitResult.Ready(parseResult.Record)
					: CodeServiceBootstrapWaitResult.Invalid(parseResult.Detail);
			}
		}
		else if (winner == exitTask)
		{
			try
			{
				await exitTask.ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				observationCancellation.Cancel();
				await ObserveTaskAsync(lineTask).ConfigureAwait(false);
				await ObserveTaskAsync(deadlineTask).ConfigureAwait(false);
				throw;
			}

			int? exitCode = TryGetExitCode(process);
			result = CodeServiceBootstrapWaitResult.ProcessExited(exitCode);
		}
		else
		{
			if (cancellationToken.IsCancellationRequested)
			{
				observationCancellation.Cancel();
				await ObserveTaskAsync(lineTask).ConfigureAwait(false);
				await ObserveTaskAsync(exitTask).ConfigureAwait(false);
				throw new OperationCanceledException(cancellationToken);
			}

			result = CodeServiceBootstrapWaitResult.TimedOut();
		}

		observationCancellation.Cancel();
		await ObserveTaskAsync(lineTask).ConfigureAwait(false);
		await ObserveTaskAsync(exitTask).ConfigureAwait(false);
		await ObserveTaskAsync(deadlineTask).ConfigureAwait(false);
		return result;
	}

	internal bool TryCheckExited(out bool hasExited, out string detail)
	{
		hasExited = false;
		detail = "";
		Process process = Volatile.Read(ref _process);
		if (process == null)
		{
			detail = "Launch process handle has been disposed.";
			return false;
		}

		try
		{
			hasExited = process.HasExited;
			return true;
		}
		catch (Exception exception)
		{
			detail = ToSingleLine(exception.Message);
			return false;
		}
	}

	public void Dispose()
	{
		Process process = Interlocked.Exchange(ref _process, null);
		if (process == null)
			return;

		try
		{
			process.StandardOutput.Dispose();
		}
		catch
		{
		}
		process.Dispose();
	}

	private static async Task<BoundedStdoutLineResult> ReadOneBoundedUtf8LineAsync(
		Stream stream,
		CancellationToken cancellationToken
	)
	{
		byte[] buffer = new byte[1024];
		using MemoryStream lineBytes = new();

		while (true)
		{
			int read = await stream
				.ReadAsync(buffer, 0, buffer.Length, cancellationToken)
				.ConfigureAwait(false);
			if (read == 0)
			{
				return lineBytes.Length == 0
					? BoundedStdoutLineResult.Invalid("bootstrap stdout closed before a readiness line was written.")
					: BoundedStdoutLineResult.Invalid("bootstrap readiness line ended without a newline terminator.");
			}

			for (int index = 0; index < read; index++)
			{
				byte value = buffer[index];
				if (value == (byte)'\n')
				{
					byte[] bytes = lineBytes.ToArray();
					int length = bytes.Length;
					if (length > 0 && bytes[length - 1] == (byte)'\r')
						length--;

					try
					{
						return BoundedStdoutLineResult.Success(
							StrictUtf8.GetString(bytes, 0, length)
						);
					}
					catch (DecoderFallbackException)
					{
						return BoundedStdoutLineResult.Invalid("bootstrap readiness line was not valid UTF-8.");
					}
				}

				if (lineBytes.Length >= CodeServiceClientProtocol.MaxReadinessLineBytes)
					return BoundedStdoutLineResult.Invalid("bootstrap readiness line exceeded the 16 KiB boundary.");

				lineBytes.WriteByte(value);
			}
		}
	}

	private static int? TryGetExitCode(Process process)
	{
		try
		{
			return process.ExitCode;
		}
		catch
		{
			return null;
		}
	}

	private static async Task ObserveTaskAsync(Task task)
	{
		try
		{
			await task.ConfigureAwait(false);
		}
		catch
		{
		}
	}

	private static async Task ObserveTaskAsync<T>(Task<T> task)
	{
		try
		{
			await task.ConfigureAwait(false);
		}
		catch
		{
		}
	}

	private static string ToSingleLine(string message)
	{
		return (message ?? "").Replace('\r', ' ').Replace('\n', ' ');
	}

	private readonly struct BoundedStdoutLineResult
	{
		private BoundedStdoutLineResult(bool isSuccess, string line, string detail)
		{
			IsSuccess = isSuccess;
			Line = line ?? "";
			Detail = detail ?? "";
		}

		internal bool IsSuccess { get; }
		internal string Line { get; }
		internal string Detail { get; }

		internal static BoundedStdoutLineResult Success(string line)
		{
			return new BoundedStdoutLineResult(true, line, "");
		}

		internal static BoundedStdoutLineResult Invalid(string detail)
		{
			return new BoundedStdoutLineResult(false, "", detail);
		}
	}
}

internal enum CodeServiceBootstrapWaitStatus
{
	Ready,
	ProcessExited,
	TimedOut,
	Invalid,
}

internal readonly struct CodeServiceBootstrapWaitResult
{
	private CodeServiceBootstrapWaitResult(
		CodeServiceBootstrapWaitStatus status,
		CodeServiceBootstrapReadinessRecord record,
		int? exitCode,
		string detail
	)
	{
		Status = status;
		Record = record;
		ExitCode = exitCode;
		Detail = detail ?? "";
	}

	internal CodeServiceBootstrapWaitStatus Status { get; }
	internal CodeServiceBootstrapReadinessRecord Record { get; }
	internal int? ExitCode { get; }
	internal string Detail { get; }

	internal static CodeServiceBootstrapWaitResult Ready(
		CodeServiceBootstrapReadinessRecord record
	)
	{
		return new CodeServiceBootstrapWaitResult(
			CodeServiceBootstrapWaitStatus.Ready,
			record,
			null,
			""
		);
	}

	internal static CodeServiceBootstrapWaitResult ProcessExited(int? exitCode)
	{
		return new CodeServiceBootstrapWaitResult(
			CodeServiceBootstrapWaitStatus.ProcessExited,
			default,
			exitCode,
			""
		);
	}

	internal static CodeServiceBootstrapWaitResult TimedOut()
	{
		return new CodeServiceBootstrapWaitResult(
			CodeServiceBootstrapWaitStatus.TimedOut,
			default,
			null,
			"bootstrap readiness deadline elapsed."
		);
	}

	internal static CodeServiceBootstrapWaitResult Invalid(string detail)
	{
		return new CodeServiceBootstrapWaitResult(
			CodeServiceBootstrapWaitStatus.Invalid,
			default,
			null,
			detail
		);
	}
}
#endif
