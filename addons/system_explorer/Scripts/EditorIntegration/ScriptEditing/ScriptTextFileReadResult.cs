#if TOOLS
using System;

namespace SystemExplorer.EditorIntegration.ScriptEditing;

internal enum ScriptTextFileReadStatus
{
	Uninitialized = 0,
	Success,
	InvalidPath,
	MissingFile,
	ReadFailed,
}

internal readonly struct ScriptTextFileReadResult
{
	private readonly ScriptTextFileReadStatus _status;
	private readonly string _text;
	private readonly string _failureDetail;

	private ScriptTextFileReadResult(
		ScriptTextFileReadStatus status,
		string text,
		string failureDetail
	)
	{
		_status = status;
		_text = text ?? "";
		_failureDetail = failureDetail ?? "";
	}

	internal ScriptTextFileReadStatus Status => IsKnownStatus(_status)
		? _status
		: ScriptTextFileReadStatus.Uninitialized;

	internal bool IsSuccess => Status == ScriptTextFileReadStatus.Success;

	internal string Text => _text ?? "";

	internal string FailureDetail => _failureDetail ?? "";

	internal static ScriptTextFileReadResult Succeeded(string text)
	{
		return new ScriptTextFileReadResult(
			ScriptTextFileReadStatus.Success,
			text ?? "",
			""
		);
	}

	internal static ScriptTextFileReadResult Failed(
		ScriptTextFileReadStatus status,
		string failureDetail = ""
	)
	{
		if (status == ScriptTextFileReadStatus.Success)
			throw new ArgumentException("Success cannot be used as a failure status.", nameof(status));

		ScriptTextFileReadStatus safeStatus = IsKnownStatus(status)
			? status
			: ScriptTextFileReadStatus.Uninitialized;

		return new ScriptTextFileReadResult(safeStatus, "", failureDetail ?? "");
	}

	private static bool IsKnownStatus(ScriptTextFileReadStatus status)
	{
		return status switch
		{
			ScriptTextFileReadStatus.Uninitialized => true,
			ScriptTextFileReadStatus.Success => true,
			ScriptTextFileReadStatus.InvalidPath => true,
			ScriptTextFileReadStatus.MissingFile => true,
			ScriptTextFileReadStatus.ReadFailed => true,
			_ => false,
		};
	}
}
#endif
