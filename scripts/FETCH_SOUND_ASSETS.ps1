param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDir
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$headers = @{
    "User-Agent" = "MaplePipManager-ReleaseBuilder/0.9.7 (https://github.com/hossea123456789-design/MaplePipManager)"
    "Accept" = "audio/mpeg,application/octet-stream;q=0.9,*/*;q=0.8"
}

function Download-WithRetry {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][string]$Destination,
        [int]$MaxAttempts = 6
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            if (Test-Path -LiteralPath $Destination) {
                Remove-Item -LiteralPath $Destination -Force
            }

            Invoke-WebRequest -Uri $Url -OutFile $Destination -Headers $headers -UseBasicParsing -TimeoutSec 45
            return
        }
        catch {
            if (Test-Path -LiteralPath $Destination) {
                Remove-Item -LiteralPath $Destination -Force -ErrorAction SilentlyContinue
            }

            if ($attempt -ge $MaxAttempts) {
                throw
            }

            $delaySeconds = [Math]::Min(30, [Math]::Pow(2, $attempt) + 2)
            Write-Warning "Download attempt $attempt/$MaxAttempts failed. Retrying in $delaySeconds seconds: $Url"
            Start-Sleep -Seconds $delaySeconds
        }
    }
}

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

for ($i = 0; $i -lt $assets.Count; $i++) {
    $asset = $assets[$i]
    $destination = Join-Path $OutputDir $asset.Name
    Write-Host "Downloading $($asset.Name)..."

    Download-WithRetry -Url $asset.Url -Destination $destination

    if (-not (Test-Path -LiteralPath $destination)) {
        throw "Sound asset download failed: $($asset.Name)"
    }

    $length = (Get-Item -LiteralPath $destination).Length
    if ($length -lt $asset.MinBytes) {
        throw "Sound asset is unexpectedly small: $($asset.Name) ($length bytes)"
    }

    Write-Host "  OK $($asset.Name) ($length bytes)"

    # Wikimedia throttles rapid sequential transcode requests from CI shared IPs.
    # Space requests out even after successful downloads.
    if ($i -lt ($assets.Count - 1)) {
        Start-Sleep -Seconds 3
    }
}
