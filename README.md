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
    <img src="https://img.shields.io/badge/Version-1.2.0-green" alt="Version">
  </a>

  <a href="./LICENSE">
    <img src="https://img.shields.io/badge/License-MIT-brightgreen" alt="License">
  </a>
</p>

> Architecture-focused navigation plugin for Godot C# projects.
---


# About

System Explorer is a Godot C# editor plugin that provides an architecture-focused view of your project.

Instead of navigating large projects through the FileSystem dock, you can organize scripts into custom systems and folders that reflect the architecture of your game—without changing your physical project structure.

<p align="center">
  <img src="screenshots/overview.png" alt="System Explorer Overview">
</p>

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

Organize your code according to how your game is structured, not simply where files happen to live on disk.

---

# Features

## Organization

<img src="screenshots/demo.gif" alt="Systems">

- Create systems and folders
- Create new scripts or add existing ones
- Rename and remove items
- Drag & drop organization
- Virtual organization that doesn't modify your project structure

---

## Navigation

- Architecture-focused project navigation
- Filter scripts across every system
- Open scripts with a single click
- Reopen already selected scripts
- Open File Path
- Expansion state persistence

---

## Scene Integration

Connect your architecture directly to the scenes that use those scripts.

- Link scripts to scenes
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
- Context menus for common actions
- Script templates
- Script tooltips
- **Shift + Click** expands or collapses entire branches
- **ctrl+ Delete** opens the delete dialog
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
addons/system_explorer/script_template.txt
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
addons/system_explorer/systems.json
```

This file can safely be committed to source control.

---

# Known Issues

## Godot Editor Cache Warnings

When deleting scripts from the filesystem through the plugin, Godot may occasionally display warnings related to scripts that no longer exist.

These warnings originate from Godot's internal editor cache and do not affect plugin functionality.

They typically disappear after rebuilding or reopening the project.

---

# Future Ideas

- Multiple architecture views
- Namespace refactoring
- Custom icons
- Beautify systems
- Beautify folders
- Beautify scripts

---

# Feedback

Feedback, suggestions, bug reports, and feature requests are always welcome.

Future development will primarily be driven by real-world usage and community feedback.

---
