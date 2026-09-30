## What and why

<!-- What does this change, and which issue does it address? -->

## Safety

- [ ] Scanned folders stay read-only: nothing new opens file contents, writes, renames, moves, deletes or follows links
- [ ] No new network, registry, elevation, persistence or telemetry behaviour
- [ ] Cancelled, failed and incomplete scans still can't look complete
- [ ] `SecurityAuditTests` pass without being loosened

## Testing

<!-- Which suites did you run (build.ps1 -Target Test -Configuration Release, powershell/tests), and which cases did
     you add? Use synthetic file names only; don't attach real inventory reports. -->
