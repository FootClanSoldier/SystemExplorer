#if TOOLS
using Godot;
using SystemExplorer.Diagnostics;

public partial class SystemExplorerPlugin
{
	#region Project Settings
	private const string ProjectSettingsPath = "addons/system_explorer";
	private const string EnableLoggingSetting =
		ProjectSettingsPath + "/Diagnostics/Enable_Logging";
	private const string LegacyEnableQuickActionsSetting =
		ProjectSettingsPath + "/enable_quick_actions";
	private const string LegacyDebugStateSetting =
		ProjectSettingsPath + "/enable_debug_state";

	private static readonly StringName ProjectSettingsChangedSignalName =
		new("project_settings_changed");

	private bool _projectSettingsChangedSubscribed;
	private bool _lastObservedEnableLogging;
	private string _diagnosticLogDirectory = "";

	private bool EnableLogging => GetBoolProjectSetting(EnableLoggingSetting, false);

	private void EnsureProjectSettings()
	{
		bool shouldPersistLegacyCleanup = false;

		if (
			!ProjectSettings.HasSetting(EnableLoggingSetting)
			&& TryGetBoolProjectSetting(LegacyDebugStateSetting, out bool legacyDebugState)
		)
		{
			ProjectSettings.SetSetting(EnableLoggingSetting, legacyDebugState);
			shouldPersistLegacyCleanup = true;
		}

		EnsureBoolProjectSetting(EnableLoggingSetting, false);

		shouldPersistLegacyCleanup |= RemoveLegacyProjectSetting(
			LegacyEnableQuickActionsSetting
		);
		shouldPersistLegacyCleanup |= RemoveLegacyProjectSetting(
			LegacyDebugStateSetting
		);

		if (!shouldPersistLegacyCleanup)
			return;

		Error saveResult = ProjectSettings.Save();
		if (saveResult != Error.Ok)
		{
			GD.PushWarning(
				$"[SystemExplorer] Could not persist Project Settings cleanup. Error={saveResult}."
			);
		}
	}

	private void InitializeDiagnosticLogging()
	{
		try
		{
			string projectUserDataDirectory = ProjectSettings.GlobalizePath("user://");
			_diagnosticLogDirectory =
				SystemExplorerDiagnosticLogPathResolver.ResolveDiagnosticDirectory(
					projectUserDataDirectory
				);
		}
		catch
		{
			_diagnosticLogDirectory = "";
		}

		_lastObservedEnableLogging = EnableLogging;

		if (!_projectSettingsChangedSubscribed)
		{
			_projectSettingsChangedSubscribed = TryConnectPluginSignal(
				this,
				ProjectSettingsChangedSignalName,
				nameof(OnSystemExplorerProjectSettingsChanged),
				"SystemExplorerPlugin project settings"
			);
		}

		if (_lastObservedEnableLogging)
			AnnounceDiagnosticLoggingEnabled();
	}

	private void ShutdownDiagnosticLoggingSettingsObserver()
	{
		if (!_projectSettingsChangedSubscribed)
			return;

		DisconnectPluginSignal(
			this,
			ProjectSettingsChangedSignalName,
			nameof(OnSystemExplorerProjectSettingsChanged),
			"SystemExplorerPlugin project settings"
		);
		_projectSettingsChangedSubscribed = false;
	}

	private void OnSystemExplorerProjectSettingsChanged()
	{
		bool isLoggingEnabled = EnableLogging;
		if (isLoggingEnabled == _lastObservedEnableLogging)
			return;

		_lastObservedEnableLogging = isLoggingEnabled;

		if (isLoggingEnabled)
			AnnounceDiagnosticLoggingEnabled();
	}

	private void AnnounceDiagnosticLoggingEnabled()
	{
		if (!EnableLogging)
			return;

		if (DebugLogger.TryEnsurePersistentLogFile(out string filePath))
		{
			GD.Print($"[SystemExplorer] Logging enabled. Log file: '{filePath}'");
			return;
		}

		GD.PushWarning(
			"[SystemExplorer] Logging is enabled, but the diagnostic log file could not be opened."
		);
	}

	private static bool TryGetBoolProjectSetting(string settingPath, out bool value)
	{
		value = false;

		if (!ProjectSettings.HasSetting(settingPath))
			return false;

		Variant settingValue = ProjectSettings.GetSetting(settingPath, false);
		if (settingValue.VariantType != Variant.Type.Bool)
			return false;

		value = settingValue.AsBool();
		return true;
	}

	private static bool GetBoolProjectSetting(string settingPath, bool defaultValue)
	{
		return TryGetBoolProjectSetting(settingPath, out bool value)
			? value
			: defaultValue;
	}

	private static void EnsureBoolProjectSetting(string settingPath, bool defaultValue)
	{
		if (!ProjectSettings.HasSetting(settingPath))
			ProjectSettings.SetSetting(settingPath, defaultValue);

		ProjectSettings.SetInitialValue(settingPath, defaultValue);
		ProjectSettings.AddPropertyInfo(
			new Godot.Collections.Dictionary
			{
				{ "name", settingPath },
				{ "type", (int)Variant.Type.Bool },
			}
		);

		ProjectSettings.SetAsBasic(settingPath, true);
	}

	private static bool RemoveLegacyProjectSetting(string settingPath)
	{
		if (!ProjectSettings.HasSetting(settingPath))
			return false;

		ProjectSettings.SetSetting(settingPath, default(Variant));
		return true;
	}

	private void AddContextPopupMenuItem(
		PopupMenu menu,
		string label,
		int id,
		Texture2D icon,
		string editorShortcutPath = ""
	)
	{
		if (menu == null || !GodotObject.IsInstanceValid(menu))
			return;

		if (icon == null)
			menu.AddItem(label, id);
		else
			menu.AddIconItem(icon, label, id);

		ApplyContextPopupMenuItemShortcut(menu, id, editorShortcutPath);
	}

	private void ApplyContextPopupMenuItemShortcut(
		PopupMenu menu,
		int id,
		string editorShortcutPath
	)
	{
		if (
			menu == null
			|| !GodotObject.IsInstanceValid(menu)
			|| string.IsNullOrWhiteSpace(editorShortcutPath)
		)
		{
			return;
		}

		int index = menu.GetItemIndex(id);

		if (index < 0)
			return;

		if (
			!TryGetCurrentEditorShortcut(
				editorShortcutPath,
				out Shortcut shortcut
			)
		)
		{
			return;
		}

		menu.SetItemShortcut(index, shortcut, global: false);
		menu.SetItemShortcutDisabled(index, true);
	}

	private void AddContextMenuIconItem(
		string label,
		int id,
		Texture2D icon,
		string editorShortcutPath = ""
	)
	{
		AddContextPopupMenuItem(
			_contextMenu,
			label,
			id,
			icon,
			editorShortcutPath
		);
	}

	#endregion
}
#endif
