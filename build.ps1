<#
.SYNOPSIS
    Build, run, clean, or continuously rebuild Chromium Process Explorer.

.EXAMPLE
    .\build.ps1
    .\build.ps1 run
    .\build.ps1 clean
    .\build.ps1 watch

.NOTES
    Watch mode does not register itself to run at Windows logon.
#>
param(
    [ValidateSet('build', 'run', 'clean', 'watch')]
    [string] $Task = 'build',

    [ValidateRange(1, 86400)]
    [int] $PollSeconds = 60
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$solution = Join-Path $PSScriptRoot 'ChromiumProcessExplorer.sln'
$guiProject = Join-Path $PSScriptRoot 'src\ChromiumProcessExplorer.Gui\ChromiumProcessExplorer.Gui.csproj'
$guiExe = Join-Path $PSScriptRoot 'src\ChromiumProcessExplorer.Gui\bin\Debug\net9.0-windows\ChromiumProcessExplorer.exe'
$watchLog = Join-Path $env:LOCALAPPDATA 'ChromiumProcessExplorer\build-watch.log'

function Update-ProcessPath {
    $machinePath = [System.Environment]::GetEnvironmentVariable('Path', 'Machine')
    $userPath = [System.Environment]::GetEnvironmentVariable('Path', 'User')
    $paths = @($machinePath, $userPath) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    $env:Path = [string]::Join(';', $paths)
}

function Install-WingetPackage {
    param(
        [Parameter(Mandatory)]
        [string] $Id,

        [Parameter(Mandatory)]
        [string] $DisplayName
    )

    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw "$DisplayName is required and winget is unavailable. Install App Installer from the Microsoft Store, then rerun this script."
    }

    Write-Host "$DisplayName was not found. Installing it with winget..." -ForegroundColor Yellow
    & winget install `
        --id $Id `
        --exact `
        --source winget `
        --accept-source-agreements `
        --accept-package-agreements
    if ($LASTEXITCODE -ne 0) {
        throw "winget failed to install $DisplayName (exit $LASTEXITCODE)."
    }

    Update-ProcessPath
}

function Test-DotnetSdk {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        return $false
    }

    $sdks = & $dotnet.Source --list-sdks 2>$null
    return $LASTEXITCODE -eq 0 -and $sdks -match '^9\.'
}

function Ensure-DotnetSdk {
    if (Test-DotnetSdk) {
        return
    }

    Install-WingetPackage `
        -Id 'Microsoft.DotNet.SDK.9' `
        -DisplayName '.NET 9 SDK'
    if (-not (Test-DotnetSdk)) {
        throw '.NET 9 SDK installation completed, but the SDK is still unavailable in this PowerShell session.'
    }

    Write-Host '.NET 9 SDK installed.' -ForegroundColor Green
}

function Ensure-Git {
    if (Get-Command git -ErrorAction SilentlyContinue) {
        return
    }

    Install-WingetPackage -Id 'Git.Git' -DisplayName 'Git for Windows'
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        throw 'Git installation completed, but Git is still unavailable in this PowerShell session.'
    }

    Write-Host 'Git for Windows installed.' -ForegroundColor Green
}

function Invoke-Restore {
    & dotnet restore $solution
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed (exit $LASTEXITCODE)."
    }
}

function Invoke-Build {
    & dotnet build $guiProject --configuration Debug --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed (exit $LASTEXITCODE)."
    }
}

function Stop-ChromiumProcessExplorer {
    $processes = Get-Process -Name 'ChromiumProcessExplorer' -ErrorAction SilentlyContinue
    if (-not $processes) {
        return
    }

    $processes | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    Write-Host "  Stopped $($processes.Count) Chromium Process Explorer process(es)." -ForegroundColor Yellow
}

function Start-ChromiumProcessExplorer {
    if (-not (Test-Path $guiExe)) {
        Write-Warning "GUI executable not found at '$guiExe'."
        return
    }

    try {
        Start-Process -FilePath $guiExe -Verb RunAs
        Write-Host '  Started Chromium Process Explorer as administrator.' -ForegroundColor Green
    } catch {
        Write-Warning "Could not start Chromium Process Explorer as administrator: $_"
    }
}

function Write-WatchFailure {
    param([System.Management.Automation.ErrorRecord] $ErrorRecord)

    $message = @(
        "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')] Watch mode stopped."
        $ErrorRecord.ToString()
        $ErrorRecord.ScriptStackTrace
        ''
    ) -join [Environment]::NewLine

    try {
        $logDirectory = Split-Path $watchLog -Parent
        New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
        Add-Content -Path $watchLog -Value $message -Encoding utf8
    } catch {
        Write-Warning "Could not write the watch log: $_"
    }

    Write-Host ''
    Write-Host 'Watch mode stopped because of an unexpected error:' -ForegroundColor Red
    Write-Host $ErrorRecord -ForegroundColor Red
    Write-Host "Details were written to '$watchLog'." -ForegroundColor Yellow
}

function Start-ChromiumProcessExplorerIfNotRunning {
    $running = Get-Process -Name 'ChromiumProcessExplorer' -ErrorAction SilentlyContinue
    if ($running) {
        return
    }

    Start-ChromiumProcessExplorer
}

function Invoke-RebuildAndRestart {
    Stop-ChromiumProcessExplorer
    Invoke-Restore
    Invoke-Build
    Start-ChromiumProcessExplorer
}

function Get-LatestSourceChange {
    $extensions = '*.cs', '*.xaml', '*.csproj', '*.props', '*.targets', '*.json', '*.sln'
    $extensions |
        ForEach-Object {
            Get-ChildItem -Path $PSScriptRoot -Filter $_ -Recurse -File -ErrorAction SilentlyContinue |
                Where-Object {
                    $_.FullName -notmatch '\\(\.git|\.vs|bin|obj|TestResults)\\'
                }
        } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty LastWriteTime
}

try {
    Ensure-DotnetSdk
    if ($Task -eq 'watch') {
        Ensure-Git
    }

    if ($Task -ne 'clean') {
        Invoke-Restore
    }

    switch ($Task) {
    'build' {
        Invoke-Build
    }
    'run' {
        Invoke-Build
        Start-ChromiumProcessExplorer
    }
    'clean' {
        & dotnet clean $solution --configuration Debug
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet clean failed (exit $LASTEXITCODE)."
        }
    }
    'watch' {
        Push-Location $PSScriptRoot
        try {
            Write-Host "Watch mode started (polling every $PollSeconds s). Press Ctrl+C to stop." -ForegroundColor Cyan
            Write-Host 'Watch mode is not registered to run at Windows logon.' -ForegroundColor DarkGray

            $lastBuildTime = if (Test-Path $guiExe) {
                (Get-Item $guiExe).LastWriteTime
            } else {
                [datetime]::MinValue
            }
            Write-Host "  Last build: $(if ($lastBuildTime -eq [datetime]::MinValue) { 'never' } else { $lastBuildTime.ToString('HH:mm:ss') })" -ForegroundColor DarkGray

            Start-ChromiumProcessExplorerIfNotRunning

            & git fetch --quiet 2>$null
            if ($LASTEXITCODE -ne 0) {
                Write-Warning 'Initial git fetch failed; continuing with local state.'
            }
            $lastWasNoChange = $false

            while ($true) {
                if ($lastWasNoChange) {
                    Write-Host "`r`e[2K[$(Get-Date -Format 'HH:mm:ss')] Checking for changes..." -ForegroundColor DarkCyan -NoNewline
                } else {
                    Write-Host "`n[$(Get-Date -Format 'HH:mm:ss')] Checking for changes..." -ForegroundColor DarkCyan -NoNewline
                }
                $lastWasNoChange = $false
                $rebuilt = $false

                & git fetch --quiet 2>$null
                if ($LASTEXITCODE -ne 0) {
                    Write-Warning 'git fetch failed; continuing with local state.'
                }
                $behindText = & git rev-list 'HEAD..@{u}' --count 2>$null
                $behind = if ($LASTEXITCODE -eq 0) {
                    [int]$behindText
                } else {
                    0
                }

                if ($behind -gt 0) {
                    Write-Host ''
                    Write-Host "  $behind new commit(s) upstream - pulling..." -ForegroundColor Cyan
                    & git pull
                    if ($LASTEXITCODE -ne 0) {
                        Write-Warning 'git pull failed; local changes will still be checked.'
                    } else {
                        Write-Host '  Rebuilding...' -ForegroundColor Cyan
                        try {
                            Invoke-RebuildAndRestart
                            $lastBuildTime = Get-Date
                            $rebuilt = $true
                            Write-Host '  Done.' -ForegroundColor Green
                        } catch {
                            Write-Warning "Build failed: $_"
                            Write-Warning 'Will retry after another change.'
                        }
                    }
                }

                if (-not $rebuilt) {
                    $latestChange = Get-LatestSourceChange
                    if ($latestChange -and $latestChange -gt $lastBuildTime) {
                        Write-Host ''
                        Write-Host "  Local change detected (newest file: $($latestChange.ToString('HH:mm:ss'))) - rebuilding..." -ForegroundColor Cyan
                        try {
                            Invoke-RebuildAndRestart
                            $lastBuildTime = Get-Date
                            $rebuilt = $true
                            Write-Host '  Done.' -ForegroundColor Green
                        } catch {
                            Write-Warning "Build failed: $_"
                            Write-Warning 'Will retry after another change.'
                        }
                    }
                }

                if (-not $rebuilt) {
                    $running = Get-Process -Name 'ChromiumProcessExplorer' -ErrorAction SilentlyContinue
                    if (-not $running) {
                        Write-Host ''
                        Start-ChromiumProcessExplorerIfNotRunning
                    } else {
                        Write-Host ' none' -ForegroundColor DarkGray -NoNewline
                        $lastWasNoChange = $true
                    }
                }

                Start-Sleep -Seconds $PollSeconds
            }
        } finally {
            Pop-Location
        }
    }
    }
} catch {
    if ($Task -eq 'watch') {
        Write-WatchFailure $_
    } else {
        throw
    }
}
