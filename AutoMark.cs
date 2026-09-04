using NAudio.Dsp;

namespace WavMarker
{
    public class AutoMarkSettings
    {
        /// <summary>Markers never closer than this (seconds).</summary>
        public double MinGap = 0.15;
        /// <summary>Pitch step that counts as a new note (semitones).</summary>
        public double PitchStep = 0.75;
        /// <summary>Each side of a pitch step must hold steady for this long (seconds).</summary>
        public double PitchHold = 0.10;
        /// <summary>Overall onset sensitivity; 1 = default, higher = more markers.</summary>
        public double Sensitivity = 1.0;
        /// <summary>Consonant detector sensitivity; 1 = default.</summary>
        public double ConsonantSensitivity = 1.0;
    }

    public class AutoMarkEvent
    {
        public double Time;        // seconds
        public string Kind;        // "consonant", "onset", "pitch"
        public double Strength;
        public override string ToString() => $"{Time,8:0.000}s  {Kind,-9} {Strength:0.00}";
    }

    /// <summary>
    /// Detects word / note starts in sung vocals:
    ///   1. consonant bursts (S, T, D, K, F ...) via high-frequency energy, marker at the burst centre
    ///   2. general onsets via a vibrato-tolerant spectral flux (SuperFlux-style max-filtered log bands)
    ///   3. sustained pitch changes (a held "oo" moving to a new note) via a YIN pitch track with
    ///      before/after median comparison so vibrato does not count
    /// Everything is deterministic and relative to the file's own level statistics.
    /// </summary>
    public static class AutoMark
    {
        // ---- analysis constants ----
        const int Fft = 2048;
        const int FftM = 11;
        const double HopSec = 0.005;           // ~5 ms frames
        const double BandLo = 60, BandHi = 16000;
        const int BandsPerOctave = 24;
        const double HfLo = 3500, HfHi = 11000; // consonant band
        const double LfLo = 90, LfHi = 1800;    // voiced band

        public static List<AutoMarkEvent> Detect(AudioData a, AutoMarkSettings s = null)
        {
            s ??= new AutoMarkSettings();
            int sr = a.SampleRate;
            float[] x = Mono(a);
            int hop = Math.Max(64, (int)Math.Round(sr * HopSec));
            int frames = Math.Max(1, (x.Length - Fft) / hop + 1);
            double frameSec = (double)hop / sr;

            // ---------- STFT into log-spaced bands ----------
            double nyq = sr / 2.0;
            double hiF = Math.Min(BandHi, nyq * 0.95);
            int nBands = (int)Math.Floor(Math.Log2(hiF / BandLo) * BandsPerOctave);
            var bandLoBin = new int[nBands]; var bandHiBin = new int[nBands];
            double binHz = (double)sr / Fft;
            for (int b = 0; b < nBands; b++)
            {
                double f0 = BandLo * Math.Pow(2, (double)b / BandsPerOctave), f1 = BandLo * Math.Pow(2, (double)(b + 1) / BandsPerOctave);
                bandLoBin[b] = Math.Max(1, (int)Math.Floor(f0 / binHz));
                bandHiBin[b] = Math.Max(bandLoBin[b], (int)Math.Floor(f1 / binHz));
            }
            int hfLo = (int)(HfLo / binHz), hfHi = Math.Min(Fft / 2 - 1, (int)(HfHi / binHz));
            int lfLo = (int)(LfLo / binHz), lfHi = (int)(LfHi / binHz);

            var window = new float[Fft];
            for (int i = 0; i < Fft; i++) window[i] = (float)FastFourierTransform.HannWindow(i, Fft);

            var L = new float[frames][];          // log band levels (dB)
            var ehf = new double[frames];          // HF energy dB
            var elf = new double[frames];          // LF energy dB
            var etot = new double[frames];         // total dB
            Parallel.For(0, frames, () => new Complex[Fft], (t, _, buf) =>
            {
                int off = t * hop;
                for (int i = 0; i < Fft; i++) { int k = off + i; buf[i].X = (k < x.Length ? x[k] : 0f) * window[i]; buf[i].Y = 0; }
                FastFourierTransform.FFT(true, FftM, buf);
                var pw = new double[Fft / 2];
                double tot = 0, hf = 0, lf = 0;
                for (int k = 0; k < Fft / 2; k++) { pw[k] = buf[k].X * buf[k].X + buf[k].Y * buf[k].Y; tot += pw[k]; }
                for (int k = hfLo; k <= hfHi; k++) hf += pw[k];
                for (int k = lfLo; k <= lfHi; k++) lf += pw[k];
                var row = new float[nBands];
                for (int b = 0; b < nBands; b++)
                {
                    double p = 0; for (int k = bandLoBin[b]; k <= bandHiBin[b]; k++) p += pw[k];
                    p /= (bandHiBin[b] - bandLoBin[b] + 1);
                    row[b] = (float)(10 * Math.Log10(p + 1e-12));
                }
                L[t] = row; ehf[t] = 10 * Math.Log10(hf + 1e-12); elf[t] = 10 * Math.Log10(lf + 1e-12); etot[t] = 10 * Math.Log10(tot + 1e-12);
                return buf;
            }, _ => { });

            // ---------- level statistics ----------
            double totFloor = Percentile(etot, 0.08), totPeak = Percentile(etot, 0.98);
            double hfFloor = Percentile(ehf, 0.10), hfPeak = Percentile(ehf, 0.985);
            double lfFloor = Percentile(elf, 0.10);
            double bandFloor = totFloor - 10 * Math.Log10(Fft / 2.0); // rough per-bin floor
            var voicedGate = new bool[frames];
            for (int t = 0; t < frames; t++) voicedGate[t] = elf[t] > lfFloor + 14;

            // ---------- 1. SuperFlux-style onset function ----------
            int mu = Math.Max(1, (int)Math.Round(0.012 / frameSec)); // compare to ~12 ms earlier
            var flux = new double[frames];
            for (int t = mu; t < frames; t++)
            {
                var cur = L[t]; var prev = L[t - mu];
                double sum = 0;
                for (int b = 0; b < nBands; b++)
                {
                    // max filter over +-2 bands of the previous frame: vibrato / small pitch wobble does not count
                    float m = prev[b];
                    for (int d = -2; d <= 2; d++) { int j = b + d; if (j >= 0 && j < nBands && prev[j] > m) m = prev[j]; }
                    double diff = cur[b] - m;
                    if (diff > 0 && cur[b] > bandFloor + 6) sum += Math.Min(diff, 25);
                }
                flux[t] = sum / nBands;
            }
            var fluxSm = Smooth(flux, Math.Max(1, (int)Math.Round(0.01 / frameSec)));

            // ---------- 2. consonant detector ----------
            // fricative/plosive when HF band is loud and close to (or above) the voiced band
            var cons = new double[frames];
            double consThresh = hfFloor + (hfPeak - hfFloor) * (0.42 / s.ConsonantSensitivity);
            for (int t = 0; t < frames; t++)
            {
                bool hfLoud = ehf[t] > consThresh;
                bool hfDominant = ehf[t] > elf[t] - 8;
                cons[t] = (hfLoud && hfDominant) ? Math.Pow(10, (ehf[t] - hfFloor) / 10) : 0;
            }
            var events = new List<AutoMarkEvent>();
            {
                int minLen = Math.Max(1, (int)Math.Round(0.02 / frameSec));
                int gapAllow = Math.Max(1, (int)Math.Round(0.012 / frameSec));
                int t = 0;
                while (t < frames)
                {
                    if (cons[t] <= 0) { t++; continue; }
                    int start = t, end = t, gap = 0, u = t;
                    while (u < frames)
                    {
                        if (cons[u] > 0) { end = u; gap = 0; } else if (++gap > gapAllow) break;
                        u++;
                    }
                    int len = end - start + 1;
                    if (len >= minLen)
                    {
                        // centre = energy-weighted centroid of the burst
                        double w = 0, ws = 0, peak = 0;
                        for (int k = start; k <= end; k++) { w += cons[k]; ws += cons[k] * k; if (cons[k] > peak) peak = cons[k]; }
                        double centre = ws / w;
                        // very long HF regions (e.g. "sss" held, or noisy sustained) : keep the centre of the first 180 ms
                        double maxLen = 0.18 / frameSec;
                        if (len > maxLen) { w = 0; ws = 0; for (int k = start; k < start + (int)maxLen; k++) { w += cons[k]; ws += cons[k] * k; } centre = ws / w; }
                        events.Add(new AutoMarkEvent { Time = centre * frameSec + Fft / 2.0 / sr, Kind = "consonant", Strength = 10 * Math.Log10(peak) / 40 + 1 });
                    }
                    t = u + 1;
                }
            }

            // ---------- onset peak picking ----------
            {
                int w = Math.Max(1, (int)Math.Round(0.03 / frameSec));         // local max window
                int ctx = Math.Max(1, (int)Math.Round(0.5 / frameSec));         // adaptive threshold window
                double fluxRef = Percentile(fluxSm.Where(v => v > 0).ToArray(), 0.90);
                if (fluxRef <= 0) fluxRef = 1;
                double delta = fluxRef * 0.28 / s.Sensitivity;
                var thr = new double[frames];
                for (int t = 0; t < frames; t++)
                {
                    int a0 = Math.Max(0, t - ctx), a1 = Math.Min(frames - 1, t + ctx);
                    double m = 0; for (int k = a0; k <= a1; k++) m += fluxSm[k]; m /= (a1 - a0 + 1);
                    thr[t] = m * 1.25 + delta;
                }
                int after = Math.Max(1, (int)Math.Round(0.06 / frameSec));
                for (int t = w; t < frames - w; t++)
                {
                    double v = fluxSm[t];
                    if (v < thr[t]) continue;
                    bool isMax = true;
                    for (int k = t - w; k <= t + w && isMax; k++) if (fluxSm[k] > v) isMax = false;
                    if (!isMax) continue;
                    // must lead into real signal (not just noise wobble)
                    double lvl = 0; int n = 0; for (int k = t; k < Math.Min(frames, t + after); k++) { lvl += etot[k]; n++; }
                    if (lvl / n < totFloor + 12) continue;
                    // must be a real rise: level (or LF/voiced level) 40 ms after vs 40 ms before, unless the flux is very strong
                    int pre = Math.Max(1, (int)Math.Round(0.04 / frameSec));
                    double before = 0, afterL = 0; int nb = 0, na = 0;
                    for (int k = Math.Max(0, t - pre - pre); k < Math.Max(0, t - pre / 2); k++) { before += etot[k]; nb++; }
                    for (int k = Math.Min(frames - 1, t + pre / 2); k < Math.Min(frames, t + pre + pre); k++) { afterL += etot[k]; na++; }
                    double rise = (na > 0 && nb > 0) ? afterL / na - before / nb : 0;
                    double strength = v / fluxRef;
                    if (rise < 3.0 && strength < 1.6) continue;
                    if (rise < 1.0 && strength < 2.5) continue;
                    // onset time = where the rise starts: walk back to where flux fell below half the peak
                    int t0 = t; while (t0 > 0 && fluxSm[t0 - 1] > v * 0.5 && t - t0 < w) t0--;
                    events.Add(new AutoMarkEvent { Time = t0 * frameSec + Fft / 2.0 / sr, Kind = "onset", Strength = strength + Math.Max(0, rise) / 20 });
                }
            }

            // ---------- 3. pitch step detector ----------
            {
                var (pitchSt, pHop) = PitchTrack(x, sr);
                int H = Math.Max(2, (int)Math.Round(s.PitchHold / pHop));
                int n = pitchSt.Length;
                var score = new double[n];
                for (int t = H; t < n - H; t++)
                {
                    var left = new List<double>(); var right = new List<double>();
                    for (int k = t - H; k < t; k++) if (!double.IsNaN(pitchSt[k])) left.Add(pitchSt[k]);
                    for (int k = t; k < t + H; k++) if (!double.IsNaN(pitchSt[k])) right.Add(pitchSt[k]);
                    if (left.Count < H * 0.75 || right.Count < H * 0.75) continue;
                    double ml = Median(left), mr = Median(right);
                    double spreadL = Mad(left, ml), spreadR = Mad(right, mr);
                    if (spreadL > 0.6 || spreadR > 0.6) continue;
                    double d = Math.Abs(mr - ml);
                    if (d < s.PitchStep) continue;
                    if (d > 11.4 && d < 12.6) continue; // octave error guard
                    score[t] = d;
                }
                // local maxima of the step score
                for (int t = 1; t < n - 1; t++)
                {
                    if (score[t] <= 0) continue;
                    bool isMax = true;
                    for (int k = Math.Max(0, t - H); k <= Math.Min(n - 1, t + H) && isMax; k++) if (score[k] > score[t]) isMax = false;
                    if (!isMax) continue;
                    // refine: the frame where the pitch crosses the midpoint between the two medians
                    double ml = Median(Slice(pitchSt, t - H, t)), mr = Median(Slice(pitchSt, t, t + H)), mid = (ml + mr) / 2;
                    int best = t;
                    for (int k = Math.Max(1, t - H); k < Math.Min(n, t + H); k++)
                    {
                        double p0 = pitchSt[k - 1], p1 = pitchSt[k];
                        if (double.IsNaN(p0) || double.IsNaN(p1)) continue;
                        if ((p0 - mid) * (p1 - mid) <= 0) { best = k; break; }
                    }
                    // must be inside voiced material
                    int f = Math.Min(frames - 1, (int)(best * pHop / frameSec));
                    if (!voicedGate[f]) continue;
                    events.Add(new AutoMarkEvent { Time = best * pHop, Kind = "pitch", Strength = score[t] / 4 });
                    // skip past this peak
                    t += H;
                }
            }

            return Merge(events, s);
        }

        // ---------- merging rules ----------
        static List<AutoMarkEvent> Merge(List<AutoMarkEvent> ev, AutoMarkSettings s)
        {
            ev.Sort((a, b) => a.Time.CompareTo(b.Time));
            var cons = ev.Where(e => e.Kind == "consonant").ToList();
            var keep = new List<AutoMarkEvent>(cons);
            // an onset just after a consonant is the vowel of the same word: drop it; the marker sits on the consonant
            foreach (var e in ev.Where(e => e.Kind == "onset"))
            {
                bool nearCons = cons.Any(c => e.Time >= c.Time - 0.07 && e.Time <= c.Time + 0.16);
                if (!nearCons) keep.Add(e);
            }
            // pitch changes only count when nothing else already marks that moment
            foreach (var e in ev.Where(e => e.Kind == "pitch"))
            {
                bool near = keep.Any(k => Math.Abs(k.Time - e.Time) < 0.14);
                if (!near) keep.Add(e);
            }
            // enforce minimum gap, strongest first
            keep.Sort((a, b) => b.Strength.CompareTo(a.Strength));
            var final = new List<AutoMarkEvent>();
            foreach (var e in keep)
                if (!final.Any(f => Math.Abs(f.Time - e.Time) < s.MinGap)) final.Add(e);
            final.Sort((a, b) => a.Time.CompareTo(b.Time));
            return final;
        }

        // ---------- pitch tracking (YIN) ----------
        static (double[] semitones, double hopSec) PitchTrack(float[] x, int sr)
        {
            // downsample to ~11 kHz for speed
            int dec = Math.Max(1, sr / 11025);
            int n = x.Length / dec;
            var y = new float[n];
            for (int i = 0; i < n; i++) { float acc = 0; for (int k = 0; k < dec; k++) acc += x[i * dec + k]; y[i] = acc / dec; }
            int fs = sr / dec;
            int win = (int)(fs * 0.045), hop = (int)(fs * 0.010);
            int minLag = (int)(fs / 1100.0), maxLag = (int)(fs / 65.0);
            int frames = Math.Max(1, (n - win - maxLag) / hop);
            var outSt = new double[frames];
            Parallel.For(0, frames, t =>
            {
                int off = t * hop;
                double e = 0; for (int i = 0; i < win; i++) e += y[off + i] * y[off + i];
                if (e / win < 1e-7) { outSt[t] = double.NaN; return; }
                var d = new double[maxLag + 1];
                for (int tau = minLag; tau <= maxLag; tau++)
                {
                    double acc = 0;
                    for (int i = 0; i < win; i++) { double df = y[off + i] - y[off + i + tau]; acc += df * df; }
                    d[tau] = acc;
                }
                // cumulative mean normalised difference
                var cm = new double[maxLag + 1]; double run = 0;
                for (int tau = minLag; tau <= maxLag; tau++) { run += d[tau]; cm[tau] = d[tau] * (tau - minLag + 1) / (run + 1e-12); }
                int best = -1;
                for (int tau = minLag + 1; tau < maxLag; tau++)
                    if (cm[tau] < 0.15 && cm[tau] <= cm[tau - 1] && cm[tau] <= cm[tau + 1]) { best = tau; break; }
                if (best < 0)
                {
                    double mn = 1; for (int tau = minLag; tau <= maxLag; tau++) if (cm[tau] < mn) { mn = cm[tau]; best = tau; }
                    if (mn > 0.3) { outSt[t] = double.NaN; return; }
                }
                // parabolic interpolation
                double tauF = best;
                if (best > minLag && best < maxLag)
                {
                    double a = cm[best - 1], b = cm[best], c = cm[best + 1];
                    double den = a - 2 * b + c; if (Math.Abs(den) > 1e-12) tauF = best + 0.5 * (a - c) / den;
                }
                double f0 = fs / tauF;
                outSt[t] = 12 * Math.Log2(f0 / 440.0);
            });
            // median filter (5) to kill single-frame glitches
            var filt = new double[frames];
            for (int t = 0; t < frames; t++)
            {
                var v = new List<double>();
                for (int k = Math.Max(0, t - 2); k <= Math.Min(frames - 1, t + 2); k++) if (!double.IsNaN(outSt[k])) v.Add(outSt[k]);
                filt[t] = v.Count >= 3 ? Median(v) : double.NaN;
            }
            return (filt, (double)hop / fs);
        }

        // ---------- helpers ----------
        static float[] Mono(AudioData a)
        {
            var m = new float[a.Length];
            int ch = a.ChannelCount;
            for (long i = 0; i < a.Length; i++) { float acc = 0; for (int c = 0; c < ch; c++) acc += a.Channels[c][i]; m[i] = acc / ch; }
            return m;
        }
        static double Percentile(double[] v, double p)
        {
            if (v.Length == 0) return 0;
            var c = (double[])v.Clone(); Array.Sort(c);
            return c[Math.Clamp((int)(p * (c.Length - 1)), 0, c.Length - 1)];
        }
        static double[] Smooth(double[] v, int r)
        {
            var o = new double[v.Length];
            for (int i = 0; i < v.Length; i++)
            {
                double s = 0; int n = 0;
                for (int k = Math.Max(0, i - r); k <= Math.Min(v.Length - 1, i + r); k++) { s += v[k]; n++; }
                o[i] = s / n;
            }
            return o;
        }
        static double Median(List<double> v) { if (v.Count == 0) return double.NaN; var c = v.ToArray(); Array.Sort(c); return c.Length % 2 == 1 ? c[c.Length / 2] : (c[c.Length / 2 - 1] + c[c.Length / 2]) / 2; }
        static double Mad(List<double> v, double med) { if (v.Count == 0) return 0; return Median(v.Select(x => Math.Abs(x - med)).ToList()); }
        static List<double> Slice(double[] v, int a, int b) { var o = new List<double>(); for (int k = Math.Max(0, a); k < Math.Min(v.Length, b); k++) if (!double.IsNaN(v[k])) o.Add(v[k]); return o; }
    }
}
