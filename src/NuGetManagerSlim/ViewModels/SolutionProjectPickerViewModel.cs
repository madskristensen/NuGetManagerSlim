using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using NuGet.Versioning;

namespace NuGetManagerSlim.ViewModels
{
    public enum SolutionPackageAction
    {
        Install,
        Update,
        Uninstall,
    }

    public sealed class SolutionProjectSelection : ObservableObject
    {
        private bool _isSelected;

        public string ProjectFullPath { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public NuGetVersion? CurrentVersion { get; init; }

        public string CurrentVersionDisplay => CurrentVersion?.ToString() ?? string.Empty;

        public bool IsInstalled => CurrentVersion != null;

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
    }

    public sealed partial class SolutionProjectPickerViewModel : ObservableObject
    {
        [ObservableProperty] private string _title = string.Empty;
        [ObservableProperty] private string _packageId = string.Empty;
        [ObservableProperty] private string _versionDisplay = string.Empty;
        [ObservableProperty] private string _actionLabel = "Apply";
        [ObservableProperty] private bool _canApply;

        public ObservableCollection<SolutionProjectSelection> Projects { get; } = [];
        public SolutionPackageAction Action { get; init; }

        public SolutionProjectPickerViewModel(
            SolutionPackageAction action,
            string packageId,
            NuGetVersion? targetVersion,
            IEnumerable<SolutionProjectSelection> projects)
        {
            Action = action;
            PackageId = packageId;
            VersionDisplay = targetVersion?.ToString() ?? string.Empty;

            switch (action)
            {
                case SolutionPackageAction.Install:
                    Title = $"Install {packageId} {targetVersion} in projects";
                    ActionLabel = "Install";
                    break;
                case SolutionPackageAction.Update:
                    Title = $"Update {packageId} to {targetVersion} in projects";
                    ActionLabel = "Update";
                    break;
                case SolutionPackageAction.Uninstall:
                    Title = $"Uninstall {packageId} from projects";
                    ActionLabel = "Uninstall";
                    break;
            }

            foreach (var p in projects)
            {
                p.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(SolutionProjectSelection.IsSelected))
                        RecomputeCanApply();
                };
                Projects.Add(p);
            }
            RecomputeCanApply();
        }

        private void RecomputeCanApply()
        {
            CanApply = Projects.Any(p => p.IsSelected);
        }

        public IReadOnlyList<SolutionProjectSelection> SelectedProjects =>
            Projects.Where(p => p.IsSelected).ToList();

        // Builds picker rows for a given action across the projects in the
        // current solution scope. `installedByProject` maps a project's full
        // path to that project's installed version (null when not installed).
        public static IReadOnlyList<SolutionProjectSelection> BuildRows(
            SolutionPackageAction action,
            IEnumerable<string> projectPaths,
            IReadOnlyDictionary<string, NuGetVersion?> installedByProject)
        {
            var rows = new List<SolutionProjectSelection>();
            foreach (var path in projectPaths)
            {
                installedByProject.TryGetValue(path, out var version);
                var displayName = Path.GetFileNameWithoutExtension(path);

                // Default selection mirrors the built-in NuGet UX:
                //   Install  -> projects that don't have the package
                //   Update   -> projects whose installed version differs from target
                //   Uninstall-> projects that have the package
                bool defaultSelected = action switch
                {
                    SolutionPackageAction.Install => version == null,
                    SolutionPackageAction.Update => version != null,
                    SolutionPackageAction.Uninstall => version != null,
                    _ => false,
                };

                rows.Add(new SolutionProjectSelection
                {
                    ProjectFullPath = path,
                    DisplayName = displayName,
                    CurrentVersion = version,
                    IsSelected = defaultSelected,
                });
            }
            return rows;
        }
    }
}
