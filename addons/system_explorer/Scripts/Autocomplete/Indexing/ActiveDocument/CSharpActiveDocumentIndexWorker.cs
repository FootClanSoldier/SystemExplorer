#if TOOLS
using System;
using System.Diagnostics;
using System.Threading;
using SystemExplorer.Autocomplete.Indexing;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.Autocomplete.Indexing.ActiveDocument;

internal sealed class CSharpActiveDocumentIndexWorker
{
	private readonly RoslynProjectTypeScanner _typeScanner;

	internal CSharpActiveDocumentIndexWorker(RoslynProjectTypeScanner typeScanner)
	{
		_typeScanner = typeScanner ?? throw new ArgumentNullException(nameof(typeScanner));
	}

	internal CSharpActiveDocumentIndexBuildResult Build(
		CSharpActiveDocumentIndexRequest request,
		CancellationToken cancellationToken
	)
	{
		ArgumentNullException.ThrowIfNull(request);
		var stopwatch = Stopwatch.StartNew();

		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			string scriptPath = ScriptPathUtility.Normalize(request.ScriptPath);

			if (!IsCSharpScriptPath(scriptPath))
			{
				return CreateFailedResult(
					request,
					scriptPath,
					stopwatch,
					"Active document script path is not a valid C# resource path."
				);
			}

			CSharpDocumentTypeScanResult scanResult = _typeScanner.ScanDocument(
				scriptPath,
				request.SourceText ?? "",
				cancellationToken
			);
			cancellationToken.ThrowIfCancellationRequested();

			var snapshot = new CSharpActiveDocumentIndexSnapshot(
				request.Revision,
				scriptPath,
				scanResult.Types,
				scanResult.SyntaxDiagnosticCount,
				scanResult.CompletionContext,
				hasBuiltAtLeastOnce: true
			);

			stopwatch.Stop();
			return new CSharpActiveDocumentIndexBuildResult(
				request.Revision,
				request.Reason,
				scriptPath,
				CSharpActiveDocumentIndexBuildStatus.Succeeded,
				stopwatch.Elapsed,
				snapshot.Types.Count,
				snapshot.SyntaxDiagnosticCount,
				failureDetail: "",
				snapshot: snapshot
			);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			stopwatch.Stop();
			return new CSharpActiveDocumentIndexBuildResult(
				request.Revision,
				request.Reason,
				ScriptPathUtility.Normalize(request.ScriptPath),
				CSharpActiveDocumentIndexBuildStatus.Cancelled,
				stopwatch.Elapsed,
				0,
				0,
				"Build cancellation was requested.",
				snapshot: null
			);
		}
		catch (Exception exception)
		{
			return CreateFailedResult(
				request,
				ScriptPathUtility.Normalize(request.ScriptPath),
				stopwatch,
				CreateExceptionDetail("Unexpected active-document index failure", exception)
			);
		}
	}

	private static CSharpActiveDocumentIndexBuildResult CreateFailedResult(
		CSharpActiveDocumentIndexRequest request,
		string scriptPath,
		Stopwatch stopwatch,
		string failureDetail
	)
	{
		if (stopwatch.IsRunning)
			stopwatch.Stop();

		return new CSharpActiveDocumentIndexBuildResult(
			request.Revision,
			request.Reason,
			scriptPath,
			CSharpActiveDocumentIndexBuildStatus.Failed,
			stopwatch.Elapsed,
			0,
			0,
			failureDetail,
			snapshot: null
		);
	}

	private static bool IsCSharpScriptPath(string scriptPath)
	{
		return !string.IsNullOrWhiteSpace(scriptPath)
			&& scriptPath.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
			&& scriptPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
	}

	private static string CreateExceptionDetail(string prefix, Exception exception)
	{
		string message = exception?.Message ?? "Unknown error.";
		message = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
		if (message.Length > 500)
			message = message.Substring(0, 500);

		return $"{prefix}: {exception?.GetType().Name ?? "Exception"}: {message}";
	}
}
#endif
