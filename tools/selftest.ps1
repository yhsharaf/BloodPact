# Blood Pact self-test: runs two copies of the game on this PC (host + guest over UDP 127.0.0.1), plays the boss
# scenarios automatically and prints the PASS/FAIL results. Nothing is saved while testing.
#   powershell -ExecutionPolicy Bypass -File tools\selftest.ps1 [-Scenario all] [-TimeoutMinutes 15]
param(
    [string]$Scenario = "all",
    [int]$TimeoutMinutes = 8,
    [string]$Game = "C:\Program Files (x86)\Steam\steamapps\common\Castlevania Belmont's Curse"
)

$exe = Join-Path $Game "Castlevania Belmont's Curse.exe"
$testDir = Join-Path $Game "BepInEx\BloodPact-test"
if (Get-Process -Name "Castlevania Belmont's Curse" -ErrorAction SilentlyContinue) {
    Write-Host "Close the game first."; exit 1
}
New-Item -ItemType Directory -Force $testDir | Out-Null
$scenarios = if ($Scenario -eq "all") { @("partner-down-win", "owner-down-win", "both-up-win") } else { @($Scenario) }
$all = @()

# Each scenario gets two fresh copies of the game, so nothing one leaves open (a reward screen...) affects the next.
foreach ($sc in $scenarios) {
    Get-ChildItem $testDir -File | Remove-Item -Force
    $common = "--rosenet-transport udp --rosenet-host 127.0.0.1 --rosenet-test $sc"
    Start-Process -FilePath $exe -WorkingDirectory $Game -ArgumentList "--rosenet-role host $common"
    Start-Sleep -Seconds 8
    Start-Process -FilePath $exe -WorkingDirectory $Game -ArgumentList "--rosenet-role guest $common"

    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    while ((Get-Date) -lt $deadline) {
        if ((Test-Path "$testDir\host.finished") -and (Test-Path "$testDir\guest.finished")) { break }
        if (-not (Get-Process -Name "Castlevania Belmont's Curse" -ErrorAction SilentlyContinue)) { Write-Host "Both games closed."; break }
        Start-Sleep -Seconds 5
    }
    foreach ($role in "host", "guest") {
        $f = "$testDir\$role-results.txt"
        $lines = if (Test-Path $f) { Get-Content $f } else { @("(no results)") }
        $all += "===== $sc / $role ====="
        $all += $lines
        Copy-Item $f "$testDir\..\BloodPact-test-$sc-$role.txt" -Force -ErrorAction SilentlyContinue
    }
    Get-Process -Name "Castlevania Belmont's Curse" -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 5
}
$all | ForEach-Object { Write-Host $_ }
$summary = Join-Path $Game "BepInEx\BloodPact-test-summary.txt"
$all | Set-Content $summary
$fails = ($all | Select-String -Pattern " FAIL ").Count
$passes = ($all | Select-String -Pattern " PASS ").Count
Write-Host "TOTAL: $passes passed, $fails failed. Summary: $summary"
