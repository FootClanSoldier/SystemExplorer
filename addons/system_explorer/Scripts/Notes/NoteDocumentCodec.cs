#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SystemExplorer.Notes;

internal static class NoteDocumentCodec
{
	internal const int CurrentFormatVersion = 1;

	internal static bool TryDeserializeAndValidateDocument(
		string expectedSystemName,
		byte[] bytes,
		out NoteSystemDocument document,
		out string failureDetail
	)
	{
		document = null;
		failureDetail = "";

		if (bytes == null || bytes.Length == 0)
		{
			failureDetail = NotePersistenceFailure.BuildFailureDetail(
				expectedSystemName,
				"validate",
				"Detail='Note file was empty.'"
			);
			return false;
		}

		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(bytes);

			if (jsonDocument.RootElement.ValueKind != JsonValueKind.Object)
			{
				failureDetail = NotePersistenceFailure.BuildFailureDetail(
					expectedSystemName,
					"validate",
					"Detail='Root JSON value must be an object.'"
				);
				return false;
			}

			bool sawFormatVersion = false;
			bool sawSystemName = false;
			bool sawNote = false;
			bool sawFolders = false;
			bool sawViewState = false;
			int formatVersion = 0;
			string storedSystemName = null;
			string note = null;
			Dictionary<string, string> folders = null;
			NoteViewState systemViewState = null;
			Dictionary<string, NoteViewState> folderViewStates =
				new(StringComparer.Ordinal);

			foreach (JsonProperty property in jsonDocument.RootElement.EnumerateObject())
			{
				switch (property.Name)
				{
					case "format_version":
						if (sawFormatVersion)
							return FailDuplicateRootProperty(expectedSystemName, property.Name, out failureDetail);

						sawFormatVersion = true;
						if (
							property.Value.ValueKind != JsonValueKind.Number
							|| !property.Value.TryGetInt32(out formatVersion)
						)
						{
							failureDetail = NotePersistenceFailure.BuildFailureDetail(
								expectedSystemName,
								"validate",
								"Detail='format_version must be an integer.'"
							);
							return false;
						}
						break;

					case "system_name":
						if (sawSystemName)
							return FailDuplicateRootProperty(expectedSystemName, property.Name, out failureDetail);

						sawSystemName = true;
						if (property.Value.ValueKind != JsonValueKind.String)
						{
							failureDetail = NotePersistenceFailure.BuildFailureDetail(
								expectedSystemName,
								"validate",
								"Detail='system_name must be a string.'"
							);
							return false;
						}
						storedSystemName = property.Value.GetString();
						break;

					case "note":
						if (sawNote)
							return FailDuplicateRootProperty(expectedSystemName, property.Name, out failureDetail);

						sawNote = true;
						if (property.Value.ValueKind != JsonValueKind.String)
						{
							failureDetail = NotePersistenceFailure.BuildFailureDetail(
								expectedSystemName,
								"validate",
								"Detail='note must be a string.'"
							);
							return false;
						}
						note = property.Value.GetString();
						break;

					case "folders":
						if (sawFolders)
							return FailDuplicateRootProperty(expectedSystemName, property.Name, out failureDetail);

						sawFolders = true;
						if (property.Value.ValueKind != JsonValueKind.Object)
						{
							failureDetail = NotePersistenceFailure.BuildFailureDetail(
								expectedSystemName,
								"validate",
								"Detail='folders must be a JSON object.'"
							);
							return false;
						}

						folders = new Dictionary<string, string>(StringComparer.Ordinal);
						foreach (JsonProperty folderProperty in property.Value.EnumerateObject())
						{
							if (string.IsNullOrEmpty(folderProperty.Name))
							{
								failureDetail = NotePersistenceFailure.BuildFailureDetail(
									expectedSystemName,
									"validate",
									"Detail='folders contains an empty folder path.'"
								);
								return false;
							}

							if (folders.ContainsKey(folderProperty.Name))
							{
								failureDetail = NotePersistenceFailure.BuildFailureDetail(
									expectedSystemName,
									"validate",
									$"Detail='folders contains duplicate ordinal path {NotePersistenceFailure.FormatForDetail(folderProperty.Name)}.'"
								);
								return false;
							}

							if (folderProperty.Value.ValueKind != JsonValueKind.String)
							{
								failureDetail = NotePersistenceFailure.BuildFailureDetail(
									expectedSystemName,
									"validate",
									$"Detail='Folder note {NotePersistenceFailure.FormatForDetail(folderProperty.Name)} must be a string.'"
								);
								return false;
							}

							string folderNote = folderProperty.Value.GetString();
							if (string.IsNullOrWhiteSpace(folderNote))
							{
								failureDetail = NotePersistenceFailure.BuildFailureDetail(
									expectedSystemName,
									"validate",
									$"Detail='Folder note {NotePersistenceFailure.FormatForDetail(folderProperty.Name)} is empty or whitespace-only.'"
								);
								return false;
							}

							folders.Add(folderProperty.Name, folderNote);
						}
						break;

					case "view_state":
						if (sawViewState)
							return FailDuplicateRootProperty(expectedSystemName, property.Name, out failureDetail);

						sawViewState = true;
						if (
							!TryReadViewState(
								expectedSystemName,
								property.Value,
								out systemViewState,
								out folderViewStates,
								out failureDetail
							)
						)
						{
							return false;
						}
						break;

					default:
						failureDetail = NotePersistenceFailure.BuildFailureDetail(
							expectedSystemName,
							"validate",
							$"Detail='Unsupported property {NotePersistenceFailure.FormatForDetail(property.Name)} for format version {CurrentFormatVersion}.'"
						);
						return false;
				}
			}

			if (!sawFormatVersion || !sawSystemName || !sawNote || !sawFolders)
			{
				failureDetail = NotePersistenceFailure.BuildFailureDetail(
					expectedSystemName,
					"validate",
					"Detail='Note document is missing one or more required properties.'"
				);
				return false;
			}

			if (formatVersion != CurrentFormatVersion)
			{
				failureDetail = NotePersistenceFailure.BuildFailureDetail(
					expectedSystemName,
					"format-version",
					$"StoredFormatVersion={formatVersion}, SupportedFormatVersion={CurrentFormatVersion}"
				);
				return false;
			}

			if (!string.Equals(storedSystemName, expectedSystemName, StringComparison.Ordinal))
			{
				failureDetail = NotePersistenceFailure.BuildFailureDetail(
					expectedSystemName,
					"identity",
					$"StoredSystemName={NotePersistenceFailure.FormatForDetail(storedSystemName)}, ExpectedSystemName={NotePersistenceFailure.FormatForDetail(expectedSystemName)}"
				);
				return false;
			}

			if (note == null || folders == null)
			{
				failureDetail = NotePersistenceFailure.BuildFailureDetail(
					expectedSystemName,
					"validate",
					"Detail='Required note data could not be materialized.'"
				);
				return false;
			}

			if (note.Length != 0 && string.IsNullOrWhiteSpace(note))
			{
				failureDetail = NotePersistenceFailure.BuildFailureDetail(
					expectedSystemName,
					"validate",
					"Detail='System note is whitespace-only.'"
				);
				return false;
			}

			if (note.Length == 0 && folders.Count == 0)
			{
				failureDetail = NotePersistenceFailure.BuildFailureDetail(
					expectedSystemName,
					"validate",
					"Detail='Note document contains no persistent notes.'"
				);
				return false;
			}

			if (systemViewState != null && note.Length == 0)
			{
				failureDetail = NotePersistenceFailure.BuildFailureDetail(
					expectedSystemName,
					"validate",
					"Detail='view_state.system cannot exist when the System Note is absent.'"
				);
				return false;
			}

			foreach (KeyValuePair<string, NoteViewState> folderViewState in folderViewStates)
			{
				if (!folders.ContainsKey(folderViewState.Key))
				{
					failureDetail = NotePersistenceFailure.BuildFailureDetail(
						expectedSystemName,
						"validate",
						$"Detail='Folder view-state {NotePersistenceFailure.FormatForDetail(folderViewState.Key)} does not reference an existing Folder Note.'"
					);
					return false;
				}
			}

			document = new NoteSystemDocument(expectedSystemName)
			{
				Note = note,
				SystemViewState = systemViewState,
			};

			foreach (KeyValuePair<string, string> folder in folders)
				document.Folders.Add(folder.Key, folder.Value);

			foreach (KeyValuePair<string, NoteViewState> folderViewState in folderViewStates)
				document.FolderViewStates.Add(folderViewState.Key, folderViewState.Value);

			return true;
		}
		catch (JsonException exception)
		{
			failureDetail = NotePersistenceFailure.BuildFailureDetail(
				expectedSystemName,
				"parse-json",
				$"Exception='{exception}'"
			);
			return false;
		}
		catch (Exception exception)
		{
			failureDetail = NotePersistenceFailure.BuildFailureDetail(
				expectedSystemName,
				"read-validate",
				$"Exception='{exception}'"
			);
			return false;
		}
	}

	internal static byte[] SerializeDocument(NoteSystemDocument document)
	{
		ValidateViewStateForSerialization(document);

		using MemoryStream stream = new();
		using (
			Utf8JsonWriter writer = new(
				stream,
				new JsonWriterOptions
				{
					Indented = true,
				}
			)
		)
		{
			writer.WriteStartObject();
			writer.WriteNumber("format_version", CurrentFormatVersion);
			writer.WriteString("system_name", document.SystemName);
			writer.WriteString("note", document.Note);
			writer.WritePropertyName("folders");
			writer.WriteStartObject();

			List<string> folderPaths = new(document.Folders.Keys);
			folderPaths.Sort(StringComparer.Ordinal);

			foreach (string folderPath in folderPaths)
				writer.WriteString(folderPath, document.Folders[folderPath]);

			writer.WriteEndObject();
			writer.WritePropertyName("view_state");
			writer.WriteStartObject();
			writer.WritePropertyName("system");

			if (document.SystemViewState == null)
				writer.WriteNullValue();
			else
				WriteCaretState(writer, document.SystemViewState);

			writer.WritePropertyName("folders");
			writer.WriteStartObject();

			List<string> folderViewStatePaths = new(document.FolderViewStates.Keys);
			folderViewStatePaths.Sort(StringComparer.Ordinal);

			foreach (string folderPath in folderViewStatePaths)
			{
				writer.WritePropertyName(folderPath);
				WriteCaretState(writer, document.FolderViewStates[folderPath]);
			}

			writer.WriteEndObject();
			writer.WriteEndObject();
			writer.WriteEndObject();
			writer.Flush();
		}

		return stream.ToArray();
	}

	private static bool TryReadViewState(
		string systemName,
		JsonElement element,
		out NoteViewState systemViewState,
		out Dictionary<string, NoteViewState> folderViewStates,
		out string failureDetail
	)
	{
		systemViewState = null;
		folderViewStates = new Dictionary<string, NoteViewState>(StringComparer.Ordinal);
		failureDetail = "";

		if (element.ValueKind != JsonValueKind.Object)
		{
			failureDetail = NotePersistenceFailure.BuildFailureDetail(
				systemName,
				"validate",
				"Detail='view_state must be a JSON object.'"
			);
			return false;
		}

		bool sawSystem = false;
		bool sawFolders = false;

		foreach (JsonProperty property in element.EnumerateObject())
		{
			switch (property.Name)
			{
				case "system":
					if (sawSystem)
						return FailDuplicateViewStateProperty(systemName, property.Name, out failureDetail);

					sawSystem = true;
					if (property.Value.ValueKind == JsonValueKind.Null)
					{
						systemViewState = null;
						break;
					}

					if (
						!TryReadCaretState(
							systemName,
							property.Value,
							"view_state.system",
							out systemViewState,
							out failureDetail
						)
					)
					{
						return false;
					}
					break;

				case "folders":
					if (sawFolders)
						return FailDuplicateViewStateProperty(systemName, property.Name, out failureDetail);

					sawFolders = true;
					if (property.Value.ValueKind != JsonValueKind.Object)
					{
						failureDetail = NotePersistenceFailure.BuildFailureDetail(
							systemName,
							"validate",
							"Detail='view_state.folders must be a JSON object.'"
						);
						return false;
					}

					foreach (JsonProperty folderProperty in property.Value.EnumerateObject())
					{
						if (string.IsNullOrEmpty(folderProperty.Name))
						{
							failureDetail = NotePersistenceFailure.BuildFailureDetail(
								systemName,
								"validate",
								"Detail='view_state.folders contains an empty folder path.'"
							);
							return false;
						}

						if (folderViewStates.ContainsKey(folderProperty.Name))
						{
							failureDetail = NotePersistenceFailure.BuildFailureDetail(
								systemName,
								"validate",
								$"Detail='view_state.folders contains duplicate ordinal path {NotePersistenceFailure.FormatForDetail(folderProperty.Name)}.'"
							);
							return false;
						}

						if (
							!TryReadCaretState(
								systemName,
								folderProperty.Value,
								$"view_state.folders[{NotePersistenceFailure.FormatForDetail(folderProperty.Name)}]",
								out NoteViewState folderViewState,
								out failureDetail
							)
						)
						{
							return false;
						}

						folderViewStates.Add(folderProperty.Name, folderViewState);
					}
					break;

				default:
					failureDetail = NotePersistenceFailure.BuildFailureDetail(
						systemName,
						"validate",
						$"Detail='Unsupported view_state property {NotePersistenceFailure.FormatForDetail(property.Name)}.'"
					);
					return false;
			}
		}

		if (!sawSystem || !sawFolders)
		{
			failureDetail = NotePersistenceFailure.BuildFailureDetail(
				systemName,
				"validate",
				"Detail='view_state is missing system or folders.'"
			);
			return false;
		}

		return true;
	}

	private static bool TryReadCaretState(
		string systemName,
		JsonElement element,
		string stateName,
		out NoteViewState viewState,
		out string failureDetail
	)
	{
		viewState = null;
		failureDetail = "";

		if (element.ValueKind != JsonValueKind.Object)
		{
			failureDetail = NotePersistenceFailure.BuildFailureDetail(
				systemName,
				"validate",
				$"Detail='{stateName} must be a JSON object.'"
			);
			return false;
		}

		bool sawCaretLine = false;
		bool sawCaretColumn = false;
		int caretLine = 0;
		int caretColumn = 0;

		foreach (JsonProperty property in element.EnumerateObject())
		{
			switch (property.Name)
			{
				case "caret_line":
					if (sawCaretLine)
						return FailDuplicateCaretStateProperty(systemName, stateName, property.Name, out failureDetail);

					sawCaretLine = true;
					if (
						property.Value.ValueKind != JsonValueKind.Number
						|| !property.Value.TryGetInt32(out caretLine)
						|| caretLine < 0
					)
					{
						failureDetail = NotePersistenceFailure.BuildFailureDetail(
							systemName,
							"validate",
							$"Detail='{stateName}.caret_line must be a non-negative integer.'"
						);
						return false;
					}
					break;

				case "caret_column":
					if (sawCaretColumn)
						return FailDuplicateCaretStateProperty(systemName, stateName, property.Name, out failureDetail);

					sawCaretColumn = true;
					if (
						property.Value.ValueKind != JsonValueKind.Number
						|| !property.Value.TryGetInt32(out caretColumn)
						|| caretColumn < 0
					)
					{
						failureDetail = NotePersistenceFailure.BuildFailureDetail(
							systemName,
							"validate",
							$"Detail='{stateName}.caret_column must be a non-negative integer.'"
						);
						return false;
					}
					break;

				default:
					failureDetail = NotePersistenceFailure.BuildFailureDetail(
						systemName,
						"validate",
						$"Detail='Unsupported caret-state property {NotePersistenceFailure.FormatForDetail(property.Name)} in {stateName}.'"
					);
					return false;
			}
		}

		if (!sawCaretLine || !sawCaretColumn)
		{
			failureDetail = NotePersistenceFailure.BuildFailureDetail(
				systemName,
				"validate",
				$"Detail='{stateName} is missing caret_line or caret_column.'"
			);
			return false;
		}

		viewState = new NoteViewState(caretLine, caretColumn);
		return true;
	}

	private static void ValidateViewStateForSerialization(NoteSystemDocument document)
	{
		if (document == null)
			throw new ArgumentNullException(nameof(document));

		if (document.SystemViewState != null && document.Note.Length == 0)
			throw new InvalidOperationException("System view-state cannot be serialized without a System Note.");

		foreach (KeyValuePair<string, NoteViewState> folderViewState in document.FolderViewStates)
		{
			if (string.IsNullOrEmpty(folderViewState.Key))
				throw new InvalidOperationException("Folder view-state path cannot be empty.");

			if (folderViewState.Value == null)
				throw new InvalidOperationException("Folder view-state cannot be null.");

			if (!document.Folders.ContainsKey(folderViewState.Key))
				throw new InvalidOperationException("Folder view-state cannot be serialized without its Folder Note.");
		}
	}

	private static void WriteCaretState(Utf8JsonWriter writer, NoteViewState viewState)
	{
		if (viewState == null)
			throw new ArgumentNullException(nameof(viewState));

		writer.WriteStartObject();
		writer.WriteNumber("caret_line", viewState.CaretLine);
		writer.WriteNumber("caret_column", viewState.CaretColumn);
		writer.WriteEndObject();
	}

	private static bool FailDuplicateRootProperty(
		string systemName,
		string propertyName,
		out string failureDetail
	)
	{
		failureDetail = NotePersistenceFailure.BuildFailureDetail(
			systemName,
			"validate",
			$"Detail='Duplicate root property {NotePersistenceFailure.FormatForDetail(propertyName)}.'"
		);
		return false;
	}

	private static bool FailDuplicateViewStateProperty(
		string systemName,
		string propertyName,
		out string failureDetail
	)
	{
		failureDetail = NotePersistenceFailure.BuildFailureDetail(
			systemName,
			"validate",
			$"Detail='Duplicate view_state property {NotePersistenceFailure.FormatForDetail(propertyName)}.'"
		);
		return false;
	}

	private static bool FailDuplicateCaretStateProperty(
		string systemName,
		string stateName,
		string propertyName,
		out string failureDetail
	)
	{
		failureDetail = NotePersistenceFailure.BuildFailureDetail(
			systemName,
			"validate",
			$"Detail='Duplicate caret-state property {NotePersistenceFailure.FormatForDetail(propertyName)} in {stateName}.'"
		);
		return false;
	}
}
#endif
