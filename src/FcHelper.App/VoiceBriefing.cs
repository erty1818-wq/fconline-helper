using System.Globalization;
using System.Speech.Synthesis;

namespace FcHelper.App;

/// <summary>
/// Reads the one-line briefing aloud so the eyes can stay on the game. Uses the Windows speech engine
/// and is only created when the feature is switched on.
/// </summary>
public sealed class VoiceBriefing : IDisposable
{
    private readonly SpeechSynthesizer _synth = new();

    public VoiceBriefing()
    {
        _synth.SetOutputToDefaultAudioDevice();
        var korean = _synth.GetInstalledVoices()
            .FirstOrDefault(v => v.Enabled && v.VoiceInfo.Culture.Equals(CultureInfo.GetCultureInfo("ko-KR")));
        if (korean is not null) _synth.SelectVoice(korean.VoiceInfo.Name);
        HasKoreanVoice = korean is not null;
        _synth.Rate = 2;
    }

    /// <summary>False when Windows has no Korean voice installed (Settings → Time &amp; language → Speech).</summary>
    public bool HasKoreanVoice { get; }

    public void Speak(string text)
    {
        _synth.SpeakAsyncCancelAll();
        _synth.SpeakAsync(text);
    }

    public void Dispose() => _synth.Dispose();
}
