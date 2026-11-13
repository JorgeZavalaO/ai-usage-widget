$script:RequiredAssemblies = @(
    'PresentationFramework.dll',
    'PresentationCore.dll',
    'WindowsBase.dll',
    'System.Xaml.dll',
    'UIAutomationProvider.dll',
    'UIAutomationTypes.dll',
    'System.dll',
    'System.Core.dll',
    'System.Xml.dll',
    'System.Web.Extensions.dll',
    'System.Windows.Forms.dll',
    'System.Drawing.dll',
    'System.Security.dll'
)

function Find-CSharpCompiler {
    $vswhere = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.Roslyn.Compiler -find 'MSBuild\**\Bin\Roslyn\csc.exe' 2>$null |
            Select-Object -First 1
        if ($found -and (Test-Path $found)) { return $found }
    }

    $globs = @(
        (Join-Path ${env:ProgramFiles} 'Microsoft Visual Studio\*\*\MSBuild\Current\Bin\Roslyn\csc.exe'),
        (Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft Visual Studio\*\*\MSBuild\Current\Bin\Roslyn\csc.exe')
    )

    foreach ($glob in $globs) {
        $hit = Get-ChildItem $glob -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }

    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (Test-Path $framework) { return $framework }

    throw 'No C# compiler found. Install Visual Studio Build Tools with the Roslyn compiler, or use a Windows image that includes .NET Framework 4.x.'
}

function Find-ReferenceAssemblies {
    $root = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Reference Assemblies\Microsoft\Framework\.NETFramework'
    if (Test-Path $root) {
        $candidates = Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^v4\.' } |
            Sort-Object { try { [version]($_.Name.Substring(1)) } catch { [version]'0.0' } } -Descending

        if ($candidates -and $candidates.Count -gt 0) {
            return $candidates[0].FullName
        }
    }

    throw "Missing .NET Framework reference assemblies under '$root'. Install the .NET Framework 4.8 Developer Pack."
}

function Get-CompilerReferenceArguments {
    param([Parameter(Mandatory = $true)][string]$ReferenceDirectory)

    $refs = @()
    foreach ($assembly in $script:RequiredAssemblies) {
        $full = Join-Path $ReferenceDirectory $assembly
        if (-not (Test-Path $full)) {
            throw "Missing reference assembly: $full. Install the matching .NET Framework Developer Pack."
        }
        $refs += '/r:"' + $full + '"'
    }
    return $refs
}

function Test-NetFramework48Runtime {
    try {
        $release = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -Name Release -ErrorAction Stop).Release
        return ($release -ge 528040)
    }
    catch {
        return $false
    }
}

function Get-CSharpSourceFiles {
    param([Parameter(Mandatory = $true)][string]$Directory)

    if (-not (Test-Path $Directory)) { throw "Source directory not found: $Directory" }

    $sources = @(Get-ChildItem $Directory -Recurse -Filter *.cs -File |
        Sort-Object FullName |
        ForEach-Object { '"' + $_.FullName + '"' })

    if ($sources.Count -eq 0) { throw "No C# sources found under $Directory" }
    return $sources
}

function Invoke-CSharpCompiler {
    param(
        [Parameter(Mandatory = $true)][string]$Compiler,
        [Parameter(Mandatory = $true)][string]$ResponseFile,
        [Parameter(Mandatory = $true)][string[]]$ArgumentLines
    )

    [System.IO.File]::WriteAllLines($ResponseFile, $ArgumentLines, (New-Object System.Text.UTF8Encoding($false)))

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $Compiler
    $startInfo.Arguments = '@"' + $ResponseFile + '"'
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true

    $process = [System.Diagnostics.Process]::Start($startInfo)
    if (-not $process) { throw "Failed to start the C# compiler: $Compiler" }

    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()

    if ($stdout) { Write-Host $stdout.TrimEnd() }
    if ($stderr) { Write-Host $stderr.TrimEnd() }

    return $process.ExitCode
}
