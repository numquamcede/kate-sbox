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

The body, in the form's sections:

```markdown
### What can't you do?

Kate isn't one of the built-in code editors. It can be set up through **Custom**, but every user has to find the executable and work out the argument templates themselves:

- To jump to a line in an already running Kate, it needs `{file}:{line}:{column}`; `--line`/`--column` are dropped when Kate hands the file to the open window.
- Open Solution needs `{project_root}`, since the default `{solution}` opens the `.slnx` as a text file.

Custom also can't pass the project folder only when Kate isn't running yet. Any folder argument makes Kate open a new window, so a fixed template either never shows the project in Kate's project view, or opens a new window on every click.

### How would you like it to work?

A **Kate** entry in Preferences › Code Editor, next to Visual Studio Code, Visual Studio and Rider:

- Found automatically: `kate` on PATH, `%LOCALAPPDATA%/Programs/Kate` (winget/installer default) and `%ProgramFiles%/Kate` on Windows, the app bundle on macOS, `/usr/bin/kate`, Flatpak and Snap on Linux.
- Opening a file passes `"path:line:column"`. If Kate isn't running, it also passes the project folder, so Kate opens with the project in its project view.
- Open Solution / Open Addon open the project folder, like the VS Code editor does.

### What have you tried?

The Custom editor, with the templates above. It works once set up, but it isn't discoverable and can't handle the running/not running case.

I've also written it as an `ICodeEditor` next to the existing ones: one file, same shape as `CodeEditor.VSCode.cs`, using `CodeEditorLocator`. I've been using it with s&box 26.10.13c and Kate 26.08 on Windows 11. It shows up in the Code Editor dropdown and opens files at the right line and column, both when Kate is already running and when it isn't. Happy to open a PR if this is wanted.

### Additional context

Related: #504 and #10481 (configurable editors), which the Custom editor now covers. This would be a dedicated entry for one more editor.

For C#, Kate's LSP client can run the Roslyn language server against the `.slnx` s&box generates (completion, diagnostics, go to definition). I've put that setup in https://github.com/numquamcede/kate-sbox; it's separate from this request.
```

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
- `FindSolutionFromPath` throws when the file isn't under a solution (e.g. engine code); that's caught, as the Custom editor does, and just the file is opened.
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
- With Kate closed, opening engine code that isn't under a solution (`addons/tools/Code/CreateAsset.cs`, line 40) through `CodeEditor.OpenFile` started Kate with the file at line 40.
