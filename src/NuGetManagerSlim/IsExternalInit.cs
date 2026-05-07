// Polyfill required for C# 9 init-only properties on .NET Framework 4.x
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
