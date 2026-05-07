# Visual Studio NuGet Package Manager (Alternative) — Feature Design

## Executive Summary

This extension provides an alternative NuGet Package Manager as a single tool window in Visual Studio. It replaces the in-box PM's tab-and-modal flow with a unified, filterable package list where every search, install, update, and uninstall runs asynchronously with inline progress — no blocking dialogs, no frozen UI. Key differentiators are speed (async-first architecture), a tab-less unified list with composable filter toggles, per-result source provenance, a clear visual split between direct and transitive packages, and per-framework version state for multi-targeted projects. The extension coexists with the in-box NuGet PM; both can be open simultaneously and write to the same project files without conflict.

---

## Table of Contents

1. [Problem Statement](#1-problem-statement)
2. [Target Users & Personas](#2-target-users--personas)
3. [Success Metrics](#3-success-metrics)
4. [Scope](#4-scope)
5. [Use Cases](#5-use-cases)
6. [Functional Requirements](#6-functional-requirements)
7. [Non-Functional Requirements](#7-non-functional-requirements)
8. [UX Design](#8-ux-design)
9. [Keyboard Shortcuts](#9-keyboard-shortcuts)
10. [Edge Cases](#10-edge-cases)

---

## 1. Problem Statement

The built-in Visual Studio NuGet Package Manager is the primary way .NET developers discover, install, and update packages — yet it is broadly criticized for performance, opacity, and poor scaling. Large solutions freeze the UI during package enumeration. The Browse / Installed / Updates tab split forces users to guess which tab holds the action they need. Multi-project update flows reload repeatedly and provide no inline progress. Error messages surface internal details instead of user-language guidance. Package source management is buried in a settings page, and authentication failures produce vague popups without naming the offending feed. Multi-targeted projects with per-framework package conditions are invisible to the UI entirely — the highest-voted NuGet feedback item.

These pain points drive developers to abandon the visual PM for CLI workarounds (`dotnet add package`, manual project file editing) or to switch to competing IDEs that provide a faster, non-blocking NuGet experience. The alternative PM addresses this by providing a single, async-first tool window that treats search responsiveness, inline operation feedback, source transparency, and multi-project legibility as baseline requirements rather than aspirations.

---

## 2. Target Users & Personas

| Persona | Description | Frequency |
|---|---|---|
| **Large-solution maintainer** | Manages 40–100+ project solutions. Uses solution-wide package updates daily. Blocked by freezes, reload loops, and cryptic multi-project errors in the in-box PM. | Multiple times per day |
| **Multi-targeting library author** | Ships packages targeting multiple frameworks (e.g., net8.0 + net48 + netstandard2.0). Conditional package references are invisible in the current PM — the highest-voted DevCom item. | Daily during development cycles |
| **Private-feed enterprise developer** | Works behind corporate proxies with authenticated feeds. Auth prompts, silent feed failures, and unknown source provenance are daily friction. | Daily |
| **Package discoverer / evaluator** | Explores the NuGet ecosystem for the right library. Needs reliable search, README preview, version history, and dependency information before committing to an install. | Weekly or during project setup |

---

## 3. Success Metrics

- Time-to-install a searched package reduced by ≥ 50% compared to the in-box PM (measured via usability study on solutions with 20+ projects).
- Feature adoption rate ≥ 30% of developers who open any NuGet PM UI within 90 days of release.
- Zero reported UI freezes or blocking dialogs during package operations on solutions with up to 50 projects.
- Reduction in "NuGet PM slow / freezing / crashing" feedback tickets by ≥ 40%.
- NPS improvement for NuGet package management in Visual Studio.
- Solution-wide package list loads within 2 seconds for solutions with up to 50 projects.
- Search results appear within 500 ms of the final keystroke for standard public feeds.

---

## 4. Scope

### In-Scope (P0 / P1)

- **Tool window:** Single dockable tool window with title, menu and keyboard invocation.
- **Search:** As-you-type search box with debounced remote feed queries and instant local filtering of loaded results.
- **Unified list:** Single package list replacing Browse / Installed / Updates tabs, with composable filter toggles (Installed, Updates, Prerelease).
- **Project scope:** Dropdown to switch between individual projects and "Entire Solution" scope.
- **Install / Update / Uninstall:** One-click actions with inline progress per row and per project.
- **Version selection:** Inline version dropdown per package in the detail pane, showing stable and (when toggled) prerelease versions.
- **Multi-project update:** Update a package across selected projects from the detail pane. Operations execute per-project sequentially.
- **Source management:** Co-located source panel with per-feed enable/disable, connection status indicators, and inline error messages naming the specific feed.
- **Inline log:** Status bar with last operation result and expandable log overlay for full operation history.
- **Restore-state visibility (best-effort):** Persistent indicator when a project's last restore is incomplete, with a manual Refresh button as guaranteed fallback.
- **Detail pane:** Package description, version dropdown, license link, download count, authors, project membership, dependency tree (collapsible), and README preview with link.
- **Transitive package awareness:** Read-only display of implicitly installed packages with "required by" labels. Direct packages are visually separated from transitive packages.
- **Per-framework version state:** For multi-targeted projects, display per-framework version badges on list rows and a per-framework breakdown in the detail pane.

### Non-Goals (v1)

- **Vulnerability & deprecation metadata** — constraint-blocked; the public NuGet surface does not expose vulnerability feeds.
- **Package Manager Console / PowerShell integration** — would re-create the in-box PM Console rather than differentiate.
- **Modifying or injecting into the in-box NuGet PM** — strategic decision; this is an alternative, not a patch.
- **NuGet.config file editor** — no user praise signal; users who edit sources prefer the file directly.
- **Cache management UI** — no user signal; CLI cache clearing is sufficient.
- **Error List integration for package conflicts** — inline log in the tool window is cleaner and more discoverable.
- **License conflict detection** — zero-vote feedback signal; building a conflict engine is scope creep.
- **"Remove unused packages" analysis** — requires deep project-system static analysis beyond PM UI scope.
- **packages.config → PackageReference migration UI** — VS already provides a built-in migration option.
- **Cross-platform parity** — VS is Windows-only in full form; irrelevant to this extension.
- **Atomic multi-project rollback** — multi-project updates are independent per project; if one fails, others are not rolled back.

---

## 5. Use Cases

| ID | Title | Actor | Trigger | Steps | Outcome |
|---|---|---|---|---|---|
| **UC-01** | Search and install a package | Package discoverer | Needs a library for the active project | 1. Open tool window via shortcut or menu. 2. Type package name in search box. 3. Local results filter instantly; remote results load after debounce with inline spinner. 4. Click package row to populate detail pane. 5. Click [Install]. | Package installed with inline progress; row updates to show installed state |
| **UC-02** | Update a package across multiple projects | Large-solution maintainer | Version drift detected across projects | 1. Set project scope to "Entire Solution." 2. Enable "Updates" filter toggle. 3. Select package row showing version drift. 4. Review per-project version state in detail pane. 5. Click [Update All Projects]. | Each project updated sequentially with per-project progress indicators; row updates on completion |
| **UC-03** | Browse prerelease versions | Package discoverer | Evaluating a preview release | 1. Enable "Prerelease" toggle. 2. Search for the package. 3. Click package row. 4. Select prerelease version from dropdown in detail pane. 5. Click [Install]. | Prerelease version installed; row shows version with "pre" badge |
| **UC-04** | Switch feeds and authenticate | Enterprise developer | Needs packages from a private feed | 1. Open source panel via source selector dropdown. 2. Enable the private feed checkbox. 3. Feed returns an authentication challenge; inline error shows feed name and **[Sign in]** link. 4. Click **[Sign in]** — the extension re-queries the feed, triggering the credential provider prompt. 5. Authenticate via OS or browser prompt. | Feed connects; search results now include private-feed packages tagged with source badge |
| **UC-05** | Recover from a restore failure | Large-solution maintainer | Status bar shows restore warning | 1. Read persistent warning in status bar: "⚠ Restore incomplete for {Project}." 2. Click [Details] to expand inline log with user-language error and suggested action. 3. Search for the problem package. 4. Select a valid version and click [Update]. | Package reference updated; restore succeeds; warning clears |
| **UC-06** | Uninstall a package | Any | Package is no longer needed | 1. Enable "Installed" filter toggle. 2. Select the package row. 3. Click [Uninstall] in detail pane. | Package removed; row disappears from list; transitive section updates after next restore |
| **UC-07** | View transitive dependencies (read-only) | Package discoverer | Wants to understand what a package pulls in | 1. Enable "Installed" filter. 2. Scroll to the "Implicitly installed" section below direct packages. 3. Each transitive row shows "required by: {parent}" label. | User sees dependency provenance without modifying anything |
| **UC-08** | View per-framework package state | Multi-targeting author | Checking conditional package references | 1. Select a multi-targeted project in scope dropdown. 2. Package rows show per-framework version badges (e.g., "v1.2 [net8.0]  v1.0 [net48]"). 3. Click row to see per-framework breakdown in detail pane. | Per-framework state is visible; user can update per-framework version |
| **UC-09** | Recover from a source error | Enterprise developer | Feed is unreachable | 1. Source selector shows "⚠" badge on the failed feed. 2. Open source panel; inline error names the feed and the failure reason. 3. Disable the unreachable feed or fix network. 4. Click Refresh. | Working feeds continue to serve results; failed feed is clearly identified |

---

## 6. Functional Requirements

### P0 — Must Ship

| ID | Title | Description |
|---|---|---|
| **FR-01** | Tool window placement & activation | A single tool window titled "NuGet Quick Manager" is available via the View menu, command palette, and keyboard shortcut (Ctrl+Alt+N). Default dock position: right side, below or tabbed with Solution Explorer. Default width: 420 px. |
| **FR-02** | Async-first operations | Every data fetch, feed query, and package operation (install, update, uninstall) runs on a background thread. The tool window remains fully interactive during all operations. No modal "Please wait" dialogs. |
| **FR-03** | Search box | A search box at the top of the tool window provides as-you-type filtering. The search box chrome, theming, accessibility, and watermark text ("Search packages…") are provided by the VS platform. Debounce timing for remote feed queries (200 ms), recent-query history (MRU dropdown via ↑/↓), and the split between instant local filtering and delayed remote queries are extension-provided behaviors layered on the platform search host. |
| **FR-04** | Project scope dropdown | A dropdown below the search box lists every project in the solution plus an "Entire Solution" option. Defaults to the active project in Solution Explorer. Changing scope re-filters the list and updates installed/version data for the selected scope. |
| **FR-05** | Unified package list | A single scrollable, virtualized list displays packages. No Browse / Installed / Updates tab split. Each row shows: package icon, package ID (bold), installed version, available version (if update exists, shown as "→ X.Y.Z"), author (muted), and source badge. Installed packages show a checkmark. Rows with available updates show an inline [Update] button. |
| **FR-06** | Filter toggles | Three composable toggle buttons — **Installed**, **Updates**, **Prerelease** — narrow the list. Toggles are composable (e.g., Installed + Updates = installed packages with updates available). All toggles off = browse mode (remote search). |
| **FR-07** | Install / Update / Uninstall actions | Clicking [Install], [Update], or [Uninstall] in the detail pane executes the operation with inline progress on the affected row (spinner replaces icon) and a status message in the status bar. The UI is not blocked during the operation. |
| **FR-08** | Source management surface | A source selector dropdown shows configured feeds with enable/disable checkboxes and connection-status indicators. Expanding the selector reveals the source panel where users can toggle feeds, see error details per feed (naming the feed and failure reason), and add a new source via an inline form. No navigation to external settings pages required. |
| **FR-09** | Inline log | A status bar at the bottom of the tool window shows the last operation result (e.g., "✓ Installed Serilog 3.1.0 in MyApp (1.2s)" or "✗ Failed to update — feed unreachable"). A [View log] link opens a scrollable inline log overlay with full operation history. Error messages use user-language descriptions with suggested next steps — no internal type names or stack traces. |
| **FR-10** | Restore-state visibility (best-effort) | When the extension detects that a project's last restore is incomplete (by monitoring changes to internal build artifacts), a persistent warning appears in the status bar: **"⚠ Restore incomplete for {ProjectName} — {N} packages unresolved. [Details]"**. Detection is best-effort; a manual **Refresh** button is always available as a guaranteed fallback. |
| **FR-11** | Version display | Each package row shows the currently installed version and, when an update is available, the latest version as a "→ X.Y.Z" badge. Prerelease versions are labeled with a "pre" badge. |
| **FR-12** | Coexistence with the in-box PM | Both this tool window and the in-box NuGet PM can be open simultaneously. Both write to the same project files through the same underlying package management infrastructure. The project system detects file changes automatically; no explicit reload notification from this extension is required. If a project file is read-only (source control locked), the operation surfaces a clear error with a suggested action. |

### P1 — High Value

| ID | Title | Description |
|---|---|---|
| **FR-13** | Detail pane | Selecting a package row populates a detail pane showing: full description, version dropdown (stable and prerelease when toggled), license link, download count, author(s), project membership checkboxes (for multi-project install/update), and a collapsible dependency tree. Action buttons: [Install], [Update], [Uninstall], [Update All Projects]. |
| **FR-14** | README preview | The detail pane shows a truncated plain-text preview of the package README with a "View on nuget.org" link. No full Markdown rendering in v1. |
| **FR-15** | Multi-project bulk update | When project scope is "Entire Solution," the detail pane shows per-project checkboxes. [Update All Projects] updates the package in each checked project sequentially (one project at a time). Per-project progress is shown inline in the detail pane. If one project fails, others continue independently — there is no atomic rollback. |
| **FR-16** | Prerelease toggle | The Prerelease filter toggle includes prerelease versions in search results and in the version dropdown. Default: off. |
| **FR-17** | License display | License type and link are shown in the detail pane metadata section, sourced from package metadata. |
| **FR-18** | Transitive package display | Below the direct-package section of the list, a visually distinct "Implicitly installed" section shows transitive packages in muted text. Each row displays a "required by: {parent package}" label. Transitive packages are read-only — no install, update, or uninstall controls. This section reflects the state as of the last restore and refreshes automatically when the underlying build artifacts change. |
| **FR-19** | Per-framework version state | For multi-targeted projects, each package row shows per-framework version badges (e.g., "v1.2 [net8.0]  v1.0 [net48]"). The detail pane shows a per-framework breakdown. Per-framework data is derived by parsing the project file's conditional package references, with per-framework build artifact sections as a cross-check. This data requires a recent build or restore to be current. |

### P2 — Desirable

| ID | Title | Description |
|---|---|---|
| **FR-20** | Search history (MRU) | The search box maintains a most-recently-used history of queries, accessible via ↑/↓ arrow keys. This history is stored by the extension, not by VS — clearing VS settings does not affect it. |
| **FR-21** | Saved filter presets | Users can save and recall combinations of filter toggle state + project scope for quick access. |
| **FR-22** | Dependency preview before install | Before installing a package, the detail pane shows a collapsible preview of what the package will pull in as dependencies, based on the package's published metadata. |
| **FR-23** | Custom keyboard shortcuts | Beyond the activation shortcut, allow users to rebind tool-window-specific actions via VS keyboard settings. |
| **FR-24** | Smart sort by importance | When the Updates filter is active, packages with the largest version drift or the most affected projects sort to the top. |

---

## 7. Non-Functional Requirements

| ID | Title | Description |
|---|---|---|
| **NFR-01** | Performance — search latency | Search results from remote feeds begin appearing within 500 ms of the final keystroke. Local filtering of already-loaded results completes within 50 ms. Debounce (200 ms) prevents excessive feed queries during active typing. |
| **NFR-02** | Performance — large solution support | Solution-wide package enumeration (Entire Solution scope) completes within 2 seconds for solutions with up to 50 projects. Data is cached after first load and updated incrementally on project add/remove events. Solutions with 100+ projects may show a progress indicator during initial enumeration. |
| **NFR-03** | Memory | The package list uses UI virtualization. Only visible rows are rendered; off-screen rows are recycled. Memory footprint remains stable regardless of total package count. |
| **NFR-04** | Accessibility | The search box inherits screen reader support, keyboard navigation, and high-contrast behavior from the VS platform search host. Custom list rows and the detail pane use standard accessibility attributes. The transitive "Implicitly installed" section is announced distinctly. Empty-state messages are announced to screen readers when they appear. |
| **NFR-05** | Theming | The search box chrome, scrollbars, and fonts inherit the active VS theme automatically. Custom surfaces (list row layout, status pills, version badges, source badges) use VS theme colors and dynamic resources — no hardcoded colors. |
| **NFR-06** | Localization | All user-facing strings (watermark text, empty-state messages, status messages, error messages, button labels) are localizable. |
| **NFR-07** | Coexistence with in-box PM | This extension does not disable, hide, or modify the in-box NuGet PM. Both can be open and used simultaneously. Writes go through the same package management infrastructure; the project system reconciles file changes automatically. The extension does not hold file locks beyond what the package management APIs themselves perform. |
| **NFR-08** | Async threading | All feed queries, file parsing, and package operations run on background threads. UI updates are marshaled to the UI thread safely. The tool window never blocks the VS main thread. |
| **NFR-09** | Stale-data tolerance | After an install, update, or uninstall, the package list and transitive dependency section may be briefly stale until the project restore completes (typically a few seconds). A subtle "refreshing…" indicator is visible during this window. |
| **NFR-10** | Extension load time | The extension loads asynchronously (deferred). It does not block solution open. The first time the tool window is opened, a brief "Loading…" state may appear while feed data is fetched. |

---

## 8. UX Design

### 8.1 Placement

The tool window is titled "NuGet Quick Manager" and docks by default on the right side of the VS shell, below or tabbed alongside Solution Explorer. Default width: 420 px; height fills available vertical space. It is invoked via View → NuGet Quick Manager, the command palette, or Ctrl+Alt+N.

```
┌─────────────────────────────────────────────────┐
│  NuGet Quick Manager                        [×] │
├─────────────────────────────────────────────────┤
│ [A: Search packages…                      🔍]  │
│ [B: Project ▼] [C: ☐ Installed] [☐ Updates]    │
│               [☐ Prerelease]  [D: Source ▼]     │
├─────────────────────────────────────────────────┤
│ E: Package List                                 │
│ ┌─────────────────────────────────────────────┐ │
│ │ 📦 Newtonsoft.Json         13.0.1 → 13.0.3 │ │
│ │    by James Newton-King  ⊕ nuget.org        │ │
│ │                              [Update ▼]     │ │
│ │─────────────────────────────────────────────│ │
│ │ 📦 Serilog                  3.1.0  ✓ latest │ │
│ │    by Serilog Contributors  ⊕ nuget.org     │ │
│ │─────────────────────────────────────────────│ │
│ │   Implicitly installed                      │ │
│ │   System.Text.Json  required by: Serilog    │ │
│ └─────────────────────────────────────────────┘ │
├─────────────────────────────────────────────────┤
│ F: Detail Pane (selected package)               │
│ ┌─────────────────────────────────────────────┐ │
│ │ Newtonsoft.Json 13.0.3                      │ │
│ │ Popular JSON framework for .NET             │ │
│ │ Version: [13.0.3 ▼]  License: MIT           │ │
│ │ Downloads: 2.1B   Authors: J. Newton-King   │ │
│ │ ──────────────────────────────────────────── │ │
│ │ Projects:                                   │ │
│ │  ☑ MyApp (13.0.1)  ☑ MyLib (13.0.1)        │ │
│ │        [Install] [Update All Projects]      │ │
│ └─────────────────────────────────────────────┘ │
├─────────────────────────────────────────────────┤
│ G: Status Bar                                   │
│  ✓ Updated Newtonsoft.Json → 13.0.3 (2s ago)   │
│  [View log]                        [↻ Refresh]  │
├─────────────────────────────────────────────────┤
│ H: Source Panel (collapsed by default)          │
│  ☑ nuget.org        ☑ AzureArtifacts           │
│  ☐ LocalFeed        [+ Add source]             │
└─────────────────────────────────────────────────┘
```

**Regions:** A = Search box (platform-hosted chrome + extension-provided debounce/MRU logic). B = Project scope dropdown. C = Filter toggles (Installed, Updates, Prerelease). D = Source selector / source panel toggle. E = Virtualized package list (direct packages, then implicitly-installed section). F = Detail pane for selected package. G = Status bar with last-operation result, [View log], and [Refresh]. H = Source panel (collapsed by default; expanded via D).

### 8.2 Interaction Model

1. **Idle state:** Search box shows watermark "Search packages…". If the Installed toggle is off, the list is empty with a prompt message. If the Installed toggle is on, the list shows installed packages for the selected scope.
2. **Typing in search:** Each keystroke filters already-loaded local results instantly. After a 200 ms debounce, a remote feed query fires. A subtle spinner appears at the bottom of the list while remote results load. The user can continue typing to refine.
3. **Active filter / list:** Filter toggles narrow the list client-side. Match count or list count is visible. Toggles are composable.
4. **Selecting a row:** Clicking or arrowing to a row populates the detail pane (F) with full metadata, version dropdown, project membership, dependency tree, and action buttons.
5. **Operating on a row:** Clicking [Install], [Update], or [Uninstall] shows an inline spinner on the row, a status message in the status bar (G), and per-project progress in the detail pane for multi-project operations. The UI remains interactive.
6. **Switching project scope:** Changing the dropdown (B) re-filters the list and refreshes installed/version data. In "Entire Solution" scope, each package row aggregates per-project version state.
7. **Source unreachable / auth challenge:** The source selector (D) shows a "⚠" badge. The source panel (H) shows an inline error naming the feed and the failure reason. If authentication is required, the error includes a **[Sign in]** link that re-queries the feed to trigger the credential provider prompt. Working feeds continue to serve results normally.
8. **Restore failure recovery:** The status bar (G) shows a persistent warning. Clicking [Details] expands the inline log with user-language error messages and suggested actions. The user can search for the problem package, select a valid version, and update. Restore status is best-effort; [Refresh] is always available.

### 8.3 Visual Design Principles

- **Platform-given:** Search box chrome, theming, scrollbars, fonts, high-contrast support, and base accessibility are inherited from VS tool-window standards.
- **Extension-provided:** List row layout (icon + ID + version badge + author + source badge), "Implicitly installed" section styling (muted text), status pills, version badges ("→ X.Y.Z"), source badges ("⊕ nuget.org"), prerelease "pre" badges, inline progress spinners, status bar, and the detail pane layout.
- All custom surfaces use VS dynamic-resource theme colors. No hardcoded colors.

### 8.4 Keyboard Navigation

All regions (A through H) are reachable via Tab in a defined order: Search → Filter toggles → Project scope → Source selector → Package list → Detail pane action buttons → Status bar links. Arrow keys navigate within the package list. Enter triggers the primary action on the focused row. See §9 for the full shortcut table.

### 8.5 Empty / Loading / Error States

**No solution loaded:**
The entire content area shows a centered message: **"Open a solution to manage NuGet packages."** All controls (B, C, D) are disabled. The search box is inactive.

**First open with a solution (no filters active):**
Search box shows watermark. List is empty with a centered message: **"Search for a package to get started, or toggle Installed to see what's in your project."**

**Search returned zero results:**
List shows centered message: **"No packages match '{query}'."** with an inline **Clear search** link. If the Prerelease toggle is off, an additional hint: **"Try enabling Prerelease to see preview packages."**

**Source unreachable:**
Source selector (D) shows a "⚠" badge. Source panel (H) shows the failed feed with inline error: **"{FeedName}: {failure reason}."** If authentication is required: **"{FeedName}: Authentication required. [Sign in]"**. Results from working feeds continue to appear normally.

**Restore failed for a project:**
Status bar (G) shows persistent warning: **"⚠ Restore incomplete for {ProjectName} — {N} packages unresolved. [Details]"**. Affected packages in the list show a "⚠ unresolved" badge. The [Details] link expands the inline log with user-language error messages.

**All packages up to date:**
When the "Updates" filter is active and no packages have updates, the list shows: **"All packages in {scope} are up to date. ✓"**

**Loading state:**
When the tool window is first opened or scope changes, a brief "Loading…" indicator appears in the list area. The search box and filter toggles remain interactive.

---

## 9. Keyboard Shortcuts

| Shortcut | Action | Wired by |
|---|---|---|
| **Ctrl+Alt+N** | Open or focus the NuGet Quick Manager tool window | Extension — registered as a custom key binding |
| **Alt+\`** (tool window focused) | Activate the search box (focus + select existing text) | Platform — automatic via VS tool-window search host registration |
| **Escape** (search box has text) | Clear the search text and restore the unfiltered list | Platform — VS-standard search box behavior |
| **Escape** (search box is empty) | Return focus to the active editor | Platform — VS-standard search box behavior |
| **Tab** | Move focus to the next region in tab order (A → C → B → D → E → F → G) | Platform — standard WPF tab navigation |
| **↑ / ↓** (package list focused) | Navigate between package rows | Platform — standard WPF list navigation |
| **Enter** (package row focused) | Trigger the primary action: [Install] if not installed, [Update] if update available, or expand detail pane | Extension — contextual action on focused row |
| **Space** (filter toggle focused) | Toggle the focused filter on/off | Platform — standard WPF toggle behavior |

> **Note:** Ctrl+E is intentionally not used as a search-focus shortcut. Ctrl+E is mapped to VS global search ("Go to Search") in VS 2022+ and would conflict. The platform search host already provides keyboard activation for the search box within the tool window via Alt+\`.

---

## 10. Edge Cases

| ID | Scenario | Handling |
|---|---|---|
| **EC-01** | No solution or project loaded | All controls disabled. Centered message: **"Open a solution to manage NuGet packages."** Search box inactive. |
| **EC-02** | Solution with 50+ projects — Entire Solution scope | Lazy-load project data with inline progress indicator. Cache results after first load; update incrementally on project add/remove. Performance budget: ≤ 2 s for 50 projects. |
| **EC-03** | Solution with 100+ projects | Show progress indicator during initial enumeration. Allow the user to interact with partially loaded data as it arrives. Consider incremental paging if total exceeds 1000 unique packages. |
| **EC-04** | Slow or unreachable feed during search | Remote results from slow feeds load incrementally as they arrive. Unreachable feeds show a "⚠" indicator on the source selector; working feeds continue to serve results. No full-list blockage. |
| **EC-05** | Feed requires authentication (401 response) | The source panel shows an inline error naming the feed with a **[Sign in]** link. Clicking [Sign in] re-queries the feed, which triggers the credential provider pipeline. The tool window remains responsive during the auth prompt. |
| **EC-06** | Packages.config legacy projects | The extension reads installed packages from the legacy package manifest file. Install/update/uninstall operations are supported through the same package management write path. Display is consistent with PackageReference projects, but some features (transitive display, per-framework state) may be unavailable. |
| **EC-07** | Central Package Management (CPM) — versions defined in a central props file | The extension reads version information from the central version file. Version selection in the detail pane respects CPM constraints: if versions are centrally managed, the version dropdown reflects this and modification updates the central file, not individual project files. |
| **EC-08** | Multi-targeted project with per-framework conditional references | Per-framework package state is derived by parsing the project file's conditional elements, with per-framework build artifact sections as a cross-check. This data reflects the last build or restore. If the project file changed since the last restore, framework-level data may be incomplete until the next restore runs. An "as of last restore" disclaimer is shown. |
| **EC-09** | Transitive packages — user attempts to modify | Transitive (implicitly installed) packages are read-only in the list. No install, update, or uninstall controls are shown. The row displays "required by: {parent}" context. To remove a transitive package, the user must remove or update the parent direct package. |
| **EC-10** | Stale data after install/update/uninstall | After an operation, the installed-package list and transitive section may be briefly stale until the project restore completes. A "refreshing…" indicator appears. The extension monitors build artifact file changes to detect restore completion and refresh automatically. If the indicator persists, the user can click [Refresh] manually. |
| **EC-11** | Project file is read-only (source control locked) | The operation surfaces a clear error: **"Cannot modify {ProjectName} — the project file is read-only. Check out the file or change source control settings."** No silent failure. |
| **EC-12** | Both this tool window and the in-box PM are open | Both operate independently on the same project files. Changes made in either PM are detected by the project system automatically. The user may see a brief refresh delay in the other PM after a change. No explicit synchronization is required. |
| **EC-13** | Restore status detection fails (best-effort limitation) | If the extension cannot detect restore status via file-change monitoring, the status bar does not show a false positive or false negative. The [Refresh] button is always visible as a fallback. Restore status is never guaranteed to be real-time. |
| **EC-14** | Search returns zero results with Prerelease off | Centered message: **"No packages match '{query}'."** with an inline **Clear search** link and a hint: **"Try enabling Prerelease to see preview packages."** |
| **EC-15** | MRU search history with sensitive package names | Search history is stored locally by the extension. It is not synced to the cloud and is not shared across VS instances. Clearing the extension's data clears the history. |
| **EC-16** | Tool window opened before solution fully loads | The tool window shows a "Loading…" state and populates when solution load completes. Controls are disabled until project data is available. |
| **EC-17** | User switches VS theme while tool window is open | All platform-given surfaces update automatically. Custom surfaces use dynamic theme resources and update on theme change. Filter and operation state is preserved. |
| **EC-18** | Package installed in some projects but not others (version drift) | In "Entire Solution" scope, the package row shows aggregated version info (e.g., "v1.2 in ProjectA, v1.3 in ProjectB"). The detail pane shows per-project checkboxes with individual versions. [Update All Projects] aligns all to the selected version. |

---
