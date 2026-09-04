namespace WavMarker
{
    /// <summary>
    /// Adobe Audition-style "logarithmic" frequency axis. Measured from Audition's display:
    /// it is piecewise-log with the low end compressed (everything under ~270 Hz squashed into
    /// the bottom rows) and the top end tightening toward 20 kHz.
    /// </summary>
    static class FreqScale
    {
        // (frequency, fraction of band height from the bottom) measured from Audition
        static readonly (double f, double y)[] Table =
        {
            (270, 0.000), (500, 0.070), (1000, 0.147), (2000, 0.283), (3000, 0.407), (4000, 0.506),
            (5000, 0.590), (6000, 0.657), (7000, 0.708), (10000, 0.817), (15000, 0.920), (20000, 1.000)
        };

        /// <summary>Returns the top-of-band fraction value so that nyq maps to 1.0 after scaling.</summary>
        static double TopScale(double nyq)
        {
            if (nyq <= 20000) return RawFrac(nyq);
            return 1.0 + Math.Log10(nyq / 20000) * 0.585;
        }

        static double RawFrac(double f)
        {
            if (f <= Table[0].f) return Table[0].y * (f / Table[0].f);
            var last = Table[^1];
            if (f >= last.f) return last.y + Math.Log10(f / last.f) * 0.585;
            for (int i = 0; i < Table.Length - 1; i++)
            {
                var (f0, y0) = Table[i]; var (f1, y1) = Table[i + 1];
                if (f <= f1) return y0 + (y1 - y0) * (Math.Log(f / f0) / Math.Log(f1 / f0));
            }
            return last.y;
        }

        static double RawInverse(double y)
        {
            if (y <= 0) return 0;
            var last = Table[^1];
            if (y >= last.y) return last.f * Math.Pow(10, (y - last.y) / 0.585);
            for (int i = 0; i < Table.Length - 1; i++)
            {
                var (f0, y0) = Table[i]; var (f1, y1) = Table[i + 1];
                if (y <= y1) return f0 * Math.Pow(f1 / f0, (y - y0) / (y1 - y0));
            }
            return last.f;
        }

        /// <summary>0 = bottom of band, 1 = top (nyquist).</summary>
        public static double ToFrac(double f, double nyq, bool log)
        {
            if (!log) return f / nyq;
            return Math.Clamp(RawFrac(f) / TopScale(nyq), 0, 1);
        }

        public static double ToFreq(double frac, double nyq, bool log)
        {
            if (!log) return frac * nyq;
            return Math.Min(nyq, RawInverse(frac * TopScale(nyq)));
        }
    }
}
