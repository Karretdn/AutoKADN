using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace AutoKADN.Proyectos.Photos;

/// <summary>Foto lista para el Excel: ya orientada, reducida y codificada como JPEG.</summary>
public sealed record PreparedPhoto(string Name, byte[] Jpeg, int Width, int Height);

public sealed record PhotoBookResult(int Photos, int[] PhotosPerPage);

/// <summary>
/// Arma el registro fotográfico en Excel a partir de la plantilla FORMATO FOTOS.xlsx, editando directamente su
/// XML: repite la página (logo y título "REGISTRO FOTOGRAFICO") tantas veces como haga falta, con salto de
/// página entre ellas, y coloca cada foto en su caja según PhotoLayouts. Como máximo 9 fotos por página; con
/// más fotos se generan más páginas en la misma hoja.
/// </summary>
public static class PhotoBookWriter
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Types = "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly XNamespace Xdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string ImageRelType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image";
    private const long EmuPerPixel = 9525;
    private const string SheetPath = "xl/worksheets/sheet1.xml";
    private const string DrawingPath = "xl/drawings/drawing1.xml";
    private const string DrawingRelsPath = "xl/drawings/_rels/drawing1.xml.rels";

    public static PhotoBookResult Write(string templatePath, string outputPath, IReadOnlyList<PreparedPhoto> photos)
    {
        if (photos.Count == 0) throw new ArgumentException("No hay fotos para colocar.", nameof(photos));
        int[] plan = PhotoLayouts.PlanPages(photos.Count);

        using var buffer = new MemoryStream(); // expandable: las fotos agrandan el archivo
        byte[] template = File.ReadAllBytes(templatePath);
        buffer.Write(template, 0, template.Length);
        buffer.Position = 0;
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Update, leaveOpen: true))
        {
            XDocument sheet = ReadXml(zip, SheetPath);
            SheetGeometry geometry = SheetGeometry.FromSheet(sheet);

            BuildSheet(sheet, plan.Length);
            WriteXml(zip, SheetPath, sheet);

            XDocument drawing = ReadXml(zip, DrawingPath);
            XDocument drawingRels = ReadXml(zip, DrawingRelsPath);
            BuildDrawing(zip, drawing, drawingRels, geometry, plan, photos);
            WriteXml(zip, DrawingPath, drawing);
            WriteXml(zip, DrawingRelsPath, drawingRels);

            EnsureJpegContentType(zip);
        }

        // Se escribe a un temporal y se reemplaza al final: nunca queda un archivo a medias.
        string temp = outputPath + ".tmp";
        File.WriteAllBytes(temp, buffer.ToArray());
        File.Move(temp, outputPath, overwrite: true);
        return new PhotoBookResult(photos.Count, plan);
    }

    // ── Hoja: una página de 51 filas por cada grupo de fotos ─────────────────────────────────────
    private static void BuildSheet(XDocument sheet, int pages)
    {
        XElement root = sheet.Root!;
        XElement sheetData = root.Element(Main + "sheetData")!;

        // La plantilla trae filas sobrantes después de la página 1: se descartan y se regeneran.
        List<XElement> templateRows = sheetData.Elements(Main + "row").Where(r => (int)r.Attribute("r")! <= SheetGeometry.PageRows).ToList();
        foreach (XElement extra in sheetData.Elements(Main + "row").Where(r => (int)r.Attribute("r")! > SheetGeometry.PageRows).ToList()) extra.Remove();

        for (int page = 1; page < pages; page++)
        {
            int shift = SheetGeometry.PageRows * page;
            foreach (XElement source in templateRows)
            {
                var clone = new XElement(source);
                clone.SetAttributeValue("r", (int)source.Attribute("r")! + shift);
                foreach (XElement cell in clone.Elements(Main + "c")) cell.SetAttributeValue("r", ShiftReference((string)cell.Attribute("r")!, shift));
                sheetData.Add(clone);
            }
        }

        XElement merges = root.Element(Main + "mergeCells") ?? throw new InvalidDataException("La plantilla no tiene el título combinado.");
        List<string> baseMerges = merges.Elements(Main + "mergeCell").Select(m => (string)m.Attribute("ref")!).ToList();
        for (int page = 1; page < pages; page++)
            foreach (string reference in baseMerges)
                merges.Add(new XElement(Main + "mergeCell", new XAttribute("ref", ShiftRange(reference, SheetGeometry.PageRows * page))));
        merges.SetAttributeValue("count", merges.Elements(Main + "mergeCell").Count());

        root.Element(Main + "dimension")?.SetAttributeValue("ref", $"A5:N{SheetGeometry.PageRows * pages}");

        root.Element(Main + "rowBreaks")?.Remove();
        if (pages > 1)
        {
            var breaks = new XElement(Main + "rowBreaks",
                new XAttribute("count", pages - 1), new XAttribute("manualBreakCount", pages - 1));
            for (int page = 1; page < pages; page++)
                breaks.Add(new XElement(Main + "brk", new XAttribute("id", SheetGeometry.PageRows * page), new XAttribute("max", 16383), new XAttribute("man", 1)));
            // Orden del esquema: ... pageSetup, headerFooter, rowBreaks ... drawing
            root.Element(Main + "drawing")!.AddBeforeSelf(breaks);
        }
    }

    // ── Dibujo: logo en cada página y las fotos en sus cajas ─────────────────────────────────────
    private static void BuildDrawing(ZipArchive zip, XDocument drawing, XDocument rels, SheetGeometry geometry,
        int[] plan, IReadOnlyList<PreparedPhoto> photos)
    {
        XElement root = drawing.Root!;
        XElement logo = root.Elements(Xdr + "twoCellAnchor").First();
        string logoRelId = (string)logo.Descendants(A + "blip").First().Attribute(Rel + "embed")!;

        int nextId = 1;
        foreach (XElement props in root.Descendants(Xdr + "cNvPr")) nextId = Math.Max(nextId, ((int?)props.Attribute("id") ?? 0) + 1);

        // Logo y título en cada página: copias del anclaje de la plantilla desplazadas 51 filas por página.
        for (int page = 1; page < plan.Length; page++)
        {
            var copy = new XElement(logo);
            int shift = SheetGeometry.PageRows * page;
            foreach (XElement row in copy.Descendants(Xdr + "row")) row.Value = ((int)row + shift).ToString(CultureInfo.InvariantCulture);
            XElement props = copy.Descendants(Xdr + "cNvPr").First();
            props.SetAttributeValue("id", nextId++);
            props.SetAttributeValue("name", $"Logo {page + 1}");
            props.Elements(A + "extLst").Remove(); // evita identificadores de creación repetidos
            XElement? offset = copy.Descendants(A + "off").FirstOrDefault();
            if (offset is not null) offset.SetAttributeValue("y", (long)offset.Attribute("y")! + (long)Math.Round(geometry.PageHeightPx * EmuPerPixel * page));
            root.Add(copy);
        }

        int relNumber = rels.Root!.Elements(PackageRel + "Relationship")
            .Select(r => int.TryParse(((string)r.Attribute("Id")!).Replace("rId", string.Empty), out int n) ? n : 0).DefaultIfEmpty(1).Max();

        int photoIndex = 0;
        for (int page = 0; page < plan.Length; page++)
        {
            IReadOnlyList<Box> boxes = PhotoLayouts.For(plan[page]);
            double pageTop = geometry.PageHeightPx * page;
            for (int slot = 0; slot < boxes.Count; slot++, photoIndex++)
            {
                PreparedPhoto photo = photos[photoIndex];
                Placement placement = PhotoFit.Place(boxes[slot], photo.Width, photo.Height);

                string mediaName = $"foto{photoIndex + 1}.jpeg";
                ZipArchiveEntry media = zip.CreateEntry("xl/media/" + mediaName, CompressionLevel.NoCompression); // el JPEG ya está comprimido
                using (Stream stream = media.Open()) stream.Write(photo.Jpeg, 0, photo.Jpeg.Length);

                string relId = "rId" + (++relNumber);
                rels.Root.Add(new XElement(PackageRel + "Relationship",
                    new XAttribute("Id", relId), new XAttribute("Type", ImageRelType), new XAttribute("Target", "../media/" + mediaName)));

                root.Add(PhotoAnchor(geometry, nextId++, photoIndex + 1, relId, placement, pageTop));
            }
        }

        _ = logoRelId; // el logo conserva su relación original (rId1)
    }

    // Recorte de imagen de Excel (a:srcRect): porcentajes en milésimas de punto porcentual (100000 = 100 %).
    private static string CropValue(double fraction) => ((long)Math.Round(fraction * 100000)).ToString(CultureInfo.InvariantCulture);

    private static XElement PhotoAnchor(SheetGeometry geometry, int id, int number, string relId, Placement placement, double pageTop)
    {
        double x = placement.X, y = pageTop + placement.Y, w = placement.W, h = placement.H;
        var from = geometry.ToAnchor(x, y);
        var to = geometry.ToAnchor(x + w, y + h);
        XElement Marker(XName name, (int Col, long ColOff, int Row, long RowOff) p) => new(name,
            new XElement(Xdr + "col", p.Col), new XElement(Xdr + "colOff", p.ColOff),
            new XElement(Xdr + "row", p.Row), new XElement(Xdr + "rowOff", p.RowOff));

        return new XElement(Xdr + "twoCellAnchor", new XAttribute("editAs", "oneCell"),
            Marker(Xdr + "from", from),
            Marker(Xdr + "to", to),
            new XElement(Xdr + "pic",
                new XElement(Xdr + "nvPicPr",
                    new XElement(Xdr + "cNvPr", new XAttribute("id", id), new XAttribute("name", $"Foto {number}")),
                    new XElement(Xdr + "cNvPicPr", new XElement(A + "picLocks", new XAttribute("noChangeAspect", 1)))),
                new XElement(Xdr + "blipFill",
                    new XElement(A + "blip", new XAttribute(XNamespace.Xmlns + "r", Rel.NamespaceName), new XAttribute(Rel + "embed", relId)),
                    placement.IsCropped
                        ? new XElement(A + "srcRect",
                            new XAttribute("l", CropValue(placement.CropLeft)), new XAttribute("t", CropValue(placement.CropTop)),
                            new XAttribute("r", CropValue(placement.CropRight)), new XAttribute("b", CropValue(placement.CropBottom)))
                        : null,
                    new XElement(A + "stretch", new XElement(A + "fillRect"))),
                new XElement(Xdr + "spPr",
                    new XElement(A + "xfrm",
                        new XElement(A + "off", new XAttribute("x", (long)Math.Round(x * EmuPerPixel)), new XAttribute("y", (long)Math.Round(y * EmuPerPixel))),
                        new XElement(A + "ext", new XAttribute("cx", (long)Math.Round(w * EmuPerPixel)), new XAttribute("cy", (long)Math.Round(h * EmuPerPixel)))),
                    new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst")))),
            new XElement(Xdr + "clientData"));
    }

    private static void EnsureJpegContentType(ZipArchive zip)
    {
        XDocument types = ReadXml(zip, "[Content_Types].xml");
        bool has = types.Root!.Elements(Types + "Default")
            .Any(d => string.Equals((string?)d.Attribute("Extension"), "jpeg", StringComparison.OrdinalIgnoreCase));
        if (has) return;
        types.Root.AddFirst(new XElement(Types + "Default", new XAttribute("Extension", "jpeg"), new XAttribute("ContentType", "image/jpeg")));
        WriteXml(zip, "[Content_Types].xml", types);
    }

    // ── Utilidades ──────────────────────────────────────────────────────────────────────────────
    private static string ShiftReference(string reference, int rows)
    {
        string letters = new(reference.TakeWhile(char.IsLetter).ToArray());
        int row = int.Parse(reference.Substring(letters.Length), CultureInfo.InvariantCulture);
        return letters + (row + rows).ToString(CultureInfo.InvariantCulture);
    }

    private static string ShiftRange(string range, int rows) => string.Join(":", range.Split(':').Select(part => ShiftReference(part, rows)));

    private static XDocument ReadXml(ZipArchive zip, string path)
    {
        ZipArchiveEntry entry = zip.GetEntry(path) ?? throw new FileNotFoundException("La plantilla no tiene " + path);
        using Stream stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static void WriteXml(ZipArchive zip, string path, XDocument document)
    {
        zip.GetEntry(path)?.Delete();
        ZipArchiveEntry entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        document.Save(writer, SaveOptions.DisableFormatting);
    }
}
