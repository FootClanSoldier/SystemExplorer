#if TOOLS
namespace SystemExplorer.Autocomplete.Indexing.ActiveDocument;

internal sealed record CSharpActiveDocumentIndexRequest(
	long Revision,
	string Reason,
	string ScriptPath,
	string SourceText
);
#endif
