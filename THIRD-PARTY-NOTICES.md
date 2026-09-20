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
| `fr_FR-upmc-medium` | [UPMC Pierre](https://github.com/marytts/upmc-pierre-data) | [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/) |
| `de_DE-thorsten-medium` | [Thorsten-Voice](https://github.com/thorstenMueller/Thorsten-Voice) | CC0 (public domain) |
| `en_US-amy-medium` | [Mycroft mimic3-voices](https://github.com/MycroftAI/mimic3-voices) | See corpus |
| `de_DE-eva_k-x_low`, `de_DE-ramona-low` | [M-AILABS Speech Dataset](https://www.caito.de/2019/01/03/the-m-ailabs-speech-dataset/) | See corpus |

Voice models are not redistributed with the installer; they are fetched from the upstream release
when a user selects one.

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
