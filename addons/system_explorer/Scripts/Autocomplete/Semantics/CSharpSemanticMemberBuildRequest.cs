#if TOOLS
using SystemExplorer.Autocomplete.Indexing;

namespace SystemExplorer.Autocomplete.Semantics;

internal sealed record CSharpSemanticMemberBuildRequest(
	long ProjectStateVersion,
	long ActiveStateVersion,
	CSharpProjectIndexSnapshot ProjectSnapshot,
	CSharpSemanticActiveDocumentRequest ActiveDocument
);
#endif
