#if TOOLS
using System;
using SystemExplorer.CodeService.Client;
using SystemExplorer.CodeService.Completion;
using SystemExplorer.CodeService.Documents;

namespace SystemExplorer.CodeService.Autocomplete;

internal sealed record AutocompleteCompletionAuthority(
	long ClientGeneration,
	string EpochId,
	string DocumentPath,
	long ClientVersion,
	CodeServiceClientSessionInfo Session,
	long WorkspaceGeneration,
	long WorkspacePublicationVersion,
	long RoslynGeneration,
	int RoslynDocumentVersion,
	long RoslynOverlayRevision)
{
	internal static bool TryCreate(
		CodeServiceDocumentCompletionAdmissionSnapshot admission,
		CodeServiceCompletionResult result,
		out AutocompleteCompletionAuthority authority,
		out string detail)
	{
		authority = null;
		detail = "";
		if (!admission.IsCurrentVersionSynchronized || result.Outcome != CodeServiceCompletionOutcome.Success)
		{
			detail = "Completion authority requires a synchronized admission and successful result.";
			return false;
		}
		if (result.ClientGeneration != admission.ClientGeneration
			|| !string.Equals(result.EpochId, admission.EpochId, StringComparison.Ordinal)
			|| !string.Equals(result.DocumentPath, admission.DocumentPath, StringComparison.Ordinal)
			|| result.AcceptedClientVersion != admission.ClientVersion
			|| !result.WorkspaceGeneration.HasValue
			|| !result.WorkspacePublicationVersion.HasValue
			|| !result.RoslynGeneration.HasValue
			|| !result.RoslynDocumentVersion.HasValue
			|| !result.RoslynOverlayRevision.HasValue)
		{
			detail = "Completion result does not contain exact publication authority.";
			return false;
		}

		authority = new AutocompleteCompletionAuthority(
			admission.ClientGeneration,
			admission.EpochId,
			admission.DocumentPath,
			admission.ClientVersion,
			admission.Session,
			result.WorkspaceGeneration.Value,
			result.WorkspacePublicationVersion.Value,
			result.RoslynGeneration.Value,
			result.RoslynDocumentVersion.Value,
			result.RoslynOverlayRevision.Value);
		return true;
	}
}
#endif
