[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [ValidateSet('win-x64')]
    [string] $RuntimeIdentifier = 'win-x64'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDirectory = Split-Path -Parent $PSCommandPath
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $scriptDirectory '..'))
$artifactsDirectory = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$publishRoot = [IO.Path]::GetFullPath((Join-Path $artifactsDirectory 'installer-publish'))
$projectPath = Join-Path $repositoryRoot 'src\StartDown\StartDown.csproj'
$installerScript = Join-Path $repositoryRoot 'installer\StartDown.iss'

if (-not $publishRoot.StartsWith($artifactsDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean a publish path outside the repository artifacts directory: $publishRoot"
}

if (Test-Path -LiteralPath $publishRoot) {
    Remove-Item -LiteralPath $publishRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null

$installerDirectory = [IO.Path]::GetFullPath((Join-Path $artifactsDirectory 'installer'))
if (-not $installerDirectory.StartsWith($artifactsDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to manage an installer path outside the repository artifacts directory: $installerDirectory"
}
New-Item -ItemType Directory -Path $installerDirectory -Force | Out-Null

$isccCandidates = @(
    $(if ($env:INNO_SETUP_HOME) { Join-Path $env:INNO_SETUP_HOME 'ISCC.exe' }),
    $(if (Get-Command ISCC.exe -ErrorAction SilentlyContinue) { (Get-Command ISCC.exe).Source }),
    'C:\Program Files\Inno Setup 7\ISCC.exe',
    'C:\Program Files (x86)\Inno Setup 7\ISCC.exe'
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }

$iscc = $isccCandidates | Select-Object -First 1
if (-not $iscc) {
    throw 'ISCC.exe was not found. Install Inno Setup 7 or set INNO_SETUP_HOME.'
}

$distributions = @(
    [pscustomobject]@{ Name = 'self-contained'; SelfContained = $true },
    [pscustomobject]@{ Name = 'framework-dependent'; SelfContained = $false }
)
$results = @()

foreach ($distribution in $distributions) {
    $publishDirectory = Join-Path $publishRoot $distribution.Name
    New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
    $selfContained = $distribution.SelfContained.ToString().ToLowerInvariant()

    & dotnet publish $projectPath `
        -c $Configuration `
        -r $RuntimeIdentifier `
        "-p:SelfContained=$selfContained" `
        '-p:PublishSingleFile=false' `
        '-p:PublishTrimmed=false' `
        '-p:PublishReadyToRun=false' `
        '-p:DebugType=None' `
        '-p:DebugSymbols=false' `
        -o $publishDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish for $($distribution.Name) failed with exit code $LASTEXITCODE."
    }

    $publishedExecutable = Join-Path $publishDirectory 'StartDown.exe'
    $appVersion = (Get-Item -LiteralPath $publishedExecutable).VersionInfo.ProductVersion.Trim()
    $expectedInstaller = Join-Path $installerDirectory "StartDown-Setup-$appVersion-win-x64-$($distribution.Name).exe"
    if (Test-Path -LiteralPath $expectedInstaller) {
        Remove-Item -LiteralPath $expectedInstaller -Force
    }

    & $iscc `
        "--define=PublishDir=$publishDirectory" `
        "--define=InstallerFlavor=$($distribution.Name)" `
        $installerScript
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup compilation for $($distribution.Name) failed with exit code $LASTEXITCODE."
    }

    if (-not (Test-Path -LiteralPath $expectedInstaller)) {
        throw "The expected installer was not produced: $expectedInstaller"
    }

    $results += [pscustomobject]@{
        Flavor = $distribution.Name
        Path = $expectedInstaller
        SHA256 = (Get-FileHash -LiteralPath $expectedInstaller -Algorithm SHA256).Hash
    }
}

foreach ($result in $results) {
    Write-Host "Installer ($($result.Flavor)): $($result.Path)"
    Write-Host "SHA-256: $($result.SHA256)"
}
