# Simple benchmark script for persistent worker
# Usage: .\bench_worker.ps1 -AudioPath C:\path\to\test.wav -Iterations 50
param(
	[string]$AudioPath,
	[int]$Iterations = 50,
	[int]$DelayMs = 50
)

if (-not (Test-Path $AudioPath)) { Write-Host "Audio file not found: $AudioPath"; exit 1 }

$uri = 'http://127.0.0.1:5000/infer'
$payload = @{ audio_path = $AudioPath } | ConvertTo-Json

$sw = [System.Diagnostics.Stopwatch]::StartNew()
for ($i=0; $i -lt $Iterations; $i++) {
	$t = [System.Diagnostics.Stopwatch]::StartNew()
	try {
		$resp = Invoke-WebRequest -Uri $uri -Method POST -Body $payload -ContentType 'application/json' -UseBasicParsing -TimeoutSec 30
		$elapsed = $t.ElapsedMilliseconds
		Write-Host "Iter $i: ${elapsed}ms -> $($resp.Content)"
	} catch {
		Write-Host "Iter $i: request failed: $_"
	}
	Start-Sleep -Milliseconds $DelayMs
}
$sw.Stop()
Write-Host "Total time: $($sw.ElapsedMilliseconds)ms"