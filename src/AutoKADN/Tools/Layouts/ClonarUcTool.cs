using AutoKADN.Core;
using AutoKADN.Tools.Acotado;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using System.Text.RegularExpressions;

namespace AutoKADN.Tools.Layouts;

// Clona el dibujo del área de plano de un layout DETALLE al layout UC que le corresponde
// (ANILLO N DETALLE -> ANILLO N UC, TRONCAL DETALLE -> TRONCAL UC). El área de plano es un marco
// fijo de la plantilla (coordenadas de papel): en el UC queda 9.75 más arriba que en el DETALLE,
// así que el clon es solo un desplazamiento vertical. Solo en el clon (el DETALLE no se toca):
// las cotas de longitud (COTA_x) pasan a cotas UC (capa UC_x + XData UC_SURFACE con terreno por
// defecto) y las demás cotas (magenta, etc.) y lo que lleva XData AUTOKADN (material de prueba,
// espiral, actividades) no se copian: eso vive únicamente en DETALLE. De los bloques de la capa Mat
// solo se clonan tapones, válvulas y silletas, sin XData (solo valor visual: en el UC no se cuentan).
public sealed class ClonarUcTool
{
    private const string DefaultSurface = "ZONA VERDE";
    internal const double FrameMinX = 4.50;
    internal const double FrameMaxX = 210.99;
    internal const double DetalleFrameMinY = 92.22;
    internal const double DetalleFrameMaxY = 224.02;
    internal const double UcFrameMinY = 101.97;
    internal const double UcFrameMaxY = 233.77;
    // Los bordes del marco quedan justo en el límite; el margen los deja fuera de la selección.
    private const double FrameInset = 0.05;
    private const string XDataAppName = "AUTOKADN";
    private const string MaterialBlocksLayer = "Mat";
    private static readonly string[] VisualMaterialNames = { "TAPON", "VALVULA", "SILLETA" };

    private static readonly string[] TitleBlockLayers = { "MARQUILLA", "Logo", "Textos" };
    private static readonly Regex DetailPattern = new Regex(@"^(?:ANILLO\s+(\d+)|(TRONCAL))\s+DETALLE$", RegexOptions.IgnoreCase);
    private static readonly Regex UcPattern = new Regex(@"^(?:ANILLO\s+(\d+)|(TRONCAL))\s+UC$", RegexOptions.IgnoreCase);
    private static readonly Regex LengthLayerPattern = new Regex(@"^COTA_(.+)$", RegexOptions.IgnoreCase);

    public void Run()
    {
        var document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (document is null) return;
        Editor editor = document.Editor;
        Database database = document.Database;

        if (database.TileMode)
        {
            editor.WriteMessage("\n[CLONARUC] Este comando solo funciona en un layout DETALLE.\n");
            return;
        }

        using Transaction transaction = database.TransactionManager.StartTransaction();

        ObjectId currentLayoutId = LayoutManager.Current.GetLayoutId(LayoutManager.Current.CurrentLayout);
        var detalleLayout = (Layout)transaction.GetObject(currentLayoutId, OpenMode.ForRead);
        Match detailMatch = DetailPattern.Match(detalleLayout.LayoutName.Trim());
        if (!detailMatch.Success)
        {
            editor.WriteMessage($"\n[CLONARUC] Este comando solo funciona en un layout DETALLE (ANILLO N DETALLE / TRONCAL DETALLE). Layout actual: '{detalleLayout.LayoutName}'.\n");
            return;
        }

        if (database.CurrentSpaceId != detalleLayout.BlockTableRecordId)
        {
            editor.WriteMessage("\n[CLONARUC] Está dentro de un viewport (espacio modelo). Salga al espacio papel del layout e intente de nuevo.\n");
            return;
        }

        Layout? ucLayout = FindUcLayout(transaction, database, detailMatch);
        if (ucLayout is null)
        {
            string expected = detailMatch.Groups[2].Success ? "TRONCAL UC" : "ANILLO " + int.Parse(detailMatch.Groups[1].Value) + " UC";
            editor.WriteMessage($"\n[CLONARUC] No se encontró el layout '{expected}' que corresponde a '{detalleLayout.LayoutName}'.\n");
            return;
        }

        var source = (BlockTableRecord)transaction.GetObject(detalleLayout.BlockTableRecordId, OpenMode.ForRead);
        var target = (BlockTableRecord)transaction.GetObject(ucLayout.BlockTableRecordId, OpenMode.ForWrite);

        var layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        var toClone = new ObjectIdCollection();
        var ucLayerBySource = new Dictionary<ObjectId, ObjectId>();
        var missingUcLayers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var visualBlocks = new HashSet<ObjectId>();
        int omitted = 0;
        foreach (ObjectId id in source)
        {
            if (transaction.GetObject(id, OpenMode.ForRead) is not Entity entity) continue;
            if (!IsInsideFrame(entity, DetalleFrameMinY, DetalleFrameMaxY)) continue;

            if (entity is Dimension)
            {
                string? ucLayerName = GetUcLayerName(entity.Layer);
                if (ucLayerName is null) { omitted++; continue; }
                if (!layerTable.Has(ucLayerName)) { missingUcLayers.Add(ucLayerName); continue; }
                ucLayerBySource[id] = layerTable[ucLayerName];
                toClone.Add(id);
                continue;
            }

            // Bloques de material (capa Mat): solo tapones, válvulas y silletas, y únicamente como dibujo.
            if (entity is BlockReference block && string.Equals(block.Layer, MaterialBlocksLayer, StringComparison.OrdinalIgnoreCase))
            {
                if (!IsVisualMaterialBlock(transaction, block)) { omitted++; continue; }
                visualBlocks.Add(id);
                toClone.Add(id);
                continue;
            }

            if (HasDetalleXData(entity)) { omitted++; continue; }
            toClone.Add(id);
        }

        if (toClone.Count == 0)
        {
            editor.WriteMessage("\n[CLONARUC] No hay objetos para clonar dentro del área del plano del DETALLE.\n");
            return;
        }

        int existing = 0;
        foreach (ObjectId id in target)
        {
            if (transaction.GetObject(id, OpenMode.ForRead) is Entity entity && IsInsideFrame(entity, UcFrameMinY, UcFrameMaxY)) existing++;
        }

        if (existing > 0)
        {
            var options = new PromptKeywordOptions($"\nEl layout '{ucLayout.LayoutName}' ya tiene {existing} objeto(s) en el área del plano; clonar encima los duplicaría. ¿Clonar de todas formas? [Si/No] <No>: ") { AllowNone = true };
            options.Keywords.Add("Si");
            options.Keywords.Add("No");
            options.Keywords.Default = "No";
            PromptResult answer = editor.GetKeywords(options);
            if (answer.Status != PromptStatus.OK || !answer.StringResult.Equals("Si", StringComparison.OrdinalIgnoreCase))
            {
                editor.WriteMessage("\n[CLONARUC] Cancelado. No se clonó nada.\n");
                return;
            }
        }

        var mapping = new IdMapping();
        database.DeepCloneObjects(toClone, target.ObjectId, mapping, false);

        Matrix3d move = Matrix3d.Displacement(new Vector3d(0.0, UcFrameMinY - DetalleFrameMinY, 0.0));
        int cloned = 0, converted = 0;
        foreach (IdPair pair in mapping)
        {
            if (!pair.IsPrimary || !pair.IsCloned) continue;
            if (transaction.GetObject(pair.Value, OpenMode.ForWrite) is not Entity clone) continue;
            clone.TransformBy(move);
            cloned++;

            if (visualBlocks.Contains(pair.Key))
            {
                // Solo valor visual: sin XData el clon no se cuenta como material en el UC.
                clone.XData = new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, XDataAppName));
            }

            if (clone is Dimension dimension && ucLayerBySource.TryGetValue(pair.Key, out ObjectId ucLayerId))
            {
                dimension.LayerId = ucLayerId;
                dimension.ColorIndex = 256;
                CotaTool.AssignUcSurface(database, transaction, dimension, DefaultSurface);
                converted++;
            }
        }

        transaction.Commit();
        editor.Regen();
        editor.WriteMessage($"\n[CLONARUC] {cloned} objeto(s) clonados de '{detalleLayout.LayoutName}' a '{ucLayout.LayoutName}'."
            + (converted > 0 ? $" {converted} cota(s) de longitud pasaron a cota UC (terreno {DefaultSurface})." : string.Empty)
            + (omitted > 0 ? $" Se omitieron {omitted} objeto(s) que solo van en el DETALLE (cotas magenta, materiales)." : string.Empty) + "\n");
        if (missingUcLayers.Count > 0)
            editor.WriteMessage($"[CLONARUC] No se clonaron cotas de longitud porque no existe(n) en el dibujo la(s) capa(s): {string.Join(", ", missingUcLayers)}.\n");
    }

    private static Layout? FindUcLayout(Transaction transaction, Database database, Match detailMatch)
    {
        var layouts = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry entry in layouts)
        {
            if (transaction.GetObject(entry.Value, OpenMode.ForRead) is not Layout layout) continue;
            Match ucMatch = UcPattern.Match(layout.LayoutName.Trim());
            if (ucMatch.Success && IsPair(detailMatch, ucMatch)) return layout;
        }
        return null;
    }

    private static bool IsPair(Match detail, Match uc)
    {
        if (detail.Groups[2].Success || uc.Groups[2].Success) return detail.Groups[2].Success && uc.Groups[2].Success;
        return int.Parse(detail.Groups[1].Value) == int.Parse(uc.Groups[1].Value);
    }

    // El rectángulo del área de dibujo (polilínea cerrada que coincide con el marco) es parte del formato:
    // su extensión es el marco entero, no algo dibujado dentro.
    private const double FrameMatchTolerance = 0.5;

    // Un objeto está "dentro del marco" si el centro de su extensión cae dentro (los bordes del marco
    // y el membrete quedan fuera) y no es el viewport general, parte del membrete ni el rectángulo del marco.
    internal static bool IsInsideFrame(Entity entity, double frameMinY, double frameMaxY)
    {
        if (entity is Viewport) return false;
        if (Array.Exists(TitleBlockLayers, x => string.Equals(x, entity.Layer, StringComparison.OrdinalIgnoreCase))) return false;

        Extents3d extents;
        try { extents = entity.GeometricExtents; }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return false; }

        bool isFrameRectangle = Math.Abs(extents.MinPoint.X - FrameMinX) <= FrameMatchTolerance
            && Math.Abs(extents.MaxPoint.X - FrameMaxX) <= FrameMatchTolerance
            && Math.Abs(extents.MinPoint.Y - frameMinY) <= FrameMatchTolerance
            && Math.Abs(extents.MaxPoint.Y - frameMaxY) <= FrameMatchTolerance;
        if (isFrameRectangle) return false;

        double centerX = (extents.MinPoint.X + extents.MaxPoint.X) / 2.0;
        double centerY = (extents.MinPoint.Y + extents.MaxPoint.Y) / 2.0;
        return centerX > FrameMinX + FrameInset && centerX < FrameMaxX - FrameInset
            && centerY > frameMinY + FrameInset && centerY < frameMaxY - FrameInset;
    }

    // Capa de cota de longitud (COTA_1-2, COTA_3-4, COTA_2, ...) -> capa UC del mismo diámetro (UC_x).
    // Devuelve null si la cota no es de longitud (por ejemplo las de la capa COTAS MAGENTA).
    private static string? GetUcLayerName(string dimensionLayer)
    {
        Match match = LengthLayerPattern.Match(dimensionLayer ?? string.Empty);
        if (!match.Success) return null;
        string ucLayerName = "UC_" + match.Groups[1].Value;
        return Naming.UcLayerDiameters.ContainsKey(ucLayerName) ? ucLayerName : null;
    }

    // Tapón, válvula o silleta (el nombre de la definición del bloque, sin acentos ni mayúsculas).
    private static bool IsVisualMaterialBlock(Transaction transaction, BlockReference block)
    {
        ObjectId definitionId = block.BlockTableRecord;
        if (block.IsDynamicBlock && !block.DynamicBlockTableRecord.IsNull) definitionId = block.DynamicBlockTableRecord;
        if (transaction.GetObject(definitionId, OpenMode.ForRead) is not BlockTableRecord definition) return false;

        string name = RemoveDiacritics(definition.Name).ToUpperInvariant();
        return Array.Exists(VisualMaterialNames, x => name.Contains(x));
    }

    private static string RemoveDiacritics(string text)
    {
        string decomposed = text.Normalize(System.Text.NormalizationForm.FormD);
        var builder = new System.Text.StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark) builder.Append(c);
        }
        return builder.ToString();
    }

    internal static bool HasDetalleXData(Entity entity)
    {
        using ResultBuffer? xdata = entity.GetXDataForApplication(XDataAppName);
        return xdata is not null;
    }
}
