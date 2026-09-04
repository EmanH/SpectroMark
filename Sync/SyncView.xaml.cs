using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using NAudio.Wave;

namespace WavMarker.Sync
{
    public partial class SyncView : UserControl
    {
        readonly List<SyncTrack> tracks = new();
        int sampleRate;
        double viewStart, viewLen;     // timeline frames
        long playhead;
        bool playing;
        WaveOutEvent waveOut;
        long playStart;
        readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(25) };
        StretchMode mode = StretchMode.Tonal;

        WriteableBitmap bmp; int[] px; double dpiScale = 1;
        readonly DispatcherTimer renderTimer = new() { Interval = TimeSpan.FromMilliseconds(15) };
        bool lanesDirty;

        // sync-click state
        bool sHeld;
        SyncTrack anchorTrack; long anchorTime;
        int groupCount;

        // mouse
        int pressLane = -1; double pressX; long pressOffset; bool draggingClip, pressed;
        (SyncTrack t, Marker m) hover;

        // undo
        readonly Stack<string> undo = new(); readonly Stack<string> redo = new();

        public TextBlock TimeDisplay;     // set by MainWindow
        public event Action<string> StatusChanged;

        static readonly Color[] Palette = { Color.FromRgb(102, 217, 255), Color.FromRgb(255, 170, 80), Color.FromRgb(140, 255, 140), Color.FromRgb(255, 120, 200), Color.FromRgb(200, 170, 255), Color.FromRgb(255, 240, 120), Color.FromRgb(120, 230, 210), Color.FromRgb(255, 140, 140) };

        public SyncView()
        {
            InitializeComponent();
            foreach (var m in StretchEngine.Modes) ModeBox.Items.Add(m.ToString());
            ModeBox.SelectedIndex = Array.IndexOf(StretchEngine.Modes, mode);
            ModeBox.ToolTip = string.Join("\n", StretchEngine.Modes.Select(StretchEngine.Describe));
            timer.Tick += (_, _) => UpdatePlayhead();
            renderTimer.Tick += (_, _) => { renderTimer.Stop(); if (lanesDirty) RenderLanes(); lanesDirty = false; };
            LaneHost.SizeChanged += (_, _) => { InvalidateLanes(); RefreshOverlay(); RebuildHeaders(); };
            Ruler.SizeChanged += (_, _) => DrawRuler();
            Overlay.Draw = DrawOverlay; PlayheadLayer.Draw = DrawPlayhead;
            Loaded += (_, _) => { var src = PresentationSource.FromVisual(this); if (src?.CompositionTarget != null) dpiScale = src.CompositionTarget.TransformToDevice.M11; };
        }

        public int TrackCount => tracks.Count;
        long EndFrame => tracks.Count == 0 ? 0 : tracks.Max(t => t.End);
        double LaneW => LaneHost.ActualWidth;
        double LaneH => tracks.Count == 0 ? LaneHost.ActualHeight : LaneHost.ActualHeight / tracks.Count;
        double SampleToX(double s) => (s - viewStart) / viewLen * LaneW;
        double XToSample(double x) => viewStart + x / LaneW * viewLen;

        void SetStatus(string s) { Status.Text = s; StatusChanged?.Invoke(s); }

        // ---------------- clips ----------------

        async void AddClips_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "WAV files|*.wav|All files|*.*", Multiselect = true };
            if (dlg.ShowDialog() == true) await AddFiles(dlg.FileNames);
        }

        public async Task AddFiles(string[] paths)
        {
            foreach (var p in paths)
            {
                if (tracks.Any(t => string.Equals(t.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
                SetStatus("Loading " + System.IO.Path.GetFileName(p) + " ...");
                AudioData a;
                try { a = await Task.Run(() => AudioIO.Read(p)); }
                catch (Exception ex) { MessageBox.Show($"Could not open {p}:\n{ex.Message}", "SpectroMark"); continue; }
                if (tracks.Count == 0) sampleRate = a.SampleRate;
                else if (a.SampleRate != sampleRate) { MessageBox.Show($"{System.IO.Path.GetFileName(p)} is {a.SampleRate} Hz but the session is {sampleRate} Hz. All clips must share a sample rate.", "SpectroMark"); continue; }
                var t = new SyncTrack { Path = p, Audio = a };
                try { t.Markers = WavCues.Read(p); } catch { }
                await Task.Run(t.Init);
                tracks.Add(t);
            }
            if (tracks.Count > 0 && viewLen <= 0) { viewStart = 0; viewLen = Math.Max(1, EndFrame); }
            RebuildHeaders(); ViewChanged(); UpdateTime();
            SetStatus($"{tracks.Count} clip(s)");
        }

        void RemoveTrack(SyncTrack t)
        {
            StopPlayback();
            tracks.Remove(t);
            RebuildHeaders(); ViewChanged();
        }

        // ---------------- rendering of stretched audio ----------------

        void ScheduleRender(SyncTrack t)
        {
            if (t.Rendering || t.RenderedVersion == t.RenderVersion) return;
            t.Rendering = true;
            int version = t.RenderVersion; var m = mode;
            RebuildHeaders();
            Task.Run(() =>
            {
                try { t.Render(m, version); }
                catch (Exception ex) { Dispatcher.BeginInvoke(() => SetStatus("Render failed: " + ex.Message)); }
                Dispatcher.BeginInvoke(() =>
                {
                    t.Rendering = false;
                    InvalidateLanes(); RefreshOverlay(); RebuildHeaders(); UpdateScrollBar();
                    if (t.RenderedVersion != t.RenderVersion) ScheduleRender(t);
                });
            });
        }

        void ScheduleAll() { foreach (var t in tracks) ScheduleRender(t); }

        void Mode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (ModeBox.SelectedIndex < 0) return;
            mode = StretchEngine.Modes[ModeBox.SelectedIndex];
            foreach (var t in tracks) t.ClearCache();
            ScheduleAll();
        }

        // ---------------- headers ----------------

        void RebuildHeaders()
        {
            Headers.Children.Clear();
            double h = LaneH;
            for (int i = 0; i < tracks.Count; i++)
            {
                var t = tracks[i];
                var col = Palette[i % Palette.Length];
                var panel = new StackPanel { Margin = new Thickness(6, 4, 4, 0) };
                panel.Children.Add(new TextBlock { Text = t.Name, Foreground = new SolidColorBrush(col), FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = t.Path });
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
                var mute = new ToggleButton { Content = "M", Width = 24, IsChecked = t.Mute, Focusable = false, Background = new SolidColorBrush(Color.FromRgb(60, 40, 40)), Foreground = Brushes.White };
                mute.Click += (_, _) => { t.Mute = mute.IsChecked == true; InvalidateLanes(); };
                var solo = new ToggleButton { Content = "S", Width = 24, IsChecked = t.Solo, Focusable = false, Margin = new Thickness(3, 0, 0, 0), Background = new SolidColorBrush(Color.FromRgb(40, 60, 40)), Foreground = Brushes.White };
                solo.Click += (_, _) => { t.Solo = solo.IsChecked == true; InvalidateLanes(); };
                var rm = new Button { Content = "✕", Width = 24, Margin = new Thickness(3, 0, 0, 0), Padding = new Thickness(0), Focusable = false, ToolTip = "Remove clip" };
                rm.Click += (_, _) => RemoveTrack(t);
                row.Children.Add(mute); row.Children.Add(solo); row.Children.Add(rm);
                panel.Children.Add(row);
                panel.Children.Add(new TextBlock { Text = (t.Rendering ? "rendering...  " : "") + $"{t.Points.Count} sync pt", Foreground = Brushes.Gray, FontSize = 10, Margin = new Thickness(0, 3, 0, 0) });
                Headers.Children.Add(new Border { Height = h, Child = panel, BorderBrush = new SolidColorBrush(Color.FromRgb(40, 40, 40)), BorderThickness = new Thickness(0, 0, 0, 1), Background = new SolidColorBrush(i % 2 == 0 ? Color.FromRgb(26, 26, 26) : Color.FromRgb(22, 22, 22)) });
            }
        }

        // ---------------- lane bitmap ----------------

        void InvalidateLanes() { lanesDirty = true; if (!renderTimer.IsEnabled) renderTimer.Start(); }

        void RenderLanes()
        {
            int W = (int)(LaneW * dpiScale), H = (int)(LaneHost.ActualHeight * dpiScale);
            if (W < 2 || H < 2) return;
            if (bmp == null || bmp.PixelWidth != W || bmp.PixelHeight != H) { bmp = new WriteableBitmap(W, H, 96, 96, PixelFormats.Bgra32, null); px = new int[W * H]; LaneImage.Source = bmp; }
            Array.Fill(px, unchecked((int)0xFF0C0C0C));
            int n = tracks.Count;
            if (n > 0 && viewLen > 0)
            {
                int laneH = H / n;
                bool anySolo = tracks.Any(t => t.Solo);
                for (int i = 0; i < n; i++)
                {
                    var t = tracks[i];
                    int top = i * laneH, center = top + laneH / 2;
                    int bg = i % 2 == 0 ? unchecked((int)0xFF101010) : unchecked((int)0xFF0C0C0C);
                    for (int y = top; y < top + laneH; y++) for (int x = 0; x < W; x++) px[y * W + x] = bg;
                    bool dim = t.Mute || (anySolo && !t.Solo);
                    var c = Palette[i % Palette.Length];
                    int col = dim ? unchecked((int)0xFF505050) : (255 << 24) | (c.R << 16) | (c.G << 8) | c.B;
                    int clipBg = dim ? unchecked((int)0xFF161616) : unchecked((int)0xFF1a1f26);
                    int amp = laneH / 2 - 4;
                    int Wl = W; var pxl = px;
                    Parallel.For(0, Wl, x =>
                    {
                        double tl0 = viewStart + (double)x / Wl * viewLen, tl1 = viewStart + (double)(x + 1) / Wl * viewLen;
                        long l0 = (long)(tl0 - t.Offset), l1 = Math.Max((long)(tl1 - t.Offset), (long)(tl0 - t.Offset) + 1);
                        if (l1 <= 0 || l0 >= t.RenderedLength) return;
                        for (int y = top; y < top + laneH; y++) pxl[y * Wl + x] = clipBg;
                        t.Peak(Math.Max(0, l0), Math.Min(t.RenderedLength, l1), out float mn, out float mx);
                        int y0 = center - (int)(mx * amp), y1 = center - (int)(mn * amp);
                        for (int y = Math.Max(top, y0); y <= Math.Min(top + laneH - 1, y1); y++) pxl[y * Wl + x] = col;
                    });
                    for (int x = 0; x < W; x++) px[(top + laneH - 1) * W + x] = unchecked((int)0xFF303030);
                }
            }
            bmp.WritePixels(new Int32Rect(0, 0, W, H), px, W * 4, 0);
        }

        // ---------------- overlay ----------------

        static readonly Brush MarkerBrush = Freeze(new SolidColorBrush(Color.FromRgb(255, 240, 60)));
        static readonly Brush SyncedBrush = Freeze(new SolidColorBrush(Color.FromRgb(90, 255, 255)));
        static readonly Brush BandBrush = Freeze(new SolidColorBrush(Color.FromArgb(40, 255, 240, 60)));
        static readonly Brush BandHotBrush = Freeze(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)));
        static readonly Pen MarkerPen = Freeze(new Pen(MarkerBrush, 1.5));
        static readonly Pen SyncedPen = Freeze(new Pen(SyncedBrush, 2.5));
        static readonly Pen AnchorPen = Freeze(new Pen(Brushes.White, 2) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) });
        static readonly Pen PlayheadPen = Freeze(new Pen(Brushes.White, 1.5));
        const double HitPx = 12;
        static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

        void RefreshOverlay() => Overlay.InvalidateVisual();
        void RefreshPlayhead() => PlayheadLayer.InvalidateVisual();

        void DrawOverlay(DrawingContext dc)
        {
            if (tracks.Count == 0 || viewLen <= 0) return;
            double h = LaneH, W = LaneW;
            for (int i = 0; i < tracks.Count; i++)
            {
                var t = tracks[i]; double top = i * h;
                foreach (var m in t.Markers)
                {
                    double x = SampleToX(t.SourceToTimeline(m.Sample));
                    if (x < -HitPx || x > W + HitPx) continue;
                    bool synced = t.PointAt(m.Sample) != null;
                    bool hot = hover.t == t && hover.m == m;
                    if (sHeld || hot) dc.DrawRectangle(hot ? BandHotBrush : BandBrush, null, new Rect(x - HitPx, top, HitPx * 2, h));
                    dc.DrawLine(synced ? SyncedPen : MarkerPen, new Point(x, top), new Point(x, top + h));
                    var g = new StreamGeometry();
                    using (var ctx = g.Open())
                    {
                        if (synced) { ctx.BeginFigure(new Point(x, top + 2), true, true); ctx.LineTo(new Point(x + 6, top + 8), false, false); ctx.LineTo(new Point(x, top + 14), false, false); ctx.LineTo(new Point(x - 6, top + 8), false, false); }
                        else { ctx.BeginFigure(new Point(x, top), true, true); ctx.LineTo(new Point(x + 9, top), false, false); ctx.LineTo(new Point(x + 9, top + 7), false, false); ctx.LineTo(new Point(x, top + 11), false, false); }
                    }
                    g.Freeze();
                    dc.DrawGeometry(synced ? SyncedBrush : MarkerBrush, null, g);
                }
            }
            if (anchorTrack != null)
            {
                double ax = SampleToX(anchorTime);
                dc.DrawLine(AnchorPen, new Point(ax, 0), new Point(ax, LaneHost.ActualHeight));
            }
        }

        void DrawPlayhead(DrawingContext dc)
        {
            if (tracks.Count == 0 || viewLen <= 0) return;
            double x = SampleToX(playhead);
            if (x >= 0 && x <= LaneW) dc.DrawLine(PlayheadPen, new Point(x, 0), new Point(x, LaneHost.ActualHeight));
        }

        void DrawRuler()
        {
            Ruler.Children.Clear();
            if (tracks.Count == 0 || viewLen <= 0) return;
            double W = Ruler.ActualWidth; if (W < 10) return;
            double secs = viewLen / sampleRate;
            double[] steps = { 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 };
            double step = steps[^1];
            foreach (var st in steps) if (st / secs * W >= 70) { step = st; break; }
            double t0 = Math.Floor(viewStart / sampleRate / step) * step;
            for (double t = t0; t <= (viewStart + viewLen) / sampleRate; t += step)
            {
                double x = SampleToX(t * sampleRate); if (x < 0) continue;
                Ruler.Children.Add(new Line { X1 = x, X2 = x, Y1 = 12, Y2 = 22, Stroke = Brushes.Gray, StrokeThickness = 1 });
                var tb = new TextBlock { Text = FormatTime(t, step < 1), Foreground = Brushes.LightGray, FontSize = 10 };
                Canvas.SetLeft(tb, x + 3); Canvas.SetTop(tb, 0); Ruler.Children.Add(tb);
            }
        }

        static string FormatTime(double secs, bool ms = true)
        {
            if (secs < 0) secs = 0;
            int m = (int)(secs / 60); double s = secs - m * 60;
            return ms ? $"{m}:{s:00.000}" : $"{m}:{s:00}";
        }

        void UpdateTime()
        {
            if (TimeDisplay == null) return;
            TimeDisplay.Text = sampleRate == 0 ? "0:00.000 / 0:00.000" : $"{FormatTime((double)playhead / sampleRate)} / {FormatTime((double)EndFrame / sampleRate)}";
        }

        void ViewChanged()
        {
            if (tracks.Count == 0) { InvalidateLanes(); RefreshOverlay(); DrawRuler(); return; }
            double total = Math.Max(1, EndFrame);
            viewLen = Math.Clamp(viewLen, sampleRate * 0.2, total);
            viewStart = Math.Clamp(viewStart, 0, Math.Max(0, total - viewLen));
            InvalidateLanes(); RefreshOverlay(); RefreshPlayhead(); DrawRuler(); UpdateScrollBar();
        }

        void UpdateScrollBar()
        {
            double total = Math.Max(1, EndFrame);
            HScroll.Minimum = 0; HScroll.Maximum = Math.Max(0, total - viewLen);
            HScroll.ViewportSize = viewLen; HScroll.LargeChange = viewLen * 0.9; HScroll.SmallChange = viewLen * 0.1; HScroll.Value = viewStart;
        }

        void HScroll_Scroll(object sender, ScrollEventArgs e) { viewStart = e.NewValue; ViewChanged(); }

        void ZoomAt(double x, double factor)
        {
            double anchor = XToSample(x);
            viewLen /= factor;
            viewStart = anchor - x / LaneW * viewLen;
            ViewChanged();
        }

        // ---------------- playback ----------------

        void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

        public void TogglePlay() { if (tracks.Count == 0) return; if (playing) StopPlayback(); else StartPlayback(); }

        void StartPlayback()
        {
            if (tracks.Count == 0) return;
            if (playhead >= EndFrame - 1) playhead = 0;
            var prov = new SyncMixProvider(tracks, sampleRate, playhead, EndFrame);
            var w = new WaveOutEvent { DesiredLatency = 120, NumberOfBuffers = 3 };
            w.Init(prov);
            w.PlaybackStopped += (_, _) => Dispatcher.BeginInvoke(() => { if (waveOut == w && playing) { StopPlayback(); playhead = EndFrame; RefreshPlayhead(); UpdateTime(); } });
            waveOut = w; playStart = playhead; w.Play();
            playing = true; timer.Start(); PlayBtn.Content = "Pause  (Space)";
        }

        public void StopPlayback()
        {
            if (waveOut != null)
            {
                if (playing) playhead = CurrentSample();
                var w = waveOut; waveOut = null;
                try { w.Stop(); w.Dispose(); } catch { }
            }
            playing = false; timer.Stop(); PlayBtn.Content = "Play  (Space)";
        }

        long CurrentSample()
        {
            if (!playing || waveOut == null) return playhead;
            try { return Math.Min(EndFrame, playStart + waveOut.GetPosition() / 8); } catch { return playhead; }
        }

        void UpdatePlayhead()
        {
            if (!playing) return;
            playhead = CurrentSample();
            if (playhead > viewStart + viewLen || playhead < viewStart) { viewStart = playhead - viewLen * 0.05; ViewChanged(); }
            RefreshPlayhead(); UpdateTime();
        }

        void SeekTo(long s)
        {
            bool was = playing; if (was) StopPlayback();
            playhead = Math.Clamp(s, 0, Math.Max(0, EndFrame));
            if (was) StartPlayback();
            RefreshPlayhead(); UpdateTime();
        }

        // ---------------- keyboard (routed from MainWindow) ----------------

        public bool HandleKey(KeyEventArgs e, bool down)
        {
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.None)
            {
                if (down && !sHeld) { sHeld = true; anchorTrack = null; groupCount = 0; SyncHint.Text = "SYNC: click the anchor marker"; Mouse.OverrideCursor = Cursors.Cross; RefreshOverlay(); }
                else if (!down) { sHeld = false; anchorTrack = null; SyncHint.Text = ""; Mouse.OverrideCursor = null; RefreshOverlay(); }
                return true;
            }
            if (!down) return false;
            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            switch (e.Key)
            {
                case Key.Space: TogglePlay(); return true;
                case Key.Home: SeekTo(0); viewStart = 0; ViewChanged(); return true;
                case Key.Left: SeekTo(playhead - (long)(sampleRate * (shift ? 0.1 : 1))); return true;
                case Key.Right: SeekTo(playhead + (long)(sampleRate * (shift ? 0.1 : 1))); return true;
                case Key.Z when ctrl && shift: Redo(); return true;
                case Key.Z when ctrl: Undo(); return true;
                case Key.Y when ctrl: Redo(); return true;
                case Key.Add: case Key.OemPlus: ZoomAt(SampleToX(playhead), 1.5); return true;
                case Key.Subtract: case Key.OemMinus: ZoomAt(SampleToX(playhead), 1 / 1.5); return true;
            }
            return false;
        }

        // ---------------- mouse ----------------

        (SyncTrack, Marker) HitMarker(Point p)
        {
            if (tracks.Count == 0) return (null, null);
            int lane = (int)(p.Y / LaneH); if (lane < 0 || lane >= tracks.Count) return (null, null);
            var t = tracks[lane]; Marker best = null; double bd = HitPx;
            foreach (var m in t.Markers) { double d = Math.Abs(SampleToX(t.SourceToTimeline(m.Sample)) - p.X); if (d < bd) { bd = d; best = m; } }
            return best == null ? (null, null) : (t, best);
        }

        void Lane_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (tracks.Count == 0) return;
            Focus();
            var p = e.GetPosition(LaneHost);
            var (t, m) = HitMarker(p);
            if (e.ChangedButton == MouseButton.Left)
            {
                if (sHeld && m != null) { SyncClick(t, m); return; }
                if (m != null && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
                {
                    var pt = t.PointAt(m.Sample);
                    if (pt != null) { PushUndo(); t.RemovePoint(pt); ScheduleRender(t); RefreshOverlay(); RebuildHeaders(); SetStatus($"Removed sync point on {t.Name}"); }
                    return;
                }
                int lane = (int)(p.Y / LaneH);
                pressed = true; pressLane = lane; pressX = p.X; pressOffset = tracks[lane].Offset; draggingClip = false;
                LaneHost.CaptureMouse();
            }
            else if (e.ChangedButton == MouseButton.Middle) { pressed = true; pressLane = -1; pressX = p.X; LaneHost.CaptureMouse(); }
        }

        void Lane_MouseMove(object sender, MouseEventArgs e)
        {
            if (tracks.Count == 0) return;
            var p = e.GetPosition(LaneHost);
            if (pressed && pressLane >= 0)
            {
                if (!draggingClip && Math.Abs(p.X - pressX) >= 4) { PushUndo(); draggingClip = true; }
                if (draggingClip)
                {
                    var t = tracks[pressLane];
                    t.Offset = pressOffset + (long)((p.X - pressX) / LaneW * viewLen);
                    InvalidateLanes(); RefreshOverlay(); UpdateScrollBar(); UpdateTime();
                }
                return;
            }
            if (pressed && pressLane < 0) { viewStart -= (p.X - pressX) / LaneW * viewLen; pressX = p.X; ViewChanged(); return; }
            var h = HitMarker(p);
            if (h.Item1 != hover.t || h.Item2 != hover.m) { hover = h; RefreshOverlay(); }
            if (!sHeld) Mouse.OverrideCursor = h.Item2 != null ? Cursors.Hand : null;
        }

        void Lane_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!pressed) return;
            var p = e.GetPosition(LaneHost);
            pressed = false; LaneHost.ReleaseMouseCapture();
            if (pressLane >= 0)
            {
                if (draggingClip) { draggingClip = false; SetStatus($"Moved {tracks[pressLane].Name}"); UpdateScrollBar(); }
                else SeekTo((long)XToSample(p.X));
            }
        }

        void Lane_MouseLeave(object sender, MouseEventArgs e) { if (!sHeld) Mouse.OverrideCursor = null; }

        void Lane_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (tracks.Count == 0) return;
            var p = e.GetPosition(LaneHost);
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { viewStart -= Math.Sign(e.Delta) * viewLen * 0.15; ViewChanged(); }
            else ZoomAt(p.X, e.Delta > 0 ? 1.3 : 1 / 1.3);
            e.Handled = true;
        }

        // ---------------- sync logic ----------------

        void SyncClick(SyncTrack t, Marker m)
        {
            if (anchorTrack == null)
            {
                anchorTrack = t; anchorTime = t.SourceToTimeline(m.Sample); groupCount = 1;
                SyncHint.Text = $"SYNC anchor: {t.Name} @ {FormatTime((double)anchorTime / sampleRate)}  - click markers on other lanes";
                RefreshOverlay(); return;
            }
            if (t == anchorTrack) { SetStatus("That lane is the anchor; click a marker on another lane."); return; }
            PushUndo();
            if (t.Points.Count == 0)
            {
                t.Offset = anchorTime - m.Sample;
                t.AddOrUpdatePoint(m.Sample, m.Sample);
            }
            else
            {
                long localTarget = anchorTime - t.Offset;
                string err = t.CheckPoint(m.Sample, localTarget);
                if (err != null) { undo.Pop(); SetStatus($"Cannot sync: {err}"); return; }
                t.AddOrUpdatePoint(m.Sample, localTarget);
            }
            groupCount++;
            ScheduleRender(t);
            RefreshOverlay(); RebuildHeaders(); UpdateScrollBar(); UpdateTime();
            SyncHint.Text = $"SYNC anchor {anchorTrack.Name} @ {FormatTime((double)anchorTime / sampleRate)}  - {groupCount} lanes in this group";
        }

        // ---------------- undo / session ----------------

        class TrackState { public string Path { get; set; } public long Offset { get; set; } public bool Mute { get; set; } public bool Solo { get; set; } public List<long[]> Points { get; set; } = new(); }
        class SessionState { public int SampleRate { get; set; } public string Mode { get; set; } public List<TrackState> Tracks { get; set; } = new(); }

        SessionState Capture() => new()
        {
            SampleRate = sampleRate, Mode = mode.ToString(),
            Tracks = tracks.Select(t => new TrackState { Path = t.Path, Offset = t.Offset, Mute = t.Mute, Solo = t.Solo, Points = t.Points.Select(p => new[] { p.Source, p.Target }).ToList() }).ToList()
        };

        void ApplyState(SessionState st)
        {
            foreach (var ts in st.Tracks)
            {
                var t = tracks.FirstOrDefault(x => string.Equals(x.Path, ts.Path, StringComparison.OrdinalIgnoreCase));
                if (t == null) continue;
                t.Offset = ts.Offset; t.Mute = ts.Mute; t.Solo = ts.Solo;
                t.Points = ts.Points.Select(p => new StretchPoint { Source = p[0], Target = p[1] }).OrderBy(p => p.Source).ToList();
                t.RenderVersion++;
                ScheduleRender(t);
            }
            RebuildHeaders(); ViewChanged(); UpdateTime();
        }

        void PushUndo() { undo.Push(JsonSerializer.Serialize(Capture())); redo.Clear(); }
        void Undo() { if (undo.Count == 0) return; redo.Push(JsonSerializer.Serialize(Capture())); ApplyState(JsonSerializer.Deserialize<SessionState>(undo.Pop())); SetStatus($"Undo ({undo.Count} left)"); }
        void Redo() { if (redo.Count == 0) return; undo.Push(JsonSerializer.Serialize(Capture())); ApplyState(JsonSerializer.Deserialize<SessionState>(redo.Pop())); SetStatus("Redo"); }

        void SaveSession_Click(object sender, RoutedEventArgs e)
        {
            if (tracks.Count == 0) return;
            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "SpectroMark sync session|*.spectrosync.json", FileName = "sync-session.spectrosync.json", InitialDirectory = System.IO.Path.GetDirectoryName(tracks[0].Path) };
            if (dlg.ShowDialog() != true) return;
            File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(Capture(), new JsonSerializerOptions { WriteIndented = true }));
            SetStatus("Session saved: " + System.IO.Path.GetFileName(dlg.FileName));
        }

        async void OpenSession_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "SpectroMark sync session|*.spectrosync.json" };
            if (dlg.ShowDialog() != true) return;
            await LoadSession(dlg.FileName);
        }

        public async Task LoadSession(string path)
        {
            var st = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(path));
            StopPlayback(); tracks.Clear(); viewLen = 0;
            if (Enum.TryParse<StretchMode>(st.Mode, out var m)) { mode = m; ModeBox.SelectedIndex = Array.IndexOf(StretchEngine.Modes, mode); }
            await AddFiles(st.Tracks.Select(t => t.Path).Where(File.Exists).ToArray());
            ApplyState(st);
            SetStatus("Session loaded: " + System.IO.Path.GetFileName(path));
        }

        // ---------------- export ----------------

        async void Export_Click(object sender, RoutedEventArgs e)
        {
            if (tracks.Count == 0) return;
            if (tracks.Any(t => t.Rendering || t.RenderedVersion != t.RenderVersion)) { SetStatus("Still rendering, try again in a moment."); return; }
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder for the synced WAVs", InitialDirectory = System.IO.Path.GetDirectoryName(tracks[0].Path) };
            if (dlg.ShowDialog() != true) return;
            string dir = dlg.FolderName;
            StopPlayback();
            long end = EndFrame; int sr = sampleRate;
            var snapshot = tracks.ToList();
            SetStatus("Exporting...");
            await Task.Run(() =>
            {
                foreach (var t in snapshot)
                {
                    string outp = System.IO.Path.Combine(dir, t.Name + "_synced.wav");
                    WriteWav24(outp, sr, t.Audio.ChannelCount, end, t);
                    var mk = t.Markers.Select(m => new Marker { Sample = t.SourceToTimeline(m.Sample), Name = m.Name }).Where(m => m.Sample >= 0 && m.Sample < end).ToList();
                    try { WavCues.Write(outp, mk); } catch { }
                }
                var mix = new SyncMixProvider(snapshot, sr, 0, end);
                string mixPath = System.IO.Path.Combine(dir, "MIX_synced.wav");
                var buf = new float[8192 * 2];
                using var w = new WaveFileWriter(mixPath, new WaveFormat(sr, 24, 2));
                int n;
                var bytes = new byte[buf.Length * 3];
                while ((n = mix.Read(buf, 0, buf.Length)) > 0)
                {
                    int nb = 0;
                    for (int i = 0; i < n; i++) { int v = (int)Math.Round(Math.Clamp(buf[i], -1f, 1f) * 8388607f); bytes[nb++] = (byte)v; bytes[nb++] = (byte)(v >> 8); bytes[nb++] = (byte)(v >> 16); }
                    w.Write(bytes, 0, nb);
                }
            });
            SetStatus($"Exported {snapshot.Count} synced WAV(s) + MIX_synced.wav to {dir}");
        }

        static void WriteWav24(string path, int sr, int ch, long frames, SyncTrack t)
        {
            using var w = new WaveFileWriter(path, new WaveFormat(sr, 24, ch));
            const int blk = 8192;
            var bytes = new byte[blk * ch * 3];
            var fl = new float[blk * ch];
            for (long i0 = 0; i0 < frames; i0 += blk)
            {
                int n = (int)Math.Min(blk, frames - i0);
                Array.Clear(fl, 0, n * ch);
                long local0 = i0 - t.Offset;
                if (local0 + n > 0 && local0 < t.RenderedLength)
                    for (int c = 0; c < ch; c++) t.AddInto(fl, c, c, local0, n, ch);
                int nb = 0;
                for (int i = 0; i < n * ch; i++)
                {
                    int v = (int)Math.Round(Math.Clamp(fl[i], -1f, 1f) * 8388607f);
                    bytes[nb++] = (byte)v; bytes[nb++] = (byte)(v >> 8); bytes[nb++] = (byte)(v >> 16);
                }
                w.Write(bytes, 0, nb);
            }
        }
    }

    /// <summary>A retained-mode layer drawn in one OnRender call; no per-item WPF elements.</summary>
    public class DrawLayer : FrameworkElement
    {
        public Action<DrawingContext> Draw;
        protected override void OnRender(DrawingContext dc) { base.OnRender(dc); Draw?.Invoke(dc); }
    }
}
