param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'release'))
$ErrorActionPreference = 'Stop'
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $taskCompiler)) { throw 'Windows .NET Framework 4.8 compiler is required.' }
if (![IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory = Join-Path $PSScriptRoot $OutputDirectory }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$taskSource = Join-Path $PSScriptRoot 'src'
$taskTests = Join-Path $PSScriptRoot 'tests'
$taskTestOutput = Join-Path $PSScriptRoot 'bin\tests'
New-Item -ItemType Directory -Force -Path $taskTestOutput | Out-Null
$taskCommon = @('/nologo','/langversion:5','/optimize+','/r:System.Web.Extensions.dll','/r:System.Security.dll')
$taskAllSources = @(Get-ChildItem -LiteralPath $taskSource -Filter '*.cs' | ForEach-Object { $_.FullName })
& $taskCompiler @taskCommon /target:winexe /platform:anycpu /r:System.Windows.Forms.dll /r:System.Drawing.dll "/win32manifest:$(Join-Path $PSScriptRoot 'app.manifest')" "/win32icon:$(Join-Path $PSScriptRoot 'assets\FudanLink.ico')" "/out:$(Join-Path $OutputDirectory 'FudanLink.exe')" @taskAllSources
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'app.config') -Destination (Join-Path $OutputDirectory 'FudanLink.exe.config') -Force
foreach ($taskSuite in @('KeeperTests','PortalReplyTests','ProtocolTests','TransportTests','RequestPolicyTests','UiTests')) {
    $taskTestExe = Join-Path $taskTestOutput ($taskSuite + '.exe')
    & $taskCompiler @taskCommon /define:OFFLINE_TESTS /r:System.Windows.Forms.dll /r:System.Drawing.dll "/win32manifest:$(Join-Path $PSScriptRoot 'app.manifest')" "/main:$taskSuite" "/out:$taskTestExe" @taskAllSources (Join-Path $taskTests ($taskSuite + '.cs'))
    if ($LASTEXITCODE -ne 0) { throw "Test build failed: $taskSuite" }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'app.config') -Destination ($taskTestExe+'.config') -Force
    & $taskTestExe
    if ($LASTEXITCODE -ne 0) { throw "Test failed: $taskSuite" }
}
$taskInstaller=Join-Path $PSScriptRoot 'installer'
& $taskCompiler /nologo /target:winexe /platform:x64 /optimize+ /r:System.Windows.Forms.dll "/win32manifest:$(Join-Path $taskInstaller 'setup.manifest')" "/win32icon:$(Join-Path $PSScriptRoot 'assets\FudanLink.ico')" "/resource:$(Join-Path $OutputDirectory 'FudanLink.exe'),FudanLink.exe" "/resource:$(Join-Path $OutputDirectory 'FudanLink.exe.config'),FudanLink.exe.config" "/out:$(Join-Path $OutputDirectory 'FudanLinkSetup.exe')" (Join-Path $taskInstaller 'Setup.cs')
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
Write-Output 'Build, installer and all test suites passed.'
