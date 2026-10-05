using AutoKADN.Mapeo.Model;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace AutoKADN.Proyectos.Services.Formatos;

public sealed class FillReport
{
    /// <summary>Celdas en las que se escribió algo.</summary>
    public int Filled { get; set; }
    /// <summary>Celdas conectadas a una clave que no tiene valor en los datos del proyecto.</summary>
    public List<string> NoData { get; } = new();
    /// <summary>Celdas sin ninguna clave conectada en el mapa.</summary>
    public int Unconnected { get; set; }
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Escribe los valores sobre el PDF base del formato según su mapa: dibuja la página original y encima el texto
/// de cada celda. Las medidas del mapa ya vienen en puntos con el origen arriba a la izquierda, igual que PDFsharp.
/// </summary>
public static class PdfFormFiller
{
    private const string FontName = "Calibri";
    private const double Padding = 2.0;
    private const double MinFontSize = 5.0;

    public static FillReport Fill(string basePdfPath, MapaFormato map, FormatSource source, string outputPath)
    {
        var report = new FillReport();
        using XPdfForm form = XPdfForm.FromFile(basePdfPath);
        using var output = new PdfDocument();
        output.Info.Title = map.Nombre;

        // Formato de una sola hoja base que se repite una vez por elemento de la lista indicada en el mapa.
        bool repeated = !string.IsNullOrWhiteSpace(map.PaginaPorLista);
        int sheets = repeated ? source.PageCount(map.PaginaPorLista) : form.PageCount;
        for (int sheet = 0; sheet < sheets; sheet++)
        {
            int pageNumber = repeated ? 1 : sheet + 1;
            FormatSource scope = repeated ? source.Page(map.PaginaPorLista, sheet) : source;
            form.PageNumber = pageNumber;
            PdfPage page = output.AddPage();
            page.Width = XUnit.FromPoint(form.PointWidth);
            page.Height = XUnit.FromPoint(form.PointHeight);
            using XGraphics gfx = XGraphics.FromPdfPage(page);
            gfx.DrawImage(form, 0, 0, form.PointWidth, form.PointHeight);

            // El resumen de avisos solo cuenta la primera hoja; las demás repiten las mismas celdas.
            FillReport target = sheet == 0 ? report : new FillReport();
            foreach (CampoMapa campo in map.Campos.Where(c => c.Pagina == pageNumber)) FillField(gfx, campo, scope, target);
            foreach (TablaMapa tabla in map.Tablas.Where(t => t.Pagina == pageNumber)) FillTable(gfx, tabla, scope, target);
            if (sheet > 0) { report.Filled += target.Filled; report.Warnings.AddRange(target.Warnings.Where(w => !report.Warnings.Contains(w))); }
        }
        if (sheets > 1) report.Warnings.Add($"El formato se generó en {sheets} hojas.");

        output.Save(outputPath);
        return report;
    }

    // ---------- campos sueltos ----------

    private static void FillField(XGraphics gfx, CampoMapa campo, FormatSource source, FillReport report)
    {
        if (string.IsNullOrWhiteSpace(campo.Fuente)) { report.Unconnected++; return; }
        string? raw = source.Resolve(campo.Fuente);
        if (raw is null) { report.NoData.Add(campo.Id + " ← " + campo.Fuente); return; }
        if (raw.Trim().Length == 0) return;   // vacío a propósito (ej. "no hubo troncal"): no es un dato que falte

        string text = FormatSource.Format(raw, campo.Tipo);
        var rect = new XRect(campo.X, campo.Y, campo.Ancho, campo.Alto);
        switch (campo.Tipo)
        {
            case TipoCampo.Check:
                if (IsTrue(raw)) DrawCell(gfx, "X", rect, Alineacion.Centro, campo.TamanoFuente, null, null, null);
                else return;
                break;
            case TipoCampo.Multilinea:
                DrawWrapped(gfx, text, campo, campo.TamanoFuente, report);
                break;
            default:
                DrawCell(gfx, text, rect, campo.Alineacion, campo.TamanoFuente, campo.Prefijo, campo.PrefijoAncho, campo.Rotacion);
                break;
        }
        report.Filled++;
    }

    private static bool IsTrue(string raw) =>
        raw.Trim().ToLowerInvariant() is "true" or "1" or "si" or "sí" or "x";

    // ---------- tablas ----------

    private static void FillTable(XGraphics gfx, TablaMapa tabla, FormatSource source, FillReport report)
    {
        var overflow = new HashSet<string>();
        foreach (ColumnaMapa columna in tabla.Columnas)
        {
            string label = tabla.Id + "." + columna.Id;
            if (string.IsNullOrWhiteSpace(columna.Fuente)) { report.Unconnected++; continue; }

            if (FormatSource.TryParseListRef(columna.Fuente, out string listKey, out string field))
            {
                IReadOnlyList<Dictionary<string, string>>? items = source.List(listKey);
                if (items is null || items.Count == 0) { report.NoData.Add(label + " ← " + columna.Fuente); continue; }
                if (items.Count > tabla.Filas.Count && overflow.Add(listKey))
                    report.Warnings.Add($"{tabla.Id}: «{listKey}» tiene {items.Count} elementos y la tabla solo {tabla.Filas.Count} filas; los demás no se escribieron.");

                bool any = false;
                for (int row = 0; row < tabla.Filas.Count && row < items.Count; row++)
                {
                    if (!items[row].TryGetValue(field, out string? value) || value.Trim().Length == 0) continue;
                    FilaMapa fila = tabla.Filas[row];
                    DrawCell(gfx, FormatSource.Format(value, columna.Tipo), new XRect(columna.X, fila.Y, columna.Ancho, fila.Alto), columna.Alineacion, columna.TamanoFuente, columna.Prefijo, columna.PrefijoAncho, null);
                    report.Filled++; any = true;
                }
                if (!any) report.NoData.Add(label + " ← " + columna.Fuente);
            }
            else
            {
                // Clave simple: un solo valor para la primera fila.
                string? raw = source.Resolve(columna.Fuente);
                if (raw is null || tabla.Filas.Count == 0) { report.NoData.Add(label + " ← " + columna.Fuente); continue; }
                if (raw.Trim().Length == 0) continue;
                FilaMapa fila = tabla.Filas[0];
                DrawCell(gfx, FormatSource.Format(raw, columna.Tipo), new XRect(columna.X, fila.Y, columna.Ancho, fila.Alto), columna.Alineacion, columna.TamanoFuente, columna.Prefijo, columna.PrefijoAncho, null);
                report.Filled++;
            }
        }
    }

    // ---------- dibujo de texto ----------

    private static XFont Font(double size) => new(FontName, size, XFontStyleEx.Regular);

    private static void DrawCell(XGraphics gfx, string text, XRect cell, string alignment, double fontSize, string? prefix, double? prefixWidth, double? rotation)
    {
        if (rotation is double angle && Math.Abs(angle) > 0.01)
        {
            DrawRotated(gfx, text, cell, fontSize, angle);
            return;
        }
        double left = cell.X + Padding, width = cell.Width - Padding * 2;
        // El texto impreso antes del valor (ej. "Ø") ya está en el PDF: se deja su espacio.
        if (!string.IsNullOrEmpty(prefix))
        {
            double used = prefixWidth ?? Math.Max(gfx.MeasureString(prefix, Font(fontSize)).Width, fontSize * 1.3) + 3;
            left += used; width -= used;
        }
        if (width <= 1) return;

        double size = Math.Max(fontSize, MinFontSize);
        XFont font = Font(size);
        while (size > MinFontSize && gfx.MeasureString(text, font).Width > width) { size -= 0.5; font = Font(size); }

        var area = new XRect(left, cell.Y, width, cell.Height);
        XStringFormat format = alignment switch
        {
            Alineacion.Centro => XStringFormats.Center,
            Alineacion.Derecha => XStringFormats.CenterRight,
            _ => XStringFormats.CenterLeft,
        };
        gfx.DrawString(text, font, XBrushes.Black, area, format);
    }

    // Texto girado dentro de la celda (ej. "CALICHE" vertical): el largo disponible es el alto de la celda y el
    // texto se centra. El ángulo es contra el reloj, así que 90 se lee de abajo hacia arriba.
    private static void DrawRotated(XGraphics gfx, string text, XRect cell, double fontSize, double angle)
    {
        var center = new XPoint(cell.X + cell.Width / 2, cell.Y + cell.Height / 2);
        bool sideways = Math.Abs(Math.Abs(angle) % 180 - 90) < 0.5;
        double length = (sideways ? cell.Height : cell.Width) - Padding * 2;
        double thickness = sideways ? cell.Width : cell.Height;
        if (length <= 1) return;

        double size = Math.Max(fontSize, MinFontSize);
        XFont font = Font(size);
        while (size > MinFontSize && gfx.MeasureString(text, font).Width > length) { size -= 0.5; font = Font(size); }

        XGraphicsState state = gfx.Save();
        gfx.RotateAtTransform(-angle, center);
        gfx.DrawString(text, font, XBrushes.Black, new XRect(center.X - length / 2, center.Y - thickness / 2, length, thickness), XStringFormats.Center);
        gfx.Restore(state);
    }

    // Párrafo en varios renglones: cada renglón del mapa (o cada alto de línea) recibe un tramo del texto.
    private static void DrawWrapped(XGraphics gfx, string text, CampoMapa campo, double fontSize, FillReport report)
    {
        double size = Math.Max(fontSize, MinFontSize);
        XFont font = Font(size);
        var lines = campo.Lineas is { Count: > 0 }
            ? campo.Lineas.Select(l => new XRect(l.X, l.Y, l.Ancho, l.Alto)).ToList()
            : Enumerable.Range(0, Math.Max(1, (int)(campo.Alto / (size * 1.25))))
                .Select(i => new XRect(campo.X, campo.Y + i * size * 1.25, campo.Ancho, size * 1.25)).ToList();

        // Cada línea del texto (separadas por salto de línea) empieza un renglón nuevo; las largas se parten por palabras.
        var paragraphs = new Queue<Queue<string>>(text.Split('\n')
            .Select(p => new Queue<string>(p.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))).Where(q => q.Count > 0));
        int lineIndex = 0;
        while (paragraphs.Count > 0 && lineIndex < lines.Count)
        {
            Queue<string> words = paragraphs.Peek();
            XRect line = lines[lineIndex++];
            double width = line.Width - Padding * 2;
            string current = "";
            while (words.Count > 0)
            {
                string candidate = current.Length == 0 ? words.Peek() : current + " " + words.Peek();
                if (gfx.MeasureString(candidate, font).Width > width && current.Length > 0) break;
                current = candidate; words.Dequeue();
            }
            gfx.DrawString(current, font, XBrushes.Black, new XRect(line.X + Padding, line.Y, width, line.Height), XStringFormats.CenterLeft);
            if (words.Count == 0) paragraphs.Dequeue();
        }
        if (paragraphs.Count > 0) report.Warnings.Add($"{campo.Id}: el texto no cabe en el cuadro; se cortó.");
    }
}
