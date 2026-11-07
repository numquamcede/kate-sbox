using System.Diagnostics;

namespace RoslynKate;

/// <summary>
/// roslyn-kate: runs the Roslyn C# language server (the engine behind VS Code's C# Dev Kit)
/// behind a small bridge so it works with Kate, or any other plain LSP client.
///
/// Usage: roslyn-kate [--server &lt;path&gt;] [--logLevel &lt;level&gt;] [--logDirectory &lt;dir&gt;] [-- extra server args]
/// </summary>
static class Program
{
	const string ServerExe = "Microsoft.CodeAnalysis.LanguageServer.exe";

	static async Task<int> Main( string[] args )
	{
		string? serverPath = null;
		var logLevel = "Information";
		var logDirectory = Path.Combine( Environment.GetFolderPath( Environment.SpecialFolder.LocalApplicationData ), "kate-sbox", "logs" );
		var extraArgs = new List<string>();

		for ( var i = 0; i < args.Length; i++ )
		{
			switch ( args[i] )
			{
				case "--server" when i + 1 < args.Length: serverPath = args[++i]; break;
				case "--logLevel" when i + 1 < args.Length: logLevel = args[++i]; break;
				case "--logDirectory" when i + 1 < args.Length: logDirectory = args[++i]; break;
				case "--": extraArgs.AddRange( args[(i + 1)..] ); i = args.Length; break;
				case "--version": Console.WriteLine( typeof( Program ).Assembly.GetName().Version ); return 0;
				default:
					Console.Error.WriteLine( $"roslyn-kate: unknown argument '{args[i]}'" );
					return 2;
			}
		}

		serverPath ??= FindServer();
		if ( serverPath is null || !File.Exists( serverPath ) )
		{
			Console.Error.WriteLine( $"roslyn-kate: couldn't find {ServerExe}. Pass --server <path> or set ROSLYN_LANGUAGE_SERVER." );
			return 1;
		}

		Directory.CreateDirectory( logDirectory );
		var log = new Log( Path.Combine( logDirectory, "roslyn-kate.log" ) );
		log.Write( $"starting {serverPath}" );

		var startInfo = new ProcessStartInfo( serverPath )
		{
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = false,
			UseShellExecute = false,
			CreateNoWindow = true,
		};

		foreach ( var arg in new[] { "--stdio", "--logLevel", logLevel, "--extensionLogDirectory", logDirectory }.Concat( extraArgs ) )
			startInfo.ArgumentList.Add( arg );

		// Starting a process takes a while; Kate can get cached answers in the meantime
		var starting = Task.Run( () =>
		{
			var started = Process.Start( startInfo )!;
			ChildProcessJob.Add( started );
			return started;
		} );
		using var cts = new CancellationTokenSource();

		// Cached answers from Roslyn are only reused with the same server build
		var server = new FileInfo( serverPath );
		var serverKey = $"{server.FullName}|{server.Length}|{server.LastWriteTimeUtc.Ticks}";

		var bridge = new Bridge(
			client: new LspChannel( Console.OpenStandardInput(), Console.OpenStandardOutput() ),
			serverStarting: starting.ContinueWith( t => new LspChannel( t.Result.StandardOutput.BaseStream, t.Result.StandardInput.BaseStream ), TaskScheduler.Default ),
			log,
			TokenCache.Default(),
			serverKey );

		try
		{
			await bridge.RunAsync( cts.Token );
		}
		finally
		{
			cts.Cancel();
			Stop( await starting, log );
		}

		using var process = await starting;
		return process.ExitCode;
	}

	/// <summary>
	/// After exit Roslyn stops serving but keeps running until its stdin closes, so close it,
	/// and only kill it if that doesn't do the job either.
	/// </summary>
	static void Stop( Process process, Log log )
	{
		if ( !process.WaitForExit( TimeSpan.FromSeconds( 2 ) ) )
		{
			try { process.StandardInput.Close(); } catch ( IOException ) { }
		}

		if ( !process.WaitForExit( TimeSpan.FromSeconds( 5 ) ) )
		{
			log.Write( "server didn't exit, killing it" );
			process.Kill( entireProcessTree: true );
		}

		log.Write( $"server exited with {process.ExitCode}" );
	}

	/// <summary>
	/// The server ships next to the bridge (in a <c>roslyn</c> folder) unless told otherwise.
	/// </summary>
	static string? FindServer()
	{
		if ( Environment.GetEnvironmentVariable( "ROSLYN_LANGUAGE_SERVER" ) is { Length: > 0 } fromEnv )
			return fromEnv;

		var candidate = Path.Combine( AppContext.BaseDirectory, "roslyn", ServerExe );
		return File.Exists( candidate ) ? candidate : null;
	}
}

/// <summary>
/// A plain append-only log, since stdout belongs to the protocol and Kate shows stderr as errors.
/// </summary>
sealed class Log( string path )
{
	readonly Lock _lock = new();

	public void Write( string message )
	{
		lock ( _lock )
		{
			try
			{
				File.AppendAllText( path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}" );
			}
			catch ( IOException )
			{
			}
		}
	}
}
