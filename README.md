[marketplace]: <https://marketplace.visualstudio.com/items?itemName=MadsKristensen.NuGetManagerSlim>
[vsixgallery]: <http://vsixgallery.com/extension/NuGetManagerSlim.02b52d4a-302c-45d8-9d1f-9cc4759f30be/>
[repo]: <https://github.com/madskristensen/NuGetManagerSlim>

# NuGet Quick Manager for Visual Studio

[![Build](https://github.com/madskristensen/NuGetManagerSlim/actions/workflows/build.yaml/badge.svg)](https://github.com/madskristensen/NuGetManagerSlim/actions/workflows/build.yaml)
[![GitHub Sponsors](https://img.shields.io/github/sponsors/madskristensen)](https://github.com/sponsors/madskristensen)

**A faster, lighter NuGet Package Manager for Visual Studio.** One dockable tool window, one filterable list, no blocking dialogs.

Download from the [Visual Studio Marketplace][marketplace] or get the latest [CI build][vsixgallery].

---

## Why another NuGet manager?

The in-box Package Manager opens a large and somewhat heavy window, makes you flip between Browse / Installed / Updates tabs, and blocks the UI while it talks to the feed. NuGet Quick Manager replaces that flow with a single dockable tool window where every search, install, update, and uninstall runs asynchronously - the UI stays responsive the whole time.

It coexists with the built-in NuGet Package Manager. Both can be open at the same time and write to the same project files without conflict.

## What Can You Do?

**One unified list** - Installed packages pin to the top of the Browse list with a clear "Installed" header, so you never have to flip tabs to see what's already there.

**Search that keeps up with you** - As-you-type local filtering on already-loaded results, with debounced remote feed queries running in the background.

**Inline actions** - Install, update, and uninstall directly from the row. Update icons stay visible at rest so you can spot outdated packages at a glance.

**See your transitive dependencies** - Indirect (transitive) packages appear under their own "Transitive packages" header in the Installed view, with a "required by" label so you know who pulled them in.

**Detail pane on demand** - Description, license, downloads, authors, version history, project membership, dependencies, and a README preview - all without leaving the window.

## Get Started in 30 Seconds

1. **Install** from the [Visual Studio Marketplace][marketplace]
2. **Right-click** a project in Solution Explorer and pick **Manage NuGet Packages...**
3. **Search, browse, install** - the tool window stays interactive throughout

## Tips

| Action | How |
|--------|-----|
| Filter to a specific feed | Type `source:"nuget.org"` in the search box |
| Toggle prerelease versions | The **Include prerelease** checkbox at the bottom |
| Switch between Browse / Installed / Updates | The toolbar at the top of the tool window |
| See which transitive package pulled in a dependency | Look for the **required by** label under the package id |
| Clear cached feed results | Use the **Refresh** button in the toolbar |

## FAQ

**Q: Does this replace the built-in NuGet Package Manager?**
A: No. It runs alongside the in-box manager and uses the same NuGet client libraries under the hood, so package operations produce identical results. You can use either one (or both) on the same solution.

**Q: Where do my package sources come from?**
A: From your existing NuGet configuration (`NuGet.config`). No separate setup.

**Q: Are packages.config projects supported?**
A: Yes - both PackageReference (SDK-style) and packages.config projects are recognized for the installed list.

**Q: Why are some packages marked as transitive?**
A: Transitive packages were pulled in by one of your direct dependencies, not added by you. They're shown for visibility (read-only) under the "Transitive packages" header. To change one, update the direct dependency that requires it.

**Q: A package shows no icon - is that a bug?**
A: Some packages publish an `iconUrl` that resolves to nothing or to a non-image. The default placeholder is shown in those cases. Cached icons live in `%LocalAppData%\NuGetManagerSlim\IconCache`.

## Contributing

This is a passion project, and contributions are welcome.

- **Found a bug?** [Open an issue][repo]
- **Have an idea?** [Start a discussion][repo]
- **Want to contribute?** Pull requests are always welcome

**If NuGet Quick Manager saves you time**, consider:

- [Rating it on the Marketplace][marketplace]
- [Sponsoring on GitHub](https://github.com/sponsors/madskristensen)

## License

[Apache 2.0](LICENSE.txt)
