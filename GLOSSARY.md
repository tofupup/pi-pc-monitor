# PC Monitor

A small live dashboard, mounted inside a PC's case, that shows the state of that PC on a screen driven by a dedicated Raspberry Pi.

## Language

**PC**:
The single Windows 11 machine being monitored.
_Avoid_: Host, server, client, sender

**Pi**:
The Raspberry Pi 4B that receives Stats from the PC and drives the Screen.
_Avoid_: Server, client, receiver, display node

**Reporter**:
The program on the PC that gathers Stats and sends them to the Kiosk.
_Avoid_: Agent, sender, client, server, collector

**Kiosk**:
The program on the Pi that receives Stats from the Reporter and shows them on the Screen.
_Avoid_: Receiver, server, client, display app

**Stats**:
The measurements the PC reports at a point in time.
_Avoid_: Metrics, telemetry, sensor data

**Report**:
One message from the Reporter to the Kiosk, carrying the PC's current Stats and Critical Alert state.
_Avoid_: Packet, payload, heartbeat, snapshot, update

**Screen**:
The physical 7" 1024x600 touchscreen attached to the Pi.
_Avoid_: Display, monitor, panel

**Dashboard**:
The glanceable live view of Stats shown on the Screen.
_Avoid_: UI, GUI, home page

**Colour Cue**:
The amber or red colouring of a temperature Stat on the Screen when it reaches its warm or hot level.
_Avoid_: Warning, alert, alarm

**Critical Alert**:
A notice of an urgent problem with the PC, such as a failing Drive or a hardware error logged by Windows, that takes over the Screen in place of the Dashboard.
_Avoid_: Warning, notification, alarm

**Trigger**:
A program or script on the PC, separate from the Reporter, that raises a Critical Alert when its condition becomes true and Clears it when the condition stops.
_Avoid_: Check, monitor, rule

**Cleared**:
Said of a Critical Alert whose underlying condition is no longer true.
_Avoid_: Resolved, fixed, closed

**Dismissed**:
Said of a Critical Alert that someone has silenced by command on the PC while its condition is still true.
_Avoid_: Acknowledged, muted, snoozed, disabled

**Blank**:
The Screen state that shows nothing, used when Stats have stopped arriving and no Critical Alert is active.
_Avoid_: Off, asleep, standby

**Drive**:
Any storage device in or attached to the PC, internal or external (e.g. NVMe, SATA SSD, HDD, USB drive).
_Avoid_: Disk, hard drive, storage
