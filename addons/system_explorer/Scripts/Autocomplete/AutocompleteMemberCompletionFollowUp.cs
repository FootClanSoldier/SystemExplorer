#if TOOLS
using System;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.Autocomplete;

internal sealed class AutocompleteMemberCompletionFollowUp
{
	private PendingDemand? _pendingDemand;

	internal bool HasPendingWork => _pendingDemand.HasValue;

	internal bool Arm(
		long activeDocumentRevision,
		string scriptPath,
		EditorBindingLease bindingLease,
		long textChangedObservationSequence,
		int caretLine,
		int caretColumn,
		int prefixStartColumn
	)
	{
		string normalizedScriptPath = ScriptPathUtility.Normalize(scriptPath);
		string normalizedLeasePath = ScriptPathUtility.Normalize(
			bindingLease.ScriptResourcePath
		);
		if (
			activeDocumentRevision <= 0
			|| string.IsNullOrWhiteSpace(normalizedScriptPath)
			|| bindingLease.BindingEpoch <= 0
			|| bindingLease.CodeEditInstanceId == 0
			|| !string.Equals(
				normalizedLeasePath,
				normalizedScriptPath,
				StringComparison.OrdinalIgnoreCase
			)
			|| caretLine < 0
			|| caretColumn < 0
			|| prefixStartColumn != caretColumn
		)
		{
			return false;
		}

		var demand = new PendingDemand(
			activeDocumentRevision,
			normalizedScriptPath,
			bindingLease,
			textChangedObservationSequence > 0 ? textChangedObservationSequence : 0,
			caretLine,
			caretColumn,
			prefixStartColumn
		);

		if (_pendingDemand.HasValue && _pendingDemand.Value.Equals(demand))
			return false;

		_pendingDemand = demand;
		return true;
	}

	internal bool TryGetPending(out PendingDemand demand)
	{
		if (_pendingDemand.HasValue)
		{
			demand = _pendingDemand.Value;
			return true;
		}

		demand = default;
		return false;
	}

	internal bool ClearIfMatches(long activeDocumentRevision, string scriptPath)
	{
		if (!_pendingDemand.HasValue)
			return false;

		PendingDemand pending = _pendingDemand.Value;
		string normalizedScriptPath = ScriptPathUtility.Normalize(scriptPath);
		if (
			pending.ActiveDocumentRevision != activeDocumentRevision
			|| !string.Equals(
				pending.ScriptPath,
				normalizedScriptPath,
				StringComparison.OrdinalIgnoreCase
			)
		)
		{
			return false;
		}

		_pendingDemand = null;
		return true;
	}

	internal void Clear()
	{
		_pendingDemand = null;
	}

	internal readonly record struct PendingDemand(
		long ActiveDocumentRevision,
		string ScriptPath,
		EditorBindingLease BindingLease,
		long TextChangedObservationSequence,
		int CaretLine,
		int CaretColumn,
		int PrefixStartColumn
	);
}
#endif
