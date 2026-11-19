[CmdletBinding()]
param(
    [string]$VsixPath,
    [string]$FormatDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
# Windows PowerShell 5.1 may not populate PSScriptRoot while evaluating parameter
# defaults when launched with -File. Resolve the default after parameter binding.
if ([string]::IsNullOrWhiteSpace($VsixPath)) {
    $VsixPath = Join-Path $repositoryRoot 'ShaderTools.VisualStudio\bin\Release\ShaderTools.VisualStudio.vsix'
}
$smokeTestPath = Join-Path $repositoryRoot 'tests\lsp-smoke.mjs'
$absoluteVsixPath = [System.IO.Path]::GetFullPath($VsixPath)
$temporaryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ('shader-tools-vsix-' + [Guid]::NewGuid().ToString('N'))

function Assert-SafeTemporaryDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    $temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    $candidate = [System.IO.Path]::GetFullPath($Path)
    $leafName = Split-Path -Leaf $candidate
    if (-not $candidate.StartsWith($temporaryRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not $leafName.StartsWith('shader-tools-vsix-', [System.StringComparison]::Ordinal)) {
        throw "Refusing to manage unsafe temporary path '$candidate'."
    }
}

function Assert-PackageFile {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    $path = Join-Path $Root $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "VSIX is missing required file '$RelativePath'."
    }
}

function Find-ClangFormatDirectory {
    if (-not [string]::IsNullOrWhiteSpace($FormatDirectory)) {
        $requested = [System.IO.Path]::GetFullPath($FormatDirectory)
        if (-not (Test-Path -LiteralPath (Join-Path $requested 'clang-format.exe') -PathType Leaf)) {
            throw "The requested format directory '$requested' does not contain clang-format.exe."
        }
        return $requested
    }

    $command = @(Get-Command 'clang-format.exe' -CommandType Application -ErrorAction SilentlyContinue) | Select-Object -First 1
    if ($null -ne $command) {
        return Split-Path -Parent $command.Source
    }

    $candidates = [System.Collections.Generic.List[string]]::new()
    $candidates.Add((Join-Path $env:ProgramFiles 'LLVM\bin'))
    $vswherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswherePath -PathType Leaf) {
        $installations = @(& $vswherePath -all -products '*' -version '[17.14,19.0)' -property installationPath)
        foreach ($installation in $installations) {
            if (-not [string]::IsNullOrWhiteSpace($installation)) {
                $candidates.Add((Join-Path $installation.Trim() 'VC\Tools\Llvm\x64\bin'))
                $candidates.Add((Join-Path $installation.Trim() 'VC\Tools\Llvm\bin'))
            }
        }
    }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath (Join-Path $candidate 'clang-format.exe') -PathType Leaf) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }
    return $null
}

if (-not (Test-Path -LiteralPath $absoluteVsixPath -PathType Leaf)) {
    throw "VSIX not found at '$absoluteVsixPath'. Run scripts\build.ps1 first or pass -VsixPath."
}
if (-not (Test-Path -LiteralPath $smokeTestPath -PathType Leaf)) {
    throw "LSP smoke test not found at '$smokeTestPath'."
}

$node = @(Get-Command 'node' -CommandType Application -ErrorAction SilentlyContinue) | Select-Object -First 1
if ($null -eq $node) {
    throw 'Node.js was not found; it is required to run tests\lsp-smoke.mjs.'
}

Assert-SafeTemporaryDirectory -Path $temporaryDirectory
[void](New-Item -ItemType Directory -Path $temporaryDirectory)

try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($absoluteVsixPath)
    try {
        $seenTargets = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        $rootPrefix = [System.IO.Path]::GetFullPath($temporaryDirectory).TrimEnd('\') + '\'
        foreach ($entry in $archive.Entries) {
            $normalized = $entry.FullName.Replace('\', '/')
            $segments = @($normalized.Split('/') | Where-Object { $_.Length -gt 0 })
            if ([string]::IsNullOrWhiteSpace($normalized) -or
                [System.IO.Path]::IsPathRooted($normalized) -or
                $normalized.Contains(':') -or
                $segments -contains '..' -or
                $segments -contains '.') {
                throw "VSIX contains an unsafe entry '$($entry.FullName)'."
            }

            $target = [System.IO.Path]::GetFullPath((Join-Path $temporaryDirectory ($normalized.Replace('/', '\'))))
            if (-not $target.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "VSIX entry '$($entry.FullName)' escapes the extraction directory."
            }
            if (-not $seenTargets.Add($target)) {
                throw "VSIX contains entries that resolve to duplicate path '$target'."
            }
            if ($normalized.EndsWith('/')) {
                [void](New-Item -ItemType Directory -Path $target -Force)
                continue
            }

            $parent = Split-Path -Parent $target
            [void](New-Item -ItemType Directory -Path $parent -Force)
            $input = $entry.Open()
            $output = [System.IO.File]::Create($target)
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

    $requiredFiles = @(
        'extension.vsixmanifest',
        'ShaderTools.VisualStudio.dll',
        'ShaderTools.Core.dll',
        'ShaderTools.VisualStudio.pkgdef',
        'ShaderTools.pkgdef',
        'Bridge\ShaderTools.Bridge.exe',
        'Bridge\ShaderTools.Core.dll',
        'Bridge\Newtonsoft.Json.dll',
        'Server\shader-language-server.exe',
        'Server\dxcompiler.dll',
        'Server\dxil.dll',
        'Grammars\glsl.tmLanguage.json',
        'Grammars\hlsl.tmLanguage.json',
        'Grammars\wgsl.tmLanguage.json',
        'Grammars\shader-language-configuration.json',
        'licenses\THIRD-PARTY-NOTICES.md',
        'licenses\shader-sense-LICENSE.txt',
        'licenses\ShaderLanguageServer-ThirdPartyNotices.txt',
        'licenses\shader-validator-LICENSE.txt',
        'licenses\Newtonsoft.Json-LICENSE.txt',
        'licenses\DirectXShaderCompiler-LICENSE.txt',
        'licenses\DirectXShaderCompiler-ThirdPartyNotices.txt'
    )
    foreach ($relativePath in $requiredFiles) {
        Assert-PackageFile -Root $temporaryDirectory -RelativePath $relativePath
    }

    foreach ($relativePath in $requiredFiles) {
        if ($relativePath -like 'licenses\*') {
            $source = Join-Path $repositoryRoot $relativePath
            if ($relativePath -eq 'licenses\THIRD-PARTY-NOTICES.md') {
                $source = Join-Path $repositoryRoot 'THIRD-PARTY-NOTICES.md'
            }
            $packaged = Join-Path $temporaryDirectory $relativePath
            if ((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $packaged).Hash) {
                throw "Packaged notice '$relativePath' differs from the repository copy."
            }
        }
    }

    [xml]$manifest = Get-Content -Raw -LiteralPath (Join-Path $temporaryDirectory 'extension.vsixmanifest')
    $namespaces = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $namespaces.AddNamespace('v', 'http://schemas.microsoft.com/developer/vsx-schema/2011')
    $identity = $manifest.SelectSingleNode('/v:PackageManifest/v:Metadata/v:Identity', $namespaces)
    $displayName = $manifest.SelectSingleNode('/v:PackageManifest/v:Metadata/v:DisplayName', $namespaces)
    if ($null -eq $identity -or $identity.Id -ne 'ShaderTools.f373ca48-f553-49ee-a743-4470b600021b') {
        throw 'VSIX identity changed; existing installations would not receive this package as an update.'
    }
    [xml]$sourceManifest = Get-Content -Raw -LiteralPath (Join-Path $repositoryRoot 'ShaderTools.VisualStudio\source.extension.vsixmanifest')
    $expectedVersion = $sourceManifest.PackageManifest.Metadata.Identity.Version
    if ($identity.Version -ne $expectedVersion) {
        throw "VSIX version '$($identity.Version)' does not match source manifest version '$expectedVersion'. Rebuild the package."
    }
    if ($null -eq $displayName -or $displayName.InnerText -ne 'Shader Tools for Visual Studio') {
        throw 'VSIX display name does not match the product name.'
    }
    $icon = $manifest.SelectSingleNode('/v:PackageManifest/v:Metadata/v:Icon', $namespaces)
    if ($null -ne $icon) {
        if ($icon.InnerText -match '(^|[/\\])\.\.([/\\]|$)' -or [IO.Path]::IsPathRooted($icon.InnerText)) {
            throw 'VSIX icon must use a path inside the package.'
        }
        Assert-PackageFile -Root $temporaryDirectory -RelativePath $icon.InnerText
    }

    # A namespace rename must update the loadable package class without changing registration identities.
    $registration = Get-Content -Raw -LiteralPath (Join-Path $temporaryDirectory 'ShaderTools.VisualStudio.pkgdef')
    $requiredRegistration = @(
        '[$RootKey$\Packages\{ffe86866-e88b-483c-ba23-3346fff7bde5}]',
        '"Class"="ShaderTools.VisualStudio.ShaderToolsPackage"',
        '"CodeBase"="$PackageFolder$\ShaderTools.VisualStudio.dll"',
        '"Page"="{f4fe06d8-6156-309d-b308-2ebf880d2276}"',
        '[$RootKey$\ToolWindows\{b34aec9d-66df-42ee-9ea2-a712ae729a75}]'
    )
    foreach ($expected in $requiredRegistration) {
        if (-not $registration.Contains($expected)) {
            throw "VSIX package registration is missing '$expected'."
        }
    }
    $assets = @($manifest.SelectNodes('/v:PackageManifest/v:Assets/v:Asset', $namespaces))
    if (-not ($assets | Where-Object { $_.Type -eq 'Microsoft.VisualStudio.MefComponent' -and $_.Path -eq 'ShaderTools.VisualStudio.dll' })) {
        throw 'VSIX manifest is missing the ShaderTools.VisualStudio MEF component asset.'
    }
    if (-not ($assets | Where-Object { $_.Type -eq 'Microsoft.VisualStudio.VsPackage' -and $_.Path -eq 'ShaderTools.VisualStudio.pkgdef' })) {
        throw 'VSIX manifest is missing the generated Visual Studio package asset.'
    }
    if (-not ($assets | Where-Object { $_.Type -eq 'Microsoft.VisualStudio.VsPackage' -and $_.Path -eq 'ShaderTools.pkgdef' })) {
        throw 'VSIX manifest is missing the native ShaderTools.pkgdef package asset.'
    }

    $bridgePath = Join-Path $temporaryDirectory 'Bridge\ShaderTools.Bridge.exe'
    $serverPath = Join-Path $temporaryDirectory 'Server\shader-language-server.exe'
    $release = Import-PowerShellDataFile (Join-Path $PSScriptRoot 'server-release.psd1')
    $serverFiles = @(Get-ChildItem -LiteralPath (Join-Path $temporaryDirectory 'Server') -File -Recurse)
    if ($serverFiles.Count -ne $release.Files.Count) { throw 'Unexpected files in the packaged server bundle.' }
    foreach ($entry in $release.Files.GetEnumerator()) {
        $path = Join-Path $temporaryDirectory ("Server\" + $entry.Key)
        if ((Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash -ne $entry.Value) {
            throw "Packaged $($entry.Key) failed SHA-256 verification against release $($release.Tag)."
        }
    }
    & $node.Source (Join-Path $repositoryRoot 'tests\backend-regression.mjs') --bridge $bridgePath --server $serverPath
    if ($LASTEXITCODE -ne 0) { throw "Packaged bridge regression failed with exit code $LASTEXITCODE." }
    $smokeArguments = @($smokeTestPath, '--bridge', $bridgePath, '--server', $serverPath)
    $clangFormatDirectory = Find-ClangFormatDirectory
    if ($null -eq $clangFormatDirectory) {
        Write-Host 'SKIP package formatting assertion: clang-format.exe was not found.'
    }
    else {
        Write-Host "Using clang-format from '$clangFormatDirectory'."
        $smokeArguments += @('--format-directory', $clangFormatDirectory)
    }

    Write-Host "Package contents and manifest assets verified. Running real-server smoke tests from '$temporaryDirectory'..."
    & $node.Source @smokeArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Packaged real-server smoke tests failed with exit code $LASTEXITCODE."
    }

    Write-Host "PASS packaged VSIX: $absoluteVsixPath"
}
finally {
    Assert-SafeTemporaryDirectory -Path $temporaryDirectory
    if (Test-Path -LiteralPath $temporaryDirectory -PathType Container) {
        [System.IO.Directory]::Delete($temporaryDirectory, $true)
    }
}
