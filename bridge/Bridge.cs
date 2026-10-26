using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoslynKate;

/// <summary>
/// Sits between Kate and the Roslyn language server and fills in the parts of the protocol that
/// VS Code's C# extension normally handles on the client side:
/// <list type="bullet">
/// <item>Tells Roslyn which solution or projects to load (<c>solution/open</c> / <c>project/open</c>).</item>
/// <item>Runs NuGet restore when Roslyn asks for it.</item>
/// <item>Turns Roslyn's custom toasts and load notifications into standard <c>window/showMessage</c>.</item>
/// <item>Re-pulls diagnostics for open files when Roslyn asks for a refresh, which Kate doesn't support.</item>
/// </list>
/// Everything else is forwarded untouched.
/// </summary>
sealed class Bridge( LspChannel client, LspChannel server, Log log )
{
	const string IdPrefix = "roslyn-kate/";

	readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();

	/// <summary>
	/// Open documents by uri, with the languageId Kate opened them with.
	/// </summary>
	readonly ConcurrentDictionary<string, string> _openDocuments = new();

	/// <summary>
	/// Completes once Roslyn has loaded the workspace (or there's nothing to load, or it gave up).
	/// </summary>
	readonly TaskCompletionSource _workspaceReady = new( TaskCreationOptions.RunContinuationsAsynchronously );

	/// <summary>
	/// Per document, completes once Roslyn has a full compilation for it (see <see cref="WarmUpAsync"/>).
	/// </summary>
	readonly ConcurrentDictionary<string, Task> _warmDocuments = new();
	int _nextId;
	string? _rootPath;
	string? _loadedName;
	int _loadCount;

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
					break;
				case "textDocument/didOpen":
					var doc = JsonNode.Parse( body )?["params"]?["textDocument"];
					if ( doc?["uri"]?.GetValue<string>() is { } opened )
						_openDocuments[opened] = doc["languageId"]?.GetValue<string>() ?? "csharp";
					break;
				case "textDocument/didClose":
					if ( GetDocumentUri( body ) is { } closed ) _openDocuments.TryRemove( closed, out _ );
					break;
				case "textDocument/didChange":
					if ( await TrySendFullSyncChangeAsync( body, ct ) ) continue;
					break;

				// Roslyn answers semantic tokens from whatever compilation it has so far and sends a refresh
				// request once the full one is ready. Kate ignores that refresh, so a file's first colours
				// would stay wrong (engine types uncoloured) until the next edit or scroll. Hold a document's
				// first request until Roslyn has a full compilation for it.
				case { } method when method.StartsWith( "textDocument/semanticTokens" ):
					if ( GetDocumentUri( body ) is { } uri )
					{
						var warm = _warmDocuments.GetOrAdd( uri, u => WarmUpAsync( u, ct ) );
						if ( !warm.IsCompleted )
						{
							_ = Guard( ForwardAfterAsync( warm, body, ct ) );
							continue;
						}
					}
					break;
			}

			await server.WriteAsync( body, ct );

			if ( info.Method == "initialized" )
				_ = Guard( OpenWorkspaceAsync( ct ) );

			// Nothing useful comes after exit; returning lets Program wind the server down.
			if ( info.Method == "exit" )
			{
				log.Write( "client sent exit" );
				return;
			}
		}

		log.Write( "client closed the stream" );
	}

	async Task PumpServerToClientAsync( CancellationToken ct )
	{
		while ( await server.ReadAsync( ct ) is { } body )
		{
			var info = MessageInfo.Peek( body );

			if ( info.IsResponse && info.StringId is { } bridgeId && bridgeId.StartsWith( IdPrefix ) )
			{
				CompletePending( bridgeId, body );
				continue;
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

		await server.WriteAsync( body, ct );
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
	async Task<bool> TrySendFullSyncChangeAsync( byte[] body, CancellationToken ct )
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

		await server.WriteAsync( new JsonObject
		{
			["method"] = "textDocument/didClose",
			["params"] = new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } },
		}, ct );

		await server.WriteAsync( new JsonObject
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
			await server.WriteAsync( new JsonObject
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

	async Task OpenWorkspaceAsync( CancellationToken ct )
	{
		// Don't hold requests forever if loading hangs or never reports back.
		_ = Task.Delay( TimeSpan.FromMinutes( 2 ), ct ).ContinueWith( _ => _workspaceReady.TrySetResult(), TaskScheduler.Default );

		if ( _rootPath is null )
		{
			_workspaceReady.TrySetResult();
			await ShowMessageAsync( 2, "C#: Kate didn't send a workspace root, so no project was loaded.", ct );
			return;
		}

		var target = WorkspaceLocator.Find( _rootPath );

		if ( target.Warning is not null )
			await ShowMessageAsync( 2, $"C#: {target.Warning}", ct );

		if ( target.Solution is not null )
		{
			_loadedName = Path.GetFileName( target.Solution );
			log.Write( $"opening solution {target.Solution}" );
			await SendToServerAsync( "solution/open", new JsonObject { ["solution"] = PathToUri( target.Solution ) }, ct );
		}
		else if ( target.Projects.Count > 0 )
		{
			_loadedName = target.Projects.Count == 1 ? Path.GetFileName( target.Projects[0] ) : $"{target.Projects.Count} projects";
			log.Write( $"opening projects {string.Join( ", ", target.Projects )}" );
			var uris = new JsonArray( target.Projects.Select( p => (JsonNode)PathToUri( p ) ).ToArray() );
			await SendToServerAsync( "project/open", new JsonObject { ["projects"] = uris }, ct );
		}
		else
		{
			_workspaceReady.TrySetResult();
		}
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

		_ = Guard( server.WriteAsync( new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters }, ct ) );
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

	Task SendToServerAsync( string method, JsonNode parameters, CancellationToken ct ) =>
		server.WriteAsync( new JsonObject { ["method"] = method, ["params"] = parameters }, ct );

	Task ReplyToServerAsync( MessageInfo request, CancellationToken ct ) =>
		server.WriteAsync( new JsonObject { ["id"] = JsonNode.Parse( request.Id!.Value.GetRawText() ), ["result"] = null }, ct );

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
