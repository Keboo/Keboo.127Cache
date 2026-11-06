#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PackageDirectory,
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string] $ExpectedSha256
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path (Join-Path $repositoryRoot 'Keboo.127Cache') 'Keboo.127Cache.csproj'
[xml] $project = Get-Content -LiteralPath $projectPath -Raw
$versions = @($project.SelectNodes('/Project/PropertyGroup/Version'))
if ($versions.Count -ne 1 -or [string]::IsNullOrWhiteSpace($versions[0].InnerText)) {
    throw 'The tool project must define exactly one source Version.'
}
$sourceVersion = $versions[0].InnerText.Trim()
$framework = $project.SelectSingleNode('/Project/PropertyGroup/TargetFramework').InnerText.Trim()
$repositoryUrl = 'https://github.com/Keboo/Keboo.127Cache'
$packageDirectoryPath = (Resolve-Path -LiteralPath $PackageDirectory).Path
$packages = @(Get-ChildItem -LiteralPath $packageDirectoryPath -Filter '*.nupkg' -File)
if ($packages.Count -ne 1) {
    throw "Expected one package in $packageDirectoryPath; found $($packages.Count)."
}
$packagePath = $packages[0].FullName
$originalHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
if ($ExpectedSha256 -and $originalHash -ne $ExpectedSha256) {
    throw "Package metadata SHA-256 mismatch for $($packages[0].Name): expected $ExpectedSha256, actual $originalHash."
}

function Get-RequiredEntry {
    param([IO.Compression.ZipArchive] $Archive, [string] $Name)
    $entries = @($Archive.Entries | Where-Object { $_.FullName -ceq $Name })
    if ($entries.Count -ne 1) { throw "Expected exactly one package entry '$Name'; found $($entries.Count)." }
    return $entries[0]
}

function Get-EntryBytes {
    param([IO.Compression.ZipArchiveEntry] $Entry)
    $stream = $Entry.Open()
    $memory = [IO.MemoryStream]::new()
    try {
        $stream.CopyTo($memory)
        return ,($memory.ToArray())
    } finally {
        $stream.Dispose()
        $memory.Dispose()
    }
}

function Get-EntryText {
    param([IO.Compression.ZipArchiveEntry] $Entry)
    $reader = [IO.StreamReader]::new($Entry.Open())
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}

function Set-EntryText {
    param([IO.Compression.ZipArchive] $Archive, [string] $Name, [string] $Text)
    (Get-RequiredEntry $Archive $Name).Delete()
    $writer = [IO.StreamWriter]::new($Archive.CreateEntry($Name).Open(), [Text.UTF8Encoding]::new($false))
    try { $writer.Write($Text) } finally { $writer.Dispose() }
}

function Get-MetadataValue {
    param([System.Xml.XmlNode] $Metadata, [string] $Name)
    $node = $Metadata.SelectSingleNode("*[local-name()='$Name']")
    if (!$node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
        throw "The package nuspec metadata '$Name' is missing."
    }
    return $node.InnerText.Trim()
}

function Assert-PackedFile {
    param([IO.Compression.ZipArchive] $Archive, [string] $Name, [string] $Source)
    [byte[]] $bytes = Get-EntryBytes (Get-RequiredEntry $Archive $Name)
    $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
    $expected = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash
    if ($actual -ne $expected) { throw "Packaged '$Name' differs from its source file." }
    return ,$bytes
}

function Assert-PackedTextFile {
    param([IO.Compression.ZipArchive] $Archive, [string] $Name, [string] $Source)
    $actual = (Get-EntryText (Get-RequiredEntry $Archive $Name)).Replace("`r`n", "`n").Replace("`r", "`n")
    $expected = (Get-Content -LiteralPath $Source -Raw).Replace("`r`n", "`n").Replace("`r", "`n")
    if ($actual -cne $expected) { throw "Packaged '$Name' differs from its source file." }
}

function Read-PngUInt32 {
    param([byte[]] $Bytes, [int] $Offset)
    return [uint32] ([uint64] $Bytes[$Offset] * 16777216 + [uint64] $Bytes[$Offset + 1] * 65536 +
        [uint64] $Bytes[$Offset + 2] * 256 + [uint64] $Bytes[$Offset + 3])
}

function Get-PngCrc {
    param([byte[]] $Bytes, [int] $Offset, [int] $Length)
    [uint32] $crc = 4294967295
    for ($i = $Offset; $i -lt $Offset + $Length; $i++) {
        $crc = $crc -bxor [uint32] $Bytes[$i]
        for ($bit = 0; $bit -lt 8; $bit++) {
            if (($crc -band 1) -ne 0) {
                $crc = ($crc -shr 1) -bxor [uint32] 3988292384
            } else {
                $crc = $crc -shr 1
            }
        }
    }
    return ($crc -bxor [uint32] 4294967295)
}

function Assert-PngIcon {
    param([byte[]] $Bytes)
    $signature = [byte[]] @(137, 80, 78, 71, 13, 10, 26, 10)
    if ($Bytes.Length -lt 45 -or $Bytes.Length -gt 1MB) { throw 'The package icon.png has an invalid size.' }
    for ($i = 0; $i -lt $signature.Length; $i++) {
        if ($Bytes[$i] -ne $signature[$i]) { throw 'The package icon.png is not a PNG image.' }
    }
    if ([Text.Encoding]::ASCII.GetString($Bytes, 12, 4) -cne 'IHDR' -or
        $Bytes[8] -ne 0 -or $Bytes[9] -ne 0 -or $Bytes[10] -ne 0 -or $Bytes[11] -ne 13) {
        throw 'The package icon.png has an invalid PNG header.'
    }
    $width = Read-PngUInt32 $Bytes 16
    $height = Read-PngUInt32 $Bytes 20
    if ($width -ne 128 -or $height -ne 128) {
        throw "The package icon.png must be 128 by 128 pixels; found $width by $height."
    }
    $compressed = [IO.MemoryStream]::new()
    try {
        $offset = 8
        $hasData = $false
        $hasEnd = $false
        while ($offset -lt $Bytes.Length) {
            if ($offset -gt $Bytes.Length - 12) { throw 'The package icon.png has a truncated PNG chunk.' }
            $size = Read-PngUInt32 $Bytes $offset
            if ($size -gt $Bytes.Length - $offset - 12) { throw 'The package icon.png has a truncated PNG chunk.' }
            $length = [int] $size
            $type = [Text.Encoding]::ASCII.GetString($Bytes, $offset + 4, 4)
            $actual = Get-PngCrc $Bytes ($offset + 4) ($length + 4)
            $expected = Read-PngUInt32 $Bytes ($offset + 8 + $length)
            if ($actual -ne $expected) { throw "The package icon.png has a checksum mismatch in its $type chunk." }
            if ($type -ceq 'IHDR' -and $offset -ne 8) { throw 'The package icon.png has multiple PNG headers.' }
            if ($type -ceq 'IDAT') {
                $compressed.Write($Bytes, ($offset + 8), $length)
                $hasData = $true
            }
            $offset += $length + 12
            if ($type -ceq 'IEND') {
                if ($length -ne 0 -or $offset -ne $Bytes.Length) {
                    throw 'The package icon.png has an invalid PNG end marker.'
                }
                $hasEnd = $true
                break
            }
        }
        if (!$hasData -or !$hasEnd) { throw 'The package icon.png is missing PNG image data or its end marker.' }
        $compressed.Position = 0
        $decoder = [IO.Compression.ZLibStream]::new($compressed, [IO.Compression.CompressionMode]::Decompress)
        try {
            $buffer = [byte[]]::new(8192)
            $decoded = 0
            do {
                $count = $decoder.Read($buffer, 0, $buffer.Length)
                $decoded += $count
                if ($decoded -gt 262144) { throw 'The decompressed icon is unexpectedly large.' }
            } while ($count -gt 0)
            if ($decoded -eq 0) { throw 'The decompressed icon is empty.' }
        } catch {
            throw "The package icon.png contains invalid compressed image data: $($_.Exception.Message)"
        } finally {
            $decoder.Dispose()
        }
    } finally {
        $compressed.Dispose()
    }
}

function Assert-PackageMetadata {
    param([string] $Path)
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $manifests = @($archive.Entries | Where-Object { $_.FullName -match '^[^/\\]+\.nuspec$' })
        if ($manifests.Count -ne 1) { throw 'The package must contain exactly one root nuspec.' }
        [xml] $manifest = Get-EntryText $manifests[0]
        $metadata = $manifest.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
        if (!$metadata) { throw 'The package nuspec metadata is missing.' }

        $id = Get-MetadataValue $metadata 'id'
        $version = Get-MetadataValue $metadata 'version'
        if ($id -cne 'Keboo.127Cache') { throw "Unexpected package ID '$id'." }
        if ($version -cne $sourceVersion) {
            throw "The nuspec version '$version' does not match the source Version '$sourceVersion'."
        }
        $expectedFile = "$id.$($version.Split('+')[0]).nupkg"
        if ([IO.Path]::GetFileName($Path) -cne $expectedFile) {
            throw "Expected package filename '$expectedFile'; found '$([IO.Path]::GetFileName($Path))'."
        }
        if ((Get-MetadataValue $metadata 'projectUrl') -cne $repositoryUrl) {
            throw 'The nuspec projectUrl does not identify this repository.'
        }
        $repository = $metadata.SelectSingleNode("*[local-name()='repository']")
        if (!$repository -or $repository.GetAttribute('type') -cne 'git' -or
            $repository.GetAttribute('url') -cne $repositoryUrl) {
            throw 'The nuspec repository must identify this Git repository.'
        }
        $tags = (Get-MetadataValue $metadata 'tags') -split '[;,\s]+'
        foreach ($tag in @('nuget', 'github-actions', 'dotnet-tool')) {
            if ($tags -cnotcontains $tag) { throw "The nuspec tags are missing '$tag'." }
        }
        $null = Get-MetadataValue $metadata 'authors'
        $description = Get-MetadataValue $metadata 'description'
        if ($description.Length -lt 20 -or $description -eq 'Package Description') {
            throw 'The nuspec description must describe the tool.'
        }
        $license = $metadata.SelectSingleNode("*[local-name()='license']")
        if (!$license -or $license.GetAttribute('type') -cne 'expression' -or $license.InnerText -cne 'MIT') {
            throw 'The nuspec must retain its MIT license expression.'
        }
        $types = @($metadata.SelectNodes("*[local-name()='packageTypes']/*[local-name()='packageType']"))
        if ($types.Count -ne 1 -or $types[0].GetAttribute('name') -cne 'DotnetTool') {
            throw 'The package must identify itself as a .NET tool.'
        }
        if ((Get-MetadataValue $metadata 'readme') -cne 'README.md') {
            throw 'The nuspec must reference the packaged README.md.'
        }
        if ((Get-MetadataValue $metadata 'icon') -cne 'icon.png') {
            throw 'The nuspec must reference the packaged icon.png.'
        }
        Assert-PackedTextFile $archive 'README.md' (Join-Path $repositoryRoot 'README.md')
        Assert-PackedTextFile $archive 'LICENSE.md' (Join-Path $repositoryRoot 'LICENSE.md')
        [byte[]] $icon = Assert-PackedFile $archive 'icon.png' (Join-Path (Join-Path $repositoryRoot 'assets') 'icon.png')
        Assert-PngIcon $icon

        $toolRoot = "tools/$framework/any"
        $null = Get-RequiredEntry $archive "$toolRoot/Keboo.127Cache.dll"
        $null = Get-RequiredEntry $archive "$toolRoot/Keboo.127Cache.runtimeconfig.json"
        [xml] $settings = Get-EntryText (Get-RequiredEntry $archive "$toolRoot/DotnetToolSettings.xml")
        $commands = @($settings.SelectNodes('/DotNetCliTool/Commands/Command'))
        if ($commands.Count -ne 1 -or $commands[0].GetAttribute('Name') -cne 'keboo-127cache' -or
            $commands[0].GetAttribute('EntryPoint') -cne 'Keboo.127Cache.dll') {
            throw 'The packaged tool settings do not expose the expected command and entry point.'
        }
    } finally {
        $archive.Dispose()
    }
}

Assert-PackageMetadata $packagePath
Write-Host "PASS: $($packages[0].Name) has source version $sourceVersion, repository metadata, README, 128x128 PNG, and $framework tool contents."

$archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    [byte[]] $damagedIcon = Get-EntryBytes (Get-RequiredEntry $archive 'icon.png')
} finally {
    $archive.Dispose()
}
$offset = 8
$damaged = $false
while ($offset -lt $damagedIcon.Length) {
    $length = [int] (Read-PngUInt32 $damagedIcon $offset)
    $type = [Text.Encoding]::ASCII.GetString($damagedIcon, $offset + 4, 4)
    if ($type -ceq 'IDAT' -and $length -gt 0) {
        $damagedIcon[$offset + 8] = $damagedIcon[$offset + 8] -bxor 1
        $damaged = $true
        break
    }
    $offset += $length + 12
}
if (!$damaged) { throw 'Could not mutate the package icon PNG image data.' }
$rejected = $false
try {
    Assert-PngIcon $damagedIcon
} catch {
    if ($_.Exception.Message -notlike '*icon.png*checksum mismatch*') { throw }
    $rejected = $true
}
if (!$rejected) { throw 'A corrupted PNG image-data chunk was accepted.' }
Write-Host 'PASS: a mutation of the actual package icon PNG image data was rejected.'

$root = Join-Path (Join-Path $repositoryRoot 'TestResults') "package-metadata-$([Guid]::NewGuid().ToString('N'))"
if (Test-Path -LiteralPath $root) { throw "Metadata-test directory already exists: $root" }
try {
    $null = New-Item -ItemType Directory -Path $root
    foreach ($name in @('README.md', 'LICENSE.md')) {
        $mutatedPath = Join-Path $root $packages[0].Name
        Copy-Item -LiteralPath $packagePath -Destination $mutatedPath -Force
        $archive = [IO.Compression.ZipFile]::Open($mutatedPath, [IO.Compression.ZipArchiveMode]::Update)
        try {
            $text = Get-EntryText (Get-RequiredEntry $archive $name)
            $normalized = $text.Replace("`r`n", "`n").Replace("`r", "`n")
            $alternate = if ($text.Contains("`r`n")) { $normalized } else { $normalized.Replace("`n", "`r`n") }
            if ($alternate -ceq $text) { throw "Could not change the line endings of '$name'." }
            Set-EntryText $archive $name $alternate
        } finally {
            $archive.Dispose()
        }
        Assert-PackageMetadata $mutatedPath
        Write-Host "PASS: equivalent $name line endings are accepted across operating systems."
    }

    foreach ($case in @('projectUrl', 'version', 'README.md', 'README-content', 'icon.png')) {
        $mutatedPath = Join-Path $root $packages[0].Name
        Copy-Item -LiteralPath $packagePath -Destination $mutatedPath -Force
        $archive = [IO.Compression.ZipFile]::Open($mutatedPath, [IO.Compression.ZipArchiveMode]::Update)
        try {
            if ($case -in @('projectUrl', 'version')) {
                $entry = @($archive.Entries | Where-Object { $_.FullName -match '^[^/\\]+\.nuspec$' })[0]
                $name = $entry.FullName
                [xml] $manifest = Get-EntryText $entry
                $node = $manifest.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='$case']")
                $node.InnerText = if ($case -eq 'projectUrl') { 'https://example.invalid/wrong' } else { '999.999.999' }
                Set-EntryText $archive $name $manifest.OuterXml
            } elseif ($case -eq 'README.md') {
                (Get-RequiredEntry $archive 'README.md').Delete()
            } elseif ($case -eq 'README-content') {
                $text = Get-EntryText (Get-RequiredEntry $archive 'README.md')
                Set-EntryText $archive 'README.md' ($text + "`nPackage metadata mutation`n")
            } else {
                (Get-RequiredEntry $archive 'icon.png').Delete()
                $stream = $archive.CreateEntry('icon.png').Open()
                try {
                    [byte[]] $invalid = @(1, 2, 3)
                    $stream.Write($invalid, 0, $invalid.Length)
                } finally {
                    $stream.Dispose()
                }
            }
        } finally {
            $archive.Dispose()
        }
        $rejected = $false
        try {
            Assert-PackageMetadata $mutatedPath
        } catch {
            $expected = if ($case -eq 'README-content') { 'README.md' } else { $case }
            if ($_.Exception.Message -notlike "*$expected*") { throw }
            $rejected = $true
        }
        if (!$rejected) { throw "Package metadata mutation '$case' was not rejected." }
        Write-Host "PASS: a mutation of the actual package's $case was rejected."
    }
    if ((Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash -ne $originalHash) {
        throw 'The canonical package was changed during metadata tests.'
    }
} finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
