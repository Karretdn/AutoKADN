using System.IO;
using AutoKADN.Mapeo.Model;

namespace AutoKADN.Proyectos.Services.Formatos;

public enum FormatStatus { Generated, Skipped, Failed }

public sealed record FormatOutcome(string Code, string Name, FormatStatus Status, string? OutputPath, string? Detail, FillReport? Report);

/// <summary>
/// "Generar formatos de interventoría": recorre los formatos en orden y, para cada uno, pregunta dónde guardarlo
/// (por defecto en la carpeta raíz del proyecto). Si se cancela, ese formato se omite y se continúa con el
/// siguiente, igual que la generación de los Excel de legalización en AutoKADN.
/// </summary>
public static class InterventoriaGenerator
{
    /// <summary>Formatos en el orden en que se generan.</summary>
    public static readonly string[] Order = { "FT-O-108", "FT-O-117", "FT-O-119", "FT-T-127" };

    public const string FormatsSubfolder = "Formatos";

    /// <summary>Carpeta con los PDF base y sus mapas: la de recursos configurada o, si no los tiene, la que viaja con la app.</summary>
    public static string FormatsFolder(string resourcesFolder)
    {
        string configured = Path.Combine(resourcesFolder, FormatsSubfolder);
        if (Directory.Exists(configured) && Directory.GetFiles(configured, "*.mapa.json").Length > 0) return configured;
        return Path.Combine(AppSettings.BundledResourcesFolder, FormatsSubfolder);
    }

    /// <param name="askPath">Recibe (nombre sugerido, carpeta inicial) y devuelve la ruta elegida, o null si se cancela.</param>
    public static List<FormatOutcome> Run(string projectFolder, string resourcesFolder, Func<string, string, string?> askPath)
    {
        string formats = FormatsFolder(resourcesFolder);
        FormatSource source = FormatSource.Load(projectFolder);
        string orden = source.Value("orden") ?? "";
        var outcomes = new List<FormatOutcome>();

        foreach (string code in Order)
        {
            string mapPath = Path.Combine(formats, code + ".mapa.json");
            string? pdfPath = Directory.Exists(formats)
                ? Directory.GetFiles(formats, code + "*.pdf").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                : null;
            if (!File.Exists(mapPath) || pdfPath is null)
            {
                outcomes.Add(new FormatOutcome(code, code, FormatStatus.Failed, null, "No se encontró el PDF base o su mapa en " + formats, null));
                continue;
            }

            MapaFormato map;
            try { map = MapaFormato.FromJson(File.ReadAllText(mapPath)); }
            catch (Exception ex)
            {
                outcomes.Add(new FormatOutcome(code, code, FormatStatus.Failed, null, "El mapa no se pudo leer: " + ex.Message, null));
                continue;
            }

            string name = string.IsNullOrWhiteSpace(map.Nombre) ? code : map.Nombre;
            List<string> warnings = SourceWarnings(map, source, projectFolder);
            string suggested = FolderNames.Sanitize(orden.Length > 0 ? $"{name} - {orden}" : name) + ".pdf";
            string? chosen = askPath(suggested, projectFolder);
            if (string.IsNullOrWhiteSpace(chosen))
            {
                outcomes.Add(new FormatOutcome(code, name, FormatStatus.Skipped, null, "Omitido (cancelado)", null));
                continue;
            }
            if (string.Equals(Path.GetFullPath(chosen), Path.GetFullPath(pdfPath), StringComparison.OrdinalIgnoreCase))
            {
                outcomes.Add(new FormatOutcome(code, name, FormatStatus.Failed, null, "No se puede sobrescribir el formato base.", null));
                continue;
            }

            try
            {
                FillReport report = PdfFormFiller.Fill(pdfPath, map, source, chosen);
                outcomes.Add(new FormatOutcome(code, name, FormatStatus.Generated, chosen, Describe(report, warnings), report));
            }
            catch (Exception ex)
            {
                outcomes.Add(new FormatOutcome(code, name, FormatStatus.Failed, null, ex.Message, null));
            }
        }
        return outcomes;
    }

    // Datos que faltan o están viejos: el formato se genera igual, pero la persona tiene que enterarse.
    private static List<string> SourceWarnings(MapaFormato map, FormatSource source, string projectFolder)
    {
        var warnings = new List<string>();
        bool needsSummary = !string.IsNullOrWhiteSpace(map.PaginaPorLista)
            || map.Campos.Any(c => UsesSummary(c.Fuente))
            || map.Tablas.Any(t => t.Columnas.Any(c => UsesSummary(c.Fuente)));
        if (!needsSummary) return warnings;

        if (!source.HasSummary)
        {
            warnings.Add("No hay resumen_obra.json en esta carpeta ni en PLANOS: corre RESUMENOBRA en AutoCAD con el dibujo de este proyecto y genera de nuevo.");
            return warnings;
        }

        string folder = Path.GetFileName(Path.GetDirectoryName(source.SummaryPath ?? string.Empty)?.TrimEnd('\\', '/') ?? string.Empty);
        string when = source.SummaryGenerated?.ToString("dd/MM HH:mm") ?? "fecha desconocida";
        if (!string.IsNullOrWhiteSpace(map.PaginaPorLista) && !source.HasList(map.PaginaPorLista))
        {
            warnings.Add($"El resumen_obra.json de «{folder}» (generado {when}) no trae «{map.PaginaPorLista}»: corre RESUMENOBRA otra vez con el plugin actualizado y genera de nuevo desde esa misma carpeta.");
        }
        else if (source.SummaryGenerated is DateTime generated)
        {
            string planos = Path.Combine(projectFolder, "PLANOS");
            if (Directory.Exists(planos))
            {
                DateTime newest = Directory.GetFiles(planos, "*.dwg").Select(File.GetLastWriteTime).DefaultIfEmpty(DateTime.MinValue).Max();
                if (newest > generated.AddMinutes(1))
                    warnings.Add($"El dibujo se guardó después del resumen (resumen {when}, dibujo {newest:dd/MM HH:mm}). Si hiciste cambios, corre RESUMENOBRA otra vez.");
            }
        }
        return warnings;
    }

    // Las claves del resumen llevan punto (anillos.total, ft108.paginas); las de datos_proyecto.json no.
    private static bool UsesSummary(string? fuente)
    {
        if (string.IsNullOrWhiteSpace(fuente) || fuente.TrimStart().StartsWith('=')) return false;
        string key = fuente.Split('|')[0];
        return key.Contains('.') || key.Contains("[]", StringComparison.Ordinal);
    }

    private static string Describe(FillReport report, List<string> warnings)
    {
        string text = Describe(report);
        return warnings.Count == 0 ? text : text + "\n⚠ " + string.Join("\n⚠ ", warnings);
    }

    private static string Describe(FillReport report)
    {
        string text = $"{report.Filled} celda(s) rellenada(s)";
        if (report.NoData.Count > 0) text += $", {report.NoData.Count} conectada(s) sin dato";
        if (report.Unconnected > 0) text += $", {report.Unconnected} sin conectar";
        if (report.Warnings.Count > 0) text += ". " + string.Join(" ", report.Warnings);
        return text;
    }
}
