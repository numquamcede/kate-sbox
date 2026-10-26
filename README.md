# kate-sbox

Use [Kate](https://kate-editor.org) as your [s&box](https://sbox.game) code editor, with real C# IntelliSense and Cate Pro theme.

- **C# IntelliSense** from the Roslyn language server, the same engine VS Code's C# Dev Kit runs on: completion, hover docs, go to definition (into decompiled engine code too), errors and warnings, semantic highlighting, inlay hints, rename, code actions and formatting. It loads the `.slnx` s&box generates, including the engine's editor projects.
- **Kate in s&box**: a **Kate** entry under *Edit › Preferences › Code Editor*, so double-clicking a component or a compile error opens Kate at the right line.
- **s&box file types**: highlighting for `.shader`/`.hlsl` (the `HEADER`/`MODES`/`VS`/`PS`/`CS` blocks, annotations, semantics) and `.razor` UI components.
- **Cate Pro**: a dark editor theme with warm accent colours, a matching window colour scheme, and file icons in the same style.

## Requirements

- Windows 10 or 11
- Kate 26.08 or newer: `winget install KDE.Kate`
- .NET 10 SDK (the one s&box uses)
- s&box

## Install

Close Kate, then from this folder:

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1
```

Then in the s&box editor pick **Edit › Preferences › Code Editor › Kate**.

The installer builds the bridge, downloads the Roslyn server from Microsoft's package feed, adds the Kate option to s&box, and sets Kate up (theme, colours, icons, highlighting, LSP config). It keeps your other Kate settings, and is safe to run again; rerun it after an s&box update, which can remove the Kate option.

Open a project by opening its folder in Kate (or *Open Solution* in s&box). The first time, C# needs a few seconds to load; Kate shows "IntelliSense is ready" when it has.

## How the C# support works

C# Dev Kit itself is licensed for Microsoft's editors only, but the language server inside it, Roslyn's `Microsoft.CodeAnalysis.LanguageServer`, is open source (MIT). It expects a few things from the editor that only VS Code's extension does, so `roslyn-kate` sits between Kate and the server and fills them in:

| Roslyn expects | roslyn-kate |
|---|---|
| The client says which solution to load (`solution/open`) | Finds the `.slnx` next to the `.sbproj` (walking up from Kate's root) and opens it; falls back to the `.csproj` files |
| The client runs NuGet restore when asked | Runs the server's restore when it asks |
| Edits arrive as ranges | Kate can send whole-document changes, which crash Roslyn's change handler; those are sent as a close and reopen |
| The client re-requests colours and diagnostics when told to refresh | Holds a file's first colour request until Roslyn has a full compilation, and re-pushes diagnostics after the project loads |
| Custom toast notifications | Shown as normal Kate messages |

It also ties the server's lifetime to its own, so restarting the server in Kate never leaves a Roslyn process behind.

## What's in here

| Path | What it is |
|---|---|
| `bridge/` | `roslyn-kate`, the LSP bridge (C#, .NET 10) |
| `sbox/CodeEditor.Kate.cs` | The s&box code editor option; installed into s&box's `addons/tools/Code/CodeEditors` |
| `theme/Cate Pro.theme` | Kate editor theme |
| `colors/CatePro.colors` | Kate window colour scheme |
| `icons/build-icons.ps1` | Generates the Cate Pro file icon theme |
| `syntax/hlsl.xml`, `syntax/razor.xml` | Highlighting for shaders and Razor |
| `mime/kate-sbox.xml` | File types for `.razor`, `.shader`, `.csproj`, `.slnx`, `.sbproj`, `.scene`, `.prefab` so they get icons |
| `install.ps1` | Installs all of the above |

## Troubleshooting

- **"AppData writes from this PowerShell are redirected"**: the PowerShell you ran the installer from belongs to a packaged app that keeps AppData changes in its own folder, so Kate would never see them. Run it again from Windows PowerShell opened from the Start menu. This is also why an older install can leave Kate with its default theme and no C#.
- **No IntelliSense**: check *Output › LSP Client* in Kate and `%LOCALAPPDATA%\kate-sbox\logs\roslyn-kate.log`. *LSP Client › Restart All LSP Servers* restarts it.
- **"No solution next to …sbproj"**: open the project in the s&box editor once; it generates the `.slnx` and `.csproj` files.
- **Kate missing from s&box's code editors**: rerun `install.ps1` (an s&box update can remove it), then restart the s&box editor.
- **Warnings like `InteropSystem: Tried to get … as Editor.Widget` after installing**: adding the Kate option while the s&box editor is open makes it hot-reload its tools; restart the editor.

## Credits

- [Roslyn](https://github.com/dotnet/roslyn) by the .NET Foundation (MIT), downloaded at install time, not redistributed here.
- [Kate](https://kate-editor.org) by KDE.

Not affiliated with Facepunch, Microsoft or KDE. Code in this repository is MIT licensed.
