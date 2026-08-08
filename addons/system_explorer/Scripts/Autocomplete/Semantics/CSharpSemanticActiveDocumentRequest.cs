#if TOOLS
namespace SystemExplorer.Autocomplete.Semantics;

internal sealed record CSharpSemanticActiveDocumentRequest(
	long Revision,
	string Reason,
	string ScriptPath,
	string SourceText
);
#endif
