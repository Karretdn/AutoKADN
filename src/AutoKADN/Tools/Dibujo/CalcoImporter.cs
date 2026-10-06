using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AutoKADN.Tools.Dibujo;

/// <summary>Rectángulo (en coordenadas del espacio) donde tiene que caber lo que se importa.</summary>
internal readonly struct CalcoFrame
{
    public CalcoFrame(double minX, double minY, double maxX, double maxY)
    {
        MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY;
    }

    public double MinX { get; }
    public double MinY { get; }
    public double MaxX { get; }
    public double MaxY { get; }
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;
    public double CenterX => (MinX + MaxX) / 2.0;
    public double CenterY => (MinY + MaxY) / 2.0;

    public static CalcoFrame FromCorners(Point3d a, Point3d b) =>
        new CalcoFrame(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    /// <summary>Escala (sin deformar) con la que algo de ancho w y alto h cabe justo dentro de este rectángulo.</summary>
    public double FitScale(double w, double h) => Math.Min(Width / w, Height / h);

    /// <summary>Parte común con otro rectángulo (puede salir vacía: ancho o alto menor o igual a cero).</summary>
    public CalcoFrame Intersect(CalcoFrame other) =>
        new CalcoFrame(Math.Max(MinX, other.MinX), Math.Max(MinY, other.MinY), Math.Min(MaxX, other.MaxX), Math.Min(MaxY, other.MaxY));

    public bool IsEmpty(double minSize) => Width <= minSize || Height <= minSize;
}

/// <summary>Lo que quedó importado: la imagen o el PDF y el rectángulo que ocupa. Si hubo un problema, Error lo explica.</summary>
internal sealed class CalcoImport
{
    public ObjectId EntityId;
    public bool IsPdf;
    public CalcoFrame Content;
    public string Error = string.Empty;
    public bool Ok => Error.Length == 0 && !EntityId.IsNull;
}

// Pone una imagen o una página de PDF dentro de un rectángulo, lista para calcar: queda en la capa CALCO (que no se
// imprime), al fondo del orden de dibujo y con transparencia. La imagen o el PDF se adjuntan como referencia
// (no se incrustan). Recortar es esconder lo demás con el borde de recorte de AutoCAD (como IMAGECLIP o PDFCLIP);
// lo que queda a la vista se reajusta para llenar el rectángulo.
internal static class CalcoImporter
{
    public const string LayerName = "CALCO";
    /// <summary>Qué tan transparente queda (0 = opaco, 100 = invisible).</summary>
    public const int TransparencyPercent = 50;

    private const double MinSize = 1e-6;
    private const int GrayColorIndex = 8;

    public static bool IsPdf(string path) => string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Adjunta el archivo completo dentro del rectángulo (centrado y sin deformar). Para PDF, page es la página (desde 1).
    /// </summary>
    public static CalcoImport Attach(Database database, ObjectId spaceId, string path, int page, CalcoFrame frame)
    {
        var result = new CalcoImport { IsPdf = IsPdf(path) };
        using (Transaction transaction = database.TransactionManager.StartTransaction())
        {
            try
            {
                var space = (BlockTableRecord)transaction.GetObject(spaceId, OpenMode.ForWrite);
                Entity entity = result.IsPdf
                    ? AttachPdf(database, transaction, space, path, page, frame)
                    : AttachImage(database, transaction, space, path, frame);
                Style(database, transaction, space, entity);
                result.EntityId = entity.ObjectId;
                result.Content = ExtentsOf(entity);
                transaction.Commit();
            }
            catch (System.Exception ex)
            {
                result.Error = Explain(ex, result.IsPdf); // sin Commit la transacción se descarta: no queda nada a medias
            }
        }
        return result;
    }

    /// <summary>
    /// Deja a la vista solo el rectángulo indicado (la parte que cae dentro de lo importado) y reajusta lo que queda
    /// para que llene el marco. Devuelve "" si salió bien o el motivo si no.
    /// </summary>
    public static string Crop(Database database, ObjectId entityId, CalcoFrame crop, CalcoFrame frame)
    {
        using (Transaction transaction = database.TransactionManager.StartTransaction())
        {
            try
            {
                var entity = (Entity)transaction.GetObject(entityId, OpenMode.ForWrite);
                CalcoFrame visible = crop.Intersect(ExtentsOf(entity));
                if (visible.IsEmpty(MinSize)) return "el rectángulo quedó fuera de la imagen";

                var image = entity as RasterImage;
                var underlay = entity as UnderlayReference;
                if (image != null) ClipImage(image, visible);
                else if (underlay != null) ClipUnderlay(transaction, underlay, visible);
                else return "no es una imagen ni un PDF";

                // Lo que quedó a la vista llena el marco: se escala desde su centro y se lleva al centro del marco.
                double k = frame.FitScale(visible.Width, visible.Height);
                var center = new Point3d(visible.CenterX, visible.CenterY, 0.0);
                Matrix3d fit = Matrix3d.Displacement(new Vector3d(frame.CenterX - visible.CenterX, frame.CenterY - visible.CenterY, 0.0))
                               * Matrix3d.Scaling(k, center);
                entity.TransformBy(fit);
                transaction.Commit();
            }
            catch (System.Exception ex)
            {
                return Explain(ex, entityId.ObjectClass.Name.IndexOf("Pdf", StringComparison.OrdinalIgnoreCase) >= 0);
            }
        }
        return string.Empty;
    }

    /// <summary>Borra lo importado (y su definición) si nadie más la usa: para cuando se cancela a la mitad.</summary>
    public static void Remove(Database database, ObjectId entityId)
    {
        using (Transaction transaction = database.TransactionManager.StartTransaction())
        {
            try
            {
                var entity = (Entity)transaction.GetObject(entityId, OpenMode.ForWrite);
                ObjectId definitionId = entity is RasterImage image ? image.ImageDefId
                    : entity is UnderlayReference underlay ? underlay.DefinitionId : ObjectId.Null;
                entity.Erase();
                if (!definitionId.IsNull)
                {
                    var definition = (DBObject)transaction.GetObject(definitionId, OpenMode.ForWrite);
                    if (!(definition is RasterImageDef rasterDef) || rasterDef.GetEntityCount(out _) == 0) definition.Erase();
                }
                transaction.Commit();
            }
            catch (System.Exception)
            {
                // Si no se pudo limpiar, queda a la vista y la persona la borra: no vale la pena fallar por eso.
            }
        }
    }

    /// <summary>
    /// Páginas de un PDF, leyendo el archivo sin abrirlo con ninguna librería. 0 = no se pudo saber (por ejemplo, las
    /// páginas van dentro de flujos comprimidos): en ese caso hay que preguntar la página.
    /// </summary>
    public static int CountPdfPages(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 40L * 1024 * 1024) return 0; // el archivo se lee entero: si es muy grande, mejor preguntar la página
            string text = Encoding.GetEncoding("ISO-8859-1").GetString(File.ReadAllBytes(path));
            if (text.IndexOf("/ObjStm", StringComparison.Ordinal) >= 0) return 0;
            return Regex.Matches(text, @"/Type\s*/Page(?![A-Za-z])").Count;
        }
        catch (System.Exception)
        {
            return 0;
        }
    }

    // ---------- imagen ----------

    private static Entity AttachImage(Database database, Transaction transaction, BlockTableRecord space, string path, CalcoFrame frame)
    {
        ObjectId dictionaryId = RasterImageDef.GetImageDictionary(database);
        if (dictionaryId.IsNull) dictionaryId = RasterImageDef.CreateImageDictionary(database);
        var dictionary = (DBDictionary)transaction.GetObject(dictionaryId, OpenMode.ForWrite);

        var definition = new RasterImageDef { SourceFileName = path };
        definition.Load();
        Vector2d pixels = definition.Size;
        if (pixels.X < 1.0 || pixels.Y < 1.0) throw new InvalidOperationException("la imagen no tiene tamaño");
        ObjectId definitionId = dictionary.SetAt(UniqueKey(dictionary, Path.GetFileNameWithoutExtension(path)), definition);
        transaction.AddNewlyCreatedDBObject(definition, true);

        // La imagen entera cabe justo en el marco, centrada. Los vectores de Orientation miden la imagen completa
        // (ancho y alto), no un píxel.
        double scale = frame.FitScale(pixels.X, pixels.Y);
        double width = pixels.X * scale, height = pixels.Y * scale;
        var origin = new Point3d(frame.CenterX - width / 2.0, frame.CenterY - height / 2.0, 0.0);
        var image = new RasterImage();
        image.SetDatabaseDefaults(database);
        image.ImageDefId = definitionId;
        image.Orientation = new CoordinateSystem3d(origin, new Vector3d(width, 0.0, 0.0), new Vector3d(0.0, height, 0.0));
        image.ShowImage = true;
        space.AppendEntity(image);
        transaction.AddNewlyCreatedDBObject(image, true);
        RasterImage.EnableReactors(true);
        image.AssociateRasterDef(definition);
        return image;
    }

    private static void ClipImage(RasterImage image, CalcoFrame visible)
    {
        // El borde de recorte de una imagen va en píxeles; PixelToModelTransform hace la conversión exacta.
        Matrix3d toPixel = image.PixelToModelTransform.Inverse();
        double z = image.Position.Z;
        Point3d a = new Point3d(visible.MinX, visible.MinY, z).TransformBy(toPixel);
        Point3d b = new Point3d(visible.MaxX, visible.MaxY, z).TransformBy(toPixel);
        double x0 = Math.Min(a.X, b.X), x1 = Math.Max(a.X, b.X), y0 = Math.Min(a.Y, b.Y), y1 = Math.Max(a.Y, b.Y);
        // AutoCAD pide el rectángulo como polígono cerrado (el primer punto se repite al final); con dos esquinas da eInvalidInput.
        var corners = new Point2dCollection
        {
            new Point2d(x0, y0), new Point2d(x0, y1), new Point2d(x1, y1), new Point2d(x1, y0), new Point2d(x0, y0),
        };
        image.SetClipBoundary(ClipBoundaryType.Rectangle, corners);
    }

    // ---------- PDF ----------

    private static Entity AttachPdf(Database database, Transaction transaction, BlockTableRecord space, string path, int page, CalcoFrame frame)
    {
        string dictionaryKey = UnderlayDefinition.GetDictionaryKey(typeof(PdfReference));
        var named = (DBDictionary)transaction.GetObject(database.NamedObjectsDictionaryId, OpenMode.ForRead);
        ObjectId dictionaryId;
        if (named.Contains(dictionaryKey)) dictionaryId = named.GetAt(dictionaryKey);
        else
        {
            named.UpgradeOpen();
            var created = new DBDictionary();
            dictionaryId = named.SetAt(dictionaryKey, created);
            transaction.AddNewlyCreatedDBObject(created, true);
        }
        var dictionary = (DBDictionary)transaction.GetObject(dictionaryId, OpenMode.ForWrite);

        var definition = new PdfDefinition { SourceFileName = path, ItemName = page.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        ObjectId definitionId = dictionary.SetAt(UniqueKey(dictionary, Path.GetFileNameWithoutExtension(path) + " - " + page), definition);
        transaction.AddNewlyCreatedDBObject(definition, true);

        var pdf = new PdfReference { DefinitionId = definitionId, Position = Point3d.Origin, ScaleFactors = new Scale3d(1.0), Rotation = 0.0 };
        pdf.SetDatabaseDefaults(database);
        space.AppendEntity(pdf);
        transaction.AddNewlyCreatedDBObject(pdf, true);

        try { pdf.IsClipped = false; } catch (System.Exception) { /* sin borde de recorte todavía: se ve la página entera */ }

        // Tamaño natural de la página (escala 1); de ahí sale la escala que la deja justo en el marco.
        CalcoFrame natural = ExtentsOf(pdf);
        if (natural.Width < MinSize || natural.Height < MinSize) throw new InvalidOperationException("no se pudo leer la página " + page + " del PDF");
        pdf.ScaleFactors = new Scale3d(frame.FitScale(natural.Width, natural.Height));
        CenterIn(pdf, frame);
        return pdf;
    }

    private static void ClipUnderlay(Transaction transaction, UnderlayReference underlay, CalcoFrame visible)
    {
        // AutoCAD arma el borde de recorte del PDF a partir de una polilínea cerrada dibujada sobre él (la conversión
        // a las coordenadas del PDF la hace AutoCAD); la polilínea es solo un medio y se borra enseguida.
        var space = (BlockTableRecord)transaction.GetObject(underlay.BlockId, OpenMode.ForWrite);
        var rectangle = new Polyline();
        rectangle.AddVertexAt(0, new Point2d(visible.MinX, visible.MinY), 0.0, 0.0, 0.0);
        rectangle.AddVertexAt(1, new Point2d(visible.MaxX, visible.MinY), 0.0, 0.0, 0.0);
        rectangle.AddVertexAt(2, new Point2d(visible.MaxX, visible.MaxY), 0.0, 0.0, 0.0);
        rectangle.AddVertexAt(3, new Point2d(visible.MinX, visible.MaxY), 0.0, 0.0, 0.0);
        rectangle.Closed = true;
        rectangle.Elevation = underlay.Position.Z;
        space.AppendEntity(rectangle);
        transaction.AddNewlyCreatedDBObject(rectangle, true);
        try
        {
            underlay.GenerateClipBoundaryFromPline(rectangle.ObjectId);
            underlay.IsClipped = true;
        }
        finally { rectangle.Erase(); }
    }

    // ---------- común ----------

    // Lo deja centrado en el marco, sea cual sea el punto de inserción del PDF.
    private static void CenterIn(Entity entity, CalcoFrame frame)
    {
        CalcoFrame now = ExtentsOf(entity);
        entity.TransformBy(Matrix3d.Displacement(new Vector3d(frame.CenterX - now.CenterX, frame.CenterY - now.CenterY, 0.0)));
    }

    private static CalcoFrame ExtentsOf(Entity entity)
    {
        Extents3d extents = entity.GeometricExtents;
        return new CalcoFrame(extents.MinPoint.X, extents.MinPoint.Y, extents.MaxPoint.X, extents.MaxPoint.Y);
    }

    // Capa CALCO, al fondo del orden de dibujo y con transparencia: así el trazo nuevo queda encima y se ve claro.
    private static void Style(Database database, Transaction transaction, BlockTableRecord space, Entity entity)
    {
        entity.LayerId = EnsureLayer(database, transaction);
        try { entity.Transparency = new Transparency((byte)Math.Round(255.0 * (100 - TransparencyPercent) / 100.0)); }
        catch (System.Exception) { /* sin transparencia de objeto: queda el desvanecido propio de la imagen/PDF */ }

        var drawOrder = (DrawOrderTable)transaction.GetObject(space.DrawOrderTableId, OpenMode.ForWrite);
        drawOrder.MoveToBottom(new ObjectIdCollection(new[] { entity.ObjectId }));
    }

    private static ObjectId EnsureLayer(Database database, Transaction transaction)
    {
        var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        if (layers.Has(LayerName)) return layers[LayerName];

        layers.UpgradeOpen();
        var layer = new LayerTableRecord
        {
            Name = LayerName,
            Color = Color.FromColorIndex(ColorMethod.ByAci, GrayColorIndex),
            IsPlottable = false, // es solo la referencia para calcar: no sale en el plano impreso
        };
        ObjectId id = layers.Add(layer);
        transaction.AddNewlyCreatedDBObject(layer, true);
        return id;
    }

    // AutoCAD responde con códigos (eInvalidInput, eFileAccessErr...): se traducen a algo que se pueda entender.
    private static string Explain(System.Exception ex, bool pdf)
    {
        var autocad = ex as Autodesk.AutoCAD.Runtime.Exception;
        if (autocad == null) return ex.Message;
        switch (autocad.ErrorStatus)
        {
            case Autodesk.AutoCAD.Runtime.ErrorStatus.FileAccessErr:
                return "no se pudo abrir el archivo (¿está en uso o sin permiso?)";
            case Autodesk.AutoCAD.Runtime.ErrorStatus.OutOfRange:
                return "esa página no existe en el PDF";
            case Autodesk.AutoCAD.Runtime.ErrorStatus.InvalidInput:
            case Autodesk.AutoCAD.Runtime.ErrorStatus.FilerError:
                return pdf ? "no se pudo leer el PDF (¿está dañado o protegido con contraseña?)"
                           : "AutoCAD no pudo leer esa imagen (formato no compatible o archivo dañado)";
            default:
                return ex.Message;
        }
    }

    private static string UniqueKey(DBDictionary dictionary, string baseName)
    {
        string name = string.IsNullOrWhiteSpace(baseName) ? "CALCO" : baseName.Trim();
        if (!dictionary.Contains(name)) return name;
        for (int i = 2; ; i++)
        {
            string candidate = name + "_" + i;
            if (!dictionary.Contains(candidate)) return candidate;
        }
    }
}
