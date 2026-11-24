# Triggers raise Critical Alerts through alert files

Critical Alerts are raised by Triggers, separate programs or scripts on the PC, never by logic inside the Reporter. A Trigger raises an alert by creating a file in a well-known Alerts folder (file name = condition key, line 1 = title, the rest = detail) and Clears it by deleting the file. The Reporter rescans the folder for every Report and sends exactly the alerts whose files exist, so the Reporter holds no thresholds or Trigger settings, and a new kind of alert never needs a Reporter or Kiosk change. An occurrence is the condition key plus the file's creation time, which lets a dismissal (a matching record in a Dismissed folder) survive reboots and lets a condition that Clears and comes back raise a fresh alert, even while the Reporter was down.

## Considered Options

- **Polled checks inside the Reporter**: the same "everything true right now" model, but every threshold and condition would live in the Reporter's config and code, and each new alert kind would mean a Reporter release.
- **Raise and clear calls into the Reporter**: a missed clear leaves an alert stuck, and it needs a local IPC listener plus a way to replay state after the Reporter restarts.
- **External programs talking to the Reporter over IPC**: keeps Triggers outside, but a Trigger or the dismiss command fails whenever the Reporter isn't running, and the Reporter has to persist alert state itself. Files give persistence and "works while stopped" for free.

## Consequences

- The alert-file layout and contents are a contract that every Trigger depends on; changing it breaks existing Triggers.
- The Alerts and Dismissed folders must be writable by any local user, so a non-elevated script can raise an alert; a stray writer can only produce a strange alert.
- An empty or unreadable alert file still raises an alert, because an alert must never be silently lost.
- NTFS tunnelling restores the old creation time if a file is recreated within ~15 s of deletion, so a clear and re-raise that fast counts as the same occurrence.
