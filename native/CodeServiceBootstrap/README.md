# System Explorer CodeService Bootstrap

`Native/CodeServiceBootstrap` contains the Windows x86_64 GDExtension used to
start SystemExplorer.CodeService as early as possible during Godot startup.

The extension runs at `GDEXTENSION_INITIALIZATION_CORE` and, when native
bootstrap has been authorized by the managed plugin, it:

1. resolves the Godot project root;
2. reads the persisted `last_script` hint from
   `.godot/system_explorer/tree_state.json`;
3. validates it as a project-relative C# path;
4. starts CodeService through the canonical installed tool shim;
5. optionally passes the document as:

   ```text
   --startup-document "<project-relative-path>"
   ```

This allows CodeService/Roslyn autocomplete warmup to run in parallel with
Godot editor startup.

## Responsibility boundary

Native bootstrap only performs early process startup and bounded startup-document
discovery.

The managed plugin remains responsible for bootstrap authorization, Service
adoption/fallback, session ownership, Workspace Ready, and authoritative editor
document synchronization.

CodeService remains responsible for workspace construction, Roslyn, semantic
readiness, completion warmup, and document processing.

## Runtime files

Descriptor:

`/Scripts/Native/CodeServiceBootstrap/CodeServiceBootstrap.gdextension`

DLL:

`/Scripts/Native/CodeServiceBootstrap/bin/CodeServiceBootstrap.windows.editor.x86_64.dll`

Bootstrap config:

`/Scripts/Native/CodeServiceBootstrap/native_bootstrap.ini`

The config is generated and maintained by the managed plugin.

## Build

Windows x86_64, MSVC:

```text
cmake -S native\CodeServiceBootstrap -B native\CodeServiceBootstrap\build -A x64
cmake --build native\CodeServiceBootstrap\build --config Release
```

Output:

`native/CodeServiceBootstrap/bin/CodeServiceBootstrap.windows.editor.x86_64.dll`
