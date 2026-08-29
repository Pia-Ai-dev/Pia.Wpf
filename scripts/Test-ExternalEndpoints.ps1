#requires -version 7
<#
.SYNOPSIS
  Probes every external URL Pia.Wpf dials at runtime and reports what each one answers. Read-only.

.DESCRIPTION
  The client fetches models, browsers and its own update feed lazily, on first use, behind a
  catch-and-log. A URL that has rotted therefore surfaces as "nothing happened" rather than as an
  error, which is exactly how the Whisper Large 404 survived. This script is the thing that asks.

  Deliberately NOT a test. `dotnet test` has to stay offline and deterministic, so this lives in
  scripts/ and is run by hand — before a release, or when someone changes a download URL.

  The list mirrors docs/external_endpoints/2026-08-29-external-endpoint-inventory.md. It covers only
  what the repo hard-codes: a user-configured provider endpoint, an MCP server URL and the asset
  hosts a Teams meeting pulls in are unbounded and cannot be enumerated here.

.PARAMETER TimeoutSec
  Per-request timeout. The default is generous because a few of these are multi-gigabyte assets
  behind a redirect chain — only headers are read, but the redirects still cost.

.EXAMPLE
  pwsh scripts/Test-ExternalEndpoints.ps1
#>
[CmdletBinding()]
param(
    [int]$TimeoutSec = 45
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$sherpaAsr = 'https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models'
$sherpaSpk = 'https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models'
$hf = 'https://huggingface.co/sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2/resolve/main'

# Ok = every status code that means "this endpoint is healthy". 401 is healthy for a provider API:
# it proves the host answers, and we deliberately send no key.
$endpoints = @(
    @{ Group = 'Pia'; Name = 'Update feed';        Url = 'https://storage.pia-ai.de/f/wpf/releases.win.json'; Ok = @(200)
       Note = 'Known open: the storage service is not deployed yet - see docs/update_feed/2026-08-29-storage-feed-server-handoff.md' }
    @{ Group = 'Pia'; Name = 'Asset mirror';       Url = 'https://storage.pia-ai.de/f/assets/models/silero_vad.onnx'; Ok = @(200)
       Note = 'Known open: same host as the update feed, and nothing has been published to it yet - see docs/external_endpoints/2026-08-29-external-endpoint-inventory.md section 10' }
    @{ Group = 'Pia'; Name = 'Update feed (GitHub fallback)'; Url = 'https://api.github.com/repos/Pia-Ai-dev/Pia.Wpf/releases/latest'; Ok = @(200) }
    @{ Group = 'Pia'; Name = 'Cloud health';       Url = 'https://cloud.pia-ai.de/health'; Ok = @(200) }
    @{ Group = 'Pia'; Name = 'Cloud register';     Url = 'https://cloud.pia-ai.de/auth/register.html'; Ok = @(200) }
    @{ Group = 'Pia'; Name = 'Cloud forgot pw';    Url = 'https://cloud.pia-ai.de/auth/forgot-password.html'; Ok = @(200) }
    @{ Group = 'Pia'; Name = 'Website';            Url = 'https://pia-ai.de'; Ok = @(200) }
    @{ Group = 'Pia'; Name = 'Imprint';            Url = 'https://pia-ai.de/impressum.html'; Ok = @(200) }
    @{ Group = 'Pia'; Name = 'Privacy policy';     Url = 'https://pia-ai.de/datenschutz.html'; Ok = @(200) }
    @{ Group = 'Pia'; Name = 'Documentation';      Url = 'https://docs.pia-ai.de'; Ok = @(200) }

    @{ Group = 'Models'; Name = 'Whisper tiny';    Url = "$sherpaAsr/sherpa-onnx-whisper-tiny.tar.bz2"; Ok = @(200) }
    @{ Group = 'Models'; Name = 'Whisper base';    Url = "$sherpaAsr/sherpa-onnx-whisper-base.tar.bz2"; Ok = @(200) }
    @{ Group = 'Models'; Name = 'Whisper small';   Url = "$sherpaAsr/sherpa-onnx-whisper-small.tar.bz2"; Ok = @(200) }
    @{ Group = 'Models'; Name = 'Whisper medium';  Url = "$sherpaAsr/sherpa-onnx-whisper-medium.tar.bz2"; Ok = @(200) }
    @{ Group = 'Models'; Name = 'Whisper large';   Url = "$sherpaAsr/sherpa-onnx-whisper-turbo.tar.bz2"; Ok = @(200)
       Note = 'large-v3-turbo ships as the bare "turbo" asset' }
    @{ Group = 'Models'; Name = 'Parakeet TDT v3'; Url = "$sherpaAsr/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8.tar.bz2"; Ok = @(200) }
    @{ Group = 'Models'; Name = 'Speaker embedding'; Url = "$sherpaSpk/3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx"; Ok = @(200)
       Note = 'The "recongition" misspelling is the real release tag' }
    @{ Group = 'Models'; Name = 'Silero VAD';      Url = 'https://github.com/snakers4/silero-vad/raw/master/src/silero_vad/data/silero_vad.onnx'; Ok = @(200) }
    @{ Group = 'Models'; Name = 'Embedding model'; Url = "$hf/onnx/model.onnx"; Ok = @(200) }
    @{ Group = 'Models'; Name = 'Embedding tokenizer'; Url = "$hf/tokenizer.json"; Ok = @(200) }
    @{ Group = 'Models'; Name = 'Embedding sentencepiece'; Url = "$hf/sentencepiece.bpe.model"; Ok = @(200) }

    # Host-level only: PiperSharp owns the exact URLs, so these probe the surfaces it is known to use
    # rather than the literal request it makes.
    @{ Group = 'TTS'; Name = 'Piper voice catalogue'; Url = 'https://huggingface.co/rhasspy/piper-voices/raw/main/voices.json'; Ok = @(200) }
    @{ Group = 'TTS'; Name = 'Piper voice file'; Url = 'https://huggingface.co/rhasspy/piper-voices/resolve/main/de/de_DE/thorsten/medium/de_DE-thorsten-medium.onnx'; Ok = @(200)
       Note = 'One curated voice, standing in for all nine' }
    @{ Group = 'TTS'; Name = 'Piper engine release'; Url = 'https://github.com/rhasspy/piper/releases/latest'; Ok = @(200) }

    @{ Group = 'Providers'; Name = 'OpenRouter models'; Url = 'https://openrouter.ai/api/v1/models'; Ok = @(200)
       Note = 'Keyless on purpose - the context-window lookup runs before a key is entered' }
    @{ Group = 'Providers'; Name = 'OpenAI';       Url = 'https://api.openai.com/v1/models'; Ok = @(200, 401) }
    @{ Group = 'Providers'; Name = 'Mistral';      Url = 'https://api.mistral.ai/v1/models'; Ok = @(200, 401) }
)

# A HEAD is enough and avoids pulling gigabytes, but some hosts answer 405 to it; fall back to a GET
# and stop as soon as the headers are in.
function Invoke-Probe {
    param([string]$Url, [int]$TimeoutSec)

    $common = @{
        Uri                  = $Url
        MaximumRedirection   = 10
        TimeoutSec           = $TimeoutSec
        SkipHttpErrorCheck   = $true
        ErrorAction          = 'Stop'
    }
    $resp = Invoke-WebRequest @common -Method Head
    if ($resp.StatusCode -eq 405) {
        $resp = Invoke-WebRequest @common -Method Get
    }
    return $resp
}

$results = foreach ($e in $endpoints) {
    $note = if ($e.ContainsKey('Note')) { $e.Note } else { '' }
    try {
        $resp = Invoke-Probe -Url $e.Url -TimeoutSec $TimeoutSec
        $len = $resp.Headers['Content-Length'] | Select-Object -First 1
        [pscustomobject]@{
            Group  = $e.Group
            Name   = $e.Name
            Status = [int]$resp.StatusCode
            Bytes  = if ($len) { [long]$len } else { $null }
            Ok     = ([int]$resp.StatusCode) -in $e.Ok
            Detail = $note
        }
    }
    catch {
        [pscustomobject]@{
            Group  = $e.Group
            Name   = $e.Name
            Status = 0
            Bytes  = $null
            Ok     = $false
            Detail = (@($note, ($_.Exception.Message -replace '\s+', ' ')) | Where-Object { $_ }) -join ' | '
        }
    }
}

$results | Format-Table Group, Name, Status, Bytes, Ok, Detail -AutoSize -Wrap

$failed = @($results | Where-Object { -not $_.Ok })
if ($failed.Count -eq 0) {
    Write-Host "All $($results.Count) endpoints healthy."
    exit 0
}

Write-Host ''
Write-Host "$($failed.Count) of $($results.Count) endpoints unhealthy:"
foreach ($f in $failed) {
    Write-Host ("  {0} / {1} -> {2}" -f $f.Group, $f.Name, ($f.Status -eq 0 ? 'no response' : $f.Status))
}
exit 1
