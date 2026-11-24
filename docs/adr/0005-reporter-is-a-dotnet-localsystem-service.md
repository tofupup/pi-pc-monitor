# The Reporter is a .NET service running as LocalSystem

The Reporter is written in C# on .NET 10 and embeds LibreHardwareMonitorLib with the PawnIO driver. It runs as a Windows service under LocalSystem with Automatic start. The Kiosk is Go, so the project has two languages. This was chosen because LibreHardwareMonitor is the only library that reads every Stat: CPU temperature from the AMD SMU through PawnIO, the NCT6687D-R fan chip, and per-fan GPU speed through NVAPI. Doing that from Go would mean rewriting that code. PawnIO only lets SYSTEM and Administrators open it. A LocalSystem service is the one way of running that has that access, starts at boot before anyone logs in, shows no UAC prompt or window, and is told by Windows when it's stopping, shutting down or suspending, which the `stopping` Report depends on.

## Considered Options

- **Go, to match the Kiosk:** one language, but the AMD SMU, Super I/O and NVAPI readers would have to be rewritten and kept up to date by hand.
- **Python with pythonnet calling LibreHardwareMonitor:** still needs .NET, and adds a second runtime to a program that starts at boot.
- **A scheduled task at boot running as SYSTEM:** has the same access, but Windows doesn't tell it about stop, shutdown or suspend, and it has no automatic restart after a crash.
- **A logon task with highest privileges** (how the LibreHardwareMonitor app does it): waits for someone to log in, which breaks the rule that the Reporter starts when Windows boots.

## Consequences

- The Reporter runs in session 0. Whether NVAPI and the Kraken's HID device can be read from there is an inference from FanControl and OhmGraphite, still to be confirmed on the real PC. If they can't, this decision gets reopened.
- The `pcmon` command-line tool is a separate, non-elevated executable in the same .NET solution. A shared library holds the alert-file rules, and the tool never talks to the Reporter (ADR 0002).
- Both are published self-contained, so the PC needs no separately installed .NET runtime, at a cost of about 70 MB on disk for the Reporter.
