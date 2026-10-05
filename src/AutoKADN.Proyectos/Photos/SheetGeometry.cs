using System.Globalization;
using System.Xml.Linq;

namespace AutoKADN.Proyectos.Photos;

/// <summary>
/// Medidas de la hoja del formato de fotos en píxeles (96 dpi), leídas de la propia plantilla: ancho de las
/// columnas y alto de las 51 filas de una página. Sirve para convertir una caja en píxeles al anclaje
/// (columna, fila y desplazamientos) que usa Excel. Con Calibri 11, cada dígito mide 7 px.
/// </summary>
internal sealed class SheetGeometry
{
    public const int PageRows = 51;
    private const long EmuPerPixel = 9525;
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private readonly double[] _colPx;
    private readonly double[] _pageRowPx = new double[PageRows];

    private SheetGeometry(XDocument sheet)
    {
        XElement format = sheet.Root!.Element(Main + "sheetFormatPr")!;
        double defaultColWidth = Number((string?)format.Attribute("defaultColWidth"), 9.140625);
        double defaultRowHeight = Number((string?)format.Attribute("defaultRowHeight"), 15);

        _colPx = new double[64];
        for (int i = 0; i < _colPx.Length; i++) _colPx[i] = ColumnPixels(defaultColWidth);
        foreach (XElement col in sheet.Root.Element(Main + "cols")?.Elements(Main + "col") ?? Enumerable.Empty<XElement>())
        {
            int min = (int)col.Attribute("min")!, max = (int)col.Attribute("max")!;
            double px = ColumnPixels(Number((string?)col.Attribute("width"), defaultColWidth));
            for (int c = min; c <= max && c <= _colPx.Length; c++) _colPx[c - 1] = px;
        }

        for (int i = 0; i < PageRows; i++) _pageRowPx[i] = defaultRowHeight * 96.0 / 72.0;
        foreach (XElement row in sheet.Root.Element(Main + "sheetData")!.Elements(Main + "row"))
        {
            int r = (int)row.Attribute("r")!;
            string? ht = (string?)row.Attribute("ht");
            if (r >= 1 && r <= PageRows && ht is not null) _pageRowPx[r - 1] = Number(ht, defaultRowHeight) * 96.0 / 72.0;
        }
        PageHeightPx = _pageRowPx.Sum();
    }

    public static SheetGeometry FromSheet(XDocument sheet) => new(sheet);

    /// <summary>Alto de una página (las 51 filas) en píxeles.</summary>
    public double PageHeightPx { get; }

    public double RowPx(int row) => _pageRowPx[row % PageRows];

    /// <summary>Esquina de una caja (x, y absolutos en píxeles desde A1) como anclaje de Excel.</summary>
    public (int Col, long ColOff, int Row, long RowOff) ToAnchor(double x, double y)
    {
        int col = 0;
        double left = 0;
        while (col < _colPx.Length - 1 && left + _colPx[col] <= x) { left += _colPx[col]; col++; }

        int row = 0;
        double top = 0;
        while (top + RowPx(row) <= y) { top += RowPx(row); row++; }

        return (col, (long)Math.Round((x - left) * EmuPerPixel), row, (long)Math.Round((y - top) * EmuPerPixel));
    }

    private static double ColumnPixels(double width) => Math.Truncate((256 * width + Math.Truncate(128.0 / 7)) / 256 * 7);

    private static double Number(string? text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : fallback;
}
