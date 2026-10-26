# Generates the "Cate Pro" icon theme: flat file-type glyphs in the theme's accent colours,
# inheriting Breeze Dark for every icon it doesn't override.
#
#   pwsh icons/build-icons.ps1 [-Out <dir>]     (default: icons/CatePro)

param( [string] $Out = (Join-Path $PSScriptRoot 'CatePro') )

$red = '#ff6188'; $orange = '#fc9867'; $yellow = '#ffd866'; $green = '#a9dc76'; $cyan = '#78dce8'; $purple = '#ab9df2'
$dim2 = '#939293'; $dim3 = '#727072'; $dim4 = '#5b595c'

function Stroke( $color, $width = 1.4, $fill = 'none', $fillOpacity = 1 ) { "fill=`"$fill`" fill-opacity=`"$fillOpacity`" stroke=`"$color`" stroke-width=`"$width`" stroke-linecap=`"round`" stroke-linejoin=`"round`"" }
function Label( $text, $color, $size = 8 ) { "<text x=`"8`" y=`"11.2`" text-anchor=`"middle`" font-family=`"Segoe UI, Arial, sans-serif`" font-weight=`"700`" font-size=`"$size`" fill=`"$color`">$text</text>" }

# Shared glyphs
$hash = { param( $c ) "<path d=`"M6.2 2.5 5 13.5M11 2.5 9.8 13.5M2.5 6h11M2 10h11`" $(Stroke $c 1.6)/>" }
$cArc = { param( $c, $x = 0 ) "<path transform=`"translate($x 0)`" d=`"M8.6 5.3A3.9 3.9 0 1 0 8.6 10.7`" $(Stroke $c 2)/>" }
$cube = "<path d=`"M8 1.8l5.6 3.1v6.2L8 14.2l-5.6-3.1V4.9zM2.4 4.9 8 8l5.6-3.1M8 8v6.2`" $(Stroke $purple 1.3)/>"

$icons = [ordered]@{
	# C# and s&box
	'mimetypes/text-x-csharp' = (& $cArc $purple) + "<path d=`"M11.4 5.6v4.8M13.9 5.6v4.8M10.2 7.1h5M10.2 8.9h5`" $(Stroke $purple 1.1)/>"
	'mimetypes/text-x-razor' = "<circle cx=`"8`" cy=`"8`" r=`"2.3`" $(Stroke $orange 1.5)/><path d=`"M10.3 8v1.1a1.6 1.6 0 0 0 3.2 0V8a5.5 5.5 0 1 0-2.2 4.4`" $(Stroke $orange 1.5)/>"
	'mimetypes/text-x-hlsl' = "<circle cx=`"8`" cy=`"8`" r=`"5.8`" $(Stroke $green 1.4)/><path d=`"M3.4 10.8a5.8 5.8 0 0 0 9.6 0 6.5 6.5 0 0 1-9.6 0z`" fill=`"$green`" opacity=`".55`"/><circle cx=`"10.2`" cy=`"5.8`" r=`"1.3`" fill=`"$green`"/>"
	'mimetypes/application-x-msbuild-project' = $cube
	'mimetypes/application-x-ms-solution' = "<path d=`"M8 2.2 14 5.2 8 8.2 2 5.2z`" $(Stroke $purple 1.2 $purple .3)/><path d=`"M2 8.2l6 3 6-3M2 11.2l6 3 6-3`" $(Stroke $purple 1.2)/>"
	'mimetypes/application-x-sbox-project' = "<path d=`"M8 1.8l5.6 3.1L8 8 2.4 4.9z`" fill=`"$yellow`"/><path d=`"M2.4 4.9 8 8v6.2l-5.6-3.1z`" fill=`"$orange`"/><path d=`"M13.6 4.9 8 8v6.2l5.6-3.1z`" fill=`"$red`"/>"
	'mimetypes/application-x-sbox-scene' = "<circle cx=`"8`" cy=`"8`" r=`"3.3`" $(Stroke $cyan 1.3 $cyan .35)/><ellipse cx=`"8`" cy=`"8`" rx=`"7`" ry=`"2.4`" transform=`"rotate(-20 8 8)`" $(Stroke $cyan 1.1)/>"
	'mimetypes/application-x-sbox-prefab' = "<path d=`"M8 1.8l5.4 3.1v6.2L8 14.2l-5.4-3.1V4.9z`" $(Stroke $orange 1.4)/><circle cx=`"8`" cy=`"8`" r=`"1.7`" fill=`"$orange`"/>"

	# Web and data
	'mimetypes/application-json' = "<path d=`"M5.5 2.5h-.3c-1.1 0-1.4.6-1.4 1.7v1.9C3.8 7.2 3.2 8 2 8c1.2 0 1.8.8 1.8 1.9v1.9c0 1.1.3 1.7 1.4 1.7h.3M10.5 2.5h.3c1.1 0 1.4.6 1.4 1.7v1.9c0 1.1.6 1.9 1.8 1.9-1.2 0-1.8.8-1.8 1.9v1.9c0 1.1-.3 1.7-1.4 1.7h-.3`" $(Stroke $yellow 1.4)/><circle cx=`"8`" cy=`"8`" r=`".95`" fill=`"$yellow`"/>"
	'mimetypes/application-xml' = "<path d=`"M5 4 1.6 8 5 12M11 4l3.4 4L11 12M9.3 2.8 6.7 13.2`" $(Stroke $red 1.5)/>"
	'mimetypes/text-html' = "<path d=`"M5.5 3.5 1.8 8l3.7 4.5M10.5 3.5 14.2 8l-3.7 4.5`" $(Stroke $orange 1.7)/>"
	'mimetypes/text-css' = (& $hash $cyan)
	'mimetypes/text-x-scss' = (& $hash $red)
	'mimetypes/application-x-yaml' = "<circle cx=`"3`" cy=`"4`" r=`"1`" fill=`"$red`"/><circle cx=`"5.5`" cy=`"8`" r=`"1`" fill=`"$red`"/><circle cx=`"5.5`" cy=`"12`" r=`"1`" fill=`"$red`"/><path d=`"M5.5 4h8M8 8h5.5M8 12h4`" $(Stroke $red 1.4)/>"
	'mimetypes/text-markdown' = "<rect x=`"1`" y=`"3.5`" width=`"14`" height=`"9`" rx=`"1.8`" $(Stroke $cyan 1.2)/><path d=`"M3.6 10V6l2 2.3 2-2.3v4M11.2 6v4M9.5 8.4l1.7 1.7 1.7-1.7`" $(Stroke $cyan 1.3)/>"

	# Languages
	'mimetypes/text-javascript' = (Label 'JS' $yellow)
	'mimetypes/application-x-typescript' = (Label 'TS' $cyan)
	'mimetypes/text-x-python' = (Label 'py' $green 9)
	'mimetypes/text-x-c++src' = (& $cArc $cyan -1) + "<path d=`"M10.5 8h4M12.5 6v4`" $(Stroke $cyan 1.2)/>"
	'mimetypes/text-x-csrc' = (& $cArc $cyan 1.5)
	'mimetypes/text-x-chdr' = "<path d=`"M4.5 2.5v11M4.5 9c0-1.8 1.2-3 3-3s3 1.2 3 3v4.5`" $(Stroke $purple 1.8)/>"
	'mimetypes/application-x-shellscript' = "<path d=`"M2.5 4.5 6 8l-3.5 3.5M8 12h5.5`" $(Stroke $green 1.6)/>"

	# Generic
	'mimetypes/text-plain' = "<path d=`"M3 4h10M3 7h10M3 10h7M3 13h5`" $(Stroke $dim2 1.4)/>"
	'mimetypes/text-x-generic' = "<path d=`"M3 4h10M3 7h10M3 10h7M3 13h5`" $(Stroke $dim2 1.4)/>"
	'mimetypes/image-x-generic' = "<rect x=`"1.5`" y=`"2.5`" width=`"13`" height=`"11`" rx=`"2`" $(Stroke $purple 1.2)/><circle cx=`"5.3`" cy=`"6.2`" r=`"1.3`" fill=`"$yellow`"/><path d=`"M2 12.5l4-4 2.5 2.5 2-2 3.5 3.5`" $(Stroke $purple 1.2)/>"

	# Folders
	'places/folder' = "<path d=`"M1.5 4.2c0-.8.6-1.4 1.4-1.4h3.3l1.5 1.6h5.4c.8 0 1.4.6 1.4 1.4v6.5c0 .8-.6 1.4-1.4 1.4H2.9c-.8 0-1.4-.6-1.4-1.4z`" fill=`"$dim4`"/><path d=`"M1.5 6.3h13`" stroke=`"$dim3`" stroke-width=`"1`"/>"
	'places/folder-open' = "<path d=`"M1.5 4.2c0-.8.6-1.4 1.4-1.4h3.3l1.5 1.6h5.4c.8 0 1.4.6 1.4 1.4v1H4.3c-.6 0-1.1.4-1.3 1L1.5 12z`" fill=`"$dim4`"/><path d=`"M3 7.6c.2-.6.7-1 1.3-1h10.2c.5 0 .9.5.7 1l-1.6 4.6c-.2.6-.7.9-1.3.9H2.2c-.5 0-.9-.5-.7-1z`" fill=`"$dim3`"/>"
}

# Same glyph, other names Kate or Qt may ask for
$aliases = @{
	'mimetypes/text-x-csharp' = @( 'mimetypes/text-x-csharp-src' )
	'mimetypes/text-x-c++src' = @( 'mimetypes/text-x-c++' )
	'mimetypes/text-x-chdr' = @( 'mimetypes/text-x-c++hdr' )
	'mimetypes/text-javascript' = @( 'mimetypes/application-javascript', 'mimetypes/text-x-javascript' )
	'mimetypes/application-x-typescript' = @( 'mimetypes/text-x-typescript' )
	'mimetypes/application-x-yaml' = @( 'mimetypes/application-yaml' )
	'mimetypes/image-x-generic' = @( 'mimetypes/image-png', 'mimetypes/image-jpeg', 'mimetypes/image-svg+xml', 'mimetypes/image-x-tga' )
	'places/folder' = @( 'mimetypes/inode-directory' )
}

foreach ( $entry in $icons.GetEnumerator() )
{
	$svg = "<svg xmlns=`"http://www.w3.org/2000/svg`" width=`"16`" height=`"16`" viewBox=`"0 0 16 16`">$($entry.Value)</svg>"

	# Qt silently draws nothing for malformed SVG (e.g. a duplicated attribute), so catch it here
	try { [xml] $svg | Out-Null } catch { throw "Icon $($entry.Key) is not valid SVG: $($_.Exception.Message)" }

	foreach ( $name in @( $entry.Key ) + @( $aliases[$entry.Key] | Where-Object { $_ } ) )
	{
		$path = Join-Path $Out "scalable/$name.svg"
		New-Item -ItemType Directory -Force -Path ( Split-Path $path ) | Out-Null
		[IO.File]::WriteAllText( $path, $svg )
	}
}

[IO.File]::WriteAllText( ( Join-Path $Out 'index.theme' ), @"
[Icon Theme]
Name=Cate Pro
Comment=Cate Pro file icons, everything else from Breeze Dark
Inherits=breeze-dark,breeze,hicolor
FollowsColorScheme=true
Directories=scalable/mimetypes,scalable/places

[scalable/mimetypes]
Size=16
MinSize=8
MaxSize=512
Type=Scalable
Context=MimeTypes

[scalable/places]
Size=16
MinSize=8
MaxSize=512
Type=Scalable
Context=Places
"@ )

$count = ( Get-ChildItem $Out -Recurse -Filter *.svg ).Count
Write-Host "Wrote $count icons to $Out"
