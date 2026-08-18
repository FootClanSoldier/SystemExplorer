#if TOOLS
using Godot;

public partial class SystemExplorerPlugin
{
	#region Project Settings
	private const string ProjectSettingsPath = "addons/system_explorer";
	private const string DiagnosticsSettingsPath = ProjectSettingsPath + "/diagnostics";

	private const string EnableQuickActionsSetting = ProjectSettingsPath + "/enable_quick_actions";
	private const string LegacyDebugStateSetting = ProjectSettingsPath + "/enable_debug_state";
	private const string DebugStateSetting = DiagnosticsSettingsPath + "/enable_debug_state";
	private const string LegacyNavigationStressSetting =
		DiagnosticsSettingsPath + "/navigation_stress_enabled";

	private bool EnableQuickActions => GetBoolProjectSetting(EnableQuickActionsSetting, false);

	// Enable only when investigating editor state/save/Quick Action issues.
	private bool DebugState => GetBoolProjectSetting(DebugStateSetting, false);

	private void EnsureProjectSettings()
	{
		MigrateLegacyProjectSettings();
		EnsureBoolProjectSetting(EnableQuickActionsSetting, false);
		EnsureBoolProjectSetting(DebugStateSetting, false);
	}

	private static void MigrateLegacyProjectSettings()
	{
		if (ProjectSettings.HasSetting(LegacyDebugStateSetting))
		{
			if (!ProjectSettings.HasSetting(DebugStateSetting))
			{
				bool legacyDebugState = GetBoolProjectSetting(
					LegacyDebugStateSetting,
					false
				);
				ProjectSettings.SetSetting(DebugStateSetting, legacyDebugState);
			}

			ProjectSettings.SetSetting(LegacyDebugStateSetting, default(Variant));
		}

		if (ProjectSettings.HasSetting(LegacyNavigationStressSetting))
		{
			ProjectSettings.SetSetting(
				LegacyNavigationStressSetting,
				default(Variant)
			);
		}
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
