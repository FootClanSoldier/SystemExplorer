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
			int formatVersion = 0;
			string storedSystemName = null;
			string note = null;
			Dictionary<string, string> folders = null;

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

			document = new NoteSystemDocument(expectedSystemName)
			{
				Note = note,
			};

			foreach (KeyValuePair<string, string> folder in folders)
				document.Folders.Add(folder.Key, folder.Value);

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
			writer.WriteEndObject();
			writer.Flush();
		}

		return stream.ToArray();
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
}
#endif
