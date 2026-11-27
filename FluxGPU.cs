// Floating GPU monitor for a Tesla P40 (read through nvidia-smi) and the AMD integrated GPU
// (read through Windows performance counters). Builds with the C# compiler included in Windows.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using IOPath = System.IO.Path;

namespace GpuMonitor
{
    class Sample
    {
        public double? Util, MemUsed, MemTotal, Temp, Power, PowerLimit, Clock, PcieGen, PcieWidth, Shared;
        public string Engine, Name, Error;
    }

    static class Theme
    {
        public static readonly SolidColorBrush Bg = new SolidColorBrush();
        public static readonly SolidColorBrush Card = new SolidColorBrush();
        public static readonly SolidColorBrush Stroke = new SolidColorBrush();
        public static readonly SolidColorBrush Text = new SolidColorBrush();
        public static readonly SolidColorBrush Sub = new SolidColorBrush();
        public static readonly SolidColorBrush Track = new SolidColorBrush();
        public static readonly SolidColorBrush Hover = new SolidColorBrush();
        public static readonly SolidColorBrush Accent = new SolidColorBrush();
        public static readonly SolidColorBrush AccentFill = new SolidColorBrush();
        public static readonly SolidColorBrush Warn = new SolidColorBrush();
        public static readonly SolidColorBrush Crit = new SolidColorBrush();
        public static readonly SolidColorBrush Brand = new SolidColorBrush();
        public static bool Dark = true;

        static Color Hex(string s) { return (Color)ColorConverter.ConvertFromString(s); }

        public static void Apply()
        {
            object light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
            Dark = light is int && (int)light == 0;
            Bg.Color = Hex(Dark ? "#202020" : "#F3F3F3");
            Card.Color = Hex(Dark ? "#2B2B2B" : "#FBFBFB");
            Stroke.Color = Hex(Dark ? "#3A3A3A" : "#E5E5E5");
            Text.Color = Hex(Dark ? "#FFFFFF" : "#1A1A1A");
            Sub.Color = Hex(Dark ? "#C5C5C5" : "#5D5D5D");
            Track.Color = Hex(Dark ? "#454545" : "#E0E0E0");
            Hover.Color = Hex(Dark ? "#383838" : "#E6E6E6");
            Warn.Color = Hex(Dark ? "#FCE100" : "#9D5D00");
            Crit.Color = Hex(Dark ? "#FF99A4" : "#C42B1C");
            Brand.Color = Hex(Dark ? "#12E9FF" : "#007F96");
            Color accent = Hex(Dark ? "#60CDFF" : "#005FB8");
            byte[] p = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent", "AccentPalette", null) as byte[];
            if (p != null && p.Length >= 32)
            {
                int i = (Dark ? 1 : 4) * 4;   // lighter accent on dark, darker accent on light
                accent = Color.FromRgb(p[i], p[i + 1], p[i + 2]);
            }
            Accent.Color = accent;
            AccentFill.Color = Color.FromArgb(40, accent.R, accent.G, accent.B);
        }
    }

    class NvidiaSource
    {
        volatile bool stop;
        Process proc;
        readonly Action<Sample> report;
        public NvidiaSource(Action<Sample> report) { this.report = report; }

        public void Start()
        {
            Thread t = new Thread(Run);
            t.IsBackground = true;
            t.Start();
        }

        public void Stop()
        {
            stop = true;
            try { if (proc != null && !proc.HasExited) proc.Kill(); } catch (Exception) { }
        }

        static double? Num(string s)
        {
            double v;
            return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : (double?)null;
        }

        void Run()
        {
            string exe = IOPath.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
            while (!stop)
            {
                try
                {
                    ProcessStartInfo si = new ProcessStartInfo(exe,
                        "--id=0 --query-gpu=name,utilization.gpu,memory.used,memory.total,temperature.gpu,power.draw,power.limit,clocks.sm,pcie.link.gen.current,pcie.link.width.current --format=csv,noheader,nounits -l 1");
                    si.UseShellExecute = false;
                    si.CreateNoWindow = true;
                    si.RedirectStandardOutput = true;
                    proc = Process.Start(si);
                    string line;
                    while (!stop && (line = proc.StandardOutput.ReadLine()) != null)
                    {
                        string[] f = line.Split(',');
                        if (f.Length < 10) { report(new Sample { Error = line.Trim() }); continue; }
                        report(new Sample {
                            Name = f[0].Trim(), Util = Num(f[1]), MemUsed = Num(f[2]), MemTotal = Num(f[3]), Temp = Num(f[4]),
                            Power = Num(f[5]), PowerLimit = Num(f[6]), Clock = Num(f[7]), PcieGen = Num(f[8]), PcieWidth = Num(f[9]) });
                    }
                }
                catch (Exception ex) { report(new Sample { Error = ex.Message }); }
                finally { try { if (proc != null && !proc.HasExited) proc.Kill(); } catch (Exception) { } }
                if (!stop) { report(new Sample { Error = "nvidia-smi stopped, retrying" }); Thread.Sleep(3000); }
            }
        }
    }

    class AmdSource
    {
        [StructLayout(LayoutKind.Sequential)] struct LUID { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)] struct AdapterInfo { public uint Handle; public LUID Luid; public uint Sources; public int Precise; }
        [StructLayout(LayoutKind.Sequential)] struct EnumAdapters { public uint Count; public IntPtr Adapters; }
        [StructLayout(LayoutKind.Sequential)] struct QueryInfo { public uint Handle; public int Type; public IntPtr Data; public uint Size; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct RegistryInfo
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Adapter;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Bios;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Dac;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Chip;
        }
        [StructLayout(LayoutKind.Sequential)] struct SegmentSize { public ulong Dedicated, DedicatedSystem, Shared; }
        [StructLayout(LayoutKind.Sequential)]
        struct PerfData
        {
            public uint Index;
            public ulong MemFreq, MaxMemFreq, MaxMemFreqOc, MemBandwidth, PcieBandwidth;
            public uint FanRpm, Power, Temperature;
            public byte PowerStateOverride;
        }
        [DllImport("gdi32.dll")] static extern int D3DKMTEnumAdapters2(ref EnumAdapters e);
        [DllImport("gdi32.dll")] static extern int D3DKMTQueryAdapterInfo(ref QueryInfo q);

        volatile bool stop;
        readonly Action<Sample> report;
        uint handle;
        string luidKey, name;
        double dedicatedTotalMiB;
        public AmdSource(Action<Sample> report) { this.report = report; }
        public void Stop() { stop = true; }
        public void Start()
        {
            Thread t = new Thread(Run);
            t.IsBackground = true;
            t.Start();
        }

        static bool Query<T>(uint adapter, int type, ref T value) where T : struct
        {
            int size = Marshal.SizeOf(typeof(T));
            IntPtr mem = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(value, mem, false);
                QueryInfo q = new QueryInfo { Handle = adapter, Type = type, Data = mem, Size = (uint)size };
                if (D3DKMTQueryAdapterInfo(ref q) != 0) return false;
                value = (T)Marshal.PtrToStructure(mem, typeof(T));
                return true;
            }
            finally { Marshal.FreeHGlobal(mem); }
        }

        bool FindAdapter()
        {
            EnumAdapters e = new EnumAdapters();
            if (D3DKMTEnumAdapters2(ref e) != 0 || e.Count == 0) return false;
            int step = Marshal.SizeOf(typeof(AdapterInfo));
            e.Adapters = Marshal.AllocHGlobal(step * (int)e.Count);
            try
            {
                if (D3DKMTEnumAdapters2(ref e) != 0) return false;
                for (int i = 0; i < e.Count; i++)
                {
                    AdapterInfo a = (AdapterInfo)Marshal.PtrToStructure(IntPtr.Add(e.Adapters, i * step), typeof(AdapterInfo));
                    RegistryInfo r = new RegistryInfo();
                    if (!Query(a.Handle, 8, ref r) || r.Adapter == null) continue;
                    if (r.Adapter.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) < 0 &&
                        r.Adapter.IndexOf("Radeon", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    handle = a.Handle;
                    name = r.Adapter;
                    luidKey = string.Format("luid_0x{0:X8}_0x{1:X8}_", a.Luid.High, a.Luid.Low).ToLowerInvariant();
                    SegmentSize s = new SegmentSize();
                    if (Query(handle, 3, ref s)) dedicatedTotalMiB = s.Dedicated / 1048576.0;
                    return true;
                }
                return false;
            }
            finally { Marshal.FreeHGlobal(e.Adapters); }
        }

        void Run()
        {
            Dictionary<string, CounterSample> previous = new Dictionary<string, CounterSample>();
            while (!stop)
            {
                try
                {
                    if (luidKey == null && !FindAdapter()) { report(new Sample { Error = "AMD adapter not found" }); Thread.Sleep(3000); continue; }
                    Sample s = new Sample { Name = name, MemTotal = dedicatedTotalMiB };

                    Dictionary<string, double> engines = new Dictionary<string, double>();
                    Dictionary<string, CounterSample> current = new Dictionary<string, CounterSample>();
                    InstanceDataCollection use = new PerformanceCounterCategory("GPU Engine").ReadCategory()["Utilization Percentage"];
                    if (use != null)
                        foreach (InstanceData d in use.Values)
                        {
                            string n = d.InstanceName.ToLowerInvariant();
                            if (n.IndexOf(luidKey, StringComparison.Ordinal) < 0) continue;
                            current[n] = d.Sample;
                            CounterSample old;
                            if (!previous.TryGetValue(n, out old)) continue;
                            double v = CounterSample.Calculate(old, d.Sample);
                            int at = n.IndexOf("_engtype_", StringComparison.Ordinal);
                            int eng = n.IndexOf("_eng_", StringComparison.Ordinal);
                            if (at < 0 || eng < 0 || double.IsNaN(v) || v < 0) continue;
                            string key = n.Substring(eng);
                            double sum;
                            engines.TryGetValue(key, out sum);
                            engines[key] = sum + v;
                        }
                    previous = current;
                    double best = 0;
                    string busiest = "idle";
                    foreach (KeyValuePair<string, double> kv in engines)
                        if (kv.Value > best)
                        {
                            best = kv.Value;
                            busiest = kv.Key.Substring(kv.Key.IndexOf("_engtype_", StringComparison.Ordinal) + 9);
                        }
                    s.Util = Math.Min(100, best);
                    s.Engine = best < 0.5 ? "idle" : busiest.ToUpperInvariant();

                    InstanceDataCollectionCollection mem = new PerformanceCounterCategory("GPU Adapter Memory").ReadCategory();
                    s.MemUsed = Total(mem["Dedicated Usage"]);
                    s.Shared = Total(mem["Shared Usage"]);

                    PerfData p = new PerfData();
                    if (Query(handle, 62, ref p))
                    {
                        if (p.Temperature > 0 && p.Temperature < 1500) s.Temp = p.Temperature / 10.0;
                        if (p.Power > 0 && p.Power <= 2000) s.Power = p.Power / 10.0;   // percent of limit
                    }
                    report(s);
                }
                catch (Exception ex) { report(new Sample { Error = ex.Message }); }
                Thread.Sleep(1000);
            }
        }

        double? Total(InstanceDataCollection c)
        {
            if (c == null) return null;
            double bytes = 0;
            bool found = false;
            foreach (InstanceData d in c.Values)
                if (d.InstanceName.ToLowerInvariant().IndexOf(luidKey, StringComparison.Ordinal) >= 0) { bytes += d.RawValue; found = true; }
            return found ? bytes / 1048576.0 : (double?)null;
        }
    }

    static class Ui
    {
        public static TextBlock Label(string text, double size, Brush brush, FontWeight weight)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = brush, FontWeight = weight, TextTrimming = TextTrimming.CharacterEllipsis };
        }
    }

    class GpuCard : Border
    {
        const double SparkW = 210, SparkH = 46;
        const int Points = 60;
        readonly TextBlock title, load, memText, compact;
        readonly TextBlock[] values = new TextBlock[4];
        readonly Polyline line = new Polyline();
        readonly Polygon area = new Polygon();
        readonly Border fill, track;
        readonly StackPanel detail = new StackPanel();
        readonly List<double> history = new List<double>();
        readonly bool isNvidia;
        double memFraction;
        public DateTime LastData = DateTime.Now;

        public GpuCard(string initialName, string role, bool isNvidia)
        {
            this.isNvidia = isNvidia;
            Background = Theme.Card; BorderBrush = Theme.Stroke; BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(8); Padding = new Thickness(14, 10, 14, 12); Margin = new Thickness(10, 0, 10, 10);
            StackPanel root = new StackPanel();
            Child = root;

            DockPanel head = new DockPanel();
            Border badge = new Border { Background = Theme.AccentFill, CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 1, 7, 2),
                Child = Ui.Label(role, 11, Theme.Accent, FontWeights.SemiBold) };
            DockPanel.SetDock(badge, Dock.Right);
            head.Children.Add(badge);
            title = Ui.Label(initialName, 14, Theme.Text, FontWeights.SemiBold);
            head.Children.Add(title);
            root.Children.Add(head);

            compact = Ui.Label("Waiting for data", 12.5, Theme.Sub, FontWeights.Normal);
            compact.Margin = new Thickness(0, 4, 0, 0);
            compact.Visibility = Visibility.Collapsed;
            root.Children.Add(compact);
            root.Children.Add(detail);

            Grid top = new Grid { Margin = new Thickness(0, 6, 0, 8) };
            top.ColumnDefinitions.Add(new ColumnDefinition());
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel big = new StackPanel();
            load = Ui.Label("--", 32, Theme.Text, FontWeights.SemiBold);
            big.Children.Add(load);
            big.Children.Add(Ui.Label("GPU load", 11, Theme.Sub, FontWeights.Normal));
            top.Children.Add(big);
            Canvas c = new Canvas { Width = SparkW, Height = SparkH, ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center };
            area.Fill = Theme.AccentFill;
            line.Stroke = Theme.Accent; line.StrokeThickness = 1.6; line.StrokeLineJoin = PenLineJoin.Round;
            Line baseline = new Line { X1 = 0, X2 = SparkW, Y1 = SparkH - 0.5, Y2 = SparkH - 0.5, Stroke = Theme.Stroke, StrokeThickness = 1 };
            c.Children.Add(baseline); c.Children.Add(area); c.Children.Add(line);
            Grid.SetColumn(c, 1);
            top.Children.Add(c);
            detail.Children.Add(top);

            memText = Ui.Label("Memory", 12, Theme.Sub, FontWeights.Normal);
            detail.Children.Add(memText);
            fill = new Border { Background = Theme.Accent, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            track = new Border { Background = Theme.Track, CornerRadius = new CornerRadius(3), Height = 6, Margin = new Thickness(0, 4, 0, 10), Child = fill };
            track.SizeChanged += delegate { fill.Width = track.ActualWidth * memFraction; };
            detail.Children.Add(track);

            string[] names = isNvidia ? new[] { "Temperature", "Power", "Core clock", "PCIe link" }
                                      : new[] { "Temperature", "Driver power", "Shared mem", "Busy engine" };
            Grid tiles = new Grid();
            for (int i = 0; i < 4; i++)
            {
                tiles.ColumnDefinitions.Add(new ColumnDefinition());
                StackPanel p = new StackPanel();
                values[i] = Ui.Label("--", 14, Theme.Text, FontWeights.SemiBold);
                p.Children.Add(values[i]);
                p.Children.Add(Ui.Label(names[i], 10.5, Theme.Sub, FontWeights.Normal));
                Grid.SetColumn(p, i);
                tiles.Children.Add(p);
            }
            detail.Children.Add(tiles);
        }

        public void SetCompact(bool on)
        {
            detail.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            compact.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }

        static string F(double? v, string format, string unit)
        {
            return v.HasValue ? v.Value.ToString(format, CultureInfo.InvariantCulture) + unit : "n/a";
        }

        public void ShowError(string message)
        {
            memText.Text = message;
            compact.Text = message;
            load.Text = "--";
            foreach (TextBlock v in values) { v.Text = "--"; v.Foreground = Theme.Text; }
        }

        public void Update(Sample s)
        {
            if (s.Error != null) { ShowError(s.Error); return; }
            LastData = DateTime.Now;
            if (s.Name != null) title.Text = s.Name.Replace("(TM)", "");
            load.Text = F(s.Util, "0", "%");
            history.Add(s.Util ?? 0);
            if (history.Count > Points) history.RemoveAt(0);
            PointCollection pts = new PointCollection();
            double dx = SparkW / (Points - 1);
            double start = SparkW - dx * (history.Count - 1);
            for (int i = 0; i < history.Count; i++)
                pts.Add(new Point(start + i * dx, 1 + (SparkH - 2) * (1 - history[i] / 100.0)));
            line.Points = pts;
            PointCollection poly = new PointCollection(pts);
            poly.Add(new Point(SparkW, SparkH));
            poly.Add(new Point(start, SparkH));
            area.Points = poly;

            double used = (s.MemUsed ?? 0) / 1024, total = (s.MemTotal ?? 0) / 1024;
            memFraction = total > 0 ? Math.Max(0, Math.Min(1, used / total)) : 0;
            fill.Width = track.ActualWidth * memFraction;
            memText.Text = string.Format(CultureInfo.InvariantCulture, "{0}   {1:0.0} / {2:0.0} GB",
                isNvidia ? "Memory" : "Dedicated memory", used, total);

            values[0].Text = F(s.Temp, "0", "°C");
            values[0].Foreground = s.Temp >= 85 ? Theme.Crit : s.Temp >= 75 ? Theme.Warn : Theme.Text;
            if (isNvidia)
            {
                values[1].Text = F(s.Power, "0", " W");
                values[2].Text = F(s.Clock, "0", " MHz");
                values[3].Text = s.PcieGen.HasValue ? "Gen" + F(s.PcieGen, "0", "") + " x" + F(s.PcieWidth, "0", "") : "n/a";
            }
            else
            {
                values[1].Text = F(s.Power, "0", "%");
                values[2].Text = F(s.Shared / 1024, "0.0", " GB");
                values[3].Text = s.Engine ?? "n/a";
            }
            string line2 = string.Format(CultureInfo.InvariantCulture, "Load {0}   ·   {1:0.0} / {2:0.0} GB", F(s.Util, "0", "%"), used, total);
            if (s.Temp.HasValue) line2 += "   ·   " + F(s.Temp, "0", "°C");
            if (isNvidia) line2 += "   ·   " + F(s.Power, "0", " W");
            compact.Text = line2;
            compact.Foreground = s.Temp >= 85 ? Theme.Crit : s.Temp >= 75 ? Theme.Warn : Theme.Sub;
        }
    }

    class MonitorWindow : Window
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        readonly GpuCard nvidia = new GpuCard("NVIDIA GPU", "AI compute", true);
        readonly GpuCard amd = new GpuCard("AMD GPU", "Display", false);
        readonly NvidiaSource nvidiaSource;
        readonly AmdSource amdSource;
        TextBlock pinIcon, sizeIcon;
        bool compactMode;
        readonly bool snapshot;
        static readonly string SettingsFile = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.txt");

        public MonitorWindow(string snapshotPath, bool startCompact)
        {
            snapshot = snapshotPath != null;
            Theme.Apply();
            Title = "FluxGPU";
            Icon = Asset("FluxGPU.ico");
            Width = 380; SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            Topmost = true; Background = Theme.Bg;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            StackPanel root = new StackPanel { Background = Theme.Bg };
            DockPanel bar = new DockPanel { Height = 38, Background = Theme.Bg };
            Border close = IconButton("", "Close", delegate { Close(); }).Item1;
            DockPanel.SetDock(close, Dock.Right); bar.Children.Add(close);
            Tuple<Border, TextBlock> size = IconButton("", "Compact view", delegate { SetCompact(!compactMode); });
            sizeIcon = size.Item2; DockPanel.SetDock(size.Item1, Dock.Right); bar.Children.Add(size.Item1);
            Tuple<Border, TextBlock> pin = IconButton("", "Always on top: on", delegate { SetPinned(!Topmost); });
            pinIcon = pin.Item2; DockPanel.SetDock(pin.Item1, Dock.Right); bar.Children.Add(pin.Item1);
            pinIcon.Text = ""; pinIcon.Foreground = Theme.Accent;
            StackPanel brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Image mark = new Image { Source = Asset("mark.png"), Width = 22, Height = 22, Margin = new Thickness(0, 0, 8, 0) };
            RenderOptions.SetBitmapScalingMode(mark, BitmapScalingMode.HighQuality);
            brand.Children.Add(mark);
            TextBlock caption = new TextBlock { FontSize = 15, FontFamily = new FontFamily("Bahnschrift"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            caption.Inlines.Add(new System.Windows.Documents.Run("Flux") { Foreground = Theme.Text });
            caption.Inlines.Add(new System.Windows.Documents.Run("GPU") { Foreground = Theme.Brand });
            brand.Children.Add(caption);
            bar.Children.Add(brand);
            root.Children.Add(bar);
            root.Children.Add(nvidia);
            root.Children.Add(amd);
            Content = root;

            Left = SystemParameters.WorkArea.Right - Width - 16;
            Top = SystemParameters.WorkArea.Top + 16;
            if (!snapshot) LoadSettings();
            if (startCompact) SetCompact(true);

            MouseLeftButtonDown += delegate { if (Mouse.LeftButton == MouseButtonState.Pressed) DragMove(); };
            SourceInitialized += delegate
            {
                int round = 2;
                DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref round, 4);
            };

            nvidiaSource = new NvidiaSource(delegate(Sample s) { Dispatcher.BeginInvoke((Action)delegate { nvidia.Update(s); }); });
            amdSource = new AmdSource(delegate(Sample s) { Dispatcher.BeginInvoke((Action)delegate { amd.Update(s); }); });
            nvidiaSource.Start();
            amdSource.Start();

            DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += delegate
            {
                Theme.Apply();
                if ((DateTime.Now - nvidia.LastData).TotalSeconds > 8) nvidia.ShowError("No data from nvidia-smi");
                if ((DateTime.Now - amd.LastData).TotalSeconds > 8) amd.ShowError("No data from Windows counters");
            };
            timer.Start();

            Closed += delegate { nvidiaSource.Stop(); amdSource.Stop(); if (!snapshot) SaveSettings(); };

            if (snapshot)
            {
                DispatcherTimer shot = new DispatcherTimer { Interval = TimeSpan.FromSeconds(9) };
                shot.Tick += delegate
                {
                    shot.Stop();
                    RenderTargetBitmap bmp = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * 2), (int)Math.Ceiling(root.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
                    bmp.Render(root);
                    PngBitmapEncoder png = new PngBitmapEncoder();
                    png.Frames.Add(BitmapFrame.Create(bmp));
                    using (FileStream fs = File.Create(snapshotPath)) png.Save(fs);
                    Close();
                };
                shot.Start();
            }
        }

        // Logo images are embedded in the program at build time.
        static BitmapFrame Asset(string name)
        {
            using (Stream s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                return BitmapFrame.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }

        Tuple<Border, TextBlock> IconButton(string glyph, string tip, Action click)
        {
            TextBlock icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 11,
                Foreground = Theme.Text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Border b = new Border { Width = 40, Height = 38, Background = Brushes.Transparent, Child = icon, ToolTip = tip };
            b.MouseEnter += delegate { b.Background = Theme.Hover; };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
            b.MouseLeftButtonDown += delegate(object o, MouseButtonEventArgs e) { e.Handled = true; };
            b.MouseLeftButtonUp += delegate(object o, MouseButtonEventArgs e) { e.Handled = true; click(); };
            return Tuple.Create(b, icon);
        }

        void SetPinned(bool on)
        {
            Topmost = on;
            pinIcon.Text = on ? "" : "";
            pinIcon.Foreground = on ? Theme.Accent : Theme.Text;
            ((Border)pinIcon.Parent).ToolTip = "Always on top: " + (on ? "on" : "off");
        }

        void SetCompact(bool on)
        {
            compactMode = on;
            nvidia.SetCompact(on);
            amd.SetCompact(on);
            sizeIcon.Text = on ? "" : "";
            ((Border)sizeIcon.Parent).ToolTip = on ? "Full view" : "Compact view";
        }

        void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsFile)) return;
                string[] f = File.ReadAllText(SettingsFile).Split('|');
                double x = double.Parse(f[0], CultureInfo.InvariantCulture), y = double.Parse(f[1], CultureInfo.InvariantCulture);
                if (x >= SystemParameters.VirtualScreenLeft - 100 && x < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
                    y >= SystemParameters.VirtualScreenTop && y < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 60) { Left = x; Top = y; }
                SetPinned(f[2] == "1");
                SetCompact(f[3] == "1");
            }
            catch (Exception) { }
        }

        void SaveSettings()
        {
            try
            {
                File.WriteAllText(SettingsFile, string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}|{3}", Left, Top, Topmost ? 1 : 0, compactMode ? 1 : 0));
            }
            catch (Exception) { }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            string snapshot = null;
            bool compact = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--snapshot" && i + 1 < args.Length) snapshot = args[++i];
                else if (args[i] == "--compact") compact = true;
            }
            bool first;
            using (Mutex m = new Mutex(true, snapshot == null ? "LocalAI.FluxGPU" : "LocalAI.FluxGPU.Snapshot", out first))
            {
                if (!first) return;
                new Application().Run(new MonitorWindow(snapshot, compact));
            }
        }
    }
}
