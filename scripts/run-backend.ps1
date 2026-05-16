Set-StrictMode -Version Latest

$host.UI.RawUI.WindowTitle = 'AgentOrion Backend'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptDir '..'))
$projectPath = Join-Path $repoRoot 'backend/src/AgentOrion.Api/AgentOrion.Api.csproj'

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "No se encontro el proyecto backend en: $projectPath"
}

Set-Location -LiteralPath $repoRoot
dotnet run --project $projectPath --launch-profile AgentOrion.Api
