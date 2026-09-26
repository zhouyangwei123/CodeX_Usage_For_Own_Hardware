param([switch]$Baseline,[switch]$Lifecycle,[string]$RunName='hud-render')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$output=Join-Path $repo ('artifacts\'+$RunName)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$refs=@('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll')
if($Baseline){
    $output=Join-Path $repo 'artifacts\hud-before'
    & $csc /nologo /target:exe "/out:$output\baseline.exe" $refs "/r:$output\CodexToolsHost.exe" (Join-Path $PSScriptRoot 'Baseline.cs')
    if($LASTEXITCODE -ne 0){throw 'Baseline compile failed'}
    & "$output\baseline.exe" $output
    exit $LASTEXITCODE
}
$src=Join-Path $repo 'host\src'
if($Lifecycle){
    $sources=@('Actions','Core','Model','Monitor','Mijia','Protocol','Quota','Usage') | ForEach-Object {Get-ChildItem -LiteralPath (Join-Path $src $_) -Filter '*.cs' -Recurse | ForEach-Object FullName}
    $sources+=@('UI\QuotaHudForm.cs','UI\HudDesktopLayer.cs','UI\QuotaHudActivity.cs','UI\QuotaHudRenderer.cs','UI\QuotaHudActivityRenderer.cs','UI\QuotaHudPresentation.cs','Tests\FakeSources.cs') | ForEach-Object {Join-Path $src $_}
    if(Test-Path -LiteralPath "$src\UI\LayeredWindowSurface.cs"){$sources+="$src\UI\LayeredWindowSurface.cs"}
    $sources+=Join-Path $PSScriptRoot 'Lifecycle.cs'
    $refs+='/r:System.Management.dll','/r:System.Web.Extensions.dll'
    foreach($name in @('UIAutomationClient','UIAutomationTypes','WindowsBase')){
        $dll=Get-ChildItem -LiteralPath "$env:WINDIR\Microsoft.NET\assembly\GAC_MSIL\$name" -Filter "$name.dll" -Recurse | Select-Object -First 1
        $refs+="/r:$($dll.FullName)"
    }
    & $csc /nologo /target:exe /optimize+ "/out:$output\hud-lifecycle.exe" $refs $sources
    if($LASTEXITCODE -ne 0){throw 'HUD lifecycle compile failed'}
    & "$output\hud-lifecycle.exe" $output
    exit $LASTEXITCODE
}
$sources=@('UI\QuotaHudRenderer.cs','UI\QuotaHudPresentation.cs','Model\QuotaSnapshot.cs','Model\OpenCodeGoQuotaSnapshot.cs') | ForEach-Object {Join-Path $src $_}
$surface=Join-Path $src 'UI\LayeredWindowSurface.cs'
if(Test-Path -LiteralPath $surface){$sources+=$surface}
$sources+=Join-Path $PSScriptRoot 'Runner.cs'
& $csc /nologo /target:exe /optimize+ "/out:$output\hud-tests.exe" $refs $sources
if($LASTEXITCODE -ne 0){throw 'HUD focused compile failed'}
& "$output\hud-tests.exe" $output
exit $LASTEXITCODE
