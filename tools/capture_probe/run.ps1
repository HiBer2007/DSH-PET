# Chromium window-capture probe.
#
# Serves one page over http://127.0.0.1 (a secure context, so getDisplayMedia is
# allowed), launches Edge with --auto-select-desktop-capture-source=<title> so the
# picker is bypassed, and writes the frame the browser captured to shot.png.
#
# Used to answer one question: can a compositor-based capturer (WGC) read the pet's
# per-pixel-alpha layered window, when the GDI one (BitBlt) provably cannot?
param(
    [string]$WindowTitle = 'DSH Balance Pet',
    [string]$Out = 'shot.png',
    [switch]$DisableWgc,
    [int]$TimeoutSec = 45
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$port = 8099
$edge = @(
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'msedge.exe not found' }

$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add("http://127.0.0.1:$port/")
$listener.Start()
Write-Output "listening on $port, browser = $edge"

$profileDir = Join-Path $here ("prof_" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$flags = @(
    "--user-data-dir=$profileDir",
    '--no-first-run', '--no-default-browser-check',
    '--enable-usermedia-screen-capturing',
    "--auto-select-desktop-capture-source=$WindowTitle",
    "--app=http://127.0.0.1:$port/",
    '--window-size=200,200'
)
if ($DisableWgc) { $flags += '--disable-features=WebRtcAllowWgcWindowCapturer' }
$proc = Start-Process -FilePath $edge -ArgumentList $flags -PassThru
Write-Output "browser pid $($proc.Id)$(if ($DisableWgc) { '  (WGC capturer disabled)' })"

$deadline = (Get-Date).AddSeconds($TimeoutSec)
$shots = @()
# One pending context task for the whole run, replaced only once a request has been
# answered. Creating a fresh GetContextAsync per loop turn leaves the previous one
# pending and it is the abandoned one that receives the next request - which then
# hangs, the page never renders, and the run reports "no frame" for no visible reason.
$ctxTask = $listener.GetContextAsync()
try {
    while ((Get-Date) -lt $deadline) {
        if (-not $ctxTask.Wait(2000)) { continue }
        $ctx = $ctxTask.Result
        $ctxTask = $listener.GetContextAsync()
        $req = $ctx.Request
        $res = $ctx.Response
        if ($req.Url.AbsolutePath -eq '/status') {
            $sr = New-Object IO.StreamReader($req.InputStream)
            Write-Output ("status: " + $sr.ReadToEnd())
            $res.StatusCode = 204
            $res.Close()
            continue
        }
        if ($req.Url.AbsolutePath -eq '/shot') {
            $ms = New-Object IO.MemoryStream
            $req.InputStream.CopyTo($ms)
            $bytes = $ms.ToArray()
            Write-Output "frame: $($bytes.Length) bytes from $($req.Url.AbsolutePath)"
            [IO.File]::WriteAllBytes((Join-Path $here $Out), $bytes)
            $shots += (Join-Path $here $Out)
            $res.StatusCode = 204
            $res.Close()
            break
        }
        $bytes = [IO.File]::ReadAllBytes((Join-Path $here 'index.html'))
        $res.ContentType = 'text/html; charset=utf-8'
        $res.ContentLength64 = $bytes.Length
        $res.OutputStream.Write($bytes, 0, $bytes.Length)
        $res.Close()
    }
} finally {
    $listener.Stop()
    # Only the processes started against this run's profile: the user has their own
    # Edge open, and killing by image name would take it with us.
    Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine.Contains($profileDir) } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}
if ($shots.Count -eq 0) { Write-Output 'NO FRAME RECEIVED' } else { Write-Output "wrote $($shots -join ', ')" }
