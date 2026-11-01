param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDir
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$assets = @(
    @{
        Name = "doorbell.mp3"
        Url = "https://upload.wikimedia.org/wikipedia/commons/transcoded/9/90/Doorbell-cheap-dingdong.ogg/Doorbell-cheap-dingdong.ogg.mp3"
        MinBytes = 10000
    },
    @{
        Name = "dog-bark.mp3"
        Url = "https://upload.wikimedia.org/wikipedia/commons/transcoded/4/4c/George_vuf_1996.ogg/George_vuf_1996.ogg.mp3"
        MinBytes = 2000
    },
    @{
        Name = "rooster.mp3"
        Url = "https://upload.wikimedia.org/wikipedia/commons/transcoded/0/0d/Medium_rooster_crowing.ogg/Medium_rooster_crowing.ogg.mp3"
        MinBytes = 20000
    }
)

foreach ($asset in $assets) {
    $destination = Join-Path $OutputDir $asset.Name
    Write-Host "Downloading $($asset.Name)..."
    Invoke-WebRequest -Uri $asset.Url -OutFile $destination -UseBasicParsing

    if (-not (Test-Path -LiteralPath $destination)) {
        throw "Sound asset download failed: $($asset.Name)"
    }

    $length = (Get-Item -LiteralPath $destination).Length
    if ($length -lt $asset.MinBytes) {
        throw "Sound asset is unexpectedly small: $($asset.Name) ($length bytes)"
    }

    Write-Host "  OK $($asset.Name) ($length bytes)"
}
