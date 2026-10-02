// Records and init-only setters need this type, which only exists in .NET 5+.
// Declaring it is the documented way to use them on net48.
namespace System.Runtime.CompilerServices;

internal static class IsExternalInit { }
