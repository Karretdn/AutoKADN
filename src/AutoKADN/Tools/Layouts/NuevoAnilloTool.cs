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

        RingCreation? created = CreateNextRing(editor, database, "[NUEVOANILLO]", sourceNumber);
        if (created is null) return;

        layoutManager.CurrentLayout = created.NewDetalle;
        editor.Regen();
        editor.WriteMessage($"\n[NUEVOANILLO] Creados '{created.NewDetalle}' y '{created.NewUc}' a partir del anillo {created.SourceNumber}. "
            + $"Formato limpio ({created.Removed} objeto(s) de contenido no se copiaron). Ya estás en '{created.NewDetalle}'.\n");
    }

    /// <summary>Los dos layouts que se acaban de crear y de qué anillo salieron.</summary>
    internal sealed class RingCreation
    {
        public int Number;
        public int SourceNumber;
        public string NewDetalle = string.Empty;
        public string NewUc = string.Empty;
        public int Removed;
    }

    /// <summary>Número más alto entre todos los layouts ANILLO N (aunque les falte la pareja); 0 si no hay ninguno.</summary>
    internal static int MaxRingNumber(Database database)
    {
        int max = 0;
        using (Transaction transaction = database.TransactionManager.StartTransaction())
        {
            var layouts = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in layouts)
            {
                if (transaction.GetObject(entry.Value, OpenMode.ForRead) is not Layout layout) continue;
                Match match = RingPattern.Match(layout.LayoutName.Trim());
                if (match.Success) max = Math.Max(max, int.Parse(match.Groups[1].Value));
            }
            transaction.Commit();
        }
        return max;
    }

    /// <summary>Número del anillo más alto que ya tiene sus dos layouts (DETALLE y UC); 0 si no hay ninguno.</summary>
    internal static int HighestCompleteRing(Database database)
    {
        var detalle = new HashSet<int>();
        var uc = new HashSet<int>();
        using (Transaction transaction = database.TransactionManager.StartTransaction())
        {
            var layouts = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in layouts)
            {
                if (transaction.GetObject(entry.Value, OpenMode.ForRead) is not Layout layout) continue;
                Match match = RingPattern.Match(layout.LayoutName.Trim());
                if (!match.Success) continue;
                int number = int.Parse(match.Groups[1].Value);
                if (match.Groups[2].Value.Equals("DETALLE", StringComparison.OrdinalIgnoreCase)) detalle.Add(number); else uc.Add(number);
            }
            transaction.Commit();
        }
        int highest = 0;
        foreach (int number in detalle) if (uc.Contains(number) && number > highest) highest = number;
        return highest;
    }

    /// <summary>Qué layouts tiene cada número de anillo.</summary>
    internal sealed class RingPair
    {
        public bool Detalle;
        public bool Uc;
    }

    /// <summary>Los layouts ANILLO N existentes, por número (ordenados).</summary>
    internal static SortedDictionary<int, RingPair> RingLayouts(Database database)
    {
        var rings = new SortedDictionary<int, RingPair>();
        using (Transaction transaction = database.TransactionManager.StartTransaction())
        {
            var layouts = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in layouts)
            {
                if (transaction.GetObject(entry.Value, OpenMode.ForRead) is not Layout layout) continue;
                Match match = RingPattern.Match(layout.LayoutName.Trim());
                if (!match.Success) continue;
                int number = int.Parse(match.Groups[1].Value);
                if (!rings.TryGetValue(number, out RingPair? pair)) { pair = new RingPair(); rings[number] = pair; }
                if (match.Groups[2].Value.Equals("DETALLE", StringComparison.OrdinalIgnoreCase)) pair.Detalle = true; else pair.Uc = true;
            }
            transaction.Commit();
        }
        return rings;
    }

    /// <summary>
    /// Crea ANILLO N+1 DETALLE y ANILLO N+1 UC copiando los dos layouts del anillo `sourceNumber` (el de mayor número
    /// si es 0 o menos) y los deja limpios. No cambia el layout actual. Devuelve null, tras escribir el motivo con la
    /// etiqueta `tag`, si no se pudo.
    /// </summary>
    internal static RingCreation? CreateNextRing(Editor editor, Database database, string tag, int sourceNumber) =>
        CreateRing(editor, database, tag, sourceNumber, 0);

    /// <summary>
    /// Igual que CreateNextRing, pero con el número indicado (por ejemplo uno que falta en la secuencia); con 0 o menos,
    /// el siguiente al más alto. Un número que falta queda en su lugar de la secuencia de pestañas.
    /// </summary>
    internal static RingCreation? CreateRing(Editor editor, Database database, string tag, int sourceNumber, int requestedNumber)
    {
        LayoutManager layoutManager = LayoutManager.Current;
        if (sourceNumber <= 0)
        {
            sourceNumber = HighestCompleteRing(database);
            if (sourceNumber == 0)
            {
                editor.WriteMessage($"\n{tag} No hay ningún anillo completo (ANILLO N DETALLE y ANILLO N UC) de donde copiar el formato.\n");
                return null;
            }
        }

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
            editor.WriteMessage($"\n{tag} Para copiar el anillo {sourceNumber} hacen falta sus dos layouts: ANILLO {sourceNumber} DETALLE y ANILLO {sourceNumber} UC.\n");
            return null;
        }

        int next = requestedNumber > 0 ? requestedNumber : maxNumber + 1;
        string newDetalle = $"ANILLO {next} DETALLE";
        string newUc = $"ANILLO {next} UC";
        if (existing.Contains(newDetalle) || existing.Contains(newUc))
        {
            editor.WriteMessage($"\n{tag} Ya existe '{newDetalle}' o '{newUc}'.\n");
            return null;
        }

        try
        {
            layoutManager.CopyLayout(sourceDetalle, newDetalle);
            layoutManager.CopyLayout(sourceUc, newUc);
        }
        catch (System.Exception ex)
        {
            editor.WriteMessage($"\n{tag} No se pudieron copiar los layouts: {ex.Message}\n");
            return null;
        }

        int removed = 0;
        using (Transaction transaction = database.TransactionManager.StartTransaction())
        {
            removed += PrepareCopy(transaction, layoutManager.GetLayoutId(newDetalle), false, sourceNumber, next);
            removed += PrepareCopy(transaction, layoutManager.GetLayoutId(newUc), true, sourceNumber, next);
            if (next > maxNumber) PlaceAfterLastRing(transaction, database, newDetalle, newUc);
            else PlaceInSequence(transaction, database, next, newDetalle, newUc);
            transaction.Commit();
        }

        return new RingCreation { Number = next, SourceNumber = sourceNumber, NewDetalle = newDetalle, NewUc = newUc, Removed = removed };
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

    // Un anillo que falta en la secuencia (por ejemplo el 1 si se borró) queda a continuación del anillo anterior
    // (el de mayor número por debajo) o, si no hay, justo antes del primero que existe.
    private static void PlaceInSequence(Transaction transaction, Database database, int number, string newDetalle, string newUc)
    {
        try
        {
            var layouts = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
            Layout? detalle = null, uc = null;
            int lowerNumber = 0, lowerOrder = 0, higherNumber = int.MaxValue, higherOrder = 0;
            foreach (DBDictionaryEntry entry in layouts)
            {
                if (transaction.GetObject(entry.Value, OpenMode.ForRead) is not Layout layout) continue;
                string name = layout.LayoutName.Trim();
                if (name.Equals(newDetalle, StringComparison.OrdinalIgnoreCase)) { detalle = layout; continue; }
                if (name.Equals(newUc, StringComparison.OrdinalIgnoreCase)) { uc = layout; continue; }
                Match match = RingPattern.Match(name);
                if (!match.Success) continue;
                int other = int.Parse(match.Groups[1].Value);
                if (other < number && (other > lowerNumber || (other == lowerNumber && layout.TabOrder > lowerOrder))) { lowerNumber = other; lowerOrder = layout.TabOrder; }
                if (other > number && (other < higherNumber || (other == higherNumber && layout.TabOrder < higherOrder))) { higherNumber = other; higherOrder = layout.TabOrder; }
            }
            if (detalle is null || uc is null) return;
            int start = lowerNumber > 0 ? lowerOrder + 1 : higherOrder;
            if (start <= 0) return;
            detalle.UpgradeOpen();
            detalle.TabOrder = start;
            uc.UpgradeOpen();
            uc.TabOrder = start + 1;
        }
        catch { /* si no se puede reordenar, quedan donde AutoCAD los puso */ }
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
