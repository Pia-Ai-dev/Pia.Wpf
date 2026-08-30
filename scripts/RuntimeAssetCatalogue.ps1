#requires -version 7
<#
.SYNOPSIS
  The one list of assets the client downloads lazily: upstream URL, mirror key, cache path.

.DESCRIPTION
  Dot-sourced by Save-RuntimeAssets.ps1 (pre-fetch into a profile) and by
  Publish-RuntimeAssets.ps1 (mirror onto storage.pia-ai.de). Keeping it here is what stops those two
  from drifting apart.

  MirrorKey is the contract with src/Pia.Wpf/Services/Assets/RuntimeAsset.cs — RuntimeAssetCatalogTests
  compares the two lists, because a key that disagrees is a mirror miss nobody sees. Upstream URLs are
  pinned a third time by ModelDownloadUrlTests; change one and change it everywhere.
#>

# The "recongition" misspelling is the real release tag; the corrected spelling 404s.
$script:SherpaAsr = 'https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models'
$script:SherpaSpk = 'https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models'
$script:HuggingFace = 'https://huggingface.co/sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2/resolve/main'

function Get-RuntimeAssetCatalogue {
    <#
    .PARAMETER DestinationRoot
      Where Models\ and Browsers\ live — the %LOCALAPPDATA%\Pia the app reads, or a staging tree.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$DestinationRoot)

    $modelsDir = Join-Path $DestinationRoot 'Models'
    $embeddingsDir = Join-Path $modelsDir 'Embeddings'
    $browsersDir = Join-Path $DestinationRoot 'Browsers'

    # SizeHint is the Content-Length measured 2026-08-29, used only for the up-front total. A download
    # verifies against whatever the server reports now, so a republished asset is not a failure.
    [ordered]@{
        Vad = @(
            @{ Kind = 'File'; Name = 'Silero VAD'
               Url = 'https://github.com/snakers4/silero-vad/raw/v6.2.1/src/silero_vad/data/silero_vad.onnx'
               MirrorKey = 'models/silero_vad.onnx'
               Target = Join-Path $modelsDir 'silero_vad.onnx'; SizeHint = 2327524 }
        )
        Speaker = @(
            @{ Kind = 'File'; Name = 'Speaker embedding (3D-Speaker CAM++)'
               Url = "$script:SherpaSpk/3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx"
               MirrorKey = 'models/3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx'
               Target = Join-Path $modelsDir '3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx'
               SizeHint = 28281164 }
        )
        # The ONNX file is renamed on the way in: EmbeddingService looks for the model under the model's
        # own name, not the "model.onnx" the URL ends in — and a bare "model.onnx" would collide with
        # anything else mirrored beside it.
        Embeddings = @(
            @{ Kind = 'File'; Name = 'Text embedding model'; Url = "$script:HuggingFace/onnx/model.onnx"
               MirrorKey = 'embeddings/paraphrase-multilingual-MiniLM-L12-v2.onnx'
               Target = Join-Path $embeddingsDir 'paraphrase-multilingual-MiniLM-L12-v2.onnx'
               SizeHint = 470301610 }
            @{ Kind = 'File'; Name = 'Text embedding tokenizer'; Url = "$script:HuggingFace/tokenizer.json"
               MirrorKey = 'embeddings/tokenizer.json'
               Target = Join-Path $embeddingsDir 'tokenizer.json'; SizeHint = 9081518 }
            @{ Kind = 'File'; Name = 'Text embedding SentencePiece'; Url = "$script:HuggingFace/sentencepiece.bpe.model"
               MirrorKey = 'embeddings/sentencepiece.bpe.model'
               Target = Join-Path $embeddingsDir 'sentencepiece.bpe.model'; SizeHint = 5069051 }
        )
        WhisperTiny = @(
            @{ Kind = 'Bundle'; Name = 'Whisper Tiny'; Url = "$script:SherpaAsr/sherpa-onnx-whisper-tiny.tar.bz2"
               MirrorKey = 'models/sherpa-onnx-whisper-tiny.tar.bz2'
               Target = Join-Path $modelsDir 'sherpa-whisper-tiny'; SizeHint = 116204861 }
        )
        WhisperBase = @(
            @{ Kind = 'Bundle'; Name = 'Whisper Base'; Url = "$script:SherpaAsr/sherpa-onnx-whisper-base.tar.bz2"
               MirrorKey = 'models/sherpa-onnx-whisper-base.tar.bz2'
               Target = Join-Path $modelsDir 'sherpa-whisper-base'; SizeHint = 207557382 }
        )
        WhisperSmall = @(
            @{ Kind = 'Bundle'; Name = 'Whisper Small'; Url = "$script:SherpaAsr/sherpa-onnx-whisper-small.tar.bz2"
               MirrorKey = 'models/sherpa-onnx-whisper-small.tar.bz2'
               Target = Join-Path $modelsDir 'sherpa-whisper-small'; SizeHint = 639387718 }
        )
        # 1.80 GiB against the storage service's 2 GiB body ceiling (Caddy and Kestrel both). Tight.
        WhisperMedium = @(
            @{ Kind = 'Bundle'; Name = 'Whisper Medium'; Url = "$script:SherpaAsr/sherpa-onnx-whisper-medium.tar.bz2"
               MirrorKey = 'models/sherpa-onnx-whisper-medium.tar.bz2'
               Target = Join-Path $modelsDir 'sherpa-whisper-medium'; SizeHint = 1931372882 }
        )
        # sherpa publishes large-v3-turbo under the bare "turbo" name; the spelled-out form 404s.
        WhisperLarge = @(
            @{ Kind = 'Bundle'; Name = 'Whisper Large v3 Turbo'; Url = "$script:SherpaAsr/sherpa-onnx-whisper-turbo.tar.bz2"
               MirrorKey = 'models/sherpa-onnx-whisper-turbo.tar.bz2'
               Target = Join-Path $modelsDir 'sherpa-whisper-turbo'; SizeHint = 563790207 }
        )
        Parakeet = @(
            @{ Kind = 'Bundle'; Name = 'Parakeet TDT v3'
               Url = "$script:SherpaAsr/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2"
               MirrorKey = 'models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2'
               Target = Join-Path $modelsDir 'sherpa-parakeet-tdt-v3'; SizeHint = 487170055 }
        )
        # No MirrorKey, and it is not an oversight: Playwright picks the browser revision to match the
        # pinned package, and mirroring it means reproducing its CDN layout per revision. The app has a
        # PLAYWRIGHT_DOWNLOAD_HOST hook (ChromiumProvisioner.DownloadHostOverride) if that is ever wanted.
        # The installer also pulls the headless shell, ffmpeg and winldd, so the real transfer is roughly
        # twice the Chromium zip on its own.
        Chromium = @(
            @{ Kind = 'Playwright'; Name = 'Chromium (meeting attendee)'; Target = $browsersDir; SizeHint = 315000000 }
        )
    }
}

function Resolve-RuntimeAssetGroups {
    <#
    .SYNOPSIS
      Normalises -Include/-All into a validated, catalogue-ordered list of group names.

    .DESCRIPTION
      -Include is taken as ArgumentCompletions rather than ValidateSet upstream, because
      `pwsh script.ps1 -Include A,B` hands the whole thing over as one string, which a ValidateSet
      rejects. Splitting and checking here is what makes the documented command lines work.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Catalogue,
        [string[]]$Include,
        [switch]$All
    )

    $known = @($Catalogue.Keys)
    if ($All) { return $known }

    $requested = @($Include | ForEach-Object { $_ -split '[,;]' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $unknown = @($requested | Where-Object { $_ -notin $known })
    if ($unknown.Count -gt 0) {
        throw "Unknown asset group(s): $($unknown -join ', '). Valid: $($known -join ', ')."
    }
    return @($known | Where-Object { $_ -in $requested })
}
