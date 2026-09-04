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

    public class FileEntry : System.ComponentModel.INotifyPropertyChanged
    {
        public string Path;
        public List<Marker> Markers = new();
        public bool MarkersLoaded;
        public Stack<List<Marker>> Undo = new();
        public readonly Stack<List<Marker>> Redo = new();
        bool dirty;
        public bool Dirty { get => dirty; set { dirty = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Display))); } }
        public string Display => (dirty ? "* " : "") + System.IO.Path.GetFileName(Path);
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
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
        List<Marker> markers = new();
        Marker selectedMarker;
        readonly System.Collections.ObjectModel.ObservableCollection<FileEntry> files = new();
        FileEntry current;

        // view (in samples)
        double viewStart, viewLen;

        // playback
        WaveOutEvent waveOut;
        BufferProvider provider;
        TempoProvider tempoProvider;
        double playTempo = 1.0;
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
                FileList.ItemsSource = files;
                var args = Environment.GetCommandLineArgs().Skip(1).Where(File.Exists).ToArray();
                if (args.Length > 0) AddFiles(args);
            };
            Closing += (_, e) =>
            {
                StopPlayback();
                if (files.Any(f => f.Dirty))
                {
                    var r = MessageBox.Show("Some files have unsaved markers. Save them all before closing?", "SpectroMark", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                    if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
                    if (r == MessageBoxResult.Yes) SaveAll_Click(null, null);
                }
            };
        }

        // ---------------- file loading ----------------

        async void Open_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Audio files|*.wav;*.flac;*.mp3;*.aif;*.aiff|All files|*.*", Multiselect = true };
            if (dlg.ShowDialog() == true) AddFiles(dlg.FileNames);
            await Task.CompletedTask;
        }

        void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped && dropped.Length > 0)
                AddFiles(dropped.SelectMany(p => Directory.Exists(p) ? Directory.EnumerateFiles(p, "*.wav") : new[] { p }).ToArray());
        }

        void AddFiles(string[] paths)
        {
            FileEntry first = null;
            foreach (var p in paths)
            {
                var existing = files.FirstOrDefault(f => string.Equals(f.Path, p, StringComparison.OrdinalIgnoreCase));
                if (existing == null) { existing = new FileEntry { Path = p }; files.Add(existing); }
                first ??= existing;
            }
            if (first != null && (current == null || paths.Length == 1)) FileList.SelectedItem = first;
        }

        void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FileList.SelectedItem is FileEntry fe && fe != current) _ = LoadEntry(fe);
        }

        void CloseFile_Click(object sender, RoutedEventArgs e)
        {
            if (FileList.SelectedItem is not FileEntry fe) return;
            if (fe.Dirty)
            {
                var r = MessageBox.Show($"Save markers into {System.IO.Path.GetFileName(fe.Path)} before closing?", "SpectroMark", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (r == MessageBoxResult.Cancel) return;
                if (r == MessageBoxResult.Yes) SaveEntry(fe);
            }
            int idx = files.IndexOf(fe);
            files.Remove(fe);
            if (fe == current)
            {
                if (files.Count > 0) FileList.SelectedItem = files[Math.Min(idx, files.Count - 1)];
                else UnloadCurrent();
            }
        }

        void CloseAll_Click(object sender, RoutedEventArgs e)
        {
            if (files.Any(f => f.Dirty))
            {
                var r = MessageBox.Show("Some files have unsaved markers. Save them all first?", "SpectroMark", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (r == MessageBoxResult.Cancel) return;
                if (r == MessageBoxResult.Yes) SaveAll_Click(null, null);
            }
            files.Clear(); UnloadCurrent();
        }

        void UnloadCurrent()
        {
            StopPlayback();
            current = null; audio = null; spec = null; filePath = null; markers = new(); selectedMarker = null;
            InfoText.Text = "Open a WAV file (or drag files in)"; UpdateDirtyIndicator();
            InvalidateOverview(); InvalidateSpec(); RefreshOverlays(); RefreshMarkerList(); UpdateTimeText(); Ruler.Children.Clear();
        }

        void MarkDirty() { if (current != null) current.Dirty = true; UpdateDirtyIndicator(); }

        void UpdateDirtyIndicator()
        {
            bool cur = current?.Dirty == true;
            int others = files.Count(f => f.Dirty && f != current);
            DirtyText.Text = cur ? "* unsaved" + (others > 0 ? $" (+{others})" : "") : (others > 0 ? $"({others} unsaved)" : "");
            Title = "SpectroMark" + (current != null ? " - " + (cur ? "* " : "") + System.IO.Path.GetFileName(current.Path) : "");
        }

        // ---------------- undo / redo ----------------

        static List<Marker> Snapshot(List<Marker> src) => src.Select(m => new Marker { Sample = m.Sample, Name = m.Name }).ToList();

        /// <summary>Call before any change to the marker list.</summary>
        void PushUndo()
        {
            if (current == null) return;
            current.Undo.Push(Snapshot(markers));
            if (current.Undo.Count > 200) current.Undo = new Stack<List<Marker>>(current.Undo.Reverse().Skip(current.Undo.Count - 200).Reverse());
            current.Redo.Clear();
        }

        void UndoMarkers()
        {
            if (current == null || current.Undo.Count == 0) return;
            current.Redo.Push(Snapshot(markers));
            RestoreMarkers(current.Undo.Pop());
            InfoText.Text = $"Undo  ({current.Undo.Count} left)";
        }

        void RedoMarkers()
        {
            if (current == null || current.Redo.Count == 0) return;
            current.Undo.Push(Snapshot(markers));
            RestoreMarkers(current.Redo.Pop());
            InfoText.Text = $"Redo  ({current.Redo.Count} left)";
        }

        void RestoreMarkers(List<Marker> state)
        {
            markers.Clear(); markers.AddRange(state);
            selectedMarker = null; MarkDirty();
            RefreshMarkerList(); RefreshOverlays();
        }

        async Task LoadEntry(FileEntry entry)
        {
            string path = entry.Path;
            StopPlayback();
            InfoText.Text = "Loading " + System.IO.Path.GetFileName(path) + " ...";
            try
            {
                var data = await Task.Run(() => ReadAudio(path));
                if (FileList.SelectedItem != entry) return; // user clicked another file meanwhile
                current = entry;
                audio = data; filePath = path; playhead = 0; spec = null;
                viewStart = 0; viewLen = audio.Length;
                markers = entry.Markers; selectedMarker = null;
                UpdateDirtyIndicator();
                string chDesc = audio.ChannelCount switch { 1 => "MONO (1 channel)", 2 => "STEREO (2 channels)", _ => audio.ChannelCount + " channels" };
                InfoText.Text = $"{System.IO.Path.GetFileName(path)}   |   {chDesc}   |   {audio.SampleRate} Hz   |   {FormatTime(audio.Duration)}   |   analysing spectrogram...";
                InvalidateOverview(); InvalidateSpec(); UpdateScrollBar(); DrawRuler(); RefreshOverlays(); UpdateTimeText();
                if (!entry.MarkersLoaded) { LoadMarkersFile(); entry.MarkersLoaded = true; } else RefreshMarkerList();
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
            double nyq = audio.SampleRate / 2.0;
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
                double freq = FreqScale.ToFreq(frac, nyq, logF);
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
                int col = unchecked((int)0xFF66D9FF), mid = unchecked((int)0xFF3A3A3A);
                float peak = 0; foreach (var chn in audio.Channels) { for (long i = 0; i < chn.Length; i += 7) { float v = Math.Abs(chn[i]); if (v > peak) peak = v; } }
                float norm = peak > 1e-4f ? 1f / peak : 1f;
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
                        mx *= norm; mn *= norm;
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
                // Audition-style axis: labels at 1..7 x each decade (plus 15k / 20k), decades bold,
                // minor ticks at every step, labels thinned when they would collide.
                var cand = new List<(double f, bool major)>();
                if (logF)
                {
                    for (double dec = 10; dec < nyq; dec *= 10)
                    {
                        if (dec >= 300) cand.Add((dec, true));
                        foreach (int k in new[] { 2, 3, 4, 5, 6, 7 }) if (dec * k < nyq && dec * k >= 300) cand.Add((dec * k, false));
                        if (dec == 1000 && 15000 < nyq) cand.Add((15000, false));
                    }
                }
                else
                {
                    double stepHz = bandH > 500 ? 1000 : bandH > 250 ? 2000 : 5000;
                    for (double f = stepHz; f < nyq; f += stepHz) cand.Add((f, f % 10000 == 0));
                }
                cand.Sort((a, b) => b.f.CompareTo(a.f));
                double Frac(double f) => FreqScale.ToFrac(f, nyq, logF);
                for (int c = 0; c < nch; c++)
                {
                    double lastLabelY = double.NegativeInfinity;
                    foreach (var (f, major) in cand)
                    {
                        double frac = Frac(f);
                        if (frac <= 0 || frac >= 1) continue;
                        double y = c * bandH + (1 - frac) * bandH;
                        SpecOverlay.Children.Add(new Line { X1 = 0, X2 = major ? 8 : 4, Y1 = y, Y2 = y, Stroke = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)), StrokeThickness = 1 });
                        if (!major && y - lastLabelY < 13) continue;
                        if (major && y - lastLabelY < 13 && lastLabelY > double.NegativeInfinity) continue;
                        lastLabelY = y;
                        string text = f >= 1000 ? (f / 1000.0).ToString("0.#") + "k" : f.ToString();
                        var tb = new TextBlock
                        {
                            Text = text, FontSize = major ? 11 : 10,
                            FontWeight = major ? FontWeights.Bold : FontWeights.Normal,
                            Foreground = new SolidColorBrush(major ? Color.FromArgb(230, 255, 255, 255) : Color.FromArgb(150, 255, 255, 255))
                        };
                        Canvas.SetLeft(tb, 10); Canvas.SetTop(tb, y - 8); SpecOverlay.Children.Add(tb);
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

        void Speed_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (SpeedText == null) return;
            double t = Math.Round(e.NewValue, 1);
            SpeedText.Text = t.ToString("0.0") + "x";
            if (Math.Abs(t - playTempo) < 1e-6) return;
            // restart playback from the current position so the played-sample counter stays exact
            bool wasPlaying = playing;
            if (wasPlaying) StopPlayback();
            playTempo = t;
            if (wasPlaying) StartPlayback();
        }

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
            tempoProvider = new TempoProvider(provider, playTempo);
            var w = new WaveOutEvent { DesiredLatency = 120, NumberOfBuffers = 3 };
            w.Init(tempoProvider);
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
            long frames = (long)(bytes / (4 * audio.ChannelCount) * playTempo);
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
            PushUndo();
            var mk = new Marker { Sample = pos };
            markers.Add(mk);
            markers.Sort((a, b) => a.Sample.CompareTo(b.Sample));
            selectedMarker = mk; MarkDirty();
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
            if (audio == null) { MarkerList.SelectionChanged += MarkerList_SelectionChanged; return; }
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
            PushUndo();
            markers.Remove(selectedMarker); selectedMarker = null; MarkDirty();
            RefreshMarkerList(); RefreshOverlays();
        }

        void ClearMarkers_Click(object sender, RoutedEventArgs e)
        {
            if (markers.Count == 0) return;
            if (MessageBox.Show("Remove all markers?", "SpectroMark", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            PushUndo();
            markers.Clear(); selectedMarker = null; MarkDirty(); RefreshMarkerList(); RefreshOverlays();
        }

        // Markers live inside the WAV itself (RIFF 'cue ' + 'LIST/adtl' chunks), like Audition and other editors.
        void SaveMarkers_Click(object sender, RoutedEventArgs e)
        {
            if (current == null) return;
            bool wasPlaying = playing; if (wasPlaying) StopPlayback();
            if (SaveEntry(current)) InfoText.Text = $"Saved {markers.Count} markers into {System.IO.Path.GetFileName(current.Path)}";
            if (wasPlaying) StartPlayback();
        }

        void SaveAll_Click(object sender, RoutedEventArgs e)
        {
            bool wasPlaying = playing; if (wasPlaying) StopPlayback();
            int n = 0;
            foreach (var f in files.Where(f => f.Dirty).ToList()) if (SaveEntry(f)) n++;
            InfoText.Text = $"Saved markers into {n} file(s)";
            if (wasPlaying) StartPlayback();
        }

        bool SaveEntry(FileEntry fe)
        {
            try
            {
                WavCues.Write(fe.Path, fe.Markers);
                fe.Dirty = false; UpdateDirtyIndicator();
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not write markers into {System.IO.Path.GetFileName(fe.Path)}:\n{ex.Message}", "SpectroMark", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        void LoadMarkersFile()
        {
            markers.Clear();
            try { if (filePath != null) markers.AddRange(WavCues.Read(filePath)); } catch { }
            RefreshMarkerList();
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
                case Key.Z when Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && shift: RedoMarkers(); e.Handled = true; break;
                case Key.Z when Keyboard.Modifiers.HasFlag(ModifierKeys.Control): UndoMarkers(); e.Handled = true; break;
                case Key.Y when Keyboard.Modifiers.HasFlag(ModifierKeys.Control): RedoMarkers(); e.Handled = true; break;
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
                    PushUndo();
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
                var mk = MarkerNear(p.X);
                if (mk != null)
                {
                    PushUndo();
                    selectedMarker = mk; draggingMarker = true; SpecHost.CaptureMouse();
                    RefreshMarkerList(); RefreshOverlays();
                    return;
                }
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
            double freq = FreqScale.ToFreq(frac, nyq, LogFreqChk.IsChecked == true);
            hoverText.Text = $"{FormatTime(secs)}   {freq:0} Hz";
            hoverText.Visibility = Visibility.Visible;
            Canvas.SetLeft(hoverText, Math.Min(p.X + 14, SpecW - 130)); Canvas.SetTop(hoverText, Math.Max(0, p.Y - 22));
            Mouse.OverrideCursor = MarkerNear(p.X) != null && !scrubbing && !panning ? Cursors.SizeWE : null;
        }

        void Spec_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (draggingMarker && (e.ChangedButton == MouseButton.Left || e.ChangedButton == MouseButton.Right))
            {
                draggingMarker = false; SpecHost.ReleaseMouseCapture(); MarkDirty();
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
