#if TOOLS
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace SystemExplorer.Autocomplete.Semantics;

internal sealed class CSharpSemanticMemberIndexSnapshot
{
	internal static CSharpSemanticMemberIndexSnapshot Empty { get; } = new(
		projectGeneration: 0,
		activeDocumentRevision: 0,
		scriptPath: "",
		memberAccesses: Array.Empty<CSharpSemanticMemberAccess>(),
		hasBuiltAtLeastOnce: false
	);

	internal CSharpSemanticMemberIndexSnapshot(
		long projectGeneration,
		long activeDocumentRevision,
		string scriptPath,
		IReadOnlyList<CSharpSemanticMemberAccess> memberAccesses,
		bool hasBuiltAtLeastOnce
	)
	{
		ProjectGeneration = projectGeneration;
		ActiveDocumentRevision = activeDocumentRevision;
		ScriptPath = scriptPath ?? "";
		MemberAccesses = new ReadOnlyCollection<CSharpSemanticMemberAccess>(
			(memberAccesses ?? Array.Empty<CSharpSemanticMemberAccess>())
				.Where(access => access != null)
				.ToArray()
		);
		HasBuiltAtLeastOnce = hasBuiltAtLeastOnce;
	}

	internal long ProjectGeneration { get; }
	internal long ActiveDocumentRevision { get; }
	internal string ScriptPath { get; }
	internal IReadOnlyList<CSharpSemanticMemberAccess> MemberAccesses { get; }
	internal bool HasBuiltAtLeastOnce { get; }

	internal bool TryGetMemberAccess(
		int memberNameLine,
		int memberNameStartColumn,
		out CSharpSemanticMemberAccess access
	)
	{
		access = MemberAccesses.FirstOrDefault(
			candidate =>
				candidate.MemberNameLine == memberNameLine
				&& candidate.MemberNameStartColumn == memberNameStartColumn
		);
		return access != null;
	}
}
#endif
