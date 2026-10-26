using System.Diagnostics;
using System.IO;

namespace Editor.CodeEditors;

[Title( "Kate" )]
public class Kate : ICodeEditor
{
	public void OpenFile( string path, int? line, int? column )
	{
		// Kate reads "file:line:column" (1-based) both when starting and when handing the file to a
		// running instance; --line/--column are dropped on the way to a running one.
		var file = line is null ? $"\"{path}\"" : $"\"{path}:{line}:{column ?? 1}\"";

		// A folder argument always starts a new Kate window, so only bring the project along
		// when there's no Kate open to hand the file to.
		string sln = null;
		if ( !IsRunning() )
		{
			// Throws when the file isn't under a solution, e.g. engine code; then just open the file
			try
			{
				sln = CodeEditor.FindSolutionFromPath( Path.GetDirectoryName( path ) );
			}
			catch { }
		}

		if ( sln is null )
		{
			Launch( file );
			return;
		}

		Launch( $"\"{Path.GetDirectoryName( sln )}\" {file}" );
	}

	public void OpenSolution()
	{
		Launch( $"\"{Project.Current.GetRootPath()}\"" );
	}

	public void OpenAddon( Project addon )
	{
		var projectPath = (addon != null) ? addon.GetRootPath() : "";

		Launch( $"\"{projectPath}\"" );
	}

	public bool IsInstalled() => GetLocation() is not null;

	private static bool IsRunning()
	{
		var processes = Process.GetProcessesByName( "kate" );
		foreach ( var p in processes ) p.Dispose();

		return processes.Length > 0;
	}

	private static void Launch( string arguments ) => CodeEditorLocator.Launch( GetLocation(), arguments );

	static string Location;

	private static string GetLocation() => Location ??= CodeEditorLocator.Find(
		"kate",
		// Windows
		"%LOCALAPPDATA%/Programs/Kate/bin/kate.exe",
		"%ProgramFiles%/Kate/bin/kate.exe",
		// macOS
		"/Applications/kate.app/Contents/MacOS/kate",
		"~/Applications/kate.app/Contents/MacOS/kate",
		// Linux
		"/usr/bin/kate",
		"/var/lib/flatpak/exports/bin/org.kde.kate",
		"~/.local/share/flatpak/exports/bin/org.kde.kate",
		"/snap/bin/kate" );
}
