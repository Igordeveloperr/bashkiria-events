[CmdletBinding()]
param(
    [switch]$Check,
    [string]$Dotnet = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$outputDirectory = Join-Path $repoRoot ('artifacts/contracts/' + [guid]::NewGuid().ToString('N'))
$contractPath = Join-Path $PSScriptRoot 'parser/openapi.json'
$previousEnvironment = $env:ASPNETCORE_ENVIRONMENT
$previousDotnetEnvironment = $env:DOTNET_ENVIRONMENT
$previousPath = $env:PATH
$dotnetPath = (Get-Command $Dotnet -CommandType Application -ErrorAction Stop).Source

Push-Location $repoRoot
try {
    # Avoid loading local .env files and registering Development-only endpoints.
    $env:ASPNETCORE_ENVIRONMENT = 'ContractExport'
    $env:DOTNET_ENVIRONMENT = 'ContractExport'
    # The build-time generator launches a child `dotnet` process through PATH.
    $env:PATH = (Split-Path -Parent $dotnetPath) + [System.IO.Path]::PathSeparator + $env:PATH
    # Rebuild so MSBuild does not reuse the document cache from a different output directory.
    & $dotnetPath build backend/parser/parser/parser.csproj --no-incremental -p:UseAppHost=false `
        -p:OpenApiGenerateDocumentsOnBuild=true "-p:OpenApiDocumentsDirectory=$outputDirectory"
    if ($LASTEXITCODE -ne 0) { throw 'Could not generate the parser contract.' }

    $generatedPath = Join-Path $outputDirectory 'openapi.json'
    $generated = [System.IO.File]::ReadAllText($generatedPath).Replace("`r`n", "`n").TrimEnd() + "`n"
    if ($Check) {
        if (!(Test-Path -LiteralPath $contractPath)) { throw 'Parser contract is missing. Run contracts/Export-ParserContract.ps1.' }
        $committed = [System.IO.File]::ReadAllText($contractPath).Replace("`r`n", "`n").TrimEnd() + "`n"
        if ($generated -cne $committed) { throw 'Parser contract is out of date. Run contracts/Export-ParserContract.ps1 and review the diff.' }
        Write-Output 'Parser contract matches the application.'
    } else {
        [System.IO.Directory]::CreateDirectory((Split-Path -Parent $contractPath)) | Out-Null
        [System.IO.File]::WriteAllText($contractPath, $generated, [System.Text.UTF8Encoding]::new($false))
        Write-Output "Updated $contractPath"
    }
} finally {
    $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment
    $env:DOTNET_ENVIRONMENT = $previousDotnetEnvironment
    $env:PATH = $previousPath
    Pop-Location
}
