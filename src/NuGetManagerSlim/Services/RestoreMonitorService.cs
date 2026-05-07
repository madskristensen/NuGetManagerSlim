using System;
using NuGetManagerSlim.Models;

namespace NuGetManagerSlim.Services
{
    public sealed class RestoreMonitorService : IRestoreMonitorService, IDisposable
    {
        public event EventHandler<RestoreStatusChangedEventArgs>? RestoreStatusChanged;

        private ProjectScopeModel? _currentScope;

        public void StartMonitoring(ProjectScopeModel scope)
        {
            _currentScope = scope;
            // Best-effort: monitor build artifact file changes (project.assets.json)
            // Real implementation uses FileSystemWatcher on obj\ directories
        }

        public void StopMonitoring()
        {
            _currentScope = null;
        }

        public void Dispose() => StopMonitoring();
    }
}
