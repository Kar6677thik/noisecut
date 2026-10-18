<#
.SYNOPSIS
  M1 demo: start the local stack, inject the three reference incidents, and check that each one raises its alert.

.DESCRIPTION
  1. docker compose up (unless -NoStart)
  2. wait until the Api and the simulator are ready
  3. raise the order rate (so the cancellation-spike guard of 300 orders/hour is reached sooner)
  4. inject kitchen-meltdown (r-017), zone-cancel-spike (z-02) and rider-shortage (z-04)
  5. poll /api/alerts until each expected alert appears, or the deadline passes
  Exit code 0 = all three detected.

.EXAMPLE
  ./scripts/demo.ps1
  ./scripts/demo.ps1 -NoStart -TimeoutMinutes 15
#>
param(
    [string]$BaseUrl = "http://localhost:8080",
    [string]$AdminKey = $(if ($env:NOISECUT_ADMIN_KEY) { $env:NOISECUT_ADMIN_KEY } else { "local-dev-admin-key" }),
    [double]$DemoRate = 600,
    [double]$NormalRate = 300,
    [int]$TimeoutMinutes = 12,
    [switch]$NoStart
)

$ErrorActionPreference = "Stop"
$headers = @{ "X-Admin-Key" = $AdminKey }
$compose = Join-Path $PSScriptRoot "../deploy/compose/docker-compose.yml"

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

function Post($path, $body) {
    Invoke-RestMethod -Method Post -Uri "$BaseUrl$path" -Headers $headers -ContentType "application/json" -Body ($body | ConvertTo-Json -Compress)
}

if (-not $NoStart) {
    Step "Starting the stack (docker compose up -d --build)"
    docker compose -f $compose up -d --build
    if ($LASTEXITCODE -ne 0) { throw "docker compose failed" }
}

Step "Waiting for the Api and the simulator"
$deadline = (Get-Date).AddMinutes(3)
while ($true) {
    try {
        Invoke-RestMethod "$BaseUrl/readyz" | Out-Null
        $status = Invoke-RestMethod -Uri "$BaseUrl/api/simulator/status" -Headers $headers
        if ($status.running) { break }
    } catch { }
    if ((Get-Date) -gt $deadline) { throw "stack did not become ready within 3 minutes" }
    Start-Sleep -Seconds 2
}
Write-Host "Simulator running at sim time $($status.simNow) (x$($status.timeScale))"

Step "Raising the order rate to $DemoRate orders/sim-minute for the demo"
Post "/api/simulator/config" @{ ordersPerSimMinute = $DemoRate } | Out-Null

Step "Injecting the three reference incidents"
$expectations = @(
    @{ Name = "A  kitchen-meltdown  r-017 -> restaurant-delay-burst"; Scenario = "kitchen-meltdown"; Target = "r-017"; Rule = "restaurant-delay-burst"; Match = { param($a) $a.entityKey -eq "r-017" } },
    @{ Name = "B  zone-cancel-spike z-02  -> zone-cancellation-spike"; Scenario = "zone-cancel-spike"; Target = "z-02"; Rule = "zone-cancellation-spike"; Match = { param($a) $a.entityKey -eq "z-02" } },
    @{ Name = "C  rider-shortage    z-04  -> stuck-pickup"; Scenario = "rider-shortage"; Target = "z-04"; Rule = "stuck-pickup"; Match = { param($a) $a.groupKey -match '^r-0(3[1-9]|40)$' } }
)
foreach ($e in $expectations) {
    $s = Post "/api/scenarios/$($e.Scenario)" @{ target = $e.Target }
    Write-Host ("  started {0,-18} target={1}  sim {2} -> {3}" -f $s.name, $s.target, $s.startsAt, $s.endsAt)
    $e.StartedAt = Get-Date
}

Step "Waiting for alerts (up to $TimeoutMinutes minutes; rule B needs 300 orders in the zone-hour before it may fire)"
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
while ($true) {
    foreach ($e in $expectations | Where-Object { -not $_.Found }) {
        $alerts = Invoke-RestMethod "$BaseUrl/api/alerts?rule=$($e.Rule)&limit=500"
        $hit = @($alerts | Where-Object { & $e.Match $_ }) | Select-Object -First 1
        if ($hit) {
            $e.Found = $hit
            $e.After = [int]((Get-Date) - $e.StartedAt).TotalSeconds
            Write-Host ("  DETECTED {0} after {1}s: {2}" -f $e.Name, $e.After, $hit.title) -ForegroundColor Green
        }
    }
    if (-not ($expectations | Where-Object { -not $_.Found })) { break }
    if ((Get-Date) -gt $deadline) { break }
    Start-Sleep -Seconds 5
}

Post "/api/simulator/config" @{ ordersPerSimMinute = $NormalRate } | Out-Null

Step "Result"
$stats = Invoke-RestMethod "$BaseUrl/api/stats"
foreach ($e in $expectations) {
    if ($e.Found) { Write-Host ("  PASS  {0}  ({1}s)" -f $e.Name, $e.After) -ForegroundColor Green }
    else { Write-Host ("  FAIL  {0}" -f $e.Name) -ForegroundColor Red }
}
Write-Host ("`n  candidates in: {0}   alerts: {1}   notifications sent: {2}   duplicate webhooks: {3}" -f `
    $stats.candidatesIn, $stats.alerts24h, $stats.notificationsSent, $stats.sinkDuplicates)
Write-Host "  Dashboard: $BaseUrl"

if ($expectations | Where-Object { -not $_.Found }) { exit 1 }
exit 0
