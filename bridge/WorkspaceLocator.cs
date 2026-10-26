namespace RoslynKate;

/// <summary>
/// What Roslyn should load for a workspace root: one solution, or a set of loose projects.
/// </summary>
sealed record WorkspaceTarget( string? Solution, IReadOnlyList<string> Projects, string? Warning );

/// <summary>
/// Works out what to load from the folder Kate gives us as the LSP root. VS Code's C# extension
/// does this on the client side and sends the result to Roslyn, so we have to do it here.
/// </summary>
static class WorkspaceLocator
{
	static readonly EnumerationOptions ProjectSearch = new()
	{
		RecurseSubdirectories = true,
		MaxRecursionDepth = 4,
		IgnoreInaccessible = true,
	};

	public static WorkspaceTarget Find( string root )
	{
		// Kate's root can be a subfolder of the real project (e.g. an s&box project's Code/ folder,
		// which has its own .csproj), so walk up to the nearest solution.
		for ( var dir = new DirectoryInfo( root ); dir is not null; dir = dir.Parent )
		{
			var sbproj = SafeGetFiles( dir, "*.sbproj" ).FirstOrDefault();
			var solutions = SafeGetFiles( dir, "*.slnx" ).Concat( SafeGetFiles( dir, "*.sln" ) ).ToArray();

			if ( solutions.Length > 0 )
				return new WorkspaceTarget( PickSolution( solutions, sbproj ).FullName, [], null );

			// An s&box project without a solution yet: the editor generates it, so don't look any further up.
			if ( sbproj is not null )
			{
				return new WorkspaceTarget( null, FindProjects( dir.FullName ),
					$"No solution next to {sbproj.Name} yet. Open the project in the s&box editor once so it generates " +
					"the .slnx, then restart the C# server (LSP Client > Restart All LSP Servers)." );
			}
		}

		var projects = FindProjects( root );
		return new WorkspaceTarget( null, projects,
			projects.Count == 0 ? $"No .slnx, .sln or .csproj found under {root}." : null );
	}

	/// <summary>
	/// s&box names the solution after the project, so prefer that; otherwise prefer .slnx, then the first by name.
	/// </summary>
	static FileInfo PickSolution( FileInfo[] solutions, FileInfo? sbproj )
	{
		if ( sbproj is not null )
		{
			var name = Path.GetFileNameWithoutExtension( sbproj.Name );
			var match = solutions.FirstOrDefault( s => Path.GetFileNameWithoutExtension( s.Name ).Equals( name, StringComparison.OrdinalIgnoreCase ) );
			if ( match is not null ) return match;
		}

		return solutions
			.OrderBy( s => s.Extension.Equals( ".slnx", StringComparison.OrdinalIgnoreCase ) ? 0 : 1 )
			.ThenBy( s => s.Name, StringComparer.OrdinalIgnoreCase )
			.First();
	}

	static List<string> FindProjects( string root )
	{
		try
		{
			return Directory.EnumerateFiles( root, "*.csproj", ProjectSearch )
				.Where( p => !IsBuildOutput( p ) )
				.ToList();
		}
		catch ( IOException )
		{
			return [];
		}
	}

	static bool IsBuildOutput( string path )
	{
		var parts = path.Split( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar );
		return parts.Any( p => p.Equals( "bin", StringComparison.OrdinalIgnoreCase ) || p.Equals( "obj", StringComparison.OrdinalIgnoreCase ) );
	}

	static FileInfo[] SafeGetFiles( DirectoryInfo dir, string pattern )
	{
		try
		{
			return dir.GetFiles( pattern );
		}
		catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException )
		{
			return [];
		}
	}
}
