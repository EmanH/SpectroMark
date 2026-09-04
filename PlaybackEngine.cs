using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WavMarker
{
    /// <summary>
    /// Playback engine:
    ///  - a producer thread pulls audio from the source and mixes ahead into a lock-free ring buffer (1 s)
    ///  - the device callback only copies from the ring; it never blocks and never re-plays old data
    ///    (an underrun produces silence and is counted)
    ///  - output goes through WASAPI shared mode, event driven, with the legacy winmm path as a fallback
    ///  - position comes from the device clock, corrected for zero-filled underrun frames
    /// </summary>
    public sealed class PlaybackEngine : IDisposable
    {
        IWavePlayer device;
        RingSource ring;
        Thread producer;
        volatile bool running;
        ISampleProvider source;
        int channels, sampleRate;

        public event Action PlaybackEnded;
        public bool IsPlaying => running;
        public int Underruns => ring?.Underruns ?? 0;
        public string DeviceName { get; private set; } = "";

        public void Start(ISampleProvider src)
        {
            Stop();
            source = src; channels = src.WaveFormat.Channels; sampleRate = src.WaveFormat.SampleRate;
            ring = new RingSource(src.WaveFormat, sampleRate * channels);   // 1 second of lookahead
            try
            {
                var w = new WasapiOut(AudioClientShareMode.Shared, true, 60);
                w.Init(ring);
                device = w; DeviceName = "WASAPI shared";
            }
            catch
            {
                var w = new WaveOutEvent { DesiredLatency = 100, NumberOfBuffers = 4 };
                w.Init(ring);
                device = w; DeviceName = "WaveOut";
            }
            var dev = device;
            dev.PlaybackStopped += (_, _) => { if (device == dev) PlaybackEnded?.Invoke(); };
            running = true;
            producer = new Thread(ProducerLoop) { IsBackground = true, Name = "audio-producer", Priority = ThreadPriority.AboveNormal };
            producer.Start();
            // prefill so the first device callback already has audio
            ring.WaitForFill(sampleRate / 4 * channels, 500);
            device.Play();
        }

        void ProducerLoop()
        {
            var tmp = new float[4096 * channels];
            while (running)
            {
                if (ring.Free < tmp.Length) { ring.WaitForSpace(20); continue; }
                int n;
                try { n = source.Read(tmp, 0, tmp.Length); } catch { n = 0; }
                if (n <= 0) { ring.MarkEnd(); return; }
                ring.Write(tmp, n);
            }
        }

        /// <summary>Frames actually delivered to the device since Start, minus any silence inserted on underrun.</summary>
        public long FramesPlayed
        {
            get
            {
                if (device is not IWavePosition wp || ring == null) return 0;
                long bytes; try { bytes = wp.GetPosition(); } catch { return 0; }
                long frames = bytes / (wp.OutputWaveFormat.BitsPerSample / 8 * wp.OutputWaveFormat.Channels);
                // the device may run at a different rate than the source in shared mode
                if (wp.OutputWaveFormat.SampleRate != sampleRate) frames = (long)(frames * (double)sampleRate / wp.OutputWaveFormat.SampleRate);
                return Math.Max(0, frames - ring.UnderrunFrames);
            }
        }

        public void Stop()
        {
            running = false;
            var dev = device; device = null;
            if (dev != null) { try { dev.Stop(); } catch { } try { dev.Dispose(); } catch { } }
            ring?.Release();
            producer = null;
        }

        public void Dispose() => Stop();

        /// <summary>Lock-free single-producer / single-consumer ring of interleaved floats, exposed as an IWaveProvider.</summary>
        sealed class RingSource : IWaveProvider
        {
            readonly float[] buf; readonly int cap;
            long writePos, readPos;         // in floats, monotonically increasing
            volatile bool ended;
            readonly AutoResetEvent space = new(false), data = new(false);
            public int Underruns; public long UnderrunFrames;
            readonly int ch;

            public RingSource(WaveFormat fmt, int capacityFloats) { WaveFormat = fmt; buf = new float[capacityFloats]; cap = capacityFloats; ch = fmt.Channels; }
            public WaveFormat WaveFormat { get; }

            public int Free => cap - (int)(Volatile.Read(ref writePos) - Volatile.Read(ref readPos));
            public int Available => (int)(Volatile.Read(ref writePos) - Volatile.Read(ref readPos));

            public void Write(float[] src, int n)
            {
                long w = writePos; int idx = (int)(w % cap);
                int first = Math.Min(n, cap - idx);
                Array.Copy(src, 0, buf, idx, first);
                if (n > first) Array.Copy(src, first, buf, 0, n - first);
                Volatile.Write(ref writePos, w + n);
                data.Set();
            }

            public void MarkEnd() { ended = true; data.Set(); }
            public void WaitForSpace(int ms) => space.WaitOne(ms);
            public void WaitForFill(int floats, int ms) { var sw = System.Diagnostics.Stopwatch.StartNew(); while (Available < floats && !ended && sw.ElapsedMilliseconds < ms) data.WaitOne(5); }
            public void Release() { ended = true; space.Set(); data.Set(); }

            // device side: bytes of IEEE float
            public int Read(byte[] dst, int offset, int count)
            {
                int wantFloats = count / 4;
                int avail = Available;
                int take = Math.Min(wantFloats, avail);
                if (take > 0)
                {
                    long r = readPos; int idx = (int)(r % cap);
                    int first = Math.Min(take, cap - idx);
                    Buffer.BlockCopy(buf, idx * 4, dst, offset, first * 4);
                    if (take > first) Buffer.BlockCopy(buf, 0, dst, offset + first * 4, (take - first) * 4);
                    Volatile.Write(ref readPos, r + take);
                    space.Set();
                }
                if (take < wantFloats)
                {
                    if (ended) return take * 4;                      // clean end of stream
                    int missing = wantFloats - take;                 // underrun: silence, never old data
                    Array.Clear(dst, offset + take * 4, missing * 4);
                    Underruns++; UnderrunFrames += missing / ch;
                    return count;
                }
                return count;
            }
        }
    }
}
