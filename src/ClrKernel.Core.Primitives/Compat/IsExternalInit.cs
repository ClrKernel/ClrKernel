// netstandard2.0 lacks this marker type the compiler requires for init accessors
// (and therefore records). Declaring it internally is the documented shim.
namespace System.Runtime.CompilerServices;

internal static class IsExternalInit { }
