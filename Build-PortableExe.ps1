$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc  = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$outExe = Join-Path $root "HaierHonDesktop.exe"
$uiHtml = Join-Path $root "ui\index.html"

Get-Process HaierHonDesktop -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

& $csc /nologo /target:winexe /optimize+ `
    /out:$outExe `
    /resource:"$uiHtml,HaierHonDesktop.ui.index.html" `
    /r:System.dll `
    /r:System.Core.dll `
    /r:System.Drawing.dll `
    /r:System.Windows.Forms.dll `
    /r:System.Web.Extensions.dll `
    /r:System.Net.Http.dll `
    (Join-Path $root "src\Program.cs") `
    (Join-Path $root "src\ControlCenterForm.cs") `
    (Join-Path $root "src\HonCloudClient.cs") `
    (Join-Path $root "src\LocalWebServer.cs")

Write-Host "Build completed successfully: $outExe" -ForegroundColor Green
