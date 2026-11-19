[CmdletBinding()]
param([string]$ArchivePath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$release = Import-PowerShellDataFile (Join-Path $PSScriptRoot 'server-release.psd1')
$releaseTag = $release.Tag
$archiveUri = "https://github.com/antaalt/shader-sense/releases/download/$releaseTag/shader-language-server-x86_64-pc-windows-msvc.zip"
$archiveSha256 = $release.ArchiveSha256
$expectedFiles = $release.Files
$destination = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\ShaderTools.VisualStudio\Server'))

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToUpperInvariant()
}

function Get-ServerVersion {
    param([Parameter(Mandatory = $true)][string]$Root)

    $serverPath = Join-Path $Root 'shader-language-server.exe'
    $output = (& $serverPath --version 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "The server version check failed with exit code $LASTEXITCODE. Output: $output"
    }

    if ($output -ne "shader-language-server $releaseTag") {
        throw "Unexpected server version output '$output'; expected $releaseTag."
    }

    return $output
}

function Test-ServerBundle {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [switch]$CheckVersion
    )

    foreach ($entry in $expectedFiles.GetEnumerator()) {
        $path = Join-Path $Root $entry.Key
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            return $false
        }

        if ((Get-Sha256 -Path $path) -ne $entry.Value) {
            return $false
        }
    }

    if ($CheckVersion) {
        [void](Get-ServerVersion -Root $Root)
    }

    return $true
}

function Assert-SafeTemporaryDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    $temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    $candidate = [System.IO.Path]::GetFullPath($Path)
    $leafName = Split-Path -Leaf $candidate

    if (-not $candidate.StartsWith($temporaryRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not $leafName.StartsWith('shader-tools-server-', [System.StringComparison]::Ordinal)) {
        throw "Refusing to manage unsafe temporary path '$candidate'."
    }
}

if (-not $ArchivePath -and (Test-Path -LiteralPath $destination -PathType Container) -and
    (Test-ServerBundle -Root $destination -CheckVersion)) {
    Write-Host "Shader language server $releaseTag is already present and verified at '$destination'."
    exit 0
}

$temporaryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ('shader-tools-server-' + [Guid]::NewGuid().ToString('N'))
Assert-SafeTemporaryDirectory -Path $temporaryDirectory
[void](New-Item -ItemType Directory -Path $temporaryDirectory)

try {
    $extractionDirectory = Join-Path $temporaryDirectory 'extracted'
    [void](New-Item -ItemType Directory -Path $extractionDirectory)

    if (-not $ArchivePath) {
        $ArchivePath = Join-Path $temporaryDirectory 'shader-language-server.zip'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Write-Host "Downloading shader-language-server $releaseTag..."
        Invoke-WebRequest -UseBasicParsing -Uri $archiveUri -OutFile $ArchivePath
    }

    $actualArchiveHash = Get-Sha256 -Path $archivePath
    if ($actualArchiveHash -ne $archiveSha256) {
        throw "Archive SHA-256 mismatch. Expected $archiveSha256, received $actualArchiveHash."
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ArchivePath).Path)
    try {
        $entries = @($archive.Entries)
        if ($entries.Count -ne $expectedFiles.Count) {
            throw "Archive contains $($entries.Count) entries; expected exactly $($expectedFiles.Count)."
        }

        $seenNames = @{}
        foreach ($entry in $entries) {
            $normalizedName = $entry.FullName.Replace('\', '/')
            if ($normalizedName.Contains('/') -or
                [System.IO.Path]::IsPathRooted($normalizedName) -or
                $normalizedName.Contains(':') -or
                -not $expectedFiles.Contains($normalizedName) -or
                $seenNames.ContainsKey($normalizedName)) {
                throw "Archive contains an unsafe, unexpected, or duplicate entry '$($entry.FullName)'."
            }

            $seenNames[$normalizedName] = $true
            $targetPath = Join-Path $extractionDirectory $normalizedName
            $input = $entry.Open()
            $output = [System.IO.File]::Create($targetPath)
            try {
                $input.CopyTo($output)
            }
            finally {
                $output.Dispose()
                $input.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }

    if (-not (Test-ServerBundle -Root $extractionDirectory -CheckVersion)) {
        throw 'The extracted server bundle failed integrity validation.'
    }

    [void](New-Item -ItemType Directory -Path $destination -Force)
    foreach ($fileName in $expectedFiles.Keys) {
        Copy-Item -LiteralPath (Join-Path $extractionDirectory $fileName) -Destination (Join-Path $destination $fileName) -Force
    }

    if (-not (Test-ServerBundle -Root $destination -CheckVersion)) {
        throw 'The installed server bundle failed integrity validation.'
    }

    Write-Host "Installed and verified shader-language-server $releaseTag at '$destination'."
}
finally {
    Assert-SafeTemporaryDirectory -Path $temporaryDirectory
    if (Test-Path -LiteralPath $temporaryDirectory -PathType Container) {
        [System.IO.Directory]::Delete($temporaryDirectory, $true)
    }
}
