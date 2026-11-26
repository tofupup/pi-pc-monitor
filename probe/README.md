# Sensor probe (throwaway)

For the wayfinder task **Check sensor access from a service on the real PC** ([#18](https://github.com/tofupup/pi-pc-monitor/issues/18)). This branch is never merged.

`pcmon-probe.exe` runs as a LocalSystem service and logs readings to `C:\ProgramData\pcmon-probe\probe.log` once a second:

- the must-have Stats: CPU temperature and load, GPU temperature and load, RAM used, Kraken liquid temperature
- how long each LibreHardwareMonitor update takes (Drives only every 10th second)
- every service notice it gets: `NOTICE STOP` / `PRESHUTDOWN` / `SHUTDOWN` / `POWEREVENT SUSPEND` / `RESUME_*`, and a `GAP` line when time jumps with no notice
- a `MINUTE` summary line, and a full sensor dump at start and every 10 minutes

It uses LibreHardwareMonitorLib 0.9.6 with its Kraken support off. The Kraken comes from its own HidSharp reader, which works as ADR 0004 describes: clear the queue, send the status request (`74 01`), take the first `75 01` reply. It never sends liquidctl's initialization sequence.

Also written to the same folder: `lhm-report-*.txt`, LibreHardwareMonitor's full report. **It contains serial numbers**, so read it but don't attach it anywhere as is.

## Build

On any machine with the .NET 10 SDK (Windows or Linux):

```
dotnet publish probe -c Release -o publish
```

This gives a self-contained `publish/pcmon-probe.exe` of about 70 MB. Copy it, `install.ps1` and `uninstall.ps1` into one folder on the PC.

## Checklist at the PC

Use an elevated PowerShell in that folder, with `Set-ExecutionPolicy -Scope Process Bypass` first. Keep a second window tailing the log:

```
Get-Content C:\ProgramData\pcmon-probe\probe.log -Wait -Tail 20
```

To pull out the notices afterwards:

```
Select-String C:\ProgramData\pcmon-probe\probe.log -Pattern 'NOTICE|GAP|=== probe start|first fresh|MISSING'
```

### 0. Prepare

1. **PawnIO:** `winget list --id namazso.PawnIO -e`. If it isn't listed, run `winget install --id namazso.PawnIO -e` and reboot if asked (exit code 3010). Record the version.
2. **Memory integrity:** open Windows Security → Device security → Core isolation. If Memory integrity is off, turn it on and reboot now, so every later check runs with it on. If Windows refuses because of an incompatible driver, note which one.
3. **Fast Startup:** leave it on. Check under Control Panel → Power Options → Choose what the power buttons do.

### 1. Quick interactive check (1 minute)

Run `.\pcmon-probe.exe --console`, watch a few lines, then press Ctrl+C. If something is `MISSING` here, it's broken whether or not it runs as a service. That separates "doesn't work at all" from "doesn't work in session 0".

### 2. Install the service and check the must-haves

Run `.\install.ps1`. In the log, check:

- `env: user=NT AUTHORITY\SYSTEM session=0`
- `env: PawnIO 2.x`, and `Memory integrity configured = 1, running = yes`
- each one-second line has a number, not `MISSING`, for `cpu`, `cpuLoad`, `gpu`, `gpuLoad`, `ram` and `liquid`
- in the sensor dump, the nice-to-haves are present and non-zero where expected: CPU package power, GPU hot spot, GPU fans (0 at idle is fine), motherboard fans `fan/0` `fan/10` `fan/14`, DIMM temperatures, VRM temperature, and three NVMe temperatures

**If any must-have is `MISSING` only as a service, stop here.** That reopens how the Reporter runs. Bring back the log.

Then **Restart** once and confirm the probe starts by itself: a new `=== probe start` line timestamped before you log in.

### 3. Read times

After a few minutes, read the `MINUTE` lines and note `lhm avg/max`, `drives avg/max` and `loop max`. A one-second Report needs `loop` max well under 1000 ms, without Drives.

### 4. Agreement with FanControl (about an hour, uninterrupted)

Put FanControl's Kraken liquid temperature beside the log tail.

1. Note the pair of readings (FanControl, probe) at the start.
2. Put the GPU under load for about 10 minutes (a game or benchmark) so the liquid warms a few degrees, then let it idle.
3. Note pairs at about 5 and 10 minutes into the load, and again at 30 and 60 minutes.

It passes if the two agree to about 0.1–0.2 °C, or move together within a few seconds, at every pair, with no growing lag. In the `MINUTE` lines, `krakenFresh` should stay at 60/60 and `drained` should stay small without growing.

### 5. Shutdown and sleep notices

Do each of these, then run the `Select-String` command above and keep the lines it prints:

1. Start → **Shut down** (Fast Startup on), wait 30 s, then power on.
2. Start → **Restart**.
3. Start → **Sleep**, then wake it after about a minute.
4. Optional, for comparison: `shutdown /s /t 0`, a full shutdown that bypasses Fast Startup.

There's no expected answer. The point is to see which notices actually arrive.

### 6. Cold start (two full power cuts)

A **power cut** here means: shut down, switch the PSU off or unplug it for 30 s so the Kraken loses standby power, then boot.

**a. Probe first.** Stop FanControl from starting at boot. Either use its own settings, or find its service with `Get-Service *fan*` and run `Set-Service <name> -StartupType Manual`. Then power cut and boot. The log should show `kraken: first fresh reading … FanControl=not running` and steady liquid values, not 0 and not `MISSING`. Without FanControl the Kraken runs its own default curve, which is fine for a few minutes. Then start FanControl and check both keep reading. Restore FanControl's startup.

**b. FanControl first.** Run `sc.exe config pcmon-probe start= demand`, then power cut and boot. Wait until FanControl shows Kraken values, then run `sc.exe start pcmon-probe` and check the first fresh reading. Restore with `sc.exe config pcmon-probe start= auto`.

### 7. Clean up

Run `.\uninstall.ps1`. The logs stay in `C:\ProgramData\pcmon-probe`.

## What to bring back

For each section, pass or fail plus the lines that show it: the `env:` lines, one ordinary one-second line, a few `MINUTE` lines, the FanControl pairs, the `NOTICE`/`GAP`/`=== probe start` lines for each case in section 5, and the `first fresh` lines from section 6. Also say whether `install.ps1` worked unchanged.
