#  ceobe GUI smoke test（本地交互式桌面，不进 CI）
#
#  用法（Windows PowerShell 5.1，交互式会话）：
#    powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File tools\ui-smoke\Invoke-UiSmoke.ps1
#    powershell.exe ... -File tools\ui-smoke\Invoke-UiSmoke.ps1 -Exe dist\app\Ceobe.exe -OutDir C:\temp\ceobe-smoke
#
#  覆盖的回归点（这些是像素截图抓不到、单测也抓不到的）：
#    1. NavigationView 实例项存在；SelectorBar 七个分区齐全；
#    2. 控制台日志区 BoundingRect 高度 > 100（* 行不再塌陷）；
#    3. 可见的 app-authored Button 都有非空 UIA Name；
#    4. 设置/新建实例对话框能打开，主要内容存在，取消能关闭；
#    5. 浅色/深色两次启动的中心区域平均亮度符合预期（深色 < 0.35，浅色 > 0.65）。
#
#  脚本使用独立的 CEOBE_HOME（临时目录），不会读写 %USERPROFILE%\.ceobe。

param(
    [string]$Exe = (Join-Path (Resolve-Path "$PSScriptRoot\..\..").Path "dist\app\Ceobe.exe"),
    [string]$OutDir = (Join-Path $env:TEMP "ceobe-ui-smoke"),
    [switch]$KeepHome
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class CeobeSmokeNative {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@
[CeobeSmokeNative]::SetProcessDPIAware() | Out-Null

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
function Write-TextNoBom([string]$Path, [string]$Text) {
    [System.IO.File]::WriteAllText($Path, $Text, $utf8NoBom)
}

function Fail([string]$Message) { throw "UI smoke FAILED: $Message" }

function Wait-Until([scriptblock]$Probe, [int]$Seconds = 20) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $result = & $Probe
        if ($result) { return $result }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

function New-AutomationRoot([IntPtr]$Handle) {
    return [System.Windows.Automation.AutomationElement]::FromHandle($Handle)
}

function New-Condition($Property, $Value) {
    return New-Object System.Windows.Automation.PropertyCondition -ArgumentList $Property, $Value
}

function Find-ByAutomationId($Root, [string]$Id) {
    if ($Root -eq $null) { return $null }
    $cond = New-Condition ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) $Id
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-ByName($Root, [string]$Name) {
    if ($Root -eq $null) { return $null }
    $cond = New-Condition ([System.Windows.Automation.AutomationElement]::NameProperty) $Name
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Invoke-Element($Element) {
    if ($Element -eq $null) { Fail "element not found for invoke" }
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Wait-Root([IntPtr]$Handle) {
    $root = Wait-Until { New-AutomationRoot $Handle } 10
    if ($root -eq $null) { Fail "UIA root unavailable" }
    return $root
}

function Wait-Element($Root, [string]$AutomationId, [string]$Name = "") {
    $element = Wait-Until {
        if ($AutomationId) { Find-ByAutomationId $Root $AutomationId }
        else { Find-ByName $Root $Name }
    } 20
    if ($element -eq $null) { Fail "element not found: $AutomationId $Name" }
    return $element
}

function Capture-Window([IntPtr]$Handle, [string]$Path) {
    $rect = New-Object CeobeSmokeNative+RECT
    [CeobeSmokeNative]::GetWindowRect($Handle, [ref]$rect) | Out-Null
    $w = $rect.R - $rect.L
    $h = $rect.B - $rect.T
    $bmp = New-Object System.Drawing.Bitmap -ArgumentList $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [CeobeSmokeNative]::PrintWindow($Handle, $hdc, 2) | Out-Null
    $g.ReleaseHdc($hdc)
    $g.Dispose()
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    return $bmp
}

function Get-CenterLuminance([System.Drawing.Bitmap]$Bitmap) {
    $sum = 0.0
    $count = 0
    $x0 = [int]($Bitmap.Width * 0.35)
    $x1 = [int]($Bitmap.Width * 0.95)
    $y0 = [int]($Bitmap.Height * 0.30)
    $y1 = [int]($Bitmap.Height * 0.85)
    for ($x = $x0; $x -lt $x1; $x += 24) {
        for ($y = $y0; $y -lt $y1; $y += 24) {
            $c = $Bitmap.GetPixel($x, $y)
            $sum += (0.2126 * $c.R + 0.7152 * $c.G + 0.0722 * $c.B) / 255.0
            $count++
        }
    }
    if ($count -eq 0) { return 1.0 }
    return $sum / $count
}

function New-SmokeHome([string]$Theme) {
    $smokeHome = Join-Path $env:TEMP ("ceobe-smoke-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
    $instance = Join-Path $smokeHome "instances\smoke-srv"
    New-Item -ItemType Directory -Path $instance -Force | Out-Null

    Write-TextNoBom (Join-Path $instance "instance.json") @"
{
  "name": "smoke-srv",
  "serverType": "PAPER",
  "gameVersion": "26.3",
  "memoryMb": 2048,
  "jvmArgs": "",
  "port": 25599,
  "launchJar": "server.jar",
  "createdAt": "2026-10-22T00:00:00Z",
  "mods": []
}
"@
    Write-TextNoBom (Join-Path $instance "server.properties") @"
server-port=25599
query.port=25599
motd=smoke
online-mode=true
max-players=20
"@
    Write-TextNoBom (Join-Path $instance "eula.txt") "eula=true"
    Write-TextNoBom (Join-Path $instance ".state") "STOPPED"
    Write-TextNoBom (Join-Path $instance "console.log") ((1..5 | ForEach-Object { "[smoke] line $_" }) -join "`r`n")
    Write-TextNoBom (Join-Path $smokeHome "settings.json") (
        '{"theme":"' + $Theme + '","defaultWhitelistEnabled":false}')
    return $smokeHome
}

function Start-Ceobe([string]$CeobeHomePath) {
    $previous = $env:CEOBE_HOME
    $env:CEOBE_HOME = $CeobeHomePath
    try {
        return Start-Process -FilePath $Exe -PassThru
    }
    finally {
        $env:CEOBE_HOME = $previous
    }
}

function Assert-CommonUi($Root, [string]$Theme) {
    $nav = Wait-Element $Root "InstanceNav"
    $instance = Wait-Until { Find-ByName $nav "smoke-srv" } 20
    if ($instance -eq $null) { Fail "instance 'smoke-srv' not in NavigationView" }

    $sections = Wait-Element $Root "WorkspaceSections"
    $sectionNames = @("控制台", "服务器配置", "游戏配置", "Mod", "整合包", "玩家", "穿透")
    $descendants = $sections.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    $present = @()
    foreach ($e in $descendants) {
        try { if ($e.Current.Name) { $present += $e.Current.Name } } catch {}
    }
    foreach ($name in $sectionNames) {
        if ($present -notcontains $name) { Fail "SelectorBar missing section: $name" }
    }

    $log = Wait-Element $Root "ConsoleLogList"
    $height = $log.Current.BoundingRectangle.Height
    if ($height -le 100) { Fail "console log area collapsed: height=$height" }

    # app-authored Button（排除滚动条/数值框的 RepeatButton）必须有可访问名称
    $buttonCondition = New-Condition ([System.Windows.Automation.AutomationElement]::ControlTypeProperty) ([System.Windows.Automation.ControlType]::Button)
    $buttons = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
    foreach ($b in $buttons) {
        try {
            $c = $b.Current
            if ($c.IsOffscreen) { continue }
            if ($c.ClassName -match "ScrollBar|RepeatButton|SpinButton") { continue }
            if ([string]::IsNullOrWhiteSpace($c.Name)) {
                Fail "visible Button without UIA Name (AutomationId='$($c.AutomationId)')"
            }
        } catch {}
    }

    # 设置对话框：主题三态存在且取消可关闭
    Invoke-Element (Wait-Element $Root "SettingsButton")
    $themeCombo = Wait-Element $Root "" "主题"
    $selection = $themeCombo.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern)
    $selected = $selection.Current.GetSelection()
    if ($selected.Count -eq 0) { Fail "theme combo has no selection" }
    $selectedName = $selected[0].Current.Name
    if (@("跟随系统", "浅色", "深色") -notcontains $selectedName) {
        Fail "unexpected theme selection: $selectedName"
    }
    $close = Wait-Until { Find-ByName $Root "取消" } 10
    if ($close -eq $null) { Fail "settings dialog close button not found" }
    Invoke-Element $close
    Start-Sleep -Milliseconds 600

    # 新建实例对话框：单页字段 + 创建按钮，取消关闭
    Invoke-Element (Wait-Element $Root "NewInstanceButton")
    $nameBox = Wait-Until { Find-ByName $Root "实例名" } 10
    if ($nameBox -eq $null) { Fail "create dialog did not open (实例名 not found)" }
    $create = Wait-Until { Find-ByName $Root "创建" } 10
    if ($create -eq $null) { Fail "create dialog primary button missing" }
    $cancel = Wait-Until { Find-ByName $Root "取消" } 10
    if ($cancel -eq $null) { Fail "create dialog cancel button missing" }
    Invoke-Element $cancel
    Start-Sleep -Milliseconds 400
}

if (Get-Process Ceobe -ErrorAction SilentlyContinue) {
    Fail "Ceobe.exe is already running; close it before the smoke test"
}

$results = @()
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

foreach ($theme in @("light", "dark")) {
    $smokeHome = New-SmokeHome $theme
    $proc = $null
    try {
        $proc = Start-Ceobe $smokeHome
        $handle = Wait-Until { $proc.Refresh(); if ($proc.MainWindowHandle -ne 0) { $proc.MainWindowHandle } } 30
        if ($handle -eq $null) { Fail "window did not appear for $theme" }
        Start-Sleep -Milliseconds 1800

        $root = Wait-Root $handle
        Assert-CommonUi $root $theme

        $shot = Join-Path $OutDir "$theme.png"
        $bmp = Capture-Window $handle $shot
        $lum = Get-CenterLuminance $bmp
        $bmp.Dispose()

        if ($theme -eq "dark" -and $lum -gt 0.35) { Fail "dark theme too bright: luminance=$lum" }
        if ($theme -eq "light" -and $lum -lt 0.65) { Fail "light theme too dark: luminance=$lum" }
        $results += "$theme OK (center luminance $([math]::Round($lum, 3))) -> $shot"
    }
    finally {
        if ($proc -ne $null -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
        if (-not $KeepHome) { Remove-Item -Recurse -Force $smokeHome -ErrorAction SilentlyContinue }
    }
}

$results | ForEach-Object { Write-Output $_ }
Write-Output "UI smoke PASSED"
