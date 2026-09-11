using System.Text;
using System.Text.RegularExpressions;

namespace AutoKADN.Core;

// Fuente única de verdad para normalizar diámetro y terreno (UC), y para las claves de
// agrupación diámetro+terreno / descripción+diámetro que antes tenía cada herramienta por
// separado (GenerarExcelTool, DebugDetallesTool, ListaBloquesTool, ResumenUCTool), con
// pequeñas diferencias de comportamiento entre copias que causaron varios de los bugs
// corregidos en esta sesión (materiales duplicados, terreno truncado, etc.).
public static class Naming
{
    public static readonly string[] SurfaceOrder =
    {
        "ZONA VERDE", "ANDEN CONCRETO", "CALZADA CONCRETO", "ANDEN TABLETA",
        "ADOQUIN", "ASFALTO", "CUNETA", "DESTAPADO"
    };

    public static string NormalizeToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return Regex.Replace(value.Trim().ToUpperInvariant(), @"[^A-Z0-9]", string.Empty);
    }

    public static string NormalizeDiameter(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return Regex.Replace(value.Trim().ToUpperInvariant().Replace("\"", string.Empty).Replace("PULGADAS", string.Empty).Replace("PULG", string.Empty), @"\s+", string.Empty);
    }

    public static string NormalizeSurface(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        string normalized = NormalizeWhitespace(value).Trim().ToUpperInvariant();
        if (normalized.Length == 0) return string.Empty;
        normalized = RemoveAccents(normalized);
        normalized = Regex.Replace(normalized, @"\s+", " ");
        if (normalized == "CALZADA ASFALTO") normalized = "ASFALTO";
        return normalized;
    }

    // Terreno UC (diámetro fijo "1/2"/"3/4") al que pertenece un material según su propio
    // diámetro/etiqueta (ej. "2x3/4" -> UC 3/4, "3/4x1/2" -> UC 3/4). Devuelve null si el
    // diámetro no corresponde a ninguna UC conocida.
    public static string GetUcDiameterFromMaterial(string diameter)
    {
        string normalized = NormalizeDiameter(diameter);
        if (normalized == "1/2") return "1/2";
        if (normalized == "3/4" || normalized.EndsWith("X3/4", StringComparison.OrdinalIgnoreCase)) return "3/4";
        if (normalized == "3/4X1/2") return "3/4";
        return null;
    }

    public static int GetSurfaceOrder(string surface)
    {
        int index = Array.FindIndex(SurfaceOrder, x => string.Equals(x, surface, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? int.MaxValue : index;
    }

    // Texto amigable para mostrar en anotaciones/mensajes (ej. "Zona Verde"). No confundir con
    // el texto que GenerarExcelTool necesita para matchear la lista desplegable ACTIVIDAD del
    // Excel (ese es un caso especial propio de esa plantilla y se mantiene local a esa clase).
    public static string ToDisplaySurface(string value) => value.ToLowerInvariant() switch
    {
        "zona verde" => "Zona Verde", "anden tableta" => "Anden Tableta", "calzada concreto" => "Calzada Concreto",
        "destapado" => "Destapado", "cuneta" => "Cuneta", "anden concreto" => "Anden Concreto",
        "asfalto" => "Asfalto", "adoquin" => "Adoquin", _ => value
    };

    private static string NormalizeWhitespace(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var builder = new StringBuilder(value.Length);
        foreach (char c in value) builder.Append(char.IsWhiteSpace(c) ? ' ' : c);
        return builder.ToString();
    }

    private static string RemoveAccents(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value
            .Replace('Á', 'A').Replace('É', 'E').Replace('Í', 'I').Replace('Ó', 'O').Replace('Ú', 'U').Replace('Ü', 'U')
            .Replace('á', 'a').Replace('é', 'e').Replace('í', 'i').Replace('ó', 'o').Replace('ú', 'u').Replace('ü', 'u');
    }
}

// Diámetro ("1/2"/"3/4") + terreno de una UC. Normaliza ambos campos al construirse, para que
// dos valores equivalentes con formato distinto ("3/4" vs "3/4\"", "ZONA VERDE" vs "Zona Verde")
// terminen siendo la MISMA clave de diccionario.
public struct UcKey : IEquatable<UcKey>
{
    public UcKey(string diameter, string surface)
    {
        Diameter = Naming.NormalizeDiameter(diameter);
        Surface = Naming.NormalizeSurface(surface);
    }

    public string Diameter { get; private set; }
    public string Surface { get; private set; }

    public bool Equals(UcKey other) => string.Equals(Diameter, other.Diameter, StringComparison.OrdinalIgnoreCase) && string.Equals(Surface, other.Surface, StringComparison.OrdinalIgnoreCase);
    public override bool Equals(object obj) => obj is UcKey other && Equals(other);
    public override int GetHashCode() { unchecked { return (StringComparer.OrdinalIgnoreCase.GetHashCode(Diameter ?? string.Empty) * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(Surface ?? string.Empty); } }
}

// Descripción + diámetro + unidad + código de un material del catálogo. Normaliza descripción
// y diámetro al construirse, por la misma razón que UcKey.
public struct MaterialKey : IEquatable<MaterialKey>
{
    public MaterialKey(string description, string diameter, string unit, string code)
    {
        Description = Naming.NormalizeToken(description);
        Diameter = Naming.NormalizeDiameter(diameter);
        Unit = string.IsNullOrWhiteSpace(unit) ? "UND" : unit.Trim();
        Code = code == null ? string.Empty : code.Trim();
    }

    public string Description { get; private set; }
    public string Diameter { get; private set; }
    public string Unit { get; private set; }
    public string Code { get; private set; }

    public bool Equals(MaterialKey other) => string.Equals(Description, other.Description, StringComparison.OrdinalIgnoreCase) && string.Equals(Diameter, other.Diameter, StringComparison.OrdinalIgnoreCase) && string.Equals(Unit, other.Unit, StringComparison.OrdinalIgnoreCase) && string.Equals(Code, other.Code, StringComparison.OrdinalIgnoreCase);
    public override bool Equals(object obj) => obj is MaterialKey other && Equals(other);
    public override int GetHashCode() { unchecked { int hash = StringComparer.OrdinalIgnoreCase.GetHashCode(Description ?? string.Empty); hash = (hash * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(Diameter ?? string.Empty); hash = (hash * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(Unit ?? string.Empty); return (hash * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(Code ?? string.Empty); } }
}
