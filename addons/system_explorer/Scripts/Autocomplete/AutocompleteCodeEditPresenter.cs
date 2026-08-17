#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using SystemExplorer.Autocomplete.Confirmation;

namespace SystemExplorer.Autocomplete;

internal readonly record struct AutocompleteCompletionDiagnosticContext(
	long RequestTransactionId,
	long MutationTransactionId,
	long RequestObservationSequence,
	long ScriptTransitionId,
	long BindingEpoch,
	long ReloadReadyEpoch,
	ulong CodeEditInstanceId,
	string ScriptPath
)
{
	internal static AutocompleteCompletionDiagnosticContext FromRequestLease(
		AutocompleteCompletionRequestLease requestLease,
		long mutationTransactionId = 0
	)
	{
		return new AutocompleteCompletionDiagnosticContext(
			requestLease.TransactionId,
			mutationTransactionId,
			requestLease.RequestObservationSequence,
			requestLease.BindingLease.ScriptTransitionId,
			requestLease.BindingLease.BindingEpoch,
			requestLease.BindingLease.ReloadReadyEpoch,
			requestLease.CodeEditInstanceId,
			requestLease.ScriptPath
		);
	}
}

internal sealed class AutocompleteCodeEditPresenter
{
	private const string VisualRightPadding = "  ";

	private readonly AutocompleteCompletionOptionMetadataCodec _metadataCodec;
	private readonly Action<string, string> _debugLog;

	internal AutocompleteCodeEditPresenter(
		AutocompleteCompletionOptionMetadataCodec metadataCodec,
		Action<string, string> debugLog
	)
	{
		_metadataCodec =
			metadataCodec ?? throw new ArgumentNullException(nameof(metadataCodec));
		_debugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
	}

	internal void Publish(
		CodeEdit codeEdit,
		IReadOnlyList<AutocompleteCompletionItem> items,
		AutocompleteCompletionDiagnosticContext diagnosticContext
	)
	{
		if (!IsValidGodotObject(codeEdit))
			throw new ArgumentException("A valid CodeEdit is required.", nameof(codeEdit));
		if (items == null)
			throw new ArgumentNullException(nameof(items));

		LogPublicationBoundary(
			"C# autocomplete publish begin",
			diagnosticContext,
			items.Count
		);

		foreach (AutocompleteCompletionItem item in items)
		{
			if (item == null)
				continue;

			string displayText = (item.DisplayText ?? "") + VisualRightPadding;

			if (item.Metadata == null)
			{
				codeEdit.AddCodeCompletionOption(
					item.Kind,
					displayText,
					item.InsertText ?? ""
				);
				continue;
			}

			Godot.Collections.Dictionary metadataValue = _metadataCodec.Encode(
				item.Metadata
			);
			Variant encodedValue = metadataValue;

			codeEdit.AddCodeCompletionOption(
				item.Kind,
				displayText,
				item.InsertText ?? "",
				null,
				null,
				encodedValue
			);
		}

		codeEdit.UpdateCodeCompletionOptions(true);
		LogPublicationBoundary(
			"C# autocomplete publish returned",
			diagnosticContext,
			items.Count
		);
	}

	private void LogPublicationBoundary(
		string operation,
		AutocompleteCompletionDiagnosticContext diagnosticContext,
		int itemCount
	)
	{
		try
		{
			string details =
				$"RequestTransactionId='{diagnosticContext.RequestTransactionId}', "
				+ $"MutationTransactionId='{diagnosticContext.MutationTransactionId}', "
				+ $"RequestObservationSequence='{diagnosticContext.RequestObservationSequence}', "
				+ $"ScriptTransitionId='{diagnosticContext.ScriptTransitionId}', "
				+ $"BindingEpoch='{diagnosticContext.BindingEpoch}', "
				+ $"ReloadReadyEpoch='{diagnosticContext.ReloadReadyEpoch}', "
				+ $"CodeEditInstanceId='{diagnosticContext.CodeEditInstanceId}', "
				+ $"ScriptPath='{diagnosticContext.ScriptPath ?? ""}', "
				+ $"ItemCount='{itemCount}'";

			_debugLog(operation ?? "", details);
		}
		catch
		{
			// Publication diagnostics must never affect completion publication control flow.
		}
	}

	private static bool IsValidGodotObject(GodotObject source)
	{
		return source != null && GodotObject.IsInstanceValid(source);
	}
}
#endif
