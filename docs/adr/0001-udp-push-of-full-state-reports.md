# One-way UDP push of full-state Reports

The Reporter sends one Report per second to the Kiosk as a single UDP datagram, and the Kiosk never replies. Each Report carries the complete current Stats and Critical Alert state, never a delta, so a lost or late datagram is healed by the next one and neither side holds a connection that has to be rebuilt when the PC sleeps, the Pi reboots or the Reporter restarts. The Kiosk judges liveness by its own receive clock (10 s without a valid Report means Blank, unless a Critical Alert is active), and the Reporter sends a final `stopping` Report on a clean stop so the Screen blanks at once.

## Considered Options

- **WebSocket / TCP from the Reporter**: reliable delivery and instant notice of a clean close, but both sides need reconnect and backoff logic, and a half-open connection still needs a timeout, so it buys nothing over full-state datagrams at 1 Hz on a wired LAN.
- **HTTP POST per Report**: easy to inspect, but a request per second and an HTTP server on the Pi for no gain.
- **Kiosk pulls from the Reporter**: needs a listener and an inbound Windows Firewall rule on the PC, where the Reporter already runs elevated.
- **MQTT**: adds a broker as a third moving part.
- **A return channel (Kiosk acknowledges)**: would let the PC report "Kiosk unreachable", but brings back the listener on the PC. Rejected: nothing on the PC confirms the Screen changed.

## Consequences

- The Kiosk must drop stale datagrams itself: each Report carries a random run id per Reporter start and a sequence number, and the Kiosk drops a Report with the same run id and a sequence number that isn't higher.
- The format evolves by addition only: the Kiosk ignores unknown fields and shows "—" for missing Stats; only a breaking change bumps the `schema` major version.
- A false Blank costs a ~3 s "NO SIGNAL" flash on this Screen, which is why the timeout is 10 s rather than a few seconds.
