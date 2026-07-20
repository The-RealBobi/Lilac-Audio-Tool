using AudioTool.Core.Playback;

namespace AudioTool.Tests;

public sealed class PlaybackPlanTests
{
    [Fact]
    public void PlansLoopPreviewWhenLoopIsActive()
    {
        var plan = AudioPlaybackPlanner.Create(
            sourcePath: "/tmp/source.wav",
            sampleCount: 100_000,
            sampleRate: 48_000,
            playStartSample: 0,
            loopStartSample: 20_000,
            loopEndSample: 80_000,
            loopEnabled: true);

        Assert.Equal(PlaybackPlanKind.LoopPreview, plan.Kind);
        Assert.Equal(0, plan.PlayStartSample);
        Assert.Equal(20_000, plan.LoopStartSample);
        Assert.Equal(80_000, plan.LoopEndSample);
        Assert.Equal(48_000, plan.SampleRate);
    }

    [Fact]
    public void PlansDirectPlaybackWhenLoopIsDisabled()
    {
        var plan = AudioPlaybackPlanner.Create(
            sourcePath: "/tmp/source.wav",
            sampleCount: 100_000,
            sampleRate: 48_000,
            playStartSample: 0,
            loopStartSample: 20_000,
            loopEndSample: 80_000,
            loopEnabled: false);

        Assert.Equal(PlaybackPlanKind.Direct, plan.Kind);
        Assert.Equal("/tmp/source.wav", plan.SourcePath);
        Assert.Null(plan.LoopStartSample);
        Assert.Null(plan.LoopEndSample);
    }
}
