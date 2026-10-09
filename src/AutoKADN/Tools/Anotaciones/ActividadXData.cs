using System.Globalization;
using AutoKADN.Core;
using Autodesk.AutoCAD.DatabaseServices;

namespace AutoKADN.Tools.Anotaciones;

// Lee el XData "AUTOKADN / ACTIVIDAD" que deja AnotacionesTool en el texto de cada actividad:
// [ACTIVIDAD, etiqueta, (diámetro, terreno, cantidad)...]. Lo usa el RESUMEN UC para leer los CRUCE DE ARROYO anotados
// en el layout UC (GenerarExcelTool y DebugDetallesTool tienen sus propias lecturas de los layouts DETALLE).
internal static class ActividadXData
{
    private const string AppName = "AUTOKADN";
    private const string TypeName = "ACTIVIDAD";

    // Cantidades (ML) de un texto de actividad con esa etiqueta, por UC (diámetro + terreno). Vacío si el texto no es esa actividad.
    public static List<KeyValuePair<UcKey, double>> ReadQuantities(Entity entity, string label)
    {
        var result = new List<KeyValuePair<UcKey, double>>();
        ResultBuffer? xdata = entity.GetXDataForApplication(AppName);
        if (xdata is null) return result;

        TypedValue[] values = xdata.AsArray();
        int typeIndex = -1;
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i].TypeCode == (int)DxfCode.ExtendedDataAsciiString
                && string.Equals(values[i].Value as string, TypeName, StringComparison.OrdinalIgnoreCase))
            {
                typeIndex = i;
                break;
            }
        }
        if (typeIndex < 0 || typeIndex + 1 >= values.Length) return result;

        string found = values[typeIndex + 1].Value?.ToString()?.Trim() ?? string.Empty;
        if (!string.Equals(found, label, StringComparison.OrdinalIgnoreCase)) return result;

        int index = typeIndex + 2;
        while (index + 2 < values.Length)
        {
            string diameter = values[index].Value?.ToString()?.Trim() ?? string.Empty;
            string surface = values[index + 1].Value?.ToString()?.Trim() ?? string.Empty;
            if (!TryReadDouble(values[index + 2].Value, out double quantity)) break;
            index += 3;
            if (string.IsNullOrWhiteSpace(surface) || string.IsNullOrWhiteSpace(diameter)) continue;
            result.Add(new KeyValuePair<UcKey, double>(new UcKey(diameter, surface), Math.Abs(quantity)));
        }
        return result;
    }

    private static bool TryReadDouble(object? value, out double result)
    {
        result = 0.0;
        if (value is null) return false;
        if (value is double d) { result = d; return true; }
        if (value is float f) { result = f; return true; }
        if (value is int i) { result = i; return true; }
        if (value is short s) { result = s; return true; }
        return double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }
}
