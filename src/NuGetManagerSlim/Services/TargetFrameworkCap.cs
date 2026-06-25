using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NuGetManagerSlim.Services
{
    /// <summary>
    /// Computes the "maximum allowed major version" cap for update suggestions,
    /// based on the project's target framework (issue #27). For a project that
    /// targets a specific .NET major (e.g. net8.0), runtime-coupled package
    /// families (System.*, Microsoft.Extensions.*, Microsoft.AspNetCore.*) should
    /// not be auto-updated to a higher major (e.g. 9.0.0); the recommended target
    /// caps at the highest version whose major is &lt;= the project's .NET major.
    ///
    /// The cap only applies to projects that target a meaningful .NET major.
    /// .NET Framework (net48 / v4.x) and .NET Standard have no such major, so they
    /// return null (no cap) and behave exactly as before.
    /// </summary>
    public static class TargetFrameworkCap
    {
        // Package id prefixes whose versioning is coupled to the .NET runtime
        // major. Matched case-insensitively as a prefix. Chosen conservatively;
        // these are the families that ship a new major lockstep with each .NET
        // release and routinely break or warn when used above the runtime major.
        private static readonly string[] CappedFamilyPrefixes =
        {
            "System.",
            "Microsoft.Extensions.",
            "Microsoft.AspNetCore.",
            // EF Core ships a new major in lockstep with each .NET release
            // (EF Core 8 -> net8.0, EF Core 9 -> net9.0), so it caps like the
            // ASP.NET Core family. No trailing dot so it covers both the base
            // Microsoft.EntityFrameworkCore package and the providers such as
            // Microsoft.EntityFrameworkCore.SqlServer (issue #30).
            "Microsoft.EntityFrameworkCore",
            // The Windows.Compatibility meta-package is versioned in lockstep
            // with the runtime too (8.0.x, 9.0.x) and is the common bridge when
            // retargeting from .NET Framework, so cap it to the project's major
            // (issue #30). Prefix form also covers the exact id.
            "Microsoft.Windows.Compatibility",
            // Microsoft.Data.Sqlite ships from the EF Core repo on the same
            // runtime-aligned cadence (8.0.x, 9.0.x). The prefix covers
            // Microsoft.Data.Sqlite and .Core without matching the
            // independently-versioned Microsoft.Data.SqlClient (issue #30).
            "Microsoft.Data.Sqlite",
            // The BCL backport packages (Microsoft.Bcl.AsyncInterfaces,
            // Microsoft.Bcl.TimeProvider, etc.) come from dotnet/runtime and
            // version in lockstep (8.0.0, 9.0.0). On .NET Framework targets the
            // cap doesn't apply (no .NET major), so backport consumers are
            // unaffected; on net8.0+ the major is held to the runtime (issue #30).
            "Microsoft.Bcl.",
            // Blazor's JS interop ships from dotnet/aspnetcore in lockstep with
            // the runtime but its id isn't under the Microsoft.AspNetCore.
            // prefix, so cap it explicitly (issue #30).
            "Microsoft.JSInterop",
        };

        /// <summary>
        /// True when the package id belongs to a runtime-coupled family that
        /// should respect the target-framework version cap.
        /// </summary>
        public static bool IsCappedFamily(string? packageId)
        {
            if (string.IsNullOrEmpty(packageId)) return false;
            foreach (var prefix in CappedFamilyPrefixes)
            {
                if (packageId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Maps a single target-framework moniker to its .NET major version, or
        /// null when the moniker has no meaningful .NET major (e.g. net48,
        /// netstandard2.0). Handles both short monikers (net8.0, netcoreapp3.1)
        /// and the legacy non-SDK form (v4.8).
        /// </summary>
        public static int? GetDotNetMajor(string? targetFramework)
        {
            if (string.IsNullOrWhiteSpace(targetFramework)) return null;

            var tfm = targetFramework.Trim();

            // Legacy non-SDK <TargetFrameworkVersion>, e.g. "v4.8" -> .NET
            // Framework, which has no .NET (Core/5+) major to cap against.
            if (tfm.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                && tfm.Length > 1 && char.IsDigit(tfm[1]))
            {
                return null;
            }

            // .NET Standard has no runtime major.
            if (tfm.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase))
                return null;

            // .NET Core 1.x-3.x: "netcoreapp3.1" -> 3.
            if (tfm.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase))
                return ParseLeadingMajor(tfm.Substring("netcoreapp".Length));

            // .NET 5+ short monikers: "net8.0", "net8.0-windows" -> 8. Classic
            // .NET Framework monikers ("net48", "net472") have no dot and are
            // excluded so they don't get treated as a .NET 48 major.
            if (tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase))
            {
                var rest = tfm.Substring("net".Length);
                if (rest.Length == 0) return null;

                // Strip any platform suffix (e.g. "-windows10.0.19041.0").
                var dash = rest.IndexOf('-');
                if (dash >= 0) rest = rest.Substring(0, dash);

                // Only the dotted form (net5.0+) denotes a .NET (Core/5+) major.
                if (rest.IndexOf('.') < 0) return null;

                return ParseLeadingMajor(rest);
            }

            return null;
        }

        /// <summary>
        /// Resolves the cap for a set of target-framework monikers, e.g. a
        /// multi-targeting project (&lt;TargetFrameworks&gt;) or a solution scope
        /// spanning several projects. Returns the most conservative (minimum)
        /// recognized .NET major. If any target framework has no .NET major
        /// (e.g. a project also targets net48 or netstandard), no cap is applied
        /// so legitimate updates for those frameworks are never hidden.
        /// </summary>
        public static int? ResolveCap(IEnumerable<string?> targetFrameworks)
        {
            if (targetFrameworks == null) return null;

            var any = false;
            var min = int.MaxValue;
            foreach (var tfm in targetFrameworks)
            {
                if (string.IsNullOrWhiteSpace(tfm)) continue;
                any = true;
                var major = GetDotNetMajor(tfm);
                if (major == null) return null;
                if (major.Value < min) min = major.Value;
            }

            return any && min != int.MaxValue ? min : (int?)null;
        }

        private static int? ParseLeadingMajor(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            var i = 0;
            while (i < text.Length && char.IsDigit(text[i])) i++;
            if (i == 0) return null;

            return int.TryParse(
                text.Substring(0, i),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var major)
                ? major
                : (int?)null;
        }
    }
}
