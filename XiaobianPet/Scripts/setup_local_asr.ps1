$ErrorActionPreference = 'Stop'

function Find-Python {
    $localPrograms = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'Programs\Python'
    if (Test-Path -LiteralPath $localPrograms -PathType Container) {
        $installed = Get-ChildItem -LiteralPath $localPrograms -Directory -Filter 'Python*' |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName 'python.exe' } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
            Select-Object -First 1
        if ($installed) {
            return (Resolve-Path -LiteralPath $installed).Path
        }
    }

    $launcher = Get-Command py.exe -ErrorAction SilentlyContinue
    if ($launcher) {
        $path = (& $launcher.Source -3 -c 'import sys; print(sys.executable)').Trim()
        if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $path -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $path).Path
        }
    }

    $python = Get-Command python.exe -ErrorAction SilentlyContinue
    if ($python -and (Test-Path -LiteralPath $python.Source -PathType Leaf)) {
        return (Resolve-Path -LiteralPath $python.Source).Path
    }

    throw 'Python 3 was not found. Install 64-bit Python 3.11 or newer first.'
}

$pythonPath = Find-Python
$localData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$modelRoot = Join-Path $localData 'XiaobianPet\Models'
$sourceModel = Join-Path $modelRoot 'whisper-large-v3-turbo'
$convertedModel = Join-Path $modelRoot 'faster-whisper-large-v3-turbo'
$convertedWeights = Join-Path $convertedModel 'model.bin'

Write-Host "Python: $pythonPath"
New-Item -ItemType Directory -Path $modelRoot -Force | Out-Null

Write-Host 'Checking local ASR packages...'
& $pythonPath -m pip install --disable-pip-version-check --no-warn-script-location `
    'faster-whisper==1.2.1' `
    'transformers==5.3.0' `
    'huggingface-hub==1.7.1'
if ($LASTEXITCODE -ne 0) {
    throw 'Failed to install faster-whisper packages.'
}

$torchCheck = & $pythonPath -c 'import torch; print(torch.__version__)' 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Installing the PyTorch CUDA 12.4 runtime...'
    & $pythonPath -m pip install --disable-pip-version-check --no-warn-script-location `
        'torch==2.6.0' --index-url 'https://download.pytorch.org/whl/cu124'
    if ($LASTEXITCODE -ne 0) {
        throw 'Failed to install the PyTorch CUDA runtime.'
    }
} else {
    Write-Host "PyTorch: $torchCheck"
}

if (Test-Path -LiteralPath $convertedWeights -PathType Leaf) {
    Write-Host 'Whisper large-v3-turbo is already ready.'
    exit 0
}

Write-Host 'Downloading OpenAI Whisper large-v3-turbo (about 1.6 GB)...'
$downloadCode = @'
from huggingface_hub import snapshot_download
import sys
snapshot_download(
    repo_id="openai/whisper-large-v3-turbo",
    local_dir=sys.argv[1],
)
'@
& $pythonPath -c $downloadCode $sourceModel
if ($LASTEXITCODE -ne 0) {
    throw 'Failed to download the Whisper model. Check the network and retry.'
}

$pythonDirectory = Split-Path -Parent $pythonPath
$converter = Join-Path $pythonDirectory 'Scripts\ct2-transformers-converter.exe'
if (-not (Test-Path -LiteralPath $converter -PathType Leaf)) {
    throw "CTranslate2 converter was not found: $converter"
}

Write-Host 'Converting the model for the local GPU worker...'
& $converter `
    --model $sourceModel `
    --output_dir $convertedModel `
    --copy_files tokenizer.json preprocessor_config.json `
    --quantization float16
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $convertedWeights -PathType Leaf)) {
    throw 'Whisper model conversion failed.'
}

Write-Host "Model ready: $convertedModel"
exit 0
