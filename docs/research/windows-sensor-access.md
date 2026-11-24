# Reading temperatures and fan speeds on Windows 11 without NZXT CAM

Research for [#3](https://github.com/tofupup/pi-pc-monitor/issues/3). Researched 2026-11-24.

**Question.** How can a Windows 11 program (the **Reporter**) read CPU temperature, GPU temperature, NZXT Kraken liquid temperature and pump speed, and fan speeds on the **PC** without NZXT CAM? What privileges, kernel drivers or services does each approach need?

Constraints from the project: the Reporter sends **Stats** to the **Pi** about once a second, starts unattended at boot, and never shows a UAC prompt. The exact hardware comes later from the inventory ticket, so this note covers every Kraken generation.

Versions checked: LibreHardwareMonitor v0.9.6 (2026-02-14) and master at `9e2d677`; PawnIO 2.2.0 (2026-03-15); PawnIO.Modules 0.2.12; liquidctl 1.16.0 (2026-03-03); NVML API version 13 (header in `NVIDIA/go-nvml` at `5e99270`); NVAPI R620 SDK; ADLX v2.0 (2026-09-20).

---

## Short answer

- **CPU temperature has no driver-free route.** It is a model-specific register (Intel) or an SMU value (AMD), and user mode cannot read either. On Windows 11 in 2026 the practical route is **LibreHardwareMonitorLib + the PawnIO kernel driver**. PawnIO replaced WinRing0 in LHM in September 2025, after Defender began flagging WinRing0 as a vulnerable driver. PawnIO is installed once, by an administrator. Only SYSTEM and Administrators can open it, so the Reporter must run elevated or as a service.
- **Motherboard fan headers** (Super I/O chip) need PawnIO too, for the same reason.
- **GPU temperature and GPU fan speed need no extra driver.** The GPU vendor's user-mode API ships with the display driver: NVML or NVAPI for NVIDIA, ADLX (or the older ADL) for AMD. LHM wraps all of them.
- **Kraken liquid temperature, pump speed and (on Z-series and 2023+ models) fan speed come over USB HID.** No kernel driver is needed. liquidctl (Python, GPL-3.0) and LHM (C#, MPL-2.0) both implement the protocol. Only one program should talk to the Kraken at a time.
- **WMI / ACPI thermal zones are not a usable substitute.** `Win32_TemperatureProbe.CurrentReading` is never filled in, `Win32_Fan` reports no live RPM, and thermal-zone `_TMP` values are whatever the board firmware chooses to report.

---

## What each Stat needs at the hardware level

| Stat | Where the value lives | User-mode API? |
| --- | --- | --- |
| CPU package or core temperature | Intel: MSRs. AMD: SMU / PCI config space. LHM reads both through PawnIO modules (`IntelMSR.bin`, `AMDFamily17.bin`, `RyzenSMU.bin`) ([LHM source tree](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/9e2d677cefff5c614ecc38da88fbc447eb478e2a/LibreHardwareMonitorLib/PawnIo)) | No. Needs a kernel driver. |
| Motherboard fan RPM (case and CPU fan headers) | Super I/O / embedded controller over LPC. LHM uses the `LpcIO.bin` and `LpcACPIEC.bin` PawnIO modules (same tree) | No. Needs a kernel driver. |
| GPU core temperature, GPU fan | GPU driver, exposed through vendor APIs (NVML / NVAPI / ADLX) | Yes |
| GPU hot spot / memory junction (NVIDIA, extra channels) | LHM reads some of these through a PawnIO module (`Nvidia.bin`) ([NvidiaGpu.cs](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/9e2d677cefff5c614ecc38da88fbc447eb478e2a/LibreHardwareMonitorLib/Hardware/Gpu/NvidiaGpu.cs#L173-L195)) | Partly |
| Kraken liquid temperature, pump RPM, Kraken fan RPM | The cooler's own USB HID interface ([liquidctl Kraken guide](https://github.com/liquidctl/liquidctl/blob/292dc561361cfbc0cdee155c26662131fe160989/docs/kraken-x3-z3-guide.md)) | Yes (HID) |

OhmGraphite, an LHM-based Windows service, gives a concrete precedent. Its v0.36.0 release notes say that without PawnIO it "will fail to detect: CPU Temperature, CPU wattage, CPU Frequency, All motherboard sensors" ([OhmGraphite v0.36.0](https://github.com/nickbabcock/OhmGraphite/releases/tag/v0.36.0)).

---

## 1. LibreHardwareMonitorLib (LHM)

**What it is.** A .NET library, plus a WinForms app, that reads "temperature sensors, fan speeds, voltages, load and clock speeds". It supports Intel/AMD CPUs, NVIDIA/AMD/Intel GPUs, motherboards, storage and network ([README](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/9e2d677cefff5c614ecc38da88fbc447eb478e2a/README.md)). Licence: **MPL-2.0** ([repo](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)). Latest stable release: **v0.9.6, 2026-02-14**. NuGet prereleases `0.9.7-preNNN` are still being published ([releases](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases), [NuGet](https://www.nuget.org/packages/LibreHardwareMonitorLib/)). Targets: .NET Framework 4.7.2 / 4.8.1, .NET Standard 2.0, .NET 10 and .NET 11 ([README](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/9e2d677cefff5c614ecc38da88fbc447eb478e2a/README.md)).

**What it reads that matters here.**
- CPU temperatures and motherboard fans, **only if PawnIO is installed** (see §2 and the OhmGraphite quote above).
- GPU temperature and fan: NVIDIA through NVAPI (`NvAPI_GPU_GetThermalSettings`, `NvAPI_GPU_GetTachReading`) and NVML, AMD through ADL, plus Intel GPUs ([NvidiaGpu.cs](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/9e2d677cefff5c614ecc38da88fbc447eb478e2a/LibreHardwareMonitorLib/Hardware/Gpu/NvidiaGpu.cs), [Interop folder](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/9e2d677cefff5c614ecc38da88fbc447eb478e2a/LibreHardwareMonitorLib/Interop)).
- **NZXT Kraken over HID** (through HidSharp), enabled by `Computer.IsControllerEnabled`. Supported USB product IDs ([NzxtGroup.cs](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/9e2d677cefff5c614ecc38da88fbc447eb478e2a/LibreHardwareMonitorLib/Hardware/Controller/Nzxt/NzxtGroup.cs#L28-L56)):
  - `0x170E` Kraken X42/X52/X62/X72 (KrakenV2)
  - `0x2007`, `0x2014` Kraken X53/X63/X73 (X3)
  - `0x3008` Kraken Z3
  - `0x300C` Kraken 2023 Elite
  - `0x300E` Kraken 2023
  - `0x3012` Kraken Elite v2 (the 2024 Elite RGB)
  - `0x1711` Grid+ V3
  - The Kraken 2024 Plus (`0x3014`) is **not** in LHM's list, though liquidctl supports it (§3).
- Kraken sensors exposed: "Liquid" temperature, "Pump" RPM and pump duty. On models with a fan controller (Z3, 2023, Elite) it also exposes "Fans" RPM and duty ([KrakenV3.cs](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/9e2d677cefff5c614ecc38da88fbc447eb478e2a/LibreHardwareMonitorLib/Hardware/Controller/Nzxt/KrakenV3.cs#L210-L236), [KrakenV2.cs](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/9e2d677cefff5c614ecc38da88fbc447eb478e2a/LibreHardwareMonitorLib/Hardware/Controller/Nzxt/KrakenV2.cs)).
- By default LHM only reads the Kraken. It writes pump or fan duty only when a control's mode is set to *Software* or *Default*. A fresh settings store starts in `Undefined` mode, which writes nothing ([Control.cs](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/9e2d677cefff5c614ecc38da88fbc447eb478e2a/LibreHardwareMonitorLib/Hardware/Control.cs#L36-L44), [KrakenV3.cs](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/9e2d677cefff5c614ecc38da88fbc447eb478e2a/LibreHardwareMonitorLib/Hardware/Controller/Nzxt/KrakenV3.cs#L141-L165)). One catch: *Default* mode pushes a **fixed 60 % pump / 40 % fan**, so a Reporter must never set it.
- LHM's KrakenV3 asks only for firmware info before it polls status. liquidctl also sends an initialise sequence and says devices "must be initialized" after a cold power-on before monitoring works ([liquidctl guide](https://github.com/liquidctl/liquidctl/blob/292dc561361cfbc0cdee155c26662131fe160989/docs/kraken-x3-z3-guide.md#initialization), [kraken3.py](https://github.com/liquidctl/liquidctl/blob/292dc561361cfbc0cdee155c26662131fe160989/liquidctl/driver/kraken3.py#L218-L250)). **Unverified:** whether LHM gets status from a Kraken that nothing has initialised since a cold power-on.

**Privileges.** The README says "Some sensors require administrator privileges" and recommends a `requireAdministrator` manifest ([README](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/9e2d677cefff5c614ecc38da88fbc447eb478e2a/README.md)). Concretely, the PawnIO device only lets SYSTEM and Administrators open it (§2). *Inference, not tested:* without elevation, the GPU (vendor APIs) and Kraken (HID) sensors should still work, but CPU and motherboard sensors will not.

**Running as a service.** This is proven by OhmGraphite, which embeds LHM and installs itself as a Windows service (`OhmGraphite.exe install`). Its docs note that the service runs as `LocalSystem` ([OhmGraphite README](https://github.com/nickbabcock/OhmGraphite/blob/master/README.md)). The LHM GUI auto-starts differently: it registers a Task Scheduler **logon** trigger with `RunLevel = Highest`, which elevates without a UAC prompt but only once a user logs on, not at boot ([StartupManager.cs](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/9e2d677cefff5c614ecc38da88fbc447eb478e2a/LibreHardwareMonitor.Windows.Forms/UI/StartupManager.cs#L140-L160)).

**Built-in HTTP endpoints (GUI app only).** The WinForms app's optional web server serves `/data.json`, a Prometheus-style `/metrics` (query parameters added in v0.9.6) and `/Sensor` ([HttpServer.cs](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/9e2d677cefff5c614ecc38da88fbc447eb478e2a/LibreHardwareMonitor.Windows.Forms/Utilities/HttpServer.cs#L420-L470), [v0.9.6 notes](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases/tag/v0.9.6)). This is an off-the-shelf "sensor server", but it is the GUI app, so it auto-starts at logon (above), not at boot.

**Language bindings.**
- .NET: native (NuGet `LibreHardwareMonitorLib`).
- Python: `HardwareMonitor` on PyPI (v1.2.1, BSD-3), "Python import layer for the LibreHardwareMonitorLib assembly" ([PyPI](https://pypi.org/project/HardwareMonitor/)). `pythonnet` also works generically.
- Rust: I found no crate on crates.io. Rust would have to host .NET, or consume LHM's HTTP output.

**Licence of the pieces.** LHM is MPL-2.0. The embedded PawnIO modules are LGPL-2.1 ([PawnIO.Modules](https://github.com/namazso/PawnIO.Modules)). PawnIO itself is installed separately (§2).

### WinRing0: what happened, and why it matters

- LHM (like OpenHardwareMonitor, FanCtrl, OpenRGB and others) used to extract and load the **WinRing0** kernel driver.
- WinRing0 has **CVE-2020-14979** (CVSS 7.8): it lets "local users, including low integrity processes, ... read and write to arbitrary memory locations", which gives anyone NT AUTHORITY\SYSTEM ([NVD](https://nvd.nist.gov/vuln/detail/CVE-2020-14979)).
- **Microsoft Defender detects it** as `VulnerableDriver:WinNT/Winring0`. Microsoft's support article says "This detection is valid", names Libre Hardware Monitor among the affected tools, and does not recommend the exclusion workaround ([Microsoft Support](https://support.microsoft.com/en-us/windows/security/threat-malware-protection/microsoft-defender-antivirus-alert-vulnerabledriver-winnt-winring0)). The article carries no date. News coverage places the start of the detections in March 2025 (secondary source: [Slashdot, 2025-03-14](https://it.slashdot.org/story/25/03/14/1351225/windows-defender-now-flags-winring0-driver-as-security-threat-breaking-multiple-pc-monitoring-tools)).
- **Microsoft's vulnerable driver blocklist** contains many WinRing0 hashes. I counted 62 matches for "winring" in `DriverPolicy_Enforced.xml` from the current download (file dated 2026-04-09), e.g. `ID_DENY_WINRING0_SHA256`. **PawnIO is not in it** ([blocklist download](https://aka.ms/VulnerableDriverBlockList), [Microsoft Learn](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/design/microsoft-recommended-driver-block-rules)). On Windows 11 the blocklist "is enabled by default for all devices" since the 2022 update. It is also enforced whenever memory integrity (HVCI), Smart App Control or S mode is on, and it is updated quarterly and through monthly Windows updates (same Microsoft Learn page). So **a Reporter that relied on WinRing0 would be quarantined by Defender and/or blocked from loading.** That rules out older LHM builds (≤ v0.9.4) and any other WinRing0-based tool.
- LHM's switch: "Swap WinRing0 to PawnIO" ([PR #1857](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/pull/1857), merged 2025-09-16, closing [#984](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/issues/984)), then "Use PawnIO driver directly in LibreHardwareMonitorLib" ([PR #1908](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/pull/1908), merged 2025-09-29). First stable release with PawnIO: **v0.9.5 (2026-01-07)**. A maintainer confirms "From 0.9.5 the underlying driver has been changed so Windows Defender should not notify anymore" ([#2075](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/issues/2075)).

---

## 2. PawnIO (the driver LHM now uses)

**What it is.** "A scriptable universal kernel driver." User-mode programs load small **signed** Pawn bytecode modules into it, and each module exposes narrow ioctls such as "read this MSR". User code never gets raw port or memory access ([pawnio.eu](https://pawnio.eu), [PawnIO source](https://github.com/namazso/PawnIO)). Module signatures are checked in-kernel with `BCryptVerifySignature` ([signature.cpp](https://github.com/namazso/PawnIO/blob/9d52965895588b0ffa3703b72eec75ba19f4ccc0/PawnIO/src/signature.cpp#L107-L160)). The official modules are LGPL-2.1, signed by the author, and released at [PawnIO.Modules](https://github.com/namazso/PawnIO.Modules/releases) (latest 0.2.12, 2026-11-17).

**Versions.** 2.0.0 (2025-05-24), 2.0.1 (2025-06-29), 2.1.0 (2026-02-12), **2.2.0 (2026-03-15)** ([PawnIO.Setup releases](https://github.com/namazso/PawnIO.Setup/releases)).

**Signing.** The official edition is "digitally signed". The "Unrestricted" edition is only test-signed and needs test-signing mode ([pawnio.eu](https://pawnio.eu), [wiki](https://github.com/namazso/PawnIO.Modules/wiki/Getting-started-with-PawnIO)). Neither page says which kind of signature. I extracted the 2.2.0 installer myself (SHA-256 `1f519a22…3032`, which matches the [winget manifest](https://github.com/microsoft/winget-pkgs/blob/master/manifests/n/namazso/PawnIO/2.2.0/namazso.PawnIO.installer.yaml)). Its payload contains `PawnIO.cat` and the signer string "Microsoft Windows Hardware Compatibility Publisher", the signer of Microsoft-signed (attestation or WHQL) driver catalogs. I did not determine which of the two.

**Install.**
- `PawnIO_setup.exe -install -silent`. Usage text taken from the binary: `PawnIOSetup.exe [-install] [-uninstall] [-unrestricted] [-debuginfo] [-silent]`.
- Also on winget as `namazso.PawnIO`: machine scope, `ElevationRequirement: elevatesSelf`, success code 3010 meaning a reboot is required ([winget manifest](https://github.com/microsoft/winget-pkgs/blob/master/manifests/n/namazso/PawnIO/2.2.0/namazso.PawnIO.installer.yaml)).
- It installs a kernel service with `StartType = 3` (demand start), and the device ACL is `D:P(A;;GA;;;SY)(A;;GA;;;BA)`, i.e. **SYSTEM and Administrators only** ([PawnIO.inf.in](https://github.com/namazso/PawnIO/blob/9d52965895588b0ffa3703b72eec75ba19f4ccc0/PawnIO/PawnIO.inf.in#L36-L50)).
- 2.2.0 added: "It is now possible to expose the device to non-administrators, although not recommended" ([2.2.0 notes](https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0)). I did not find how to do this.
- LHM's library does **not** install PawnIO. It detects it through `HKLM\...\Uninstall\PawnIO` ([PawnIo.cs](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/9e2d677cefff5c614ecc38da88fbc447eb478e2a/LibreHardwareMonitorLib/PawnIo/PawnIo.cs#L22-L42)). The GUI app bundles `PawnIO_setup.exe`.
- PawnIO's author asks that it be installed globally rather than bundled and removed per app: "The kernel and the drivers in there are inherently global resources and never 'portable'" ([LHM #2140](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/issues/2140)).

**Licence.**
- Official and unrestricted binaries: "Proprietary, however redistribution of installer is allowed".
- Source code: GPL-2 with an exception for programs that talk to it only via the device IO control interface. PawnIOLib is LGPL-2.1 ([wiki](https://github.com/namazso/PawnIO.Modules/wiki/Using-PawnIO-Modules), [README](https://github.com/namazso/PawnIO/blob/9d52965895588b0ffa3703b72eec75ba19f4ccc0/README.md)).

**Anti-cheat.**
- FACEIT blocked PawnIO in 2025 because its signing certificate "is also used by cheat software" ([PawnIO.Setup #1](https://github.com/namazso/PawnIO.Setup/issues/1), opened 2025-09-19).
- The author closed the issue on 2026-02-12 with "This should be fixed as of 2.1.0".
- I could not load FACEIT's own blocked-driver page to confirm the current status (HTTP 403).
- I found no primary source saying Riot Vanguard or other anti-cheats block PawnIO. Unverified either way.

**Defender / HVCI.** PawnIO is not in Microsoft's blocklist (above), and LHM maintainers report that Defender no longer flags LHM from v0.9.5. **Unverified:** whether PawnIO loads with memory integrity (HVCI) on. I found no primary statement.

---

## 3. liquidctl (for the Kraken)

**What it is.** A Python CLI and library for AIO coolers and fan or RGB controllers. Licence **GPL-3.0-or-later**, latest **1.16.0 (2026-03-03)**, requires Python ≥ 3.10 ([repo](https://github.com/liquidctl/liquidctl), [PyPI](https://pypi.org/project/liquidctl/)).

**Kraken coverage** (from the supported-devices table, [README](https://github.com/liquidctl/liquidctl/blob/292dc561361cfbc0cdee155c26662131fe160989/README.md)):

| Kraken | liquidctl doc | Windows notes |
| --- | --- | --- |
| X31/X41/X61, X40/X60 (Asetek 690LC) | asetek-690lc-guide | **Z**: needs the device driver replaced with WinUSB via Zadig. Also needs `--legacy-690lc` |
| M22, X42/X52/X62/X72 | kraken-x2-m2-guide | HID |
| X53/X63/X73, Z53/Z63/Z73 | kraken-x3-z3-guide | HID |
| 2023 Standard/Elite, 2024 Elite RGB | kraken-x3-z3-guide | HID, "partially supported" (LCD / light-ring features) |
| 2024 Plus | kraken-x3-z3-guide | HID, new in 1.16.0 |

The driver's match table labels PID `0x300C` "NZXT Kraken 2023 Elite (broken)". The table does not say what is broken ([kraken3.py](https://github.com/liquidctl/liquidctl/blob/292dc561361cfbc0cdee155c26662131fe160989/liquidctl/driver/kraken3.py#L570-L615)).

**What it reads.** `liquidctl status` on X3-class coolers reports "Liquid temperature", "Pump speed" and "Pump duty". Z-series and 2023+ coolers add fan speed and duty ([guide](https://github.com/liquidctl/liquidctl/blob/292dc561361cfbc0cdee155c26662131fe160989/docs/kraken-x3-z3-guide.md#monitoring), [kraken3.py](https://github.com/liquidctl/liquidctl/blob/292dc561361cfbc0cdee155c26662131fe160989/liquidctl/driver/kraken3.py#L48-L52)). X3 models have no fan controller, so their radiator fans sit on the motherboard and need PawnIO to read (§1).

**Important behaviours.**
- "Monitoring and/or configuring the coolers is not possible with CAM running". In practice the Kraken expects **one** controlling program ([guide](https://github.com/liquidctl/liquidctl/blob/292dc561361cfbc0cdee155c26662131fe160989/docs/kraken-x3-z3-guide.md)). Running liquidctl and LHM's Kraken polling side by side is untested and likely to interleave HID reports.
- "Devices must be initialized" after power-on from mechanical off. Fan and pump settings "persist as long as the device still gets power, even if the system has gone to Soft Off (S5)" (same guide). Without CAM, *something* has to set the pump and fan curves. That is a cooling concern beyond the Dashboard, but it affects which program owns the Kraken.
- The Kraken can also send pump RPM to a motherboard fan header through the sense pin (same guide). If it is wired that way, pump RPM also appears among the motherboard sensors (which need PawnIO).

**Privileges and drivers on Windows.**
- HID Krakens use the in-box Microsoft HID driver: no extra kernel driver, and libusb is bundled through `libusb-package` since 1.9.0 ([README, Windows dependencies](https://github.com/liquidctl/liquidctl/blob/292dc561361cfbc0cdee155c26662131fe160989/README.md#windows-system-level-dependencies)).
- Only the old Asetek-based models need a WinUSB driver swap.
- **Unverified:** that HID access needs no administrator rights. Microsoft's HID docs describe plain `CreateFile`/`ReadFile`/`WriteFile` access from user mode but do not state the privilege requirement ([Opening HID collections](https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/opening-hid-collections)).

**Running at boot.** liquidctl's own Windows guidance is a batch file run by a Task Scheduler task at logon, or the Startup folder ([README](https://github.com/liquidctl/liquidctl/blob/292dc561361cfbc0cdee155c26662131fe160989/README.md#set-up-windows-using-task-scheduler)). It ships no Windows service. A Reporter would use liquidctl as a library (`liquidctl.find_liquidctl_devices()`; see the README section "Using liquidctl in other programs and scripts") inside its own process.

**Bindings.** Python only. Other languages would reimplement the HID protocol: liquidctl's drivers are small and readable, and the X3/Z3/2023 status packet is decoded in about 20 lines in both [kraken3.py](https://github.com/liquidctl/liquidctl/blob/292dc561361cfbc0cdee155c26662131fe160989/liquidctl/driver/kraken3.py) and [KrakenV3.cs](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/9e2d677cefff5c614ecc38da88fbc447eb478e2a/LibreHardwareMonitorLib/Hardware/Controller/Nzxt/KrakenV3.cs#L203-L236). I found no maintained Rust crate.

**Defender / anti-cheat.** Nothing found: no kernel driver is involved.

---

## 4. NVIDIA: NVML and NVAPI

### NVML
- **Reads.** `nvmlDeviceGetTemperatureV`, "For all products"; the older `nvmlDeviceGetTemperature` is deprecated. `nvmlDeviceGetFanSpeed_v2` returns a percentage, "For all discrete products with dedicated fans". `nvmlDeviceGetFanSpeedRPM` is "For Maxwell or newer **fully supported** devices" ([nvml.h](https://github.com/NVIDIA/go-nvml/blob/5e992702f9f516e8db7a0c34d3e462a87f48339d/gen/nvml/nvml.h#L6340-L6520)). NVML grants "Full Support" to Tesla/Quadro and "Selected GeForce Titan" products, and only "Limited Support" to "All Geforce products" ([nvml.h header comment](https://github.com/NVIDIA/go-nvml/blob/5e992702f9f516e8db7a0c34d3e462a87f48339d/gen/nvml/nvml.h#L38-L62)). So on a GeForce card, expect temperature and fan **percent**. **Fan RPM may return NOT_SUPPORTED** (unverified on a specific card).
- **Where it lives.** `nvml.dll` ships with the display driver. The header still says `%ProgramW6432%\NVIDIA Corporation\NVSMI\`; LHM tries the plain DLL name first (System32), then that path ([NvidiaML.cs](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/9e2d677cefff5c614ecc38da88fbc447eb478e2a/LibreHardwareMonitorLib/Interop/NvidiaML.cs#L170-L190)).
- **Privileges.** No extra driver. The read queries above carry no admin note in the header. **Unverified:** behaviour from a session-0 service.
- **Bindings.** C. Python: `nvidia-ml-py` (NVIDIA, BSD, 13.615.71) ([PyPI](https://pypi.org/project/nvidia-ml-py/)). Rust: `nvml-wrapper` 0.13.0 (MIT OR Apache-2.0) ([crates.io](https://crates.io/crates/nvml-wrapper)). .NET: P/Invoke (LHM does this).
- **Licence.** The DLL is part of the driver, and the header carries NVIDIA's licence notice.

### NVAPI
- **Reads.** `NvAPI_GPU_GetThermalSettings` (temperatures) and `NvAPI_GPU_GetTachReading` ("fan speed tachometer reading", "SUPPORTED OS: Windows 10 and higher") are in the public SDK ([nvapi.h](https://github.com/NVIDIA/nvapi/blob/51e5aae4bc40c3ccd3fb7d4dfd94256e7bfc496e/nvapi.h#L3352-L3364)). These are what LHM uses for GeForce temperature and fan RPM.
- **Licence.** The SDK's `nvapi*.lib` files are MIT ([License.txt](https://github.com/NVIDIA/nvapi/blob/51e5aae4bc40c3ccd3fb7d4dfd94256e7bfc496e/License.txt)). The runtime `nvapi64.dll` comes with the driver.
- **Platform and bindings.** Windows only. C. No official Python or Rust binding; I did not survey community ones.
- **Privileges.** No extra driver. **Unverified:** that it works from a service in session 0.

## 5. AMD: ADLX (and legacy ADL)

- **ADLX** is AMD's current SDK. AMD's ADL page is archived with "Now superceded by ADLX" and "we strongly encourage developers to start using ADLX for new applications" ([gpuopen.com/adl](https://gpuopen.com/adl/)). ADLX v2.0 was released 2026-09-20 ([releases](https://github.com/GPUOpen-LibrariesAndSDKs/ADLX/releases)).
- **Reads.** GPU metrics include `GPUTemperature`, `GPUHotspotTemperature`, `GPUIntakeTemperature`, `GPUFanSpeed` (RPM), `GPUUsage` and `GPUTotalBoardPower`. System metrics include CPU usage but **no CPU temperature** ([IPerformanceMonitoring.h](https://github.com/GPUOpen-LibrariesAndSDKs/ADLX/blob/32b5a740d42295c5dfe9026b9f52683da0f3af91/SDK/Include/IPerformanceMonitoring.h#L900-L1080)).
- **Requirements.** Windows 10 or later and an "ADLX-compatible AMD GPU driver" ([README](https://github.com/GPUOpen-LibrariesAndSDKs/ADLX/blob/32b5a740d42295c5dfe9026b9f52683da0f3af91/README.md)). No extra kernel driver. Privileges are not stated, so admin and service behaviour are **unverified**.
- **Bindings.** C, C++, C#, Java, Python (pybind11 `adlx.pyd` wheel) (same README). No Rust.
- **Licence.** Proprietary "ADLX SDK License Agreement" (PDF in the repo).
- LHM uses the older ADL interface for AMD GPUs ([AtiAdlxx.cs](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/9e2d677cefff5c614ecc38da88fbc447eb478e2a/LibreHardwareMonitorLib/Interop/AtiAdlxx.cs)).

---

## 6. WMI / ACPI thermal zones

- **`Win32_TemperatureProbe`**: "current implementations of WMI do not populate the **CurrentReading** property" because the data comes from SMBIOS ([Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-temperatureprobe)). Useless.
- **`Win32_Fan`** exposes `DesiredSpeed` (requested RPM) only. Live speed is said to come from an associated `CIM_Tachometer`. `SetSpeed` is "Not implemented" ([Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-fan)). I found no evidence that Windows fills in live fan RPM here on consumer boards.
- **ACPI thermal zones** (`MSAcpi_ThermalZoneTemperature` in `root\WMI`, and the "Thermal Zone Information" performance counters): the temperature is whatever the firmware's `_TMP` method returns for a zone "the system designer" defined. It exists for OS thermal management (passive/active cooling, `_CRT` shutdown), not to report die temperatures ([ACPI-defined devices, thermal zones](https://learn.microsoft.com/en-us/windows-hardware/drivers/bringup/acpi-defined-devices#thermal-zones)).
  - Whether a desktop board exposes a zone at all, and whether it tracks the CPU, is board-specific. Test on the PC.
  - **Unverified:** Microsoft has no reference page I could find for `MSAcpi_ThermalZoneTemperature`. Its privilege requirement (it is widely said to need admin) is unconfirmed.
- **Verdict:** not a reliable source for CPU temperature, and no source at all for Kraken or GPU values.

## 7. Other routes found along the way (not asked for, partly verified)

- **D3DKMT adapter perf data.** `D3DKMTQueryAdapterInfo` with `KMTQAITYPE_ADAPTERPERFDATA` fills `D3DKMT_ADAPTER_PERFDATA`, which includes `Temperature` ("1 = 0.1C") and `FanRPM` ("current rpm of the main fan"). This would be a vendor-neutral, user-mode GPU route ([struct](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/d3dkmthk/ns-d3dkmthk-_d3dkmt_adapter_perfdata), [enum](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/d3dkmthk/ne-d3dkmthk-_kmtqueryadapterinfotype)). The enum page gives no minimum version for these values. **Unverified:** which drivers fill it in and what privileges it needs.
- **AMD Ryzen Master Monitoring SDK.** An AMD-only CPU route: "read-only access to a subset of the functions in" Ryzen Master, v3.0.1.4971, Windows 10/11 x64. AMD says to call `GetCPUParameters` no more than once per second ([AMD](https://www.amd.com/en/developer/ryzen-master-monitoring-sdk.html)). AMD's bulletin AMD-SB-9004 covered a privilege-escalation bug in its install directory permissions ([AMD-SB-9004](https://www.amd.com/en/resources/product-security/bulletin/amd-sb-9004.html)). I did not verify its driver or privilege model.
- **HWiNFO shared memory** and similar commercial tools: not researched.

---

## 8. Running unattended from boot without UAC

- A **Windows service** under **LocalSystem** starts at boot with no logged-on user. Its token "includes the NT AUTHORITY\SYSTEM and BUILTIN\Administrators SIDs" ([LocalSystem account](https://learn.microsoft.com/en-us/windows/win32/services/localsystem-account)). That satisfies PawnIO's `SY`/`BA` ACL without any UAC prompt. Installing the service and PawnIO is a one-time elevated step.
- A **scheduled task** with `TASK_RUNLEVEL_HIGHEST` runs "with the highest privileges", so no prompt appears. The doc adds that RunLevel "will be ignored" if the task is registered for Local System ([Principal.RunLevel](https://learn.microsoft.com/en-us/windows/win32/taskschd/principal-runlevel)). A logon trigger waits for a user login; a boot trigger running as SYSTEM behaves like a service.
- Both LHM-based precedents exist: OhmGraphite as a service, and LHM GUI as a logon task (§1).
- **Unverified for session 0 (service):** NVAPI, NVML and ADLX calls, and HID access to the Kraken. These need a quick test on the PC.

---

## 9. Comparison

| Approach | CPU temp | GPU temp / fan | Kraken liquid / pump / fan | Mobo fans | Kernel driver | Admin needed | Service at boot | Bindings | Licence | Defender / anti-cheat |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **LHM v0.9.5+ with PawnIO** | Yes | Yes (NVAPI/NVML/ADL) | Yes, most PIDs (not 2024 Plus) | Yes | PawnIO (Microsoft-signed catalog; one-time install) | Yes, for PawnIO parts | Yes (OhmGraphite precedent) | .NET; Python via HardwareMonitor / pythonnet | MPL-2.0 (+ PawnIO proprietary, redistributable) | Not on blocklist. FACEIT block said fixed in PawnIO 2.1.0 |
| **LHM without PawnIO** | No | Yes | Yes | No | None | Probably not (inferred) | Yes | same | MPL-2.0 | None known |
| **LHM ≤ v0.9.4 / anything with WinRing0** | Yes | Yes | Yes | Yes | WinRing0 | Yes | — | — | — | **Defender flags it (`VulnerableDriver:WinNT/Winring0`), and it is on the blocklist. Not viable** |
| **liquidctl** | No | No | Yes, all Krakens incl. 2024 Plus (Asetek-era needs WinUSB swap) | No | None (HID) | Not documented; probably not | As a library inside the Reporter's service | Python only | GPL-3.0+ | None known |
| **NVML** | No | Temp yes; fan % yes; RPM maybe not on GeForce | No | No | None (driver DLL) | Not documented | Unverified in session 0 | C, Python, Rust, .NET (P/Invoke) | NVIDIA (driver component) | None known |
| **NVAPI** | No | Temp + tach RPM | No | No | None | Not documented | Unverified in session 0 | C (.NET via P/Invoke) | SDK libs MIT | None known |
| **ADLX** | No | Temp, hot spot, fan RPM | No | No | None | Not documented | Unverified | C, C++, C#, Java, Python | AMD proprietary SDK | None known |
| **WMI / ACPI thermal zone** | Board-dependent, often not the CPU | No | No | No (`Win32_Fan` has no live RPM) | None | Unverified | Yes | Any (WMI) | n/a | None |

---

## 10. Implications for the Reporter's design (input to #8, no decision here)

1. **If the v1 Dashboard shows CPU temperature or motherboard fan speeds, the Reporter needs a kernel driver, and in 2026 that means PawnIO.** That brings a one-time admin install (`-install -silent`, or winget), a possible reboot (exit code 3010), and an elevated or SYSTEM process. Without CPU temperature, GPU and Kraken Stats need no kernel driver at all.
2. **The elevated process should probably be a Windows service under LocalSystem.** That gives start-at-boot, no UAC, and access to the PawnIO device. A logon-triggered elevated task also avoids UAC but waits for a user login. Session-0 behaviour of NVAPI/NVML/ADLX and Kraken HID must be checked on the PC before committing.
3. **LHM covers every Stat in one library**, including GPU through vendor APIs and the Kraken through HID, which strongly favours **.NET** for the Reporter. Python can still use it via `HardwareMonitor`/pythonnet, at the cost of hosting .NET inside Python. Rust has no binding.
4. **For the Kraken, pick exactly one owner.** LHM reads it without writing by default. liquidctl covers more models (2024 Plus, Asetek-era). Without CAM, something must also set pump and fan curves at power-on, and that program will hold the HID device. The Reporter must not fight it.
5. **Pin LHM ≥ v0.9.5** (PawnIO) and never ship or load WinRing0. Expect Defender to quarantine any WinRing0-based build.
6. **GPU vendor matters for the details.** On NVIDIA GeForce, fan RPM through NVML may be unavailable; NVAPI's tach reading (what LHM uses) is the fallback. On AMD, ADLX gives temperature, hot spot and RPM. Wait for the inventory ticket.
7. **Do not plan around WMI thermal zones** for any Stat.

## 11. Open items to verify on the actual PC

- Whether PawnIO loads with memory integrity (HVCI) enabled on this PC.
- Whether LHM's Kraken readings work after a cold boot without liquidctl's initialise sequence, and on the specific Kraken model.
- NVAPI/NVML/ADLX and Kraken HID from a LocalSystem service (session 0).
- Whether Kraken HID access works without admin (relevant only if the Reporter is unelevated).
- NVML `nvmlDeviceGetFanSpeedRPM` on the actual GeForce card, if NVIDIA.
- Whether the motherboard exposes a meaningful ACPI thermal zone (for curiosity only).
