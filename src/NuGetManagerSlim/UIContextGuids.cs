namespace NuGetManagerSlim
{
    // Shared identifiers for our custom UIContext rules. The string literal must
    // exactly match the GuidSymbol value declared in VSCommandTable.vsct so the
    // <VisibilityConstraints> entries resolve to the same context the package
    // registers via ProvideUIContextRule.
    internal static class UIContextGuids
    {
        public const string DotNetProjectContextString = "9b254846-1ea2-451c-b07c-4ac6a8c64f37";
    }
}
