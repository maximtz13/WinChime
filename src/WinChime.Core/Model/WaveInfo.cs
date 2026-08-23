namespace WinChime.Core.Model;

public sealed record WaveInfo(
    bool IsValid,
    string FormatName,
    int FormatTag,
    int Channels,
    int SampleRate,
    int BitsPerSample,
    TimeSpan Duration,
    long FileBytes,
    IReadOnlyList<string> Warnings,
    string? Error)
{
    public bool IsPlayableByWindows => IsValid && (FormatTag == 1 || FormatTag == 0xFFFE);

    public string Summary => IsValid
        ? $"{FormatName}, {SampleRate / 1000.0:0.#} kHz, {BitsPerSample}-bit, " +
          $"{(Channels == 1 ? "mono" : Channels == 2 ? "stereo" : $"{Channels}ch")}, {Duration.TotalSeconds:0.00}s"
        : Error ?? "Unreadable file";
}
