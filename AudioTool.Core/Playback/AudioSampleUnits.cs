namespace AudioTool.Core.Playback;

public static class AudioSampleUnits
{
    public static int ToInterleavedSampleOffset(int frameSample, int channels)
    {
        if (frameSample <= 0)
        {
            return 0;
        }

        return checked(frameSample * Math.Max(1, channels));
    }
}
