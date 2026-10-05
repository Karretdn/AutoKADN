using System.IO;

namespace AutoKADN.Proyectos.Services;

public static class FolderNames
{
    /// <summary>Reemplaza los caracteres no válidos en Windows y quita puntos o espacios del final.</summary>
    public static string Sanitize(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray();
        return new string(chars).Trim().TrimEnd('.', ' ');
    }
}
