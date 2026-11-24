# Research: booting a Pi 4B to a full-screen Kiosk, and Blanking and waking the Screen

- Ticket: [#4](https://github.com/tofupup/pi-pc-monitor/issues/4), "How can a Pi 4B boot to a full-screen view and blank and wake the Screen?"
- Researched: 2026-11-24
- Scope: Raspberry Pi OS (64-bit, Debian 13 "Trixie" base) as of the **2026-10-06** image (Linux 6.18.50, labwc/wlroots 0.20). Package versions are taken from the Raspberry Pi apt archive on the research date: `labwc 0.20.2-1~bpo13+1`, `cage 0.3.1-1~bpo13+1+rpt2`, `chromium 156.0.8078.108`, `raspi-config 20261026+2`, `rpd-wayland-core 1.35` ([Packages index](http://archive.raspberrypi.com/debian/dists/trixie/main/binary-arm64/Packages.gz)).
- The Screen's model and connection (HDMI or DSI) are not known yet, so both are covered. There is no keyboard, mouse or touch input.

This is research only. Choosing an approach is a separate ticket ([#9](https://github.com/tofupup/pi-pc-monitor/issues/9)).

## TL;DR

1. **Raspberry Pi OS is Wayland-first.** labwc is the desktop compositor on every model, X11 (openbox) is "not recommended", and wayfire support has been removed. Raspberry Pi packages **cage**, a single-app kiosk compositor, in its own archive. All three routes work for a full-screen Kiosk with no input: Desktop with autologin plus labwc autostart, Lite plus cage under systemd, or Lite plus a native DRM app with no compositor.
2. **`vcgencmd display_power` does nothing** under the default KMS driver (blocked on purpose in the firmware). Don't build on it.
3. **Blanking under a compositor is one command.** On labwc, `wlopm --off '*'` or `wlr-randr --output <name> --off`; `--on` wakes it. On labwc both turn the DRM output off, so the HDMI signal stops or the DSI panel is disabled. cage 0.3.1 supports only `wlr-randr` (its `wlopm` support is an open PR).
4. **DSI is the cleaner case.** The panel driver is told when the output is disabled, and the backlight is a standard sysfs device (`/sys/class/backlight/*/bl_power`, `brightness`) that works with or without a compositor.
5. **HDMI is less certain.** Turning the output off stops the signal, but what a cheap 1024x600 HDMI panel does then (backlight off, "No Signal" banner, or a lit black screen) is **not documented by vendors and could not be verified**. It has to be tested on the actual Screen. CEC exists on the Pi 4 but cheap panels' support for it is undocumented.
6. **OS blanking is off by default** on the current image (no `swayidle` line in the default labwc autostart; kernel `consoleblank` defaults to 0). The idle blanker must stay off: with no input devices it would blank after 10 minutes and nothing could wake it. The Kiosk must control Blank itself.
7. **No primary source gives boot-time or memory figures** for Chromium versus native rendering on a Pi 4. These need measuring.

## 1. Current Raspberry Pi OS baseline

| Fact | Source |
|---|---|
| Oct 2024: desktop switched from wayfire to labwc; "Raspberry Pi Desktop now runs Wayland by default across all models"; wayfire no longer updated. | [Raspberry Pi news, Oct 2024](https://www.raspberrypi.com/news/a-new-release-of-raspberry-pi-os/) |
| 2025-10-01: image rebased on Debian Trixie. 2026-09-15: "labwc and wlroots updated to version 0.20", "Support for wayfire removed from Screens, Mouse and Keyboard panels". 2026-10-06: current image, kernel 6.18.50. | [Release notes](https://downloads.raspberrypi.com/raspios_arm64/release_notes.txt) |
| `raspi-config` → *6 Advanced Options → A7 Wayland* offers `W1 X11` ("not recommended") and `W2 Labwc` ("recommended"); "we aren't actively developing X11". | [Docs source: configuration](https://github.com/raspberrypi/documentation/tree/master/documentation/asciidoc/computers/configuration), [raspi-config `do_wayland`](https://github.com/RPi-Distro/raspi-config/blob/trixie/raspi-config) |
| Lite is "command-line-only". It can be turned into a desktop with `sudo apt install rpd-wayland-core` (or `rpd-x-core` for X). | [Trixie announcement, 2 Oct 2025](https://www.raspberrypi.com/news/trixie-the-new-version-of-raspberry-pi-os/), [OS docs](https://www.raspberrypi.com/documentation/computers/os.html) |
| `rpd-wayland-core 1.35` depends on `labwc, wf-panel-pi, kanshi, swayidle, wlopm, swaylock, … wlr-randr, xwayland`. | [Packages index](http://archive.raspberrypi.com/debian/dists/trixie/main/binary-arm64/Packages.gz) |
| The Desktop session starts `/usr/bin/labwc-pi`, which runs `labwc -m` (merge config), so `~/.config/labwc/autostart` is **added to** `/etc/xdg/labwc/autostart` rather than replacing it. | `usr/bin/labwc-pi` and `usr/share/wayland-sessions/rpd-labwc.desktop` inside [rpd-wayland-core_1.35_arm64.deb](http://archive.raspberrypi.com/debian/pool/main/r/rpd-metas/rpd-wayland-core_1.35_arm64.deb); [labwc-config(5)](https://github.com/labwc/labwc/blob/master/docs/labwc-config.5.scd) on `--merge-config` |
| Old firmware display settings in `config.txt` (`hdmi_cvt`, `hdmi_group`, `hdmi_blanking`, …) "don't work with Raspberry Pi OS Bookworm and later". Display modes are now set with KMS, for example `video=HDMI-A-1:1024x600M@60` in `cmdline.txt`. Connector names are `HDMI-A-1` and `HDMI-A-2` for HDMI, and `DSI-1` for DSI or DPI. | [Legacy config.txt](https://www.raspberrypi.com/documentation/computers/legacy_config_txt.html), [Display settings](https://www.raspberrypi.com/documentation/computers/configuration.html#display-settings) |

Note: vendor wikis for cheap panels still say to use `hdmi_cvt 1024 600 60` (e.g. [Waveshare search result](https://www.waveshare.com/wiki/7inch_HDMI_LCD_(C))). Per the legacy-options page above, those lines are ignored on current images. If EDID is wrong, use `video=` or `drm.edid_firmware=HDMI-A-1:edid/<file>.bin` ([configuration docs](https://www.raspberrypi.com/documentation/computers/configuration.html)).

## 2. Booting straight to a full-screen view (no input)

### Option A: Raspberry Pi OS Desktop + autologin + labwc autostart

- Boot to desktop with autologin: `raspi-config` → *1 System Options → S5 Boot → B2 Desktop* and *S6 Auto Login* ([configuration docs](https://www.raspberrypi.com/documentation/computers/configuration.html)).
- Raspberry Pi's own kiosk tutorial starts Chromium from `~/.config/labwc/autostart`, which is "used to automatically run programs when the Raspberry Pi OS desktop has loaded". Its command line is `chromium <urls> --kiosk --noerrdialogs --disable-infobars --no-first-run --enable-features=OverlayScrollbar --start-maximized &` ([Raspberry Pi tutorial](https://www.raspberrypi.com/tutorials/how-to-use-a-raspberry-pi-in-kiosk-mode/), undated, targets Raspberry Pi OS 64-bit with labwc).
- Because the session uses `labwc -m`, the default autostart (pcmanfm-pi desktop, wf-panel-pi taskbar, kanshi) still runs ([rpd-wayland-core deb](http://archive.raspberrypi.com/debian/pool/main/r/rpd-metas/rpd-wayland-core_1.35_arm64.deb)). A kiosk window covers the panel, but those processes still use memory. Removing them needs a custom session or a different config dir (`labwc -C`, [labwc(1)](https://github.com/labwc/labwc/blob/master/docs/labwc.1.scd)). This is an inference from the files, not a documented Raspberry Pi kiosk recipe.
- The labwc **Wayland protocols** that matter here: `wlr-output-power-management` and `wlr-output-management` (so both `wlopm` and `wlr-randr` work), idle-notify and idle-inhibit ([labwc `src/server.c`](https://github.com/labwc/labwc/blob/master/src/server.c), [`src/output.c`](https://github.com/labwc/labwc/blob/master/src/output.c)).
- Pros: this is the path Raspberry Pi supports and tests, and Control Centre still works for fixing the display. Cons: the most packages and processes.

### Option B: Raspberry Pi OS Lite + cage

- cage "runs a single, maximized application … User interaction and activities outside the scope of the running application are prevented" ([cage(1)](https://github.com/cage-kiosk/cage/blob/master/cage.1.scd)). Run from a TTY, it uses the KMS/DRM backend ([cage README](https://github.com/cage-kiosk/cage/blob/master/README.md)).
- Versions: Debian Trixie has cage 0.2.0. The Raspberry Pi archive ships **0.3.1** built against wlroots 0.20 ([Debian sources](https://sources.debian.org/src/cage/), [RPi Packages index](http://archive.raspberrypi.com/debian/dists/trixie/main/binary-arm64/Packages.gz)).
- Boot integration: the cage wiki gives a `cage@.service` systemd template with its own PAM stack. It replaces the getty and is enabled with `systemctl enable cage@tty1.service` and `systemctl set-default graphical.target` ([cage wiki: Starting Cage on boot with systemd](https://github.com/cage-kiosk/cage/wiki/Starting-Cage-on-boot-with-systemd)). `Restart=always` restarts the Kiosk if it crashes.
- Any Wayland client can be the app (Chromium with `--ozone-platform=wayland`, a Qt app on the `wayland` platform, an SDL app on the `wayland` driver).
- Protocols: cage has `wlr-output-management` (so `wlr-randr` works), idle-notify and idle-inhibit, but **not** `wlr-output-power-management` (so `wlopm` does not work) ([cage.c](https://github.com/cage-kiosk/cage/blob/master/cage.c), master at f551ca2, 2026-11-01). Adding it is open PR [#529](https://github.com/cage-kiosk/cage/pull/529) (Aug 2026), tracked in issue [#245](https://github.com/cage-kiosk/cage/issues/245).
- Pros: a minimal stack with one app and no desktop. Cons: you write the systemd/PAM glue yourself, and Blank must use `wlr-randr` (see §4.1 for the catch).

### Option C: Lite + native app straight on DRM/KMS (no compositor)

- **Qt 6** `eglfs` (KMS backend): "EGLFS forces the first top-level window" to be full-screen, with no windowing system. `linuxfb` can also render through DRM dumb buffers (`QT_QPA_FB_DRM=1`). The KMS JSON config (`QT_QPA_EGLFS_KMS_CONFIG`) accepts per-output `mode` values including `off` ([Qt 6.12: Qt for Embedded Linux](https://doc.qt.io/qt-6/embedded-linux.html)).
- **SDL3 / pygame**: select the `kmsdrm` driver with `SDL_VIDEO_DRIVER` ([SDL_HINT_VIDEO_DRIVER](https://wiki.libsdl.org/SDL3/SDL_HINT_VIDEO_DRIVER), [SDL_hints.h](https://github.com/libsdl-org/SDL/blob/main/include/SDL3/SDL_hints.h)).
- Start it from a systemd service on a TTY (same idea as the cage unit, without cage).
- Catch: the app becomes DRM master, so outside tools (`wlr-randr`, `wlopm`) have nothing to talk to. HDMI Blank then has to be done by the app through libdrm (the CRTC `ACTIVE` property, below). Neither Qt's docs nor SDL's `kmsdrm` source expose a runtime display-power call. The Qt docs never mention DPMS, and SDL's `kmsdrm` driver sets CRTC `active` only internally ([SDL_kmsdrmvideo.c](https://github.com/libsdl-org/SDL/blob/main/src/video/kmsdrm/SDL_kmsdrmvideo.c)). DSI Blank via backlight sysfs still works (§4.2).

### Option D: X11 (openbox) + Chromium

- Still installable (`rpd-x-core`), but "not recommended" and not actively developed ([configuration docs](https://www.raspberrypi.com/documentation/computers/configuration.html)). The Raspberry Pi engineer's suggested way to blank under X is `xset dpms force off` / `on` ([raspberrypi/firmware#1740](https://github.com/raspberrypi/firmware/issues/1740)). Listed for completeness only.

### Browser kiosk versus native rendering: what's verifiable

- Chromium switches confirmed in current Chromium source (main at 5bf36fe, 2026-11-24): `--kiosk` ("Enable kiosk mode. Please note this is not Chrome OS kiosk mode."), `--start-maximized`, `--no-first-run`, `--no-default-browser-check`, `--incognito`, `--app=` ([chrome_switches.h](https://github.com/chromium/chromium/blob/main/chrome/common/chrome_switches.h)); `--noerrdialogs` ("Suppresses all error dialogs") ([base_switches.h](https://github.com/chromium/chromium/blob/main/base/base_switches.h)); `--ozone-platform` ([ozone_switches.cc](https://github.com/chromium/chromium/blob/main/ui/ozone/public/ozone_switches.cc)). `--disable-infobars`, used in the Raspberry Pi tutorial, is **not** defined in `chrome_switches.h` any more, so it is probably ignored. I didn't search the whole Chromium tree.
- **Boot time and memory:** I found no primary source (Raspberry Pi, Chromium, Qt or SDL) that publishes boot-to-first-frame time or RAM use for these stacks on a Pi 4B. Any number would be a guess. A spike should measure each option on the target Pi with `systemd-analyze`, `free`/`smem`, and time-to-first-Dashboard. Also unknown: **how much RAM the project's Pi 4B has** (the 4B was sold with several RAM sizes).
- One related fact: zram swap is on by default in the Trixie image (staff reply under the [Trixie announcement](https://www.raspberrypi.com/news/trixie-the-new-version-of-raspberry-pi-os/)).

## 3. Stopping the OS from blanking on its own schedule

| Layer | Default on current image | How to make sure it is off | Source |
|---|---|---|---|
| labwc desktop (Option A) | **Off.** Raspberry Pi enables blanking by appending `swayidle -w timeout 600 'wlopm --off \*' resume 'wlopm --on \*' &` to `~/.config/labwc/autostart` and `/etc/xdg/labwc-greeter/autostart`. The shipped `/etc/xdg/labwc/autostart` has no `swayidle` line. | `sudo raspi-config nonint do_blanking 1` (removes the line), or Control Centre → Display → Screen Blanking off | [raspi-config `do_blanking` (trixie)](https://github.com/RPi-Distro/raspi-config/blob/trixie/raspi-config), [rpd-wayland-core deb](http://archive.raspberrypi.com/debian/pool/main/r/rpd-metas/rpd-wayland-core_1.35_arm64.deb), [docs: screen blanking](https://www.raspberrypi.com/documentation/computers/configuration.html#screen-blanking) |
| cage (Option B) | No idle blanking of its own. It only offers the idle-notify protocol to clients like swayidle. | Don't run swayidle. | [cage.c](https://github.com/cage-kiosk/cage/blob/master/cage.c), [cage#245](https://github.com/cage-kiosk/cage/issues/245) |
| Kernel text console (Option C and boot) | `consoleblank` "Defaults to 0" (disabled) upstream. | Add `consoleblank=0` to `/boot/firmware/cmdline.txt` to be explicit. Check with `cat /sys/module/kernel/parameters/consoleblank`. | [kernel-parameters.txt](https://github.com/torvalds/linux/blob/master/Documentation/admin-guide/kernel-parameters.txt), [docs: screen blanking](https://www.raspberrypi.com/documentation/computers/configuration.html#screen-blanking) |
| X11 (Option D) | raspi-config disables blanking by installing `/etc/X11/xorg.conf.d/10-blanking.conf` | `raspi-config nonint do_blanking 1` | [raspi-config](https://github.com/RPi-Distro/raspi-config/blob/trixie/raspi-config) |
| Qt linuxfb | Switching to graphics mode "disables the blinking cursor and screen blanking" | Leave `QT_QPA_PRESERVE_CONSOLE_STATE` unset | [Qt for Embedded Linux](https://doc.qt.io/qt-6/embedded-linux.html) |
| SDL | `SDL_VIDEO_ALLOW_SCREENSAVER` defaults to "0: Disable screensaver" | Nothing to do | [SDL wiki](https://wiki.libsdl.org/SDL3/SDL_HINT_VIDEO_ALLOW_SCREENSAVER) |

**Why idle-based blanking must stay off:** `swayidle` runs its timeout command "if there is no activity for <timeout> seconds" and the resume command "when there is activity again" ([swayidle(1)](https://github.com/swaywm/swayidle/blob/master/swayidle.1.scd)). With no input devices there is never any activity. The Screen would blank 10 minutes after boot whether or not Stats are arriving, and only an outside command could wake it. Blank has to be driven by the Kiosk from Stats and Critical Alert state.

Also: SDL disables the screensaver by default, and on Wayland it binds the idle-inhibit protocol ([SDL_waylandvideo.c](https://github.com/libsdl-org/SDL/blob/main/src/video/wayland/SDL_waylandvideo.c)). That would stop swayidle anyway. This doesn't matter if swayidle isn't used, but it is a trap if someone later tries to reuse swayidle for Blank.

## 4. Blanking and waking the Screen

### 4.0 `vcgencmd display_power`: dead end

Raspberry Pi engineers state: "vcgencmd issues commands to the firmware … [with] the vc4-kms-v3d driver … all displays are controlled by the Linux kernel, and the firmware has no involvement. `vcgencmd display_power` will therefore do nothing". It was blocked on purpose by firmware commit "platform: Block "vcgencmd display_power" if vc4-kms-v3d is enabled" ([raspberrypi/firmware#1740](https://github.com/raspberrypi/firmware/issues/1740), 2022). The current docs no longer list it among the `vcgencmd` commands ([OS docs](https://www.raspberrypi.com/documentation/computers/os.html)). Its replacement, `tvservice`, belongs to the same legacy firmware stack.

### 4.1 Under a Wayland compositor (HDMI or DSI)

- **Documented by Raspberry Pi:** `WAYLAND_DISPLAY=wayland-1 wlr-randr --output HDMI-A-1 --off` and `… --on` ([docs: turn a display on or off in Wayland](https://www.raspberrypi.com/documentation/computers/configuration.html#wayland-display-turn-on-off)). Raspberry Pi's own blanker uses `wlopm --off \*` / `wlopm --on \*` ([raspi-config](https://github.com/RPi-Distro/raspi-config/blob/trixie/raspi-config)). Waveshare documents the same `wlr-randr --output DSI-x --off/--on` for its 1024x600 DSI panel ([Waveshare 7inch DSI LCD (C)](https://www.waveshare.com/wiki/7inch_DSI_LCD_(C))).
- **The two tools behave differently.** In the wlroots maintainer's words, "disabling an output via wlr-output-management [wlr-randr] removes it from the output layout, making it as-if it never existed from the PoV of regular Wayland clients. OTOH, powering off an output via wlr-output-power-management [wlopm] keeps the output in the output layout, and doesn't evict any window from it nor re-arrange surfaces" ([cage#245](https://github.com/cage-kiosk/cage/issues/245)). The protocol says it exists "to allow special clients like desktop shells to power down outputs when the system is idle" ([wlr-output-power-management-unstable-v1.xml](https://gitlab.freedesktop.org/wlroots/wlr-protocols/-/blob/master/unstable/wlr-output-power-management-unstable-v1.xml)).
  - On **labwc**, `wlopm --off` disables the wlroots output (`wlr_output_state_set_enabled(false)`) without touching the layout ([labwc `handle_output_power_manager_set_mode`](https://github.com/labwc/labwc/blob/master/src/output.c)). At the hardware level it is the same "output off" as `wlr-randr`, but the Kiosk window keeps its place.
  - On **cage** only `wlr-randr --off` exists. It disables the output and removes it from the layout (`output_disable` → `output_layout_remove`, [cage output.c](https://github.com/cage-kiosk/cage/blob/master/output.c)). How a single full-screen client behaves when its only output disappears and comes back was **not verified**. It needs testing, or PR [#529](https://github.com/cage-kiosk/cage/pull/529) once merged.
- **What "output off" means in the kernel:** for atomic DRM drivers such as vc4, the old DPMS property "remaps to controlling the "ACTIVE" property on the CRTC". With `ACTIVE`=0 "the CRTC will be powered off" ([drm_connector.c](https://github.com/torvalds/linux/blob/master/drivers/gpu/drm/drm_connector.c), [drm_crtc.c](https://github.com/torvalds/linux/blob/master/drivers/gpu/drm/drm_crtc.c)). On HDMI that ends the video signal. On DSI the panel driver's disable hook runs (§4.2).
- **Hotplug gotcha (HDMI):** Raspberry Pi docs: "If your monitor immediately wakes back up after blanking (displaying "NO SIGNAL" and then returning), append `vc4.force_hotplug=1`" to `cmdline.txt` ([docs: screen blanking](https://www.raspberrypi.com/documentation/computers/configuration.html#screen-blanking)). Some displays drop hot-plug-detect when the signal stops, and the compositor then sees an unplug and replug.
- **Calling it from the Kiosk:** a process outside the compositor needs `WAYLAND_DISPLAY` (and `XDG_RUNTIME_DIR`) for the session ([docs](https://www.raspberrypi.com/documentation/computers/configuration.html#wayland-display-turn-on-off)). A Kiosk running inside the session can run `wlopm`/`wlr-randr`, or speak the protocol itself.

### 4.2 DSI: backlight sysfs, and the panel driver

- Standard kernel ABI, `/sys/class/backlight/<name>/`: `bl_power` (0 = on, 4 = power off), `brightness` (0…`max_brightness`), `actual_brightness` ([sysfs-class-backlight ABI](https://github.com/torvalds/linux/blob/master/Documentation/ABI/stable/sysfs-class-backlight)). Raspberry Pi OS added "udev rule to allow backlight change" (raspberrypi-sys-mods, 2024 release, [release notes](https://downloads.raspberrypi.com/raspios_arm64/release_notes.txt)). It works with **any** of Options A–C, even when a native app holds DRM master.
- When an output is turned off, the DRM panel core calls `drm_panel_disable`, which "will typically turn off the panel's backlight". If the panel has a registered backlight, the core calls `backlight_disable` and later `backlight_enable` ([drm_panel.c](https://github.com/torvalds/linux/blob/master/drivers/gpu/drm/drm_panel.c)).
- **Example 1024x600 DSI panel (Waveshare 7" C):** in-kernel overlay `dtoverlay=vc4-kms-dsi-waveshare-panel,7_0_inchC` ("7.0" C 1024x600") ([overlays README, rpi-6.18.y](https://github.com/raspberrypi/linux/blob/rpi-6.18.y/arch/arm/boot/dts/overlays/README)). Its driver registers a `BACKLIGHT_RAW` device (0–255) and attaches it to the DRM panel. On panel disable it writes an I2C "off" register ([panel-waveshare-dsi.c](https://github.com/raspberrypi/linux/blob/rpi-6.18.y/drivers/gpu/drm/panel/panel-waveshare-dsi.c)). So `wlopm --off` on this panel should reach the backlight through the kernel. Waveshare documents `echo X | sudo tee /sys/class/backlight/*/brightness` (0–255) and quotes "about 500mA" with the backlight on versus "about 100mA" with it off ([Waveshare 7inch DSI LCD (C)](https://www.waveshare.com/wiki/7inch_DSI_LCD_(C))). I haven't seen this work on real hardware. A USB power meter on the Screen's supply would confirm it.
- Other DSI panels need their own overlay and driver. Whether they have a backlight device depends on the driver.

### 4.3 HDMI: signal off, CEC, and the unknown panel behaviour

- **Signal off** (§4.1) is the only HDMI method Raspberry Pi documents for the current OS.
- **What a cheap 1024x600 HDMI panel does without a signal: not verifiable from primary sources.** The Waveshare 7" HDMI (C) wiki and the 70H-1024600 wiki do not say what happens on loss of signal ([7inch HDMI LCD (C)](https://www.waveshare.com/wiki/7inch_HDMI_LCD_(C)), [70H-1024600](https://waveshare.com/wiki/70H-1024600)). Possible outcomes are: backlight off (true dark), a "No Signal" banner, or a backlit black screen, possibly followed by sleep after a timeout. Which one this Screen does can only be seen by testing it. Inside a dark PC case, a lit black panel or a banner may be visible through a window.
- **Panel-side backlight control:** the 70H board's 40-pin FPC carries `BL_EN` ("Display backlight enable") and `BL_PWM`. Waveshare quotes ~450 mA backlight on versus ~100 mA off ([70H-1024600](https://waveshare.com/wiki/70H-1024600)). The HDMI (C) Rev 4.1 has a "Backlight On/Off" control and "Brightness is adjustable by the USB interface" via a vendor tool, with no published protocol ([7inch HDMI LCD (C)](https://www.waveshare.com/wiki/7inch_HDMI_LCD_(C))). If the chosen panel exposes `BL_EN` or a documented USB command, wiring it to a Pi GPIO or driving it over USB could give a true backlight-off on HDMI. This depends on the panel and is unverified.
- **CEC:** the Pi 4 HDMI driver registers a kernel CEC adapter ([vc4_hdmi.c `cec_allocate_adapter`](https://github.com/raspberrypi/linux/blob/rpi-6.18.y/drivers/gpu/drm/vc4/vc4_hdmi.c)). The display can be told to sleep and wake with `cec-ctl -d/dev/cecX --to 0 --standby` and `--image-view-on` ([cec-ctl(1)](https://github.com/gjasny/v4l-utils/blob/master/utils/cec-ctl/cec-ctl.1.in), from `v4l-utils`). The Waveshare 70H FPC pinout includes a CEC line ([70H-1024600](https://waveshare.com/wiki/70H-1024600)), but **no cheap-panel vendor documents CEC standby support**, so assume it doesn't work until tested.
- **DDC/CI** (`ddcutil`, in Trixie as 2.2.0, [Debian sources](https://sources.debian.org/src/ddcutil/)) can switch some monitors off. Whether any cheap 1024x600 panel controller supports it is unverified.

## 5. Comparison

### Display stacks

| Option | Boots to full screen via | Blank on HDMI | Blank on DSI | Packages / moving parts | Boot time / RAM |
|---|---|---|---|---|---|
| A. Desktop + labwc + Chromium | lightdm autologin → `labwc -m` → autostart | `wlopm --off` (keeps layout) or `wlr-randr --off` | same, plus backlight sysfs | Most (full desktop, panel, kanshi) | Not published, measure |
| B. Lite + cage + any Wayland app | systemd `cage@tty1` | `wlr-randr --off` only (wlopm PR pending), layout side effects untested | same, plus backlight sysfs | Few | Not published, measure |
| C. Lite + native DRM (Qt eglfs / SDL kmsdrm) | systemd service on a TTY | App must set CRTC `ACTIVE` via libdrm itself (no toolkit API found) | backlight sysfs | Fewest | Not published, measure |
| D. X11 + openbox + Chromium | lightdm autologin | `xset dpms force off/on` | same, plus backlight sysfs | Many, not recommended | Not published |

### Blanking mechanisms

| Mechanism | Works on current OS (KMS)? | HDMI result | DSI result | Notes |
|---|---|---|---|---|
| `vcgencmd display_power` | **No** (blocked in firmware) | n/a | n/a | [firmware#1740](https://github.com/raspberrypi/firmware/issues/1740) |
| `wlopm --off/--on` | labwc yes; cage no (until PR #529) | Signal off; panel response unknown | Panel disabled → backlight off via kernel (driver-dependent) | Keeps windows in place |
| `wlr-randr --off/--on` | labwc, cage | Signal off; panel response unknown | As above | Removes output from layout |
| Backlight sysfs `bl_power` / `brightness` | Yes, any stack | n/a | True backlight off | Needs a panel driver with a backlight device |
| CEC `--standby` / `--image-view-on` | Kernel adapter exists | Only if panel supports CEC (undocumented) | n/a | Test on the real Screen |
| Panel `BL_EN` GPIO / vendor USB | Panel-specific | True backlight off, if the panel exposes it | n/a | Hardware work, unverified |
| Drawing black | Always | Backlight stays on | Backlight stays on | Not a true Blank, fallback only |

## 6. Implications for the Kiosk's design (input to #9, no decision here)

1. **The Kiosk owns Blank.** OS idle blanking must be off and stay off (§3), and the Kiosk turns the Screen off and on from Stats arriving or stopping, and from Critical Alert state. Every option above supports that, but by different means.
2. **Hide Blank behind one interface with an HDMI and a DSI implementation.** The mechanism depends on the connection (output power via compositor or DRM, versus backlight sysfs), and the connection isn't known yet. Keeping it swappable avoids redoing the design when the Screen is chosen.
3. **Labwc plus `wlopm` is currently the only documented, packaged path where an outside command blanks without disturbing the app's window.** cage is leaner but needs `wlr-randr` (layout side effects untested) until its PR lands. A native DRM app would have to write its own libdrm display-power code for HDMI.
4. **If HDMI, test the actual Screen before committing.** Turn the signal off and watch whether the backlight goes dark, a banner shows, or it re-wakes (`vc4.force_hotplug=1`). The answer decides whether HDMI "signal off" counts as a Blank at all, or whether CEC, `BL_EN` or USB backlight control is needed.
5. **Wake must work with no input.** Nothing on the Pi will ever produce user activity, so waking must be an explicit Kiosk action, triggered by Stats returning or a Critical Alert.
6. **Boot time and memory for Chromium versus native need a measurement spike** on the actual Pi 4B, whose RAM size should be recorded in #2.

## Sources

Raspberry Pi
- Raspberry Pi OS release notes (latest 2026-10-06): https://downloads.raspberrypi.com/raspios_arm64/release_notes.txt
- Trixie announcement (2 Oct 2025): https://www.raspberrypi.com/news/trixie-the-new-version-of-raspberry-pi-os/
- labwc-by-default release (Oct 2024): https://www.raspberrypi.com/news/a-new-release-of-raspberry-pi-os/
- Configuration docs (display settings, screen blanking, Wayland on/off): https://www.raspberrypi.com/documentation/computers/configuration.html (source: https://github.com/raspberrypi/documentation/blob/master/documentation/asciidoc/computers/configuration/audio-display.adoc, commit fe7c672, 2026-11-23)
- OS docs (Lite vs Desktop, vcgencmd): https://www.raspberrypi.com/documentation/computers/os.html
- Legacy config.txt options: https://www.raspberrypi.com/documentation/computers/legacy_config_txt.html
- Official Touch Display (800x480) and Touch Display 2 (720x1280): https://www.raspberrypi.com/documentation/accessories/display.html, https://www.raspberrypi.com/documentation/accessories/touch-display-2.html
- Kiosk tutorial: https://www.raspberrypi.com/tutorials/how-to-use-a-raspberry-pi-in-kiosk-mode/
- raspi-config (trixie branch, `do_blanking`, `do_wayland`): https://github.com/RPi-Distro/raspi-config/blob/trixie/raspi-config
- Apt archive index: http://archive.raspberrypi.com/debian/dists/trixie/main/binary-arm64/Packages.gz
- rpd-wayland-core 1.35 package: http://archive.raspberrypi.com/debian/pool/main/r/rpd-metas/rpd-wayland-core_1.35_arm64.deb
- Firmware issue on `display_power` under KMS: https://github.com/raspberrypi/firmware/issues/1740
- Kernel overlays README (rpi-6.18.y): https://github.com/raspberrypi/linux/blob/rpi-6.18.y/arch/arm/boot/dts/overlays/README
- Waveshare DSI panel driver: https://github.com/raspberrypi/linux/blob/rpi-6.18.y/drivers/gpu/drm/panel/panel-waveshare-dsi.c
- vc4 HDMI driver (CEC): https://github.com/raspberrypi/linux/blob/rpi-6.18.y/drivers/gpu/drm/vc4/vc4_hdmi.c

Compositors and tools
- labwc: https://github.com/labwc/labwc (`src/server.c`, `src/output.c`, `docs/labwc.1.scd`, `docs/labwc-config.5.scd`; master 4d5f9cb)
- cage: https://github.com/cage-kiosk/cage (README, `cage.1.scd`, `cage.c`, `output.c`; master f551ca2), wiki: https://github.com/cage-kiosk/cage/wiki/Starting-Cage-on-boot-with-systemd, DPMS issue: https://github.com/cage-kiosk/cage/issues/245, PR: https://github.com/cage-kiosk/cage/pull/529
- wlr-randr: https://gitlab.freedesktop.org/emersion/wlr-randr
- wlopm: https://git.sr.ht/~leon_plickat/wlopm
- wlr-output-power-management protocol: https://gitlab.freedesktop.org/wlroots/wlr-protocols/-/blob/master/unstable/wlr-output-power-management-unstable-v1.xml
- swayidle(1): https://github.com/swaywm/swayidle/blob/master/swayidle.1.scd
- Debian package versions: https://sources.debian.org/src/cage/, https://sources.debian.org/src/labwc/, https://sources.debian.org/src/ddcutil/

Linux kernel
- Backlight sysfs ABI: https://github.com/torvalds/linux/blob/master/Documentation/ABI/stable/sysfs-class-backlight
- `drm_panel_disable`: https://github.com/torvalds/linux/blob/master/drivers/gpu/drm/drm_panel.c
- DPMS → CRTC ACTIVE: https://github.com/torvalds/linux/blob/master/drivers/gpu/drm/drm_connector.c, https://github.com/torvalds/linux/blob/master/drivers/gpu/drm/drm_crtc.c
- `consoleblank`: https://github.com/torvalds/linux/blob/master/Documentation/admin-guide/kernel-parameters.txt
- cec-ctl(1): https://github.com/gjasny/v4l-utils/blob/master/utils/cec-ctl/cec-ctl.1.in

Rendering
- Chromium switches: https://github.com/chromium/chromium/blob/main/chrome/common/chrome_switches.h, https://github.com/chromium/chromium/blob/main/base/base_switches.h, https://github.com/chromium/chromium/blob/main/ui/ozone/public/ozone_switches.cc
- Qt 6.12, Qt for Embedded Linux: https://doc.qt.io/qt-6/embedded-linux.html
- SDL3 hints: https://wiki.libsdl.org/SDL3/SDL_HINT_VIDEO_ALLOW_SCREENSAVER, https://wiki.libsdl.org/SDL3/SDL_HINT_VIDEO_DRIVER; SDL source: https://github.com/libsdl-org/SDL

Panel vendors (example 1024x600 panels, not the confirmed Screen)
- Waveshare 7inch HDMI LCD (C): https://www.waveshare.com/wiki/7inch_HDMI_LCD_(C)
- Waveshare 70H-1024600: https://waveshare.com/wiki/70H-1024600
- Waveshare 7inch DSI LCD (C): https://www.waveshare.com/wiki/7inch_DSI_LCD_(C)
