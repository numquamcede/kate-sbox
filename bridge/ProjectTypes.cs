using System.Text.RegularExpressions;

namespace RoslynKate;

/// <summary>
/// Puts the type names Roslyn has found in your projects into the installed C# highlighting, so they're
/// coloured from the first moment as well, including in files Roslyn hasn't seen yet. Kate reads the
/// highlighting when it starts, so names found now show from the next start.
/// </summary>
static class ProjectTypes
{
	static readonly HashSet<string> TypeKinds = ["type", "class", "interface", "struct", "enum", "recordClass", "recordStruct", "delegate"];

	/// <summary>
	/// Kinds that don't tell us anything about what a name is.
	/// </summary>
	static bool IsNeutral( string kind ) =>
		kind is "keyword" or "operator" or "punctuation" or "string" or "number" or "comment" or "excludedCode" or "controlKeyword" or "stringEscapeCharacter" or "stringVerbatim"
		|| kind.StartsWith( "xml" ) || kind.StartsWith( "regex" ) || kind.StartsWith( "json" );

	static string SyntaxPath => Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ), "org.kde.syntax-highlighting", "syntax", "csharp.xml" );

	public static void Update( TokenCache cache, string projectFolder, string legendKey, string[] tokenTypes, Log log )
	{
		var asType = new Dictionary<string, int>();
		var asOther = new Dictionary<string, int>();
		var enums = new HashSet<string>();

		foreach ( var path in TokenCache.FindSources( projectFolder ) )
		{
			string text;
			try { text = File.ReadAllText( path ); }
			catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException ) { continue; }

			if ( cache.LoadTokens( path, legendKey, TokenCache.HashText( text ) ) is not { } data )
				continue;

			var lines = text.Replace( "\r\n", "\n" ).Split( '\n' );
			int line = 0, character = 0;
			for ( var i = 0; i + 4 < data.Length; i += 5 )
			{
				character = data[i] == 0 ? character + data[i + 1] : data[i + 1];
				line += data[i];
				if ( line >= lines.Length || character + data[i + 2] > lines[line].Length || data[i + 3] >= tokenTypes.Length )
					continue;

				// The highlighting only uses the list for names that don't follow a dot
				if ( lines[line][..character].TrimEnd().EndsWith( '.' ) )
					continue;

				var name = lines[line].Substring( character, data[i + 2] );
				var kind = tokenTypes[data[i + 3]];
				if ( TypeKinds.Contains( kind ) )
				{
					asType[name] = asType.GetValueOrDefault( name ) + 1;
					if ( kind == "enum" ) enums.Add( name );
				}
				else if ( !IsNeutral( kind ) )
				{
					asOther[name] = asOther.GetValueOrDefault( name ) + 1;
				}
			}
		}

		// Same rule as for s&box's own types: a name that's nearly always a type, so the list never
		// colours what Roslyn would colour differently
		var names = asType.Keys
			.Where( n => Regex.IsMatch( n, @"^[A-Za-z_]\w*$" ) && asOther.GetValueOrDefault( n ) <= 0.03 * ( asType[n] + asOther.GetValueOrDefault( n ) ) )
			.ToHashSet();

		cache.SaveProjectTypes( projectFolder, names.Order().Select( n => ( enums.Contains( n ) ? "E " : "T " ) + n ) );
		WriteToSyntax( cache.LoadAllProjectTypes(), log );
	}

	static void WriteToSyntax( IReadOnlyCollection<string> entries, Log log )
	{
		string xml;
		try { xml = File.ReadAllText( SyntaxPath ); }
		catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException ) { return; }

		var types = entries.Select( e => e[2..] ).Distinct().Order().ToList();
		var enums = entries.Where( e => e.StartsWith( "E " ) ).Select( e => e[2..] ).Distinct().Order().ToList();

		var updated = Fill( Fill( xml, "project-types", types ), "project-enums", enums );
		if ( updated is null || updated == xml )
			return;

		var temporary = $"{SyntaxPath}.{Environment.ProcessId}.tmp";
		File.WriteAllText( temporary, updated );
		File.Move( temporary, SyntaxPath, overwrite: true );
		log.Write( $"C# highlighting now knows {types.Count} of your projects' type names" );
	}

	static string? Fill( string? xml, string list, IEnumerable<string> names )
	{
		if ( xml is null )
			return null;

		var pattern = new Regex( $"(<list name=\"{list}\">\\n).*?(    </list>)", RegexOptions.Singleline );
		if ( !pattern.IsMatch( xml ) )
			return null;

		// Kate wants something in a list; this never matches a real name
		var items = string.Concat( names.DefaultIfEmpty( "__kate_sbox_none__" ).Select( n => $"      <item>{n}</item>\n" ) );
		return pattern.Replace( xml, m => m.Groups[1].Value + items + m.Groups[2].Value, 1 );
	}
}
