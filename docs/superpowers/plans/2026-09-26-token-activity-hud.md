# Token activity HUD Implementation Plan

> **For agentic workers:** Use executing-plans for integration; independent numeric aggregation and renderer work may use dispatching-parallel-agents. Follow test-driven-development for data correctness, persistence and window interaction behavior; requesting-code-review before delivery.

**Goal:** Add a collapsible one-hour Token activity chart and input/output tokens-per-minute beside API and network summaries in the existing glass HUD.

**Architecture:** The native scanner exposes recent deduplicated numeric events before daily grouping. A pure activity projection computes each point from its preceding 60-second window. The tray-owned usage service feeds the existing WinForms HUD; the view redraws at most once per new 10-second chart bucket outside existing quota/network updates. Folding only hides the chart, retaining three footer columns and all collection.

**Tech Stack:** .NET Framework 4.8, C# compatible with Windows Framework csc, WinForms, GDI+ premultiplied Alpha layered window; existing standalone PowerShell/C# validation harnesses.

## Global Constraints

- Firmware remains unchanged; no device writes, firmware builds or flashing.
- Keep STATUS compatibility and PC_METRICS v3 at 28 bytes.
- No prompt, tool body, API key, auth data or private configuration in reports or Git.
- No sounds, browser automation of the user's desktop, new npm/runtime dependency, or polling official services for each chart point.
- Preserve three HUD styles, opacity, scale, position, topmost, refresh, dragging, quota/API/network and existing settings behavior.
- Source is local readable Codex logs only. Input includes cache; output includes reasoning; do not sum subcategories twice. Do not equate Token counts to subscription quota or network traffic.
- Curve: 10 seconds per sample, latest one hour, each sample shows preceding 60 seconds of observed tokens. Recent history is dated by event time, not initial scan arrival.
- UI: footer columns API / TOKEN / NETWORK. TOKEN always shows input and output separately in tokens/min. A small visible button toggles only the curve and remembers the choice. Expanded by default for the new feature.
- Scanning/not available/stale states must be visibly distinct from healthy zero observed records. Background collection continues with folded or hidden HUD; hidden view timers stop.

## Task 1: Numeric event projection

Files: `host/src/Usage/UsageScanner.cs`, `UsageReport.cs`, `LocalUsageService.cs`, new `UsageActivity.cs`; new `tools/test-token-activity.ps1` and isolated C# test harness under `tests/`.

Interface consumed by the renderer:

```csharp
public sealed class UsageActivityPoint {
    public DateTimeOffset Time { get; internal set; }
    public long InputTokens { get; internal set; }
    public long OutputTokens { get; internal set; }
    public bool HasData { get; internal set; }
}
public sealed class UsageActivitySnapshot {
    public DateTimeOffset AsOf { get; internal set; }
    public DateTimeOffset? LastEventAt { get; internal set; }
    public bool IsReady { get; internal set; }
    public bool IsStale { get; internal set; }
    public bool IsPartial { get; internal set; }
    public long InputTokensPerMinute { get; internal set; }
    public long OutputTokensPerMinute { get; internal set; }
    public long HourTokens { get; internal set; }
    public ReadOnlyCollection<UsageActivityPoint> Points { get; internal set; }
}
public static class UsageActivity {
    public static UsageActivitySnapshot Create(UsageReport report, DateTimeOffset now);
}
```

- [ ] Add meaningful failing tests: an event at `now-30s` input 120/output 30 contributes 120/30; `now-60s` is excluded; historical import at `now-2h` adds no current rate; cumulative duplicates and archive/fork copies contribute once; future timestamps do not contribute.
- [ ] Include tests for moving window expiry with unchanged files, 10-second bucket count bound, stale/initial/partial reports, cache/reasoning subsets, same-time independent sessions and truncation/rewrite replacement.
- [ ] Expose only the needed recent numeric rows (at least one hour plus the preceding 60 seconds), preserving original event times through cached scans. Retain the same rows in a failed-service refresh while marking the refresh unhealthy.
- [ ] Implement a bounded pure projection using sorted events and a sliding window; no reading files or wall-clock calls in the projection. `HasData` describes observable coverage, not whether positive tokens exist.
- [ ] Run the new activity tests and existing usage checks; write `review/token-activity-data-report.md` with commands, results, interfaces and limits.

## Task 2: Collapsible HUD and settings integration

Files: `host/src/UI/QuotaHudRenderer.cs`, `QuotaHudForm.cs`, `QuotaHudPresentation.cs`, `HudStylePreview.cs`, `TrayApp.cs`, `SettingsForm.cs`, `host/src/Model/AppConfig.cs`; new `host/src/Tests/TokenActivitySelfChecks.cs` and `tools/test-token-hud.ps1`.

- [ ] Test config migration/default/roundtrip and failed save; collapsed and expanded layouts must leave API/TOKEN/NETWORK visible; scaled toggle hit testing must not trigger dragging or quota refresh.
- [ ] Add `quotaHudChartExpanded` config field, default true. Save a click immediately with rollback on failure, retain desktop bottom-right anchoring when folding, and clamp inside current working area.
- [ ] Keep legacy no-usage constructor/render signature for historical contract tests; application and style preview opt into the new layout. Add a usage-aware constructor and pass the tray-owned service.
- [ ] Define a readable canonical three-column layout, checking 60/75/90/100 percent scales. Keep existing full API/OpenCode text accessible if a compact footer must abbreviate it. Draw an antialiased line and subtle fill, without point markers or decorative animation; no fake interpolated activity during unavailable coverage.
- [ ] Subscribe/unsubscribe usage notifications safely, marshal to UI thread, use a 10-second presentation cadence, recompute recent rates when records age out, stop view timers while hidden and keep the shared service alive.
- [ ] Update settings preview to match actual HUD; carry the fold state through save without overwriting live changes from the HUD.
- [ ] Run renderer and window lifecycle checks, inspect saved offscreen images at normal and smallest scale. Re-run existing HUD, settings and phase-two self-checks.

## Task 3: Review and delivery

Files: `docs/PHASE_TWO.md`, new `docs/VALIDATION-0.4.0.md`, `host/src/Properties/AssemblyInfo.cs`, README/handoff release notes and ignored artifacts.

- [ ] Run full source build, relevant existing and new checks, firmware SHA manifest verification.
- [ ] Review the complete feature diff independently; fix validated issues and rerun their covering checks.
- [ ] Run the exact final EXE with local real usage/PC/quota sources and a mock device; verify rolling update, folding/unfolding, callback/timer shutdown and no growing GDI handle trend. Do not claim physical device acceptance.
- [ ] Commit verified source, package version 0.4.0, publish through the already authorized repository workflow and verify anonymous download hash.
- [ ] Back up private live config locally and current EXE, deploy the verified EXE to the established launch path and restart the user's tray silently. Verify file hash/version, process and preserved config values; save evidence under ignored artifacts.
- [ ] Update workspace task state/progress with delivered evidence and remaining hardware-only limits.
