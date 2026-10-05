using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AutoKADN.Core;

namespace AutoKADN.Tools.Layouts;

// Rellena el cajetín de los layouts ANILLO N DETALLE / ANILLO N UC / TRONCAL con los datos del proyecto que
// dejó la app AutoKADN Proyectos en datos_proyecto.json (en la carpeta raíz del proyecto; también se acepta junto al DWG, en PLANOS): municipio, obra o sector,
// interventor (con código), pegador (supervisor), tubería, O.T. y las fechas. El diámetro de los títulos
// (3/4" o 1/2") también se ajusta. Si algún campo ya tiene un dato distinto pregunta antes de sobrescribir.
// Las reglas (qué es cada campo y cuál FECHA es cuál) están en Core/CajetinRules.
public sealed class RellenarDatosTool
{
    public const string DataFileName = "datos_proyecto.json";

    private static readonly Regex RingLayout = new Regex(@"^(?:ANILLO\s+\d+|TRONCAL)\s+(DETALLE|UC)$", RegexOptions.IgnoreCase);
    private static readonly Regex DiameterPattern = new Regex("(?:1/2|3/4)\"");
    private static readonly string[] RequiredKeys =
    {
        "orden", "proyecto", "municipio", "interventor", "supervisor", "tuberia",
        "planoPruebaInicial", "planoPruebaFinal", "planoGasificado"
    };

    private sealed class Field
    {
        public ObjectId Id;
        public string Layout = string.Empty;
        public string Prefix = string.Empty;
        public string Label = string.Empty;
        public string Value = string.Empty;
        public Point3d Location;
        public string? NewValue;

        public bool HasChange => NewValue is not null && !string.Equals(Value.Trim(), NewValue.Trim(), StringComparison.OrdinalIgnoreCase);
        public bool IsEmpty => string.IsNullOrWhiteSpace(Value);
        // La tubería viene de fábrica con una de las dos marcas: cambiarla no es pisar un dato.
        public bool IsDefaultValue => Label.Equals("TUBERIA", StringComparison.OrdinalIgnoreCase)
            && (Value.Trim().Equals("EXTRUCOL", StringComparison.OrdinalIgnoreCase) || Value.Trim().Equals("PAVCO", StringComparison.OrdinalIgnoreCase));
        public string Title => $"{Layout} · {Label.ToUpperInvariant()}";
    }

    public void Run()
    {
        var document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (document is null) return;
        Editor editor = document.Editor;
        Database database = document.Database;

        string dwgPath = database.Filename;
        if (string.IsNullOrWhiteSpace(dwgPath) || !File.Exists(dwgPath))
        {
            editor.WriteMessage("\n[RELLENARDATOS] Guarda el dibujo primero: los datos se buscan junto al archivo DWG.\n");
            return;
        }

        string? jsonPath = FindDataFile(dwgPath);
        if (jsonPath is null)
        {
            editor.WriteMessage($"\n[RELLENARDATOS] No se encontró {DataFileName} en la carpeta del proyecto ni junto al DWG ({Path.GetDirectoryName(dwgPath)}). Lo crea la app AutoKADN Proyectos al crear el proyecto.\n");
            return;
        }

        Dictionary<string, string> data = FlatJson.Parse(File.ReadAllText(jsonPath, Encoding.UTF8));
        List<string> missingKeys = RequiredKeys.Where(k => !data.TryGetValue(k, out string? v) || string.IsNullOrWhiteSpace(v)).ToList();
        if (missingKeys.Count > 0)
        {
            editor.WriteMessage($"\n[RELLENARDATOS] {DataFileName} está incompleto. Faltan: {string.Join(", ", missingKeys)}.\n");
            return;
        }

        var fields = new List<Field>();
        var warnings = new List<string>();
        int layoutCount = 0;
        using (Transaction transaction = database.TransactionManager.StartTransaction())
        {
            var layouts = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in layouts)
            {
                if (transaction.GetObject(entry.Value, OpenMode.ForRead) is not Layout layout) continue;
                string layoutName = layout.LayoutName.Trim();
                Match ring = RingLayout.Match(layoutName);
                if (!ring.Success) continue;
                layoutCount++;
                bool isUc = ring.Groups[1].Value.Equals("UC", StringComparison.OrdinalIgnoreCase);

                var layoutFields = new List<Field>();
                (double X, double Y)? inicialLabel = null, finalLabel = null;
                var space = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                foreach (ObjectId id in space)
                {
                    if (transaction.GetObject(id, OpenMode.ForRead) is not MText mtext) continue;
                    string contents = mtext.Contents ?? string.Empty;
                    if (CajetinRules.TryParseField(contents, out string prefix, out string label, out string value))
                    {
                        layoutFields.Add(new Field { Id = id, Layout = layoutName, Location = mtext.Location, Prefix = prefix, Label = label, Value = value });
                    }
                    else if (contents.IndexOf("PRUEBA INICIAL", StringComparison.OrdinalIgnoreCase) >= 0) inicialLabel = (mtext.Location.X, mtext.Location.Y);
                    else if (contents.IndexOf("PRUEBA FINAL", StringComparison.OrdinalIgnoreCase) >= 0) finalLabel = (mtext.Location.X, mtext.Location.Y);
                }

                AssignValues(layoutFields, isUc, layoutName, data, inicialLabel, finalLabel, warnings);
                fields.AddRange(layoutFields);
            }
            transaction.Commit();
        }

        if (layoutCount == 0)
        {
            editor.WriteMessage("\n[RELLENARDATOS] No hay layouts ANILLO N DETALLE / ANILLO N UC / TRONCAL en este dibujo.\n");
            return;
        }

        List<Field> changes = fields.Where(f => f.HasChange).ToList();
        List<Field> conflicts = changes.Where(f => !f.IsEmpty && !f.IsDefaultValue).ToList();
        bool overwrite = true;
        if (conflicts.Count > 0)
        {
            var sample = conflicts.Take(8).Select(f => $" • {f.Title}: «{f.Value}» → «{f.NewValue}»");
            string more = conflicts.Count > 8 ? $"\n   … y {conflicts.Count - 8} más" : string.Empty;
            System.Windows.MessageBoxResult answer = Dialogs.Ask(
                $"{conflicts.Count} campo(s) ya tienen datos distintos:\n\n{string.Join("\n", sample)}{more}\n\n" +
                "Sí = sobrescribir todo\nNo = rellenar solo los campos vacíos\nCancelar = no hacer nada",
                "AutoKADN · Rellenar datos", System.Windows.MessageBoxButton.YesNoCancel);
            if (answer == System.Windows.MessageBoxResult.Cancel)
            {
                editor.WriteMessage("\n[RELLENARDATOS] Cancelado. No se cambió nada.\n");
                return;
            }
            overwrite = answer == System.Windows.MessageBoxResult.Yes;
        }

        int filled = 0, overwritten = 0, skipped = 0, diameterChanges = 0;
        using (Transaction transaction = database.TransactionManager.StartTransaction())
        {
            foreach (Field field in changes)
            {
                if (!field.IsEmpty && !field.IsDefaultValue && !overwrite) { skipped++; continue; }
                var mtext = (MText)transaction.GetObject(field.Id, OpenMode.ForWrite);
                mtext.Contents = CajetinRules.FormatField(field.Prefix, field.Label, field.NewValue!);
                if (field.IsEmpty) filled++; else overwritten++;
            }

            if (data.TryGetValue("diametro", out string? diameter) && (diameter == "3/4" || diameter == "1/2"))
                diameterChanges = ApplyDiameter(database, transaction, diameter);

            transaction.Commit();
        }
        editor.Regen();

        editor.WriteMessage($"\n[RELLENARDATOS] {layoutCount} layout(s) revisados · {filled} campo(s) rellenados"
            + (overwritten > 0 ? $" · {overwritten} sobrescritos" : string.Empty)
            + (skipped > 0 ? $" · {skipped} con datos distintos se dejaron como estaban" : string.Empty)
            + (diameterChanges > 0 ? $" · diámetro {data["diametro"]}\" aplicado en {diameterChanges} texto(s)" : string.Empty) + ".\n");
        foreach (string warning in warnings) editor.WriteMessage($"[RELLENARDATOS] Aviso: {warning}\n");
        if (changes.Count == 0 && diameterChanges == 0) editor.WriteMessage("[RELLENARDATOS] El cajetín ya estaba al día.\n");
    }

    private static void AssignValues(List<Field> fields, bool isUc, string layoutName, Dictionary<string, string> data,
        (double X, double Y)? inicialLabel, (double X, double Y)? finalLabel, List<string> warnings)
    {
        foreach (Field field in fields) field.NewValue = CajetinRules.ValueFor(field.Label, data);

        List<Field> dates = fields.Where(f => f.Label.Equals("FECHA", StringComparison.OrdinalIgnoreCase)).ToList();
        if (dates.Count == 0) return;

        if (isUc)
        {
            foreach (Field date in dates) date.NewValue = CajetinRules.DateValue(CajetinRules.DateKind.Gasificado, data);
            return;
        }

        CajetinRules.DateKind[]? kinds = CajetinRules.ClassifyDetalleDates(
            dates.Select(d => (d.Location.X, d.Location.Y)).ToList(), inicialLabel, finalLabel);
        if (kinds is null)
        {
            warnings.Add($"{layoutName}: se esperaban 3 campos FECHA y hay {dates.Count}; no se rellenaron las fechas.");
            return;
        }
        for (int i = 0; i < dates.Count; i++) dates[i].NewValue = CajetinRules.DateValue(kinds[i], data);
    }

    // El diámetro de los títulos (ANILLO 1 - 3/4", %%C3/4") sigue al del proyecto. Solo textos sin XData del plugin.
    private static int ApplyDiameter(Database database, Transaction transaction, string diameter)
    {
        int changed = 0;
        var layouts = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry entry in layouts)
        {
            if (transaction.GetObject(entry.Value, OpenMode.ForRead) is not Layout layout || !RingLayout.IsMatch(layout.LayoutName.Trim())) continue;
            var space = (BlockTableRecord)transaction.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            foreach (ObjectId id in space)
            {
                if (transaction.GetObject(id, OpenMode.ForRead) is not MText mtext) continue;
                string contents = mtext.Contents ?? string.Empty;
                if (!DiameterPattern.IsMatch(contents) || ClonarUcTool.HasDetalleXData(mtext)) continue;
                string updated = DiameterPattern.Replace(contents, diameter + "\"");
                if (updated == contents) continue;
                mtext.UpgradeOpen();
                mtext.Contents = updated;
                changed++;
            }
        }
        return changed;
    }

    private static string? FindDataFile(string dwgPath)
    {
        string? directory = Path.GetDirectoryName(dwgPath);
        for (int level = 0; level < 2 && !string.IsNullOrEmpty(directory); level++)
        {
            string candidate = Path.Combine(directory, DataFileName);
            if (File.Exists(candidate)) return candidate;
            directory = Path.GetDirectoryName(directory);
        }
        return null;
    }
}
