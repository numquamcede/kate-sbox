# Installs kate-sbox for the current user (Windows). Run it with Kate closed, since Kate rewrites its
# config on exit. Needs the .NET 10 SDK. Safe to run again, e.g. after an s&box update.
#
#   powershell -ExecutionPolicy Bypass -File install.ps1 [-RoslynVersion <version>] [-SboxPath <path>]

param(
	# Microsoft.CodeAnalysis.LanguageServer build to use; the same server VS Code's C# extension ships
	[string] $RoslynVersion = '5.0.0-2.26521.8',
	# s&box install folder; found from FACEPUNCH_ENGINE or Steam when not given
	[string] $SboxPath
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$data = $env:LOCALAPPDATA
$tools = "$data\kate-sbox"

# Some packaged apps (their terminals and the tools they run) keep AppData writes in their own folder
# under Packages, where Kate never looks: the install would look fine from here and do nothing for Kate
$probe = "kate-sbox-probe-$PID.tmp"
Set-Content "$data\$probe" ''
$redirected = Get-ChildItem "$data\Packages" -Directory -ErrorAction SilentlyContinue |
	Where-Object { Test-Path "$( $_.FullName )\LocalCache\Local\$probe" } | Select-Object -First 1
Remove-Item "$data\$probe" -Force
if ( $redirected )
{
	Write-Error "AppData writes from this PowerShell are redirected into the $( $redirected.Name ) app's folder, where Kate won't find them. Run install.ps1 from a normal PowerShell window (Start menu > Windows PowerShell)."
	return
}

if ( Get-Process kate -ErrorAction SilentlyContinue )
{
	Write-Error "Close Kate first; it overwrites its settings when it exits."
	return
}

# --- C# language server: the bridge, plus the Roslyn server it runs -----------------------------------

Write-Host "Building roslyn-kate..."
dotnet publish "$root\bridge\RoslynKate.csproj" -c Release -o $tools --nologo -v q
if ( $LASTEXITCODE -ne 0 ) { throw "Building the bridge failed." }

$installed = if ( Test-Path "$tools\roslyn\VERSION" ) { ( Get-Content "$tools\roslyn\VERSION" ).Trim() }
if ( $installed -ne $RoslynVersion )
{
	Write-Host "Downloading Roslyn language server $RoslynVersion..."
	$id = 'microsoft.codeanalysis.languageserver.win-x64'
	$url = "https://pkgs.dev.azure.com/azure-public/vside/_packaging/vs-impl/nuget/v3/flat2/$id/$RoslynVersion/$id.$RoslynVersion.nupkg"
	$work = Join-Path $env:TEMP "kate-sbox-roslyn"
	Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
	New-Item -ItemType Directory -Force $work | Out-Null

	Invoke-WebRequest $url -OutFile "$work\roslyn.zip" -UseBasicParsing
	Expand-Archive "$work\roslyn.zip" "$work\x"

	Remove-Item "$tools\roslyn" -Recurse -Force -ErrorAction SilentlyContinue
	Copy-Item "$work\x\content\LanguageServer\win-x64" "$tools\roslyn" -Recurse
	Set-Content "$tools\roslyn\VERSION" $RoslynVersion
	Remove-Item $work -Recurse -Force
}

# --- s&box: the "Kate" code editor option -----------------------------------------------------------

if ( -not $SboxPath )
{
	$SboxPath = [Environment]::GetEnvironmentVariable( 'FACEPUNCH_ENGINE', 'User' )
	if ( -not $SboxPath ) { $SboxPath = "${env:ProgramFiles(x86)}\Steam\steamapps\common\sbox" }
}

$codeEditors = "$SboxPath\addons\tools\Code\CodeEditors"
if ( Test-Path $codeEditors )
{
	# A running s&box editor hotloads its whole tools library when this changes, so only copy if needed
	$target = "$codeEditors\CodeEditor.Kate.cs"
	if ( -not ( Test-Path $target ) -or ( Get-FileHash $target ).Hash -ne ( Get-FileHash "$root\sbox\CodeEditor.Kate.cs" ).Hash )
	{
		Copy-Item "$root\sbox\CodeEditor.Kate.cs" $codeEditors -Force
	}
	Write-Host "Kate is in s&box's code editors ($codeEditors)"
}
else
{
	Write-Warning "s&box not found at $SboxPath; pass -SboxPath to add the Kate code editor option."
}

# --- Kate: theme, highlighting, icons, LSP and settings ------------------------------------------------

function Copy-To( $src, $dir ) { New-Item -ItemType Directory -Force $dir | Out-Null; Copy-Item $src $dir -Force }

# Sets keys in a KDE-style ini file, keeping everything else in it.
function Set-KConfig( [string] $path, [string] $group, [hashtable] $values )
{
	$lines = [Collections.Generic.List[string]]::new()
	if ( Test-Path $path ) { $lines.AddRange( [string[]]( Get-Content $path ) ) }

	$start = $lines.IndexOf( "[$group]" )
	if ( $start -lt 0 )
	{
		if ( $lines.Count -gt 0 -and $lines[$lines.Count - 1] -ne '' ) { $lines.Add( '' ) }
		$lines.Add( "[$group]" )
		$start = $lines.Count - 1
	}

	$end = $start + 1
	while ( $end -lt $lines.Count -and -not $lines[$end].StartsWith( '[' ) ) { $end++ }

	foreach ( $key in $values.Keys )
	{
		$entry = "$key=$($values[$key])"
		$found = $false
		for ( $i = $start + 1; $i -lt $end; $i++ )
		{
			if ( $lines[$i].StartsWith( "$key=" ) ) { $lines[$i] = $entry; $found = $true; break }
		}

		if ( -not $found )
		{
			# Insert before the blank lines that end the group
			$at = $end
			while ( $at -gt $start + 1 -and $lines[$at - 1] -eq '' ) { $at-- }
			$lines.Insert( $at, $entry )
			$end++
		}
	}

	[IO.File]::WriteAllLines( $path, $lines )
}

Copy-To "$root\theme\Cate Pro.theme" "$data\org.kde.syntax-highlighting\themes"
Copy-To "$root\syntax\*.xml" "$data\org.kde.syntax-highlighting\syntax"
Copy-To "$root\colors\CatePro.colors" "$data\color-schemes"
Copy-To "$root\mime\kate-sbox.xml" "$data\mime\packages"
& "$root\icons\build-icons.ps1" -Out "$data\icons\CatePro"

# C# through the Roslyn bridge; overrides Kate's default OmniSharp entry
$bridge = "$data\kate-sbox\roslyn-kate.exe" -replace '\\', '/'
$lsp = "$data\kate\lspclient"
New-Item -ItemType Directory -Force $lsp | Out-Null
if ( Test-Path "$lsp\settings.json" ) { Copy-Item "$lsp\settings.json" "$lsp\settings.json.bak" -Force }
[IO.File]::WriteAllText( "$lsp\settings.json", @"
{
    "servers": {
        "c-sharp": {
            "command": ["$bridge"],
            "rootIndicationFilePatterns": ["*.slnx", "*.sln", "*.sbproj", "*.csproj"],
            "url": "https://github.com/dotnet/roslyn",
            "highlightingModeRegex": "^C#$"
        }
    }
}
"@ )

$font = if ( Test-Path "$env:WINDIR\Fonts\CascadiaCode.ttf" ) { 'Cascadia Code' } else { 'Consolas' }

$katerc = "$data\katerc"
Set-KConfig $katerc 'UiSettings' @{ ColorScheme = 'CatePro' }
Set-KConfig $katerc 'KTextEditor Renderer' @{
	'Auto Color Theme Selection' = 'false'
	'Color Theme' = 'Cate Pro'
	'Text Font' = "$font,10,-1,5,400,0,0,0,0,0,0,0,0,0,0,1"
}
# Documents list tints for edited / recently viewed files: the theme's orange and purple
Set-KConfig $katerc 'filetree' @{ editShade = '252,152,103'; viewShade = '171,157,242' }
Set-KConfig $katerc 'lspclient' @{
	AllowedServerCommandLines = $bridge
	IncrementalSync = 'true'
	SemanticHighlighting = 'true'
	InlayHints = 'true'
	AutoHover = 'true'
	SignatureHelp = 'true'
}

Set-KConfig "$data\kdeglobals" 'Icons' @{ Theme = 'CatePro' }

Write-Host "kate-sbox installed. Start Kate, and pick Kate under Edit > Preferences > Code Editor in s&box."
