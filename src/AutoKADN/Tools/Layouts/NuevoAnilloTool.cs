using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;

namespace AutoKADN.Tools.Layouts;

// Desde ANILLO N DETALLE (o ANILLO N UC) crea ANILLO N+1 DETALLE y ANILLO N+1 UC copiando esos dos layouts
// con todo su formato (cajetín, tablas, viewport, configuración de página). El número es el siguiente a
// la secuencia existente. Las copias quedan limpias: se retira el dibujo del área de plano y lo generado por
// el plugin (resúmenes, materiales, anotaciones con XData); el número "ANILLO N" de los títulos se actualiza.
public sealed class NuevoAnilloTool
{
    private static readonly Regex RingPattern = new Regex(@"^ANILLO\s+(\d+)\s+(DETALLE|UC)$", RegexOptions.IgnoreCase);
    private static readonly Regex RingText = new Regex(@"ANILLO(\s+)\d+", RegexOptions.IgnoreCase);

    public void Run()
    {
        var document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (document is null) return;
        Editor editor = document.Editor;
        Database database = document.Database;

        if (database.TileMode)
        {
            editor.WriteMessage("\n[NUEVOANILLO] Este comando funciona en un layout ANILLO N DETALLE o ANILLO N UC.\n");
            return;
        }

        LayoutManager layoutManager = LayoutManager.Current;
        string current = layoutManager.CurrentLayout;
        Match currentMatch = RingPattern.Match(current.Trim());
        if (!currentMatch.Success)
        {
            editor.WriteMessage($"\n[NUEVOANILLO] Ubícate en un layout ANILLO N DETALLE o ANILLO N UC. Layout actual: '{current}'.\n");
            return;
        }
        int sourceNumber = int.Parse(currentMatch.Groups[1].Value);

        string? sourceDetalle = null, sourceUc = null;
        int maxNumber = 0;
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (Transaction transaction = database.TransactionManager.StartTransaction())
        {
            var layouts = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in layouts)
            {
                if (transaction.GetObject(entry.Value, OpenMode.ForRead) is not Layout layout) continue;
                string name = layout.LayoutName.Trim();
                existing.Add(name);
                Match match = RingPattern.Match(name);
                if (!match.Success) continue;
                int number = int.Parse(match.Groups[1].Value);
                maxNumber = Math.Max(maxNumber, number);
                if (number != sourceNumber) continue;
                if (match.Groups[2].Value.Equals("DETALLE", StringComparison.OrdinalIgnoreCase)) sourceDetalle = layout.LayoutName;
                else sourceUc = layout.LayoutName;
            }
            transaction.Commit();
        }

        if (sourceDetalle is null || sourceUc is null)
        {
            editor.WriteMessage($"\n[NUEVOANILLO] Para copiar el anillo {sourceNumber} hacen falta sus dos layouts: ANILLO {sourceNumber} DETALLE y ANILLO {sourceNumber} UC.\n");
            return;
        }

        int next = maxNumber + 1;
        string newDetalle = $"ANILLO {next} DETALLE";
        string newUc = $"ANILLO {next} UC";
        if (existing.Contains(newDetalle) || existing.Contains(newUc))
        {
            editor.WriteMessage($"\n[NUEVOANILLO] Ya existe '{newDetalle}' o '{newUc}'.\n");
            return;
        }

        try
        {
            layoutManager.CopyLayout(sourceDetalle, newDetalle);
            layoutManager.CopyLayout(sourceUc, newUc);
        }
        catch (System.Exception ex)
        {
            editor.WriteMessage($"\n[NUEVOANILLO] No se pudieron copiar los layouts: {ex.Message}\n");
            return;
        }

        int removed = 0;
        using (Transaction transaction = database.TransactionManager.StartTransaction())
        {
            removed += PrepareCopy(transaction, layoutManager.GetLayoutId(newDetalle), false, sourceNumber, next);
            removed += PrepareCopy(transaction, layoutManager.GetLayoutId(newUc), true, sourceNumber, next);
            PlaceAfterLastRing(transaction, database, newDetalle, newUc);
            transaction.Commit();
        }

        layoutManager.CurrentLayout = newDetalle;
        editor.Regen();
        editor.WriteMessage($"\n[NUEVOANILLO] Creados '{newDetalle}' y '{newUc}' a partir del anillo {sourceNumber}. "
            + $"Formato limpio ({removed} objeto(s) de contenido no se copiaron). Ya estás en '{newDetalle}'.\n");
    }

    // Limpia el contenido de la copia y actualiza el número del anillo en los títulos.
    private static int PrepareCopy(Transaction transaction, ObjectId layoutId, bool isUc, int oldNumber, int newNumber)
    {
        double frameMinY = isUc ? ClonarUcTool.UcFrameMinY : ClonarUcTool.DetalleFrameMinY;
        double frameMaxY = isUc ? ClonarUcTool.UcFrameMaxY : ClonarUcTool.DetalleFrameMaxY;
        var layout = (Layout)transaction.GetObject(layoutId, OpenMode.ForRead);
        var space = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);

        var toErase = new List<ObjectId>();
        var toRename = new List<ObjectId>();
        foreach (ObjectId id in space)
        {
            if (transaction.GetObject(id, OpenMode.ForRead) is not Entity entity || entity is Viewport) continue;
            if (ClonarUcTool.IsInsideFrame(entity, frameMinY, frameMaxY) || ClonarUcTool.HasDetalleXData(entity)) toErase.Add(id);
            else if (entity is MText || entity is DBText) toRename.Add(id);
        }

        foreach (ObjectId id in toErase)
        {
            var entity = (Entity)transaction.GetObject(id, OpenMode.ForWrite);
            entity.Erase();
        }

        string replacement = "ANILLO${1}" + newNumber;
        foreach (ObjectId id in toRename)
        {
            var entity = transaction.GetObject(id, OpenMode.ForRead);
            if (entity is MText mtext && RingText.IsMatch(mtext.Contents ?? string.Empty))
            {
                mtext.UpgradeOpen();
                mtext.Contents = RingText.Replace(mtext.Contents, replacement);
            }
            else if (entity is DBText text && RingText.IsMatch(text.TextString ?? string.Empty))
            {
                text.UpgradeOpen();
                text.TextString = RingText.Replace(text.TextString, replacement);
            }
        }
        return toErase.Count;
    }

    // Los dos layouts nuevos quedan a continuación del último ANILLO existente (si la posición no es válida, se dejan al final).
    private static void PlaceAfterLastRing(Transaction transaction, Database database, string newDetalle, string newUc)
    {
        try
        {
            var layouts = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
            int lastRingOrder = 0;
            Layout? detalle = null, uc = null;
            foreach (DBDictionaryEntry entry in layouts)
            {
                if (transaction.GetObject(entry.Value, OpenMode.ForRead) is not Layout layout) continue;
                string name = layout.LayoutName.Trim();
                if (name.Equals(newDetalle, StringComparison.OrdinalIgnoreCase)) { detalle = layout; continue; }
                if (name.Equals(newUc, StringComparison.OrdinalIgnoreCase)) { uc = layout; continue; }
                if (RingPattern.IsMatch(name)) lastRingOrder = Math.Max(lastRingOrder, layout.TabOrder);
            }
            if (detalle is null || uc is null || lastRingOrder == 0) return;
            detalle.UpgradeOpen();
            detalle.TabOrder = lastRingOrder + 1;
            uc.UpgradeOpen();
            uc.TabOrder = lastRingOrder + 2;
        }
        catch { /* si no se puede reordenar, quedan al final */ }
    }
}
