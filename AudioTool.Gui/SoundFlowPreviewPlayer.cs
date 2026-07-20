using System;
using System.IO;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Backends.MiniAudio.Devices;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Providers;
using SoundFlow.Structs;

namespace Level5.AudioTool.Gui;

internal sealed class SoundFlowPreviewPlayer : IDisposable
{
    private MiniAudioEngine? _engine;
    private AudioPlaybackDevice? _device;
    private SoundPlayer? _player;
    private StreamDataProvider? _provider;
    private FileStream? _stream;
    private AudioFormat _format;

    public bool IsActive => _player is not null;
    public bool IsPlaying => _player?.State == PlaybackState.Playing;

    public bool TryPlay(
        string path,
        int sampleRate,
        int channels,
        int startSample,
        int? loopStartSample,
        int? loopEndSample,
        out string? error)
    {
        error = null;
        try
        {
            Stop();
            EnsureDevice(sampleRate, channels);
            if (_engine is null || _device is null)
            {
                error = "SoundFlow is not initialized.";
                return false;
            }

            _stream = File.OpenRead(path);
            _provider = new StreamDataProvider(_engine, _stream, null!);
            _player = new SoundPlayer(_engine, _format, _provider);
            _device.MasterMixer.AddComponent(_player);

            if (loopStartSample is not null && loopEndSample is not null && loopEndSample > loopStartSample)
            {
                _player.SetLoopPoints(loopStartSample.Value, loopEndSample.Value);
                _player.IsLooping = true;
            }

            if (startSample > 0)
            {
                _player.Seek(startSample);
            }

            _player.Play();
            if (_player.State == PlaybackState.Playing)
            {
                return true;
            }

            error = $"SoundFlow did not enter playback state ({_player.State}).";
            Stop();
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Stop();
            return false;
        }
    }

    public void Stop()
    {
        try
        {
            if (_player is not null)
            {
                _player.Stop();
                try
                {
                    _device?.MasterMixer.RemoveComponent(_player);
                }
                catch
                {
                    // The mixer may already have detached the component.
                }
            }
        }
        finally
        {
            _player?.Dispose();
            _provider?.Dispose();
            _stream?.Dispose();
            _player = null;
            _provider = null;
            _stream = null;
        }
    }

    public void Dispose()
    {
        Stop();
        _device?.Dispose();
        _engine?.Dispose();
        _device = null;
        _engine = null;
    }

    private void EnsureDevice(int sampleRate, int channels)
    {
        sampleRate = sampleRate > 0 ? sampleRate : 48_000;
        channels = channels > 0 ? channels : 2;
        var format = AudioFormat.Dvd;
        format.SampleRate = sampleRate;
        format.Channels = channels;
        format.Format = SampleFormat.F32;

        if (_device is not null && _format.SampleRate == format.SampleRate && _format.Channels == format.Channels)
        {
            return;
        }

        _device?.Dispose();
        _device = null;
        _engine ??= new MiniAudioEngine(null);
        _format = format;
        _device = _engine.InitializePlaybackDevice(null, _format, new MiniAudioDeviceConfig());
    }
}
