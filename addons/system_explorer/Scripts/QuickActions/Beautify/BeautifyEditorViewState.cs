#if TOOLS
using Godot;
using SystemExplorer.EditorIntegration.ScriptEditing;

namespace SystemExplorer.QuickActions.Beautify;

internal readonly struct BeautifyEditorViewState
{
	internal BeautifyEditorViewState(
		string path,
		TextEdit textEditor,
		int firstVisibleLine,
		int scrollHorizontal,
		double scrollVertical,
		int caretLine,
		int caretColumn,
		bool hadFocus
	)
	{
		Path = ScriptPathUtility.Normalize(path);
		TextEditor = textEditor;
		FirstVisibleLine = firstVisibleLine;
		ScrollHorizontal = scrollHorizontal;
		ScrollVertical = scrollVertical;
		CaretLine = caretLine;
		CaretColumn = caretColumn;
		HadFocus = hadFocus;
	}

	internal string Path { get; }
	internal TextEdit TextEditor { get; }
	internal int FirstVisibleLine { get; }
	internal int ScrollHorizontal { get; }
	internal double ScrollVertical { get; }
	internal int CaretLine { get; }
	internal int CaretColumn { get; }
	internal bool HadFocus { get; }
	internal bool IsValid => TextEditor != null;
}
#endif
