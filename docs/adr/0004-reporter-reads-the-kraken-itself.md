# The Reporter reads the Kraken itself, not through LibreHardwareMonitor

FanControl, running as a service from boot, controls the Kraken's pump and radiator fans, and the Reporter only reads them. The Kraken answers each status request with a reply that every open handle on the device can receive, so a second reader's queue fills with the controller's replies. LibreHardwareMonitor's Kraken code takes the first queued reply and never clears the queue, so beside FanControl its liquid temperature can go minutes stale (a FanControl user saw a 2–3 minute, growing lag with NZXT CAM alongside). So the Reporter has its own small Kraken reader, the way liquidctl does it: clear the queue, send the status request, and take the first status reply that arrives after it, whoever asked for it. Liquid temperature, pump and radiator fan speed all come from that one reply. If no fresh reply arrives within about 500 ms, those Stats are missing from that Report, never repeated from an old one.

## Considered Options

- **LibreHardwareMonitor's Kraken support as is:** no extra code, but it's exposed to the stale-reply problem whenever FanControl is polling too, which is always.
- **Read the values from FanControl:** FanControl has no official way to read values out. The community plugins that do it reach into FanControl's internals and say they may break at any time, and they tie the Reporter to FanControl being up.
- **Fix LibreHardwareMonitor upstream so it clears the queue:** the right fix in the long run and worth offering, but v1 shouldn't wait on a release.

## Consequences

- If the Reporter embeds LibreHardwareMonitor, its Kraken support stays switched off so it never opens the Kraken. Its read-only access to the motherboard's fan chip is safe alongside FanControl, which takes the same system-wide lock and whose settings it never writes.
- The Reporter writes only the status request, never a speed or a control mode. Controlling cooling is FanControl's job.
- Whether the Kraken answers after a full power cut without liquidctl's start-up sequence is still unverified. If it doesn't, the Reporter's reader also needs that sequence.
