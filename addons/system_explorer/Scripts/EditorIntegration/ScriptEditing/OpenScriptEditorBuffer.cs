#if TOOLS
using Godot;

namespace SystemExplorer.EditorIntegration.ScriptEditing;

internal readonly struct OpenScriptEditorBuffer
{
	internal OpenScriptEditorBuffer(string path, TextEdit textEditor)
	{
		Path = path ?? "";
		TextEditor = textEditor;
	}

	internal string Path { get; }
	internal TextEdit TextEditor { get; }
}
#endif
