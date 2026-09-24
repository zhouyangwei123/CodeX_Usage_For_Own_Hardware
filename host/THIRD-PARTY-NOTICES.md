# Third-party notices

## LibreHardwareMonitor 0.9.6

- Project: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor
- Release: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases/tag/v0.9.6
- Package license: MPL-2.0
- Purpose in CodeX Tools: optional CPU/GPU/motherboard sensor discovery.

CodeX Tools embeds this library in the release executable and extracts the original
assemblies to a versioned per-user cache at runtime. If extraction, a supported sensor,
or the required access permission is unavailable, the host continues running and
reports the affected metric as unavailable.
