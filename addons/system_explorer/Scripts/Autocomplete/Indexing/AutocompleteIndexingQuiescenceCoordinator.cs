#if TOOLS
using System;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.Autocomplete.Indexing;

internal readonly record struct AutocompleteIndexingQuiescenceBatch(
	bool ActiveDocumentRequested,
	EditorBindingLease ActiveDocumentBindingLease,
	string ActiveDocumentReason,
	int ActiveDocumentCoalescedCount,
	bool ProjectRefreshRequested,
	string ProjectRefreshReason,
	int ProjectRefreshCoalescedCount,
	double QuietDurationSeconds
);

internal sealed class AutocompleteIndexingQuiescenceCoordinator
{
	internal const double QuietPeriodSeconds = 0.200;
	internal const int QuietPeriodMilliseconds = 200;

	private bool _activeDocumentPending;
	private EditorBindingLease _activeDocumentBindingLease;
	private string _activeDocumentReason = "";
	private int _activeDocumentCoalescedCount;

	private bool _projectRefreshPending;
	private string _projectRefreshReason = "";
	private int _projectRefreshCoalescedCount;

	private double _quietElapsedSeconds;
	private bool _discardNextEligibleDelta;

	internal bool HasPendingWork => _activeDocumentPending || _projectRefreshPending;

	internal bool RequestActiveDocument(EditorBindingLease bindingLease, string reason)
	{
		if (
			_activeDocumentPending
			&& _activeDocumentBindingLease.Equals(bindingLease)
		)
		{
			return false;
		}

		if (_activeDocumentPending && _activeDocumentCoalescedCount < int.MaxValue)
			_activeDocumentCoalescedCount++;
		else if (!_activeDocumentPending)
			_activeDocumentCoalescedCount = 0;

		_activeDocumentPending = true;
		_activeDocumentBindingLease = bindingLease;
		_activeDocumentReason = NormalizeReason(reason, "Active document indexing");
		ResetQuietWindowForActivity();
		return true;
	}

	internal void RequestProjectRefresh(string reason)
	{
		if (_projectRefreshPending && _projectRefreshCoalescedCount < int.MaxValue)
			_projectRefreshCoalescedCount++;
		else if (!_projectRefreshPending)
			_projectRefreshCoalescedCount = 0;

		_projectRefreshPending = true;
		_projectRefreshReason = NormalizeReason(reason, "Project index refresh");
		ResetQuietWindowForActivity();
	}

	internal bool ConsumeActiveDocument(EditorBindingLease bindingLease)
	{
		if (
			!_activeDocumentPending
			|| !_activeDocumentBindingLease.Equals(bindingLease)
		)
		{
			return false;
		}

		ClearActiveDocumentIntent();
		if (!HasPendingWork)
			ResetQuietWindowForActivity();
		return true;
	}

	internal void InvalidateSpeculativeActiveDocumentForExternalMutation()
	{
		ClearActiveDocumentIntent();
		RestartQuietWindowAfterBarrier();
	}

	internal void RestartQuietWindowAfterBarrier()
	{
		_quietElapsedSeconds = 0;
		_discardNextEligibleDelta = HasPendingWork;
	}

	internal bool TryAdvance(
		double delta,
		bool admissionAllowed,
		out AutocompleteIndexingQuiescenceBatch batch
	)
	{
		batch = default;
		if (!HasPendingWork)
		{
			_quietElapsedSeconds = 0;
			_discardNextEligibleDelta = false;
			return false;
		}

		if (!admissionAllowed)
		{
			RestartQuietWindowAfterBarrier();
			return false;
		}

		if (_discardNextEligibleDelta)
		{
			_discardNextEligibleDelta = false;
			_quietElapsedSeconds = 0;
			return false;
		}

		if (double.IsNaN(delta) || double.IsInfinity(delta) || delta < 0)
			delta = 0;

		_quietElapsedSeconds += delta;
		if (_quietElapsedSeconds < QuietPeriodSeconds)
			return false;

		batch = new AutocompleteIndexingQuiescenceBatch(
			_activeDocumentPending,
			_activeDocumentBindingLease,
			_activeDocumentReason,
			_activeDocumentCoalescedCount,
			_projectRefreshPending,
			_projectRefreshReason,
			_projectRefreshCoalescedCount,
			_quietElapsedSeconds
		);

		ClearPendingWork();
		return true;
	}

	internal void ClearPendingWork()
	{
		ClearActiveDocumentIntent();
		_projectRefreshPending = false;
		_projectRefreshReason = "";
		_projectRefreshCoalescedCount = 0;
		_quietElapsedSeconds = 0;
		_discardNextEligibleDelta = false;
	}

	private void ClearActiveDocumentIntent()
	{
		_activeDocumentPending = false;
		_activeDocumentBindingLease = default;
		_activeDocumentReason = "";
		_activeDocumentCoalescedCount = 0;
	}

	private void ResetQuietWindowForActivity()
	{
		_quietElapsedSeconds = 0;
		_discardNextEligibleDelta = false;
	}

	private static string NormalizeReason(string reason, string fallback)
	{
		string normalized = string.IsNullOrWhiteSpace(reason) ? fallback : reason.Trim();
		return normalized.Length <= 160 ? normalized : normalized.Substring(0, 160);
	}
}
#endif
