Set-StrictMode -Version Latest

function Stop-Listener {
    param([int]$Port)

    $pids = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique

    foreach ($processId in $pids) {
        Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
    }
}

function Stop-Window {
    param([string]$Title)

    $processes = Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowTitle -eq $Title }

    foreach ($process in $processes) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
}

Stop-Listener -Port 5000
Stop-Listener -Port 5173

Stop-Window -Title 'AgentOrion Backend'
Stop-Window -Title 'AgentOrion Frontend'

"AgentOrion local detenido."
