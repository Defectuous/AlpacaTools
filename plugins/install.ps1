# Installs the Alpaca Dashboard key plugin into every supported controller app found on this PC:
#   SOOMFON Stream Controller (MiraBox StreamDock)  ->  %APPDATA%\HotSpot\StreamDock\plugins
#   Elgato Stream Deck                              ->  %APPDATA%\Elgato\StreamDeck\Plugins
# Each copy is shared/ plus that app's manifest. Restart the app afterwards so it loads the new version.
param([ValidateSet('all', 'soomfon', 'elgato')] [string]$Target = 'all')

$name = 'com.alpacatools.dashboard.sdPlugin'
$targets = @(
    @{ Key = 'soomfon'; App = 'SOOMFON Stream Controller'; Dir = Join-Path $env:APPDATA 'HotSpot\StreamDock\plugins' },
    @{ Key = 'elgato'; App = 'Elgato Stream Deck'; Dir = Join-Path $env:APPDATA 'Elgato\StreamDeck\Plugins' }
)

$installed = 0
foreach ($t in $targets | Where-Object { $Target -eq 'all' -or $_.Key -eq $Target }) {
    if (-not (Test-Path $t.Dir)) {
        Write-Host "$($t.App): not found ($($t.Dir)), skipped."
        continue
    }
    $dest = Join-Path $t.Dir $name
    if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
    Copy-Item -Recurse (Join-Path $PSScriptRoot 'shared') $dest
    Copy-Item (Join-Path $PSScriptRoot "$($t.Key)\manifest.json") $dest
    Set-Content -Path (Join-Path $dest 'target.js') -Value "const CLIENT_NAME = '$($t.App)';" -Encoding utf8
    Write-Host "$($t.App): installed to $dest. Restart it to load the plugin."
    $installed++
}
if ($installed -eq 0) { Write-Error 'No supported controller app was found.'; exit 1 }
