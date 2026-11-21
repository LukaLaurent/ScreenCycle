# Builds ScreenCycle. No SDK needed: it only uses what ships with Windows.
#   powershell -ExecutionPolicy Bypass -File build.ps1
# Output:
#   app\ScreenCycle.ps1   - the program (all sources inside; Windows PowerShell compiles it in memory at start-up,
#                           so it runs even where Smart App Control blocks unsigned .exe files)
#   ..\ScreenCycle.cmd    - what people double-click: opens the small launch / set-up window
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }

$sourceFiles = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | Sort-Object Name
$refs = 'System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xml.dll','System.Management.dll','System.Web.Extensions.dll'

# --- compile check (C# 5, warnings shown) into a throw-away DLL
$check = Join-Path ([IO.Path]::GetTempPath()) ('ScreenCycle-check-' + [Guid]::NewGuid().ToString('N') + '.dll')
& $csc /nologo /target:library /langversion:5 /warn:4 "/out:$check" ($refs | ForEach-Object { "/reference:$_" }) ($sourceFiles | ForEach-Object { $_.FullName })
$ok = $LASTEXITCODE -eq 0
Remove-Item $check -ErrorAction SilentlyContinue
if (-not $ok) { throw 'Compilation failed' }

# --- the program
$parts = foreach ($f in $sourceFiles) {
    $text = [IO.File]::ReadAllText($f.FullName)
    if ($text -match "(?m)^'@") { throw "$($f.Name) contains a line starting with '@" }
    "# ---- $($f.Name)`r`n@'`r`n" + $text.TrimEnd().Replace("`r`n", "`n").Replace("`n", "`r`n") + "`r`n'@"
}
$template = [IO.File]::ReadAllText((Join-Path $root 'launcher.template.ps1'))
$ps1 = Join-Path $root 'ScreenCycle.ps1'
[IO.File]::WriteAllText($ps1, $template.Replace('##SOURCES##', ($parts -join ",`r`n")), (New-Object Text.UTF8Encoding($true)))

# --- the one file people click
$appName = Split-Path -Leaf $root
$cmd = "@echo off`r`n" +
       "rem ScreenCycle - created by Luka LAURENT. Opens the launch / set-up window.`r`n" +
       "start `"`" `"%SystemRoot%\System32\conhost.exe`" --headless `"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe`" " +
       "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"%~dp0$appName\ScreenCycle.ps1`" --welcome`r`n"
[IO.File]::WriteAllText((Join-Path (Split-Path -Parent $root) 'ScreenCycle.cmd'), $cmd)

Write-Host "Built:" -ForegroundColor Green
Write-Host ("  $appName\ScreenCycle.ps1  (" + [math]::Round((Get-Item $ps1).Length / 1KB) + " KB)")
Write-Host "  ScreenCycle.cmd"
