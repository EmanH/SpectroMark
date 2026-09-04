using System.Globalization;
using NAudio.Wave;
using WavMarker;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
if (args.Length == 0) { Usage(); return 1; }

switch (args[0])
{
    case "snip":   // snip <in.wav> <durationSec> <out.wav> [startSec]   (start auto-picked = loudest window if omitted)
    {
        string inp = args[1]; double dur = double.Parse(args[2]); string outp = args[3];
        double start = args.Length > 4 ? double.Parse(args[4]) : PickStart(inp, dur);
        Cut(inp, start, dur, outp);
        Console.WriteLine($"{Path.GetFileName(outp)}: {FormatTime(start)} - {FormatTime(start + dur)} from {Path.GetFileName(inp)}");
        return 0;
    }
    case "run":    // run <wav...>   detect and write markers into the files (replaces existing)
    {
        foreach (var f in args.Skip(1))
        {
            var a = AudioIO.Read(f);
            var ev = AutoMark.Detect(a);
            WavCues.Write(f, ev.Select((e, i) => new Marker { Sample = (long)(e.Time * a.SampleRate), Name = $"{e.Kind[0].ToString().ToUpper()}{i + 1:00}" }).ToList());
            Console.WriteLine($"{Path.GetFileName(f)}: {ev.Count} markers");
            foreach (var e in ev) Console.WriteLine("   " + e);
        }
        return 0;
    }
    case "eval":   // eval <tolMs> <wav...>   markers in file = ground truth; compare with detector
    {
        double tol = double.Parse(args[1]) / 1000.0;
        int tp = 0, fp = 0, fn = 0;
        foreach (var f in args.Skip(2))
        {
            var a = AudioIO.Read(f);
            var truth = WavCues.Read(f).Select(m => (double)m.Sample / a.SampleRate).ToList();
            var det = AutoMark.Detect(a);
            var matchedT = new bool[truth.Count]; var matchedD = new bool[det.Count];
            for (int i = 0; i < det.Count; i++)
            {
                int best = -1; double bd = tol;
                for (int j = 0; j < truth.Count; j++) if (!matchedT[j]) { double d = Math.Abs(truth[j] - det[i].Time); if (d < bd) { bd = d; best = j; } }
                if (best >= 0) { matchedT[best] = true; matchedD[i] = true; }
            }
            int ftp = matchedD.Count(b => b), ffp = det.Count - ftp, ffn = truth.Count - ftp;
            tp += ftp; fp += ffp; fn += ffn;
            Console.WriteLine($"{Path.GetFileName(f)}: truth={truth.Count} det={det.Count}  hit={ftp} extra={ffp} missed={ffn}");
            for (int j = 0; j < truth.Count; j++) if (!matchedT[j]) Console.WriteLine($"   MISSED  {FormatTime(truth[j])}");
            for (int i = 0; i < det.Count; i++) if (!matchedD[i]) Console.WriteLine($"   EXTRA   {FormatTime(det[i].Time)}  ({det[i].Kind} {det[i].Strength:0.00})");
        }
        double p = tp / (double)Math.Max(1, tp + fp), r = tp / (double)Math.Max(1, tp + fn);
        Console.WriteLine($"TOTAL hit={tp} extra={fp} missed={fn}  precision={p:0.00} recall={r:0.00} F1={(2 * p * r / Math.Max(1e-9, p + r)):0.00}");
        return 0;
    }
    case "stretchtest":  // stretchtest <wav>  : stretch a 3 s piece by several ratios, report length/energy/timing
    {
        var a = AudioIO.Read(args[1]);
        long s0 = a.SampleRate * 2, s1 = s0 + a.SampleRate * 3;
        foreach (var ratio in new[] { 1.0, 0.97, 1.03, 0.9, 1.1, 1.5 })
        {
            long target = (long)((s1 - s0) * ratio);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var seg = WavMarker.Sync.StretchEngine.RenderSegment(a, s0, s1, target, WavMarker.Sync.StretchMode.Tonal);
            sw.Stop();
            double eIn = 0; for (long i = s0; i < s1; i++) eIn += a.Channels[0][i] * a.Channels[0][i]; eIn /= (s1 - s0);
            double eOut = 0; int zerosTail = 0; for (long i = 0; i < target; i++) eOut += seg[0][i] * seg[0][i]; eOut /= target;
            for (long i = target - 1; i >= 0 && seg[0][i] == 0; i--) zerosTail++;
            Console.WriteLine($"ratio {ratio:0.00}: out len {seg[0].Length} (want {target})  rmsIn {Math.Sqrt(eIn):0.0000} rmsOut {Math.Sqrt(eOut):0.0000}  trailing zeros {zerosTail}  {sw.ElapsedMilliseconds} ms");
        }
        return 0;
    }
    case "mixtest":  // mixtest <wav> : run the sync mixer over one clip (identity) and compare with the source
    {
        var a = AudioIO.Read(args[1]);
        var t = new WavMarker.Sync.SyncTrack { Path = args[1], Audio = a }; t.Init();
        var mix = new WavMarker.Sync.SyncMixProvider(new List<WavMarker.Sync.SyncTrack> { t }, a.SampleRate, 0, t.End);
        var buf = new float[5292 * 2]; long pos = 0; double maxErr = 0; int n;
        while ((n = mix.Read(buf, 0, buf.Length)) > 0)
        {
            int frames = n / 2;
            for (int i = 0; i < frames; i++)
            {
                float l = a.Channels[0][pos + i], r = a.Channels[Math.Min(1, a.ChannelCount - 1)][pos + i];
                maxErr = Math.Max(maxErr, Math.Max(Math.Abs(buf[i * 2] - l), Math.Abs(buf[i * 2 + 1] - r)));
            }
            pos += frames;
        }
        Console.WriteLine($"frames read {pos} of {a.Length}, max abs error vs source {maxErr}");
        // now with a sync point: stretch 2..5 s to 2..5.3 s and check continuity
        t.AddOrUpdatePoint(a.SampleRate * 2, a.SampleRate * 2);
        t.AddOrUpdatePoint(a.SampleRate * 5, (long)(a.SampleRate * 5.3));
        t.Render(WavMarker.Sync.StretchMode.Tonal, t.RenderVersion);
        Console.WriteLine($"segments: {string.Join(" | ", t.Segments.Select(sg => $"src {sg.SrcStart}-{sg.SrcEnd} -> local {sg.LocalStart}+{sg.LocalLen} {(sg.Identity ? "identity" : "stretched")}"))}");
        mix = new WavMarker.Sync.SyncMixProvider(new List<WavMarker.Sync.SyncTrack> { t }, a.SampleRate, 0, t.End);
        pos = 0; double e = 0; long cnt = 0;
        while ((n = mix.Read(buf, 0, buf.Length)) > 0) { for (int i = 0; i < n; i += 2) { e += buf[i] * buf[i]; cnt++; } pos += n / 2; }
        Console.WriteLine($"stretched: frames read {pos} of {t.End}, rms {Math.Sqrt(e / cnt):0.0000}");
        return 0;
    }
    case "list":
    {
        foreach (var f in args.Skip(1))
        {
            var a = AudioIO.Read(f);
            Console.WriteLine(Path.GetFileName(f));
            foreach (var m in WavCues.Read(f)) Console.WriteLine($"   {FormatTime((double)m.Sample / a.SampleRate)}  {m.Name}");
        }
        return 0;
    }
    default: Usage(); return 1;
}

static void Usage() => Console.WriteLine("AutoMarkCli snip <in> <dur> <out> [start] | run <wav...> | eval <tolMs> <wav...> | list <wav...>");

static string FormatTime(double s) { int m = (int)(s / 60); return $"{m}:{s - m * 60:00.000}"; }

static double PickStart(string path, double dur)
{
    var a = AudioIO.Read(path);
    int sr = a.SampleRate; int blk = sr / 10; // 100 ms blocks
    int nb = (int)(a.Length / blk);
    var rms = new double[nb];
    for (int b = 0; b < nb; b++)
    {
        double e = 0;
        for (int c = 0; c < a.ChannelCount; c++) for (int i = 0; i < blk; i++) { float v = a.Channels[c][(long)b * blk + i]; e += v * v; }
        rms[b] = Math.Sqrt(e / (blk * a.ChannelCount));
    }
    // "active" blocks = above 10% of peak rms; choose the window with the most active blocks (ties -> earliest)
    double peak = rms.Max(); var act = rms.Select(v => v > peak * 0.1 ? 1 : 0).ToArray();
    int wb = (int)(dur * 10); int best = 0, bestScore = -1; int run = 0;
    for (int b = 0; b < nb; b++)
    {
        run += act[b]; if (b >= wb) run -= act[b - wb];
        if (b >= wb - 1 && run > bestScore) { bestScore = run; best = b - wb + 1; }
    }
    return best / 10.0;
}

static void Cut(string inp, double start, double dur, string outp)
{
    using var r = new WaveFileReader(inp);
    int ba = r.WaveFormat.BlockAlign;
    long s0 = (long)(start * r.WaveFormat.SampleRate) * ba, len = (long)(dur * r.WaveFormat.SampleRate) * ba;
    s0 = Math.Clamp(s0, 0, r.Length); len = Math.Min(len, r.Length - s0);
    using var w = new WaveFileWriter(outp, r.WaveFormat);
    r.Position = s0;
    var buf = new byte[ba * 4096]; long left = len;
    while (left > 0) { int n = r.Read(buf, 0, (int)Math.Min(buf.Length, left)); if (n <= 0) break; w.Write(buf, 0, n); left -= n; }
}
