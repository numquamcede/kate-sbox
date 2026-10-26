using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoslynKate;

/// <summary>
/// One direction of a JSON-RPC stream using LSP's Content-Length framing.
/// Messages are kept as raw bytes so anything we don't touch is forwarded byte for byte.
/// </summary>
sealed class LspChannel( Stream input, Stream output )
{
	readonly Stream _input = new BufferedStream( input );
	readonly SemaphoreSlim _writeLock = new( 1, 1 );

	/// <summary>
	/// Reads the next message body, or null at end of stream.
	/// </summary>
	public async Task<byte[]?> ReadAsync( CancellationToken ct )
	{
		int length = -1;

		while ( true )
		{
			var line = await ReadHeaderLineAsync( ct );
			if ( line is null ) return null;

			if ( line.Length == 0 )
			{
				if ( length >= 0 ) break;
				continue;
			}

			var colon = line.IndexOf( ':' );
			if ( colon > 0 && line[..colon].Trim().Equals( "Content-Length", StringComparison.OrdinalIgnoreCase ) )
				length = int.Parse( line[(colon + 1)..].Trim() );
		}

		var body = new byte[length];
		try
		{
			await _input.ReadExactlyAsync( body, ct );
		}
		catch ( EndOfStreamException )
		{
			return null;
		}

		return body;
	}

	public async Task WriteAsync( byte[] body, CancellationToken ct = default )
	{
		var header = Encoding.ASCII.GetBytes( $"Content-Length: {body.Length}\r\n\r\n" );

		await _writeLock.WaitAsync( ct );
		try
		{
			await output.WriteAsync( header, ct );
			await output.WriteAsync( body, ct );
			await output.FlushAsync( ct );
		}
		finally
		{
			_writeLock.Release();
		}
	}

	public Task WriteAsync( JsonNode message, CancellationToken ct = default )
	{
		message["jsonrpc"] = "2.0";
		return WriteAsync( Encoding.UTF8.GetBytes( message.ToJsonString() ), ct );
	}

	async Task<string?> ReadHeaderLineAsync( CancellationToken ct )
	{
		var sb = new StringBuilder();
		var one = new byte[1];

		while ( true )
		{
			if ( await _input.ReadAsync( one, ct ) == 0 )
				return null;

			if ( one[0] == '\n' )
				return sb.ToString().TrimEnd( '\r' );

			sb.Append( (char)one[0] );
		}
	}
}

/// <summary>
/// The parts of a JSON-RPC message we route on, read without materialising the whole body.
/// </summary>
readonly record struct MessageInfo( string? Method, JsonElement? Id )
{
	public bool IsRequest => Method is not null && Id is not null;
	public bool IsNotification => Method is not null && Id is null;
	public bool IsResponse => Method is null && Id is not null;

	/// <summary>
	/// The id as a string, if it is one. Ids the bridge makes up itself are always strings.
	/// </summary>
	public string? StringId => Id is { ValueKind: JsonValueKind.String } id ? id.GetString() : null;

	public static MessageInfo Peek( byte[] body )
	{
		var reader = new Utf8JsonReader( body );
		string? method = null;
		JsonElement? id = null;

		if ( !reader.Read() || reader.TokenType != JsonTokenType.StartObject )
			return default;

		while ( reader.Read() && reader.TokenType == JsonTokenType.PropertyName )
		{
			var name = reader.GetString();
			reader.Read();

			if ( name == "method" && reader.TokenType == JsonTokenType.String )
				method = reader.GetString();
			else if ( name == "id" && reader.TokenType is JsonTokenType.String or JsonTokenType.Number )
				id = JsonElement.ParseValue( ref reader );
			else
				reader.Skip();
		}

		return new MessageInfo( method, id );
	}
}
