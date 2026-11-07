using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoslynKate;

/// <summary>
/// What Roslyn said last time, kept on disk so Kate can have it the moment it asks instead of once
/// Roslyn has started and loaded the project: Roslyn's <c>initialize</c> answer, and each source file's
/// semantic tokens (the colours for types, parameters and so on), keyed by the file's text.
/// </summary>
sealed class TokenCache( string directory )
{
	/// <summary>
	/// Part of every key; bump it when the file format or what goes into a key changes.
	/// </summary>
	const int Format = 1;

	sealed record Entry( string LegendKey, string TextHash, int[] Data );

	readonly ConcurrentDictionary<string, Entry?> _tokens = new( StringComparer.OrdinalIgnoreCase );

	public static TokenCache Default() =>
		new( Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ), "kate-sbox", "cache" ) );

	public static string Key( params string?[] parts ) => Hash( $"{Format}|{string.Join( '|', parts )}" );

	public JsonNode? LoadInitializeResult( string key )
	{
		try
		{
			var saved = JsonNode.Parse( File.ReadAllText( InitializePath( key ) ) );
			return saved?["key"]?.GetValue<string>() == key ? saved["result"] : null;
		}
		catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException or JsonException )
		{
			return null;
		}
	}

	public void SaveInitializeResult( string key, JsonNode result )
	{
		var json = new JsonObject { ["key"] = key, ["result"] = result.DeepClone() }.ToJsonString();
		WriteAtomically( InitializePath( key ), stream => stream.Write( Encoding.UTF8.GetBytes( json ) ) );
	}

	/// <summary>
	/// The file's tokens, if they were made from this text with this token legend.
	/// </summary>
	public int[]? LoadTokens( string path, string legendKey, string textHash )
	{
		var entry = _tokens.GetOrAdd( path, ReadEntry );
		return entry is not null && entry.LegendKey == legendKey && entry.TextHash == textHash ? entry.Data : null;
	}

	public void SaveTokens( string path, string legendKey, string textHash, int[] data )
	{
		_tokens[path] = new Entry( legendKey, textHash, data );

		WriteAtomically( TokensPath( path ), stream =>
		{
			using var writer = new BinaryWriter( stream, Encoding.UTF8, leaveOpen: true );
			writer.Write( Format );
			writer.Write( path );
			writer.Write( legendKey );
			writer.Write( textHash );
			writer.Write( data.Length );
			foreach ( var value in data ) writer.Write( value );
		} );
	}

	/// <summary>
	/// Hashes a document's text the same whichever line endings it has: Kate sends its text with \n,
	/// the file on disk usually has \r\n, and token positions don't depend on either.
	/// </summary>
	public static string HashText( string text ) => Hash( text.Replace( "\r\n", "\n" ).Replace( '\r', '\n' ) );

	static string Hash( string value ) => Convert.ToHexString( SHA256.HashData( Encoding.UTF8.GetBytes( value ) ) );

	/// <summary>
	/// The tokens on lines <paramref name="first"/> to <paramref name="last"/>, re-encoded the way LSP
	/// does it: each token relative to the one before, the first relative to the start of the document.
	/// </summary>
	public static int[] Slice( int[] data, int first, int last )
	{
		var result = new List<int>();
		int line = 0, character = 0, previousLine = 0, previousCharacter = 0;

		for ( var i = 0; i + 4 < data.Length; i += 5 )
		{
			character = data[i] == 0 ? character + data[i + 1] : data[i + 1];
			line += data[i];

			if ( line < first ) continue;
			if ( line > last ) break;

			result.Add( line - previousLine );
			result.Add( line == previousLine ? character - previousCharacter : character );
			result.Add( data[i + 2] );
			result.Add( data[i + 3] );
			result.Add( data[i + 4] );
			(previousLine, previousCharacter) = (line, character);
		}

		return [.. result];
	}

	/// <summary>
	/// The line and character of each token, for finding one to hover.
	/// </summary>
	public static IEnumerable<(int Line, int Character, int Type)> Positions( int[] data )
	{
		int line = 0, character = 0;

		for ( var i = 0; i + 4 < data.Length; i += 5 )
		{
			character = data[i] == 0 ? character + data[i + 1] : data[i + 1];
			line += data[i];
			yield return (line, character, data[i + 3]);
		}
	}

	/// <summary>
	/// The C# files under a project folder, skipping build output and hidden folders.
	/// </summary>
	public static IEnumerable<string> FindSources( string root )
	{
		var folders = new Stack<string>( [root] );

		while ( folders.TryPop( out var folder ) )
		{
			string[] files, subfolders;
			try
			{
				files = Directory.GetFiles( folder, "*.cs" );
				subfolders = Directory.GetDirectories( folder );
			}
			catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException )
			{
				continue;
			}

			foreach ( var file in files ) yield return file;

			foreach ( var subfolder in subfolders )
			{
				if ( !IsSkipped( subfolder ) ) folders.Push( subfolder );
			}
		}
	}

	public static bool IsSkipped( string folder )
	{
		var name = Path.GetFileName( folder );
		return name.StartsWith( '.' ) || name is "bin" or "obj" or "node_modules";
	}

	// One per key, so two Kate builds sharing the cache don't keep replacing each other's
	string InitializePath( string key ) => Path.Combine( directory, $"initialize-{key[..16]}.json" );

	string TokensPath( string path ) =>
		Path.Combine( directory, "tokens", Hash( Path.GetFullPath( path ).ToLowerInvariant() )[..32] + ".bin" );

	Entry? ReadEntry( string path )
	{
		try
		{
			using var reader = new BinaryReader( File.OpenRead( TokensPath( path ) ), Encoding.UTF8 );
			if ( reader.ReadInt32() != Format ) return null;

			var savedPath = reader.ReadString();
			var legendKey = reader.ReadString();
			var textHash = reader.ReadString();
			var data = new int[reader.ReadInt32()];
			for ( var i = 0; i < data.Length; i++ ) data[i] = reader.ReadInt32();

			return string.Equals( savedPath, path, StringComparison.OrdinalIgnoreCase ) ? new Entry( legendKey, textHash, data ) : null;
		}
		catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException )
		{
			return null;
		}
	}

	/// <summary>
	/// Writes to a temporary file and moves it into place, so a crash or a second Kate never leaves half a file.
	/// </summary>
	static void WriteAtomically( string path, Action<Stream> write )
	{
		try
		{
			Directory.CreateDirectory( Path.GetDirectoryName( path )! );
			var temporary = $"{path}.{Environment.ProcessId}.tmp";
			using ( var stream = File.Create( temporary ) ) write( stream );
			File.Move( temporary, path, overwrite: true );
		}
		catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException )
		{
		}
	}
}
