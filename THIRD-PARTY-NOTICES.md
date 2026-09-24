# Third-party notices

Pia bundles or downloads the components below. Where a licence requires attribution, this file is it.

## Text-to-speech voices

Pia downloads Piper voices on demand from the
[sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) TTS model releases. Each voice inherits the
licence of the speech corpus it was trained on:

| Voice | Corpus | Licence |
|-------|--------|---------|
| `en_GB-alba-medium` | [Alba (University of Edinburgh)](https://datashare.ed.ac.uk/handle/10283/3270) | [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) |
| `fr_FR-siwis-medium` | [SIWIS (University of Edinburgh)](https://datashare.is.ed.ac.uk/handle/10283/2353) | [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) |
| `en_US-amy-medium` | [Mycroft mimic3-voices](https://github.com/MycroftAI/mimic3-voices) | [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/) |
| `fr_FR-upmc-medium` | [UPMC Pierre](https://github.com/marytts/upmc-pierre-data) | [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/) |
| `de_DE-thorsten-medium` | [Thorsten-Voice](https://github.com/thorstenMueller/Thorsten-Voice) | CC0 (public domain) |
| `de_DE-eva_k-x_low`, `de_DE-ramona-low` | M-AILABS Speech Dataset | BSD-style, commercial use permitted — reproduced below |

Voice models are not redistributed with the installer. They are fetched from the upstream release
when a user selects a voice, and Pia neither modifies nor redistributes the models themselves.

### M-AILABS Speech Dataset licence

Reproduced in full because the original page (`caito.de`) is offline; the text below is from the
Internet Archive capture of `https://www.caito.de/2019/01/03/the-m-ailabs-speech-dataset/`.

> Copyright (c) 2017-2019 by the original creators @ M-AILABS with the following license:
>
> Redistribution and use in any form, including any commercial use, with or without modification are
> permitted provided that the following conditions are met:
>
> Redistributions of source data must retain the above copyright notice, this list of conditions and
> the following disclaimer.
>
> Neither the name of the copyright holder nor the names of its contributors may be used to endorse
> or promote products derived from this downloaded data, source-code or binary-code without specific
> prior written permission.
>
> THIS DATA IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED
> WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
> FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE
> LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
> (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
> PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
> CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF
> THE USE OF THIS SOFTWARE and/or DATA, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

## Speech-to-text and speaker models

Whisper, Parakeet and CAM++ models are downloaded on demand from their upstream releases and are not
redistributed with the installer.

## Browser runtime

The Teams meeting attendee provisions a Chromium build through
[Microsoft Playwright](https://github.com/microsoft/playwright-dotnet), pinned to the Playwright
package version, stored outside the application directory and removed on uninstall.

## NuGet packages

Package licences are declared in each package and resolved at restore time; see the
`PackageReference` entries in `src/Pia.Wpf/Pia.Wpf.csproj` and `src/Pia.Shared/Pia.Shared.csproj`.

---

Voice licences were established from each voice's `MODEL_CARD` in
`huggingface.co/rhasspy/piper-voices` and the corpus sources those name, checked 2026-09-20. A voice
whose corpus forbids commercial use is not offered: see `Retired` in
`src/Pia.Wpf/Services/Tts/TtsVoiceCatalog.cs`.
