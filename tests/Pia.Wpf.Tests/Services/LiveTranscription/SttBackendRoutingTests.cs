using Pia.Converters;
using Pia.Models;
using Pia.Services.LiveTranscription;
using Xunit;

namespace Pia.Tests.Services.LiveTranscription;

/// <summary>
/// Every backend must be routed explicitly. The engine and availability switches fall through to
/// Whisper, so a missing arm is a silent wrong-model bug rather than a compile error.
/// </summary>
public class SttBackendRoutingTests
{
    [Fact]
    public void Nemotron_is_appended_last_so_persisted_settings_keep_their_values()
    {
        Assert.Equal(0, (int)SttBackend.Whisper);
        Assert.Equal(1, (int)SttBackend.Parakeet);
        Assert.Equal(2, (int)SttBackend.Nemotron);
    }

    [Fact]
    public void Every_backend_has_a_distinct_localized_display_name()
    {
        var converter = new EnumToLocalizedStringConverter();
        var names = Enum.GetValues<SttBackend>()
            .Select(b => (string)converter.Convert(b, typeof(string), null!, null!))
            .ToList();

        // A missing arm falls through to value.ToString(), i.e. the bare enum name; a missing resx
        // key resolves to the bracketed key. Both are display bugs no compiler catches.
        Assert.DoesNotContain(names, n => string.IsNullOrWhiteSpace(n));
        Assert.DoesNotContain(names, n => n.StartsWith('['));
        Assert.DoesNotContain("Nemotron", names);
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(SttBackend.Whisper, "whisper-base")]
    [InlineData(SttBackend.Parakeet, "parakeet-tdt-v3")]
    [InlineData(SttBackend.Nemotron, "nemotron-3.5-streaming-560ms")]
    public void Every_backend_stamps_its_own_model_id_on_a_saved_meeting(SttBackend backend, string expected)
    {
        var settings = new AppSettings { SttBackend = backend, WhisperModel = WhisperModelSize.Base };
        Assert.Equal(expected, DirectTranscriptionService.ComputeSttModelId(settings));
    }
}
