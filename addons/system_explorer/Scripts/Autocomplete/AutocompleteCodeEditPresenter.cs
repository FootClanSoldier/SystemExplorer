#if TOOLS
using Godot;
using System;
using System.Collections.Generic;

namespace SystemExplorer.Autocomplete;

internal readonly record struct AutocompleteCompletionDiagnosticContext(
	long RequestTransactionId,
	long ParentRequestDispatchMutationTransactionId,
	long MutationTransactionId,
	long PublicationId,
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
		long mutationTransactionId = 0,
		long publicationId = 0
	)
	{
		return new AutocompleteCompletionDiagnosticContext(
			requestLease.TransactionId,
			requestLease.ParentRequestDispatchMutationTransactionId,
			mutationTransactionId,
			publicationId,
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

	private readonly AutocompleteCompletionPublicationEnvelopeCodec _publicationEnvelopeCodec;
	private readonly Action<string, string> _debugLog;

	internal AutocompleteCodeEditPresenter(
		AutocompleteCompletionPublicationEnvelopeCodec publicationEnvelopeCodec,
		Action<string, string> debugLog
	)
	{
		_publicationEnvelopeCodec =
			publicationEnvelopeCodec
			?? throw new ArgumentNullException(nameof(publicationEnvelopeCodec));
		_debugLog = debugLog ?? throw new ArgumentNullException(nameof(debugLog));
	}

	internal void Publish(
		CodeEdit codeEdit,
		IReadOnlyList<AutocompleteCompletionItem> items,
		long publicationId,
		bool resetSelectedIndexToFirst,
		AutocompleteCompletionDiagnosticContext diagnosticContext
	)
	{
		if (!IsValidGodotObject(codeEdit))
			throw new ArgumentException("A valid CodeEdit is required.", nameof(codeEdit));
		if (items == null)
			throw new ArgumentNullException(nameof(items));
		if (publicationId <= 0)
			throw new ArgumentOutOfRangeException(nameof(publicationId));

		LogPublicationBoundary(
			"C# autocomplete publish begin",
			diagnosticContext,
			items.Count,
			resetSelectedIndexToFirst
		);

		foreach (AutocompleteCompletionItem item in items)
		{
			if (item == null)
				continue;

			string displayText = (item.DisplayText ?? "") + VisualRightPadding;
			Godot.Collections.Dictionary envelope = _publicationEnvelopeCodec.Encode(
				publicationId,
				item.Metadata
			);
			Variant encodedValue = envelope;

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
		if (resetSelectedIndexToFirst)
			codeEdit.SetCodeCompletionSelectedIndex(0);

		LogPublicationBoundary(
			"C# autocomplete publish returned",
			diagnosticContext,
			items.Count,
			resetSelectedIndexToFirst
		);
	}

	private void LogPublicationBoundary(
		string operation,
		AutocompleteCompletionDiagnosticContext diagnosticContext,
		int itemCount,
		bool resetSelectedIndexToFirst
	)
	{
		try
		{
			string details =
				$"RequestTransactionId='{diagnosticContext.RequestTransactionId}', "
				+ $"ParentRequestDispatchMutationTransactionId='{diagnosticContext.ParentRequestDispatchMutationTransactionId}', "
				+ $"MutationTransactionId='{diagnosticContext.MutationTransactionId}', "
				+ $"PublicationId='{diagnosticContext.PublicationId}', "
				+ $"RequestObservationSequence='{diagnosticContext.RequestObservationSequence}', "
				+ $"ScriptTransitionId='{diagnosticContext.ScriptTransitionId}', "
				+ $"BindingEpoch='{diagnosticContext.BindingEpoch}', "
				+ $"ReloadReadyEpoch='{diagnosticContext.ReloadReadyEpoch}', "
				+ $"CodeEditInstanceId='{diagnosticContext.CodeEditInstanceId}', "
				+ $"ScriptPath='{diagnosticContext.ScriptPath ?? ""}', "
				+ $"ItemCount='{itemCount}', "
				+ $"ResetSelectedIndexToFirst='{resetSelectedIndexToFirst}'";

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
