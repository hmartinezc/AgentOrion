Set-StrictMode -Version Latest

$host.UI.RawUI.WindowTitle = 'AgentOrion Frontend'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptDir '..'))
$frontendPath = Join-Path $repoRoot 'frontend'

if (-not (Test-Path -LiteralPath $frontendPath)) {
    throw "No se encontro el frontend en: $frontendPath"
}

Set-Location -LiteralPath $frontendPath

if (-not (Test-Path -LiteralPath (Join-Path $frontendPath 'node_modules'))) {
    npm install
}

npm run dev -- --host localhost --port 5173
