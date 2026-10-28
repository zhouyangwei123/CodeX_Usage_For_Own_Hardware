param([string]$Python='python',[switch]$Live,[int]$SoakSeconds=0)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$out=Join-Path $repo 'artifacts\token-rate-host-20261028'
New-Item -ItemType Directory -Force -Path $out|Out-Null
& $Python "$PSScriptRoot\generate_host_fixtures.py" "$out\fixtures.json"
if($LASTEXITCODE -ne 0){throw 'Fixture generation failed'}
$sources=@(Get-ChildItem -LiteralPath "$repo\host\src" -Recurse -Filter *.cs|ForEach-Object FullName)
$sources+=Join-Path $PSScriptRoot 'HostRunner.cs'
$refs=@('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll','/r:System.Management.dll')
foreach($name in @('UIAutomationClient','UIAutomationTypes','WindowsBase')) {
 $dll=(Get-ChildItem -LiteralPath "$env:WINDIR\Microsoft.NET\assembly\GAC_MSIL\$name" -Filter "$name.dll" -Recurse|Select-Object -First 1).FullName
 $refs+="/r:$dll"
}
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /main:HostTokenSpeedTests "/out:$out\HostTokenSpeedTests.exe" $refs $sources
if($LASTEXITCODE -ne 0){throw 'Native host test compilation failed'}
$arguments=@("$out\fixtures.json",$out)
if($Live){$arguments+='--live'}
& "$out\HostTokenSpeedTests.exe" $arguments
if($LASTEXITCODE -ne 0){throw 'Native host TPS tests failed'}
if($SoakSeconds -gt 0){
 $soak=Join-Path $out 'soak'
 New-Item -ItemType Directory -Force -Path $soak|Out-Null
 Copy-Item -LiteralPath "$repo\host\release\CodexToolsHost.exe" -Destination "$soak\CodexToolsHost.exe" -Force
 & "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize+ /target:exe "/out:$soak\HostSoak.exe" /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll "/r:$soak\CodexToolsHost.exe" "$PSScriptRoot\HostSoak.cs"
 if($LASTEXITCODE -ne 0){throw 'Release soak compilation failed'}
 & "$soak\HostSoak.exe" "$soak\live-release-$($SoakSeconds)s.json" $SoakSeconds
 if($LASTEXITCODE -ne 0){throw 'Release service soak failed'}
}
