using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace AutoKADN.Proyectos.Services;

public sealed record OrderInfo(
    string Orden,
    string IdProyecto,
    string Proyecto,
    string Localidad,
    string Departamento,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Lee la orden de trabajo en PDF. Las expresiones se validaron contra 7 órdenes reales (35 de 35 campos
/// iguales a la lectura que hacía KARP con PyMuPDF). El PDF trae una fuente con la "ú" mal codificada
/// ("NC:mero"), por eso se busca "mero de la Orden" sin la letra acentuada.
/// El nombre del archivo (OT {orden}-{id}-PROYECTO {nombre}.pdf) sirve de respaldo y de verificación.
/// </summary>
public static class OrderPdfReader
{
    private static readonly Regex FileNamePattern = new(@"^OT\s+(\d+)-(\d+)-PROYECTO\s+(.+?)(?:\s+\(\d+\))?$", RegexOptions.IgnoreCase);

    public static OrderInfo Read(string pdfPath)
    {
        var warnings = new List<string>();
        string text;
        try
        {
            text = ExtractText(pdfPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("No se pudo leer el PDF: " + ex.Message, ex);
        }

        string departamento = Capture(text, @"Departamento\s+\d+-(.+?)\s+Localidad");
        string localidad = Capture(text, @"Localidad\s+\d+-(.+?)\s*\n");
        string orden = Capture(text, @"mero de la Orden\s+(\d+)");
        string observaciones = Capture(text, @"Observaciones de generacion\s*\n(.*?)\nCOMENTARIOS");

        string id = string.Empty, proyecto = observaciones;
        Match prefix = Regex.Match(observaciones, @"^(\d+)\s*-\s*(.+)$");
        if (prefix.Success)
        {
            id = prefix.Groups[1].Value;
            proyecto = prefix.Groups[2].Value.Trim();
        }
        if (proyecto.StartsWith("PROYECTO ", StringComparison.OrdinalIgnoreCase)) proyecto = proyecto.Substring(9).Trim();

        // Respaldo y verificación con el nombre del archivo.
        Match file = FileNamePattern.Match(Path.GetFileNameWithoutExtension(pdfPath).Trim());
        if (file.Success)
        {
            string fileOrden = file.Groups[1].Value, fileId = file.Groups[2].Value, fileProyecto = file.Groups[3].Value.Trim();
            if (orden.Length == 0) { orden = fileOrden; warnings.Add("El N.º de orden se tomó del nombre del archivo."); }
            else if (orden != fileOrden) warnings.Add($"El N.º de orden del PDF ({orden}) no coincide con el del nombre del archivo ({fileOrden}).");

            if (id.Length == 0) { id = fileId; warnings.Add("El ID del proyecto se tomó del nombre del archivo."); }
            else if (id != fileId) warnings.Add($"El ID del PDF ({id}) no coincide con el del nombre del archivo ({fileId}).");

            if (proyecto.Length == 0) { proyecto = fileProyecto; warnings.Add("El nombre del proyecto se tomó del nombre del archivo."); }
        }

        if (orden.Length == 0) warnings.Add("No se encontró el N.º de orden.");
        if (proyecto.Length == 0) warnings.Add("No se encontró el nombre del proyecto.");
        if (localidad.Length == 0) warnings.Add("No se encontró la localidad.");
        if (departamento.Length == 0) warnings.Add("No se encontró el departamento.");

        return new OrderInfo(orden, id, proyecto, localidad, departamento, warnings);
    }

    private static string ExtractText(string pdfPath)
    {
        using PdfDocument document = PdfDocument.Open(pdfPath);
        var builder = new StringBuilder();
        foreach (var page in document.GetPages()) builder.AppendLine(ContentOrderTextExtractor.GetText(page));
        return builder.ToString().Replace("\r\n", "\n");
    }

    private static string Capture(string text, string pattern)
    {
        Match match = Regex.Match(text, pattern, RegexOptions.Singleline);
        return match.Success ? Regex.Replace(match.Groups[1].Value, @"\s+", " ").Trim() : string.Empty;
    }
}
