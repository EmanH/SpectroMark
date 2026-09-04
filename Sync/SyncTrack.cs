using System.IO;

namespace WavMarker.Sync
{
    /// <summary>A stretch marker: source frame in the clip maps to a local (pre-offset) timeline frame.</summary>
    public class StretchPoint
    {
        public long Source;
        public long Target;
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

        // rendered (stretched) audio in local time
        public float[][] Rendered;
        public long RenderedLength;
        public int RenderVersion;                   // bumps on every model change
        public int RenderedVersion = -1;
        public bool Rendering;

        // peak pyramid of the rendered audio (per channel, per 256-frame block: min,max)
        public const int PeakBlock = 256;
        public float[][] PeakMin, PeakMax;

        readonly Dictionary<string, float[][]> segCache = new();

        public long End => Offset + RenderedLength;

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

        /// <summary>Can a point (source -> localTarget) be inserted keeping time monotonic? Returns null if ok, else reason.</summary>
        public string CheckPoint(long source, long localTarget)
        {
            foreach (var p in Points)
            {
                if (p.Source == source) continue;
                if (p.Source < source && p.Target >= localTarget) return "would fold time backwards over an earlier sync point";
                if (p.Source > source && p.Target <= localTarget) return "would fold time backwards over a later sync point";
            }
            double ratioLimit = 4.0;
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

        /// <summary>Keep the invariant: the first point is a pin (no stretch before it), absorbing any shift into Offset.</summary>
        void NormaliseFirstPin()
        {
            if (Points.Count == 0) return;
            var f = Points[0];
            long shift = f.Target - f.Source;
            if (shift == 0) return;
            Offset += shift;
            foreach (var p in Points) p.Target -= shift;
        }

        // ---------- rendering ----------
        public long ComputeRenderedLength() => SourceToLocal(Audio.Length);

        /// <summary>Full render from segment cache. Safe to call on a worker thread; swaps buffers at the end.</summary>
        public void Render(StretchMode mode, int version)
        {
            var a = Audio; int ch = a.ChannelCount;
            long total = ComputeRenderedLength();
            var outp = new float[ch][];
            for (int c = 0; c < ch; c++) outp[c] = new float[total];

            // segment boundaries in source: 0, points..., length
            var bounds = new List<(long s, long t)> { (0, SourceToLocal(0)) };
            foreach (var p in Points) if (p.Source > 0 && p.Source < a.Length) bounds.Add((p.Source, p.Target));
            bounds.Add((a.Length, total));

            var keep = new HashSet<string>();
            for (int i = 0; i < bounds.Count - 1; i++)
            {
                var (s0, t0) = bounds[i]; var (s1, t1) = bounds[i + 1];
                long tl = t1 - t0; if (tl <= 0 || s1 <= s0) continue;
                string key = $"{s0}:{s1}:{tl}:{mode}";
                keep.Add(key);
                float[][] seg;
                lock (segCache) segCache.TryGetValue(key, out seg);
                if (seg == null)
                {
                    seg = StretchEngine.RenderSegment(a, s0, s1, tl, mode);
                    lock (segCache) segCache[key] = seg;
                }
                for (int c = 0; c < ch; c++) Array.Copy(seg[c], 0, outp[c], t0, Math.Min(tl, seg[c].Length));
                // short crossfade across the join to hide any phase discontinuity
                if (i > 0)
                {
                    int xf = (int)Math.Min(a.SampleRate / 200, Math.Min(tl, t0)); // 5 ms
                    for (int c = 0; c < ch; c++)
                        for (int k = 0; k < xf; k++)
                        {
                            double w = (k + 1.0) / (xf + 1);
                            long idx = t0 - xf / 2 + k;
                            if (idx <= 0 || idx >= total) continue;
                            // blend what is already there (previous segment tail) with this segment's start region
                            float prevV = outp[c][idx];
                            long segIdx = idx - t0;
                            float curV = segIdx >= 0 && segIdx < seg[c].Length ? seg[c][segIdx] : prevV;
                            outp[c][idx] = (float)(prevV * (1 - w) + curV * w);
                        }
                }
            }
            lock (segCache) foreach (var k in segCache.Keys.Where(k => !keep.Contains(k)).ToList()) segCache.Remove(k);

            // peaks
            int blocks = (int)((total + PeakBlock - 1) / PeakBlock);
            var pmin = new float[ch][]; var pmax = new float[ch][];
            for (int c = 0; c < ch; c++)
            {
                pmin[c] = new float[blocks]; pmax[c] = new float[blocks];
                var src = outp[c];
                Parallel.For(0, blocks, b =>
                {
                    long i0 = (long)b * PeakBlock, i1 = Math.Min(total, i0 + PeakBlock);
                    float mn = 0, mx = 0;
                    for (long i = i0; i < i1; i++) { float v = src[i]; if (v < mn) mn = v; if (v > mx) mx = v; }
                    pmin[c][b] = mn; pmax[c][b] = mx;
                });
            }
            Rendered = outp; RenderedLength = total; PeakMin = pmin; PeakMax = pmax; RenderedVersion = version;
        }

        public void ClearCache() { lock (segCache) segCache.Clear(); RenderVersion++; }
    }
}
