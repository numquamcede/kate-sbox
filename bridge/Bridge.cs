using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace RoslynKate;

/// <summary>
/// Sits between Kate and the Roslyn language server and fills in the parts of the protocol that
/// VS Code's C# extension normally handles on the client side:
/// <list type="bullet">
/// <item>Tells Roslyn which solution or projects to load (<c>solution/open</c> / <c>project/open</c>).</item>
/// <item>Runs NuGet restore when Roslyn asks for it.</item>
/// <item>Turns Roslyn's custom toasts and load notifications into standard <c>window/showMessage</c>.</item>
/// <item>Re-pulls diagnostics for open files when Roslyn asks for a refresh, which Kate doesn't support.</item>
/// <item>Gives Kate a file's colours from last time straight away (see <see cref="TokenCache"/>).</item>
/// </list>
/// Everything else is forwarded untouched.
/// </summary>
/// <param name="serverStarting">Roslyn's channel, once its process is up; starting it doesn't hold up answering Kate.</param>
sealed class Bridge( LspChannel client, Task<LspChannel> serverStarting, Log log, TokenCache cache, string serverKey )
{
	const string IdPrefix = "roslyn-kate/";

	/// <summary>
	/// Token types worth hovering to make Roslyn build a project's compilation; see <see cref="ForceCompilationAsync"/>.
	/// </summary>
	static readonly Dictionary<string, string> KateTokenTypes = new()
	{
		["recordClass"] = "class",
		["recordStruct"] = "struct",
		["delegate"] = "class",
		["extensionMethod"] = "method",
	};

	static readonly HashSet<string> SymbolTokenTypes =
	[
		"namespace", "type", "class", "enum", "interface", "struct", "typeParameter", "parameter", "variable", "property",
		"enumMember", "event", "function", "method", "constant", "delegate", "extensionMethod", "field", "recordClass", "recordStruct",
	];

	readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();

	/// <summary>
	/// Open documents by uri, with the languageId Kate opened them with.
	/// </summary>
	readonly ConcurrentDictionary<string, string> _openDocuments = new();

	/// <summary>
	/// Hash of Kate's text for each open document, while we know it: from opening or saving it until the next edit.
	/// </summary>
	readonly ConcurrentDictionary<string, string> _documentHashes = new();

	/// <summary>
	/// Documents Kate has had colours for from the cache, so that's only logged once per document.
	/// </summary>
	readonly ConcurrentDictionary<string, byte> _servedFromCache = new();

	/// <summary>
	/// Completes once Roslyn has answered <c>initialize</c>. Nothing else may reach it before then.
	/// </summary>
	readonly TaskCompletionSource _serverInitialized = new( TaskCreationOptions.RunContinuationsAsynchronously );

	/// <summary>
	/// Completes once Roslyn has loaded the workspace (or there's nothing to load, or it gave up).
	/// </summary>
	readonly TaskCompletionSource _workspaceReady = new( TaskCreationOptions.RunContinuationsAsynchronously );

	/// <summary>
	/// Per document, completes once Roslyn has a full compilation for it (see <see cref="WarmUpAsync"/>).
	/// </summary>
	readonly ConcurrentDictionary<string, Task> _warmDocuments = new();

	/// <summary>
	/// Source files whose colours should be (re)cached, see <see cref="FillCacheAsync"/>.
	/// </summary>
	readonly Channel<string> _toCache = Channel.CreateUnbounded<string>();

	readonly Lock _writeLock = new();
	Task? _serverWrites;
	Task? _kateWrites;
	int _nextId;
	string? _rootPath;
	string? _loadedName;
	int _loadCount;

	/// <summary>
	/// The raw id of Kate's initialize request, while it's with Roslyn.
	/// </summary>
	string? _initializeId;
	string? _initializeKey;

	/// <summary>
	/// Set when there's no Kate, only <see cref="WarmAsync"/> filling the cache.
	/// </summary>
	bool _warming;
	string? _projectFolder;

	/// <summary>
	/// Identifies the token legend Kate is using, so cached tokens are only used with the legend they were made for.
	/// </summary>
	string? _legendKey;
	string[] _tokenTypes = [];

	/// <summary>
	/// Without Kate: loads the project in <paramref name="folder"/>, caches the colours of every source
	/// file in it, and shuts Roslyn down, so the first file Kate opens there is already coloured.
	/// Initializes Roslyn the way Kate last did, so Kate gets the cached answer to that too.
	/// </summary>
	public async Task<int> WarmAsync( string folder, CancellationToken ct )
	{
		_warming = true;
		_ = Guard( PumpServerToClientAsync( ct ) );

		var parameters = cache.LoadClientParameters() ?? new JsonObject { ["capabilities"] = new JsonObject() };
		parameters["processId"] = Environment.ProcessId;
		parameters["rootUri"] = PathToUri( folder );
		parameters["rootPath"] = folder;
		parameters["workspaceFolders"] = new JsonArray( new JsonObject { ["uri"] = PathToUri( folder ), ["name"] = Path.GetFileName( folder ) } );
		var body = System.Text.Encoding.UTF8.GetBytes( new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 0, ["method"] = "initialize", ["params"] = parameters }.ToJsonString() );

		CaptureRoot( body );
		if ( !await TryInitializeFromCacheAsync( body, ct ) )
		{
			_initializeId = "0";
			await ( await serverStarting ).WriteAsync( body, ct );
		}

		await _serverInitialized.Task.WaitAsync( ct );
		await SendToServerAsync( "initialized", new JsonObject(), ct );
		OpenWorkspace( ct );
		await _workspaceReady.Task.WaitAsync( ct );

		if ( _projectFolder is null )
			return 1;

		var sources = TokenCache.FindSources( _projectFolder ).ToList();
		var stored = await CacheTokensAsync( _projectFolder, sources, ct );

		await RequestFromServerAsync( "shutdown", new JsonObject(), ct );
		await SendToServerAsync( "exit", new JsonObject(), ct );
		return stored > 0 || sources.Count == 0 ? 0 : 1;
	}

	public async Task RunAsync( CancellationToken ct )
	{
		var up = PumpClientToServerAsync( ct );
		var down = PumpServerToClientAsync( ct );

		await Task.WhenAny( up, down );
	}

	async Task PumpClientToServerAsync( CancellationToken ct )
	{
		while ( await client.ReadAsync( ct ) is { } body )
		{
			var info = MessageInfo.Peek( body );

			switch ( info.Method )
			{
				case "initialize":
					CaptureRoot( body );
					if ( await TryInitializeFromCacheAsync( body, ct ) ) continue;

					// First run with this server and this Kate: Roslyn answers, and the answer is kept for next time.
					_initializeId = info.Id?.GetRawText();
					await ( await serverStarting ).WriteAsync( body, ct );
					continue;
				case "textDocument/didOpen":
					var doc = JsonNode.Parse( body )?["params"]?["textDocument"];
					if ( doc?["uri"]?.GetValue<string>() is { } opened )
					{
						_openDocuments[opened] = doc["languageId"]?.GetValue<string>() ?? "csharp";
						_documentHashes[opened] = TokenCache.HashText( doc["text"]?.GetValue<string>() ?? "" );
					}
					break;
				case "textDocument/didClose":
					if ( GetDocumentUri( body ) is { } closed )
					{
						_openDocuments.TryRemove( closed, out _ );
						_documentHashes.TryRemove( closed, out _ );
					}
					break;
				case "textDocument/didChange":
					if ( GetDocumentUri( body ) is { } changed ) _documentHashes.TryRemove( changed, out _ );
					if ( TrySendFullSyncChange( body, ct ) ) continue;
					break;
				case "textDocument/didSave":
					// Kate's text is what's on disk now, so the cache applies to it again once it's updated
					if ( GetDocumentUri( body ) is { } saved && TryHashFile( UriToPath( saved ) ) is { } savedHash )
						_documentHashes[saved] = savedHash;
					break;

				case { } method when method.StartsWith( "textDocument/semanticTokens" ):
					if ( GetDocumentUri( body ) is { } uri )
					{
						if ( await TryAnswerTokensFromCacheAsync( info, body, uri, ct ) ) continue;

						// Roslyn answers semantic tokens from whatever compilation it has so far and sends a refresh
						// request once the full one is ready. Kate ignores that refresh, so a file's first colours
						// would stay wrong (engine types uncoloured) until the next edit or scroll. Hold a document's
						// first request until Roslyn has a full compilation for it.
						var warm = _warmDocuments.GetOrAdd( uri, u => WarmUpAsync( u, ct ) );
						if ( !warm.IsCompleted )
						{
							_ = Guard( ForwardAfterAsync( warm, body, ct ) );
							continue;
						}
					}
					break;
			}

			// Kate's answers to Roslyn's own requests (its settings, say) go straight through: Roslyn may need
			// them to finish loading
			var forwarded = info.IsResponse || info.Method is "initialized" or "shutdown" or "exit" ? WriteServerAsync( body, ct ) : ForwardFromKate( body, ct );

			if ( info.Method == "initialized" )
				OpenWorkspace( ct );

			// Nothing useful comes after exit; returning lets Program wind the server down.
			if ( info.Method == "exit" )
			{
				log.Write( "client sent exit" );
				await Task.WhenAny( forwarded, Task.Delay( TimeSpan.FromSeconds( 5 ), ct ) );
				return;
			}
		}

		log.Write( "client closed the stream" );
	}

	async Task PumpServerToClientAsync( CancellationToken ct )
	{
		var server = await serverStarting;

		while ( await server.ReadAsync( ct ) is { } body )
		{
			var info = MessageInfo.Peek( body );

			if ( info.IsResponse && info.StringId is { } bridgeId && bridgeId.StartsWith( IdPrefix ) )
			{
				CompletePending( bridgeId, body );
				continue;
			}

			if ( info.IsResponse && _initializeId is not null && info.Id?.GetRawText() == _initializeId )
			{
				_initializeId = null;
				var response = JsonNode.Parse( body )!;
				OnServerInitialized( response["result"], answeredWith: null );

				if ( response["result"] is { } result )
				{
					response["result"] = ForKate( result );
					await client.WriteAsync( response, ct );
					continue;
				}
			}

			if ( info.IsRequest && await HandleServerRequestAsync( info, body, ct ) )
				continue;

			if ( info.IsNotification && await HandleServerNotificationAsync( info, body, ct ) )
				continue;

			await client.WriteAsync( body, ct );
		}

		log.Write( "server closed the stream" );
	}

	async Task<bool> HandleServerRequestAsync( MessageInfo info, byte[] body, CancellationToken ct )
	{
		switch ( info.Method )
		{
			case "workspace/_roslyn_projectNeedsRestore":
				await ReplyToServerAsync( info, ct );
				var paths = JsonNode.Parse( body )?["params"]?["projectFilePaths"]?.AsArray()
					.Select( p => p?.GetValue<string>() )
					.OfType<string>()
					.ToArray() ?? [];
				_ = Guard( RestoreAsync( paths, ct ) );
				return true;

			case "workspace/diagnostic/refresh":
				await ReplyToServerAsync( info, ct );
				_ = Guard( RefreshDiagnosticsAsync( ct ) );
				return true;

			// Kate answers these with MethodNotFound, which Roslyn logs as an error every time.
			case "workspace/inlayHint/refresh":
			case "workspace/codeLens/refresh":
				await ReplyToServerAsync( info, ct );
				return true;
		}

		return false;
	}

	async Task<bool> HandleServerNotificationAsync( MessageInfo info, byte[] body, CancellationToken ct )
	{
		switch ( info.Method )
		{
			case "window/_roslyn_showToast":
				var toast = JsonNode.Parse( body )?["params"];
				var type = toast?["messageType"]?.GetValue<int>() ?? 3;
				var text = toast?["message"]?.GetValue<string>() ?? "";
				await ShowMessageAsync( type, text, ct );
				return true;

			case "workspace/projectInitializationComplete":
				// Only pop up the first load; reloads after a restore or a .csproj edit just get logged.
				var message = $"C#: {_loadedName ?? "workspace"} loaded, IntelliSense is ready.";
				if ( Interlocked.Increment( ref _loadCount ) == 1 )
					await ShowMessageAsync( 3, message, ct );
				else
					await LogMessageAsync( 3, message, ct );

				_workspaceReady.TrySetResult();
				_ = Guard( RefreshDiagnosticsAsync( ct ) );
				return true;
		}

		return false;
	}

	/// <summary>
	/// Roslyn takes a second or two to answer <c>initialize</c>, and Kate asks for nothing (colours
	/// included) until it has the answer. So when we have Roslyn's answer from last time, for the same
	/// server and the same Kate, Kate gets that straight away while Roslyn starts up behind it.
	/// </summary>
	async Task<bool> TryInitializeFromCacheAsync( byte[] body, CancellationToken ct )
	{
		var request = JsonNode.Parse( body )!;
		var p = request["params"];
		if ( !_warming && p is not null )
			cache.SaveClientParameters( p );

		_initializeKey = TokenCache.Key( serverKey, p?["capabilities"]?.ToJsonString(), p?["initializationOptions"]?.ToJsonString() );

		var cached = cache.LoadInitializeResult( _initializeKey );
		if ( cached is null )
			return false;

		UseLegend( cached );
		await client.WriteAsync( new JsonObject { ["id"] = request["id"]?.DeepClone(), ["result"] = ForKate( cached ) }, ct );

		var id = IdPrefix + "initialize";
		var answer = new TaskCompletionSource<JsonNode?>( TaskCreationOptions.RunContinuationsAsynchronously );
		_pending[id] = answer;
		request["id"] = id;

		_ = Guard( FinishInitializeAsync( request, answer.Task, cached, ct ) );
		log.Write( "answered initialize from the cache" );
		return true;
	}

	async Task FinishInitializeAsync( JsonNode request, Task<JsonNode?> answer, JsonNode cached, CancellationToken ct )
	{
		try
		{
			await ( await serverStarting ).WriteAsync( request, ct );
			OnServerInitialized( await answer, cached );
		}
		finally
		{
			_serverInitialized.TrySetResult();
		}
	}

	/// <summary>
	/// Roslyn has answered <c>initialize</c>: let everything queued for it through, and keep the answer for next time.
	/// </summary>
	void OnServerInitialized( JsonNode? result, JsonNode? answeredWith )
	{
		if ( result is not null && _initializeKey is not null )
		{
			// Kate keeps the capabilities it was given first, so only a fresh answer sets the legend.
			if ( answeredWith is null )
				UseLegend( result );

			if ( answeredWith is null || !JsonNode.DeepEquals( result, answeredWith ) )
			{
				cache.SaveInitializeResult( _initializeKey, result );
				if ( answeredWith is not null )
					log.Write( "Roslyn's capabilities changed since last time; Kate gets them from the next start" );
			}
		}

		_serverInitialized.TrySetResult();
	}

	/// <summary>
	/// Roslyn's answer as Kate gets it. Kate only colours the standard token types, so records, delegates
	/// and extension methods would stay uncoloured; they're named as the plain kinds instead, which gives
	/// them the colours VS Code gives them.
	/// </summary>
	static JsonNode ForKate( JsonNode initializeResult )
	{
		var result = initializeResult.DeepClone();
		if ( result["capabilities"]?["semanticTokensProvider"]?["legend"]?["tokenTypes"] is JsonArray types )
		{
			for ( var i = 0; i < types.Count; i++ )
			{
				if ( types[i]?.GetValue<string>() is { } name && KateTokenTypes.TryGetValue( name, out var plain ) )
					types[i] = plain;
			}
		}

		return result;
	}

	void UseLegend( JsonNode initializeResult )
	{
		var legend = initializeResult["capabilities"]?["semanticTokensProvider"]?["legend"];
		_legendKey = TokenCache.Key( legend?.ToJsonString() );
		_tokenTypes = legend?["tokenTypes"]?.AsArray().Select( t => t?.GetValue<string>() ?? "" ).ToArray() ?? [];
	}

	/// <summary>
	/// Answers a semantic tokens request from the cache when Kate's text for the file is what the cached
	/// tokens were made from, so the file has its final colours straight away rather than once Roslyn has
	/// loaded the project. Kate only asks again after an edit, a scroll or switching tabs, so a guess here
	/// would stick; the cache is never a guess.
	/// </summary>
	async Task<bool> TryAnswerTokensFromCacheAsync( MessageInfo info, byte[] body, string uri, CancellationToken ct )
	{
		if ( _legendKey is null || !_documentHashes.TryGetValue( uri, out var hash ) )
			return false;

		var data = cache.LoadTokens( UriToPath( uri ), _legendKey, hash );
		if ( data is null )
			return false;

		var request = JsonNode.Parse( body );
		if ( info.Method == "textDocument/semanticTokens/range" && request?["params"]?["range"] is { } range )
			data = TokenCache.Slice( data, range["start"]?["line"]?.GetValue<int>() ?? 0, range["end"]?["line"]?.GetValue<int>() ?? int.MaxValue );

		await client.WriteAsync( new JsonObject
		{
			["id"] = request?["id"]?.DeepClone(),
			["result"] = new JsonObject { ["data"] = new JsonArray( data.Select( v => (JsonNode)v ).ToArray() ) },
		}, ct );

		if ( _servedFromCache.TryAdd( uri, 0 ) )
			log.Write( $"colours for {UriToPath( uri )} from the cache" );

		return true;
	}

	/// <summary>
	/// Once the project has loaded, has Roslyn colour every source file in it and keeps the results for
	/// next time; then does the same for files as they're saved or change on disk.
	/// </summary>
	async Task FillCacheAsync( string projectFolder, CancellationToken ct )
	{
		await _workspaceReady.Task.WaitAsync( ct );

		using var watcher = WatchSources( projectFolder );

		// Every file once per session: a change to one file can change another's colours.
		foreach ( var path in TokenCache.FindSources( projectFolder ) )
			_toCache.Writer.TryWrite( path );

		while ( await _toCache.Reader.WaitToReadAsync( ct ) )
		{
			// Let a burst of saves arrive together
			await Task.Delay( TimeSpan.FromSeconds( 1 ), ct );

			var batch = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
			while ( _toCache.Reader.TryRead( out var path ) ) batch.Add( Path.GetFullPath( path ) );

			await CacheTokensAsync( projectFolder, batch, ct );
		}
	}

	void StartCaching( string projectFolder, CancellationToken ct )
	{
		_projectFolder = projectFolder;
		if ( !_warming )
			_ = Guard( FillCacheAsync( projectFolder, ct ) );
	}

	async Task<int> CacheTokensAsync( string projectFolder, IReadOnlyCollection<string> paths, CancellationToken ct )
	{
		var stored = 0;

		foreach ( var project in paths.GroupBy( p => OwningProjectFolder( p, projectFolder ), StringComparer.OrdinalIgnoreCase ) )
		{
			// Roslyn colours from whatever compilation it has, which right after loading is a partial one;
			// colours from that must never be cached, so make it build the full one first.
			if ( !await ForceCompilationAsync( project, ct ) )
			{
				log.Write( $"couldn't get a full compilation for {project.Key}, not caching its colours" );
				continue;
			}

			foreach ( var path in project )
			{
				// Kate's unsaved text isn't what's on disk, and Roslyn colours Kate's text
				if ( TryHashFile( path ) is not { } hash || KateHasDifferentText( path, hash ) )
					continue;

				if ( _legendKey is { } legendKey && await RequestTokensAsync( path, ct ) is { } tokens )
				{
					cache.SaveTokens( path, legendKey, hash, tokens );
					stored++;
				}
			}
		}

		log.Write( $"cached colours for {stored} of {paths.Count} files" );
		return stored;
	}

	/// <summary>
	/// Hovering a symbol makes Roslyn bind it, which builds the project's full compilation; after that its
	/// tokens for every file in the project are final.
	/// </summary>
	async Task<bool> ForceCompilationAsync( IEnumerable<string> paths, CancellationToken ct )
	{
		foreach ( var path in paths )
		{
			if ( await RequestTokensAsync( path, ct ) is not { } tokens )
				continue;

			var symbol = TokenCache.Positions( tokens )
				.FirstOrDefault( t => t.Type < _tokenTypes.Length && SymbolTokenTypes.Contains( _tokenTypes[t.Type] ), (Line: -1, Character: 0, Type: 0) );
			if ( symbol.Line < 0 )
				continue;

			await RequestFromServerAsync( "textDocument/hover", new JsonObject
			{
				["textDocument"] = new JsonObject { ["uri"] = PathToUri( path ) },
				["position"] = new JsonObject { ["line"] = symbol.Line, ["character"] = symbol.Character },
			}, ct );
			return true;
		}

		return false;
	}

	async Task<int[]?> RequestTokensAsync( string path, CancellationToken ct )
	{
		var result = await RequestFromServerAsync( "textDocument/semanticTokens/full", new JsonObject
		{
			["textDocument"] = new JsonObject { ["uri"] = PathToUri( path ) },
		}, ct );

		return result?["data"]?.AsArray().Select( v => v?.GetValue<int>() ?? 0 ).ToArray();
	}

	FileSystemWatcher? WatchSources( string projectFolder )
	{
		try
		{
			var watcher = new FileSystemWatcher( projectFolder, "*.cs" )
			{
				IncludeSubdirectories = true,
				NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
			};

			FileSystemEventHandler queue = ( _, e ) =>
			{
				var relative = Path.GetRelativePath( projectFolder, Path.GetDirectoryName( e.FullPath )! );
				if ( relative == "." || !relative.Split( Path.DirectorySeparatorChar ).Any( TokenCache.IsSkipped ) )
					_toCache.Writer.TryWrite( e.FullPath );
			};

			watcher.Changed += queue;
			watcher.Created += queue;
			watcher.Renamed += ( s, e ) => queue( s, e );
			watcher.EnableRaisingEvents = true;
			return watcher;
		}
		catch ( Exception e ) when ( e is IOException or ArgumentException or PlatformNotSupportedException )
		{
			log.Write( $"not watching {projectFolder} for changes: {e.Message}" );
			return null;
		}
	}

	/// <summary>
	/// The folder of the project a file belongs to: the nearest folder above it with a .csproj, within the project folder.
	/// </summary>
	static string OwningProjectFolder( string path, string projectFolder )
	{
		for ( var folder = Path.GetDirectoryName( path ); folder is not null && folder.Length >= projectFolder.Length; folder = Path.GetDirectoryName( folder ) )
		{
			if ( Directory.EnumerateFiles( folder, "*.csproj" ).Any() )
				return folder;
		}

		return projectFolder;
	}

	bool KateHasDifferentText( string path, string diskHash )
	{
		foreach ( var uri in _openDocuments.Keys )
		{
			if ( string.Equals( UriToPath( uri ), path, StringComparison.OrdinalIgnoreCase ) )
				return !_documentHashes.TryGetValue( uri, out var hash ) || hash != diskHash;
		}

		return false;
	}

	static string? TryHashFile( string path )
	{
		try
		{
			return TokenCache.HashText( File.ReadAllText( path ) );
		}
		catch ( Exception e ) when ( e is IOException or UnauthorizedAccessException )
		{
			return null;
		}
	}

	async Task ForwardAfterAsync( Task gate, byte[] body, CancellationToken ct )
	{
		try
		{
			await gate.WaitAsync( ct );
		}
		catch ( Exception e ) when ( e is not OperationCanceledException )
		{
			log.Write( $"warm-up failed, forwarding anyway: {e.Message}" );
		}

		await ForwardFromKate( body, ct );
	}

	/// <summary>
	/// Waits for the workspace, then pulls diagnostics for the document: semantic diagnostics need the
	/// full compilation, so once they're back, token requests see fully resolved types. The diagnostics
	/// go to Kate as well since we have them anyway. Gives up after a while rather than hold colours forever.
	/// </summary>
	async Task WarmUpAsync( string uri, CancellationToken ct )
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource( ct );
		timeout.CancelAfter( TimeSpan.FromSeconds( 30 ) );

		try
		{
			await _workspaceReady.Task.WaitAsync( timeout.Token );
			await KateMessagesSent().WaitAsync( timeout.Token );
			await PublishDiagnosticsAsync( uri, timeout.Token );
		}
		catch ( OperationCanceledException ) when ( !ct.IsCancellationRequested )
		{
			log.Write( $"warm-up of {uri} timed out" );
		}
	}

	/// <summary>
	/// Roslyn only accepts ranged edits, and a whole-document change (which Kate sends unless
	/// incremental sync is on) throws inside its didChange handler, which shuts the server down.
	/// So a whole-document change is sent as a close and reopen with the new text instead.
	/// </summary>
	bool TrySendFullSyncChange( byte[] body, CancellationToken ct )
	{
		var p = JsonNode.Parse( body )?["params"];
		var changes = p?["contentChanges"]?.AsArray();
		var uri = p?["textDocument"]?["uri"]?.GetValue<string>();
		var version = p?["textDocument"]?["version"]?.DeepClone();

		if ( changes is null || uri is null )
			return false;

		var lastFull = -1;
		for ( var i = 0; i < changes.Count; i++ )
		{
			if ( changes[i]?["range"] is null ) lastFull = i;
		}

		if ( lastFull < 0 )
			return false;

		var languageId = _openDocuments.TryGetValue( uri, out var id ) ? id : "csharp";

		_ = ForwardFromKate( new JsonObject
		{
			["method"] = "textDocument/didClose",
			["params"] = new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } },
		}, ct );

		_ = ForwardFromKate( new JsonObject
		{
			["method"] = "textDocument/didOpen",
			["params"] = new JsonObject
			{
				["textDocument"] = new JsonObject
				{
					["uri"] = uri,
					["languageId"] = languageId,
					["version"] = version,
					["text"] = changes[lastFull]?["text"]?.GetValue<string>() ?? "",
				},
			},
		}, ct );

		// Ranged edits after the full one still apply on top of it.
		var rest = changes.Skip( lastFull + 1 ).Select( c => c?.DeepClone() ).ToArray();
		if ( rest.Length > 0 )
		{
			_ = ForwardFromKate( new JsonObject
			{
				["method"] = "textDocument/didChange",
				["params"] = new JsonObject
				{
					["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = version?.DeepClone() },
					["contentChanges"] = new JsonArray( rest ),
				},
			}, ct );
		}

		return true;
	}

	void CaptureRoot( byte[] body )
	{
		var p = JsonNode.Parse( body )?["params"];
		var uri = p?["rootUri"]?.GetValue<string>()
			?? p?["workspaceFolders"]?.AsArray().FirstOrDefault()?["uri"]?.GetValue<string>();

		_rootPath = uri is not null ? UriToPath( uri ) : p?["rootPath"]?.GetValue<string>();
		log.Write( $"root: {_rootPath ?? "(none)"}" );
	}

	/// <summary>
	/// Queues the solution (or projects) for Roslyn straight after <c>initialized</c>, before anything else
	/// Kate sends: a file Kate opens before then is taken for a standalone file, outside the project.
	/// </summary>
	void OpenWorkspace( CancellationToken ct )
	{
		// Don't hold requests forever if loading hangs or never reports back.
		_ = Task.Delay( TimeSpan.FromMinutes( 2 ), ct ).ContinueWith( _ => _workspaceReady.TrySetResult(), TaskScheduler.Default );

		if ( _rootPath is null )
		{
			_workspaceReady.TrySetResult();
			_ = Guard( ShowMessageAsync( 2, "C#: Kate didn't send a workspace root, so no project was loaded.", ct ) );
			return;
		}

		var target = WorkspaceLocator.Find( _rootPath );

		if ( target.Solution is not null )
		{
			_loadedName = Path.GetFileName( target.Solution );
			log.Write( $"opening solution {target.Solution}" );
			_ = SendToServerAsync( "solution/open", new JsonObject { ["solution"] = PathToUri( target.Solution ) }, ct );
			StartCaching( Path.GetDirectoryName( target.Solution )!, ct );
		}
		else if ( target.Projects.Count > 0 )
		{
			_loadedName = target.Projects.Count == 1 ? Path.GetFileName( target.Projects[0] ) : $"{target.Projects.Count} projects";
			log.Write( $"opening projects {string.Join( ", ", target.Projects )}" );
			var uris = new JsonArray( target.Projects.Select( p => (JsonNode)PathToUri( p ) ).ToArray() );
			_ = SendToServerAsync( "project/open", new JsonObject { ["projects"] = uris }, ct );
			StartCaching( _rootPath, ct );
		}
		else
		{
			_workspaceReady.TrySetResult();
		}

		if ( target.Warning is not null )
			_ = Guard( ShowMessageAsync( 2, $"C#: {target.Warning}", ct ) );
	}

	async Task RestoreAsync( string[] projectPaths, CancellationToken ct )
	{
		var what = projectPaths.Length == 1 ? Path.GetFileName( projectPaths[0] ) : $"{projectPaths.Length} projects";
		await LogMessageAsync( 3, $"C#: restoring {what}...", ct );

		var result = await RequestFromServerAsync( "workspace/_roslyn_restore", new JsonObject
		{
			["projectFilePaths"] = new JsonArray( projectPaths.Select( p => (JsonNode)p ).ToArray() ),
		}, ct );

		foreach ( var part in result?.AsArray() ?? [] )
		{
			if ( part?["message"]?.GetValue<string>() is { Length: > 0 } line )
				await LogMessageAsync( 4, line.TrimEnd(), ct );
		}

		await LogMessageAsync( 3, $"C#: restore of {what} finished.", ct );
	}

	async Task RefreshDiagnosticsAsync( CancellationToken ct )
	{
		await KateMessagesSent();

		foreach ( var uri in _openDocuments.Keys )
			await PublishDiagnosticsAsync( uri, ct );
	}

	/// <summary>
	/// Pulls a document's diagnostics from Roslyn and pushes them to Kate.
	/// </summary>
	async Task PublishDiagnosticsAsync( string uri, CancellationToken ct )
	{
		var report = await RequestFromServerAsync( "textDocument/diagnostic", new JsonObject
		{
			["textDocument"] = new JsonObject { ["uri"] = uri },
		}, ct );

		if ( report?["kind"]?.GetValue<string>() != "full" )
			return;

		await client.WriteAsync( new JsonObject
		{
			["method"] = "textDocument/publishDiagnostics",
			["params"] = new JsonObject
			{
				["uri"] = uri,
				["diagnostics"] = report["items"]?.DeepClone() ?? new JsonArray(),
			},
		}, ct );
	}

	Task<JsonNode?> RequestFromServerAsync( string method, JsonNode parameters, CancellationToken ct )
	{
		var id = IdPrefix + Interlocked.Increment( ref _nextId );
		var tcs = new TaskCompletionSource<JsonNode?>( TaskCreationOptions.RunContinuationsAsynchronously );
		_pending[id] = tcs;
		ct.Register( () => tcs.TrySetCanceled() );

		_ = WriteServerAsync( new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters }, ct );
		return tcs.Task;
	}

	void CompletePending( string id, byte[] body )
	{
		if ( !_pending.TryRemove( id, out var tcs ) )
			return;

		var response = JsonNode.Parse( body );
		if ( response?["error"] is { } error )
		{
			log.Write( $"{id} failed: {error.ToJsonString()}" );
			tcs.TrySetResult( null );
			return;
		}

		tcs.TrySetResult( response?["result"] );
	}

	/// <summary>
	/// Everything for Roslyn after <c>initialize</c> goes through here, in order, and waits until Roslyn
	/// has answered <c>initialize</c>: when Kate got the answer from the cache, it carries on before
	/// Roslyn is up. The returned task never faults.
	/// </summary>
	Task WriteServerAsync( byte[] body, CancellationToken ct ) => QueueServerWrite( async () => await ( await serverStarting ).WriteAsync( body, ct ) );

	Task WriteServerAsync( JsonNode message, CancellationToken ct ) => QueueServerWrite( async () => await ( await serverStarting ).WriteAsync( message, ct ) );

	Task QueueServerWrite( Func<Task> write )
	{
		lock ( _writeLock )
			return _serverWrites = WriteAfterAsync( _serverWrites ?? _serverInitialized.Task, write );
	}

	/// <summary>
	/// What Kate sends after <c>initialized</c> waits, in order, until Roslyn has loaded the project: Roslyn
	/// takes a file opened while it's still loading for a standalone file outside the project, and keeps it
	/// that way. Kate doesn't miss anything, since nothing Roslyn says before then is of use.
	/// </summary>
	Task ForwardFromKate( byte[] body, CancellationToken ct ) => QueueKateWrite( () => WriteServerAsync( body, ct ) );

	Task ForwardFromKate( JsonNode message, CancellationToken ct ) => QueueKateWrite( () => WriteServerAsync( message, ct ) );

	Task QueueKateWrite( Func<Task> write )
	{
		lock ( _writeLock )
			return _kateWrites = WriteAfterAsync( _kateWrites ?? _workspaceReady.Task, write );
	}

	/// <summary>
	/// Completes once everything Kate has sent so far has reached Roslyn, such as the files it opened.
	/// </summary>
	Task KateMessagesSent()
	{
		lock ( _writeLock )
			return _kateWrites ?? Task.CompletedTask;
	}

	async Task WriteAfterAsync( Task previous, Func<Task> write )
	{
		await previous;

		try
		{
			await write();
		}
		catch ( OperationCanceledException )
		{
		}
		catch ( Exception e )
		{
			log.Write( $"writing to the server failed: {e.Message}" );
		}
	}

	Task SendToServerAsync( string method, JsonNode parameters, CancellationToken ct ) =>
		WriteServerAsync( new JsonObject { ["method"] = method, ["params"] = parameters }, ct );

	Task ReplyToServerAsync( MessageInfo request, CancellationToken ct ) =>
		WriteServerAsync( new JsonObject { ["id"] = JsonNode.Parse( request.Id!.Value.GetRawText() ), ["result"] = null }, ct );

	Task ShowMessageAsync( int type, string message, CancellationToken ct ) =>
		client.WriteAsync( new JsonObject { ["method"] = "window/showMessage", ["params"] = new JsonObject { ["type"] = type, ["message"] = message } }, ct );

	Task LogMessageAsync( int type, string message, CancellationToken ct ) =>
		client.WriteAsync( new JsonObject { ["method"] = "window/logMessage", ["params"] = new JsonObject { ["type"] = type, ["message"] = message } }, ct );

	static string? GetDocumentUri( byte[] body ) =>
		JsonNode.Parse( body )?["params"]?["textDocument"]?["uri"]?.GetValue<string>();

	static string UriToPath( string uri ) => new Uri( uri ).LocalPath;

	static string PathToUri( string path ) => new Uri( Path.GetFullPath( path ) ).AbsoluteUri;

	/// <summary>
	/// Fire-and-forget work must never take the bridge down; log and carry on.
	/// </summary>
	async Task Guard( Task task )
	{
		try
		{
			await task;
		}
		catch ( OperationCanceledException )
		{
		}
		catch ( Exception e )
		{
			log.Write( $"error: {e}" );
		}
	}
}
