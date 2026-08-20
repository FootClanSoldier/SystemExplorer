#if TOOLS
using System;
using System.Collections.Generic;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.Autocomplete;

internal sealed class AutocompleteCompletionSession
{
	private readonly IReadOnlyList<AutocompleteCompletionItem> _publishedItems;
	private readonly AutocompleteCompletionMatchPolicy _matchPolicy;
	private readonly string _scriptPath;
	private readonly AutocompleteRequestKind _kind;
	private readonly int _caretLine;
	private readonly int _prefixStartColumn;
	private bool _isDormant;
	private bool _recoveryRequestIssuedForCurrentMatchingState;

	internal AutocompleteCompletionSession(
		IReadOnlyList<AutocompleteCompletionItem> publishedItems,
		AutocompleteRequestContext publishedRequest,
		AutocompleteCompletionMatchPolicy matchPolicy
	)
	{
		if (publishedItems == null)
			throw new ArgumentNullException(nameof(publishedItems));
		if (publishedRequest == null)
			throw new ArgumentNullException(nameof(publishedRequest));
		if (publishedRequest.Prefix == null)
			throw new ArgumentException(
				"The published request must contain a prefix.",
				nameof(publishedRequest)
			);

		_publishedItems = new List<AutocompleteCompletionItem>(publishedItems).AsReadOnly();
		PublishedPrefix = publishedRequest.Prefix;
		_scriptPath = ScriptPathUtility.Normalize(publishedRequest.ScriptPath);
		_kind = publishedRequest.Kind;
		_caretLine = publishedRequest.CaretLine;
		_prefixStartColumn = publishedRequest.PrefixStartColumn;
		_matchPolicy = matchPolicy ?? throw new ArgumentNullException(nameof(matchPolicy));
	}

	internal IReadOnlyList<AutocompleteCompletionItem> PublishedItems => _publishedItems;
	internal string PublishedPrefix { get; }
	internal bool WasPublishedForEmptyPrefix => PublishedPrefix.Length == 0;
	internal bool IsCompleteMemberAccessSession =>
		_kind == AutocompleteRequestKind.MemberAccess && WasPublishedForEmptyPrefix;
	internal bool IsDormant => _isDormant;

	internal bool BelongsToSameAnchor(AutocompleteRequestContext currentRequest)
	{
		if (currentRequest == null || currentRequest.Prefix == null)
			return false;

		return AutocompleteCompletionAnchorPolicy.BelongsToSameAnchor(
			_scriptPath,
			_kind,
			_caretLine,
			_prefixStartColumn,
			currentRequest.ScriptPath,
			currentRequest.Kind,
			currentRequest.CaretLine,
			currentRequest.PrefixStartColumn
		);
	}

	internal bool HasAvailableMatch(AutocompleteRequestContext currentRequest)
	{
		if (currentRequest == null || currentRequest.Prefix == null)
			return false;

		return _matchPolicy.CanRemainAvailable(
			_publishedItems,
			currentRequest.Prefix
		);
	}

	internal bool CanRemainOpen(AutocompleteRequestContext currentRequest)
	{
		if (!BelongsToSameAnchor(currentRequest))
			return false;

		string currentPrefix = currentRequest.Prefix;
		if (currentPrefix.Length == 0 && !WasPublishedForEmptyPrefix)
			return false;

		return HasAvailableMatch(currentRequest);
	}

	internal bool MarkDormant()
	{
		bool becameDormant = !_isDormant;
		_isDormant = true;
		_recoveryRequestIssuedForCurrentMatchingState = false;
		return becameDormant;
	}

	internal void MarkActive()
	{
		_isDormant = false;
		_recoveryRequestIssuedForCurrentMatchingState = false;
	}

	internal bool TryBeginDormantRecoveryRequest()
	{
		if (!_isDormant || _recoveryRequestIssuedForCurrentMatchingState)
			return false;

		_recoveryRequestIssuedForCurrentMatchingState = true;
		return true;
	}
}
#endif
