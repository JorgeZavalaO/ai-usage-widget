param(
    [string]$Version,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'tools\toolchain.ps1')

if (-not $Version) {
    $appInfo = Get-Content (Join-Path $root 'src\AppInfo.cs') -Raw
    $match = [regex]::Match($appInfo, 'Version\s*=\s*"([^"]+)"')
    if (-not $match.Success) { throw 'Cannot read AppInfo.Version.' }
    $Version = $match.Groups[1].Value
}

Push-Location $root
try {
    & (Join-Path $root 'build.ps1')
    if ($LASTEXITCODE -ne 0) { throw "build.ps1 exited with $LASTEXITCODE." }

    if (-not $SkipTests) {
        & (Join-Path $root 'test\run.ps1')
        if ($LASTEXITCODE -ne 0) { throw "test\run.ps1 exited with $LASTEXITCODE." }
    }

    $releaseRoot = Join-Path $root 'dist\release'
    New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null

    $baseName = "AIUsageWidget-$Version-win-x64"
    $stage = Join-Path $releaseRoot $baseName
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null

    foreach ($file in @('AIUsageWidget.exe')) {
        Copy-Item (Join-Path $root ('dist\' + $file)) (Join-Path $stage $file) -Force
    }
    foreach ($file in @('README.md', 'CHANGELOG.md')) {
        Copy-Item (Join-Path $root $file) (Join-Path $stage $file) -Force
    }

    $commit = (& git rev-parse --short HEAD 2>$null | Out-String).Trim()
    $dirty = (& git status --porcelain 2>$null | Out-String).Trim().Length -gt 0
    if ($dirty) { $commit = $commit + '-dirty' }

    $built = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    $compiler = [string](Find-CSharpCompiler)
    $references = [string](Find-ReferenceAssemblies)
    $verification = if ($SkipTests) { 'tests skipped' } else { 'build.ps1 and test\run.ps1 passed' }

    $info = @(
        'Product: AI Usage Widget',
        "Version: $Version",
        "Source commit: $commit",
        "Built (UTC): $built",
        "Compiler: $compiler",
        "Reference assemblies: $references",
        'Runtime requirement: .NET Framework 4.8',
        "Verification: $verification"
    )
    [System.IO.File]::WriteAllLines((Join-Path $stage 'BUILD-INFO.txt'), $info, (New-Object System.Text.UTF8Encoding($false)))

    $zip = Join-Path $releaseRoot ($baseName + '.zip')
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal

    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    [System.IO.File]::WriteAllText(
        $zip + '.sha256',
        $hash + '  ' + (Split-Path $zip -Leaf) + "`n",
        (New-Object System.Text.UTF8Encoding($false)))

    Write-Host "Release package: $zip" -ForegroundColor Green
    Write-Host "SHA-256: $hash"
    Write-Host 'Publication, tagging, signing, and licensing require maintainer approval.'
}
finally {
    Pop-Location
}
