using System;
using NuGetManagerSlim.Models;

namespace NuGetManagerSlim.Services
{
    public interface IRestoreMonitorService
    {
        event EventHandler<RestoreStatusChangedEventArgs>? RestoreStatusChanged;
        void StartMonitoring(ProjectScopeModel scope);
        void StopMonitoring();
    }

    public class RestoreStatusChangedEventArgs : EventArgs
    {
        public bool IsRestoreIncomplete { get; init; }
        public string? ProjectName { get; init; }
        public int UnresolvedCount { get; init; }
    }
}
