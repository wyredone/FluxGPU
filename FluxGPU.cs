// FluxGPU 0.2.0 - floating GPU monitor for a Tesla P40 (read through nvidia-smi) and the AMD integrated GPU
// (read through Windows performance counters and D3DKMT). Builds with the C# 5 compiler included in Windows
// (.NET Framework 4.x, see build.cmd). Single file on purpose (v0.2); see IMPROVEMENT_PLAN.md P1-0 for the split.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Media;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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

[assembly: AssemblyTitle("FluxGPU")]
[assembly: AssemblyProduct("FluxGPU")]
[assembly: AssemblyDescription("Floating GPU monitor for an NVIDIA compute card and AMD integrated graphics")]
[assembly: AssemblyCopyright("Copyright (c) 2026 wyredone, MIT License")]
[assembly: AssemblyVersion("0.2.0.0")]
[assembly: AssemblyFileVersion("0.2.0.0")]
[assembly: AssemblyInformationalVersion("0.2.0")]

namespace GpuMonitor
{
    // ------------------------------------------------------------------------------------------------
    // App info, monotonic clock, logging
    // ------------------------------------------------------------------------------------------------

    static class AppInfo
    {
        public const string Name = "FluxGPU";
        public const string Version = "0.2.0";

        public static string DataDirectory
        {
            get
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(local)) local = IOPath.GetTempPath();
                return IOPath.Combine(local, Name);
            }
        }
    }

    // Staleness and timeouts use a monotonic clock, never DateTime.Now (DST or clock changes must not matter).
    static class Mono
    {
        static readonly Stopwatch watch = Stopwatch.StartNew();
        public static long Ms { get { return watch.ElapsedMilliseconds; } }
    }

    // One locked writer for all threads. Daily files, about 5 MB cap per file, 14 days kept,
    // rate limiting by error class (not by message text, which contains paths and PIDs).
    static class Log
    {
        public const string TimeFormat = "MM-dd-yy hh:mm tt";   // Kevin's standard: %m-%d-%y %I:%M %p
        public const long MaxFileBytes = 5L * 1024 * 1024;
        public const int KeepDays = 14;
        public const int MaxPartsPerDay = 10;
        public const long RateLimitMs = 60000;

        static readonly object gate = new object();
        static readonly Dictionary<string, long> lastByClass = new Dictionary<string, long>();
        static readonly Dictionary<string, int> suppressedByClass = new Dictionary<string, int>();
        static string folder;

        public static string Folder { get { return folder; } }

        public static void Init(string logFolder)
        {
            lock (gate)
            {
                try
                {
                    Directory.CreateDirectory(logFolder);
                    folder = logFolder;
                }
                catch (Exception ex)
                {
                    folder = null;
                    Trace.WriteLine("FluxGPU: log folder unavailable: " + ex.Message);
                    return;
                }
                Prune(DateTime.Now);
            }
        }

        public static string Stamp(DateTime local) { return local.ToString(TimeFormat, CultureInfo.InvariantCulture); }

        public static void Info(string message) { Write("INFO", message); }
        public static void Warn(string message) { Write("WARN", message); }
        public static void Error(string message, Exception ex)
        {
            Write("ERROR", ex == null ? message : message + " | " + ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex.StackTrace);
        }

        // Writes at most once per RateLimitMs for a given class; reports how many were suppressed.
        public static void Limited(string errorClass, string level, string message)
        {
            long now = Mono.Ms;
            int suppressed = 0;
            lock (gate)
            {
                long last;
                if (lastByClass.TryGetValue(errorClass, out last) && now - last < RateLimitMs)
                {
                    int n;
                    suppressedByClass.TryGetValue(errorClass, out n);
                    suppressedByClass[errorClass] = n + 1;
                    return;
                }
                lastByClass[errorClass] = now;
                if (suppressedByClass.TryGetValue(errorClass, out suppressed)) suppressedByClass.Remove(errorClass);
            }
            Write(level, suppressed > 0 ? message + " (" + suppressed + " similar suppressed)" : message);
        }

        static void Write(string level, string message)
        {
            lock (gate)
            {
                if (folder == null) return;
                try
                {
                    DateTime now = DateTime.Now;
                    string file = CurrentFile(now);
                    if (file == null) return;   // daily cap reached; stop writing rather than fill the disk
                    File.AppendAllText(file, Stamp(now) + " [" + level + "] " + message + Environment.NewLine, Encoding.UTF8);
                }
                catch (IOException) { }                  // logging must never take the app down
                catch (UnauthorizedAccessException) { }
            }
        }

        static string CurrentFile(DateTime now)
        {
            string day = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            for (int part = 1; part <= MaxPartsPerDay; part++)
            {
                string name = part == 1 ? "fluxgpu-" + day + ".log" : "fluxgpu-" + day + "." + part + ".log";
                string path = IOPath.Combine(folder, name);
                FileInfo fi = new FileInfo(path);
                if (!fi.Exists || fi.Length < MaxFileBytes) return path;
            }
            return null;
        }

        static void Prune(DateTime now)
        {
            try
            {
                foreach (string f in Directory.GetFiles(folder, "fluxgpu-*.log"))
                    if ((now - File.GetLastWriteTime(f)).TotalDays > KeepDays) File.Delete(f);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Samples and the latest-sample slot
    // ------------------------------------------------------------------------------------------------

    enum SourceState { Connecting, Live, Empty, Error }

    // Unit-typed: NVIDIA watts and AMD driver percent no longer share one field.
    sealed class Sample
    {
        public SourceState State = SourceState.Live;
        public string Message;          // Connecting / Empty / Error text, already user-facing
        public bool Terminal;           // "Reboot required" class of error
        public string Name, Engine, DriverModel;
        public double? Util, MemUsedMiB, MemTotalMiB, SharedUsedMiB, SharedTotalMiB, TempC;
        public double? PowerW, PowerLimitW, PowerPct, ClockMHz, PcieGen, PcieGenMax, PcieWidth;
        public double? SlowdownC, ShutdownC;
        public ulong? ThrottleMask;

        public static Sample Status(SourceState state, string message, bool terminal)
        {
            Sample s = new Sample();
            s.State = state; s.Message = message; s.Terminal = terminal;
            return s;
        }
    }

    // Workers overwrite the slot; the UI tick renders only the newest sample, so a busy UI thread
    // can never build up a backlog of BeginInvoke calls.
    sealed class SampleSlot
    {
        Sample latest;
        int sequence;

        public void Put(Sample s)
        {
            Interlocked.Exchange(ref latest, s);
            Interlocked.Increment(ref sequence);
        }

        public Sample TakeIfNew(ref int seen)
        {
            int now = Volatile.Read(ref sequence);
            if (now == seen) return null;
            seen = now;
            return Volatile.Read(ref latest);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Pure logic (covered by tests/FluxGPU.Tests.cs)
    // ------------------------------------------------------------------------------------------------

    sealed class NvReading
    {
        public string Name, DriverModel;
        public double? Util, MemUsedMiB, MemTotalMiB, TempC, PowerW, PowerLimitW, ClockMHz, PcieGen, PcieGenMax, PcieWidth;
        public ulong? ThrottleMask;
    }

    // Parses nvidia-smi --format=csv output by header name (not position), so a missing optional field or a
    // comma inside a GPU name cannot shift the columns. Rejects NaN, Infinity and out-of-range values.
    static class NvidiaSmiCsv
    {
        public const string FieldName = "name";
        public static readonly string[] BaseFields = {
            "name", "utilization.gpu", "memory.used", "memory.total", "temperature.gpu", "power.draw",
            "power.limit", "clocks.sm", "pcie.link.gen.current", "pcie.link.width.current" };

        public static string NormalizeHeader(string h)
        {
            if (h == null) return "";
            h = h.Trim();
            int i = h.IndexOf('[');
            if (i >= 0) h = h.Substring(0, i);
            return h.Trim().ToLowerInvariant();
        }

        public static bool IsHeader(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            string first = line.Split(',')[0];
            return NormalizeHeader(first) == FieldName;
        }

        public static string[] ParseHeader(string line)
        {
            string[] parts = line.Split(',');
            string[] names = new string[parts.Length];
            for (int i = 0; i < parts.Length; i++) names[i] = NormalizeHeader(parts[i]);
            return names;
        }

        // Returns null and sets error when the line is not a data row (for example a driver error message).
        public static NvReading ParseRow(string[] header, string line, out string error)
        {
            error = null;
            if (header == null || header.Length == 0) { error = "no header"; return null; }
            string[] f = line.Split(',');
            if (f.Length < header.Length) { error = line.Trim(); return null; }
            int nameAt = Array.IndexOf(header, FieldName);
            if (f.Length > header.Length)
            {
                if (nameAt < 0) { error = line.Trim(); return null; }
                int extra = f.Length - header.Length;
                string[] merged = new string[header.Length];
                for (int i = 0, j = 0; i < header.Length; i++, j++)
                {
                    if (i == nameAt)
                    {
                        merged[i] = string.Join(",", f, j, extra + 1);
                        j += extra;
                    }
                    else merged[i] = f[j];
                }
                f = merged;
            }
            Dictionary<string, string> v = new Dictionary<string, string>();
            for (int i = 0; i < header.Length; i++) v[header[i]] = f[i].Trim();

            NvReading r = new NvReading();
            r.Name = Get(v, "name");
            if (string.IsNullOrEmpty(r.Name) || r.Name.StartsWith("[", StringComparison.Ordinal)) r.Name = null;
            r.Util = Num(Get(v, "utilization.gpu"), 0, 100);
            r.MemUsedMiB = Num(Get(v, "memory.used"), 0, 10000000);
            r.MemTotalMiB = Num(Get(v, "memory.total"), 1, 10000000);
            r.TempC = Num(Get(v, "temperature.gpu"), -40, 150);
            r.PowerLimitW = Num(Get(v, "power.limit"), 1, 5000);
            r.PowerW = Num(Get(v, "power.draw"), 0, r.PowerLimitW.HasValue ? 2 * r.PowerLimitW.Value : 5000);
            r.ClockMHz = Num(Get(v, "clocks.sm"), 0, 10000);
            r.PcieGen = Num(Get(v, "pcie.link.gen.current"), 1, 10);
            r.PcieGenMax = Num(Get(v, "pcie.link.gen.max"), 1, 10);
            r.PcieWidth = Num(Get(v, "pcie.link.width.current"), 1, 64);
            string dm = Get(v, "driver_model.current");
            r.DriverModel = string.IsNullOrEmpty(dm) || dm.StartsWith("[", StringComparison.Ordinal) ? null : dm;
            string t = Get(v, "clocks_event_reasons.active");
            if (t == null) t = Get(v, "clocks_throttle_reasons.active");
            r.ThrottleMask = Hex(t);
            if (r.Name == null && !r.Util.HasValue && !r.MemUsedMiB.HasValue && !r.TempC.HasValue) { error = line.Trim(); return null; }
            return r;
        }

        static string Get(Dictionary<string, string> v, string key)
        {
            string s;
            return v.TryGetValue(key, out s) ? s : null;
        }

        public static double? Num(string s, double min, double max)
        {
            if (s == null) return null;
            double v;
            if (!double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return null;
            if (double.IsNaN(v) || double.IsInfinity(v) || v < min || v > max) return null;
            return v;
        }

        public static ulong? Hex(string s)
        {
            if (s == null) return null;
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            if (s.Length == 0 || s.Length > 16) return null;
            ulong v;
            return ulong.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out v) ? v : (ulong?)null;
        }

        static readonly Regex SlowdownRx = new Regex(@"GPU Slowdown Temp\s*:\s*(\d+(\.\d+)?)\s*C", RegexOptions.IgnoreCase);
        static readonly Regex ShutdownRx = new Regex(@"GPU Shutdown Temp\s*:\s*(\d+(\.\d+)?)\s*C", RegexOptions.IgnoreCase);

        // Reads "GPU Slowdown Temp : 92 C" and "GPU Shutdown Temp : 95 C" from nvidia-smi -q -d TEMPERATURE.
        public static void ParseTemperatureReport(string text, out double? slowdownC, out double? shutdownC)
        {
            slowdownC = null; shutdownC = null;
            if (text == null) return;
            Match m = SlowdownRx.Match(text);
            if (m.Success) slowdownC = Num(m.Groups[1].Value, 30, 150);
            m = ShutdownRx.Match(text);
            if (m.Success) shutdownC = Num(m.Groups[1].Value, 30, 150);
        }
    }

    enum NvErrorKind { Transient, Terminal }

    static class NvidiaErrors
    {
        static readonly string[] TerminalMarkers = {
            "gpu is lost", "no devices were found", "couldn't communicate with the nvidia driver",
            "could not communicate with the nvidia driver", "fallen off the bus", "reboot the system", "driver not loaded" };

        public static NvErrorKind Classify(string text)
        {
            if (string.IsNullOrEmpty(text)) return NvErrorKind.Transient;
            string t = text.ToLowerInvariant();
            foreach (string m in TerminalMarkers)
                if (t.IndexOf(m, StringComparison.Ordinal) >= 0) return NvErrorKind.Terminal;
            return NvErrorKind.Transient;
        }

        public static bool IsInvalidField(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string t = text.ToLowerInvariant();
            return t.IndexOf("not a valid field", StringComparison.Ordinal) >= 0 || t.IndexOf("invalid", StringComparison.Ordinal) >= 0;
        }
    }

    // Transient failures: 3, 6, 12, 24, 48, 60, 60 ... seconds. Terminal failures: every 60 s.
    sealed class Backoff
    {
        public const int MinMs = 3000, MaxMs = 60000;
        int next = MinMs;
        public int NextTransientMs() { int v = next; next = Math.Min(MaxMs, next * 2); return v; }
        public int TerminalMs() { return MaxMs; }
        public void Reset() { next = MinMs; }
    }

    // nvidia-smi clocks_event_reasons / clocks_throttle_reasons bitmask (NVML nvmlClocksEventReason* values).
    static class Throttle
    {
        public const ulong GpuIdle = 0x1, AppClocks = 0x2, SwPowerCap = 0x4, HwSlowdown = 0x8, SyncBoost = 0x10,
            SwThermal = 0x20, HwThermal = 0x40, HwPowerBrake = 0x80;

        // Only real slowdowns become warning badges. Idle, app clocks and sync boost are never badged.
        public static List<string> WarningBadges(ulong mask)
        {
            List<string> b = new List<string>();
            if ((mask & HwThermal) != 0) b.Add("HW THERMAL");
            if ((mask & SwThermal) != 0) b.Add("SW THERMAL");
            if ((mask & HwPowerBrake) != 0) b.Add("POWER BRAKE");
            if ((mask & HwSlowdown) != 0 && (mask & (HwThermal | HwPowerBrake)) == 0) b.Add("HW SLOWDOWN");
            return b;
        }

        // SwPowerCap is normal under load: information only.
        public static bool PowerCapped(ulong mask) { return (mask & SwPowerCap) != 0; }
    }

    enum AlertLevel { None = 0, Warn = 1, Critical = 2 }

    // Temperature state with hysteresis: rises immediately, falls only once the temperature is HystC below a threshold.
    sealed class TempAlarm
    {
        public double WarnC, CritC, HystC;
        public AlertLevel Level = AlertLevel.None;

        public TempAlarm(double warnC, double critC, double hystC) { WarnC = warnC; CritC = critC; HystC = hystC; }

        public AlertLevel Update(double? tempC)
        {
            if (!tempC.HasValue) return Level;   // a gap holds the current level
            double t = tempC.Value;
            AlertLevel next;
            if (t >= CritC) next = AlertLevel.Critical;
            else if (Level == AlertLevel.Critical && t > CritC - HystC) next = AlertLevel.Critical;
            else if (t >= WarnC) next = AlertLevel.Warn;
            else if (Level >= AlertLevel.Warn && t > WarnC - HystC) next = AlertLevel.Warn;
            else next = AlertLevel.None;
            Level = next;
            return next;
        }
    }

    static class Thresholds
    {
        public const double FallbackWarnC = 75, FallbackCritC = 85;

        // User override first, then the GPU's own slowdown temperature (warn = slowdown - 10, critical = slowdown - 3),
        // then 75 / 85 C. Invalid combinations fall back and say why.
        public static void Resolve(double? slowdownC, double? userWarnC, double? userCritC,
            out double warnC, out double critC, out string source)
        {
            double autoWarn = FallbackWarnC, autoCrit = FallbackCritC;
            string autoSource = "default 75/85 C";
            if (slowdownC.HasValue && slowdownC.Value >= 50)
            {
                autoWarn = slowdownC.Value - 10;
                autoCrit = slowdownC.Value - 3;
                autoSource = "GPU slowdown " + slowdownC.Value.ToString("0", CultureInfo.InvariantCulture) + " C";
            }
            warnC = userWarnC.HasValue ? userWarnC.Value : autoWarn;
            critC = userCritC.HasValue ? userCritC.Value : autoCrit;
            source = userWarnC.HasValue || userCritC.HasValue ? "settings" : autoSource;
            if (warnC >= critC)
            {
                source = "invalid settings (warn >= critical), using " + autoSource;
                warnC = autoWarn; critC = autoCrit;
            }
        }
    }

    // "P40 telemetry lost": no good sample for longer than LostAfterMs (including never having had one).
    sealed class TelemetryWatch
    {
        public const long DefaultLostAfterMs = 30000;
        readonly long lostAfterMs;
        long lastGoodMs;
        bool lost;
        public bool Lost { get { return lost; } }

        public TelemetryWatch(long lostAfterMs, long startMs) { this.lostAfterMs = lostAfterMs; lastGoodMs = startMs; }

        public void Good(long nowMs) { lastGoodMs = nowMs; }

        // Returns +1 when telemetry becomes lost, -1 when it recovers, 0 when unchanged.
        public int Update(long nowMs)
        {
            bool now = nowMs - lastGoodMs > lostAfterMs;
            if (now == lost) return 0;
            lost = now;
            return now ? 1 : -1;
        }

        public long SinceGoodMs(long nowMs) { return nowMs - lastGoodMs; }
    }

    // Sparkline gaps: returns [start, end] index pairs of consecutive non-null values.
    static class Sparkline
    {
        public static List<int[]> Runs(IList<double?> values)
        {
            List<int[]> runs = new List<int[]>();
            int start = -1;
            for (int i = 0; i <= values.Count; i++)
            {
                bool ok = i < values.Count && values[i].HasValue;
                if (ok && start < 0) start = i;
                if (!ok && start >= 0) { runs.Add(new[] { start, i - 1 }); start = -1; }
            }
            return runs;
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Settings: key=value text in %LOCALAPPDATA%\FluxGPU\settings.ini, atomic writes, one-time migration
    // ------------------------------------------------------------------------------------------------

    sealed class AppSettings
    {
        public const int FormatVersion = 1;
        public double? Left, Top;
        public bool Topmost = true, Compact = false, AlertSound = true, MigratedLegacy = false;
        public double? WarnC, CritC;          // null = automatic
        public double HystC = 3;
        public string NvidiaSmiPath;          // null = search the standard locations

        public static AppSettings Parse(string text, List<string> problems)
        {
            AppSettings s = new AppSettings();
            if (text == null) return s;
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            for (int n = 0; n < lines.Length; n++)
            {
                string line = lines[n].Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith(";", StringComparison.Ordinal)) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) { problems.Add("line " + (n + 1) + ": not key=value"); continue; }
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "version": break;
                    case "position_units": break;
                    case "left": s.Left = Coord(val, key, problems); break;
                    case "top": s.Top = Coord(val, key, problems); break;
                    case "topmost": s.Topmost = Bool(val, s.Topmost, key, problems); break;
                    case "compact": s.Compact = Bool(val, s.Compact, key, problems); break;
                    case "alert_sound": s.AlertSound = Bool(val, s.AlertSound, key, problems); break;
                    case "migrated_settings_txt": s.MigratedLegacy = Bool(val, false, key, problems); break;
                    case "temp_warn_c": s.WarnC = Temp(val, key, problems); break;
                    case "temp_crit_c": s.CritC = Temp(val, key, problems); break;
                    case "temp_hysteresis_c":
                        {
                            double? h = NvidiaSmiCsv.Num(val, 0.5, 10);
                            if (h.HasValue) s.HystC = h.Value; else if (val.Length > 0) problems.Add(key + ": expected 0.5-10");
                            break;
                        }
                    case "nvidia_smi_path":
                        if (val.Length == 0) s.NvidiaSmiPath = null;
                        else if (NvidiaSmiLocator.IsAbsolute(val)) s.NvidiaSmiPath = val;
                        else problems.Add(key + ": must be a full path such as C:\\Windows\\System32\\nvidia-smi.exe");
                        break;
                    default: problems.Add("unknown key '" + key + "'"); break;
                }
            }
            return s;
        }

        // v0.1 format: "left|top|topmost(1/0)|compact(1/0)" in settings.txt next to the exe.
        public static AppSettings ParseLegacy(string text, List<string> problems)
        {
            AppSettings s = new AppSettings();
            if (text == null) return s;
            string[] f = text.Trim().Split('|');
            if (f.Length < 4) { problems.Add("legacy settings.txt: expected 4 fields, found " + f.Length); return s; }
            s.Left = Coord(f[0], "left", problems);
            s.Top = Coord(f[1], "top", problems);
            s.Topmost = f[2].Trim() == "1";
            s.Compact = f[3].Trim() == "1";
            return s;
        }

        public string Serialize()
        {
            StringBuilder b = new StringBuilder();
            b.AppendLine("# FluxGPU settings (key=value). Edit only while FluxGPU is closed. Blank = automatic.");
            b.AppendLine("version=" + FormatVersion);
            b.AppendLine("position_units=dip-system");
            b.AppendLine("left=" + D(Left));
            b.AppendLine("top=" + D(Top));
            b.AppendLine("topmost=" + (Topmost ? "1" : "0"));
            b.AppendLine("compact=" + (Compact ? "1" : "0"));
            b.AppendLine("# NVIDIA temperature alerts. Blank = GPU slowdown temp - 10 (warn) and - 3 (critical), else 75 / 85.");
            b.AppendLine("temp_warn_c=" + D(WarnC));
            b.AppendLine("temp_crit_c=" + D(CritC));
            b.AppendLine("temp_hysteresis_c=" + HystC.ToString("0.##", CultureInfo.InvariantCulture));
            b.AppendLine("alert_sound=" + (AlertSound ? "1" : "0"));
            b.AppendLine("# Full path to nvidia-smi.exe. Blank = System32, then Program Files\\NVIDIA Corporation\\NVSMI.");
            b.AppendLine("nvidia_smi_path=" + (NvidiaSmiPath ?? ""));
            b.AppendLine("migrated_settings_txt=" + (MigratedLegacy ? "1" : "0"));
            return b.ToString();
        }

        static string D(double? v) { return v.HasValue ? v.Value.ToString("0.##", CultureInfo.InvariantCulture) : ""; }

        static double? Coord(string val, string key, List<string> problems)
        {
            if (val.Trim().Length == 0) return null;
            double? v = NvidiaSmiCsv.Num(val, -100000, 100000);
            if (!v.HasValue) problems.Add(key + ": not a number");
            return v;
        }

        static double? Temp(string val, string key, List<string> problems)
        {
            if (val.Length == 0) return null;
            double? v = NvidiaSmiCsv.Num(val, 30, 120);
            if (!v.HasValue) problems.Add(key + ": expected 30-120 C or blank");
            return v;
        }

        static bool Bool(string val, bool fallback, string key, List<string> problems)
        {
            if (val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (val == "0" || val.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            problems.Add(key + ": expected 1 or 0");
            return fallback;
        }
    }

    static class SettingsStore
    {
        public const string FileName = "settings.ini";
        public const string LegacyFileName = "settings.txt";

        // Loads settings.ini; if it does not exist yet, migrates the v0.1 settings.txt once (the old file is left in place).
        public static AppSettings Load(string folder, string legacyFile, List<string> notes)
        {
            string path = IOPath.Combine(folder, FileName);
            List<string> problems = new List<string>();
            try
            {
                if (File.Exists(path))
                {
                    AppSettings s = AppSettings.Parse(File.ReadAllText(path, Encoding.UTF8), problems);
                    if (problems.Count > 0)
                    {
                        notes.Add("settings.ini has problems (" + string.Join("; ", problems.ToArray()) + "); bad values were ignored");
                        BackupBadFile(path, notes);
                    }
                    return s;
                }
                if (legacyFile != null && File.Exists(legacyFile))
                {
                    AppSettings s = AppSettings.ParseLegacy(File.ReadAllText(legacyFile), problems);
                    s.MigratedLegacy = true;
                    notes.Add("migrated " + legacyFile + " to " + path + (problems.Count > 0 ? " (" + string.Join("; ", problems.ToArray()) + ")" : ""));
                    Save(s, folder, notes);
                    return s;
                }
            }
            catch (IOException ex) { notes.Add("could not read settings: " + ex.Message); }
            catch (UnauthorizedAccessException ex) { notes.Add("could not read settings: " + ex.Message); }
            return new AppSettings();
        }

        // Atomic: write a temp file, flush it to disk, then replace (or move into place on the first save).
        public static bool Save(AppSettings s, string folder, List<string> notes)
        {
            string path = IOPath.Combine(folder, FileName);
            string tmp = path + ".tmp";
            try
            {
                Directory.CreateDirectory(folder);
                byte[] data = new UTF8Encoding(false).GetBytes(s.Serialize());
                using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(data, 0, data.Length);
                    fs.Flush(true);
                }
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                return true;
            }
            catch (IOException ex) { notes.Add("could not save settings: " + ex.Message); }
            catch (UnauthorizedAccessException ex) { notes.Add("could not save settings: " + ex.Message); }
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return false;
        }

        static void BackupBadFile(string path, List<string> notes)
        {
            try
            {
                string bad = path + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                File.Copy(path, bad, false);
                notes.Add("kept a copy of the problem file as " + bad);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // ------------------------------------------------------------------------------------------------
    // nvidia-smi location (absolute, known paths only; never a bare PATH lookup)
    // ------------------------------------------------------------------------------------------------

    static class NvidiaSmiLocator
    {
        public static bool IsAbsolute(string p)
        {
            if (string.IsNullOrEmpty(p)) return false;
            try { return IOPath.IsPathRooted(p) && string.Equals(IOPath.GetFullPath(p), p, StringComparison.OrdinalIgnoreCase); }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (PathTooLongException) { return false; }
        }

        public static List<string> Candidates(string overridePath)
        {
            List<string> c = new List<string>();
            if (IsAbsolute(overridePath)) c.Add(overridePath);
            string sys = Environment.SystemDirectory;
            if (!string.IsNullOrEmpty(sys)) c.Add(IOPath.Combine(sys, "nvidia-smi.exe"));
            foreach (string pf in new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetEnvironmentVariable("ProgramFiles") })
            {
                if (string.IsNullOrEmpty(pf) || !IOPath.IsPathRooted(pf)) continue;
                string p = IOPath.Combine(pf, "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe");
                if (!c.Contains(p)) c.Add(p);
            }
            return c;
        }

        public static string Find(string overridePath, List<string> lookedIn)
        {
            foreach (string p in Candidates(overridePath))
            {
                lookedIn.Add(p);
                if (File.Exists(p)) return p;
            }
            return null;
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Child process lifetime: a Win32 job object kills nvidia-smi when FluxGPU exits for any reason
    // ------------------------------------------------------------------------------------------------

    static class ChildProcesses
    {
        [StructLayout(LayoutKind.Sequential)]
        struct BasicLimits
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)]
        struct ExtendedLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        const uint KillOnJobClose = 0x2000;
        const int ExtendedLimitInformation = 9;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits info, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        static readonly object gate = new object();
        static readonly List<Process> live = new List<Process>();
        static IntPtr job = IntPtr.Zero;
        static bool jobTried;

        public static int ExtendedLimitsSize { get { return Marshal.SizeOf(typeof(ExtendedLimits)); } }
        public static bool JobActive { get { lock (gate) return job != IntPtr.Zero; } }

        static IntPtr Job()
        {
            if (jobTried) return job;
            jobTried = true;
            try
            {
                IntPtr j = CreateJobObject(IntPtr.Zero, null);
                if (j == IntPtr.Zero) { Log.Warn("Job object: CreateJobObject failed (" + Marshal.GetLastWin32Error() + ")"); return job; }
                ExtendedLimits info = new ExtendedLimits();
                info.Basic.LimitFlags = KillOnJobClose;
                if (!SetInformationJobObject(j, ExtendedLimitInformation, ref info, (uint)Marshal.SizeOf(typeof(ExtendedLimits))))
                {
                    Log.Warn("Job object: SetInformationJobObject failed (" + Marshal.GetLastWin32Error() + ")");
                    return job;
                }
                job = j;   // intentionally never closed: Windows closes it when FluxGPU exits, which kills the children
            }
            catch (DllNotFoundException ex) { Log.Warn("Job object unavailable: " + ex.Message); }
            catch (EntryPointNotFoundException ex) { Log.Warn("Job object unavailable: " + ex.Message); }
            return job;
        }

        public static void Register(Process p)
        {
            lock (gate)
            {
                live.Add(p);
                IntPtr j = Job();
                if (j == IntPtr.Zero) return;
                try
                {
                    if (!AssignProcessToJobObject(j, p.Handle))
                        Log.Limited("job-assign", "WARN", "Job object: AssignProcessToJobObject failed (" + Marshal.GetLastWin32Error() + ")");
                }
                catch (InvalidOperationException) { }   // process already exited
            }
        }

        public static void Unregister(Process p) { lock (gate) live.Remove(p); }

        public static int LiveCount { get { lock (gate) return live.Count; } }

        // Called from the crash handler and on shutdown.
        public static void KillAll()
        {
            lock (gate)
            {
                foreach (Process p in live)
                {
                    try { if (!p.HasExited) p.Kill(); }
                    catch (InvalidOperationException) { }
                    catch (Win32Exception) { }
                }
            }
        }
    }

    // ------------------------------------------------------------------------------------------------
    // NVIDIA source: nvidia-smi -l 1 with a stall supervisor, field probing, error classification and backoff
    // ------------------------------------------------------------------------------------------------

    sealed class NvidiaProbe
    {
        public string ThrottleField, PcieGenMaxField, DriverModelField;
        public double? SlowdownC, ShutdownC;

        public List<string> QueryFields()
        {
            List<string> f = new List<string>(NvidiaSmiCsv.BaseFields);
            if (ThrottleField != null) f.Add(ThrottleField);
            if (PcieGenMaxField != null) f.Add(PcieGenMaxField);
            if (DriverModelField != null) f.Add(DriverModelField);
            return f;
        }
    }

    sealed class NvidiaSource
    {
        public const int FirstLineTimeoutMs = 10000;   // nvidia-smi can be slow to start on a TCC card
        public const int StallTimeoutMs = 5000;        // P0-8: no line for 5 s = stalled, kill and restart
        public const int ProbeTimeoutMs = 10000;
        const int TailLines = 20, ErrorLines = 3;

        readonly Action<Sample> report;
        readonly string overridePath;
        readonly CancellationTokenSource cts = new CancellationTokenSource();
        Thread thread;

        // Session state shared with the asynchronous output handlers.
        readonly object gate = new object();
        Process current;
        string[] header;
        long lastLineMs;
        bool gotData;
        int outEof;
        readonly Queue<string> errorLines = new Queue<string>();
        readonly Queue<string> stderrTail = new Queue<string>();
        long stderrBytes;
        volatile NvidiaProbe probe;
        int launches;

        public NvidiaSource(Action<Sample> report, string overridePath)
        {
            this.report = report;
            this.overridePath = overridePath;
        }

        public int Launches { get { return Volatile.Read(ref launches); } }
        public long StderrBytes { get { lock (gate) return stderrBytes; } }

        public void Start()
        {
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Name = "FluxGPU nvidia-smi supervisor";
            thread.Start();
        }

        // Shutdown order: cancel, kill the child, join with a timeout (the worker disposes its own handles).
        public void Stop(int joinMs)
        {
            cts.Cancel();
            Process p;
            lock (gate) p = current;
            Kill(p);
            if (thread != null && !thread.Join(joinMs)) Log.Warn("nvidia-smi supervisor did not stop within " + joinMs + " ms");
        }

        bool Cancelled { get { return cts.IsCancellationRequested; } }
        bool Wait(int ms) { return cts.Token.WaitHandle.WaitOne(ms); }

        static void Kill(Process p)
        {
            if (p == null) return;
            try { if (!p.HasExited) p.Kill(); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }

        void Run()
        {
            Backoff backoff = new Backoff();
            string lastState = null;
            report(Sample.Status(SourceState.Connecting, "Connecting\u2026", false));
            while (!Cancelled)
            {
                try
                {
                    List<string> looked = new List<string>();
                    string exe = NvidiaSmiLocator.Find(overridePath, looked);
                    if (exe == null)
                    {
                        int wait = backoff.NextTransientMs();
                        string msg = "nvidia-smi not found. Looked in: " + string.Join("; ", looked.ToArray());
                        report(Sample.Status(SourceState.Empty, msg + " \u2014 checking again in " + wait / 1000 + " s", false));
                        StateChange(ref lastState, "empty", msg);
                        if (Wait(wait)) break;
                        continue;
                    }
                    if (probe == null) probe = Probe(exe);
                    if (Cancelled) break;

                    string error;
                    NvErrorKind kind;
                    bool hadData = RunSession(exe, out error, out kind);
                    if (Cancelled) break;
                    if (hadData) { backoff.Reset(); lastState = "live"; }

                    int delay;
                    string text;
                    if (kind == NvErrorKind.Terminal)
                    {
                        delay = backoff.TerminalMs();
                        text = error + " \u2014 Reboot required. Retrying every 60 s.";
                        probe = null;   // probe again once the driver is back
                    }
                    else
                    {
                        delay = backoff.NextTransientMs();
                        text = error + " \u2014 retrying in " + delay / 1000 + " s";
                    }
                    report(Sample.Status(SourceState.Error, text, kind == NvErrorKind.Terminal));
                    StateChange(ref lastState, kind == NvErrorKind.Terminal ? "terminal" : "error", error);
                    if (Wait(delay)) break;
                }
                catch (Exception ex)
                {
                    // Last-resort guard so the supervisor thread itself can never die silently.
                    Log.Error("nvidia-smi supervisor error", ex);
                    report(Sample.Status(SourceState.Error, "Internal error: " + ex.Message + " \u2014 retrying in 10 s", false));
                    if (Wait(10000)) break;
                }
            }
        }

        static void StateChange(ref string last, string state, string detail)
        {
            if (state == last) { Log.Limited("nvidia-" + state, "WARN", "NVIDIA still " + state + ": " + detail); return; }
            last = state;
            Log.Warn("NVIDIA source state -> " + state + ": " + detail);
        }

        // H1: probe each optional field on its own, so an older driver cannot reject the whole query.
        NvidiaProbe Probe(string exe)
        {
            NvidiaProbe p = new NvidiaProbe();
            bool timedOut;
            foreach (string f in new[] { "clocks_event_reasons.active", "clocks_throttle_reasons.active" })
            {
                if (FieldWorks(exe, f, out timedOut)) { p.ThrottleField = f; break; }
                if (timedOut) return LogProbe(p, "probe timed out, optional fields skipped");
            }
            if (FieldWorks(exe, "pcie.link.gen.max", out timedOut)) p.PcieGenMaxField = "pcie.link.gen.max";
            if (timedOut) return LogProbe(p, "probe timed out, optional fields skipped");
            if (FieldWorks(exe, "driver_model.current", out timedOut)) p.DriverModelField = "driver_model.current";
            if (timedOut) return LogProbe(p, "probe timed out, optional fields skipped");
            int code;
            string text = Capture(exe, "--id=0 -q -d TEMPERATURE", ProbeTimeoutMs, out code, out timedOut);
            double? slow, shut;
            NvidiaSmiCsv.ParseTemperatureReport(text, out slow, out shut);
            p.SlowdownC = slow; p.ShutdownC = shut;
            return LogProbe(p, null);
        }

        static NvidiaProbe LogProbe(NvidiaProbe p, string note)
        {
            Log.Info("nvidia-smi probe: throttle=" + (p.ThrottleField ?? "unsupported") + ", pcie.link.gen.max=" + (p.PcieGenMaxField != null ? "yes" : "no") +
                ", driver_model=" + (p.DriverModelField != null ? "yes" : "no") +
                ", slowdown=" + (p.SlowdownC.HasValue ? p.SlowdownC.Value.ToString(CultureInfo.InvariantCulture) + " C" : "unknown") +
                ", shutdown=" + (p.ShutdownC.HasValue ? p.ShutdownC.Value.ToString(CultureInfo.InvariantCulture) + " C" : "unknown") +
                (note != null ? " (" + note + ")" : ""));
            return p;
        }

        bool FieldWorks(string exe, string field, out bool timedOut)
        {
            int code;
            string text = Capture(exe, "--id=0 --query-gpu=" + field + " --format=csv,noheader", ProbeTimeoutMs, out code, out timedOut);
            return !timedOut && code == 0 && !NvidiaErrors.IsInvalidField(text);
        }

        static ProcessStartInfo NewStartInfo(string exe, string args)
        {
            ProcessStartInfo si = new ProcessStartInfo(exe, args);
            si.UseShellExecute = false;
            si.CreateNoWindow = true;
            si.RedirectStandardOutput = true;
            si.RedirectStandardError = true;   // drained asynchronously into a bounded tail, never read synchronously (H4)
            si.StandardOutputEncoding = Encoding.UTF8;
            si.StandardErrorEncoding = Encoding.UTF8;
            return si;
        }

        // Runs a short nvidia-smi command with a hard timeout. Both streams are drained asynchronously.
        string Capture(string exe, string args, int timeoutMs, out int exitCode, out bool timedOut)
        {
            exitCode = -1; timedOut = false;
            StringBuilder output = new StringBuilder();
            object outGate = new object();
            Process p = new Process();
            p.StartInfo = NewStartInfo(exe, args);
            DataReceivedEventHandler collect = delegate(object o, DataReceivedEventArgs e)
            {
                if (e.Data == null) return;
                lock (outGate) { if (output.Length < 65536) output.AppendLine(e.Data); }
            };
            p.OutputDataReceived += collect;
            p.ErrorDataReceived += collect;
            bool started = false;
            try
            {
                p.Start();
                started = true;
                Interlocked.Increment(ref launches);
                ChildProcesses.Register(p);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                long until = Mono.Ms + timeoutMs;
                while (!p.HasExited)
                {
                    if (Wait(50)) { Kill(p); break; }
                    if (Mono.Ms > until)
                    {
                        timedOut = true;
                        Kill(p);
                        Log.Warn("nvidia-smi " + args + " timed out after " + timeoutMs / 1000 + " s and was stopped");
                        break;
                    }
                }
                if (p.WaitForExit(2000)) { p.WaitForExit(); exitCode = p.ExitCode; }
            }
            catch (Win32Exception ex) { lock (outGate) output.AppendLine(ex.Message); }
            catch (InvalidOperationException ex) { lock (outGate) output.AppendLine(ex.Message); }
            finally
            {
                if (started) { ChildProcesses.Unregister(p); Kill(p); }
                p.Dispose();
            }
            lock (outGate) return output.ToString();
        }

        // One "nvidia-smi -l 1" session. Returns when the child exits, stalls (killed by the supervisor) or on cancel.
        // Returns true when at least one good sample arrived.
        bool RunSession(string exe, out string error, out NvErrorKind kind)
        {
            error = null; kind = NvErrorKind.Transient;
            List<string> fields = probe.QueryFields();
            string args = "--id=0 --query-gpu=" + string.Join(",", fields.ToArray()) + " --format=csv,nounits -l 1";
            Process p = new Process();
            p.StartInfo = NewStartInfo(exe, args);
            p.OutputDataReceived += OnOutput;
            p.ErrorDataReceived += OnError;
            lock (gate)
            {
                header = null; gotData = false; lastLineMs = -1;
                errorLines.Clear(); stderrTail.Clear(); stderrBytes = 0;
            }
            Volatile.Write(ref outEof, 0);
            long started = Mono.Ms;
            bool running = false;
            try
            {
                try { p.Start(); running = true; }
                catch (Win32Exception ex) { error = "Could not start " + exe + ": " + ex.Message; return false; }
                Interlocked.Increment(ref launches);
                lock (gate) current = p;
                ChildProcesses.Register(p);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                while (true)
                {
                    if (Wait(250)) return GotData();
                    if (p.HasExited)
                    {
                        long eofBy = Mono.Ms + 2000;
                        while (Volatile.Read(ref outEof) == 0 && Mono.Ms < eofBy) Thread.Sleep(20);
                        error = DescribeExit(p.ExitCode);
                        kind = NvidiaErrors.Classify(error);
                        return GotData();
                    }
                    long last;
                    lock (gate) last = lastLineMs;
                    long silentMs = last < 0 ? Mono.Ms - started : Mono.Ms - last;
                    long limit = last < 0 ? FirstLineTimeoutMs : StallTimeoutMs;
                    if (silentMs > limit)
                    {
                        Kill(p);
                        error = "nvidia-smi stopped responding (no output for " + silentMs / 1000 + " s) and was restarted";
                        string tail = Tail();
                        if (tail.Length > 0) { error += ". Last output: " + tail; kind = NvidiaErrors.Classify(tail); }
                        Log.Warn("NVIDIA stall supervisor: killed nvidia-smi after " + silentMs + " ms without output");
                        return GotData();
                    }
                }
            }
            finally
            {
                lock (gate) current = null;
                if (running) { ChildProcesses.Unregister(p); Kill(p); }
                p.Dispose();
            }
        }

        bool GotData() { lock (gate) return gotData; }

        string Tail()
        {
            lock (gate)
            {
                if (errorLines.Count > 0) return string.Join(" | ", errorLines.ToArray());
                if (stderrTail.Count > 0) return string.Join(" | ", stderrTail.ToArray());
                return "";
            }
        }

        string DescribeExit(int code)
        {
            string tail = Tail();
            return tail.Length > 0 ? "nvidia-smi exited (code " + code + "): " + tail : "nvidia-smi exited (code " + code + ")";
        }

        void OnOutput(object sender, DataReceivedEventArgs e)
        {
            try
            {
                if (e.Data == null) { Volatile.Write(ref outEof, 1); return; }
                string line = e.Data;
                lock (gate) lastLineMs = Mono.Ms;
                if (line.Trim().Length == 0) return;
                if (NvidiaSmiCsv.IsHeader(line)) { lock (gate) header = NvidiaSmiCsv.ParseHeader(line); return; }
                NvidiaProbe pr = probe;
                string[] h;
                lock (gate) h = header;
                if (h == null) h = (pr != null ? pr.QueryFields() : new List<string>(NvidiaSmiCsv.BaseFields)).ToArray();
                string error;
                NvReading r = NvidiaSmiCsv.ParseRow(h, line, out error);
                if (r == null)
                {
                    string msg = Shorten(error);
                    lock (gate)
                    {
                        errorLines.Enqueue(msg);
                        while (errorLines.Count > ErrorLines) errorLines.Dequeue();
                    }
                    // Sticky: the error stays on the card until a good sample arrives.
                    report(Sample.Status(SourceState.Error, msg, NvidiaErrors.Classify(msg) == NvErrorKind.Terminal));
                    return;
                }
                bool first;
                lock (gate) { first = !gotData; gotData = true; errorLines.Clear(); }
                if (first) Log.Info("NVIDIA source state -> live (" + (r.Name ?? "unnamed GPU") + ", fields: " + h.Length + ")");
                Sample s = new Sample();
                s.State = SourceState.Live;
                s.Name = r.Name; s.Util = r.Util; s.MemUsedMiB = r.MemUsedMiB; s.MemTotalMiB = r.MemTotalMiB; s.TempC = r.TempC;
                s.PowerW = r.PowerW; s.PowerLimitW = r.PowerLimitW; s.ClockMHz = r.ClockMHz; s.PcieGen = r.PcieGen;
                s.PcieGenMax = r.PcieGenMax; s.PcieWidth = r.PcieWidth; s.ThrottleMask = r.ThrottleMask; s.DriverModel = r.DriverModel;
                if (pr != null) { s.SlowdownC = pr.SlowdownC; s.ShutdownC = pr.ShutdownC; }
                report(s);
            }
            catch (Exception ex)
            {
                // An exception escaping a thread-pool callback would end the process: log it and keep going.
                Log.Limited("nvidia-parse", "ERROR", "nvidia-smi output handler: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        void OnError(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            lock (gate)
            {
                stderrBytes += e.Data.Length + 1;
                if (e.Data.Trim().Length == 0) return;
                stderrTail.Enqueue(Shorten(e.Data));
                while (stderrTail.Count > TailLines) stderrTail.Dequeue();
            }
        }

        static string Shorten(string s)
        {
            s = (s ?? "").Trim();
            return s.Length > 300 ? s.Substring(0, 300) + "\u2026" : s;
        }
    }

    // ------------------------------------------------------------------------------------------------
    // AMD source: Windows performance counters + D3DKMT, with adapter rediscovery and handle cleanup
    // ------------------------------------------------------------------------------------------------

    sealed class AmdSource
    {
        [StructLayout(LayoutKind.Sequential)] struct LUID { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)] struct AdapterInfo { public uint Handle; public LUID Luid; public uint Sources; public int Precise; }
        [StructLayout(LayoutKind.Sequential)] struct EnumAdapters { public uint Count; public IntPtr Adapters; }
        [StructLayout(LayoutKind.Sequential)] struct QueryInfo { public uint Handle; public int Type; public IntPtr Data; public uint Size; }
        [StructLayout(LayoutKind.Sequential)] struct CloseAdapterArgs { public uint Handle; }
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

        const int TypeSegmentSize = 3, TypeRegistryInfo = 8, TypePerfData = 62;
        const int StatusDeviceRemoved = unchecked((int)0xC00002B6), StatusInvalidHandle = unchecked((int)0xC0000008);
        const int MissesBeforeRediscovery = 3;

        [DllImport("gdi32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern int D3DKMTEnumAdapters2(ref EnumAdapters e);
        [DllImport("gdi32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern int D3DKMTQueryAdapterInfo(ref QueryInfo q);
        [DllImport("gdi32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern int D3DKMTCloseAdapter(ref CloseAdapterArgs c);

        readonly Action<Sample> report;
        readonly CancellationTokenSource cts = new CancellationTokenSource();
        Thread thread;
        uint handle;           // 0 = no adapter open
        string luidKey, name;
        double? dedicatedTotalMiB, sharedTotalMiB;

        public AmdSource(Action<Sample> report) { this.report = report; }

        public void Start()
        {
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Name = "FluxGPU AMD sampler";
            thread.Start();
        }

        // The worker closes its own D3DKMT handle on the way out.
        public void Stop(int joinMs)
        {
            cts.Cancel();
            if (thread != null && !thread.Join(joinMs)) Log.Warn("AMD sampler did not stop within " + joinMs + " ms");
        }

        bool Wait(int ms) { return cts.Token.WaitHandle.WaitOne(ms); }

        static int Query<T>(uint adapter, int type, ref T value) where T : struct
        {
            int size = Marshal.SizeOf(typeof(T));
            IntPtr mem = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(value, mem, false);
                QueryInfo q = new QueryInfo { Handle = adapter, Type = type, Data = mem, Size = (uint)size };
                int status = D3DKMTQueryAdapterInfo(ref q);
                if (status == 0) value = (T)Marshal.PtrToStructure(mem, typeof(T));
                return status;
            }
            finally { Marshal.FreeHGlobal(mem); }
        }

        static void CloseHandle(uint h)
        {
            if (h == 0) return;
            CloseAdapterArgs c = new CloseAdapterArgs { Handle = h };
            D3DKMTCloseAdapter(ref c);
        }

        void CloseCurrent()
        {
            CloseHandle(handle);
            handle = 0; luidKey = null;
        }

        static bool IsAmd(string adapter)
        {
            return adapter != null && (adapter.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0 || adapter.IndexOf("Radeon", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // Every handle from the enumeration is closed except the one kept (M4). The LUID is re-resolved each time.
        bool FindAdapter()
        {
            EnumAdapters e = new EnumAdapters();
            if (D3DKMTEnumAdapters2(ref e) != 0 || e.Count == 0) return false;
            int step = Marshal.SizeOf(typeof(AdapterInfo));
            e.Adapters = Marshal.AllocHGlobal(step * (int)e.Count);
            try
            {
                if (D3DKMTEnumAdapters2(ref e) != 0) return false;
                bool found = false;
                for (int i = 0; i < e.Count; i++)
                {
                    AdapterInfo a = (AdapterInfo)Marshal.PtrToStructure(IntPtr.Add(e.Adapters, i * step), typeof(AdapterInfo));
                    bool keep = false;
                    if (!found)
                    {
                        RegistryInfo r = new RegistryInfo();
                        if (Query(a.Handle, TypeRegistryInfo, ref r) == 0 && IsAmd(r.Adapter))
                        {
                            keep = true; found = true;
                            handle = a.Handle;
                            name = r.Adapter;
                            luidKey = string.Format(CultureInfo.InvariantCulture, "luid_0x{0:X8}_0x{1:X8}_", a.Luid.High, a.Luid.Low).ToLowerInvariant();
                            SegmentSize s = new SegmentSize();
                            dedicatedTotalMiB = null; sharedTotalMiB = null;
                            if (Query(handle, TypeSegmentSize, ref s) == 0)
                            {
                                if (s.Dedicated > 0) dedicatedTotalMiB = s.Dedicated / 1048576.0;
                                if (s.Shared > 0) sharedTotalMiB = s.Shared / 1048576.0;
                            }
                        }
                    }
                    if (!keep) CloseHandle(a.Handle);
                }
                return found;
            }
            finally { Marshal.FreeHGlobal(e.Adapters); }
        }

        void Run()
        {
            Backoff backoff = new Backoff();
            Dictionary<string, CounterSample> previous = new Dictionary<string, CounterSample>();
            bool baseline = false;
            int misses = 0;
            string lastState = null;
            report(Sample.Status(SourceState.Connecting, "Connecting\u2026", false));
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        if (handle == 0)
                        {
                            if (!FindAdapter())
                            {
                                int wait = backoff.NextTransientMs();
                                report(Sample.Status(SourceState.Empty, "No AMD adapter found \u2014 checking again in " + wait / 1000 + " s", false));
                                if (lastState != "empty") { lastState = "empty"; Log.Warn("AMD source state -> empty: no AMD/Radeon adapter"); }
                                if (Wait(wait)) break;
                                continue;
                            }
                            Log.Info("AMD adapter: " + name + " (" + luidKey.TrimEnd('_') + ")");
                            previous.Clear(); baseline = false; misses = 0;
                        }

                        Sample s = new Sample();
                        s.State = SourceState.Live;
                        s.Name = name; s.MemTotalMiB = dedicatedTotalMiB; s.SharedTotalMiB = sharedTotalMiB;

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
                                if (!previous.TryGetValue(n, out old)) continue;   // a new instance has no rate until the next interval
                                double v = CounterSample.Calculate(old, d.Sample);
                                int at = n.IndexOf("_engtype_", StringComparison.Ordinal);
                                int eng = n.IndexOf("_eng_", StringComparison.Ordinal);
                                if (at < 0 || eng < 0 || double.IsNaN(v) || double.IsInfinity(v) || v < 0) continue;
                                string key = n.Substring(eng);
                                double sum;
                                engines.TryGetValue(key, out sum);
                                engines[key] = sum + v;
                            }
                        previous = current;

                        InstanceDataCollectionCollection mem = new PerformanceCounterCategory("GPU Adapter Memory").ReadCategory();
                        int matchedDedicated, matchedShared;
                        s.MemUsedMiB = Total(mem["Dedicated Usage"], out matchedDedicated);
                        s.SharedUsedMiB = Total(mem["Shared Usage"], out matchedShared);

                        // The adapter always has memory counters; none matching means the LUID changed (driver update, disable/enable).
                        if (matchedDedicated + matchedShared == 0) misses++; else misses = 0;
                        if (misses >= MissesBeforeRediscovery) { Rediscover("counters no longer match the adapter (LUID changed?)", ref lastState); continue; }

                        PerfData p = new PerfData();
                        int status = Query(handle, TypePerfData, ref p);
                        if (status == StatusDeviceRemoved || status == StatusInvalidHandle) { Rediscover("adapter removed or handle invalid", ref lastState); continue; }
                        if (status == 0)
                        {
                            if (p.Temperature > 0 && p.Temperature < 1500) s.TempC = p.Temperature / 10.0;
                            if (p.Power > 0 && p.Power <= 2000) s.PowerPct = p.Power / 10.0;   // tenths of a percent; meaning unverified
                        }

                        if (!baseline)
                        {
                            // The first interval after start or rediscovery has no rate yet: a gap, never a fake 0 %.
                            baseline = true;
                            s = Sample.Status(SourceState.Connecting, "Connecting\u2026", false);
                        }
                        else
                        {
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
                            if (lastState != "live") { lastState = "live"; Log.Info("AMD source state -> live"); }
                            backoff.Reset();
                        }
                        report(s);
                    }
                    catch (Exception ex)
                    {
                        // Performance counters throw several unrelated types (InvalidOperationException, Win32Exception, ...).
                        int wait = backoff.NextTransientMs();
                        report(Sample.Status(SourceState.Error, ex.Message + " \u2014 retrying in " + wait / 1000 + " s", false));
                        Log.Limited("amd-" + ex.GetType().Name, "WARN", "AMD source error: " + ex.GetType().Name + ": " + ex.Message);
                        lastState = "error";
                        if (Wait(wait)) break;
                        continue;
                    }
                    if (Wait(1000)) break;
                }
            }
            finally
            {
                try { CloseCurrent(); }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
            }
        }

        void Rediscover(string why, ref string lastState)
        {
            Log.Warn("AMD adapter rediscovery: " + why);
            report(Sample.Status(SourceState.Error, "AMD adapter changed, reconnecting\u2026", false));
            lastState = "reconnecting";
            CloseCurrent();
        }

        double? Total(InstanceDataCollection c, out int matched)
        {
            matched = 0;
            if (c == null) return null;
            double bytes = 0;
            foreach (InstanceData d in c.Values)
                if (d.InstanceName.ToLowerInvariant().IndexOf(luidKey, StringComparison.Ordinal) >= 0) { bytes += d.RawValue; matched++; }
            return matched > 0 ? bytes / 1048576.0 : (double?)null;
        }

        // For tests: native struct sizes that must match d3dkmthk.h on x64.
        public static Dictionary<string, int> StructSizes()
        {
            Dictionary<string, int> d = new Dictionary<string, int>();
            d["AdapterInfo"] = Marshal.SizeOf(typeof(AdapterInfo));
            d["EnumAdapters"] = Marshal.SizeOf(typeof(EnumAdapters));
            d["QueryInfo"] = Marshal.SizeOf(typeof(QueryInfo));
            d["RegistryInfo"] = Marshal.SizeOf(typeof(RegistryInfo));
            d["SegmentSize"] = Marshal.SizeOf(typeof(SegmentSize));
            d["PerfData"] = Marshal.SizeOf(typeof(PerfData));
            d["CloseAdapter"] = Marshal.SizeOf(typeof(CloseAdapterArgs));
            return d;
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Theme
    // ------------------------------------------------------------------------------------------------

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

    static class Ui
    {
        public static TextBlock Label(string text, double size, Brush brush, FontWeight weight)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = brush, FontWeight = weight, TextTrimming = TextTrimming.CharacterEllipsis };
        }
    }

    // ------------------------------------------------------------------------------------------------
    // GPU card (UI only). State Matrix: Connecting, Live, Empty, Error, Stale, Alert.
    // ------------------------------------------------------------------------------------------------

    sealed class GpuCard : Border
    {
        const double SparkW = 210, SparkH = 46;
        const int Points = 60;
        public const double StaleOpacity = 0.5;
        readonly TextBlock title, load, memText, compact, status, info;
        readonly TextBlock[] values = new TextBlock[4];
        readonly System.Windows.Shapes.Path line = new System.Windows.Shapes.Path(), area = new System.Windows.Shapes.Path();
        readonly Border fill, track;
        readonly StackPanel detail = new StackPanel();
        readonly List<double?> history = new List<double?>();
        readonly bool isNvidia;
        double memFraction;
        bool compactMode;
        public long LastGoodMs = -1;          // Mono.Ms of the last live sample
        public DateTime LastGoodLocal;
        public SourceState State = SourceState.Connecting;
        public bool Stale;

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

            status = Ui.Label("Connecting\u2026", 12, Theme.Sub, FontWeights.Normal);
            status.TextWrapping = TextWrapping.Wrap;
            status.TextTrimming = TextTrimming.None;
            status.Margin = new Thickness(0, 4, 0, 0);
            root.Children.Add(status);

            compact = Ui.Label("", 12.5, Theme.Sub, FontWeights.Normal);
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

            memText = Ui.Label(isNvidia ? "Memory" : "Shared memory", 12, Theme.Sub, FontWeights.Normal);
            detail.Children.Add(memText);
            fill = new Border { Background = Theme.Accent, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            track = new Border { Background = Theme.Track, CornerRadius = new CornerRadius(3), Height = 6, Margin = new Thickness(0, 4, 0, 10), Child = fill };
            track.SizeChanged += delegate { fill.Width = track.ActualWidth * memFraction; };
            detail.Children.Add(track);

            string[] names = isNvidia ? new[] { "Temperature", "Power / limit", "Core clock", "PCIe link" }
                                      : new[] { "Temperature", "Drv power (?)", "Dedicated", "Busy engine" };
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
            if (!isNvidia) tiles.ToolTip = "Drv power is the raw percentage the AMD driver reports. Its meaning has not been verified, so FluxGPU never alerts on it.";
            detail.Children.Add(tiles);

            info = Ui.Label("", 11, Theme.Sub, FontWeights.Normal);
            info.TextWrapping = TextWrapping.Wrap;
            info.TextTrimming = TextTrimming.None;
            info.Margin = new Thickness(0, 8, 0, 0);
            info.Visibility = Visibility.Collapsed;
            detail.Children.Add(info);
            detail.Opacity = StaleOpacity;
        }

        public void SetCompact(bool on)
        {
            compactMode = on;
            detail.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            compact.Visibility = on && State == SourceState.Live ? Visibility.Visible : Visibility.Collapsed;
        }

        static string F(double? v, string format, string unit)
        {
            return v.HasValue ? v.Value.ToString(format, CultureInfo.InvariantCulture) + unit : "n/a";
        }

        static string Clock(DateTime t) { return t.ToString("hh:mm:ss tt", CultureInfo.InvariantCulture); }

        // Connecting / Empty / Error. Clears every live value so nothing stale looks current (B5).
        public void ShowStatus(Sample s)
        {
            State = s.State;
            Stale = false;
            string text = s.Message ?? "";
            if (LastGoodMs >= 0 && s.State == SourceState.Error) text += "\nLast good sample " + Clock(LastGoodLocal);
            status.Text = text;
            status.Foreground = s.State == SourceState.Error ? (s.Terminal ? Theme.Crit : Theme.Warn) : Theme.Sub;
            status.Visibility = Visibility.Visible;
            load.Text = "--";
            memFraction = 0; fill.Width = 0;
            memText.Text = (isNvidia ? "Memory" : "Shared memory") + "   n/a";
            foreach (TextBlock v in values) { v.Text = "--"; v.Foreground = Theme.Text; }
            info.Visibility = Visibility.Collapsed;
            history.Add(null);   // a gap in the graph, not a drop to 0
            if (history.Count > Points) history.RemoveAt(0);
            DrawHistory();
            detail.Opacity = StaleOpacity;
            compact.Visibility = Visibility.Collapsed;
        }

        // Live data stopped arriving: keep the last values, dimmed, with the time of the last sample.
        public void MarkStale()
        {
            if (Stale || State != SourceState.Live) return;
            Stale = true;
            detail.Opacity = StaleOpacity;
            compact.Opacity = StaleOpacity;
            status.Text = "Stale \u2014 last sample " + Clock(LastGoodLocal) + ", reconnecting\u2026";
            status.Foreground = Theme.Warn;
            status.Visibility = Visibility.Visible;
        }

        public void Update(Sample s, double warnC, double critC)
        {
            if (s.State != SourceState.Live) { ShowStatus(s); return; }
            State = SourceState.Live;
            Stale = false;
            LastGoodMs = Mono.Ms;
            LastGoodLocal = DateTime.Now;
            status.Visibility = Visibility.Collapsed;
            detail.Opacity = 1; compact.Opacity = 1;
            compact.Visibility = compactMode ? Visibility.Visible : Visibility.Collapsed;
            if (s.Name != null) title.Text = s.Name.Replace("(TM)", "");
            load.Text = F(s.Util, "0", "%");
            history.Add(s.Util);
            if (history.Count > Points) history.RemoveAt(0);
            DrawHistory();

            double? used, total;
            string memLabel;
            if (isNvidia) { used = s.MemUsedMiB; total = s.MemTotalMiB; memLabel = "Memory"; }
            else { used = s.SharedUsedMiB; total = s.SharedTotalMiB; memLabel = "Shared memory"; }   // iGPU: shared memory is what it really uses (M5)
            bool haveTotal = total.HasValue && total.Value > 0;
            memFraction = haveTotal && used.HasValue ? Math.Max(0, Math.Min(1, used.Value / total.Value)) : 0;
            fill.Width = track.ActualWidth * memFraction;
            memText.Text = memLabel + "   " + Gb(used) + " / " + (haveTotal ? Gb(total) : "n/a");

            values[0].Text = F(s.TempC, "0", "\u00B0C");
            values[0].Foreground = s.TempC >= critC ? Theme.Crit : s.TempC >= warnC ? Theme.Warn : Theme.Text;
            List<string> parts = new List<string>();
            bool throttled = false;
            if (isNvidia)
            {
                values[1].Text = s.PowerLimitW.HasValue ? F(s.PowerW, "0", "") + "/" + F(s.PowerLimitW, "0", " W") : F(s.PowerW, "0", " W");
                values[2].Text = F(s.ClockMHz, "0", " MHz");
                values[3].Text = s.PcieGen.HasValue ? "Gen" + F(s.PcieGen, "0", "") + " x" + F(s.PcieWidth, "0", "") : "n/a";
                if (s.ThrottleMask.HasValue)
                {
                    List<string> badges = Throttle.WarningBadges(s.ThrottleMask.Value);
                    if (badges.Count > 0) { parts.Add("\u26A0 Throttling: " + string.Join(", ", badges.ToArray())); throttled = true; }
                    if (Throttle.PowerCapped(s.ThrottleMask.Value)) parts.Add("Power cap (normal under load)");
                }
                if (s.SlowdownC.HasValue && s.TempC.HasValue) parts.Add(F(s.SlowdownC - s.TempC, "0", "\u00B0C") + " to slowdown");
                if (s.DriverModel != null) parts.Add(s.DriverModel + " mode");
                if (s.PcieGenMax.HasValue) parts.Add("PCIe max Gen" + F(s.PcieGenMax, "0", ""));
            }
            else
            {
                values[1].Text = F(s.PowerPct, "0", "%");
                values[2].Text = s.MemTotalMiB.HasValue && s.MemTotalMiB.Value > 0 ? Gb(s.MemUsedMiB) + "/" + Gb(s.MemTotalMiB) : Gb(s.MemUsedMiB);
                values[3].Text = s.Engine ?? "n/a";
            }
            info.Text = string.Join("   \u00B7   ", parts.ToArray());
            info.Foreground = throttled ? Theme.Warn : Theme.Sub;
            info.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            // Compact view: AMD driver power is left out on purpose (unverified).
            string line2 = "Load " + F(s.Util, "0", "%") + "   \u00B7   " + Gb(used) + " / " + (haveTotal ? Gb(total) : "n/a");
            if (s.TempC.HasValue) line2 += "   \u00B7   " + F(s.TempC, "0", "\u00B0C");
            if (isNvidia) line2 += "   \u00B7   " + F(s.PowerW, "0", " W");
            if (throttled) line2 += "   \u00B7   THROTTLING";
            compact.Text = line2;
            compact.Foreground = s.TempC >= critC ? Theme.Crit : (s.TempC >= warnC || throttled) ? Theme.Warn : Theme.Sub;
        }

        static string Gb(double? mib)
        {
            return mib.HasValue ? (mib.Value / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " GB" : "n/a";
        }

        // Each run of valid points is its own figure, so gaps are real gaps and no NaN ever reaches WPF geometry (H3).
        void DrawHistory()
        {
            PathGeometry lineGeo = new PathGeometry(), areaGeo = new PathGeometry();
            double dx = SparkW / (Points - 1);
            double start = SparkW - dx * (history.Count - 1);
            foreach (int[] run in Sparkline.Runs(history))
            {
                PathFigure lf = new PathFigure { IsClosed = false, IsFilled = false };
                PathFigure af = new PathFigure { IsClosed = true, IsFilled = true };
                double x0 = start + run[0] * dx, x1 = start + run[1] * dx;
                lf.StartPoint = Pt(x0, history[run[0]].Value);
                af.StartPoint = new Point(x0, SparkH);
                af.Segments.Add(new LineSegment(Pt(x0, history[run[0]].Value), false));
                for (int i = run[0] + 1; i <= run[1]; i++)
                {
                    Point p = Pt(start + i * dx, history[i].Value);
                    lf.Segments.Add(new LineSegment(p, true));
                    af.Segments.Add(new LineSegment(p, false));
                }
                af.Segments.Add(new LineSegment(new Point(x1, SparkH), false));
                lineGeo.Figures.Add(lf);
                areaGeo.Figures.Add(af);
            }
            lineGeo.Freeze(); areaGeo.Freeze();
            line.Data = lineGeo;
            area.Data = areaGeo;
        }

        static Point Pt(double x, double util)
        {
            double u = Math.Max(0, Math.Min(100, util));
            return new Point(x, 1 + (SparkH - 2) * (1 - u / 100.0));
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Alerts: sound + taskbar flash + coloured window border + log entry (no toast until a tray icon exists)
    // ------------------------------------------------------------------------------------------------

    sealed class AlertManager
    {
        [StructLayout(LayoutKind.Sequential)]
        struct FlashInfo { public uint Size; public IntPtr Hwnd; public uint Flags; public uint Count; public uint Timeout; }
        [DllImport("user32.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern bool FlashWindowEx(ref FlashInfo info);
        const uint FlashAll = 3, FlashTimerNoForeground = 12;

        public static int FlashInfoSize { get { return Marshal.SizeOf(typeof(FlashInfo)); } }

        readonly Window window;
        readonly Border frame;
        readonly TextBlock banner;
        readonly bool enabled;
        public bool Sound = true;
        AlertLevel temp = AlertLevel.None;
        bool telemetryLost;

        public AlertManager(Window window, Border frame, TextBlock banner, bool enabled)
        {
            this.window = window; this.frame = frame; this.banner = banner; this.enabled = enabled;
        }

        public bool TelemetryLost { get { return telemetryLost; } }
        public AlertLevel Temperature { get { return temp; } }
        string tempText = "", lostText = "";

        public void SetTemperature(AlertLevel level, double? tempC, double warnC, double critC)
        {
            if (level == temp) return;
            string t = tempC.HasValue ? tempC.Value.ToString("0", CultureInfo.InvariantCulture) + " C" : "n/a";
            string th = " (warn " + warnC.ToString("0", CultureInfo.InvariantCulture) + " C, critical " + critC.ToString("0", CultureInfo.InvariantCulture) + " C)";
            if (level > temp)
            {
                Log.Warn("ALERT: NVIDIA temperature " + level.ToString().ToUpperInvariant() + " at " + t + th);
                Raise(level == AlertLevel.Critical);
            }
            else Log.Info("Alert eased: NVIDIA temperature now " + level.ToString().ToLowerInvariant() + " at " + t + th);
            temp = level;
            tempText = level == AlertLevel.None ? "" : "\u26A0 NVIDIA temperature " + (level == AlertLevel.Critical ? "CRITICAL" : "high") + ": " +
                (tempC.HasValue ? tempC.Value.ToString("0", CultureInfo.InvariantCulture) + "\u00B0C" : "n/a") +
                " (warn " + warnC.ToString("0", CultureInfo.InvariantCulture) + ", critical " + critC.ToString("0", CultureInfo.InvariantCulture) + ")";
            Refresh();
        }

        public void SetTelemetryLost(bool lost, long silentMs)
        {
            if (lost == telemetryLost) return;
            telemetryLost = lost;
            if (lost)
            {
                Log.Warn("ALERT: P40 telemetry lost (no good NVIDIA sample for " + silentMs / 1000 + " s)");
                Raise(true);
            }
            else Log.Info("Alert cleared: P40 telemetry restored");
            UpdateLostText(silentMs);
            Refresh();
        }

        public void UpdateLostText(long silentMs)
        {
            lostText = telemetryLost ? "\u26A0 P40 telemetry lost \u2014 no good NVIDIA sample for " + silentMs / 1000 + " s" : "";
            Refresh();
        }

        void Raise(bool critical)
        {
            if (!enabled) return;
            try
            {
                if (Sound) { if (critical) SystemSounds.Hand.Play(); else SystemSounds.Exclamation.Play(); }
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (critical && hwnd != IntPtr.Zero)
                {
                    FlashInfo f = new FlashInfo { Size = (uint)Marshal.SizeOf(typeof(FlashInfo)), Hwnd = hwnd, Flags = FlashAll | FlashTimerNoForeground, Count = 0, Timeout = 0 };
                    FlashWindowEx(ref f);
                }
            }
            catch (Exception ex) { Log.Error("Could not play the alert", ex); }   // an alert failure must never stop monitoring
        }

        public void Refresh()
        {
            frame.BorderThickness = new Thickness(2);
            if (telemetryLost || temp == AlertLevel.Critical) frame.BorderBrush = Theme.Crit;
            else if (temp == AlertLevel.Warn) frame.BorderBrush = Theme.Warn;
            else frame.BorderBrush = Brushes.Transparent;
            string text = lostText;
            if (tempText.Length > 0) text = text.Length > 0 ? text + "\n" + tempText : tempText;
            banner.Text = text;
            banner.Foreground = telemetryLost || temp == AlertLevel.Critical ? Theme.Crit : Theme.Warn;
            banner.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Main window
    // ------------------------------------------------------------------------------------------------

    sealed class MonitorWindow : Window
    {
        [DllImport("dwmapi.dll")] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        const int UiTickMs = 500, StaleAfterMs = 8000, ThemeEveryTicks = 6, JoinMs = 2000;

        readonly GpuCard nvidia = new GpuCard("NVIDIA GPU", "AI compute", true);
        readonly GpuCard amd = new GpuCard("AMD GPU", "Display", false);
        readonly SampleSlot nvidiaSlot = new SampleSlot(), amdSlot = new SampleSlot();
        int nvidiaSeen, amdSeen, ticks;
        readonly NvidiaSource nvidiaSource;
        readonly AmdSource amdSource;
        readonly AlertManager alerts;
        readonly TelemetryWatch telemetry;
        TempAlarm alarm;
        double warnC = Thresholds.FallbackWarnC, critC = Thresholds.FallbackCritC;
        string thresholdSource;
        double? slowdownSeen;
        TextBlock pinIcon, sizeIcon;
        bool compactMode, stopped;
        readonly bool snapshot;
        readonly AppSettings settings;
        readonly string settingsFolder = AppInfo.DataDirectory;
        static readonly string LegacySettingsFile = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, SettingsStore.LegacyFileName);

        public MonitorWindow(string snapshotPath, bool startCompact, string nvidiaSmiOverride)
        {
            snapshot = snapshotPath != null;
            Theme.Apply();
            List<string> notes = new List<string>();
            settings = snapshot ? new AppSettings() : SettingsStore.Load(settingsFolder, LegacySettingsFile, notes);
            foreach (string n in notes) Log.Warn("Settings: " + n);
            ResolveThresholds(null);

            Title = AppInfo.Name;
            BitmapFrame ico = Asset("FluxGPU.ico");
            if (ico != null) Icon = ico;
            Width = 380; SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            Topmost = true; Background = Theme.Bg;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            StackPanel root = new StackPanel { Background = Theme.Bg };
            DockPanel bar = new DockPanel { Height = 38, Background = Theme.Bg };
            Border close = IconButton("\uE8BB", "Close", delegate { Close(); }).Item1;
            DockPanel.SetDock(close, Dock.Right); bar.Children.Add(close);
            Tuple<Border, TextBlock> size = IconButton("\uE73F", "Compact view", delegate { SetCompact(!compactMode); SaveSettings(); });
            sizeIcon = size.Item2; DockPanel.SetDock(size.Item1, Dock.Right); bar.Children.Add(size.Item1);
            Tuple<Border, TextBlock> pin = IconButton("\uE840", "Always on top: on", delegate { SetPinned(!Topmost); SaveSettings(); });
            pinIcon = pin.Item2; DockPanel.SetDock(pin.Item1, Dock.Right); bar.Children.Add(pin.Item1);
            pinIcon.Text = "\uE841"; pinIcon.Foreground = Theme.Accent;
            StackPanel brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            brand.ToolTip = AppInfo.Name + " " + AppInfo.Version + "\nSettings: " + IOPath.Combine(settingsFolder, SettingsStore.FileName) +
                "\nLogs: " + (Log.Folder ?? "unavailable");
            BitmapFrame markImage = Asset("mark.png");
            if (markImage != null)
            {
                Image mark = new Image { Source = markImage, Width = 22, Height = 22, Margin = new Thickness(0, 0, 8, 0) };
                RenderOptions.SetBitmapScalingMode(mark, BitmapScalingMode.HighQuality);
                brand.Children.Add(mark);
            }
            TextBlock caption = new TextBlock { FontSize = 15, FontFamily = new FontFamily("Bahnschrift"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            caption.Inlines.Add(new System.Windows.Documents.Run("Flux") { Foreground = Theme.Text });
            caption.Inlines.Add(new System.Windows.Documents.Run("GPU") { Foreground = Theme.Brand });
            brand.Children.Add(caption);
            bar.Children.Add(brand);
            root.Children.Add(bar);
            TextBlock banner = Ui.Label("", 12.5, Theme.Crit, FontWeights.SemiBold);
            banner.TextWrapping = TextWrapping.Wrap;
            banner.TextTrimming = TextTrimming.None;
            banner.Margin = new Thickness(14, 0, 14, 8);
            banner.Visibility = Visibility.Collapsed;
            root.Children.Add(banner);
            root.Children.Add(nvidia);
            root.Children.Add(amd);
            Border frame = new Border { Child = root, BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(2) };
            Content = frame;

            alerts = new AlertManager(this, frame, banner, !snapshot);
            alerts.Sound = settings.AlertSound;
            telemetry = new TelemetryWatch(TelemetryWatch.DefaultLostAfterMs, Mono.Ms);

            Left = SystemParameters.WorkArea.Right - Width - 16;
            Top = SystemParameters.WorkArea.Top + 16;
            if (!snapshot) ApplySettings();
            if (startCompact) SetCompact(true);

            MouseLeftButtonDown += delegate
            {
                if (Mouse.LeftButton != MouseButtonState.Pressed) return;
                DragMove();
                SaveSettings();   // DragMove returns when the drag ends
            };
            SourceInitialized += delegate
            {
                int round = 2;
                int hr = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref round, 4);
                if (hr != 0) Log.Info("Rounded corners not available (DwmSetWindowAttribute 0x" + hr.ToString("X8", CultureInfo.InvariantCulture) + ")");
            };

            nvidiaSource = new NvidiaSource(nvidiaSlot.Put, nvidiaSmiOverride ?? settings.NvidiaSmiPath);
            amdSource = new AmdSource(amdSlot.Put);
            nvidiaSource.Start();
            amdSource.Start();

            DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(UiTickMs) };
            timer.Tick += delegate { Tick(); };
            timer.Start();

            Closed += delegate { timer.Stop(); Shutdown("window closed"); };
            if (Application.Current != null)
                Application.Current.SessionEnding += delegate(object o, SessionEndingCancelEventArgs e) { Shutdown("Windows session ending (" + e.ReasonSessionEnding + ")"); };

            if (snapshot)
            {
                DispatcherTimer shot = new DispatcherTimer { Interval = TimeSpan.FromSeconds(9) };
                shot.Tick += delegate
                {
                    shot.Stop();
                    try
                    {
                        RenderTargetBitmap bmp = new RenderTargetBitmap((int)Math.Ceiling(frame.ActualWidth * 2), (int)Math.Ceiling(frame.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
                        bmp.Render(frame);
                        PngBitmapEncoder png = new PngBitmapEncoder();
                        png.Frames.Add(BitmapFrame.Create(bmp));
                        using (FileStream fs = File.Create(snapshotPath)) png.Save(fs);
                        Log.Info("Snapshot written to " + snapshotPath);
                    }
                    catch (Exception ex) { Log.Error("Snapshot failed for " + snapshotPath, ex); }
                    Close();
                };
                shot.Start();
            }
            Log.Info("Window ready. Temperature alerts: warn " + warnC + " C, critical " + critC + " C (" + thresholdSource + ")");
        }

        void ResolveThresholds(double? slowdownC)
        {
            double w, c;
            string src;
            Thresholds.Resolve(slowdownC, settings.WarnC, settings.CritC, out w, out c, out src);
            if (alarm != null && w == warnC && c == critC) return;
            warnC = w; critC = c; thresholdSource = src;
            AlertLevel keep = alarm != null ? alarm.Level : AlertLevel.None;
            alarm = new TempAlarm(warnC, critC, settings.HystC);
            alarm.Level = keep;
            if (slowdownC.HasValue) Log.Info("Temperature alerts: warn " + warnC + " C, critical " + critC + " C (" + src + ")");
        }

        void Tick()
        {
            ticks++;
            if (ticks % ThemeEveryTicks == 0) Theme.Apply();
            long now = Mono.Ms;

            Sample s = nvidiaSlot.TakeIfNew(ref nvidiaSeen);
            if (s != null)
            {
                if (s.SlowdownC.HasValue && s.SlowdownC != slowdownSeen) { slowdownSeen = s.SlowdownC; ResolveThresholds(s.SlowdownC); }
                SafeUpdate(nvidia, s, warnC, critC);
                if (s.State == SourceState.Live)
                {
                    telemetry.Good(now);
                    alerts.SetTemperature(alarm.Update(s.TempC), s.TempC, warnC, critC);
                }
            }
            Sample a = amdSlot.TakeIfNew(ref amdSeen);
            if (a != null) SafeUpdate(amd, a, Thresholds.FallbackWarnC, Thresholds.FallbackCritC);   // AMD: colours only, no alerts

            CheckStale(nvidia, "NVIDIA", now);
            CheckStale(amd, "AMD", now);

            if (telemetry.Update(now) != 0) alerts.SetTelemetryLost(telemetry.Lost, telemetry.SinceGoodMs(now));
            if (telemetry.Lost) alerts.UpdateLostText(telemetry.SinceGoodMs(now));
        }

        static void CheckStale(GpuCard card, string which, long now)
        {
            if (card.State != SourceState.Live || card.Stale || card.LastGoodMs < 0 || now - card.LastGoodMs <= StaleAfterMs) return;
            card.MarkStale();
            Log.Warn(which + " card stale: no new sample for " + (now - card.LastGoodMs) / 1000 + " s");
        }

        // A display bug in one card must not take down the window or the other card.
        void SafeUpdate(GpuCard card, Sample s, double warn, double crit)
        {
            try { card.Update(s, warn, crit); }
            catch (Exception ex)
            {
                Log.Limited("card-update", "ERROR", "Card update failed: " + ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex.StackTrace);
                try { card.ShowStatus(Sample.Status(SourceState.Error, "Display error (details in the log)", false)); }
                catch (Exception inner) { Log.Limited("card-status", "ERROR", "Card status failed: " + inner.Message); }
            }
        }

        // Cancel, stop and join the workers (they free their own handles), then save settings.
        void Shutdown(string why)
        {
            if (stopped) return;
            stopped = true;
            Log.Info("Shutting down: " + why);
            nvidiaSource.Stop(JoinMs);
            amdSource.Stop(JoinMs);
            ChildProcesses.KillAll();
            if (!snapshot) SaveSettings();
        }

        // Logo images are embedded in the program at build time. A missing resource is logged, not fatal.
        static BitmapFrame Asset(string name)
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                {
                    if (s == null) { Log.Warn("Embedded resource missing: " + name + " (build with build.cmd)"); return null; }
                    return BitmapFrame.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                }
            }
            catch (Exception ex) { Log.Error("Could not load embedded resource " + name, ex); return null; }
        }

        static Tuple<Border, TextBlock> IconButton(string glyph, string tip, Action click)
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
            pinIcon.Text = on ? "\uE841" : "\uE718";
            pinIcon.Foreground = on ? Theme.Accent : Theme.Text;
            ((Border)pinIcon.Parent).ToolTip = "Always on top: " + (on ? "on" : "off");
        }

        void SetCompact(bool on)
        {
            compactMode = on;
            nvidia.SetCompact(on);
            amd.SetCompact(on);
            sizeIcon.Text = on ? "\uE740" : "\uE73F";
            ((Border)sizeIcon.Parent).ToolTip = on ? "Full view" : "Compact view";
        }

        void ApplySettings()
        {
            if (settings.Left.HasValue && settings.Top.HasValue)
            {
                double x = settings.Left.Value, y = settings.Top.Value;
                if (x >= SystemParameters.VirtualScreenLeft - 100 && x < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
                    y >= SystemParameters.VirtualScreenTop && y < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 60) { Left = x; Top = y; }
                else Log.Info("Saved position " + x + "," + y + " is off screen; using the default position");
            }
            SetPinned(settings.Topmost);
            SetCompact(settings.Compact);
        }

        void SaveSettings()
        {
            if (snapshot || settings == null) return;
            settings.Left = Left; settings.Top = Top; settings.Topmost = Topmost; settings.Compact = compactMode;
            List<string> notes = new List<string>();
            if (!SettingsStore.Save(settings, settingsFolder, notes))
                foreach (string n in notes) Log.Warn("Settings: " + n);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Entry point: arguments, single instance, crash handlers, log
    // ------------------------------------------------------------------------------------------------

    static class Program
    {
        static int uiErrors;
        static long uiErrorWindowStart;

        [STAThread]
        static int Main(string[] args)
        {
            string snapshot = null, nvidiaSmi = null;
            bool compact = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--snapshot" && i + 1 < args.Length) snapshot = args[++i];
                else if (args[i] == "--compact") compact = true;
                else if (args[i] == "--nvidia-smi" && i + 1 < args.Length) nvidiaSmi = args[++i];
            }

            Log.Init(IOPath.Combine(AppInfo.DataDirectory, "logs"));
            AppDomain.CurrentDomain.UnhandledException += delegate(object o, UnhandledExceptionEventArgs e)
            {
                Log.Error("Unhandled exception (terminating=" + e.IsTerminating + ")", e.ExceptionObject as Exception);
                ChildProcesses.KillAll();   // never leave nvidia-smi running behind a crash
            };
            TaskScheduler.UnobservedTaskException += delegate(object o, UnobservedTaskExceptionEventArgs e)
            {
                Log.Error("Unobserved task exception", e.Exception);
                e.SetObserved();
            };

            if (nvidiaSmi != null && !NvidiaSmiLocator.IsAbsolute(nvidiaSmi))
            {
                Log.Warn("--nvidia-smi ignored: it must be a full path (" + nvidiaSmi + ")");
                nvidiaSmi = null;
            }

            bool first;
            using (Mutex m = new Mutex(true, snapshot == null ? "LocalAI.FluxGPU" : "LocalAI.FluxGPU.Snapshot", out first))
            {
                if (!first) { Log.Info("Another FluxGPU is already running; exiting"); return 0; }
                Log.Info(AppInfo.Name + " " + AppInfo.Version + " starting (pid " + Process.GetCurrentProcess().Id + ", " + Environment.OSVersion + ", " +
                    (Environment.Is64BitProcess ? "x64" : "x86") + ")");
                try
                {
                    Application app = new Application();
                    app.DispatcherUnhandledException += OnUiException;
                    int code = app.Run(new MonitorWindow(snapshot, compact, nvidiaSmi));
                    Log.Info("Exited normally");
                    return code;
                }
                catch (Exception ex)
                {
                    Log.Error("FluxGPU failed to start", ex);
                    ChildProcesses.KillAll();
                    MessageBox.Show("FluxGPU could not start:\n\n" + ex.Message + "\n\nDetails are in the log:\n" + (Log.Folder ?? "(log unavailable)"),
                        AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Error);
                    return 1;
                }
            }
        }

        // Keep running after a UI exception, but give up if they keep coming (more than 10 in a minute).
        static void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Error("UI exception", e.Exception);
            long now = Mono.Ms;
            if (now - uiErrorWindowStart > 60000) { uiErrorWindowStart = now; uiErrors = 0; }
            uiErrors++;
            if (uiErrors > 10)
            {
                Log.Warn("Too many UI exceptions in one minute; shutting down");
                ChildProcesses.KillAll();
                return;   // not handled: the app ends, and the crash is in the log
            }
            e.Handled = true;
        }
    }
}
