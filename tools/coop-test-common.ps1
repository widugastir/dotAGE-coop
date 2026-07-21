# Shared helpers for DotAgeCoop local two-copy test launches.
# ASCII-only to avoid PowerShell encoding/parser issues.

$ErrorActionPreference = "Stop"

$script:DefaultHostGame = "E:\Games\dotAGE"
$script:DefaultClientGame = "E:\Games\dotAGE_2"

function Get-CoopPaths {
    param(
        [string]$HostGame = $script:DefaultHostGame,
        [string]$ClientGame = $script:DefaultClientGame
    )

    if (-not (Test-Path (Join-Path $HostGame "dotAge.exe"))) {
        throw "Host game not found: $HostGame\dotAge.exe"
    }
    if (-not (Test-Path (Join-Path $ClientGame "dotAge.exe"))) {
        throw "Client game not found: $ClientGame\dotAge.exe"
    }

    [pscustomobject]@{
        HostGame   = $HostGame
        ClientGame = $ClientGame
        HostExe    = (Join-Path $HostGame "dotAge.exe")
        ClientExe  = (Join-Path $ClientGame "dotAge.exe")
    }
}

function Write-AutopilotCommand {
    param(
        [Parameter(Mandatory = $true)][string]$GameDir,
        [Parameter(Mandatory = $true)][ValidateSet("host", "client")][string]$Role,
        [Parameter(Mandatory = $true)][ValidateSet("newgame", "loadgame")][string]$Action,
        [double]$SettleSeconds = 12,
        [double]$JoinTimeout = 90
    )

    $dir = Join-Path $GameDir "UserData\DotAgeCoop"
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $path = Join-Path $dir "autopilot.txt"

    $lines = @(
        "# DotAgeCoop autopilot - consumed once by the mod on boot"
        "role=$Role"
        "action=$Action"
        "settle=$SettleSeconds"
        "jointimeout=$JoinTimeout"
    )
    $lines | Set-Content -Path $path -Encoding ASCII

    Write-Host "Wrote $path"
}

function Stop-DotAgeCopies {
    param(
        [string]$HostGame = $script:DefaultHostGame,
        [string]$ClientGame = $script:DefaultClientGame
    )

    $targets = @()
    Get-Process -Name "dotAge" -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            $path = $_.Path
            if ($path -and (
                    $path.StartsWith($HostGame, [StringComparison]::OrdinalIgnoreCase) -or
                    $path.StartsWith($ClientGame, [StringComparison]::OrdinalIgnoreCase))) {
                $targets += $_
            }
        }
        catch { }
    }

    if ($targets.Count -eq 0) {
        Write-Host "No running DotAGE copies to stop."
        return
    }

    Write-Host "Stopping $($targets.Count) DotAGE process(es)..."
    $targets | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

function Initialize-NativeWindowHelpers {
    if ("CoopTestNative" -as [type]) {
        return
    }

    $code = @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class CoopTestNative {
    public const int SW_MINIMIZE = 6;
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public static void MinimizeOwnConsole() {
        IntPtr hwnd = GetConsoleWindow();
        if (hwnd != IntPtr.Zero)
            ShowWindow(hwnd, SW_MINIMIZE);
    }

    public static List<IntPtr> FindMelonConsoles(int[] pids) {
        var set = new HashSet<uint>();
        foreach (var p in pids) set.Add((uint)p);
        var found = new List<IntPtr>();
        EnumWindows((hWnd, l) => {
            if (!IsWindowVisible(hWnd)) return true;
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            if (!set.Contains(pid)) return true;
            var sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            string title = sb.ToString();
            if (string.IsNullOrEmpty(title)) return true;
            if (title.IndexOf("MelonLoader", StringComparison.OrdinalIgnoreCase) >= 0 ||
                title.IndexOf("Melon Loader", StringComparison.OrdinalIgnoreCase) >= 0) {
                found.Add(hWnd);
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@

    Add-Type -TypeDefinition $code
}

function Minimize-OwnConsole {
    Initialize-NativeWindowHelpers
    [CoopTestNative]::MinimizeOwnConsole()
}

function Minimize-MelonConsoles {
    param(
        [int[]]$ProcessIds,
        [int]$Attempts = 20,
        [int]$DelayMs = 500
    )

    if (-not $ProcessIds -or $ProcessIds.Count -eq 0) {
        return
    }

    Initialize-NativeWindowHelpers

    $minimized = 0
    for ($i = 0; $i -lt $Attempts; $i++) {
        $hwnds = [CoopTestNative]::FindMelonConsoles($ProcessIds)
        foreach ($h in $hwnds) {
            [void][CoopTestNative]::ShowWindow($h, [CoopTestNative]::SW_MINIMIZE)
            $minimized++
        }
        if ($hwnds.Count -gt 0 -and $i -ge 3) {
            break
        }
        Start-Sleep -Milliseconds $DelayMs
    }

    Write-Host "Minimized MelonLoader console window(s) (hits~$minimized)."
}

function Start-CoopTestPair {
    param(
        [Parameter(Mandatory = $true)][ValidateSet("newgame", "loadgame")][string]$Action,
        [string]$HostGame = $script:DefaultHostGame,
        [string]$ClientGame = $script:DefaultClientGame,
        [double]$SettleSeconds = 12,
        [double]$JoinTimeout = 90,
        [switch]$KillExisting
    )

    $paths = Get-CoopPaths -HostGame $HostGame -ClientGame $ClientGame

    # Minimize this tool's own console immediately.
    Minimize-OwnConsole

    if ($KillExisting) {
        Stop-DotAgeCopies -HostGame $paths.HostGame -ClientGame $paths.ClientGame
    }

    Write-AutopilotCommand -GameDir $paths.HostGame -Role host -Action $Action `
        -SettleSeconds $SettleSeconds -JoinTimeout $JoinTimeout
    Write-AutopilotCommand -GameDir $paths.ClientGame -Role client -Action $Action `
        -SettleSeconds $SettleSeconds -JoinTimeout $JoinTimeout

    Write-Host "Launching HOST:  $($paths.HostExe)"
    $hostProc = Start-Process -FilePath $paths.HostExe -WorkingDirectory $paths.HostGame -PassThru
    Start-Sleep -Seconds 3

    Write-Host "Launching CLIENT: $($paths.ClientExe)"
    $clientProc = Start-Process -FilePath $paths.ClientExe -WorkingDirectory $paths.ClientGame -PassThru

    $pids = @($hostProc.Id, $clientProc.Id)
    Write-Host "PIDs: host=$($hostProc.Id) client=$($clientProc.Id)"
    Write-Host "Minimizing MelonLoader consoles..."
    Minimize-MelonConsoles -ProcessIds $pids

    Write-Host ""
    Write-Host "Autopilot armed:"
    Write-Host "  1) both reach title (~${SettleSeconds}s settle)"
    Write-Host "  2) host Local lobby, client Join Local"
    if ($Action -eq "newgame") {
        Write-Host "  3) host deletes current save (if any) and NewGame_Immediate"
    }
    else {
        Write-Host "  3) host LoadGame_Immediate (current save must exist)"
    }
    Write-Host "Watch in-game banner Autopilot / Melon log [Autopilot]."
}
