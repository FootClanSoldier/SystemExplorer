#if TOOLS
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace SystemExplorer.Autocomplete.Semantics;

internal sealed class CSharpSemanticMemberAccess
{
	internal CSharpSemanticMemberAccess(
		int memberNameLine,
		int memberNameStartColumn,
		string receiverTypeName,
		IReadOnlyList<CSharpSemanticMemberSymbol> members
	)
	{
		MemberNameLine = memberNameLine;
		MemberNameStartColumn = memberNameStartColumn;
		ReceiverTypeName = receiverTypeName ?? "";
		Members = new ReadOnlyCollection<CSharpSemanticMemberSymbol>(
			(members ?? Array.Empty<CSharpSemanticMemberSymbol>())
				.Where(member => member != null)
				.ToArray()
		);
	}

	internal int MemberNameLine { get; }
	internal int MemberNameStartColumn { get; }
	internal string ReceiverTypeName { get; }
	internal IReadOnlyList<CSharpSemanticMemberSymbol> Members { get; }
}
#endif
