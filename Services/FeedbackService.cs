using System.IO;
using System.Runtime.InteropServices;
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

    // Pinned unmanaged memory pointers for WinMM PlaySound (SND_MEMORY | SND_ASYNC)
    private IntPtr _pLightTick = IntPtr.Zero;
    private IntPtr _pHomeBump = IntPtr.Zero;
    private IntPtr _pKeyClick = IntPtr.Zero;
    private double _cachedVolume = -1;
    private readonly object _soundLock = new();

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
        if (_settings.EnableHaptics && _hapticsController is not null && _clickWaveform is not null)
        {
            try
            {
                _hapticsController.SendHapticFeedback(_clickWaveform, 0.45);
            }
            catch
            {
                // Fall back
            }
        }

        if (_settings.EnableSound)
        {
            EnsureBuffersUpdated();
            PlaySoundInternal(_pLightTick);
        }
    }

    private void TriggerHomeKeyBump()
    {
        if (_settings.EnableHaptics && _hapticsController is not null)
        {
            try
            {
                var wf = _bumpWaveform ?? _clickWaveform;
                if (wf is not null)
                {
                    _hapticsController.SendHapticFeedback(wf, 1.0);
                }
            }
            catch
            {
                // Fall back
            }
        }

        if (_settings.EnableSound)
        {
            EnsureBuffersUpdated();
            PlaySoundInternal(_pHomeBump);
        }
    }

    private void TriggerKeyCommit()
    {
        if (_settings.EnableHaptics && _hapticsController is not null && _clickWaveform is not null)
        {
            try
            {
                _hapticsController.SendHapticFeedback(_clickWaveform, 0.85);
            }
            catch
            {
                // Fall back
            }
        }

        if (_settings.EnableSound)
        {
            EnsureBuffersUpdated();
            PlaySoundInternal(_pKeyClick);
        }
    }

    private void PlaySoundInternal(IntPtr pSound)
    {
        if (pSound == IntPtr.Zero || _settings.SoundVolume <= 0.001)
            return;

        NativeMethods.PlaySound(pSound, IntPtr.Zero, NativeMethods.SND_ASYNC | NativeMethods.SND_MEMORY | NativeMethods.SND_NODEFAULT);
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
        lock (_soundLock)
        {
            double vol = Math.Clamp(_settings.SoundVolume, 0.0, 1.0);
            _cachedVolume = vol;

            // Stop any playing sound before modifying buffers
            NativeMethods.PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
            FreeUnmanagedBuffers();

            if (vol <= 0.001)
                return;

            byte[] tickBytes = SynthesizeLightTickWav(vol);
            byte[] bumpBytes = SynthesizeHomeBumpWav(vol);
            byte[] clickBytes = SynthesizeKeyClickWav(vol);

            _pLightTick = Marshal.AllocHGlobal(tickBytes.Length);
            Marshal.Copy(tickBytes, 0, _pLightTick, tickBytes.Length);

            _pHomeBump = Marshal.AllocHGlobal(bumpBytes.Length);
            Marshal.Copy(bumpBytes, 0, _pHomeBump, bumpBytes.Length);

            _pKeyClick = Marshal.AllocHGlobal(clickBytes.Length);
            Marshal.Copy(clickBytes, 0, _pKeyClick, clickBytes.Length);
        }
    }

    private void FreeUnmanagedBuffers()
    {
        if (_pLightTick != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_pLightTick);
            _pLightTick = IntPtr.Zero;
        }
        if (_pHomeBump != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_pHomeBump);
            _pHomeBump = IntPtr.Zero;
        }
        if (_pKeyClick != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_pKeyClick);
            _pKeyClick = IntPtr.Zero;
        }
    }

    // 1. Crisp micro-tick for boundary crossing (1800Hz + 2600Hz, 25ms, audible on laptop speakers)
    private static byte[] SynthesizeLightTickWav(double volume)
    {
        const int sampleRate = 44100;
        int numSamples = (int)(sampleRate * 0.025); // 25 ms
        short[] samples = new short[numSamples];

        for (int i = 0; i < numSamples; i++)
        {
            double t = (double)i / sampleRate;
            double decay = Math.Exp(-t / 0.005);
            double wave = 0.7 * Math.Sin(2.0 * Math.PI * 1800.0 * t) + 0.3 * Math.Sin(2.0 * Math.PI * 2600.0 * t);
            short sample = (short)(wave * decay * volume * 0.75 * short.MaxValue);
            samples[i] = sample;
        }

        return CreateWavFromSamples(samples, sampleRate);
    }

    // 2. Tactile double-bump for Home key "5" (650Hz + 1300Hz dual pulse, 45ms)
    private static byte[] SynthesizeHomeBumpWav(double volume)
    {
        const int sampleRate = 44100;
        int numSamples = (int)(sampleRate * 0.045); // 45 ms
        short[] samples = new short[numSamples];

        for (int i = 0; i < numSamples; i++)
        {
            double t = (double)i / sampleRate;
            double wave1 = Math.Sin(2.0 * Math.PI * 650.0 * t) + 0.4 * Math.Sin(2.0 * Math.PI * 1300.0 * t);

            // Dual pulse: 0ms and 18ms
            double env1 = Math.Exp(-t / 0.006);
            double t2 = Math.Max(0.0, t - 0.018);
            double env2 = (t >= 0.018) ? Math.Exp(-t2 / 0.006) * 0.95 : 0.0;
            double env = Math.Max(env1, env2);

            short sample = (short)(wave1 * env * volume * 0.85 * short.MaxValue);
            samples[i] = sample;
        }

        return CreateWavFromSamples(samples, sampleRate);
    }

    // 3. Crisp, satisfying switch click on commit/lift-off (920Hz body + 2800Hz transient click, 35ms)
    private static byte[] SynthesizeKeyClickWav(double volume)
    {
        const int sampleRate = 44100;
        int numSamples = (int)(sampleRate * 0.035); // 35 ms
        short[] samples = new short[numSamples];

        for (int i = 0; i < numSamples; i++)
        {
            double t = (double)i / sampleRate;
            // Fast attack click transient
            double clickDecay = Math.Exp(-t / 0.003);
            double clickWave = Math.Sin(2.0 * Math.PI * 2800.0 * t);

            // Resonant switch body
            double bodyDecay = Math.Exp(-t / 0.008);
            double bodyWave = Math.Sin(2.0 * Math.PI * 920.0 * t) + 0.3 * Math.Sin(2.0 * Math.PI * 1840.0 * t);

            double mixed = (clickWave * clickDecay * 0.45) + (bodyWave * bodyDecay * 0.55);
            short sample = (short)(mixed * volume * 0.90 * short.MaxValue);
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
        bw.Write("RIFF"u8);
        bw.Write(chunkSize);
        bw.Write("WAVE"u8);

        // fmt sub-chunk
        bw.Write("fmt "u8);
        bw.Write(16); // SubChunk1Size (16 for PCM)
        bw.Write((short)1); // AudioFormat (1 = PCM)
        bw.Write((short)1); // NumChannels (1 = Mono)
        bw.Write(sampleRate); // SampleRate
        bw.Write(sampleRate * 2); // ByteRate (SampleRate * NumChannels * BitsPerSample/8)
        bw.Write((short)2); // BlockAlign (NumChannels * BitsPerSample/8)
        bw.Write((short)16); // BitsPerSample

        // data sub-chunk
        bw.Write("data"u8);
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

        lock (_soundLock)
        {
            NativeMethods.PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
            FreeUnmanagedBuffers();
        }
    }
}
