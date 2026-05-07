using System;

namespace NuGetManagerSlim
{
    internal static class PackageGuids
    {
        public const string PackageGuidString = "f6649f79-8c76-4df8-ae04-370c692ed6ed";
        public static readonly Guid PackageGuid = new(PackageGuidString);

        public const string CommandSetGuidString = "ed430da2-0265-404b-829b-3a2ca2e96fc2";
        public static readonly Guid CommandSetGuid = new(CommandSetGuidString);
    }

    internal static class PackageIds
    {
        public const int OpenNuGetQuickManagerCommand = 0x0100;
    }
}
