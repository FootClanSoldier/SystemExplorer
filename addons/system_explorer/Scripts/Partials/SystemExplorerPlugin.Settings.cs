#if TOOLS
using System;
using Godot;

public partial class SystemExplorerPlugin
{
	#region Project Settings
	private const string ProjectSettingsPath = "addons/system_explorer";
	private const string EnableQuickActionsSetting = ProjectSettingsPath + "/enable_quick_actions";
	private const string DebugStateSetting = ProjectSettingsPath + "/enable_debug_state";
	private static readonly StringName ProjectSettingsChangedSignalName = new("settings_changed");

	private GodotObject _projectSettingsSignalSource;
	private bool _projectSettingsDebugStateObservationInitialized;
	private bool _projectSettingsObservedDebugState;

	private bool EnableQuickActions => GetBoolProjectSetting(EnableQuickActionsSetting, false);

	// Enable only when investigating editor state/save/Quick Action issues.
	private bool DebugState => GetBoolProjectSetting(DebugStateSetting, false);

	private void EnsureProjectSettings()
	{
		EnsureBoolProjectSetting(EnableQuickActionsSetting, false);
		EnsureBoolProjectSetting(DebugStateSetting, false);
	}

	private void InitializeProjectSettingsDebugStateObservation()
	{
		_projectSettingsObservedDebugState = DebugState;
		_projectSettingsDebugStateObservationInitialized = true;
	}

	private bool EnsureProjectSettingsSignalIntegrationCurrent()
	{
		try
		{
			GodotObject source = ProjectSettings.Singleton;
			if (
				!IsPluginSignalConnected(
					source,
					ProjectSettingsChangedSignalName,
					nameof(OnProjectSettingsChangedSignal)
				)
				&& !TryConnectPluginSignal(
					source,
					ProjectSettingsChangedSignalName,
					nameof(OnProjectSettingsChangedSignal),
					nameof(ProjectSettings)
				)
			)
			{
				return false;
			}

			if (
				!IsPluginSignalConnected(
					source,
					ProjectSettingsChangedSignalName,
					nameof(OnProjectSettingsChangedSignal)
				)
			)
			{
				return false;
			}

			_projectSettingsSignalSource = source;
			return true;
		}
		catch (Exception exception)
		{
			DebugLogger.LogOperation(
				"Project Settings signal integration failed",
				$"Exception='{exception}'"
			);
			return false;
		}
	}

	private void DisconnectProjectSettingsSignalIntegration()
	{
		GodotObject source = _projectSettingsSignalSource;
		if (!IsValidGodotObject(source))
		{
			try
			{
				source = ProjectSettings.Singleton;
			}
			catch
			{
				source = null;
			}
		}

		DisconnectPluginSignal(
			source,
			ProjectSettingsChangedSignalName,
			nameof(OnProjectSettingsChangedSignal),
			nameof(ProjectSettings)
		);
		_projectSettingsSignalSource = null;
	}

	private void OnProjectSettingsChangedSignal()
	{
		if (!EnsureManagedAssemblyStateCurrent("Project Settings Changed"))
			return;

		string[] changedSettings;
		try
		{
			changedSettings = ProjectSettings.GetChangedSettings();
		}
		catch (Exception exception)
		{
			DebugLogger.LogOperation(
				"Project Settings changed-settings read failed",
				$"Exception='{exception}'"
			);
			return;
		}

		bool debugStateChanged = false;
		foreach (string settingPath in changedSettings)
		{
			if (string.Equals(settingPath, DebugStateSetting, StringComparison.Ordinal))
			{
				debugStateChanged = true;
				break;
			}
		}

		if (!debugStateChanged)
			return;

		bool diagnosticLogging = DebugState;
		if (
			_projectSettingsDebugStateObservationInitialized
			&& _projectSettingsObservedDebugState == diagnosticLogging
		)
		{
			return;
		}

		_projectSettingsObservedDebugState = diagnosticLogging;
		_projectSettingsDebugStateObservationInitialized = true;
		TrySynchronizeCodeServiceNativeBootstrapDiagnosticLogging(
			diagnosticLogging,
			"Project Settings Changed"
		);
	}

	private static bool GetBoolProjectSetting(string settingPath, bool defaultValue)
	{
		if (!ProjectSettings.HasSetting(settingPath))
			return defaultValue;

		Variant value = ProjectSettings.GetSetting(settingPath, defaultValue);

		return value.VariantType == Variant.Type.Bool ? value.AsBool() : defaultValue;
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
