# The Kiosk is a Go service plus Chromium on labwc

The Kiosk draws the Screen with Chromium in `--kiosk` mode under labwc on Raspberry Pi OS Lite, showing a plain HTML/CSS/JS page. A separate Go service receives the Reports, owns the Screen state (Dashboard, takeover, Blank) and pushes full state to the page over Server-Sent Events on localhost. A browser looks heavy for a display of about twenty numbers. It was chosen because the Dashboard and the Critical Alert takeover can then be designed, prototyped and checked as ordinary web pages at 1024x600, on any machine and without a Pi. The Pi has 4 GB of RAM and the Screen takes no input, so Chromium's cost doesn't matter.

## Considered Options

- **Native app (pygame/SDL or Qt) on DRM with no compositor:** a single small process, but the UI is drawn in code, so prototypes are throwaway. Neither SDL's kmsdrm nor Qt's eglfs gives a clean way to stop the HDMI signal, which Blank depends on on this Screen.
- **cage instead of labwc:** built for a single app, but it can't power off the screen yet (`wlopm` support is unmerged), so Blank would have to remove the output with `wlr-randr`. Chromium would then lose its screen and resize on every wake.
- **Python or .NET for the service:** Python on Debian can't `pip install` system-wide, and .NET would only pay off if code were shared with the Reporter, which shared JSON test examples largely replace. Go cross-compiles to one static arm64 binary with the page embedded.

## Consequences

- The page is a dumb renderer: every rule and timer lives in the Go service, which also drives Blank with `wlopm --off/--on`, so Blank still works when Chromium hangs.
- The browser is a separate supervised piece that can freeze without dying. The page sends the service a heartbeat, and after about 30 s of silence while the Screen should be lit, the service restarts the labwc/Chromium session. The page also reloads on a new service version and once a day.
- Only Blank and boot setup are Pi-specific. Everything else, plus a fake Reporter, runs on a development machine.
