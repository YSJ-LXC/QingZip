$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $projectRoot ('work\ui-test-' + [DateTime]::UtcNow.Ticks)
$sourceRoot = Join-Path $testRoot 'source'
New-Item -ItemType Directory -Path $sourceRoot -Force | Out-Null
Set-Content -LiteralPath (Join-Path $sourceRoot 'notes.txt') -Value 'QingZip glass UI test' -Encoding UTF8
New-Item -ItemType Directory -Path (Join-Path $sourceRoot 'empty-folder') | Out-Null
$resultPath = Join-Path $testRoot 'ui-results.json'
$testArguments = @('--self-test', ('"'+$resultPath+'"'), ('"'+(Join-Path $projectRoot 'samples\example.zip')+'"'), ('"'+$sourceRoot+'"'), ('"'+(Join-Path $testRoot 'output')+'"'))
$testProcess = Start-Process -FilePath (Join-Path $projectRoot 'bin\QingZip.exe') -ArgumentList $testArguments -WindowStyle Hidden -PassThru
if (-not $testProcess.WaitForExit(30000)) { $testProcess.Kill(); throw 'UI test timed out.' }
if ($testProcess.ExitCode -ne 0) {
    if (Test-Path -LiteralPath ($resultPath+'.error.txt')) { Get-Content -LiteralPath ($resultPath+'.error.txt') }
    throw ('UI test failed, exit '+$testProcess.ExitCode)
}
Get-Content -LiteralPath $resultPath
