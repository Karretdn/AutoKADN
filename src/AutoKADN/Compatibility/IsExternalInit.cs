// Compatibilidad para compilar record/init en .NET Framework 4.8.
// El tipo existe en runtimes modernos, pero no está incluido en net48.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
