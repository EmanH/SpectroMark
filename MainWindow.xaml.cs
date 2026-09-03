using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using NAudio.Dsp;
using NAudio.Wave;

namespace WavMarker
{
    public class Marker
    {
        public long Sample;
        public string Name = "";
    }

    class AudioData
    {
        public float[][] Channels;
        public int SampleRate;
        public long Length;
        public int ChannelCount => Channels.Length;
        public double Duration => (double)Length / SampleRate;
    }

    class Spectrogram
    {
        public byte[][] Data;    // per channel: frames * bins, 0..255 dB-scaled
        public int Frames, Bins, Fft, Hop;
        public float MaxDb;
    }

    class BufferProvider : ISampleProvider
    {
        readonly AudioData d;
        public long Position;
        public BufferProvider(AudioData d) { this.d = d; WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(d.SampleRate, d.ChannelCount); }
        public WaveFormat WaveFormat { get; }
        public int Read(float[] buffer, int offset, int count)
        {
            int ch = d.ChannelCount;
            int frames = count / ch;
            long avail = d.Length - Position;
            if (avail <= 0) return 0;
            if (frames > avail) frames = (int)avail;
            for (int i = 0; i < frames; i++)
                for (int c = 0; c < ch; c++)
                    buffer[offset + i * ch + c] = d.Channels[c][Position + i];
            Position += frames;
            return frames * ch;
        }
    }

    public partial class MainWindow : Window
    {
        AudioData audio;
        Spectrogram spec;
        string filePath;
        readonly List<Marker> markers = new();
        Marker selectedMarker;

        // view (in samples)
        double viewStart, viewLen;

        // playback
        WaveOutEvent waveOut;
        BufferProvider provider;
        long playhead;
        long playStartSample;
        bool playing;
        readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(25) };

        // rendering
        WriteableBitmap specBmp, ovBmp;
        int[] specPx, ovPx;
        readonly int[] lut = BuildLut();
        double dpiScale = 1.0;
        readonly DispatcherTimer renderTimer = new() { Interval = TimeSpan.FromMilliseconds(15) };
        bool specDirty, ovDirty;

        // overlay elements
        readonly Line specPlayhead = new() { Stroke = Brushes.White, StrokeThickness = 1.5, IsHitTestVisible = false };
        readonly Line ovPlayhead = new() { Stroke = Brushes.White, StrokeThickness = 1.5 };
        readonly Rectangle ovViewRect = new() { Fill = new SolidColorBrush(Color.FromArgb(50, 120, 180, 255)), Stroke = new SolidColorBrush(Color.FromArgb(180, 120, 180, 255)), StrokeThickness = 1 };
        readonly TextBlock hoverText = new() { Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), FontFamily = new FontFamily("Consolas"), FontSize = 12, Padding = new Thickness(4, 1, 4, 1) };
        static readonly Brush MarkerBrush = new SolidColorBrush(Color.FromRgb(255, 240, 60));
        static readonly Brush MarkerSelBrush = new SolidColorBrush(Color.FromRgb(90, 255, 255));

        // mouse state
        bool scrubbing, panning, draggingMarker, ovDragging;
        double panLastX, ovDragOffset;
        bool wasPlayingBeforeScrub;

        public MainWindow()
        {
            InitializeComponent();
            timer.Tick += (_, _) => UpdatePlayhead();
            renderTimer.Tick += (_, _) => { renderTimer.Stop(); if (specDirty) RenderSpectrogram(); if (ovDirty) RenderOverview(); specDirty = ovDirty = false; };
            SpecHost.SizeChanged += (_, _) => { InvalidateSpec(); RefreshOverlays(); };
            OverviewHost.SizeChanged += (_, _) => { InvalidateOverview(); RefreshOverlays(); };
            Ruler.SizeChanged += (_, _) => DrawRuler();
            Loaded += (_, _) =>
            {
                var src = PresentationSource.FromVisual(this);
                if (src?.CompositionTarget != null) dpiScale = src.CompositionTarget.TransformToDevice.M11;
                var args = Environment.GetCommandLineArgs();
                if (args.Length > 1 && File.Exists(args[1])) _ = LoadFile(args[1]);
            };
            Closing += (_, _) => StopPlayback();
        }

        // ---------------- file loading ----------------

        async void Open_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Audio files|*.wav;*.flac;*.mp3;*.aif;*.aiff|All files|*.*" };
            if (dlg.ShowDialog() == true) await LoadFile(dlg.FileName);
        }

        void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) _ = LoadFile(files[0]);
        }

        async Task LoadFile(string path)
        {
            StopPlayback();
            InfoText.Text = "Loading " + System.IO.Path.GetFileName(path) + " ...";
            try
            {
                var data = await Task.Run(() => ReadAudio(path));
                audio = data; filePath = path; playhead = 0; spec = null;
                viewStart = 0; viewLen = audio.Length;
                markers.Clear(); selectedMarker = null;
                Title = "SpectroMark - " + System.IO.Path.GetFileName(path);
                string chDesc = audio.ChannelCount switch { 1 => "MONO (1 channel)", 2 => "STEREO (2 channels)", _ => audio.ChannelCount + " channels" };
                InfoText.Text = $"{System.IO.Path.GetFileName(path)}   |   {chDesc}   |   {audio.SampleRate} Hz   |   {FormatTime(audio.Duration)}   |   analysing spectrogram...";
                InvalidateOverview(); InvalidateSpec(); UpdateScrollBar(); DrawRuler(); RefreshOverlays(); UpdateTimeText();
                LoadMarkersFile();
                var s = await Task.Run(() => ComputeSpectrogram(audio));
                if (audio != data) return;
                spec = s;
                InfoText.Text = $"{System.IO.Path.GetFileName(path)}   |   {chDesc}   |   {audio.SampleRate} Hz   |   {FormatTime(audio.Duration)}";
                InvalidateSpec(); RefreshOverlays();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open file:\n" + ex.Message, "SpectroMark", MessageBoxButton.OK, MessageBoxImage.Error);
                InfoText.Text = "Failed to load.";
            }
        }

        static AudioData ReadAudio(string path)
        {
            using var reader = new AudioFileReader(path);
            int ch = reader.WaveFormat.Channels;
            int sr = reader.WaveFormat.SampleRate;
            long totalFrames = reader.Length / (reader.WaveFormat.BitsPerSample / 8) / ch;
            var chans = new float[ch][];
            for (int c = 0; c < ch; c++) chans[c] = new float[totalFrames + 4096];
            var buf = new float[sr * ch];
            long pos = 0; int n;
            while ((n = reader.Read(buf, 0, buf.Length)) > 0)
            {
                int frames = n / ch;
                if (pos + frames > chans[0].Length)
                    for (int c = 0; c < ch; c++) Array.Resize(ref chans[c], (int)(pos + frames + 65536));
                for (int i = 0; i < frames; i++)
                    for (int c = 0; c < ch; c++) chans[c][pos + i] = buf[i * ch + c];
                pos += frames;
            }
            for (int c = 0; c < ch; c++) Array.Resize(ref chans[c], (int)pos);
            return new AudioData { Channels = chans, SampleRate = sr, Length = pos };
        }

        // ---------------- spectrogram analysis ----------------

        static Spectrogram ComputeSpectrogram(AudioData a)
        {
            const int fft = 2048, m = 11;
            int hop = 512;
            while ((a.Length / hop) * (fft / 2) * (long)a.ChannelCount > 400_000_000L) hop *= 2;
            int bins = fft / 2;
            int frames = (int)Math.Max(1, (a.Length - fft) / hop + 1);
            var window = new float[fft];
            for (int i = 0; i < fft; i++) window[i] = (float)FastFourierTransform.HannWindow(i, fft);
            var result = new byte[a.ChannelCount][];
            var dbAll = new float[a.ChannelCount][];
            float globalMax = -200;
            object lockObj = new();
            for (int c = 0; c < a.ChannelCount; c++)
            {
                var src = a.Channels[c];
                var db = new float[(long)frames * bins];
                float localMax = -200;
                Parallel.For(0, frames, () => (buf: new Complex[fft], max: -200f), (f, _, st) =>
                {
                    var buf = st.buf;
                    long off = (long)f * hop;
                    for (int i = 0; i < fft; i++) { buf[i].X = (off + i < src.Length ? src[off + i] : 0f) * window[i]; buf[i].Y = 0; }
                    FastFourierTransform.FFT(true, m, buf);
                    long o = (long)f * bins;
                    float mx = st.max;
                    for (int b = 0; b < bins; b++)
                    {
                        float mag = buf[b].X * buf[b].X + buf[b].Y * buf[b].Y;
                        float d = 10f * MathF.Log10(mag + 1e-20f);
                        db[o + b] = d; if (d > mx) mx = d;
                    }
                    return (buf, mx);
                }, st => { lock (lockObj) { if (st.max > localMax) localMax = st.max; } });
                dbAll[c] = db;
                if (localMax > globalMax) globalMax = localMax;
            }
            // store relative dB in 0.5 dB steps: 255 = peak, 0 = -127.5 dB
            for (int c = 0; c < a.ChannelCount; c++)
            {
                var db = dbAll[c];
                var bytes = new byte[db.Length];
                float gm = globalMax;
                Parallel.For(0, frames, f =>
                {
                    long o = (long)f * bins;
                    for (int b = 0; b < bins; b++)
                    {
                        float rel = db[o + b] - gm;
                        int v = (int)(255 + rel * 2f);
                        bytes[o + b] = (byte)(v < 0 ? 0 : v);
                    }
                });
                result[c] = bytes;
                dbAll[c] = null;
            }
            return new Spectrogram { Data = result, Frames = frames, Bins = bins, Fft = fft, Hop = hop, MaxDb = globalMax };
        }

        // Audition-style heat map: black -> blue -> purple -> red -> orange -> yellow -> white
        static int[] BuildLut()
        {
            var stops = new (double p, int r, int g, int b)[] {
                (0.00, 0, 0, 0), (0.12, 5, 5, 60), (0.28, 40, 0, 130), (0.45, 130, 10, 140),
                (0.60, 220, 30, 60), (0.74, 255, 120, 10), (0.88, 255, 225, 40), (1.00, 255, 255, 255) };
            var lut = new int[256];
            for (int i = 0; i < 256; i++)
            {
                double p = i / 255.0; int k = 0;
                while (k < stops.Length - 2 && p > stops[k + 1].p) k++;
                double t = (p - stops[k].p) / (stops[k + 1].p - stops[k].p);
                int r = (int)(stops[k].r + (stops[k + 1].r - stops[k].r) * t);
                int g = (int)(stops[k].g + (stops[k + 1].g - stops[k].g) * t);
                int b = (int)(stops[k].b + (stops[k + 1].b - stops[k].b) * t);
                lut[i] = (255 << 24) | (r << 16) | (g << 8) | b;
            }
            return lut;
        }

        // ---------------- rendering ----------------

        void InvalidateSpec() { specDirty = true; if (!renderTimer.IsEnabled) renderTimer.Start(); }
        void InvalidateOverview() { ovDirty = true; if (!renderTimer.IsEnabled) renderTimer.Start(); }

        void Spec_SettingChanged(object sender, RoutedEventArgs e) { if (IsLoaded) { InvalidateSpec(); RefreshOverlays(); } }

        void RenderSpectrogram()
        {
            int W = (int)(SpecHost.ActualWidth * dpiScale), H = (int)(SpecHost.ActualHeight * dpiScale);
            if (W < 2 || H < 2) return;
            if (specBmp == null || specBmp.PixelWidth != W || specBmp.PixelHeight != H)
            {
                specBmp = new WriteableBitmap(W, H, 96, 96, PixelFormats.Bgra32, null);
                specPx = new int[W * H];
                SpecImage.Source = specBmp;
            }
            if (spec == null || audio == null) { Array.Clear(specPx); specBmp.WritePixels(new Int32Rect(0, 0, W, H), specPx, W * 4, 0); return; }

            var s = spec; int nch = s.Data.Length;
            int bandH = H / nch;
            bool logF = LogFreqChk.IsChecked == true;
            double nyq = audio.SampleRate / 2.0, fmin = 20;
            double floorDb = FloorSlider.Value;
            int floorByte = (int)Math.Max(0, 255 + floorDb * 2);
            var gain = new int[256];
            for (int i = 0; i < 256; i++) gain[i] = (int)Math.Clamp((i - floorByte) * 255.0 / Math.Max(1, 255 - floorByte), 0, 255);

            var fx = new int[W + 1];
            for (int x = 0; x <= W; x++)
            {
                double smp = viewStart + (double)x / W * viewLen;
                if (smp > audio.Length) { fx[x] = -1; continue; }
                int f = (int)Math.Round((smp - s.Fft / 2.0) / s.Hop);
                fx[x] = Math.Clamp(f, 0, s.Frames - 1);
            }
            var by = new int[bandH + 1];
            for (int y = 0; y <= bandH; y++)
            {
                double frac = 1.0 - (double)y / bandH;
                double freq = logF ? fmin * Math.Pow(nyq / fmin, frac) : frac * nyq;
                by[y] = Math.Clamp((int)(freq / nyq * s.Bins), 0, s.Bins - 1);
            }
            int bins = s.Bins;
            var px = specPx;
            Parallel.For(0, W, x =>
            {
                int f0 = fx[x];
                if (f0 < 0) { for (int y = 0; y < H; y++) px[y * W + x] = unchecked((int)0xFF000000); return; }
                int f1 = fx[x + 1] < 0 ? f0 : Math.Max(fx[x + 1], f0);
                if (f1 - f0 > 64) f1 = f0 + 64;
                for (int c = 0; c < nch; c++)
                {
                    var data = s.Data[c];
                    int top = c * bandH;
                    for (int y = 0; y < bandH; y++)
                    {
                        int b0 = by[y + 1], b1 = Math.Max(by[y], b0);
                        int mx = 0;
                        for (int f = f0; f <= f1; f++)
                        {
                            long o = (long)f * bins;
                            for (int b = b0; b <= b1; b++) { int v = data[o + b]; if (v > mx) mx = v; }
                        }
                        px[(top + y) * W + x] = lut[gain[mx]];
                    }
                }
                for (int c = 1; c < nch; c++) px[(c * bandH) * W + x] = unchecked((int)0xFF404040);
                for (int y = nch * bandH; y < H; y++) px[y * W + x] = unchecked((int)0xFF000000);
            });
            specBmp.WritePixels(new Int32Rect(0, 0, W, H), specPx, W * 4, 0);
        }

        void RenderOverview()
        {
            int W = (int)(OverviewHost.ActualWidth * dpiScale), H = (int)(OverviewHost.ActualHeight * dpiScale);
            if (W < 2 || H < 2) return;
            if (ovBmp == null || ovBmp.PixelWidth != W || ovBmp.PixelHeight != H)
            {
                ovBmp = new WriteableBitmap(W, H, 96, 96, PixelFormats.Bgra32, null);
                ovPx = new int[W * H];
                OverviewImage.Source = ovBmp;
            }
            Array.Fill(ovPx, unchecked((int)0xFF0C0C0C));
            if (audio != null)
            {
                int nch = audio.ChannelCount; int bandH = H / nch;
                int col = unchecked((int)0xFF3FA9F5), mid = unchecked((int)0xFF2A2A2A);
                var px = ovPx;
                for (int c = 0; c < nch; c++)
                {
                    var src = audio.Channels[c]; int top = c * bandH; int center = top + bandH / 2;
                    for (int x = 0; x < W; x++) px[center * W + x] = mid;
                    Parallel.For(0, W, x =>
                    {
                        long s0 = (long)((double)x / W * audio.Length), s1 = (long)((double)(x + 1) / W * audio.Length);
                        if (s1 <= s0) s1 = s0 + 1;
                        if (s1 > audio.Length) s1 = audio.Length;
                        float mn = 0, mx = 0;
                        long step = Math.Max(1, (s1 - s0) / 2000);
                        for (long i = s0; i < s1; i += step) { float v = src[i]; if (v < mn) mn = v; if (v > mx) mx = v; }
                        int y0 = center - (int)(mx * (bandH / 2 - 1)), y1 = center - (int)(mn * (bandH / 2 - 1));
                        for (int y = Math.Max(top, y0); y <= Math.Min(top + bandH - 1, y1); y++) px[y * W + x] = col;
                    });
                }
            }
            ovBmp.WritePixels(new Int32Rect(0, 0, W, H), ovPx, W * 4, 0);
        }

        // ---------------- overlays ----------------

        double SpecW => SpecHost.ActualWidth;
        double SampleToX(double smp) => (smp - viewStart) / viewLen * SpecW;
        double XToSample(double x) => viewStart + x / SpecW * viewLen;
        double OvSampleToX(double smp) => audio == null ? 0 : smp / audio.Length * OverviewHost.ActualWidth;
        double OvXToSample(double x) => audio == null ? 0 : x / OverviewHost.ActualWidth * audio.Length;

        void RefreshOverlays()
        {
            SpecOverlay.Children.Clear();
            OverviewOverlay.Children.Clear();
            if (audio == null) return;
            double H = SpecHost.ActualHeight, OH = OverviewHost.ActualHeight;

            foreach (var mk in markers)
            {
                bool sel = mk == selectedMarker;
                double x = SampleToX(mk.Sample);
                if (x >= -2 && x <= SpecW + 2)
                {
                    SpecOverlay.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = H, Stroke = sel ? MarkerSelBrush : MarkerBrush, StrokeThickness = sel ? 3 : 2 });
                    var flag = new Polygon { Points = new PointCollection { new Point(x, 0), new Point(x + 12, 0), new Point(x + 12, 10), new Point(x, 16) }, Fill = sel ? MarkerSelBrush : MarkerBrush };
                    SpecOverlay.Children.Add(flag);
                    if (!string.IsNullOrEmpty(mk.Name))
                    {
                        var tb = new TextBlock { Text = mk.Name, Foreground = sel ? MarkerSelBrush : MarkerBrush, FontSize = 11 };
                        Canvas.SetLeft(tb, x + 14); Canvas.SetTop(tb, 0); SpecOverlay.Children.Add(tb);
                    }
                }
                double ox = OvSampleToX(mk.Sample);
                OverviewOverlay.Children.Add(new Line { X1 = ox, X2 = ox, Y1 = 0, Y2 = OH, Stroke = sel ? MarkerSelBrush : MarkerBrush, StrokeThickness = 1 });
            }

            if (spec != null)
            {
                int nch = audio.ChannelCount; double bandH = H / nch;
                bool logF = LogFreqChk.IsChecked == true; double nyq = audio.SampleRate / 2.0;
                double[] freqs = logF ? new double[] { 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 } : new double[] { 1000, 2000, 4000, 6000, 8000, 10000, 12000, 15000, 20000 };
                for (int c = 0; c < nch; c++)
                {
                    foreach (var f in freqs)
                    {
                        if (f >= nyq) continue;
                        double frac = logF ? Math.Log(f / 20.0) / Math.Log(nyq / 20.0) : f / nyq;
                        double y = c * bandH + (1 - frac) * bandH;
                        var tb = new TextBlock { Text = f >= 1000 ? (f / 1000.0).ToString("0.#") + "k" : f.ToString(), Foreground = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), FontSize = 10 };
                        Canvas.SetLeft(tb, 3); Canvas.SetTop(tb, y - 7); SpecOverlay.Children.Add(tb);
                    }
                    var lbl = new TextBlock { Text = nch == 1 ? "MONO" : (c == 0 ? "L" : c == 1 ? "R" : "Ch " + (c + 1)), Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 12, Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), Padding = new Thickness(3, 0, 3, 0) };
                    Canvas.SetRight(lbl, 4); Canvas.SetTop(lbl, c * bandH + 4); SpecOverlay.Children.Add(lbl);
                }
            }

            ovViewRect.Width = Math.Max(2, OvSampleToX(viewStart + viewLen) - OvSampleToX(viewStart));
            ovViewRect.Height = OH; Canvas.SetLeft(ovViewRect, OvSampleToX(viewStart)); Canvas.SetTop(ovViewRect, 0);
            OverviewOverlay.Children.Add(ovViewRect);

            specPlayhead.Y1 = 0; specPlayhead.Y2 = H; SpecOverlay.Children.Add(specPlayhead);
            ovPlayhead.Y1 = 0; ovPlayhead.Y2 = OH; OverviewOverlay.Children.Add(ovPlayhead);
            SpecOverlay.Children.Add(hoverText); hoverText.Visibility = Visibility.Collapsed;
            PositionPlayheads();
        }

        void PositionPlayheads()
        {
            double x = SampleToX(playhead);
            specPlayhead.X1 = specPlayhead.X2 = x;
            specPlayhead.Visibility = (x >= 0 && x <= SpecW) ? Visibility.Visible : Visibility.Collapsed;
            double ox = OvSampleToX(playhead);
            ovPlayhead.X1 = ovPlayhead.X2 = ox;
        }

        void DrawRuler()
        {
            Ruler.Children.Clear();
            if (audio == null) return;
            double W = Ruler.ActualWidth; if (W < 10) return;
            double secs = viewLen / audio.SampleRate;
            double[] steps = { 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 };
            double step = steps[^1];
            foreach (var st in steps) if (st / secs * W >= 70) { step = st; break; }
            double t0 = Math.Floor(viewStart / audio.SampleRate / step) * step;
            for (double t = t0; t <= (viewStart + viewLen) / audio.SampleRate; t += step)
            {
                double x = SampleToX(t * audio.SampleRate);
                if (x < 0) continue;
                Ruler.Children.Add(new Line { X1 = x, X2 = x, Y1 = 12, Y2 = 20, Stroke = Brushes.Gray, StrokeThickness = 1 });
                var tb = new TextBlock { Text = FormatTime(t, step < 1), Foreground = Brushes.LightGray, FontSize = 10 };
                Canvas.SetLeft(tb, x + 3); Canvas.SetTop(tb, 0); Ruler.Children.Add(tb);
            }
        }

        void ViewChanged()
        {
            if (audio == null) return;
            double minLen = audio.SampleRate * 0.2;
            viewLen = Math.Clamp(viewLen, minLen, audio.Length);
            viewStart = Math.Clamp(viewStart, 0, Math.Max(0, audio.Length - viewLen));
            InvalidateSpec(); DrawRuler(); RefreshOverlays(); UpdateScrollBar();
        }

        void UpdateScrollBar()
        {
            if (audio == null) return;
            HScroll.Minimum = 0; HScroll.Maximum = Math.Max(0, audio.Length - viewLen);
            HScroll.ViewportSize = viewLen; HScroll.LargeChange = viewLen * 0.9; HScroll.SmallChange = viewLen * 0.1;
            HScroll.Value = viewStart;
        }

        void HScroll_Scroll(object sender, ScrollEventArgs e) { viewStart = e.NewValue; ViewChanged(); }

        static string FormatTime(double secs, bool ms = true)
        {
            if (secs < 0) secs = 0;
            int m = (int)(secs / 60); double s = secs - m * 60;
            return ms ? $"{m}:{s:00.000}" : $"{m}:{s:00}";
        }

        void UpdateTimeText()
        {
            if (audio == null) { TimeText.Text = "0:00.000 / 0:00.000"; return; }
            TimeText.Text = $"{FormatTime((double)playhead / audio.SampleRate)} / {FormatTime(audio.Duration)}";
        }

        // ---------------- playback ----------------

        void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

        void TogglePlay()
        {
            if (audio == null) return;
            if (playing) StopPlayback(); else StartPlayback();
        }

        void StartPlayback()
        {
            if (audio == null) return;
            if (playhead >= audio.Length - 1) playhead = 0;
            provider = new BufferProvider(audio) { Position = playhead };
            var w = new WaveOutEvent { DesiredLatency = 120, NumberOfBuffers = 3 };
            w.Init(provider);
            w.PlaybackStopped += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (waveOut == w && playing) { StopPlayback(); playhead = audio.Length; PositionPlayheads(); UpdateTimeText(); }
            });
            waveOut = w;
            playStartSample = playhead;
            w.Play();
            playing = true; timer.Start();
            PlayBtn.Content = "Pause  (Space)";
        }

        void StopPlayback()
        {
            if (waveOut != null)
            {
                if (playing) playhead = CurrentPlaybackSample();
                var w = waveOut; waveOut = null;
                try { w.Stop(); w.Dispose(); } catch { }
            }
            playing = false; timer.Stop();
            PlayBtn.Content = "Play  (Space)";
        }

        long CurrentPlaybackSample()
        {
            if (!playing || waveOut == null || audio == null) return playhead;
            long bytes;
            try { bytes = waveOut.GetPosition(); } catch { return playhead; }
            long frames = bytes / (4 * audio.ChannelCount);
            return Math.Min(audio.Length, playStartSample + frames);
        }

        void UpdatePlayhead()
        {
            if (!playing) return;
            playhead = CurrentPlaybackSample();
            if (!scrubbing && (playhead > viewStart + viewLen || playhead < viewStart))
            {
                viewStart = playhead - viewLen * 0.05;
                ViewChanged();
            }
            PositionPlayheads(); UpdateTimeText();
        }

        void SeekTo(double sample)
        {
            if (audio == null) return;
            sample = Math.Clamp(sample, 0, audio.Length);
            bool wasPlaying = playing;
            if (wasPlaying) StopPlayback();
            playhead = (long)sample;
            if (wasPlaying) StartPlayback();
            PositionPlayheads(); UpdateTimeText();
        }

        // ---------------- markers ----------------

        void Marker_Click(object sender, RoutedEventArgs e) => DropMarker();

        void DropMarker()
        {
            if (audio == null) return;
            long pos = playing ? CurrentPlaybackSample() : playhead;
            var mk = new Marker { Sample = pos };
            markers.Add(mk);
            markers.Sort((a, b) => a.Sample.CompareTo(b.Sample));
            selectedMarker = mk;
            RefreshMarkerList(); RefreshOverlays();
            FlashMarker(mk);
        }

        void FlashMarker(Marker mk)
        {
            double x = SampleToX(mk.Sample);
            var glow = new Line { X1 = x, X2 = x, Y1 = 0, Y2 = SpecHost.ActualHeight, Stroke = Brushes.White, StrokeThickness = 14, Opacity = 0.9, IsHitTestVisible = false };
            SpecOverlay.Children.Add(glow);
            var anim = new DoubleAnimation(0.9, 0, TimeSpan.FromMilliseconds(700)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            anim.Completed += (_, _) => SpecOverlay.Children.Remove(glow);
            glow.BeginAnimation(OpacityProperty, anim);
            glow.BeginAnimation(Line.StrokeThicknessProperty, new DoubleAnimation(14, 3, TimeSpan.FromMilliseconds(700)));
        }

        void RefreshMarkerList()
        {
            MarkerList.SelectionChanged -= MarkerList_SelectionChanged;
            MarkerList.Items.Clear();
            int i = 1;
            foreach (var mk in markers)
                MarkerList.Items.Add($"{i++,2}  {FormatTime((double)mk.Sample / audio.SampleRate)}{(string.IsNullOrEmpty(mk.Name) ? "" : "  " + mk.Name)}");
            MarkerList.SelectedIndex = selectedMarker == null ? -1 : markers.IndexOf(selectedMarker);
            MarkerList.SelectionChanged += MarkerList_SelectionChanged;
        }

        void MarkerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            selectedMarker = MarkerList.SelectedIndex >= 0 && MarkerList.SelectedIndex < markers.Count ? markers[MarkerList.SelectedIndex] : null;
            RefreshOverlays();
        }

        void MarkerList_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (selectedMarker == null) return;
            SeekTo(selectedMarker.Sample);
            EnsurePlayheadVisible();
        }

        void DeleteMarker_Click(object sender, RoutedEventArgs e) => DeleteSelected();

        void DeleteSelected()
        {
            if (selectedMarker == null) return;
            markers.Remove(selectedMarker); selectedMarker = null;
            RefreshMarkerList(); RefreshOverlays();
        }

        void ClearMarkers_Click(object sender, RoutedEventArgs e)
        {
            if (markers.Count == 0) return;
            if (MessageBox.Show("Remove all markers?", "SpectroMark", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            markers.Clear(); selectedMarker = null; RefreshMarkerList(); RefreshOverlays();
        }

        string MarkersPath => filePath == null ? null : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(filePath), System.IO.Path.GetFileNameWithoutExtension(filePath) + "_markers.csv");

        // Adobe Audition-compatible tab-separated marker file
        void SaveMarkers_Click(object sender, RoutedEventArgs e)
        {
            if (audio == null) return;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Name\tStart\tDuration\tTime Format\tType\tDescription");
            int i = 1;
            foreach (var mk in markers)
            {
                string name = string.IsNullOrEmpty(mk.Name) ? $"Marker {i:00}" : mk.Name;
                sb.AppendLine($"{name}\t{FormatTime((double)mk.Sample / audio.SampleRate)}\t0:00.000\tdecimal\tCue\t{mk.Sample}");
                i++;
            }
            File.WriteAllText(MarkersPath, sb.ToString());
            InfoText.Text = $"Saved {markers.Count} markers to {System.IO.Path.GetFileName(MarkersPath)}";
        }

        void LoadMarkersFile()
        {
            markers.Clear();
            var p = MarkersPath;
            if (p == null || !File.Exists(p)) { RefreshMarkerList(); return; }
            foreach (var line in File.ReadAllLines(p).Skip(1))
            {
                var parts = line.Split('\t');
                if (parts.Length < 2) continue;
                long smp;
                if (parts.Length >= 6 && long.TryParse(parts[5], out var exact)) smp = exact;
                else if (TryParseTime(parts[1], out var secs)) smp = (long)(secs * audio.SampleRate);
                else continue;
                markers.Add(new Marker { Sample = smp, Name = parts[0].StartsWith("Marker ") ? "" : parts[0] });
            }
            markers.Sort((a, b) => a.Sample.CompareTo(b.Sample));
            RefreshMarkerList();
        }

        static bool TryParseTime(string s, out double secs)
        {
            secs = 0;
            var parts = s.Split(':');
            try
            {
                double mult = 1;
                for (int i = parts.Length - 1; i >= 0; i--) { secs += double.Parse(parts[i], CultureInfo.InvariantCulture) * mult; mult *= 60; }
                return true;
            }
            catch { return false; }
        }

        Marker MarkerNear(double x, double tolerance = 8)
        {
            Marker best = null; double bd = tolerance;
            foreach (var mk in markers) { double d = Math.Abs(SampleToX(mk.Sample) - x); if (d < bd) { bd = d; best = mk; } }
            return best;
        }

        // ---------------- keyboard ----------------

        void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (audio == null) return;
            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            switch (e.Key)
            {
                case Key.Space: TogglePlay(); e.Handled = true; break;
                case Key.M: DropMarker(); e.Handled = true; break;
                case Key.Home: SeekTo(0); viewStart = 0; ViewChanged(); e.Handled = true; break;
                case Key.End: SeekTo(audio.Length); viewStart = audio.Length - viewLen; ViewChanged(); e.Handled = true; break;
                case Key.Left: SeekTo(playhead - audio.SampleRate * (shift ? 0.1 : 1)); EnsurePlayheadVisible(); e.Handled = true; break;
                case Key.Right: SeekTo(playhead + audio.SampleRate * (shift ? 0.1 : 1)); EnsurePlayheadVisible(); e.Handled = true; break;
                case Key.Delete: DeleteSelected(); e.Handled = true; break;
                case Key.S when Keyboard.Modifiers.HasFlag(ModifierKeys.Control): SaveMarkers_Click(null, null); e.Handled = true; break;
                case Key.Add: case Key.OemPlus: ZoomAt(SampleToX(playhead), 1.5); e.Handled = true; break;
                case Key.Subtract: case Key.OemMinus: ZoomAt(SampleToX(playhead), 1 / 1.5); e.Handled = true; break;
            }
        }

        void EnsurePlayheadVisible()
        {
            if (playhead < viewStart || playhead > viewStart + viewLen) { viewStart = playhead - viewLen / 2; ViewChanged(); }
        }

        // ---------------- mouse: spectrogram ----------------

        void ZoomAt(double x, double factor)
        {
            if (audio == null) return;
            double anchor = XToSample(x);
            viewLen /= factor;
            viewLen = Math.Clamp(viewLen, audio.SampleRate * 0.2, audio.Length);
            viewStart = anchor - x / SpecW * viewLen;
            ViewChanged();
        }

        void Spec_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (audio == null) return;
            var p = e.GetPosition(SpecHost);
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                viewStart -= Math.Sign(e.Delta) * viewLen * 0.15;
                ViewChanged();
            }
            else ZoomAt(p.X, e.Delta > 0 ? 1.3 : 1 / 1.3);
            e.Handled = true;
        }

        void Spec_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (audio == null) return;
            var p = e.GetPosition(SpecHost);
            if (e.ChangedButton == MouseButton.Right)
            {
                var mk = MarkerNear(p.X);
                if (mk != null)
                {
                    selectedMarker = mk; draggingMarker = true; SpecHost.CaptureMouse();
                    RefreshMarkerList(); RefreshOverlays();
                }
                e.Handled = true;
            }
            else if (e.ChangedButton == MouseButton.Middle)
            {
                panning = true; panLastX = p.X; SpecHost.CaptureMouse();
            }
            else if (e.ChangedButton == MouseButton.Left)
            {
                var mk = MarkerNear(p.X, 5);
                if (mk != null) { selectedMarker = mk; RefreshMarkerList(); RefreshOverlays(); }
                scrubbing = true; wasPlayingBeforeScrub = playing;
                if (playing) StopPlayback();
                playhead = (long)Math.Clamp(XToSample(p.X), 0, audio.Length);
                PositionPlayheads(); UpdateTimeText();
                SpecHost.CaptureMouse();
            }
        }

        void Spec_MouseMove(object sender, MouseEventArgs e)
        {
            if (audio == null) return;
            var p = e.GetPosition(SpecHost);
            if (draggingMarker && selectedMarker != null)
            {
                selectedMarker.Sample = (long)Math.Clamp(XToSample(p.X), 0, audio.Length);
                RefreshOverlays(); RefreshMarkerList();
            }
            else if (panning)
            {
                viewStart -= (p.X - panLastX) / SpecW * viewLen; panLastX = p.X; ViewChanged();
            }
            else if (scrubbing)
            {
                playhead = (long)Math.Clamp(XToSample(p.X), 0, audio.Length);
                PositionPlayheads(); UpdateTimeText();
            }
            double secs = XToSample(p.X) / audio.SampleRate;
            int nch = audio.ChannelCount; double bandH = SpecHost.ActualHeight / nch;
            double frac = 1 - (p.Y % bandH) / bandH; double nyq = audio.SampleRate / 2.0;
            double freq = LogFreqChk.IsChecked == true ? 20 * Math.Pow(nyq / 20, frac) : frac * nyq;
            hoverText.Text = $"{FormatTime(secs)}   {freq:0} Hz";
            hoverText.Visibility = Visibility.Visible;
            Canvas.SetLeft(hoverText, Math.Min(p.X + 14, SpecW - 130)); Canvas.SetTop(hoverText, Math.Max(0, p.Y - 22));
            Mouse.OverrideCursor = MarkerNear(p.X) != null && !scrubbing && !panning ? Cursors.SizeWE : null;
        }

        void Spec_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (draggingMarker && e.ChangedButton == MouseButton.Right)
            {
                draggingMarker = false; SpecHost.ReleaseMouseCapture();
                markers.Sort((a, b) => a.Sample.CompareTo(b.Sample)); RefreshMarkerList(); RefreshOverlays();
            }
            else if (panning && e.ChangedButton == MouseButton.Middle) { panning = false; SpecHost.ReleaseMouseCapture(); }
            else if (scrubbing && e.ChangedButton == MouseButton.Left)
            {
                scrubbing = false; SpecHost.ReleaseMouseCapture();
                if (wasPlayingBeforeScrub) StartPlayback();
            }
        }

        void Spec_MouseLeave(object sender, MouseEventArgs e) { hoverText.Visibility = Visibility.Collapsed; Mouse.OverrideCursor = null; }

        // ---------------- mouse: overview ----------------

        void Overview_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (audio == null) return;
            var p = e.GetPosition(OverviewHost);
            double s = OvXToSample(p.X);
            if (s >= viewStart && s <= viewStart + viewLen && viewLen < audio.Length)
            {
                ovDragging = true; ovDragOffset = s - viewStart; OverviewHost.CaptureMouse();
            }
            else
            {
                SeekTo(s);
                if (viewLen < audio.Length) { viewStart = s - viewLen / 2; ViewChanged(); }
            }
        }

        void Overview_MouseMove(object sender, MouseEventArgs e)
        {
            if (!ovDragging) return;
            viewStart = OvXToSample(e.GetPosition(OverviewHost).X) - ovDragOffset; ViewChanged();
        }

        void Overview_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (ovDragging) { ovDragging = false; OverviewHost.ReleaseMouseCapture(); }
        }
    }
}
