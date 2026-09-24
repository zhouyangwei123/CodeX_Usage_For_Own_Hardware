$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$IconPath = Join-Path $Root 'assets\CodexToolsHost.ico'
if (-not (Test-Path -LiteralPath $IconPath -PathType Leaf)) {
    throw "应用图标资源不存在: $IconPath"
}
$Csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path -LiteralPath $Csc)) {
    $Csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
}
if (-not (Test-Path -LiteralPath $Csc)) {
    throw 'Microsoft .NET Framework 4.8 编译器未找到。'
}

$BuildDirectory = Join-Path $Root 'build'
$ReleaseDirectory = Join-Path $Root 'release'
New-Item -ItemType Directory -Force -Path $BuildDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $ReleaseDirectory | Out-Null
$Sources = @(Get-ChildItem "$Root\src" -Recurse -Filter *.cs | ForEach-Object FullName)
$References = @(
    '/r:System.dll',
    '/r:System.Core.dll',
    '/r:System.Drawing.dll',
    '/r:System.Windows.Forms.dll',
    '/r:System.Management.dll',
    '/r:System.Web.Extensions.dll'
)

$UiaClient = (Get-ChildItem "$env:WINDIR\Microsoft.NET\assembly\GAC_MSIL\UIAutomationClient" -Filter UIAutomationClient.dll -Recurse | Select-Object -First 1).FullName
$UiaTypes = (Get-ChildItem "$env:WINDIR\Microsoft.NET\assembly\GAC_MSIL\UIAutomationTypes" -Filter UIAutomationTypes.dll -Recurse | Select-Object -First 1).FullName
$WindowsBase = (Get-ChildItem "$env:WINDIR\Microsoft.NET\assembly\GAC_MSIL\WindowsBase" -Filter WindowsBase.dll -Recurse | Select-Object -First 1).FullName
if (-not $UiaClient -or -not $UiaTypes -or -not $WindowsBase) {
    throw 'UI Automation / WindowsBase 程序集未找到。'
}
$References += "/r:$UiaClient", "/r:$UiaTypes", "/r:$WindowsBase"

$LhmDirectory = Join-Path $Root 'lib\LibreHardwareMonitor-0.9.6'
if (-not (Test-Path -LiteralPath $LhmDirectory -PathType Container)) {
    throw "LibreHardwareMonitor 依赖目录不存在: $LhmDirectory"
}
$LhmDlls = @(Get-ChildItem -LiteralPath $LhmDirectory -Filter *.dll -File | Sort-Object Name)
if ($LhmDlls.Count -ne 27) {
    throw "LibreHardwareMonitor 依赖应为 27 个 DLL，实际为 $($LhmDlls.Count)"
}
$Resources = @($LhmDlls | ForEach-Object {
    "/resource:$($_.FullName),CodexToolsHost.Dependencies.$($_.Name)"
})
$NoticePath = Join-Path $Root 'THIRD-PARTY-NOTICES.md'
if (Test-Path -LiteralPath $NoticePath -PathType Leaf) {
    $Resources += "/resource:$NoticePath,CodexToolsHost.Notices.ThirdParty.md"
}

$BuildExe = Join-Path $BuildDirectory 'CodexToolsHost.exe'
& $Csc /nologo /target:winexe /platform:anycpu /optimize+ /out:$BuildExe `
    "/win32icon:$IconPath" `
    $References $Resources $Sources
if ($LASTEXITCODE -ne 0) {
    throw '上位机编译失败。'
}

$ResolvedRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\')
$ResolvedRelease = [IO.Path]::GetFullPath($ReleaseDirectory).TrimEnd('\')
if (-not $ResolvedRelease.StartsWith($ResolvedRoot + '\',
        [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($ResolvedRelease) -ne 'release') {
    throw "拒绝清理非预期发布目录: $ResolvedRelease"
}
$ReleaseItem = Get-Item -LiteralPath $ResolvedRelease -Force
if (($ReleaseItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "拒绝清理重解析点发布目录: $ResolvedRelease"
}
$ReleaseEntries = @(Get-ChildItem -LiteralPath $ResolvedRelease -Force)
$UnexpectedEntries = @($ReleaseEntries | Where-Object {
    $_.PSIsContainer -or ($_.Name -ne 'CodexToolsHost.exe' -and
        $_.Name -ne 'CodexToolsHost.exe.new')
})
if ($UnexpectedEntries.Count -gt 0) {
    throw "release 含非发布内容，已停止且未删除: $($UnexpectedEntries.Name -join ', ')"
}

$ReleaseExe = Join-Path $ResolvedRelease 'CodexToolsHost.exe'
$ReleaseTemp = Join-Path $ResolvedRelease 'CodexToolsHost.exe.new'
$ReleaseBackup = Join-Path $BuildDirectory (
    'CodexToolsHost.release-backup-' + [Guid]::NewGuid().ToString('N') + '.exe')
try {
    Copy-Item -LiteralPath $BuildExe -Destination $ReleaseTemp -Force
    $BuildHash = (Get-FileHash -LiteralPath $BuildExe -Algorithm SHA256).Hash
    $TempHash = (Get-FileHash -LiteralPath $ReleaseTemp -Algorithm SHA256).Hash
    if ($BuildHash -ne $TempHash) {
        throw '发布暂存文件 SHA-256 校验失败。'
    }

    if (Test-Path -LiteralPath $ReleaseExe -PathType Leaf) {
        [IO.File]::Replace($ReleaseTemp, $ReleaseExe, $ReleaseBackup)
    }
    else {
        [IO.File]::Move($ReleaseTemp, $ReleaseExe)
    }
}
finally {
    if (Test-Path -LiteralPath $ReleaseTemp -PathType Leaf) {
        Remove-Item -LiteralPath $ReleaseTemp -Force
    }
    if (Test-Path -LiteralPath $ReleaseBackup -PathType Leaf) {
        Remove-Item -LiteralPath $ReleaseBackup -Force -ErrorAction SilentlyContinue
    }
}

$PublishedFiles = @(Get-ChildItem -LiteralPath $ResolvedRelease -File -Force)
if ($PublishedFiles.Count -ne 1 -or $PublishedFiles[0].Name -ne 'CodexToolsHost.exe') {
    throw '发布目录校验失败：应只包含 CodexToolsHost.exe。'
}

Write-Host "构建暂存: $BuildExe"
Write-Host "单文件发布: $ReleaseExe"
