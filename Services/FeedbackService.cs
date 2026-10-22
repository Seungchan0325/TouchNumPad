using System.IO;
using TouchNumPad.Interop;
using TouchNumPad.Models;
using Windows.Devices.Haptics;

namespace TouchNumPad.Services;

public sealed class FeedbackService : IDisposable
{
    private readonly AppSettings _settings;
    private SimpleHapticsController? _hapticsController;
    private SimpleHapticsControllerFeedback? _clickWaveform;
    private SimpleHapticsControllerFeedback? _bumpWaveform;

    private byte[]? _lightTickWav;
    private byte[]? _homeBumpWav;
    private byte[]? _keyClickWav;
    private double _cachedVolume = -1;

    public bool IsHapticsSupported { get; private set; }

    public FeedbackService(AppSettings settings)
    {
        _settings = settings;
        _ = InitializeHapticsAsync();
        RebuildSoundBuffers();
    }

    private async Task InitializeHapticsAsync()
    {
        try
        {
            var access = await VibrationDevice.RequestAccessAsync();
            if (access == VibrationAccessStatus.Allowed)
            {
                var device = await VibrationDevice.GetDefaultAsync();
                if (device?.SimpleHapticsController is not null)
                {
                    _hapticsController = device.SimpleHapticsController;
                    var waveforms = _hapticsController.SupportedFeedback;

                    foreach (var wf in waveforms)
                    {
                        if (wf.Waveform == KnownSimpleHapticsControllerWaveforms.Click)
                            _clickWaveform = wf;
                        else if (wf.Waveform == KnownSimpleHapticsControllerWaveforms.Press ||
                                 wf.Waveform == KnownSimpleHapticsControllerWaveforms.Release)
                            _bumpWaveform ??= wf;
                    }

                    _clickWaveform ??= waveforms.FirstOrDefault();
                    _bumpWaveform ??= _clickWaveform;

                    IsHapticsSupported = _hapticsController is not null && _clickWaveform is not null;
                }
            }
        }
        catch
        {
            IsHapticsSupported = false;
        }
    }

    /// <summary>
    /// Triggered when the finger crosses into a new key area during drag.
    /// </summary>
    public void KeyEntered(NumpadKeyDefinition key)
    {
        if (key.IsHomeKey)
        {
            TriggerHomeKeyBump();
        }
        else
        {
            TriggerLightTick();
        }
    }

    /// <summary>
    /// Triggered when a key tap or click is committed.
    /// </summary>
    public void KeyCommitted()
    {
        TriggerKeyCommit();
    }

    private void TriggerLightTick()
    {
        bool hapticTriggered = false;

        if (_settings.EnableHaptics && _hapticsController is not null && _clickWaveform is not null)
        {
            try
            {
                _hapticsController.SendHapticFeedback(_clickWaveform, 0.45);
                hapticTriggered = true;
            }
            catch
            {
                // Fall back
            }
        }

        if (_settings.EnableSound && (!hapticTriggered || _settings.EnableSound))
        {
            PlaySoundInternal(_lightTickWav);
        }
    }

    private void TriggerHomeKeyBump()
    {
        bool hapticTriggered = false;

        if (_settings.EnableHaptics && _hapticsController is not null)
        {
            try
            {
                var wf = _bumpWaveform ?? _clickWaveform;
                if (wf is not null)
                {
                    _hapticsController.SendHapticFeedback(wf, 1.0);
                    hapticTriggered = true;
                }
            }
            catch
            {
                // Fall back
            }
        }

        if (_settings.EnableSound && (!hapticTriggered || _settings.EnableSound))
        {
            PlaySoundInternal(_homeBumpWav);
        }
    }

    private void TriggerKeyCommit()
    {
        bool hapticTriggered = false;

        if (_settings.EnableHaptics && _hapticsController is not null && _clickWaveform is not null)
        {
            try
            {
                _hapticsController.SendHapticFeedback(_clickWaveform, 0.85);
                hapticTriggered = true;
            }
            catch
            {
                // Fall back
            }
        }

        if (_settings.EnableSound && (!hapticTriggered || _settings.EnableSound))
        {
            PlaySoundInternal(_keyClickWav);
        }
    }

    private void PlaySoundInternal(byte[]? wavData)
    {
        if (wavData is null || wavData.Length == 0 || _settings.SoundVolume <= 0.001)
            return;

        EnsureBuffersUpdated();
        NativeMethods.PlaySound(wavData, IntPtr.Zero, NativeMethods.SND_ASYNC | NativeMethods.SND_MEMORY | NativeMethods.SND_NODEFAULT);
    }

    public void EnsureBuffersUpdated()
    {
        if (Math.Abs(_cachedVolume - _settings.SoundVolume) > 0.01)
        {
            RebuildSoundBuffers();
        }
    }

    private void RebuildSoundBuffers()
    {
        double vol = Math.Clamp(_settings.SoundVolume, 0.0, 1.0);
        _cachedVolume = vol;

        _lightTickWav = SynthesizeLightTickWav(vol);
        _homeBumpWav = SynthesizeHomeBumpWav(vol);
        _keyClickWav = SynthesizeKeyClickWav(vol);
    }

    // Synthesizes a crisp, subtle micro-tick (1300Hz, 8ms exponential decay)
    private static byte[] SynthesizeLightTickWav(double volume)
    {
        const int sampleRate = 44100;
        int numSamples = (int)(sampleRate * 0.009); // 9 ms
        short[] samples = new short[numSamples];

        for (int i = 0; i < numSamples; i++)
        {
            double t = (double)i / sampleRate;
            double decay = Math.Exp(-t / 0.0018);
            double wave = Math.Sin(2.0 * Math.PI * 1350.0 * t);
            short sample = (short)(wave * decay * volume * 0.40 * short.MaxValue);
            samples[i] = sample;
        }

        return CreateWavFromSamples(samples, sampleRate);
    }

    // Synthesizes a tactile double-bump (220Hz + 440Hz dual pulse, 28ms) for home key "5"
    private static byte[] SynthesizeHomeBumpWav(double volume)
    {
        const int sampleRate = 44100;
        int numSamples = (int)(sampleRate * 0.030); // 30 ms
        short[] samples = new short[numSamples];

        for (int i = 0; i < numSamples; i++)
        {
            double t = (double)i / sampleRate;
            double wave1 = Math.Sin(2.0 * Math.PI * 220.0 * t) + 0.4 * Math.Sin(2.0 * Math.PI * 440.0 * t);
            
            // Dual pulse envelope: pulse at 0ms and second pulse at 13ms
            double env1 = Math.Exp(-t / 0.0035);
            double t2 = Math.Max(0.0, t - 0.012);
            double env2 = (t >= 0.012) ? Math.Exp(-t2 / 0.004) * 0.9 : 0.0;
            double env = Math.Max(env1, env2);

            short sample = (short)(wave1 * env * volume * 0.65 * short.MaxValue);
            samples[i] = sample;
        }

        return CreateWavFromSamples(samples, sampleRate);
    }

    // Synthesizes a tactile key commit click (850Hz, 14ms)
    private static byte[] SynthesizeKeyClickWav(double volume)
    {
        const int sampleRate = 44100;
        int numSamples = (int)(sampleRate * 0.015); // 15 ms
        short[] samples = new short[numSamples];

        for (int i = 0; i < numSamples; i++)
        {
            double t = (double)i / sampleRate;
            double decay = Math.Exp(-t / 0.0032);
            double wave = Math.Sin(2.0 * Math.PI * 880.0 * t) + 0.3 * Math.Sin(2.0 * Math.PI * 1760.0 * t);
            short sample = (short)(wave * decay * volume * 0.55 * short.MaxValue);
            samples[i] = sample;
        }

        return CreateWavFromSamples(samples, sampleRate);
    }

    private static byte[] CreateWavFromSamples(short[] samples, int sampleRate)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        int subChunk2Size = samples.Length * 2; // 16-bit = 2 bytes per sample
        int chunkSize = 36 + subChunk2Size;

        // RIFF chunk descriptor
        bw.Write("RIFF"u8.ToArray());
        bw.Write(chunkSize);
        bw.Write("WAVE"u8.ToArray());

        // fmt sub-chunk
        bw.Write("fmt "u8.ToArray());
        bw.Write(16); // SubChunk1Size (16 for PCM)
        bw.Write((short)1); // AudioFormat (1 = PCM)
        bw.Write((short)1); // NumChannels (1 = Mono)
        bw.Write(sampleRate); // SampleRate
        bw.Write(sampleRate * 2); // ByteRate (SampleRate * NumChannels * BitsPerSample/8)
        bw.Write((short)2); // BlockAlign (NumChannels * BitsPerSample/8)
        bw.Write((short)16); // BitsPerSample

        // data sub-chunk
        bw.Write("data"u8.ToArray());
        bw.Write(subChunk2Size);

        for (int i = 0; i < samples.Length; i++)
        {
            bw.Write(samples[i]);
        }

        return ms.ToArray();
    }

    public void Dispose()
    {
        _hapticsController = null;
        _clickWaveform = null;
        _bumpWaveform = null;
    }
}
