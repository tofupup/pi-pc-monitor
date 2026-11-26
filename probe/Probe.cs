using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;

namespace PcmonProbe;

/// <summary>
/// Reads the must-have Stats once a second the way ADR 0004 and ADR 0005 describe, and logs them
/// with timings. LibreHardwareMonitor has its Kraken support off; the Kraken comes from KrakenReader.
/// Drives are updated every 10th second only.
/// </summary>
static class Probe
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Uptime() =>
        TimeSpan.FromMilliseconds(Environment.TickCount64).ToString(@"d\.hh\:mm\:ss", Inv);

    public static void Run(string mode, CancellationToken ct)
    {
        Log.Line($"=== probe start, mode={mode}, pid={Environment.ProcessId}, uptime={Uptime()} ===");
        LogEnvironment();

        var computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true,
            IsControllerEnabled = false, // never let LibreHardwareMonitor open the Kraken (ADR 0004)
        };
        var sw = Stopwatch.StartNew();
        computer.Open();
        Log.Line($"lhm: Open took {sw.ElapsedMilliseconds} ms; hardware: " +
                 string.Join(", ", computer.Hardware.Select(h => $"{h.HardwareType}:{h.Name}")));

        sw.Restart();
        UpdateAll(computer, includeDrives: true, out _, out _);
        Log.Line($"lhm: first full update took {sw.ElapsedMilliseconds} ms");
        try
        {
            // Contains serial numbers: read it, don't attach it anywhere as is.
            var reportPath = Path.Combine(Log.Dir, $"lhm-report-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(reportPath, computer.GetReport());
            Log.Line($"lhm: full report written to {reportPath}");
        }
        catch (Exception ex) { Log.Line($"lhm: report failed: {ex.Message}"); }
        DumpSensors(computer);

        using var kraken = new KrakenReader();
        var startedAt = Stopwatch.StartNew();
        bool gotFreshKraken = false;
        int rawFramesLogged = 0;
        var minute = new MinuteStats();
        long tick = 0;
        var next = DateTime.UtcNow;
        var lastTick = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            double gap = (now - lastTick).TotalSeconds;
            if (tick > 0 && gap > 5)
                Log.Line($"GAP {gap:0} s since the last reading (slept or hibernated without a notice?) uptime={Uptime()} FanControl={FanControlProcesses()}");
            lastTick = now;

            bool drives = tick % 10 == 0;
            var loop = Stopwatch.StartNew();
            UpdateAll(computer, drives, out double lhmMs, out double drivesMs);

            float? cpuT = Find(computer, HardwareType.Cpu, SensorType.Temperature, "Core (Tctl/Tdie)");
            float? cpuL = Find(computer, HardwareType.Cpu, SensorType.Load, "CPU Total");
            float? gpuT = Find(computer, HardwareType.GpuNvidia, SensorType.Temperature, "GPU Core");
            float? gpuL = Find(computer, HardwareType.GpuNvidia, SensorType.Load, "GPU Core");
            float? ram = Find(computer, HardwareType.Memory, SensorType.Data, "Memory Used",
                              hw => !hw.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase));

            var k = kraken.Read(TimeSpan.FromMilliseconds(500));
            double loopMs = loop.Elapsed.TotalMilliseconds;

            if (k is { } fresh)
            {
                if (!gotFreshKraken)
                {
                    gotFreshKraken = true;
                    Log.Line($"kraken: first fresh reading {startedAt.Elapsed.TotalSeconds:0.0} s after start, uptime={Uptime()}, FanControl={FanControlProcesses()}");
                }
                if (rawFramesLogged < 3)
                {
                    rawFramesLogged++;
                    Log.Line($"kraken: raw status frame {Convert.ToHexString(fresh.Raw)}");
                }
            }
            else
            {
                Log.Throttled("kraken-missing", $"kraken: MISSING: {kraken.LastError}");
            }

            string krakenPart = k is { } s
                ? $"liquid={s.Liquid.ToString("0.0", Inv)}C pump={s.Pump}rpm/{s.PumpDuty}% fan={s.Fan}rpm/{s.FanDuty}% " +
                  $"(reply {s.LatencyMs.ToString("0", Inv)}ms, drained {s.Drained}, skipped {s.Skipped})"
                : "liquid=MISSING pump=MISSING fan=MISSING";

            Log.Line($"cpu={F(cpuT, "0.0")}C cpuLoad={F(cpuL, "0")}% gpu={F(gpuT, "0.0")}C gpuLoad={F(gpuL, "0")}% " +
                     $"ram={F(ram, "0.0")}GB | {krakenPart} | lhm={lhmMs.ToString("0", Inv)}ms" +
                     (drives ? $" drives={drivesMs.ToString("0", Inv)}ms" : "") +
                     $" loop={loopMs.ToString("0", Inv)}ms");

            minute.Add(cpuT, cpuL, gpuT, gpuL, ram, k, lhmMs, drives ? drivesMs : null, loopMs);
            tick++;
            if (tick % 60 == 0)
            {
                Log.Line(minute.Summary());
                minute = new MinuteStats();
            }
            if (tick % 600 == 0) DumpSensors(computer);

            next += TimeSpan.FromSeconds(1);
            var delay = next - DateTime.UtcNow;
            if (delay < TimeSpan.Zero)
            {
                next = DateTime.UtcNow;
                delay = TimeSpan.Zero;
            }
            ct.WaitHandle.WaitOne(delay);
        }

        Log.Line($"probe: loop ended, uptime={Uptime()}");
        computer.Close();
    }

    static void UpdateAll(Computer computer, bool includeDrives, out double lhmMs, out double drivesMs)
    {
        lhmMs = 0;
        drivesMs = 0;
        foreach (var hw in computer.Hardware)
        {
            bool isDrive = hw.HardwareType == HardwareType.Storage;
            if (isDrive && !includeDrives) continue;
            var sw = Stopwatch.StartNew();
            try
            {
                hw.Update();
                foreach (var sub in hw.SubHardware) sub.Update();
            }
            catch (Exception ex) { Log.Throttled($"update-{hw.Identifier}", $"lhm: update of {hw.Name} failed: {ex.Message}"); }
            if (isDrive) drivesMs += sw.Elapsed.TotalMilliseconds;
            else lhmMs += sw.Elapsed.TotalMilliseconds;
        }
    }

    static float? Find(Computer computer, HardwareType type, SensorType sensorType, string name,
                       Func<IHardware, bool>? hardwareFilter = null)
    {
        foreach (var hw in computer.Hardware)
        {
            if (hw.HardwareType != type || (hardwareFilter != null && !hardwareFilter(hw))) continue;
            foreach (var s in hw.Sensors)
                if (s.SensorType == sensorType && s.Name == name)
                    return s.Value is float v && !float.IsNaN(v) ? v : null;
        }
        return null;
    }

    static string F(float? value, string format) => value?.ToString(format, Inv) ?? "MISSING";

    /// <summary>Every sensor LibreHardwareMonitor sees, to check the nice-to-have Stats from session 0 too.</summary>
    static void DumpSensors(Computer computer)
    {
        Log.Line("--- sensor dump ---");
        foreach (var hw in computer.Hardware)
            DumpHardware(hw);
        Log.Line("--- end of sensor dump ---");

        static void DumpHardware(IHardware hw)
        {
            foreach (var s in hw.Sensors)
                Log.Line($"sensor {s.Identifier} [{hw.Name}] {s.SensorType} '{s.Name}' = {s.Value?.ToString("0.###", Inv) ?? "null"}");
            foreach (var sub in hw.SubHardware)
                DumpHardware(sub);
        }
    }

    static void LogEnvironment()
    {
        try
        {
            var id = WindowsIdentity.GetCurrent();
            Log.Line($"env: user={id.Name} session={Process.GetCurrentProcess().SessionId} " +
                     $"admin={new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator)} " +
                     $"os={Environment.OSVersion.VersionString}");
        }
        catch (Exception ex) { Log.Line($"env: identity failed: {ex.Message}"); }

        Log.Line($"env: booted at about {DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64):yyyy-MM-dd HH:mm:ss}");
        Log.Line($"env: PawnIO {Reg(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO", "DisplayVersion") ?? "NOT INSTALLED"}");
        Log.Line($"env: Fast Startup (HiberbootEnabled) = {Reg(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled") ?? "unset"}");
        Log.Line($"env: Memory integrity configured = {Reg(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled") ?? "unset"}, running = {MemoryIntegrityRunning()}");
        Log.Line($"env: FanControl = {FanControlProcesses()}");
    }

    static string? Reg(string path, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key?.GetValue(name)?.ToString();
        }
        catch (Exception ex) { return $"? ({ex.Message})"; }
    }

    static string MemoryIntegrityRunning()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\DeviceGuard",
                "SELECT SecurityServicesRunning FROM Win32_DeviceGuard");
            foreach (ManagementObject o in searcher.Get())
            {
                // 2 = hypervisor-enforced code integrity (Memory integrity).
                if (o["SecurityServicesRunning"] is uint[] running) return running.Contains(2u) ? "yes" : "no";
            }
            return "unknown";
        }
        catch (Exception ex) { return $"? ({ex.Message})"; }
    }

    static string FanControlProcesses()
    {
        try
        {
            var names = Process.GetProcesses()
                .Where(p => p.ProcessName.Contains("FanControl", StringComparison.OrdinalIgnoreCase))
                .Select(p => $"{p.ProcessName}(session {p.SessionId})")
                .ToList();
            return names.Count == 0 ? "not running" : "running: " + string.Join(", ", names);
        }
        catch (Exception ex) { return $"? ({ex.Message})"; }
    }

    /// <summary>One line per minute, so an hour's log can be read at a glance.</summary>
    sealed class MinuteStats
    {
        int _ticks, _missingMustHave, _krakenFresh;
        readonly List<double> _lhm = [], _drives = [], _loop = [], _latency = [];
        readonly List<int> _drained = [];
        double _liqMin = double.MaxValue, _liqMax = double.MinValue;

        public void Add(float? cpuT, float? cpuL, float? gpuT, float? gpuL, float? ram, KrakenStatus? k,
                        double lhmMs, double? drivesMs, double loopMs)
        {
            _ticks++;
            if (cpuT is null || cpuL is null || gpuT is null || gpuL is null || ram is null || k is null) _missingMustHave++;
            if (k is { } s)
            {
                _krakenFresh++;
                _latency.Add(s.LatencyMs);
                _drained.Add(s.Drained);
                _liqMin = Math.Min(_liqMin, s.Liquid);
                _liqMax = Math.Max(_liqMax, s.Liquid);
            }
            _lhm.Add(lhmMs);
            if (drivesMs is { } d) _drives.Add(d);
            _loop.Add(loopMs);
        }

        public string Summary() =>
            $"MINUTE readings={_ticks} withAnyMustHaveMissing={_missingMustHave} krakenFresh={_krakenFresh}/{_ticks} " +
            (_krakenFresh > 0
                ? $"liquid {_liqMin.ToString("0.0", Inv)}..{_liqMax.ToString("0.0", Inv)}C reply {Stat(_latency)}ms drained {Stat(_drained.Select(x => (double)x).ToList())} "
                : "") +
            $"lhm {Stat(_lhm)}ms drives {Stat(_drives)}ms loop {Stat(_loop)}ms";

        static string Stat(List<double> xs) => xs.Count == 0
            ? "n/a"
            : $"avg {xs.Average().ToString("0", Inv)}/max {xs.Max().ToString("0", Inv)}";
    }
}
