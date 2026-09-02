using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace AutoKADN.Tools.Anotaciones;

public sealed class AnotacionesTool
{
    private const double TextHeight = 2.40;
    private const double TextOffset = 1.00;

    public void Run()
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        if (document is null) return;

        Editor editor = document.Editor;
        object originalShortcutMenu = Application.GetSystemVariable("SHORTCUTMENU");

        try
        {
            Application.SetSystemVariable("SHORTCUTMENU", 0);
            editor.WriteMessage("\n[ANOTACIONES] ESC o clic derecho para salir.\n");

            while (true)
            {
                if (!GetReferenceLine(editor, out Point3d startPoint, out Point3d endPoint)) return;

                ObjectId lineId = CreateReferenceLine(document.Database, startPoint, endPoint);
                if (lineId == ObjectId.Null) return;

                string? type = SelectAnnotationType(editor);
                if (type is null)
                {
                    EraseEntity(document.Database, lineId);
                    return;
                }

                string? text = BuildAnnotation(editor, type);
                if (text is null)
                {
                    EraseEntity(document.Database, lineId);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(text))
                    CreateText(document.Database, startPoint, endPoint, text);

                editor.Regen();
            }
        }
        finally
        {
            Application.SetSystemVariable("SHORTCUTMENU", originalShortcutMenu);
        }
    }

    private static bool GetReferenceLine(Editor editor, out Point3d startPoint, out Point3d endPoint)
    {
        startPoint = Point3d.Origin;
        endPoint = Point3d.Origin;

        var firstOptions = new PromptPointOptions("\nPrimer punto de la línea (ESC o clic derecho para salir): ")
        {
            AllowNone = true
        };
        PromptPointResult first = editor.GetPoint(firstOptions);
        if (first.Status != PromptStatus.OK) return false;

        var secondOptions = new PromptPointOptions("\nSegundo punto de la línea (ESC o clic derecho para salir): ")
        {
            BasePoint = first.Value,
            UseBasePoint = true,
            AllowNone = true
        };
        PromptPointResult second = editor.GetPoint(secondOptions);
        if (second.Status != PromptStatus.OK) return false;

        if (first.Value.DistanceTo(second.Value) <= Tolerance.Global.EqualPoint)
        {
            editor.WriteMessage("\nLa línea debe tener una longitud mayor que cero.\n");
            return true;
        }

        startPoint = first.Value;
        endPoint = second.Value;
        return true;
    }

    private static string? SelectAnnotationType(Editor editor)
    {
        var options = new PromptKeywordOptions(
            "\nSeleccione tipo de anotación [ESPIRAL/CAMISA/PANTALLA/CRUCE CON TOPO/EMPEDRADO/VIGA EN CONCRETO/LIBRE] (ESC o clic derecho para cancelar): ")
        {
            AllowNone = true
        };

        // AutoCAD 2022 expone el overload de 5 parámetros:
        // globalName, localName, displayName, enabled, visible.
        // Los nombres internos son únicos para evitar cualquier cruce entre etiquetas.
        options.Keywords.Add("ESPIRAL", "ESPIRAL", "ESPIRAL", true, true);
        options.Keywords.Add("CAMISA", "CAMISA", "CAMISA", true, true);
        options.Keywords.Add("PANTALLA", "PANTALLA", "PANTALLA", true, true);
        options.Keywords.Add("CRUCE_TOPO", "CRUCE_TOPO", "CRUCE CON TOPO", true, true);
        options.Keywords.Add("EMPEDRADO", "EMPEDRADO", "EMPEDRADO", true, true);
        options.Keywords.Add("VIGA_CONCRETO", "VIGA_CONCRETO", "VIGA EN CONCRETO", true, true);
        options.Keywords.Add("LIBRE", "LIBRE", "LIBRE", true, true);

        PromptResult result = editor.GetKeywords(options);
        return result.Status == PromptStatus.OK ? result.StringResult : null;
    }

    private static string? BuildAnnotation(Editor editor, string type)
    {
        if (type.Equals("LIBRE", StringComparison.OrdinalIgnoreCase))
            return ReadFreeText(editor);

        if (type.Equals("ESPIRAL", StringComparison.OrdinalIgnoreCase))
            return ReadSpiral(editor);

        string label = type switch
        {
            "CAMISA" => "CAMISA",
            "PANTALLA" => "PANTALLA",
            "CRUCE_TOPO" => "CRUCE CON TOPO",
            "EMPEDRADO" => "EMPEDRADO",
            "VIGA_CONCRETO" => "VIGA EN CONCRETO",
            _ => type
        };

        var options = new PromptStringOptions($"\n{label} - ingrese el valor de LONG.: (ENTER para aceptar, ESC o clic derecho para cancelar): ")
        {
            AllowSpaces = false
        };
        PromptResult result = editor.GetString(options);
        if (result.Status != PromptStatus.OK) return null;

        string value = result.StringResult.Trim();
        if (value.Length == 0) return null;

        return $"{label}\\PLONG.: {value}ML";
    }

    private static string? ReadFreeText(Editor editor)
    {
        var lines = new List<string>();
        editor.WriteMessage("\nLIBRE: escriba una línea y presione ENTER para pasar a la siguiente. Termine con clic derecho o ESC.\n");

        while (true)
        {
            var options = new PromptStringOptions("Texto: ") { AllowSpaces = true };
            PromptResult result = editor.GetString(options);
            if (result.Status != PromptStatus.OK)
                return lines.Count == 0 ? null : string.Join("\\P", lines);

            string line = result.StringResult.TrimEnd();
            if (line.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }

            lines.Add(line);
        }
    }

    private static string? ReadSpiral(Editor editor)
    {
        string? pipe = ReadNumber(editor, "METROS DE TUBERIA DE 3/4\"?");
        if (pipe is null) return null;

        string? unions = ReadNumber(editor, "CANTIDAD DE UNIONES DE 3/4\"?");
        if (unions is null) return null;

        string? tees = ReadNumber(editor, "CANTIDAD DE TEE DE 3/4\"?");
        if (tees is null) return null;

        string? valves = ReadNumber(editor, "VALVULA DE 3/4\"?");
        if (valves is null) return null;

        string? saddles = ReadNumber(editor, "SILLETA?");
        if (saddles is null) return null;

        string saddleDiameter = string.Empty;
        if (!IsZero(saddles))
        {
            var diameterOptions = new PromptStringOptions("DIAMETRO DE SILLETA? (puede escribir signos y números): ")
            {
                AllowSpaces = false
            };
            PromptResult diameterResult = editor.GetString(diameterOptions);
            if (diameterResult.Status != PromptStatus.OK) return null;

            saddleDiameter = diameterResult.StringResult.Trim();
            if (saddleDiameter.Length == 0) return null;
        }

        string? peExt = ReadYesNo(editor, "PE.EXT.? [Y/N]: ");
        if (peExt is null) return null;

        var lines = new List<string>();
        if (!IsZero(pipe)) lines.Add($"{pipe}ML TUBERIA 3/4\"");
        if (!IsZero(unions)) lines.Add($"{unions} UNIONES DE 3/4\"");
        if (!IsZero(tees)) lines.Add($"{tees} TEE DE 3/4\"");
        if (!IsZero(valves)) lines.Add($"{valves} VALVULA DE 3/4\"");
        if (!IsZero(saddles)) lines.Add($"{saddles} SILLETA DE {saddleDiameter}");
        if (peExt.Equals("Y", StringComparison.OrdinalIgnoreCase)) lines.Add("PE.EXT.");

        if (lines.Count == 0)
        {
            editor.WriteMessage("\nESPIRAL: no se generó ninguna línea porque todas las cantidades fueron cero y PE.EXT. fue N.\n");
            return string.Empty;
        }

        return string.Join("\\P", lines);
    }

    private static string? ReadNumber(Editor editor, string prompt)
    {
        var options = new PromptStringOptions($"\n{prompt} (número): ") { AllowSpaces = false };
        while (true)
        {
            PromptResult result = editor.GetString(options);
            if (result.Status != PromptStatus.OK) return null;

            string value = result.StringResult.Trim();
            if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double number)
                && number >= 0.0)
                return value;

            editor.WriteMessage("\nIngrese una cantidad numérica mayor o igual a cero.\n");
        }
    }

    private static string? ReadYesNo(Editor editor, string prompt)
    {
        var options = new PromptKeywordOptions(prompt) { AllowNone = false };
        options.Keywords.Add("Y");
        options.Keywords.Add("N");
        PromptResult result = editor.GetKeywords(options);
        return result.Status == PromptStatus.OK ? result.StringResult : null;
    }

    private static bool IsZero(string value)
    {
        return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double number)
               && Math.Abs(number) <= 1e-12;
    }

    private static ObjectId CreateReferenceLine(Database database, Point3d startPoint, Point3d endPoint)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();
        BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
        var line = new Line(startPoint, endPoint) { ColorIndex = 256 };
        currentSpace.AppendEntity(line);
        transaction.AddNewlyCreatedDBObject(line, true);
        transaction.Commit();
        return line.ObjectId;
    }

    private static void CreateText(Database database, Point3d startPoint, Point3d endPoint, string text)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();
        BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);

        Vector3d direction = (endPoint - startPoint).GetNormal();
        Vector3d normal = new Vector3d(-direction.Y, direction.X, 0.0).GetNormal();
        if (normal.Y < 0.0) normal = -normal;

        Point3d textPoint = endPoint + normal * TextOffset;
        var mtext = new MText
        {
            Location = textPoint,
            Contents = text,
            TextHeight = TextHeight,
            Attachment = AttachmentPoint.TopLeft,
            Rotation = 0.0,
            ColorIndex = 256
        };

        currentSpace.AppendEntity(mtext);
        transaction.AddNewlyCreatedDBObject(mtext, true);
        transaction.Commit();
    }

    private static void EraseEntity(Database database, ObjectId objectId)
    {
        if (objectId == ObjectId.Null) return;
        using Transaction transaction = database.TransactionManager.StartTransaction();
        if (transaction.GetObject(objectId, OpenMode.ForWrite, false) is Entity entity)
            entity.Erase();
        transaction.Commit();
    }
}
