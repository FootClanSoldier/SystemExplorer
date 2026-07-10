<p align="center">
	<a href="https://github.com/FootClanSoldier/SystemExplorer">
  <img src="icon.png" width="300" alt="System Explorer Logo">
	</a>
</p>

<h1 align="center">System Explorer</h1>

<p align="center">
  <a href="https://godotengine.org/">
    <img src="https://img.shields.io/badge/Godot-4.6-blue" alt="Godot">
  </a>

  <a href="#about">
    <img src="https://img.shields.io/badge/C%23-.NET-purple" alt="C#">
  </a>

  <a href="https://github.com/FootClanSoldier/SystemExplorer/releases">
    <img src="https://img.shields.io/badge/Version-1.4.0-green" alt="Version">
  </a>

  <a href="./LICENSE">
    <img src="https://img.shields.io/badge/License-MIT-brightgreen" alt="License">
  </a>
</p>

> Architecture-focused navigation plugin for Godot C# projects.

> **Note:** The `main` branch may contain work-in-progress changes for the next plugin version. Stable versions are available from the official releases.

---

<p align="center">
  <a href="#contents">
    <img src="screenshots/overview.png" alt="System Explorer Overview">
  </a>
</p>

<a id="contents"></a>

## Contents

- [About](#about)
- [Why](#why)
- [Features](#features)
- [Installation](#installation)
- [Script Templates](#script-templates)
- [Data Storage](#data-storage)
- [Known Issues](#known-issues)
- [Future Ideas](#future-ideas)

# About

System Explorer is a Godot C# editor plugin that lets you organize your project from an architectural perspective instead of relying solely on the FileSystem dock.

Create custom systems and folders, organize scripts and scene links, and navigate large codebases without changing your physical project structure.

---

# Why?

Large C# projects often end up with deep folder structures:

```text
Game
└── Gameplay
    └── Entities
        └── Player
            └── Modules
```

System Explorer lets you navigate your project from a higher-level architectural perspective instead:

```text
Core
GameFlow
Sound
Player
UI
```

Organize your code around your game's architecture, not simply where files happen to live on disk.

---

# Features

## Organization

- Create systems and folders
- Create new scripts or add existing ones
- Add multiple scripts or scenes in a single operation
- Rename and remove items
- Organize systems, folders, scripts, and scenes using drag & drop
- Lock systems, folders, scripts, and scene links to prevent accidental drag & drop
- Virtual organization that doesn't modify your project structure

---

## Navigation

- Architecture-focused project navigation
- Filter scripts across every system
- Open scripts with a single click
- Open containing folder
- Expansion state persistence

---

## Scene Integration

Connect your architecture directly to the scenes that use those scripts.

- Link scripts to their corresponding scenes
- Single-click opens the script
- Double-click opens both the script and its linked scene
- Unlink scene associations
- Automatic recovery if linked scenes are moved or deleted

Scene links are stored in `systems.json` and persist between editor sessions.

---

## Workflow

Several quality-of-life features help speed up common workflows:

- Press **Enter** to confirm dialogs and create new systems
- Click the **+** button inside the System Name field to create a system
- Context menus with optional icons
- Project Settings support for plugin options
- Script tooltips
- **Double-click** systems and folders to expand or collapse them
- **Ctrl + Click** expands or collapses systems and folders with a single click
- **Ctrl + Shift** collapses the entire tree when a system or folder is selected
- **Middle Mouse Button** while hovering an item locks or unlocks it
- **Ctrl + L** locks or unlocks the selected item
- **Ctrl + Delete** opens the delete dialog
- Expansion state is automatically preserved between common operations

---

# Installation

1. Copy the addon into:

```text
addons/system_explorer/
```

2. Open the project in Godot.

3. Make sure the project contains a C# solution/project file.

If the project has not been initialized for C#, create one via:

```text
Project
→ Tools
→ C#
→ Create C# Solution
```

4. Build the C# project.

5. Enable the plugin:

```text
Project
→ Project Settings
→ Plugins
```

6. Enable System Explorer.

> **Note:** System Explorer is designed for C# projects. A valid Godot C# solution/project file must exist before the plugin can be compiled and used.

---

## Script Templates

New scripts are generated using:

```text
addons/system_explorer/Resources/script_template.txt
```

You can customize this template to match your coding style, namespaces, project structure, or preferred class layout.

The placeholder:

```text
{{CLASS_NAME}}
```

is automatically replaced with the script file name.

Example:

```csharp
using Godot;

namespace MyNamespace
{
	public sealed class {{CLASS_NAME}}
	{

	}
}
```

If no template file is found, System Explorer falls back to a built-in default template.

---

# Data Storage

System Explorer stores its configuration in:

```text
addons/system_explorer/Resources/systems.json
```

This file stores all System Explorer data, including systems, folders, scene links, and plugin state.

It can safely be committed to source control.

---

# Known Issues

## Godot Editor Cache Warnings

When deleting scripts from the filesystem through the plugin, Godot may occasionally display warnings related to scripts that no longer exist.

These warnings originate from Godot's internal editor cache and do not affect plugin functionality.

They typically disappear after rebuilding or reopening the project.

---

# Future Ideas

- Multiple architecture views
- Custom icons
- More C# workflow tools
- Lightweight autocomplete suggestions in script editor

---

# Feedback

Feedback, suggestions, bug reports, and feature requests are always welcome.

Future development will primarily be driven by real-world usage and community feedback.
