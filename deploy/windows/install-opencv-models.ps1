$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$destination = Join-Path $env:LOCALAPPDATA 'ADFi\models\opencv'
New-Item -ItemType Directory -Force -Path $destination | Out-Null
$revision = '47534e27c9851bb1128ccc0102f1145e27f23f98'
$models = @(
    @{
        Name = 'face_detection_yunet_2023mar.onnx'
        Relative = 'models/face_detection_yunet/face_detection_yunet_2023mar.onnx'
        Sha256 = '8F2383E4DD3CFBB4553EA8718107FC0423210DC964F9F4280604804ED2552FA4'
    },
    @{
        Name = 'face_recognition_sface_2021dec.onnx'
        Relative = 'models/face_recognition_sface/face_recognition_sface_2021dec.onnx'
        Sha256 = '0BA9FBFA01B5270C96627C4EF784DA859931E02F04419C829E83484087C34E79'
    }
)

foreach ($model in $models) {
    $target = Join-Path $destination $model.Name
    if (Test-Path -LiteralPath $target) {
        $existingHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $target).Hash
        if ($existingHash -eq $model.Sha256) {
            continue
        }
    }

    $temporary = "$target.partial"
    try {
        $uri = "https://media.githubusercontent.com/media/opencv/opencv_zoo/$revision/$($model.Relative)"
        Invoke-WebRequest -UseBasicParsing -Uri $uri -OutFile $temporary
        $downloadedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $temporary).Hash
        if ($downloadedHash -ne $model.Sha256) {
            throw "Hash verification failed for $($model.Name)."
        }

        Move-Item -Force -LiteralPath $temporary -Destination $target
    }
    finally {
        if (Test-Path -LiteralPath $temporary) {
            Remove-Item -Force -LiteralPath $temporary
        }
    }
}

Write-Output "Verified OpenCV Zoo revision $revision face models."
