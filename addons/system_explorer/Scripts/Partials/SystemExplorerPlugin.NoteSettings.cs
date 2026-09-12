#if TOOLS
using Godot;
using System;

public partial class SystemExplorerPlugin
{
	private const string NoteSettingsPath = NotesFolderPath + "/note_settings.ini";
	private const string NoteSettingsEditorSection = "editor";
	private const string NoteSettingsTextLeftPaddingKey = "text_left_padding";
	private const string NoteSettingsFontSizeKey = "font_size";
	private const string NoteSettingsWindowSizeKey = "window_size";
	private const string NoteSettingsCenterPositionKey = "center_position";
	private const string NoteSettingsPositionKey = "position";
	private const string NoteSettingsMaximizedKey = "maximized";
	private const int NoteTextLeftPaddingDefault = 25;
	private const int NoteTextLeftPaddingMinimum = 0;
	private const int NoteTextLeftPaddingMaximum = 256;
	private const int NoteFontSizeMinimum = 8;
	private const int NoteFontSizeMaximum = 80;
	private const int NoteFontSizeFallback = 16;

	private sealed class NoteEditorSettings
	{
		public int TextLeftPadding { get; set; } = NoteTextLeftPaddingDefault;
		public bool HasFontSize { get; set; }
		public int FontSize { get; set; }
		public bool HasWindowSize { get; set; }
		public Vector2I WindowSize { get; set; }
		public bool CenterPosition { get; set; } = true;
		public bool HasPosition { get; set; }
		public Vector2I Position { get; set; }
		public bool Maximized { get; set; }
	}

	private sealed class NoteEditorSettingsMutation
	{
		public bool HasFontSize { get; set; }
		public int FontSize { get; set; }
		public bool HasWindowSize { get; set; }
		public Vector2I WindowSize { get; set; }
		public bool HasCenterPosition { get; set; }
		public bool CenterPosition { get; set; }
		public bool HasPosition { get; set; }
		public Vector2I Position { get; set; }
		public bool HasMaximized { get; set; }
		public bool Maximized { get; set; }

		public bool HasAny =>
			HasFontSize
			|| HasWindowSize
			|| HasCenterPosition
			|| HasPosition
			|| HasMaximized;
	}

	private bool TryReadGlobalNoteEditorSettings(
		out NoteEditorSettings settings,
		out string failureDetail
	)
	{
		settings = new NoteEditorSettings();
		failureDetail = "";

		try
		{
			if (!FileAccess.FileExists(NoteSettingsPath))
				return true;

			var config = new ConfigFile();
			Error loadError = config.Load(NoteSettingsPath);
			if (loadError != Error.Ok)
			{
				failureDetail =
					$"Could not load Note settings from '{NoteSettingsPath}'. Error='{loadError}'.";
				return false;
			}

			ReadNoteTextLeftPaddingSetting(config, settings);
			ReadNoteFontSizeSetting(config, settings);
			ReadNoteWindowSizeSetting(config, settings);
			ReadNoteCenterPositionSetting(config, settings);
			ReadNotePositionSetting(config, settings);
			ReadNoteMaximizedSetting(config, settings);
			return true;
		}
		catch (Exception exception)
		{
			settings = new NoteEditorSettings();
			failureDetail =
				$"Could not read Note settings from '{NoteSettingsPath}'. Exception='{exception}'";
			return false;
		}
	}

	private void ReadNoteTextLeftPaddingSetting(
		ConfigFile config,
		NoteEditorSettings settings
	)
	{
		try
		{
			if (!config.HasSectionKey(NoteSettingsEditorSection, NoteSettingsTextLeftPaddingKey))
				return;

			Variant rawTextLeftPadding = config.GetValue(
				NoteSettingsEditorSection,
				NoteSettingsTextLeftPaddingKey
			);

			if (rawTextLeftPadding.VariantType != Variant.Type.Int)
			{
				LogInvalidGlobalNoteSetting(
					NoteSettingsTextLeftPaddingKey,
					$"Expected integer but VariantType='{rawTextLeftPadding.VariantType}'."
				);
				return;
			}

			long persistedTextLeftPadding = rawTextLeftPadding.AsInt64();
			if (
				persistedTextLeftPadding < NoteTextLeftPaddingMinimum
				|| persistedTextLeftPadding > NoteTextLeftPaddingMaximum
			)
			{
				LogInvalidGlobalNoteSetting(
					NoteSettingsTextLeftPaddingKey,
					$"Value '{persistedTextLeftPadding}' is outside the supported range {NoteTextLeftPaddingMinimum}..{NoteTextLeftPaddingMaximum}."
				);
				return;
			}

			settings.TextLeftPadding = (int)persistedTextLeftPadding;
		}
		catch (Exception exception)
		{
			settings.TextLeftPadding = NoteTextLeftPaddingDefault;
			LogInvalidGlobalNoteSetting(
				NoteSettingsTextLeftPaddingKey,
				$"Could not read value. Exception='{exception}'"
			);
		}
	}

	private void ReadNoteFontSizeSetting(ConfigFile config, NoteEditorSettings settings)
	{
		try
		{
			if (!config.HasSectionKey(NoteSettingsEditorSection, NoteSettingsFontSizeKey))
				return;

			Variant rawFontSize = config.GetValue(
				NoteSettingsEditorSection,
				NoteSettingsFontSizeKey
			);

			if (rawFontSize.VariantType != Variant.Type.Int)
			{
				LogInvalidGlobalNoteSetting(
					NoteSettingsFontSizeKey,
					$"Expected integer but VariantType='{rawFontSize.VariantType}'."
				);
				return;
			}

			long persistedFontSize = rawFontSize.AsInt64();
			if (persistedFontSize < NoteFontSizeMinimum || persistedFontSize > NoteFontSizeMaximum)
			{
				LogInvalidGlobalNoteSetting(
					NoteSettingsFontSizeKey,
					$"Value '{persistedFontSize}' is outside the supported range {NoteFontSizeMinimum}..{NoteFontSizeMaximum}."
				);
				return;
			}

			settings.HasFontSize = true;
			settings.FontSize = (int)persistedFontSize;
		}
		catch (Exception exception)
		{
			LogInvalidGlobalNoteSetting(
				NoteSettingsFontSizeKey,
				$"Could not read value. Exception='{exception}'"
			);
		}
	}

	private void ReadNoteWindowSizeSetting(ConfigFile config, NoteEditorSettings settings)
	{
		try
		{
			if (!config.HasSectionKey(NoteSettingsEditorSection, NoteSettingsWindowSizeKey))
				return;

			Variant rawWindowSize = config.GetValue(
				NoteSettingsEditorSection,
				NoteSettingsWindowSizeKey
			);

			if (rawWindowSize.VariantType != Variant.Type.Vector2I)
			{
				LogInvalidGlobalNoteSetting(
					NoteSettingsWindowSizeKey,
					$"Expected Vector2I but VariantType='{rawWindowSize.VariantType}'."
				);
				return;
			}

			Vector2I persistedWindowSize = rawWindowSize.AsVector2I();
			if (persistedWindowSize.X <= 0 || persistedWindowSize.Y <= 0)
			{
				LogInvalidGlobalNoteSetting(
					NoteSettingsWindowSizeKey,
					$"Value '{persistedWindowSize}' must have positive X and Y components."
				);
				return;
			}

			if (
				persistedWindowSize.X < NoteDialogMinimumSize.X
				|| persistedWindowSize.Y < NoteDialogMinimumSize.Y
			)
			{
				LogInvalidGlobalNoteSetting(
					NoteSettingsWindowSizeKey,
					$"Value '{persistedWindowSize}' is smaller than the Note dialog minimum '{NoteDialogMinimumSize}'."
				);
				return;
			}

			settings.HasWindowSize = true;
			settings.WindowSize = persistedWindowSize;
		}
		catch (Exception exception)
		{
			LogInvalidGlobalNoteSetting(
				NoteSettingsWindowSizeKey,
				$"Could not read value. Exception='{exception}'"
			);
		}
	}

	private void ReadNoteCenterPositionSetting(ConfigFile config, NoteEditorSettings settings)
	{
		try
		{
			if (!config.HasSectionKey(NoteSettingsEditorSection, NoteSettingsCenterPositionKey))
				return;

			Variant rawCenterPosition = config.GetValue(
				NoteSettingsEditorSection,
				NoteSettingsCenterPositionKey
			);

			if (rawCenterPosition.VariantType != Variant.Type.Bool)
			{
				LogInvalidGlobalNoteSetting(
					NoteSettingsCenterPositionKey,
					$"Expected bool but VariantType='{rawCenterPosition.VariantType}'."
				);
				return;
			}

			settings.CenterPosition = rawCenterPosition.AsBool();
		}
		catch (Exception exception)
		{
			settings.CenterPosition = true;
			LogInvalidGlobalNoteSetting(
				NoteSettingsCenterPositionKey,
				$"Could not read value. Exception='{exception}'"
			);
		}
	}

	private void ReadNotePositionSetting(ConfigFile config, NoteEditorSettings settings)
	{
		try
		{
			if (!config.HasSectionKey(NoteSettingsEditorSection, NoteSettingsPositionKey))
				return;

			Variant rawPosition = config.GetValue(
				NoteSettingsEditorSection,
				NoteSettingsPositionKey
			);

			if (rawPosition.VariantType != Variant.Type.Vector2I)
			{
				LogInvalidGlobalNoteSetting(
					NoteSettingsPositionKey,
					$"Expected Vector2I but VariantType='{rawPosition.VariantType}'."
				);
				return;
			}

			settings.HasPosition = true;
			settings.Position = rawPosition.AsVector2I();
		}
		catch (Exception exception)
		{
			LogInvalidGlobalNoteSetting(
				NoteSettingsPositionKey,
				$"Could not read value. Exception='{exception}'"
			);
		}
	}

	private void ReadNoteMaximizedSetting(ConfigFile config, NoteEditorSettings settings)
	{
		try
		{
			if (!config.HasSectionKey(NoteSettingsEditorSection, NoteSettingsMaximizedKey))
				return;

			Variant rawMaximized = config.GetValue(
				NoteSettingsEditorSection,
				NoteSettingsMaximizedKey
			);

			if (rawMaximized.VariantType != Variant.Type.Bool)
			{
				LogInvalidGlobalNoteSetting(
					NoteSettingsMaximizedKey,
					$"Expected bool but VariantType='{rawMaximized.VariantType}'."
				);
				return;
			}

			settings.Maximized = rawMaximized.AsBool();
		}
		catch (Exception exception)
		{
			settings.Maximized = false;
			LogInvalidGlobalNoteSetting(
				NoteSettingsMaximizedKey,
				$"Could not read value. Exception='{exception}'"
			);
		}
	}

	private void LogInvalidGlobalNoteSetting(string key, string detail)
	{
		DebugLogger.LogOperation(
			"Invalid global Note editor setting ignored",
			$"Path='{NoteSettingsPath}', Key='{NoteSettingsEditorSection}/{key}', Detail='{detail}'"
		);
	}

	private bool TrySaveGlobalNoteEditorSettings(
		NoteEditorSettingsMutation mutation,
		out string failureDetail
	)
	{
		failureDetail = "";

		if (mutation == null || !mutation.HasAny)
			return true;

		if (
			mutation.HasFontSize
			&& (mutation.FontSize < NoteFontSizeMinimum || mutation.FontSize > NoteFontSizeMaximum)
		)
		{
			failureDetail =
				$"Refused to save Note font size '{mutation.FontSize}' outside the supported range {NoteFontSizeMinimum}..{NoteFontSizeMaximum}.";
			return false;
		}

		if (
			mutation.HasWindowSize
			&& (
				mutation.WindowSize.X < NoteDialogMinimumSize.X
				|| mutation.WindowSize.Y < NoteDialogMinimumSize.Y
			)
		)
		{
			failureDetail =
				$"Refused to save Note window size '{mutation.WindowSize}' smaller than the Note dialog minimum '{NoteDialogMinimumSize}'.";
			return false;
		}

		try
		{
			var config = new ConfigFile();

			if (FileAccess.FileExists(NoteSettingsPath))
			{
				Error loadError = config.Load(NoteSettingsPath);
				if (loadError != Error.Ok)
				{
					DebugLogger.LogOperation(
						"Note settings could not be loaded during explicit preference save; replacing file",
						$"Path='{NoteSettingsPath}', Error='{loadError}'"
					);
					config = new ConfigFile();
				}
			}

			if (!TryGetAbsoluteNotesDirectoryPath(out string absoluteNotesPath, out failureDetail))
				return false;

			System.IO.Directory.CreateDirectory(absoluteNotesPath);

			if (!config.HasSectionKey(NoteSettingsEditorSection, NoteSettingsTextLeftPaddingKey))
			{
				config.SetValue(
					NoteSettingsEditorSection,
					NoteSettingsTextLeftPaddingKey,
					NoteTextLeftPaddingDefault
				);
			}

			if (mutation.HasFontSize)
			{
				config.SetValue(
					NoteSettingsEditorSection,
					NoteSettingsFontSizeKey,
					mutation.FontSize
				);
			}

			if (mutation.HasWindowSize)
			{
				config.SetValue(
					NoteSettingsEditorSection,
					NoteSettingsWindowSizeKey,
					mutation.WindowSize
				);
			}

			if (mutation.HasCenterPosition)
			{
				config.SetValue(
					NoteSettingsEditorSection,
					NoteSettingsCenterPositionKey,
					mutation.CenterPosition
				);
			}

			if (mutation.HasPosition)
			{
				config.SetValue(
					NoteSettingsEditorSection,
					NoteSettingsPositionKey,
					mutation.Position
				);
			}

			if (mutation.HasMaximized)
			{
				config.SetValue(
					NoteSettingsEditorSection,
					NoteSettingsMaximizedKey,
					mutation.Maximized
				);
			}

			Error saveError = config.Save(NoteSettingsPath);
			if (saveError != Error.Ok)
			{
				failureDetail =
					$"Could not save Note settings to '{NoteSettingsPath}'. Error='{saveError}'.";
				return false;
			}

			return true;
		}
		catch (Exception exception)
		{
			failureDetail =
				$"Could not save Note settings to '{NoteSettingsPath}'. Exception='{exception}'";
			return false;
		}
	}
}
#endif
