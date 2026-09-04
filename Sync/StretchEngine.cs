using SoundTouch;

namespace WavMarker.Sync
{
    public enum StretchMode { Balanced, Tonal, Transient, Smooth }

    /// <summary>
    /// Renders time-stretched segments (pitch preserved). Small ratios, as needed for syncing takes,
    /// are essentially transparent with the WSOLA engine used here.
    /// </summary>
    public static class StretchEngine
    {
        public static readonly StretchMode[] Modes = { StretchMode.Balanced, StretchMode.Tonal, StretchMode.Transient, StretchMode.Smooth };

        public static string Describe(StretchMode m) => m switch
        {
            StretchMode.Balanced => "Balanced: general purpose",
            StretchMode.Tonal => "Tonal: sustained voices, choir, pads",
            StretchMode.Transient => "Transient: percussive, tight consonants",
            StretchMode.Smooth => "Smooth: longest windows, fewest artifacts on held notes",
            _ => m.ToString()
        };

        static void Configure(SoundTouchProcessor st, StretchMode m)
        {
            st.SetSetting(SettingId.UseQuickSeek, 0);
            st.SetSetting(SettingId.UseAntiAliasFilter, 1);
            switch (m)
            {
                case StretchMode.Balanced: st.SetSetting(SettingId.SequenceDurationMs, 60); st.SetSetting(SettingId.SeekWindowDurationMs, 20); st.SetSetting(SettingId.OverlapDurationMs, 10); break;
                case StretchMode.Tonal: st.SetSetting(SettingId.SequenceDurationMs, 90); st.SetSetting(SettingId.SeekWindowDurationMs, 30); st.SetSetting(SettingId.OverlapDurationMs, 14); break;
                case StretchMode.Transient: st.SetSetting(SettingId.SequenceDurationMs, 30); st.SetSetting(SettingId.SeekWindowDurationMs, 10); st.SetSetting(SettingId.OverlapDurationMs, 6); break;
                case StretchMode.Smooth: st.SetSetting(SettingId.SequenceDurationMs, 130); st.SetSetting(SettingId.SeekWindowDurationMs, 45); st.SetSetting(SettingId.OverlapDurationMs, 20); break;
            }
        }

        /// <summary>
        /// Render source frames [s0, s1) of <paramref name="a"/> to exactly <paramref name="targetLen"/> frames.
        /// Returns per-channel buffers.
        /// </summary>
        public static float[][] RenderSegment(AudioData a, long s0, long s1, long targetLen, StretchMode mode)
        {
            int ch = a.ChannelCount;
            var outp = new float[ch][];
            for (int c = 0; c < ch; c++) outp[c] = new float[targetLen];
            long srcLen = s1 - s0;
            if (targetLen <= 0 || srcLen <= 0) return outp;
            double tempo = (double)srcLen / targetLen;   // >1 = shorter output

            if (Math.Abs(tempo - 1.0) < 1e-6)
            {
                long n = Math.Min(srcLen, targetLen);
                for (int c = 0; c < ch; c++) Array.Copy(a.Channels[c], s0, outp[c], 0, n);
                return outp;
            }

            // give the stretcher context on both sides so the segment edges are clean
            long ctx = Math.Min(a.SampleRate / 8, srcLen);
            long pre = Math.Min(s0, ctx), post = Math.Min(a.Length - s1, ctx);
            long in0 = s0 - pre, in1 = s1 + post, inLen = in1 - in0;

            var st = new SoundTouchProcessor { SampleRate = a.SampleRate, Channels = ch, Tempo = tempo };
            Configure(st, mode);

            var expectedOut = (long)Math.Ceiling(inLen / tempo) + a.SampleRate;
            var result = new float[ch][];
            for (int c = 0; c < ch; c++) result[c] = new float[expectedOut];
            long produced = 0;

            var inBuf = new float[4096 * ch];
            var outBuf = new float[4096 * ch];
            long pos = in0;
            void Drain()
            {
                int n;
                while ((n = st.ReceiveSamples(outBuf, 4096)) > 0)
                {
                    if (produced + n > expectedOut) { for (int c = 0; c < ch; c++) Array.Resize(ref result[c], (int)(expectedOut * 2)); expectedOut *= 2; }
                    for (int i = 0; i < n; i++) for (int c = 0; c < ch; c++) result[c][produced + i] = outBuf[i * ch + c];
                    produced += n;
                }
            }
            while (pos < in1)
            {
                int frames = (int)Math.Min(4096, in1 - pos);
                for (int i = 0; i < frames; i++) for (int c = 0; c < ch; c++) inBuf[i * ch + c] = a.Channels[c][pos + i];
                st.PutSamples(inBuf, frames);
                pos += frames;
                Drain();
            }
            st.Flush();
            Drain();

            // the segment proper starts after the stretched pre-context
            long start = (long)Math.Round(pre / tempo);
            for (int c = 0; c < ch; c++)
            {
                long avail = Math.Max(0, Math.Min(targetLen, produced - start));
                if (avail > 0) Array.Copy(result[c], start, outp[c], 0, avail);
            }
            return outp;
        }
    }
}
