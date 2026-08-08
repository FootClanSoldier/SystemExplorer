#if TOOLS
using Godot;
using System;

namespace SystemExplorer.Autocomplete.Confirmation;

internal sealed class AutocompleteCompletionConfirmationBridge
{
	private const string AcceptAction = "ui_text_completion_accept";
	private const string ReplaceAction = "ui_text_completion_replace";
	private const string DefaultValueKey = "default_value";

	private readonly AutocompleteCompletionOptionMetadataCodec _metadataCodec;
	private readonly AutocompleteProjectTypeConfirmationService _projectTypeConfirmationService;
	private readonly Action<string, string> _debugLog;

	internal AutocompleteCompletionConfirmationBridge(
		AutocompleteCompletionOptionMetadataCodec metadataCodec,
		AutocompleteProjectTypeConfirmationService projectTypeConfirmationService,
		Action<string, string> debugLog
	)
	{
		_metadataCodec =
			metadataCodec ?? throw new ArgumentNullException(nameof(metadataCodec));
		_projectTypeConfirmationService =
			projectTypeConfirmationService
			?? throw new ArgumentNullException(nameof(projectTypeConfirmationService));
		_debugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
	}

	internal bool TryHandleGuiInput(CodeEdit codeEdit, InputEvent inputEvent)
	{
		if (codeEdit == null || inputEvent == null)
			return false;
		if (inputEvent is not InputEventKey keyEvent)
			return false;

		bool confirmationSucceeded = false;

		try
		{
			if (
				!IsValidGodotObject(codeEdit)
				|| !IsValidGodotObject(keyEvent)
				|| !keyEvent.Pressed
			)
			{
				return false;
			}

			bool replace = keyEvent.IsAction(ReplaceAction, true);
			bool accept = keyEvent.IsAction(AcceptAction, true);

			if (!replace && !accept)
				return false;

			int selectedIndex = codeEdit.GetCodeCompletionSelectedIndex();
			if (selectedIndex < 0)
				return false;

			Godot.Collections.Dictionary option =
				codeEdit.GetCodeCompletionOption(selectedIndex);
			if (
				option == null
				|| !option.TryGetValue(DefaultValueKey, out Variant defaultValue)
				|| !_metadataCodec.TryDecode(
					defaultValue,
					out AutocompleteCompletionOptionMetadata metadata
				)
				|| !string.Equals(
					metadata.Owner,
					AutocompleteCompletionOptionMetadata.SystemExplorerOwner,
					StringComparison.Ordinal
				)
				|| !string.Equals(
					metadata.Source,
					AutocompleteCompletionOptionMetadata.ProjectTypeSource,
					StringComparison.Ordinal
				)
			)
			{
				return false;
			}

			AutocompleteProjectTypeConfirmationResult result =
				_projectTypeConfirmationService.Confirm(codeEdit, metadata, replace);
			confirmationSucceeded = result?.ConfirmationSucceeded == true;
			if (!confirmationSucceeded)
				return false;

			codeEdit.AcceptEvent();
			LogCompletion(
				metadata,
				selectedIndex,
				replace,
				result.EffectiveReplace,
				result.UsingAction
			);
			return true;
		}
		catch (Exception exception)
		{
			LogFailure(exception, confirmationSucceeded);
			return confirmationSucceeded;
		}
	}

	private void LogCompletion(
		AutocompleteCompletionOptionMetadata metadata,
		int selectedIndex,
		bool requestedReplace,
		bool effectiveReplace,
		string usingAction
	)
	{
		try
		{
			_debugLog(
				"C# autocomplete confirmation completed",
				$"Source='{metadata?.Source ?? ""}', "
					+ $"Name='{metadata?.Name ?? ""}', "
					+ $"Namespace='{metadata?.NamespaceName ?? ""}', "
					+ $"SelectedIndex={selectedIndex}, "
					+ $"RequestedReplace={requestedReplace}, "
					+ $"EffectiveReplace={effectiveReplace}, "
					+ $"UsingAction='{usingAction ?? ""}'"
			);
		}
		catch
		{
			// Debug logging must never escape the CodeEdit GuiInput callback.
		}
	}

	private void LogFailure(Exception exception, bool confirmationSucceeded)
	{
		try
		{
			_debugLog(
				"C# autocomplete confirmation interception failed",
				$"ConfirmationSucceeded={confirmationSucceeded}, "
					+ $"ExceptionType='{exception?.GetType().FullName ?? ""}', "
					+ $"Exception='{exception}'"
			);
		}
		catch
		{
			// Debug logging must never escape the CodeEdit GuiInput callback.
		}
	}

	private static bool IsValidGodotObject(GodotObject source)
	{
		return source != null && GodotObject.IsInstanceValid(source);
	}
}
#endif
