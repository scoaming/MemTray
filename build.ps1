# Build MemTray.exe with the csc.exe that ships with .NET Framework 4.8.
# No SDK, no NuGet, no network access required.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }

# Stop a running instance so the exe is not locked.
Get-Process -Name MemTray -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300

# Generate a simple 32x32 tray-style icon for the exe itself.
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap 32, 32
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)
$borderPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 214, 219, 228)), 2
$g.DrawRectangle($borderPen, 6, 3, 19, 25)
$greenBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 46, 204, 113))
$g.FillRectangle($greenBrush, 9, 13, 13, 12)
$g.Dispose()
$hicon = $bmp.GetHicon()
$ico = [System.Drawing.Icon]::FromHandle($hicon)
$fs = [System.IO.File]::Create((Join-Path $root 'app.ico'))
$ico.Save($fs)
$fs.Close()
$ico.Dispose()
$bmp.Dispose()

$cscArgs = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    '/codepage:65001',
    ('/out:' + (Join-Path $root 'MemTray.exe')),
    ('/win32icon:' + (Join-Path $root 'app.ico')),
    ('/win32manifest:' + (Join-Path $root 'app.manifest')),
    '/r:System.dll',
    '/r:System.Core.dll',
    '/r:System.Drawing.dll',
    '/r:System.Windows.Forms.dll',
    (Join-Path $root 'MemTray.cs')
)

& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }

$exe = Join-Path $root 'MemTray.exe'
$fi = Get-Item $exe
"OK  {0}  {1:N0} bytes" -f $fi.FullName, $fi.Length
