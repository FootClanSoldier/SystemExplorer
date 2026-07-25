#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SystemExplorer.EditorIntegration.ScriptEditing;

internal sealed class OpenScriptEditorBufferGroup
{
	private readonly IReadOnlyList<OpenScriptEditorBuffer> _buffers;

	internal OpenScriptEditorBufferGroup(
		string path,
		IEnumerable<OpenScriptEditorBuffer> buffers,
		TextEdit currentTextEditor = null
	)
	{
		Path = path ?? "";

		List<OpenScriptEditorBuffer> bufferList = new();
		HashSet<TextEdit> seenTextEditors = new();

		foreach (OpenScriptEditorBuffer buffer in buffers ?? Array.Empty<OpenScriptEditorBuffer>())
		{
			if (buffer.TextEditor == null)
				throw new ArgumentException("Open script editor buffer groups cannot contain a null TextEdit.", nameof(buffers));

			if (!seenTextEditors.Add(buffer.TextEditor))
				throw new ArgumentException("The same TextEdit cannot occur more than once in an open script editor buffer group.", nameof(buffers));

			bufferList.Add(new OpenScriptEditorBuffer(Path, buffer.TextEditor));
		}

		_buffers = bufferList.AsReadOnly();

		if (currentTextEditor == null)
		{
			CurrentEditorBuffer = default;
			HasCurrentEditorBuffer = false;
			return;
		}

		OpenScriptEditorBuffer currentBuffer = bufferList.FirstOrDefault(buffer =>
			ReferenceEquals(buffer.TextEditor, currentTextEditor)
		);

		if (currentBuffer.TextEditor == null)
			throw new ArgumentException("The current TextEdit must be a member of the open script editor buffer group.", nameof(currentTextEditor));

		CurrentEditorBuffer = currentBuffer;
		HasCurrentEditorBuffer = true;
	}

	internal string Path { get; }
	internal IReadOnlyList<OpenScriptEditorBuffer> Buffers => _buffers;
	internal bool HasCurrentEditorBuffer { get; }
	internal OpenScriptEditorBuffer CurrentEditorBuffer { get; }

	internal static OpenScriptEditorBufferGroup CreateSingle(
		OpenScriptEditorBuffer buffer,
		bool isCurrentEditor
	)
	{
		return new OpenScriptEditorBufferGroup(
			buffer.Path,
			new[] { buffer },
			isCurrentEditor ? buffer.TextEditor : null
		);
	}
}
#endif
