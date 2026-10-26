# Contributing the Kate option to s&box

`CodeEditor.Kate.cs` is written to go into [Facepunch/sbox-public](https://github.com/Facepunch/sbox-public) as-is,
at `game/addons/tools/Code/CodeEditors/CodeEditor.Kate.cs`, next to the VS Code, Visual Studio and Rider editors.
It follows their `.editorconfig` (tabs, CRLF, UTF-8 BOM like its neighbours) and passes `dotnet format whitespace`
against it.

Their [CONTRIBUTING.md](https://github.com/Facepunch/sbox-public/blob/master/CONTRIBUTING.md) asks for new features to
be discussed in an issue first, so: open the feature request, then the pull request referencing it.

Only the s&box file goes upstream. The language server bridge, theme and highlighting stay in this repository.

## 1. Feature request (Issues › New › Feature request)

**Title:** Add Kate as a built-in code editor

**What can't you do?**

Kate (KDE's editor, on Windows, Linux and macOS) isn't in the Code Editor list. The Custom option can launch it, but
can't do it well: Kate opens a new window for every folder argument, so opening a file should only pass the project
folder when Kate isn't already running, and a fixed argument template can't express that.

**How would you like it to work?**

A Kate entry alongside Visual Studio Code, Visual Studio and Rider, found automatically (PATH, the winget/installer
locations on Windows, the app bundle on macOS, distro and Flatpak paths on Linux). Opening a file uses Kate's
`file:line:column` form, which works both when starting Kate and when handing the file to a running instance.

**What have you tried?**

The Custom code editor, and a local `ICodeEditor` implementation, which is what the PR would contain. With Kate's LSP
client pointed at the Roslyn language server it gives completion, diagnostics and go to definition against the
generated `.slnx`.

## 2. Pull request

Fork the repo, create a branch (e.g. `kate-code-editor`), add the file, commit, push, and open the PR with:

**Title:** Add Kate code editor

```markdown
## Summary

Adds Kate as a built-in code editor in Editor Preferences › Code Editor, next to Visual Studio Code, Visual Studio and Rider.

## Motivation & Context

Kate is a common editor on Linux and also ships for Windows and macOS. The Custom editor can launch it, but not well: see the issue.

Fixes: #<issue number>

## Implementation Details

- One new file, `CodeEditors/CodeEditor.Kate.cs`, in the same shape as the VS Code editor and using `CodeEditorLocator` for lookup and launching.
- Files open as `"path:line:column"`. Kate parses this (1-based) both on startup and when it hands the file to an already running instance, whereas `--line`/`--column` are dropped on the way to a running instance.
- Any folder argument makes Kate start a new window, so `OpenFile` only passes the project folder (for Kate's project view) when no Kate is running; otherwise it hands just the file to the open window.
- `OpenSolution`/`OpenAddon` open the project folder, like the VS Code editor.
- Lookup: `kate` on PATH, `%LOCALAPPDATA%/Programs/Kate` (winget/installer default), `%ProgramFiles%/Kate`, the macOS app bundle, `/usr/bin/kate`, Flatpak exports and Snap.

## Screenshots / Videos (if applicable)

<!-- Code Editor dropdown with Kate, and Kate opened at the clicked line -->

## Checklist

- [x] Code follows existing style and conventions
- [x] No unnecessary formatting or unrelated changes
- [ ] Public APIs are documented (if applicable)
- [ ] Unit tests added where applicable and all passing
- [x] I'm okay with this PR being rejected or requested to change 🙂
```

## Tested

On Windows 11 with Kate 26.08.2 and s&box 26.10.13c:

- Kate appears in the Code Editor dropdown and is reported as installed.
- With Kate running, opening a project file at line 120, column 16 opened it as a new tab in the running window at that position.
- With Kate closed, opening a project file at line 50, column 6 started Kate with the project folder in its project view and the file at that position.
