Set-StrictMode -Version Latest

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptDir '..'))
$pwshPath = (Get-Command pwsh).Source

if (-not (Test-Path -LiteralPath $repoRoot)) {
    throw "No se encontro la raiz del repo en: $repoRoot"
}

function Stop-Listener {
    param([int]$Port)

    $pids = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique

    foreach ($processId in $pids) {
        Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
    }
}

function Wait-Port {
    param(
        [int]$Port,
        [int]$TimeoutSeconds = 45
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $listener = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
        if ($listener) {
            return $true
        }

        Start-Sleep -Milliseconds 500
    }

    return $false
}

Stop-Listener -Port 5000
Stop-Listener -Port 5173

$backendScript = Join-Path $scriptDir 'run-backend.ps1'
$frontendScript = Join-Path $scriptDir 'run-frontend.ps1'

Start-Process -FilePath $pwshPath -ArgumentList @('-NoLogo', '-NoExit', '-File', $backendScript) -WorkingDirectory $repoRoot | Out-Null
Start-Process -FilePath $pwshPath -ArgumentList @('-NoLogo', '-NoExit', '-File', $frontendScript) -WorkingDirectory $repoRoot | Out-Null

$backendReady = Wait-Port -Port 5000
$frontendReady = Wait-Port -Port 5173

if ($backendReady) {
    "Backend listo: http://localhost:5000"
}
else {
    "Backend no confirmo escucha en puerto 5000. Revisa la ventana 'AgentOrion Backend'."
}

if ($frontendReady) {
    "Frontend listo: http://localhost:5173"
}
else {
    "Frontend no confirmo escucha en puerto 5173. Revisa la ventana 'AgentOrion Frontend'."
}
