[marketplace]: <https://marketplace.visualstudio.com/items?itemName=MadsKristensen.NuGetManagerSlim>
[vsixgallery]: <http://vsixgallery.com/extension/NuGetManagerSlim.02b52d4a-302c-45d8-9d1f-9cc4759f30be/>
[repo]: <https://github.com/madskristensen/NuGetManagerSlim>

# NuGet Quick Manager for Visual Studio

[![Build](https://github.com/madskristensen/NuGetManagerSlim/actions/workflows/build.yaml/badge.svg)](https://github.com/madskristensen/NuGetManagerSlim/actions/workflows/build.yaml)
![GitHub Sponsors](https://img.shields.io/github/sponsors/madskristensen)

Download this extension from the [Visual Studio Marketplace][marketplace]
or get the [CI build][vsixgallery].

----------------------------------------

An alternative **NuGet Package Manager** for Visual Studio. A single, dockable tool window replaces the in-box Browse / Installed / Updates tab flow with a unified, filterable package list where every search, install, update, and uninstall runs asynchronously with inline progress - no blocking dialogs, no frozen UI.

The extension coexists with the built-in NuGet Package Manager - both can be open at the same time and write to the same project files without conflict.

## Features

### Async-first tool window

- Single dockable tool window titled **NuGet Quick Manager**
- Open from the **View** menu, the command palette, or with **Ctrl+Alt+N**
- Every feed query and package operation runs on a background thread
- The tool window stays fully interactive during all operations

### Unified, filterable package list

- One scrollable list instead of three tabs
- Each row shows package icon, ID, installed version, available version, author, and source badge
- Composable filter toggles: **Installed**, **Updates**, and **Prerelease**
- Inline **Update** button on rows that have a newer version available

### As-you-type search

- Search box at the top of the tool window
- Instant local filtering of already-loaded results
- Debounced remote feed queries with an inline spinner
- Recent-query history available from the search box

### Project scope

- Dropdown lists every project in the solution plus an **Entire Solution** option
- Defaults to the active project in Solution Explorer
- Switching scope re-filters the list and refreshes installed and version data

### Install, update, and uninstall

- One-click **Install**, **Update**, and **Uninstall** actions in the detail pane
- Inline progress on the affected row and a status message in the status bar
- Multi-project updates execute per project sequentially with per-project progress

### Detail pane

- Package description, author list, license link, and download count
- Version dropdown with stable and (when toggled) prerelease versions
- Project membership and dependency tree
- README preview with a link to the full README

### Direct vs. transitive packages

- Direct (top-level) packages are visually separated from transitive (implicitly installed) packages
- Each transitive row shows a **required by** label so dependency provenance is clear
- Transitive packages are read-only

### Per-framework version state

- For multi-targeted projects, package rows show per-framework version badges (for example **v1.2 [net8.0]  v1.0 [net48]**)
- The detail pane shows a per-framework breakdown so conditional package references are visible
- Per-framework versions can be updated independently

### Source management

- Source selector dropdown with per-feed enable/disable checkboxes and connection-status indicators
- Inline source panel for toggling feeds, viewing per-feed errors, and adding a new source
- Error messages name the specific feed and reason - no navigation to external settings pages required
- Authentication challenges surface an inline **Sign in** link that re-queries the feed through the configured credential provider

### Inline log and status bar

- Status bar at the bottom shows the last operation result, for example "Installed Serilog 3.1.0 in MyApp (1.2s)"
- A **View log** link opens a scrollable inline log overlay with full operation history
- Error messages use plain language with suggested next steps - no internal type names or stack traces

### Restore-state visibility

- Persistent warning in the status bar when a project's last restore is incomplete
- A manual **Refresh** button is always available as a guaranteed fallback

## Requirements

- Visual Studio 2022 (17.0 or later)
- A solution with one or more projects that use PackageReference

## How it works

The extension hosts its own NuGet UI in a tool window built on the Visual Studio Extensibility platform. Feed queries, dependency resolution, and project file edits go through the standard NuGet client libraries, so the in-box Package Manager and this extension produce identical results and can be used interchangeably on the same solution.

## Contribute

If you find this extension useful, please:

- [Rate it on the Marketplace][marketplace]
- [Report issues or request features][repo]
- [Sponsor development](https://github.com/sponsors/madskristensen)
