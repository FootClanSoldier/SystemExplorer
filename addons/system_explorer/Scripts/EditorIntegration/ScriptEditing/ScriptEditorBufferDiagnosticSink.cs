#if TOOLS
using Godot;
using System;

namespace SystemExplorer.EditorIntegration.ScriptEditing;

internal sealed class ScriptEditorBufferDiagnosticSink
{
	private readonly Func<bool> _isEnabled;
	private readonly Action<string, string> _log;
	private readonly Func<string, string> _summarizeText;

	internal ScriptEditorBufferDiagnosticSink(
		Func<bool> isEnabled,
		Action<string, string> log,
		Func<string, string> summarizeText
	)
	{
		_isEnabled = isEnabled ?? throw new ArgumentNullException(nameof(isEnabled));
		_log = log ?? throw new ArgumentNullException(nameof(log));
		_summarizeText = summarizeText ?? throw new ArgumentNullException(nameof(summarizeText));
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

	internal void Log(string phase, Func<string> detailsFactory)
	{
		if (!IsEnabled)
			return;

		string details;

		try
		{
			details = detailsFactory?.Invoke() ?? "";
		}
		catch (Exception exception)
		{
			details = $"DiagnosticReadFailed: {exception.GetType().Name}: {exception.Message}";
		}

		try
		{
			_log(phase ?? "BufferDiagnostics", details);
		}
		catch
		{
			// Diagnostics must never escape into the operation being observed.
		}
	}

	internal string DescribeText(string text)
	{
		if (!IsEnabled)
			return "<debug-disabled>";

		try
		{
			return _summarizeText(text ?? "");
		}
		catch (Exception exception)
		{
			return $"DiagnosticReadFailed({exception.GetType().Name})";
		}
	}

	internal string DescribeTextEditor(
		TextEdit textEditor,
		string memberPath,
		bool isCurrentEditor,
		int memberNumber
	)
	{
		if (!IsEnabled)
			return "<debug-disabled>";

		if (textEditor == null)
		{
			return $"Member={memberNumber}; Path='{memberPath ?? ""}'; IsCurrent={isCurrentEditor}; TextEditNull=true; InstanceValid=false";
		}

		bool instanceValid = false;
		string validityError = "";

		try
		{
			instanceValid = GodotObject.IsInstanceValid(textEditor);
		}
		catch (Exception exception)
		{
			validityError = exception.GetType().Name;
		}

		string version = ReadDiagnosticValue(() => textEditor.GetVersion().ToString());
		string savedVersion = ReadDiagnosticValue(() => textEditor.GetSavedVersion().ToString());
		string unsaved = ReadDiagnosticValue(
			() => ScriptEditorBufferStateService.IsUnsaved(textEditor).ToString()
		);
		string textSummary = ReadDiagnosticValue(
			() => DescribeText(textEditor.Text ?? "")
		);

		return
			$"Member={memberNumber}; Path='{memberPath ?? ""}'; IsCurrent={isCurrentEditor}; TextEditNull=false; InstanceValid={instanceValid}; ValidityReadError='{validityError}'; Version={version}; SavedVersion={savedVersion}; IsUnsaved={unsaved}; Text={textSummary}";
	}

	private static string ReadDiagnosticValue(Func<string> valueFactory)
	{
		try
		{
			return valueFactory?.Invoke() ?? "<null>";
		}
		catch (Exception exception)
		{
			return $"<DiagnosticReadFailed:{exception.GetType().Name}>";
		}
	}
}
#endif
