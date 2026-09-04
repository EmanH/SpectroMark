using NAudio.Wave;
using SoundTouch;

namespace WavMarker
{
    /// <summary>Time-stretches a sample provider (pitch preserved) using SoundTouch. Tempo 1.0 = bypass.</summary>
    class TempoProvider : ISampleProvider
    {
        readonly ISampleProvider source;
        readonly SoundTouchProcessor st;
        readonly int ch;
        readonly float[] inBuf;
        bool sourceDone;
        double tempo = 1.0;

        public TempoProvider(ISampleProvider source, double tempo)
        {
            this.source = source;
            WaveFormat = source.WaveFormat;
            ch = WaveFormat.Channels;
            st = new SoundTouchProcessor { SampleRate = WaveFormat.SampleRate, Channels = ch };
            // settings tuned for natural-sounding speech/music at modest speed-ups
            st.SetSetting(SettingId.UseQuickSeek, 0);
            st.SetSetting(SettingId.UseAntiAliasFilter, 1);
            st.SetSetting(SettingId.SequenceDurationMs, 40);
            st.SetSetting(SettingId.SeekWindowDurationMs, 15);
            st.SetSetting(SettingId.OverlapDurationMs, 8);
            inBuf = new float[4096 * ch];
            Tempo = tempo;
        }

        public WaveFormat WaveFormat { get; }

        public double Tempo
        {
            get => tempo;
            set { tempo = Math.Clamp(value, 0.5, 4.0); st.Tempo = tempo; }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            if (Math.Abs(tempo - 1.0) < 1e-3 && st.AvailableSamples == 0) return source.Read(buffer, offset, count);
            int wantFrames = count / ch;
            int got = 0;
            while (got < wantFrames)
            {
                int n = st.ReceiveSamples(new Span<float>(buffer, offset + got * ch, (wantFrames - got) * ch), wantFrames - got);
                got += n;
                if (got >= wantFrames) break;
                if (sourceDone) { if (n == 0) break; continue; }
                int read = source.Read(inBuf, 0, inBuf.Length);
                if (read <= 0) { sourceDone = true; st.Flush(); continue; }
                st.PutSamples(new ReadOnlySpan<float>(inBuf, 0, read), read / ch);
            }
            return got * ch;
        }
    }
}
