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
        readonly PlaybackEngine engine = new();
        long playStart;
        double laneHeight = 0;         // 0 = fit all lanes; otherwise fixed height per lane
        double vScroll = 0;
        readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(25) };
        StretchMode mode = StretchMode.Tonal;

        WriteableBitmap bmp; int[] px; double dpiScale = 1;
        WriteableBitmap navBmp; int[] navPx; bool navDirty;
        bool navDragging; double navDragOffset;
        readonly DispatcherTimer renderTimer = new() { Interval = TimeSpan.FromMilliseconds(15) };
        bool lanesDirty;

        // sync-click state
        bool sHeld;
        SyncTrack anchorTrack; long anchorTime;
        int groupCount;

        // mouse
        int pressLane = -1; double pressX; long pressOffset; bool draggingClip, pressed;
        int selectedLane = -1;
        SyncTrack tempSolo;            // lane soloed while S is held
        bool bandActive, bandDragging; Point bandStart, bandEnd;   // Ctrl+drag batch-sync selection
        List<(SyncTrack t, Marker m)> dragGroup; bool groupDragActive, groupDragging; double groupPressX; long groupPressTime;   // dragging a synced group along the timeline
        (SyncTrack t, Marker m) hover;

        // undo
        readonly Stack<string> undo = new(); readonly Stack<string> redo = new();

        public TextBlock TimeDisplay;     // set by MainWindow
        string sessionPath;
        bool dirty;
        public bool Dirty => dirty;
        public string SessionName => sessionPath == null ? null : System.IO.Path.GetFileName(sessionPath);
        public event Action DirtyChanged;
        void SetDirty(bool d) { dirty = d; DirtyText.Text = d ? "* unsaved" : ""; DirtyChanged?.Invoke(); }
        public event Action<string> StatusChanged;

        static readonly Color[] Palette = { Color.FromRgb(102, 217, 255), Color.FromRgb(255, 170, 80), Color.FromRgb(140, 255, 140), Color.FromRgb(255, 120, 200), Color.FromRgb(200, 170, 255), Color.FromRgb(255, 240, 120), Color.FromRgb(120, 230, 210), Color.FromRgb(255, 140, 140) };

        public SyncView()
        {
            InitializeComponent();
            foreach (var m in StretchEngine.Modes) ModeBox.Items.Add(m.ToString());
            ModeBox.SelectedIndex = Array.IndexOf(StretchEngine.Modes, mode);
            ModeBox.ToolTip = string.Join("\n", StretchEngine.Modes.Select(StretchEngine.Describe));
            timer.Tick += (_, _) => UpdatePlayhead();
            renderTimer.Tick += (_, _) => { renderTimer.Stop(); if (lanesDirty) RenderLanes(); if (navDirty) RenderNav(); lanesDirty = navDirty = false; };
            LaneHost.SizeChanged += (_, _) => { ClampVScroll(); InvalidateLanes(); RefreshOverlay(); RefreshPlayhead(); RebuildHeaders(); };
            Ruler.SizeChanged += (_, _) => DrawRuler();
            Overlay.Draw = DrawOverlay; PlayheadLayer.Draw = DrawPlayhead; NavOverlay.Draw = DrawNav;
            NavHost.SizeChanged += (_, _) => { InvalidateNav(); };
            engine.PlaybackEnded += () => Dispatcher.BeginInvoke(() => { if (playing && engine.IsPlaying == false) { } });
            Headers.MouseWheel += Headers_MouseWheel;
            Loaded += (_, _) => { var src = PresentationSource.FromVisual(this); if (src?.CompositionTarget != null) dpiScale = src.CompositionTarget.TransformToDevice.M11; };
        }

        public int TrackCount => tracks.Count;
        long EndFrame => tracks.Count == 0 ? 0 : tracks.Max(t => t.End);
        double LaneW => LaneHost.ActualWidth;
        double LaneH => tracks.Count == 0 ? LaneHost.ActualHeight : (laneHeight > 0 ? laneHeight : LaneHost.ActualHeight / tracks.Count);
        double LaneTop(int i) => i * LaneH - vScroll;
        int LaneAt(double y) { int i = (int)((y + vScroll) / LaneH); return i >= 0 && i < tracks.Count ? i : -1; }
        void ClampVScroll() { double total = tracks.Count * LaneH; vScroll = Math.Clamp(vScroll, 0, Math.Max(0, total - LaneHost.ActualHeight)); }
        double SampleToX(double s) => (s - viewStart) / viewLen * LaneW;
        double XToSample(double x) => viewStart + x / LaneW * viewLen;

        void SetStatus(string s) { Status.Text = s; StatusChanged?.Invoke(s); }

        void Warn(string msg)
        {
            SetStatus(msg);
            SyncHint.Foreground = Brushes.OrangeRed; SyncHint.Text = "⚠ " + msg;
            var tmr = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            tmr.Tick += (_, _) => { tmr.Stop(); SyncHint.Foreground = new SolidColorBrush(Color.FromRgb(255, 235, 59)); if (sHeld && group.Count > 0) ApplyGroup(); else if (sHeld) SyncHint.Text = "SYNC: click the first marker of a group"; else SyncHint.Text = ""; };
            tmr.Start();
        }

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
            RebuildHeaders(); ViewChanged(); InvalidateNav(); UpdateTime();
            if (!loadingSession && tracks.Count > 0) SetDirty(true);
            SetStatus($"{tracks.Count} clip(s)");
        }

        void RemoveTrack(SyncTrack t)
        {
            StopPlayback();
            tracks.Remove(t); SetDirty(true); if (selectedLane >= tracks.Count) selectedLane = tracks.Count - 1; if (tempSolo == t) tempSolo = null;
            RebuildHeaders(); ViewChanged(); InvalidateNav();
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
                    InvalidateLanes(); InvalidateNav(); RefreshOverlay(); RebuildHeaders(); UpdateScrollBar();
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
            Headers.Margin = new Thickness(0, -vScroll, 0, 0);
            for (int i = 0; i < tracks.Count; i++)
            {
                var t = tracks[i];
                var col = Palette[i % Palette.Length];
                var panel = new StackPanel { Margin = new Thickness(6, 4, 4, 0) };
                panel.Children.Add(new TextBlock { Text = t.Name, Foreground = new SolidColorBrush(col), FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = t.Path });
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
                var mute = new ToggleButton { Content = "M", Width = 26, Padding = new Thickness(0, 2, 0, 2), IsChecked = t.Mute, ToolTip = "Mute   (Ctrl+click: unmute all)" };
                mute.Click += (_, _) =>
                {
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { foreach (var o in tracks) o.Mute = false; RebuildHeaders(); InvalidateLanes(); SetStatus("All lanes unmuted"); return; }
                    t.Mute = mute.IsChecked == true; InvalidateLanes();
                };
                var solo = new ToggleButton { Content = "S", Width = 26, Padding = new Thickness(0, 2, 0, 2), IsChecked = t.Solo, Margin = new Thickness(3, 0, 0, 0), ToolTip = "Solo   (Ctrl+click: unsolo all)" };
                solo.Click += (_, _) =>
                {
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { foreach (var o in tracks) o.Solo = false; RebuildHeaders(); InvalidateLanes(); SetStatus("All lanes unsoloed"); return; }
                    t.Solo = solo.IsChecked == true; InvalidateLanes();
                };
                var rm = new Button { Content = "✕", Width = 26, Margin = new Thickness(3, 0, 0, 0), Padding = new Thickness(0, 2, 0, 2), ToolTip = "Remove clip" };
                rm.Click += (_, _) => RemoveTrack(t);
                row.Children.Add(mute); row.Children.Add(solo); row.Children.Add(rm);
                panel.Children.Add(row);
                panel.Children.Add(new TextBlock { Text = (tempSolo == t ? "SOLO  " : "") + (t.Rendering ? "rendering...  " : "") + $"{t.Points.Count} sync pt", Foreground = tempSolo == t ? Brushes.LightGreen : Brushes.Gray, FontSize = 10, Margin = new Thickness(0, 3, 0, 0) });
                bool selectedHdr = i == selectedLane;
                Headers.Children.Add(new Border { Height = h, Child = panel, BorderBrush = selectedHdr ? new SolidColorBrush(Color.FromRgb(79, 179, 232)) : new SolidColorBrush(Color.FromRgb(40, 40, 40)), BorderThickness = selectedHdr ? new Thickness(3, 0, 0, 1) : new Thickness(0, 0, 0, 1), Background = new SolidColorBrush(selectedHdr ? Color.FromRgb(34, 38, 46) : i % 2 == 0 ? Color.FromRgb(26, 26, 26) : Color.FromRgb(22, 22, 22)) });
            }
        }

        // ---------------- lane bitmap ----------------

        void InvalidateLanes() { lanesDirty = true; if (!renderTimer.IsEnabled) renderTimer.Start(); }
        void InvalidateNav() { navDirty = true; if (!renderTimer.IsEnabled) renderTimer.Start(); }

        // ---------------- navigator (whole project) ----------------

        double NavW => NavHost.ActualWidth;
        double NavSampleToX(double s) => EndFrame == 0 ? 0 : s / EndFrame * NavW;
        double NavXToSample(double x) => x / NavW * Math.Max(1, EndFrame);

        void RenderNav()
        {
            int W = (int)(NavW * dpiScale), H = (int)(NavHost.ActualHeight * dpiScale);
            if (W < 2 || H < 2) return;
            if (navBmp == null || navBmp.PixelWidth != W || navBmp.PixelHeight != H) { navBmp = new WriteableBitmap(W, H, 96, 96, PixelFormats.Bgra32, null); navPx = new int[W * H]; NavImage.Source = navBmp; }
            Array.Fill(navPx, unchecked((int)0xFF0C0C0C));
            int n = tracks.Count; long end = EndFrame;
            if (n > 0 && end > 0)
            {
                int laneH = Math.Max(2, H / n);
                for (int i = 0; i < n; i++)
                {
                    var t = tracks[i]; int top = i * laneH, center = top + laneH / 2, amp = Math.Max(1, laneH / 2 - 1);
                    var c = Palette[i % Palette.Length]; int col = (255 << 24) | (c.R << 16) | (c.G << 8) | c.B;
                    int Wl = W; var pxl = navPx;
                    Parallel.For(0, Wl, x =>
                    {
                        long tl0 = (long)((double)x / Wl * end), tl1 = Math.Max(tl0 + 1, (long)((double)(x + 1) / Wl * end));
                        long l0 = tl0 - t.Offset, l1 = tl1 - t.Offset;
                        if (l1 <= 0 || l0 >= t.RenderedLength) return;
                        t.Peak(Math.Max(0, l0), Math.Min(t.RenderedLength, l1), out float mn, out float mx);
                        int y0 = center - (int)(mx * amp), y1 = center - (int)(mn * amp);
                        for (int y = Math.Max(top, y0); y <= Math.Min(top + laneH - 1, y1); y++) pxl[y * Wl + x] = col;
                    });
                }
            }
            navBmp.WritePixels(new Int32Rect(0, 0, W, H), navPx, W * 4, 0);
            NavOverlay.InvalidateVisual();
        }

        static readonly Brush NavViewFill = Freeze(new SolidColorBrush(Color.FromArgb(50, 120, 180, 255)));
        static readonly Pen NavViewPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(200, 120, 180, 255)), 1));

        void DrawNav(DrawingContext dc)
        {
            if (tracks.Count == 0 || EndFrame == 0) return;
            double x0 = NavSampleToX(viewStart), x1 = NavSampleToX(viewStart + viewLen);
            dc.DrawRectangle(NavViewFill, NavViewPen, new Rect(x0, 0, Math.Max(2, x1 - x0), NavHost.ActualHeight));
            double px = NavSampleToX(playhead);
            dc.DrawLine(PlayheadPen, new Point(px, 0), new Point(px, NavHost.ActualHeight));
        }

        void Nav_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (tracks.Count == 0) return;
            var p = e.GetPosition(NavHost); double s = NavXToSample(p.X);
            if (s >= viewStart && s <= viewStart + viewLen && viewLen < EndFrame) { navDragging = true; navDragOffset = s - viewStart; NavHost.CaptureMouse(); }
            else { SeekTo((long)s); if (viewLen < EndFrame) { viewStart = s - viewLen / 2; ViewChanged(); } }
        }

        void Nav_MouseMove(object sender, MouseEventArgs e)
        {
            if (!navDragging) return;
            viewStart = NavXToSample(e.GetPosition(NavHost).X) - navDragOffset; ViewChanged();
        }

        void Nav_MouseUp(object sender, MouseButtonEventArgs e) { if (navDragging) { navDragging = false; NavHost.ReleaseMouseCapture(); } }

        void RenderLanes()
        {
            int W = (int)(LaneW * dpiScale), H = (int)(LaneHost.ActualHeight * dpiScale);
            if (W < 2 || H < 2) return;
            if (bmp == null || bmp.PixelWidth != W || bmp.PixelHeight != H) { bmp = new WriteableBitmap(W, H, 96, 96, PixelFormats.Bgra32, null); px = new int[W * H]; LaneImage.Source = bmp; }
            Array.Fill(px, unchecked((int)0xFF0C0C0C));
            int n = tracks.Count;
            if (n > 0 && viewLen > 0)
            {
                int laneH = Math.Max(8, (int)(LaneH * dpiScale));
                bool anySolo = tracks.Any(t => t.Solo);
                for (int i = 0; i < n; i++)
                {
                    var t = tracks[i];
                    int top = (int)(LaneTop(i) * dpiScale), center = top + laneH / 2;
                    if (top + laneH <= 0 || top >= H) continue;
                    bool selected = i == selectedLane;
                    int bg = selected ? unchecked((int)0xFF1a1c22) : i % 2 == 0 ? unchecked((int)0xFF101010) : unchecked((int)0xFF0C0C0C);
                    for (int y = Math.Max(0, top); y < Math.Min(H, top + laneH); y++) for (int x = 0; x < W; x++) px[y * W + x] = bg;
                    bool dim = t.Mute || (anySolo && !t.Solo) || (tempSolo != null && tempSolo != t);
                    var c = Palette[i % Palette.Length];
                    int col = dim ? unchecked((int)0xFF505050) : (255 << 24) | (c.R << 16) | (c.G << 8) | c.B;
                    int clipBg = dim ? unchecked((int)0xFF161616) : selected ? unchecked((int)0xFF22293a) : unchecked((int)0xFF1a1f26);
                    int amp = laneH / 2 - 4;
                    int Wl = W; var pxl = px;
                    Parallel.For(0, Wl, x =>
                    {
                        double tl0 = viewStart + (double)x / Wl * viewLen, tl1 = viewStart + (double)(x + 1) / Wl * viewLen;
                        long l0 = (long)(tl0 - t.Offset), l1 = Math.Max((long)(tl1 - t.Offset), (long)(tl0 - t.Offset) + 1);
                        if (l1 <= 0 || l0 >= t.RenderedLength) return;
                        for (int y = Math.Max(0, top); y < Math.Min(H, top + laneH); y++) pxl[y * Wl + x] = clipBg;
                        t.Peak(Math.Max(0, l0), Math.Min(t.RenderedLength, l1), out float mn, out float mx);
                        int y0 = center - (int)(mx * amp), y1 = center - (int)(mn * amp);
                        for (int y = Math.Max(Math.Max(0, top), y0); y <= Math.Min(Math.Min(H - 1, top + laneH - 1), y1); y++) pxl[y * Wl + x] = col;
                    });
                    // lane separator: 2 px, clearly visible but quiet
                    for (int d = 1; d <= 2; d++) { int sep = top + laneH - d; if (sep >= 0 && sep < H) for (int x = 0; x < W; x++) px[sep * W + x] = d == 1 ? unchecked((int)0xFF3a3a3a) : unchecked((int)0xFF262626); }
                }
            }
            if (n > 0 && viewLen > 0)
            {
                double secs = viewLen / sampleRate;
                double[] steps = { 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 };
                double step = steps[^1];
                foreach (var st in steps) if (st / secs * LaneW >= 70) { step = st; break; }
                double t0 = Math.Floor(viewStart / sampleRate / step) * step;
                for (double t = t0; t <= (viewStart + viewLen) / sampleRate; t += step)
                {
                    int gx = (int)(SampleToX(t * sampleRate) * dpiScale);
                    if (gx < 0 || gx >= W) continue;
                    for (int y = 0; y < H; y++) { int v = px[y * W + gx]; px[y * W + gx] = v == unchecked((int)0xFF101010) || v == unchecked((int)0xFF0C0C0C) || v == unchecked((int)0xFF1a1f26) || v == unchecked((int)0xFF161616) ? unchecked((int)0xFF222830) : v; }
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
        static readonly Brush HotBrush = Freeze(new SolidColorBrush(Color.FromRgb(255, 70, 70)));
        static readonly Pen HotPen = Freeze(new Pen(HotBrush, 3));
        static readonly Brush BandHotRed = Freeze(new SolidColorBrush(Color.FromArgb(70, 255, 70, 70)));
        static readonly Pen AnchorPen = Freeze(new Pen(Brushes.White, 2) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) });
        static readonly Pen PlayheadPen = Freeze(new Pen(Brushes.White, 1.5));
        const double HitPx = 12;
        static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

        void RefreshOverlay() => Overlay.InvalidateVisual();
        void RefreshPlayhead() { PlayheadLayer.InvalidateVisual(); NavOverlay.InvalidateVisual(); }

        /// <summary>Marker prominence by zoom: nearly invisible when the whole song is on screen, clear when zoomed in.</summary>
        double MarkerAlpha()
        {
            double secs = viewLen / sampleRate;
            // 8 s visible -> 0.85, 240 s visible -> 0.08, log-interpolated
            double t = Math.Clamp((Math.Log(secs) - Math.Log(8)) / (Math.Log(240) - Math.Log(8)), 0, 1);
            return 0.85 - t * 0.77;
        }

        void DrawOverlay(DrawingContext dc)
        {
            if (tracks.Count == 0 || viewLen <= 0) return;
            double h = LaneH, W = LaneW;
            double alpha = MarkerAlpha();
            var markerBrush = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), 232, 205, 80)); markerBrush.Freeze();
            var markerPen = new Pen(markerBrush, 1); markerPen.Freeze();
            var syncedBrush = new SolidColorBrush(Color.FromArgb((byte)(Math.Max(0.35, alpha) * 255), 90, 255, 255)); syncedBrush.Freeze();
            var syncedPen = new Pen(syncedBrush, 2); syncedPen.Freeze();
            for (int i = 0; i < tracks.Count; i++)
            {
                var t = tracks[i]; double top = LaneTop(i);
                if (top + h < 0 || top > LaneHost.ActualHeight) continue;
                foreach (var m in t.Markers)
                {
                    double x = SampleToX(t.SourceToTimeline(m.Sample));
                    if (x < -HitPx || x > W + HitPx) continue;
                    bool synced = t.PointAt(m.Sample) != null;
                    bool hot = hover.t == t && hover.m == m;
                    if (sHeld || hot) dc.DrawRectangle(hot ? BandHotRed : BandBrush, null, new Rect(x - HitPx, top, HitPx * 2, h));
                    dc.DrawLine(hot ? HotPen : synced ? syncedPen : markerPen, new Point(x, top), new Point(x, top + h));
                    var g = new StreamGeometry();
                    using (var ctx = g.Open())
                    {
                        if (synced) { ctx.BeginFigure(new Point(x, top + 2), true, true); ctx.LineTo(new Point(x + 6, top + 8), false, false); ctx.LineTo(new Point(x, top + 14), false, false); ctx.LineTo(new Point(x - 6, top + 8), false, false); }
                        else { ctx.BeginFigure(new Point(x, top), true, true); ctx.LineTo(new Point(x + 9, top), false, false); ctx.LineTo(new Point(x + 9, top + 7), false, false); ctx.LineTo(new Point(x, top + 11), false, false); }
                    }
                    g.Freeze();
                    if (alpha > 0.25 || synced || hot) dc.DrawGeometry(hot ? HotBrush : synced ? syncedBrush : markerBrush, null, g);
                }
            }
            if (anchorTrack != null)
            {
                double ax = SampleToX(anchorTime);
                dc.DrawLine(AnchorPen, new Point(ax, 0), new Point(ax, LaneHost.ActualHeight));
            }
            if (bandActive && bandDragging)
            {
                var r = new Rect(bandStart, bandEnd);
                dc.DrawRectangle(BandSelFill, BandSelPen, r);
            }
        }

        static readonly Brush BandSelFill = Freeze(new SolidColorBrush(Color.FromArgb(40, 79, 179, 232)));
        static readonly Pen BandSelPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(200, 79, 179, 232)), 1) { DashStyle = new DashStyle(new double[] { 3, 2 }, 0) });

        /// <summary>
        /// Batch sync: every lane touched by the rectangle contributes the markers inside its time range, in order.
        /// The k-th marker of each lane forms group k; each group is centred on its own (robust centre of the
        /// original positions) and applied in time order. One undo step.
        /// </summary>
        void BatchSync(Point a, Point b)
        {
            if (tracks.Count == 0) return;
            double x0 = Math.Min(a.X, b.X), x1 = Math.Max(a.X, b.X), y0 = Math.Min(a.Y, b.Y), y1 = Math.Max(a.Y, b.Y);
            long t0 = (long)XToSample(x0), t1 = (long)XToSample(x1);
            var lanes = new List<(SyncTrack t, List<Marker> ms, long baseOffset, bool hadPoints)>();
            for (int i = 0; i < tracks.Count; i++)
            {
                double top = LaneTop(i), bot = top + LaneH;
                if (bot < y0 || top > y1) continue;
                var t = tracks[i];
                var ms = t.Markers.Where(m => { long tl = t.SourceToTimeline(m.Sample); return tl >= t0 && tl <= t1; }).OrderBy(m => m.Sample).ToList();
                if (ms.Count > 0) lanes.Add((t, ms, t.Offset, t.Points.Count > 0));
            }
            if (lanes.Count < 2) { Warn("Select a range covering markers on at least two lanes."); return; }
            int groups = lanes.Max(l => l.ms.Count);
            int minCount = lanes.Min(l => l.ms.Count);
            PushUndo();
            // original positions for every marker, captured before anything moves
            var baseTimes = lanes.ToDictionary(l => l.t, l => l.ms.Select(m => l.t.SourceToTimeline(m.Sample)).ToList());
            for (int k = 0; k < groups; k++)
            {
                var members = lanes.Where(l => k < l.ms.Count).ToList();
                if (members.Count < 2) continue;
                long centre = RobustCentre(members.Select(l => baseTimes[l.t][k]).ToList());
                foreach (var l in members)
                {
                    var t = l.t; var m = l.ms[k]; long bt = baseTimes[t][k];
                    if (t.Points.Count == 0)
                    {
                        t.Offset = t.Offset + (centre - t.SourceToTimeline(m.Sample));
                        t.AddOrUpdatePoint(m.Sample, m.Sample, bt);
                    }
                    else t.AddOrUpdatePoint(m.Sample, centre - t.Offset, bt);
                }
            }
            foreach (var l in lanes) ScheduleRender(l.t);
            RefreshOverlay(); RebuildHeaders(); UpdateScrollBar(); UpdateTime(); InvalidateNav();
            string note = minCount != groups ? $"   (lanes had between {minCount} and {groups} markers in the range: check the alignment)" : "";
            SetStatus($"Batch sync: {groups} group(s) across {lanes.Count} lanes" + note);
            SyncHint.Text = $"Batch sync: {groups} group(s) across {lanes.Count} lanes" + note;
        }

        static readonly Pen PlayheadGlow = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), 7));
        static readonly Pen PlayheadCore = Freeze(new Pen(Brushes.White, 2));

        void DrawPlayhead(DrawingContext dc)
        {
            if (tracks.Count == 0 || viewLen <= 0) return;
            double x = SampleToX(playhead), H = LaneHost.ActualHeight;
            if (x < 0 || x > LaneW) return;
            dc.DrawLine(PlayheadGlow, new Point(x, 0), new Point(x, H));
            dc.DrawLine(PlayheadCore, new Point(x, 0), new Point(x, H));
            var g = new StreamGeometry();
            using (var c = g.Open()) { c.BeginFigure(new Point(x - 7, 0), true, true); c.LineTo(new Point(x + 7, 0), false, false); c.LineTo(new Point(x, 9), false, false); }
            g.Freeze();
            dc.DrawGeometry(Brushes.White, null, g);
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
            if (!Finite(viewLen) || viewLen <= 0) viewLen = total;
            if (!Finite(viewStart)) viewStart = 0;
            viewLen = Math.Clamp(viewLen, sampleRate * 0.2, total);
            viewStart = Math.Clamp(viewStart, 0, Math.Max(0, total - viewLen));
            InvalidateLanes(); RefreshOverlay(); RefreshPlayhead(); DrawRuler(); UpdateScrollBar(); NavOverlay.InvalidateVisual();
        }

        void UpdateScrollBar()
        {
            double total = Math.Max(1, EndFrame);
            HScroll.Minimum = 0; HScroll.Maximum = Math.Max(0, total - viewLen);
            HScroll.ViewportSize = viewLen; HScroll.LargeChange = viewLen * 0.9; HScroll.SmallChange = viewLen * 0.1; HScroll.Value = viewStart;
        }

        void HScroll_Scroll(object sender, ScrollEventArgs e) { viewStart = e.NewValue; ViewChanged(); }

        /// <summary>Zoom about the playhead: the view is centred on it, then scaled.</summary>
        void ZoomAt(double x, double factor) => ZoomOnPlayhead(factor);

        void ZoomOnPlayhead(double factor)
        {
            double centre = CurrentSample();
            viewLen /= factor;
            viewStart = centre - viewLen / 2;
            ViewChanged();
        }

        // ---------------- playback ----------------

        void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

        bool followPlayhead;
        void Follow_Click(object sender, RoutedEventArgs e)
        {
            followPlayhead = FollowBtn.IsChecked == true;
            FollowBtn.Content = followPlayhead ? "Scroll" : "Page";
            if (followPlayhead && playing) { viewStart = playhead - viewLen / 2; ViewChanged(); }
        }

        public void TogglePlay() { if (tracks.Count == 0) return; if (playing) StopPlayback(); else StartPlayback(); }

        void StartPlayback()
        {
            if (tracks.Count == 0) return;
            if (playhead >= EndFrame - 1) playhead = 0;
            var prov = new SyncMixProvider(tracks, sampleRate, playhead, EndFrame) { TempSolo = () => tempSolo };
            playStart = playhead;
            engine.Start(prov);
            playing = true; timer.Start(); PlayBtn.Content = "Pause  (Space)";
        }

        /// <summary>Stop and return the playhead to where playback started (Space).</summary>
        void StopAndReturn()
        {
            if (!playing) return;
            StopPlayback();
            SeekTo(playStart);
        }

        public void StopPlayback()
        {
            if (playing) playhead = CurrentSample();
            engine.Stop();
            playing = false; timer.Stop(); PlayBtn.Content = "Play  (Space)";
        }

        long CurrentSample() => !playing ? playhead : Math.Min(EndFrame, playStart + engine.FramesPlayed);

        void UpdatePlayhead()
        {
            if (!playing) return;
            playhead = CurrentSample();
            if (playhead >= EndFrame) { StopPlayback(); playhead = EndFrame; RefreshPlayhead(); UpdateTime(); return; }
            if (followPlayhead) { viewStart = playhead - viewLen / 2; ViewChanged(); }
            else if (playhead > viewStart + viewLen || playhead < viewStart) { viewStart = playhead - viewLen * 0.05; ViewChanged(); }
            RefreshPlayhead(); UpdateTime();
            if (engine.Underruns > 0 && !Status.Text.StartsWith("Audio underruns")) SetStatus($"Audio underruns: {engine.Underruns} ({engine.DeviceName})");
        }

        void SeekTo(long s)
        {
            bool was = playing; if (was) StopPlayback();
            playhead = Math.Clamp(s, 0, Math.Max(0, EndFrame));
            if (was) StartPlayback();
            if (followPlayhead) { viewStart = playhead - viewLen / 2; ViewChanged(); }   // scroll mode: playhead stays centred, content moves
            RefreshPlayhead(); UpdateTime();
        }

        // ---------------- keyboard (routed from MainWindow) ----------------

        public bool HandleKey(KeyEventArgs e, bool down)
        {
            if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl)
            {
                if (down && !sHeld) { sHeld = true; anchorTrack = null; group.Clear(); groupCount = 0; SyncHint.Text = "SYNC: click the first marker of a group"; Mouse.OverrideCursor = Cursors.Cross; RefreshOverlay(); }
                else if (!down) { sHeld = false; anchorTrack = null; group.Clear(); SyncHint.Text = ""; Mouse.OverrideCursor = null; RefreshOverlay(); }
                return false;   // let Ctrl combos (Ctrl+Z, Ctrl+S ...) still work
            }
            if (e.Key == Key.LeftShift || e.Key == Key.RightShift)
            {
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return false;   // Ctrl+Shift combos untouched
                var sel = selectedLane >= 0 && selectedLane < tracks.Count ? tracks[selectedLane] : null;
                if (down) { if (tempSolo == null && sel != null) { tempSolo = sel; SetStatus($"Solo (held): {sel.Name}"); InvalidateLanes(); RebuildHeaders(); } }
                else if (tempSolo != null) { tempSolo = null; SetStatus(""); InvalidateLanes(); RebuildHeaders(); }
                return false;   // Shift stays unhandled so Shift+wheel and Shift+arrows still work
            }
            if (!down) return false;
            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            switch (e.Key)
            {
                case Key.Space: if (tracks.Count == 0) return true; if (playing) StopAndReturn(); else StartPlayback(); return true;
                case Key.Enter: if (tracks.Count == 0) return true; if (playing) StopPlayback(); else StartPlayback(); return true;
                case Key.M: AddMarkerToSelected(); return true;
                case Key.Delete: DeleteHoveredMarker(); return true;
                case Key.S when ctrl && shift: SaveSession(true); return true;
                case Key.S when ctrl: SaveSession(false); return true;
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
            int lane = LaneAt(p.Y); if (lane < 0) return (null, null);
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
                if (sHeld) { bandActive = true; bandDragging = false; bandStart = bandEnd = p; LaneHost.CaptureMouse(); return; }
                if (m != null && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
                {
                    var pt = t.PointAt(m.Sample);
                    if (pt != null) { PushUndo(); t.RemovePoint(pt); ScheduleRender(t); RefreshOverlay(); RebuildHeaders(); SetStatus($"Removed sync point on {t.Name}"); }
                    return;
                }
                if (m != null && t.PointAt(m.Sample) != null)
                {
                    // press on a synced marker: drag the whole group along the timeline
                    long time = t.SourceToTimeline(m.Sample); long tol = Math.Max(2, sampleRate / 500);
                    dragGroup = new List<(SyncTrack, Marker)>();
                    foreach (var other in tracks)
                        foreach (var om in other.Markers)
                            if (other.PointAt(om.Sample) != null && Math.Abs(other.SourceToTimeline(om.Sample) - time) <= tol) { dragGroup.Add((other, om)); break; }
                    groupDragActive = true; groupDragging = false; groupPressX = p.X; groupPressTime = time;
                    LaneHost.CaptureMouse();
                    return;
                }
                int lane = LaneAt(p.Y); if (lane < 0) return;
                if (selectedLane != lane) { selectedLane = lane; InvalidateLanes(); RebuildHeaders(); }
                pressed = true; pressLane = lane; pressX = p.X; pressOffset = tracks[lane].Offset; draggingClip = false;
                LaneHost.CaptureMouse();
            }
            else if (e.ChangedButton == MouseButton.Middle) { pressed = true; pressLane = -1; pressX = p.X; LaneHost.CaptureMouse(); }
        }

        void Lane_MouseMove(object sender, MouseEventArgs e)
        {
            if (tracks.Count == 0) return;
            var p = e.GetPosition(LaneHost);
            if (groupDragActive)
            {
                if (!groupDragging && Math.Abs(p.X - groupPressX) >= 4) { PushUndo(); groupDragging = true; Mouse.OverrideCursor = Cursors.SizeWE; }
                if (groupDragging)
                {
                    long newTime = groupPressTime + (long)((p.X - groupPressX) / LaneW * viewLen);
                    foreach (var (gt, gm) in dragGroup) gt.AddOrUpdatePoint(gm.Sample, newTime - gt.Offset);
                    anchorTime = newTime;
                    RefreshOverlay(); UpdateScrollBar(); UpdateTime();
                    SyncHint.Text = $"Moving group of {dragGroup.Count} lane(s) to {FormatTime((double)newTime / sampleRate)}";
                }
                return;
            }
            if (bandActive)
            {
                bandEnd = p;
                if (!bandDragging && (Math.Abs(p.X - bandStart.X) >= 4 || Math.Abs(p.Y - bandStart.Y) >= 4)) bandDragging = true;
                RefreshOverlay();
                return;
            }
            if (pressed && pressLane >= 0)
            {
                if (!draggingClip && Math.Abs(p.X - pressX) >= 4) { PushUndo(); draggingClip = true; }
                if (draggingClip)
                {
                    var t = tracks[pressLane];
                    t.Offset = pressOffset + (long)((p.X - pressX) / LaneW * viewLen);
                    InvalidateLanes(); InvalidateNav(); RefreshOverlay(); UpdateScrollBar(); UpdateTime();
                }
                return;
            }
            if (pressed && pressLane < 0) { viewStart -= (p.X - pressX) / LaneW * viewLen; pressX = p.X; ViewChanged(); return; }
            var h = HitMarker(p);
            if (h.Item1 != hover.t || h.Item2 != hover.m) { hover = h; RefreshOverlay(); }
            if (!sHeld) Mouse.OverrideCursor = h.Item2 != null ? (h.Item1.PointAt(h.Item2.Sample) != null ? Cursors.SizeWE : Cursors.Hand) : null;
        }

        void Lane_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (groupDragActive && e.ChangedButton == MouseButton.Left)
            {
                groupDragActive = false; LaneHost.ReleaseMouseCapture(); Mouse.OverrideCursor = null;
                if (groupDragging)
                {
                    foreach (var (gt, _) in dragGroup) ScheduleRender(gt);
                    InvalidateLanes(); InvalidateNav(); RefreshOverlay(); RebuildHeaders();
                    SetStatus($"Group moved to {FormatTime((double)anchorTime / sampleRate)}");
                    if (!sHeld) { anchorTrack = null; SyncHint.Text = ""; }
                }
                else { int lane = LaneAt(e.GetPosition(LaneHost).Y); if (lane >= 0 && selectedLane != lane) { selectedLane = lane; InvalidateLanes(); RebuildHeaders(); } }
                groupDragging = false; dragGroup = null;
                return;
            }
            if (bandActive && e.ChangedButton == MouseButton.Left)
            {
                bandActive = false; LaneHost.ReleaseMouseCapture();
                if (bandDragging) BatchSync(bandStart, bandEnd);
                bandDragging = false; RefreshOverlay();
                return;
            }
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
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { AdjustLaneHeight(e.Delta > 0 ? 1.2 : 1 / 1.2, p.Y); }
            else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { viewStart -= Math.Sign(e.Delta) * viewLen * 0.15; ViewChanged(); }
            else ZoomAt(p.X, e.Delta > 0 ? 1.3 : 1 / 1.3);
            e.Handled = true;
        }

        // wheel over the track headers: scroll lanes vertically; Ctrl+wheel: lane height (like Reaper)
        void Headers_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (tracks.Count == 0) return;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) AdjustLaneHeight(e.Delta > 0 ? 1.2 : 1 / 1.2, LaneHost.ActualHeight / 2);
            else { vScroll -= Math.Sign(e.Delta) * LaneH * 0.5; ClampVScroll(); InvalidateLanes(); RefreshOverlay(); RefreshPlayhead(); RebuildHeaders(); }
            e.Handled = true;
        }

        void AdjustLaneHeight(double factor, double anchorY)
        {
            double old = LaneH;
            double fit = LaneHost.ActualHeight / Math.Max(1, tracks.Count);
            double nh = Math.Clamp(old * factor, 36, 600);
            if (Math.Abs(nh - fit) < 4) nh = fit;
            laneHeight = nh == fit ? 0 : nh;
            // keep the lane under the cursor in place
            vScroll = (anchorY + vScroll) * (LaneH / old) - anchorY;
            ClampVScroll();
            InvalidateLanes(); RefreshOverlay(); RefreshPlayhead(); RebuildHeaders();
        }

        // ---------------- manual markers ----------------

        void AddMarkerToSelected()
        {
            if (selectedLane < 0 || selectedLane >= tracks.Count) { Warn("Select a lane first (click it), then press M to add a marker at the playhead."); return; }
            var t = tracks[selectedLane];
            long pos = playing ? CurrentSample() : playhead;
            long src = t.TimelineToSource(pos);
            if (src < 0 || src >= t.Audio.Length) { Warn("The playhead is outside that clip."); return; }
            if (t.Markers.Any(m => Math.Abs(m.Sample - src) < sampleRate * 0.01)) return;
            PushUndo();
            t.Markers.Add(new Marker { Sample = src }); t.Markers.Sort((a, b) => a.Sample.CompareTo(b.Sample));
            t.MarkersDirty = true;
            RefreshOverlay(); RebuildHeaders();
            SetStatus($"Marker added to {t.Name} @ {FormatTime((double)pos / sampleRate)}  (written into the WAV on save)");
        }

        void DeleteHoveredMarker()
        {
            if (hover.m == null || hover.t == null) { Warn("Hover a marker and press Delete to remove it."); return; }
            var t = hover.t; var m = hover.m;
            PushUndo();
            var pt = t.PointAt(m.Sample); if (pt != null) t.RemovePoint(pt);
            t.Markers.Remove(m); t.MarkersDirty = true; hover = (null, null);
            ScheduleRender(t); RefreshOverlay(); RebuildHeaders();
            SetStatus($"Marker removed from {t.Name}");
        }

        /// <summary>Write changed marker lists back into their WAV files (cue chunks).</summary>
        int SaveMarkersToWavs()
        {
            int n = 0;
            foreach (var t in tracks.Where(t => t.MarkersDirty))
            {
                try { WavCues.Write(t.Path, t.Markers); t.MarkersDirty = false; n++; }
                catch (Exception ex) { MessageBox.Show($"Could not write markers into {t.Name}: {ex.Message}", "SpectroMark", MessageBoxButton.OK, MessageBoxImage.Error); }
            }
            return n;
        }

        void SaveMarkers_Click(object sender, RoutedEventArgs e)
        {
            bool was = playing; if (was) StopPlayback();
            int n = SaveMarkersToWavs();
            SetStatus(n > 0 ? $"Markers written into {n} WAV file(s)" : "No marker changes to write");
            if (was) StartPlayback();
        }

        // ---------------- sync logic ----------------

        // A sync group: every Ctrl-click adds that lane's marker; the group's time is the robust centre of all
        // members' ORIGINAL positions (before this group changed anything) and every member is re-snapped to it.
        class GroupMember { public SyncTrack Track; public Marker Marker; public long BaseTime; public long BaseOffset; public bool HadPoints; }
        readonly List<GroupMember> group = new();

        void SyncClick(SyncTrack t, Marker m)
        {
            if (group.Count == 0) PushUndo();                      // one undo step for the whole group
            var existing = group.FirstOrDefault(g => g.Track == t);
            if (existing != null && existing.Marker == m) return;
            if (existing != null)
            {
                // different marker on a lane already in the group: swap it (restore the lane's base state first)
                RestoreMember(existing);
                group.Remove(existing);
            }
            var pt = t.PointAt(m.Sample);
            if (group.Count == 0 && pt != null)
            {
                // clicked an already-synced marker: adopt its whole group (every lane synced at that same time)
                long time = t.SourceToTimeline(m.Sample);
                long tol = Math.Max(2, sampleRate / 500);
                foreach (var other in tracks)
                {
                    if (other == t) continue;
                    foreach (var om in other.Markers)
                    {
                        var op = other.PointAt(om.Sample);
                        if (op == null) continue;
                        if (Math.Abs(other.SourceToTimeline(om.Sample) - time) <= tol)
                        {
                            group.Add(new GroupMember { Track = other, Marker = om, BaseTime = op.BaseTime >= 0 ? op.BaseTime : other.SourceToTimeline(om.Sample), BaseOffset = other.Offset, HadPoints = true });
                            break;
                        }
                    }
                }
            }
            long baseTime = pt != null && pt.BaseTime >= 0 ? pt.BaseTime : t.SourceToTimeline(m.Sample);
            group.Add(new GroupMember { Track = t, Marker = m, BaseTime = baseTime, BaseOffset = t.Offset, HadPoints = t.Points.Count > 0 });
            ApplyGroup();
        }

        void RestoreMember(GroupMember g)
        {
            var pt = g.Track.PointAt(g.Marker.Sample);
            if (pt != null) g.Track.RemovePoint(pt);
            if (!g.HadPoints) { g.Track.Offset = g.BaseOffset; g.Track.Points.Clear(); g.Track.RenderVersion++; }
        }

        static long RobustCentre(List<long> xs)
        {
            if (xs.Count == 1) return xs[0];
            var sorted = xs.OrderBy(v => v).ToList();
            double median = sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;
            var keep = new List<long>(xs);
            if (xs.Count >= 4)
            {
                // drop the single worst outlier if it is far from everyone else
                var devs = xs.Select(v => Math.Abs(v - median)).OrderBy(v => v).ToList();
                double typical = devs[devs.Count / 2];
                var worst = xs.OrderByDescending(v => Math.Abs(v - median)).First();
                if (Math.Abs(worst - median) > Math.Max(typical * 2.5, 1)) keep.Remove(worst);
            }
            return (long)Math.Round(keep.Average());
        }

        void ApplyGroup()
        {
            if (group.Count == 0) return;
            long centre = RobustCentre(group.Select(g => g.BaseTime).ToList());
            anchorTime = centre; anchorTrack = group[0].Track;
            foreach (var g in group)
            {
                var t = g.Track;
                if (!g.HadPoints)
                {
                    // first sync point on this lane: slide the clip, pin the marker
                    t.Offset = g.BaseOffset + (centre - g.BaseTime);
                    t.Points.Clear();
                    t.AddOrUpdatePoint(g.Marker.Sample, g.Marker.Sample, g.BaseTime);
                }
                else
                {
                    t.AddOrUpdatePoint(g.Marker.Sample, centre - t.Offset, g.BaseTime);
                }
                ScheduleRender(t);
            }
            groupCount = group.Count;
            RefreshOverlay(); RebuildHeaders(); UpdateScrollBar(); UpdateTime(); InvalidateNav();
            var spread = group.Count > 1 ? (group.Max(g => g.BaseTime) - group.Min(g => g.BaseTime)) / (double)sampleRate : 0;
            SyncHint.Text = $"SYNC: {group.Count} lane(s) centred @ {FormatTime((double)centre / sampleRate)}   spread {spread * 1000:0} ms" + (group.Count > 1 && group.All(g => g.HadPoints) ? "   (existing group re-opened)" : "");
        }

        // ---------------- undo / session ----------------

        class TrackState { public string Path { get; set; } public long Offset { get; set; } public bool Mute { get; set; } public bool Solo { get; set; } public List<long[]> Points { get; set; } = new(); public List<long> Markers { get; set; } public List<string> MarkerNames { get; set; } }
        class SessionState
        {
            public int SampleRate { get; set; } public string Mode { get; set; } public List<TrackState> Tracks { get; set; } = new();
            // view state (only written to the session file, ignored by undo)
            public double ViewStart { get; set; } public double ViewLen { get; set; } public long Playhead { get; set; }
            public double VScroll { get; set; } public double LaneHeight { get; set; } public bool Follow { get; set; }
            public double WinLeft { get; set; } public double WinTop { get; set; } public double WinWidth { get; set; } public double WinHeight { get; set; } public bool WinMaximized { get; set; }
        }

        static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        SessionState CaptureFull()
        {
            var st = Capture();
            st.ViewStart = viewStart; st.ViewLen = viewLen; st.Playhead = playhead; st.VScroll = vScroll; st.LaneHeight = laneHeight; st.Follow = followPlayhead;
            var w = Window.GetWindow(this);
            if (w != null)
            {
                var b = w.WindowState == WindowState.Normal ? new Rect(w.Left, w.Top, w.Width, w.Height) : w.RestoreBounds;
                if (b.IsEmpty || !Finite(b.Left) || !Finite(b.Top) || !Finite(b.Width) || !Finite(b.Height)) b = new Rect(0, 0, w.ActualWidth, w.ActualHeight);
                st.WinLeft = b.Left; st.WinTop = b.Top; st.WinWidth = b.Width; st.WinHeight = b.Height; st.WinMaximized = w.WindowState == WindowState.Maximized;
            }
            if (!Finite(st.ViewStart)) st.ViewStart = 0;
            if (!Finite(st.ViewLen) || st.ViewLen <= 0) st.ViewLen = Math.Max(1, EndFrame);
            if (!Finite(st.VScroll)) st.VScroll = 0;
            if (!Finite(st.LaneHeight)) st.LaneHeight = 0;
            return st;
        }

        void ApplyView(SessionState st)
        {
            if (st.ViewLen > 0) { viewStart = st.ViewStart; viewLen = st.ViewLen; }
            playhead = Math.Clamp(st.Playhead, 0, Math.Max(0, EndFrame));
            vScroll = st.VScroll; laneHeight = st.LaneHeight; followPlayhead = st.Follow; FollowBtn.IsChecked = followPlayhead; FollowBtn.Content = followPlayhead ? "Scroll" : "Page";
            var w = Window.GetWindow(this);
            if (w != null && st.WinWidth > 200 && st.WinHeight > 150)
            {
                // only restore onto a position that is actually on a screen
                var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
                if (vs.IntersectsWith(new Rect(st.WinLeft, st.WinTop, st.WinWidth, st.WinHeight)))
                {
                    w.WindowState = WindowState.Normal;
                    w.Left = st.WinLeft; w.Top = st.WinTop; w.Width = st.WinWidth; w.Height = st.WinHeight;
                    if (st.WinMaximized) w.WindowState = WindowState.Maximized;
                }
            }
            ClampVScroll(); ViewChanged(); RefreshPlayhead(); UpdateTime(); RebuildHeaders();
        }

        SessionState Capture() => new()
        {
            SampleRate = sampleRate, Mode = mode.ToString(),
            Tracks = tracks.Select(t => new TrackState { Path = t.Path, Offset = t.Offset, Mute = t.Mute, Solo = t.Solo, Points = t.Points.Select(p => new[] { p.Source, p.Target, p.BaseTime }).ToList(), Markers = t.Markers.Select(m => m.Sample).ToList(), MarkerNames = t.Markers.Select(m => m.Name ?? "").ToList() }).ToList()
        };

        void ApplyState(SessionState st)
        {
            foreach (var ts in st.Tracks)
            {
                var t = tracks.FirstOrDefault(x => string.Equals(x.Path, ts.Path, StringComparison.OrdinalIgnoreCase));
                if (t == null) continue;
                t.Offset = ts.Offset; t.Mute = ts.Mute; t.Solo = ts.Solo;
                if (ts.Markers != null && !loadingSession)
                {
                    var restored = ts.Markers.Select((smp, i) => new Marker { Sample = smp, Name = ts.MarkerNames != null && i < ts.MarkerNames.Count ? ts.MarkerNames[i] : "" }).ToList();
                    if (restored.Count != t.Markers.Count || restored.Zip(t.Markers).Any(z => z.First.Sample != z.Second.Sample)) { t.Markers = restored; t.MarkersDirty = true; }
                }
                t.Points = ts.Points.Select(p => new StretchPoint { Source = p[0], Target = p[1], BaseTime = p.Length > 2 ? p[2] : -1 }).OrderBy(p => p.Source).ToList();
                t.RenderVersion++;
                ScheduleRender(t);
            }
            RebuildHeaders(); ViewChanged(); InvalidateNav(); UpdateTime();
        }

        void PushUndo() { undo.Push(JsonSerializer.Serialize(Capture())); redo.Clear(); SetDirty(true); }
        void Undo() { if (undo.Count == 0) return; redo.Push(JsonSerializer.Serialize(Capture())); ApplyState(JsonSerializer.Deserialize<SessionState>(undo.Pop())); SetDirty(true); SetStatus($"Undo ({undo.Count} left)"); }
        void Redo() { if (redo.Count == 0) return; undo.Push(JsonSerializer.Serialize(Capture())); ApplyState(JsonSerializer.Deserialize<SessionState>(redo.Pop())); SetDirty(true); SetStatus("Redo"); }

        void SaveSession_Click(object sender, RoutedEventArgs e) => SaveSession(false);
        void SaveSessionAs_Click(object sender, RoutedEventArgs e) => SaveSession(true);

        public void SaveSession(bool askPath)
        {
            if (tracks.Count == 0) return;
            if (askPath || sessionPath == null)
            {
                var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "SpectroMark sync session|*.spectrosync.json", FileName = sessionPath != null ? System.IO.Path.GetFileName(sessionPath) : "sync-session.spectrosync.json", InitialDirectory = sessionPath != null ? System.IO.Path.GetDirectoryName(sessionPath) : System.IO.Path.GetDirectoryName(tracks[0].Path) };
                if (dlg.ShowDialog() != true) return;
                sessionPath = dlg.FileName;
            }
            try
            {
                var opts = new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
                File.WriteAllText(sessionPath, JsonSerializer.Serialize(CaptureFull(), opts));
                bool was = playing; if (was) StopPlayback();
                int n = SaveMarkersToWavs();
                if (was) StartPlayback();
                SetDirty(false);
                SetStatus("Session saved: " + System.IO.Path.GetFileName(sessionPath) + (n > 0 ? $"   (markers written into {n} WAV file(s))" : ""));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not save the session: " + ex.Message, "SpectroMark", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        async void OpenSession_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "SpectroMark sync session|*.spectrosync.json" };
            if (dlg.ShowDialog() != true) return;
            await LoadSession(dlg.FileName);
        }

        bool loadingSession;

        /// <summary>Ask about unsaved changes. Returns false if the user cancelled.</summary>
        public bool ConfirmDiscard()
        {
            if (!dirty || tracks.Count == 0) return true;
            var r = MessageBox.Show("The sync session has unsaved changes. Save it?", "SpectroMark", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return false;
            if (r == MessageBoxResult.Yes) { SaveSession(false); if (dirty) return false; }
            return true;
        }

        public async Task LoadSession(string path)
        {
            if (!ConfirmDiscard()) return;
            SessionState st;
            try { st = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(path), new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }); }
            catch (Exception ex) { MessageBox.Show("Could not read the session: " + ex.Message, "SpectroMark", MessageBoxButton.OK, MessageBoxImage.Error); return; }
            StopPlayback(); tracks.Clear(); viewLen = 0; loadingSession = true;
            if (Enum.TryParse<StretchMode>(st.Mode, out var m)) { mode = m; ModeBox.SelectedIndex = Array.IndexOf(StretchEngine.Modes, mode); }
            await AddFiles(st.Tracks.Select(t => t.Path).Where(File.Exists).ToArray());
            ApplyState(st); loadingSession = false;
            // window/view restore has to wait until layout has settled
            _ = Dispatcher.BeginInvoke(() => ApplyView(st), DispatcherPriority.Loaded);
            sessionPath = path; undo.Clear(); redo.Clear();
            SetDirty(false);
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
