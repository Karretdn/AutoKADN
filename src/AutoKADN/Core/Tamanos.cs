using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace AutoKADN.Core;

/// <summary>Tamaños de lo que dibuja cada herramienta. Los valores por defecto son los que tenían fijos en el código.</summary>
public sealed class TamanoValores
{
    public double Limites { get; set; } = 2.40;
    public double Anotaciones { get; set; } = 2.40;
    public double Vial { get; set; } = 2.40;
    public double Predial { get; set; } = 2.40;
    public double Cotas { get; set; } = 0.05;
    public double Bloques { get; set; } = 1.00;

    public TamanoValores Clone() => (TamanoValores)MemberwiseClone();
}

/// <summary>
/// Tamaños configurables (ventana "Tamaños"). Se guardan en %APPDATA%\AutoKADN\tamanos.txt como líneas
/// "clave=valor"; cada herramienta los lee en el momento de dibujar, así un cambio vale de inmediato.
/// </summary>
public static class Tamanos
{
    public static string FilePath
    {
        get
        {
            string? custom = Environment.GetEnvironmentVariable("AUTOKADN_TAMANOS");
            return !string.IsNullOrWhiteSpace(custom)
                ? custom!
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoKADN", "tamanos.txt");
        }
    }

    public static TamanoValores Load()
    {
        var values = new TamanoValores();
        try
        {
            if (!File.Exists(FilePath)) return values;
            foreach (string line in File.ReadAllLines(FilePath))
            {
                int equals = line.IndexOf('=');
                if (equals <= 0) continue;
                string key = line.Substring(0, equals).Trim();
                if (!double.TryParse(line.Substring(equals + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || number <= 0) continue;
                switch (key)
                {
                    case "Limites": values.Limites = number; break;
                    case "Anotaciones": values.Anotaciones = number; break;
                    case "Vial": values.Vial = number; break;
                    case "Predial": values.Predial = number; break;
                    case "Cotas": values.Cotas = number; break;
                    case "Bloques": values.Bloques = number; break;
                }
            }
        }
        catch { /* archivo ilegible: se usan los valores por defecto */ }
        return values;
    }

    public static void Save(TamanoValores values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        File.WriteAllLines(FilePath, new[]
        {
            $"Limites={F(values.Limites)}", $"Anotaciones={F(values.Anotaciones)}", $"Vial={F(values.Vial)}",
            $"Predial={F(values.Predial)}", $"Cotas={F(values.Cotas)}", $"Bloques={F(values.Bloques)}"
        });
    }

    // ── Reconocimiento de lo ya dibujado (sin dependencias de AutoCAD) ─────────────────────────────
    public const string KindLimite = "LIMITE";
    public const string KindVial = "VIAL";
    public const string KindPredial = "PREDIAL";

    private static readonly Regex LimitePattern = new Regex(@"^(?:LB|LP|LC)(?:0\.0)?$", RegexOptions.IgnoreCase);
    private static readonly Regex VialPattern = new Regex(@"^(?:KR|CL)\s+\S+\s*-\s*(?:T\.N\.|PAV|ASF|ADO)$", RegexOptions.IgnoreCase);
    private static readonly Regex CotaLayerPattern = new Regex(@"^(?:COTA_.+|UC_.+|COTAS MAGENTA)$", RegexOptions.IgnoreCase);

    /// <summary>Tipo de un texto de una línea por lo que dice (LB, LP, LC... o "KR 12 - ASF"); null si no se reconoce.</summary>
    public static string? ClassifyText(string content)
    {
        string text = (content ?? string.Empty).Trim();
        if (LimitePattern.IsMatch(text)) return KindLimite;
        if (VialPattern.IsMatch(text)) return KindVial;
        return null;
    }

    public static bool IsCotaLayer(string layer) => CotaLayerPattern.IsMatch(layer ?? string.Empty);

    public static double HeightFor(TamanoValores values, string kind)
    {
        switch (kind)
        {
            case KindLimite: return values.Limites;
            case KindVial: return values.Vial;
            case KindPredial: return values.Predial;
            default: return 0.0;
        }
    }
}
