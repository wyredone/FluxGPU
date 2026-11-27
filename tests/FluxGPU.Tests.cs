// FluxGPU pure-logic tests. No test framework and no project file: compiled together with FluxGPU.cs by
// "build.cmd test" using the same in-box C# 5 compiler as build.cmd. Exit code 0 = all passed.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace GpuMonitor.Tests
{
    static class TestMain
    {
        static int passed, failed;
        static string current;

        static void Check(bool ok, string what)
        {
            if (ok) passed++;
            else { failed++; Console.WriteLine("  FAIL [" + current + "] " + what); }
        }

        static void Eq<T>(T expected, T actual, string what)
        {
            Check(EqualityComparer<T>.Default.Equals(expected, actual), what + " (expected " + Show(expected) + ", got " + Show(actual) + ")");
        }

        static string Show(object o) { return o == null ? "null" : Convert.ToString(o, CultureInfo.InvariantCulture); }

        static void Run(string name, Action test)
        {
            current = name;
            int before = failed;
            try { test(); }
            catch (Exception ex) { failed++; Console.WriteLine("  FAIL [" + name + "] threw " + ex.GetType().Name + ": " + ex.Message); }
            Console.WriteLine((failed == before ? "PASS " : "FAIL ") + name);
        }

        static string[] Header()
        {
            return NvidiaSmiCsv.ParseHeader("name, utilization.gpu [%], memory.used [MiB], memory.total [MiB], temperature.gpu, power.draw [W], " +
                "power.limit [W], clocks.sm [MHz], pcie.link.gen.current, pcie.link.width.current, clocks_event_reasons.active, pcie.link.gen.max, driver_model.current");
        }

        static int Main()
        {
            Console.WriteLine("FluxGPU " + AppInfo.Version + " pure-logic tests (" + (IntPtr.Size == 8 ? "x64" : "x86") + ")");

            // ---------------- CSV parser (P0-1, P0-6/H3, P1-5/H1, M2, L7) ----------------
            Run("csv: header normalisation strips units", delegate
            {
                string[] h = Header();
                Eq("utilization.gpu", h[1], "units removed");
                Eq("clocks_event_reasons.active", h[10], "optional field kept");
                Check(NvidiaSmiCsv.IsHeader("name, utilization.gpu [%]"), "header detected");
                Check(!NvidiaSmiCsv.IsHeader("Tesla P40, 37"), "data row is not a header");
            });
            Run("csv: normal row parsed by header name", delegate
            {
                string err;
                NvReading r = NvidiaSmiCsv.ParseRow(Header(), "Tesla P40, 37, 1234, 23040, 41, 51.23, 250.00, 1303, 3, 16, 0x0000000000000004, 3, TCC", out err);
                Check(r != null, "parsed");
                Eq("Tesla P40", r.Name, "name");
                Eq((double?)37, r.Util, "util");
                Eq((double?)23040, r.MemTotalMiB, "mem total");
                Eq((double?)51.23, r.PowerW, "power");
                Eq((double?)250, r.PowerLimitW, "power limit");
                Eq((ulong?)4, r.ThrottleMask, "throttle mask (hex)");
                Eq((double?)3, r.PcieGenMax, "pcie gen max");
                Eq("TCC", r.DriverModel, "driver model");
            });
            Run("csv: columns found by name when order differs", delegate
            {
                string err;
                string[] h = NvidiaSmiCsv.ParseHeader("temperature.gpu, name, utilization.gpu");
                NvReading r = NvidiaSmiCsv.ParseRow(h, "55, Tesla P40, 12", out err);
                Eq((double?)55, r.TempC, "temp");
                Eq((double?)12, r.Util, "util");
            });
            Run("csv: [N/A] / [Not Supported] become null", delegate
            {
                string err;
                NvReading r = NvidiaSmiCsv.ParseRow(Header(), "Tesla P40, [N/A], 1234, 23040, [N/A], [Not Supported], 250.00, 1303, 3, 16, [N/A], [N/A], [N/A]", out err);
                Check(r != null, "row still parsed");
                Eq((double?)null, r.Util, "util null");
                Eq((double?)null, r.TempC, "temp null");
                Eq((double?)null, r.PowerW, "power null");
                Eq((ulong?)null, r.ThrottleMask, "mask null");
                Eq((string)null, r.DriverModel, "driver model null");
            });
            Run("csv: NaN, Infinity, -1 and out-of-range rejected", delegate
            {
                Eq((double?)null, NvidiaSmiCsv.Num("NaN", 0, 100), "NaN");
                Eq((double?)null, NvidiaSmiCsv.Num("Infinity", 0, 100), "Infinity");
                Eq((double?)null, NvidiaSmiCsv.Num("-Infinity", -1000, 1000), "-Infinity");
                Eq((double?)null, NvidiaSmiCsv.Num("-1", 0, 100), "-1 util");
                Eq((double?)null, NvidiaSmiCsv.Num("101", 0, 100), "101 util");
                Eq((double?)null, NvidiaSmiCsv.Num("1,5", 0, 100), "comma decimal");
                Eq((double?)42, NvidiaSmiCsv.Num(" 42 ", 0, 100), "42");
                string err;
                NvReading r = NvidiaSmiCsv.ParseRow(Header(), "Tesla P40, NaN, 1234, 23040, Infinity, 9999, 250, 1303, 3, 16, 0x0, 3, TCC", out err);
                Eq((double?)null, r.Util, "NaN util in a row");
                Eq((double?)null, r.TempC, "Infinity temp in a row");
                Eq((double?)null, r.PowerW, "power above 2x limit");
            });
            Run("csv: hex bitmask parser", delegate
            {
                Eq((ulong?)0x44, NvidiaSmiCsv.Hex("0x0000000000000044"), "0x44");
                Eq((ulong?)null, NvidiaSmiCsv.Hex("[N/A]"), "N/A");
                Eq((ulong?)null, NvidiaSmiCsv.Hex("0x"), "empty");
                Eq((ulong?)null, NvidiaSmiCsv.Hex("0x00000000000000001"), "17 digits");
            });
            Run("csv: comma inside the GPU name does not shift columns", delegate
            {
                string err;
                string[] h = NvidiaSmiCsv.ParseHeader("name, utilization.gpu, temperature.gpu");
                NvReading r = NvidiaSmiCsv.ParseRow(h, "Tesla P40, Rev A, 12, 55", out err);
                Eq("Tesla P40, Rev A", r.Name, "merged name");
                Eq((double?)12, r.Util, "util");
                Eq((double?)55, r.TempC, "temp");
            });
            Run("csv: error lines are not data", delegate
            {
                string err;
                NvReading r = NvidiaSmiCsv.ParseRow(Header(), "Unable to determine the device handle for GPU0000:03:00.0: Unknown Error", out err);
                Check(r == null, "not parsed");
                Check(err.IndexOf("Unknown Error", StringComparison.Ordinal) >= 0, "error text kept");
                r = NvidiaSmiCsv.ParseRow(Header(), "No devices were found", out err);
                Check(r == null, "no devices not parsed");
            });
            Run("csv: temperature report thresholds", delegate
            {
                double? slow, shut;
                NvidiaSmiCsv.ParseTemperatureReport("    GPU Current Temp : 41 C\n    GPU Shutdown Temp : 95 C\n    GPU Slowdown Temp : 92 C\n", out slow, out shut);
                Eq((double?)92, slow, "slowdown");
                Eq((double?)95, shut, "shutdown");
                NvidiaSmiCsv.ParseTemperatureReport("GPU Slowdown Temp : N/A", out slow, out shut);
                Eq((double?)null, slow, "N/A slowdown");
            });

            // ---------------- Errors, backoff, throttle ----------------
            Run("errors: terminal vs transient classification", delegate
            {
                Eq(NvErrorKind.Terminal, NvidiaErrors.Classify("No devices were found"), "no devices");
                Eq(NvErrorKind.Terminal, NvidiaErrors.Classify("Unable to determine the device handle for GPU 0000:03:00.0: GPU is lost.  Reboot the system to recover this GPU"), "gpu lost");
                Eq(NvErrorKind.Terminal, NvidiaErrors.Classify("NVIDIA-SMI has failed because it couldn't communicate with the NVIDIA driver."), "driver comms");
                Eq(NvErrorKind.Transient, NvidiaErrors.Classify("Unable to determine the device handle for GPU0000:03:00.0: Unknown Error"), "unknown error");
                Eq(NvErrorKind.Transient, NvidiaErrors.Classify(""), "empty");
                Check(NvidiaErrors.IsInvalidField("Field \"clocks_event_reasons.active\" is not a valid field to query."), "invalid field");
                Check(!NvidiaErrors.IsInvalidField("0x0000000000000001"), "valid probe output");
            });
            Run("backoff: 3, 6, 12, 24, 48, 60, 60 s and reset", delegate
            {
                Backoff b = new Backoff();
                int[] expect = { 3000, 6000, 12000, 24000, 48000, 60000, 60000 };
                foreach (int e in expect) Eq(e, b.NextTransientMs(), "step");
                b.Reset();
                Eq(3000, b.NextTransientMs(), "after reset");
                Eq(60000, b.TerminalMs(), "terminal");
            });
            Run("throttle: only real slowdowns are badged", delegate
            {
                Eq(0, Throttle.WarningBadges(Throttle.GpuIdle).Count, "idle never badged");
                Eq(0, Throttle.WarningBadges(Throttle.SwPowerCap).Count, "power cap is info");
                Check(Throttle.PowerCapped(Throttle.SwPowerCap), "power cap info flag");
                List<string> b = Throttle.WarningBadges(Throttle.HwThermal | Throttle.HwSlowdown | Throttle.GpuIdle);
                Eq(1, b.Count, "HW thermal implies HW slowdown, one badge");
                Eq("HW THERMAL", b[0], "badge text");
                Eq(2, Throttle.WarningBadges(Throttle.SwThermal | Throttle.HwPowerBrake | Throttle.HwSlowdown).Count, "sw thermal + power brake");
                Eq("HW SLOWDOWN", Throttle.WarningBadges(Throttle.HwSlowdown)[0], "plain hw slowdown");
            });

            // ---------------- Threshold hysteresis (P1-6) ----------------
            Run("thresholds: from slowdown, settings, fallback", delegate
            {
                double w, c; string src;
                Thresholds.Resolve(92, null, null, out w, out c, out src);
                Eq(82.0, w, "warn = slowdown - 10"); Eq(89.0, c, "crit = slowdown - 3");
                Thresholds.Resolve(null, null, null, out w, out c, out src);
                Eq(75.0, w, "fallback warn"); Eq(85.0, c, "fallback crit");
                Thresholds.Resolve(92, 70, 80, out w, out c, out src);
                Eq(70.0, w, "user warn"); Eq(80.0, c, "user crit"); Eq("settings", src, "source");
                Thresholds.Resolve(92, 90, 80, out w, out c, out src);
                Eq(82.0, w, "invalid user values fall back"); Check(src.StartsWith("invalid", StringComparison.Ordinal), "says invalid");
            });
            Run("hysteresis: rises at once, falls only below threshold - 3", delegate
            {
                TempAlarm a = new TempAlarm(82, 89, 3);
                Eq(AlertLevel.None, a.Update(70), "70");
                Eq(AlertLevel.Warn, a.Update(82), "82 warn");
                Eq(AlertLevel.Warn, a.Update(80), "80 still warn (82-3=79)");
                Eq(AlertLevel.Critical, a.Update(89), "89 critical");
                Eq(AlertLevel.Critical, a.Update(87), "87 still critical (89-3=86)");
                Eq(AlertLevel.Critical, a.Update(null), "gap holds level");
                Eq(AlertLevel.Warn, a.Update(85), "85 drops to warn");
                Eq(AlertLevel.Warn, a.Update(79.5), "79.5 still warn");
                Eq(AlertLevel.None, a.Update(79), "79 clears");
                Eq(AlertLevel.Critical, a.Update(95), "jump straight to critical");
                Eq(AlertLevel.None, a.Update(60), "big drop clears");
            });
            Run("telemetry: lost after 30 s without a good sample, then restored", delegate
            {
                TelemetryWatch t = new TelemetryWatch(30000, 0);
                Eq(0, t.Update(29000), "29 s fine");
                Eq(1, t.Update(30001), "30 s lost");
                Eq(0, t.Update(40000), "still lost, no repeat");
                t.Good(41000);
                Eq(-1, t.Update(41000), "restored");
                Check(!t.Lost, "not lost");
            });
            Run("sparkline: gaps split the graph into runs", delegate
            {
                List<double?> v = new List<double?> { 1, 2, null, 3, null, null, 4, 5, 6 };
                List<int[]> runs = Sparkline.Runs(v);
                Eq(3, runs.Count, "three runs");
                Eq(0, runs[0][0], "run 1 start"); Eq(1, runs[0][1], "run 1 end");
                Eq(3, runs[1][0], "run 2 single point");
                Eq(6, runs[2][0], "run 3 start"); Eq(8, runs[2][1], "run 3 end");
                Eq(0, Sparkline.Runs(new List<double?> { null, null }).Count, "all gaps");
            });

            // ---------------- Settings (P0-5, K-8) ----------------
            Run("settings: key=value round trip", delegate
            {
                AppSettings s = new AppSettings { Left = 1500.5, Top = 16, Topmost = false, Compact = true, WarnC = 80, CritC = null, HystC = 2.5, NvidiaSmiPath = null };
                List<string> problems = new List<string>();
                AppSettings back = AppSettings.Parse(s.Serialize(), problems);
                Eq(0, problems.Count, "no problems");
                Eq((double?)1500.5, back.Left, "left"); Eq((double?)16, back.Top, "top");
                Eq(false, back.Topmost, "topmost"); Eq(true, back.Compact, "compact");
                Eq((double?)80, back.WarnC, "warn"); Eq((double?)null, back.CritC, "crit auto");
                Eq(2.5, back.HystC, "hysteresis");
                Eq(true, back.ShowProcesses, "process list on by default");
                s.ShowProcesses = false;
                back = AppSettings.Parse(s.Serialize(), problems);
                Eq(false, back.ShowProcesses, "process list off round trip");
                Eq(0, problems.Count, "still no problems");
                Check(s.Serialize().Contains("show_processes=0"), "show_processes written");
                back = AppSettings.Parse("show_processes=maybe", problems);
                Eq(true, back.ShowProcesses, "bad value keeps the default (on)");
                Eq(1, problems.Count, "bad show_processes reported");
            });
            Run("settings: bad values ignored and reported", delegate
            {
                List<string> problems = new List<string>();
                AppSettings s = AppSettings.Parse("left=abc\ntop=20\ntemp_warn_c=500\ntopmost=maybe\nnvidia_smi_path=nvidia-smi.exe\nbogus=1\nnot a pair", problems);
                Eq((double?)null, s.Left, "bad left");
                Eq((double?)20, s.Top, "good top kept");
                Eq((double?)null, s.WarnC, "out-of-range warn");
                Eq(true, s.Topmost, "bad bool keeps default");
                Eq((string)null, s.NvidiaSmiPath, "relative path rejected");
                Eq(6, problems.Count, "six problems reported");
            });
            Run("settings: legacy settings.txt parsed", delegate
            {
                List<string> problems = new List<string>();
                AppSettings s = AppSettings.ParseLegacy("1520.25|16|0|1", problems);
                Eq((double?)1520.25, s.Left, "left"); Eq((double?)16, s.Top, "top");
                Eq(false, s.Topmost, "topmost"); Eq(true, s.Compact, "compact");
                AppSettings.ParseLegacy("garbage", problems);
                Eq(1, problems.Count, "garbage reported");
            });
            Run("settings: one-time migration and atomic save", delegate
            {
                string dir = Path.Combine(Path.GetTempPath(), "fluxgpu-test-" + Guid.NewGuid().ToString("N"));
                string appDir = Path.Combine(dir, "app"), dataDir = Path.Combine(dir, "data");
                Directory.CreateDirectory(appDir);
                string legacy = Path.Combine(appDir, "settings.txt");
                File.WriteAllText(legacy, "100|200|1|1");
                try
                {
                    List<string> notes = new List<string>();
                    AppSettings s = SettingsStore.Load(dataDir, legacy, notes);
                    Eq((double?)100, s.Left, "migrated left");
                    Check(s.MigratedLegacy, "migration flag");
                    Check(File.Exists(Path.Combine(dataDir, "settings.ini")), "settings.ini written (first save, File.Move path)");
                    Check(File.Exists(legacy), "old settings.txt left in place");
                    Check(!File.Exists(Path.Combine(dataDir, "settings.ini.tmp")), "no temp file left");

                    File.WriteAllText(legacy, "999|999|0|0");   // must not be migrated again
                    s = SettingsStore.Load(dataDir, legacy, notes);
                    Eq((double?)100, s.Left, "not migrated twice");

                    s.Left = 300;
                    Check(SettingsStore.Save(s, dataDir, notes), "second save (File.Replace path)");
                    s = SettingsStore.Load(dataDir, legacy, notes);
                    Eq((double?)300, s.Left, "saved value read back");
                    Check(!File.Exists(Path.Combine(dataDir, "settings.ini.tmp")), "no temp file after replace");

                    File.AppendAllText(Path.Combine(dataDir, "settings.ini"), "left=oops\n");
                    notes.Clear();
                    s = SettingsStore.Load(dataDir, legacy, notes);
                    Check(notes.Count >= 1, "problem file reported");
                    Check(Directory.GetFiles(dataDir, "settings.ini.bad-*").Length == 1, "problem file backed up");
                }
                finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
            });

            // ---------------- Locator, log format, native layouts ----------------
            Run("locator: absolute override only, reports where it looked", delegate
            {
                Check(!NvidiaSmiLocator.IsAbsolute("nvidia-smi.exe"), "bare name rejected");
                Check(!NvidiaSmiLocator.IsAbsolute(@"..\nvidia-smi.exe"), "relative rejected");
                string tmp = Path.GetTempFileName();
                try
                {
                    List<string> looked = new List<string>();
                    Eq(tmp, NvidiaSmiLocator.Find(tmp, looked), "override found");
                    looked.Clear();
                    string missing = Path.Combine(Path.GetTempPath(), "no-such-dir-" + Guid.NewGuid().ToString("N"), "nvidia-smi.exe");
                    string found = NvidiaSmiLocator.Find(missing, looked);
                    Check(looked.Count >= 1 && looked[0] == missing, "looked-in list starts with the override");
                    Check(found == null || found != missing, "missing override not returned");
                }
                finally { File.Delete(tmp); }
            });
            Run("log: timestamp format MM-DD-YY HH:MM AM/PM", delegate
            {
                Eq("11-26-26 05:41 PM", Log.Stamp(new DateTime(2026, 11, 26, 17, 41, 9)), "afternoon");
                Eq("01-02-27 09:05 AM", Log.Stamp(new DateTime(2027, 1, 2, 9, 5, 0)), "morning");
            });
            // ---------------- "Who's using the P40" (P2-1) ----------------
            Run("procs: normal rows, full paths, sorted by memory, top 3", delegate
            {
                string err;
                List<GpuProcess> all = ComputeApps.Parse(
                    "4120, C:\\Users\\Kevin\\llama.cpp\\llama-server.exe, 18432\r\n" +
                    "880, C:\\Program Files\\Ollama\\ollama.exe, 512\n" +
                    "9001, C:\\Python312\\python.exe, 2048\n" +
                    "77, C:\\Tools\\tiny.exe, 12\n", out err);
                Check(all != null, "parsed");
                Eq((string)null, err, "no error");
                Eq(4, all.Count, "four processes");
                Eq("C:\\Program Files\\Ollama\\ollama.exe", all[1].Path, "full path kept");
                ProcessReport r = ComputeApps.BuildReport(all);
                Eq(ProcessListState.Active, r.State, "active");
                Eq(4, r.Total, "total");
                Eq(3, r.Top.Count, "top 3 only");
                Eq(4120, r.Top[0].Pid, "biggest first");
                Eq(9001, r.Top[1].Pid, "second");
                Eq(880, r.Top[2].Pid, "third");
                Eq("llama-server.exe", ComputeApps.DisplayName(r.Top[0]), "file name shown");
                Eq("18.0 GB", ComputeApps.MemoryText(r.Top[0].UsedMiB), "GB above 1 GiB");
                Eq("512 MiB", ComputeApps.MemoryText(r.Top[2].UsedMiB), "MiB below 1 GiB");
                Check(ComputeApps.Tooltip(r.Top[0]).StartsWith("C:\\Users\\Kevin\\llama.cpp\\llama-server.exe\nPID 4120", StringComparison.Ordinal), "tooltip has the full path and PID");
                Check(!r.AnyMemoryUnknown, "all memory known");
            });
            Run("procs: [N/A] memory sorts last and never becomes 0", delegate
            {
                string err;
                List<GpuProcess> all = ComputeApps.Parse("10, C:\\a\\one.exe, [N/A]\n20, C:\\b\\two.exe, 300\n5, C:\\c\\three.exe, [Not Supported]\n", out err);
                Eq((double?)null, all[0].UsedMiB, "[N/A] is null");
                ProcessReport r = ComputeApps.BuildReport(all);
                Eq(20, r.Top[0].Pid, "known memory first");
                Eq(5, r.Top[1].Pid, "unknowns by PID");
                Eq(10, r.Top[2].Pid, "unknowns by PID (2)");
                Eq("n/a", ComputeApps.MemoryText(r.Top[1].UsedMiB), "shown as n/a");
                Check(r.AnyMemoryUnknown, "note shown");
                Eq((double?)null, ComputeApps.Memory("-1"), "negative rejected");
                Eq((double?)null, ComputeApps.Memory("NaN"), "NaN rejected");
                Eq((double?)1234, ComputeApps.Memory("1234 MiB"), "units tolerated");
            });
            Run("procs: no processes is idle, not an error", delegate
            {
                string err;
                foreach (string text in new[] { "", "\r\n", "No running processes found\n" })
                {
                    List<GpuProcess> all = ComputeApps.Parse(text, out err);
                    Check(all != null && all.Count == 0, "empty list for \"" + text.Trim() + "\"");
                    ProcessReport r = ComputeApps.BuildReport(all);
                    Eq(ProcessListState.Idle, r.State, "idle");
                    Eq("No compute processes (idle)", r.Message, "idle text");
                    Eq(0, r.Top.Count, "no rows, no zeros");
                }
            });
            Run("procs: error output is an error, never rows of zeros", delegate
            {
                string err;
                Check(ComputeApps.Parse("Failed to initialize NVML: Insufficient Permissions\n", out err) == null, "NVML error");
                Eq("Failed to initialize NVML: Insufficient Permissions", err, "error text kept");
                Check(ComputeApps.Parse("No devices were found", out err) == null, "no devices");
                Check(ComputeApps.Parse("Field \"process_name\" is not a valid field to query.", out err) == null, "invalid field");
                Check(ComputeApps.Parse("10, C:\\a.exe, 5\nUnable to determine the device handle for GPU0000:03:00.0: Unknown Error", out err) == null, "error after a row");
                Check(ComputeApps.Parse("0, x.exe, 5", out err) == null, "PID 0 rejected");
                Check(ComputeApps.Parse("12, 34", out err) == null, "too few fields");
            });
            Run("procs: access denied, commas in paths, long names, duplicate PIDs", delegate
            {
                string err;
                List<GpuProcess> all = ComputeApps.Parse(
                    "31, [Insufficient Permissions], 1024\n" +
                    "32, C:\\Odd, Folder\\my, app.exe, 64\n" +
                    "33, C:\\AI\\a-really-long-model-server-name-that-keeps-going.exe, 10\n" +
                    "33, C:\\AI\\a-really-long-model-server-name-that-keeps-going.exe, 20\n" +
                    "34, , [N/A]\n", out err);
                Eq(4, all.Count, "duplicate PID merged");
                Eq((string)null, all[0].Path, "insufficient permissions = no path");
                Eq("PID 31 (access denied)", ComputeApps.DisplayName(all[0]), "access denied shown plainly");
                all[0].WindowsName = "secret.exe";
                Eq("secret.exe", ComputeApps.DisplayName(all[0]), "Windows name used when available");
                Check(ComputeApps.Tooltip(all[0]).Contains("name comes from Windows"), "tooltip explains the source");
                Eq("C:\\Odd, Folder\\my, app.exe", all[1].Path, "commas inside the path kept");
                Eq((double?)64, all[1].UsedMiB, "memory is the last field");
                Eq("my, app.exe", ComputeApps.DisplayName(all[1]), "file name after the last backslash");
                Eq((double?)30, all[2].UsedMiB, "contexts of one PID added up");
                string shown = ComputeApps.DisplayName(all[2]);
                Eq(ComputeApps.MaxNameChars, shown.Length, "long name shortened");
                Check(shown.EndsWith("\u2026", StringComparison.Ordinal), "ends with an ellipsis");
                Check(ComputeApps.Tooltip(all[2]).StartsWith("C:\\AI\\a-really-long-model-server-name-that-keeps-going.exe", StringComparison.Ordinal), "full path in the tooltip");
                Eq("PID 34 (access denied)", ComputeApps.DisplayName(all[3]), "blank name");
                Eq("ollama", ComputeApps.FileNameOf("ollama"), "plain name");
                Eq("x.exe", ComputeApps.FileNameOf("\"C:\\q\\x.exe\""), "quoted path");
                Eq("abc", ComputeApps.Ellipsize("abc", 30), "short name unchanged");
            });
            Run("procs: query is separate from the -l 1 stream and has a timeout", delegate
            {
                Check(ComputeApps.Query.Contains("--query-compute-apps=pid,process_name,used_memory"), "fields");
                Check(ComputeApps.Query.Contains("--format=csv,noheader,nounits"), "format");
                Check(!ComputeApps.Query.Contains("-l "), "one-shot, not looping");
                Check(ProcessPoller.TimeoutMs > 0 && ProcessPoller.TimeoutMs <= ProcessPoller.IntervalMs, "timeout no longer than the interval");
                Eq(5000, ProcessPoller.IntervalMs, "every 5 s");
            });
            Run("native: struct sizes match the Windows headers (x64)", delegate
            {
                if (IntPtr.Size != 8) { Console.WriteLine("  (skipped: not a 64-bit process)"); return; }
                Dictionary<string, int> d = AmdSource.StructSizes();
                Eq(20, d["AdapterInfo"], "D3DKMT_ADAPTERINFO");
                Eq(16, d["EnumAdapters"], "D3DKMT_ENUMADAPTERS2");
                Eq(24, d["QueryInfo"], "D3DKMT_QUERYADAPTERINFO");
                Eq(2080, d["RegistryInfo"], "D3DKMT_ADAPTERREGISTRYINFO");
                Eq(24, d["SegmentSize"], "D3DKMT_SEGMENTSIZEINFO");
                Eq(64, d["PerfData"], "D3DKMT_ADAPTER_PERFDATA");
                Eq(4, d["CloseAdapter"], "D3DKMT_CLOSEADAPTER");
                Eq(144, ChildProcesses.ExtendedLimitsSize, "JOBOBJECT_EXTENDED_LIMIT_INFORMATION");
                Eq(32, AlertManager.FlashInfoSize, "FLASHWINFO");
            });

            Console.WriteLine();
            Console.WriteLine(passed + " checks passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }
    }
}
