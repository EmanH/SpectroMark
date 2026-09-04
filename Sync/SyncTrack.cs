namespace WavMarker.Sync
{
    /// <summary>A stretch marker: source frame in the clip maps to a local (pre-offset) timeline frame.</summary>
    public class StretchPoint
    {
        public long Source;
        public long Target;
    }

    /// <summary>
    /// One piece of the stretched timeline. Identity segments read straight from the source audio
    /// (no copy); stretched segments own a small rendered buffer with its own peak pyramid.
    /// </summary>
    public class Segment
    {
        public long SrcStart, SrcEnd;
        public long LocalStart, LocalLen;
        public float[][] Buf;            // null = identity (read from source at SrcStart + (local - LocalStart))
        public float[][] PeakMin, PeakMax;
        public bool Identity => Buf == null;
    }

    public class SyncTrack
    {
        public string Path;
        public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
        public AudioData Audio;
        public List<Marker> Markers = new();
        public long Offset;                         // timeline frame where local 0 sits
        public List<StretchPoint> Points = new();   // sorted by Source; the first is always a pin (Source == Target)
        public bool Mute, Solo;

        public List<Segment> Segments = new();
        public long RenderedLength;
        public int RenderVersion;
        public int RenderedVersion = -1;
        public bool Rendering;

        public const int PeakBlock = 256;
        public float[][] SrcPeakMin, SrcPeakMax;    // peak pyramid of the source, computed once

        readonly Dictionary<string, Segment> segCache = new();

        public long End => Offset + RenderedLength;

        public void Init()
        {
            (SrcPeakMin, SrcPeakMax) = BuildPeaks(Audio.Channels, Audio.Length);
            RenderedLength = Audio.Length;
            Segments = new List<Segment> { new Segment { SrcStart = 0, SrcEnd = Audio.Length, LocalStart = 0, LocalLen = Audio.Length } };
            RenderedVersion = RenderVersion;
        }

        static (float[][], float[][]) BuildPeaks(float[][] chans, long len)
        {
            int ch = chans.Length; int blocks = (int)((len + PeakBlock - 1) / PeakBlock);
            var pmin = new float[ch][]; var pmax = new float[ch][];
            for (int c = 0; c < ch; c++)
            {
                var mn = new float[blocks]; var mx = new float[blocks]; var src = chans[c];
                Parallel.For(0, blocks, b =>
                {
                    long i0 = (long)b * PeakBlock, i1 = Math.Min(len, i0 + PeakBlock);
                    float lo = 0, hi = 0;
                    for (long i = i0; i < i1; i++) { float v = src[i]; if (v < lo) lo = v; if (v > hi) hi = v; }
                    mn[b] = lo; mx[b] = hi;
                });
                pmin[c] = mn; pmax[c] = mx;
            }
            return (pmin, pmax);
        }

        // ---------- mapping ----------
        public long SourceToLocal(long src)
        {
            if (Points.Count == 0) return src;
            var first = Points[0];
            if (src <= first.Source) return src + (first.Target - first.Source);
            for (int i = 0; i < Points.Count - 1; i++)
            {
                var p = Points[i]; var q = Points[i + 1];
                if (src <= q.Source)
                    return p.Target + (long)Math.Round((double)(src - p.Source) * (q.Target - p.Target) / Math.Max(1, q.Source - p.Source));
            }
            var last = Points[^1];
            return src + (last.Target - last.Source);
        }

        public long LocalToSource(long local)
        {
            if (Points.Count == 0) return local;
            var first = Points[0];
            if (local <= first.Target) return local - (first.Target - first.Source);
            for (int i = 0; i < Points.Count - 1; i++)
            {
                var p = Points[i]; var q = Points[i + 1];
                if (local <= q.Target)
                    return p.Source + (long)Math.Round((double)(local - p.Target) * (q.Source - p.Source) / Math.Max(1, q.Target - p.Target));
            }
            var last = Points[^1];
            return local - (last.Target - last.Source);
        }

        public long SourceToTimeline(long src) => Offset + SourceToLocal(src);
        public long TimelineToSource(long tl) => LocalToSource(tl - Offset);

        public StretchPoint PointAt(long source) => Points.FirstOrDefault(p => p.Source == source);

        public string CheckPoint(long source, long localTarget)
        {
            foreach (var p in Points)
            {
                if (p.Source == source) continue;
                if (p.Source < source && p.Target >= localTarget) return "would fold time backwards over an earlier sync point";
                if (p.Source > source && p.Target <= localTarget) return "would fold time backwards over a later sync point";
            }
            const double ratioLimit = 4.0;
            var prev = Points.Where(p => p.Source < source).OrderBy(p => p.Source).LastOrDefault();
            var next = Points.Where(p => p.Source > source).OrderBy(p => p.Source).FirstOrDefault();
            if (prev != null) { double r = (double)(localTarget - prev.Target) / Math.Max(1, source - prev.Source); if (r > ratioLimit || r < 1 / ratioLimit) return $"stretch ratio {r:0.00} too extreme"; }
            if (next != null) { double r = (double)(next.Target - localTarget) / Math.Max(1, next.Source - source); if (r > ratioLimit || r < 1 / ratioLimit) return $"stretch ratio {r:0.00} too extreme"; }
            return null;
        }

        public void AddOrUpdatePoint(long source, long localTarget)
        {
            var existing = PointAt(source);
            if (existing != null) existing.Target = localTarget;
            else Points.Add(new StretchPoint { Source = source, Target = localTarget });
            Points.Sort((a, b) => a.Source.CompareTo(b.Source));
            NormaliseFirstPin();
            RenderVersion++;
        }

        public void RemovePoint(StretchPoint p)
        {
            Points.Remove(p);
            NormaliseFirstPin();
            RenderVersion++;
        }

        void NormaliseFirstPin()
        {
            if (Points.Count == 0) return;
            var f = Points[0];
            long shift = f.Target - f.Source;
            if (shift == 0) return;
            Offset += shift;
            foreach (var p in Points) p.Target -= shift;
        }

        // ---------- rendering (only stretched segments are ever rendered) ----------
        public void Render(StretchMode mode, int version)
        {
            var a = Audio;
            var bounds = new List<(long s, long t)> { (0, 0) };
            foreach (var p in Points) if (p.Source > 0 && p.Source < a.Length) bounds.Add((p.Source, p.Target));
            long total = SourceToLocal(a.Length);
            bounds.Add((a.Length, total));

            var segs = new List<Segment>();
            var keep = new HashSet<string>();
            for (int i = 0; i < bounds.Count - 1; i++)
            {
                var (s0, t0) = bounds[i]; var (s1, t1) = bounds[i + 1];
                long tl = t1 - t0; if (tl <= 0 || s1 <= s0) continue;
                if (tl == s1 - s0) { segs.Add(new Segment { SrcStart = s0, SrcEnd = s1, LocalStart = t0, LocalLen = tl }); continue; }
                string key = $"{s0}:{s1}:{tl}:{mode}";
                keep.Add(key);
                Segment seg;
                lock (segCache) segCache.TryGetValue(key, out seg);
                if (seg == null)
                {
                    var buf = StretchEngine.RenderSegment(a, s0, s1, tl, mode);
                    var (pmin, pmax) = BuildPeaks(buf, tl);
                    seg = new Segment { SrcStart = s0, SrcEnd = s1, LocalLen = tl, Buf = buf, PeakMin = pmin, PeakMax = pmax };
                    lock (segCache) segCache[key] = seg;
                }
                segs.Add(new Segment { SrcStart = s0, SrcEnd = s1, LocalStart = t0, LocalLen = tl, Buf = seg.Buf, PeakMin = seg.PeakMin, PeakMax = seg.PeakMax });
            }
            lock (segCache) foreach (var k in segCache.Keys.Where(k => !keep.Contains(k)).ToList()) segCache.Remove(k);
            Segments = segs; RenderedLength = total; RenderedVersion = version;
        }

        public void ClearCache() { lock (segCache) segCache.Clear(); RenderVersion++; }

        // ---------- reading ----------
        /// <summary>Adds local frames [local0, local0+n) of channel ch into dst[dstOff..] (stride 1). Frames outside the clip are skipped.</summary>
        public void AddInto(float[] dst, int dstOff, int ch, long local0, int n, int dstStride = 1)
        {
            var segs = Segments; var src = Audio.Channels[Math.Min(ch, Audio.ChannelCount - 1)];
            long end = local0 + n;
            foreach (var s in segs)
            {
                long segEnd = s.LocalStart + s.LocalLen;
                if (segEnd <= local0 || s.LocalStart >= end) continue;
                long from = Math.Max(local0, s.LocalStart), to = Math.Min(end, segEnd);
                if (s.Identity)
                {
                    long srcIdx = s.SrcStart + (from - s.LocalStart);
                    for (long i = from; i < to; i++, srcIdx++) dst[dstOff + (int)(i - local0) * dstStride] += src[srcIdx];
                }
                else
                {
                    var b = s.Buf[Math.Min(ch, s.Buf.Length - 1)];
                    long bi = from - s.LocalStart;
                    for (long i = from; i < to; i++, bi++) dst[dstOff + (int)(i - local0) * dstStride] += b[bi];
                }
            }
        }

        /// <summary>Min/max over local frames [l0, l1) across all channels, using the peak pyramids.</summary>
        public void Peak(long l0, long l1, out float mn, out float mx)
        {
            mn = 0; mx = 0;
            var segs = Segments;
            foreach (var s in segs)
            {
                long segEnd = s.LocalStart + s.LocalLen;
                if (segEnd <= l0 || s.LocalStart >= l1) continue;
                long from = Math.Max(l0, s.LocalStart), to = Math.Min(l1, segEnd);
                float[][] pmin, pmax; long b0, b1;
                if (s.Identity)
                {
                    pmin = SrcPeakMin; pmax = SrcPeakMax;
                    b0 = (s.SrcStart + (from - s.LocalStart)) / PeakBlock; b1 = (s.SrcStart + (to - 1 - s.LocalStart)) / PeakBlock;
                }
                else
                {
                    pmin = s.PeakMin; pmax = s.PeakMax;
                    b0 = (from - s.LocalStart) / PeakBlock; b1 = (to - 1 - s.LocalStart) / PeakBlock;
                }
                if (pmin == null) continue;
                for (int c = 0; c < pmin.Length; c++)
                {
                    var a = pmin[c]; var b = pmax[c];
                    long hi = Math.Min(b1, a.Length - 1);
                    for (long k = Math.Max(0, b0); k <= hi; k++) { if (a[k] < mn) mn = a[k]; if (b[k] > mx) mx = b[k]; }
                }
            }
        }
    }
}
