namespace AudioTool.Core.Playback;

public enum PlaybackPlanKind
{
    Direct,
    LoopPreview
}

public sealed record AudioPlaybackPlan(
    PlaybackPlanKind Kind,
    string SourcePath,
    int SampleCount,
    int SampleRate,
    int PlayStartSample,
    int? LoopStartSample,
    int? LoopEndSample);

public static class AudioPlaybackPlanner
{
    public static AudioPlaybackPlan Create(
        string sourcePath,
        int sampleCount,
        int sampleRate,
        int playStartSample,
        int loopStartSample,
        int loopEndSample,
        bool loopEnabled)
    {
        if (!loopEnabled || sampleCount <= 0 || sampleRate <= 0 || loopEndSample <= loopStartSample)
        {
            return new AudioPlaybackPlan(
                PlaybackPlanKind.Direct,
                sourcePath,
                Math.Max(0, sampleCount),
                Math.Max(0, sampleRate),
                Math.Clamp(playStartSample, 0, Math.Max(0, sampleCount)),
                null,
                null);
        }

        var start = Math.Clamp(playStartSample, 0, Math.Max(0, sampleCount - 1));
        var loopStart = Math.Clamp(loopStartSample, 0, Math.Max(0, sampleCount - 1));
        var loopEnd = Math.Clamp(loopEndSample, loopStart + 1, sampleCount);
        if (start >= loopEnd)
        {
            start = loopStart;
        }

        return new AudioPlaybackPlan(
            PlaybackPlanKind.LoopPreview,
            sourcePath,
            sampleCount,
            sampleRate,
            start,
            loopStart,
            loopEnd);
    }
}
