using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Colors;

namespace AutoKADN.Tools.Anotaciones;

/// <summary>
/// Herramienta ANOTACIONES.
/// Dibuja una línea de referencia y permite crear anotaciones LIBRE,
/// de longitud o ESPIRAL.
/// </summary>
public sealed class AnotacionesTool
{
    private const double TextHeight = 2.40;
    private const double TextOffset = 1.00;
    private const double MTextWidth = 0.0;

    private static readonly string[] AnnotationTypes =
    {
        "LIBRE",
        "CAMISA LONG.:",
        "PANTALLA LONG.:",
        "CRUCE CON TOPO LONG.:",
        "EMPEDRADO LONG.:",
        "VIGA EN CONCRETO LONG.:",
        "ESPIRAL"
    };

    public void Run()
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        if (document is null)
            return;

        Editor editor = document.Editor;
        object originalShortcutMenu = Application.GetSystemVariable("SHORTCUTMENU");

        try
        {
            Application.SetSystemVariable("SHORTCUTMENU", 0);
            editor.WriteMessage("\n[ANOTACIONES] ESC o clic derecho para salir.\n");

            while (true)
            {
                if (!GetReferenceLine(editor, out Point3d startPoint, out Point3d endPoint))
                    return;

                string? type = SelectAnnotationType(editor);
                if (type is null)
                    return;

                string? text = BuildAnnotation(editor, type);
                if (text is null)
                    return;

                CreateLineAndText(document.Database, startPoint, endPoint, text);
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

        PromptPointOptions firstOptions = new PromptPointOptions(
            "\nPrimer punto de la línea (ESC o clic derecho para salir): ")
        {
            AllowNone = true
        };

        PromptPointResult first = editor.GetPoint(firstOptions);
        if (first.Status != PromptStatus.OK)
            return false;

        PromptPointOptions secondOptions = new PromptPointOptions(
            "\nSegundo punto de la línea (ESC o clic derecho para salir): ")
        {
            BasePoint = first.Value,
            UseBasePoint = true,
            AllowNone = true
        };

        PromptPointResult second = editor.GetPoint(secondOptions);
        if (second.Status != PromptStatus.OK)
            return false;

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
            "\nSeleccione tipo de anotación [LIBRE/CAMISA/PANTALLA/CRUCE_CON_TOPO/EMPEDRADO/VIGA_EN_CONCRETO/ESPIRAL] (ESC o clic derecho para cancelar): ")
        {
            AllowNone = true
        };

        options.Keywords.Add("LIBRE");
        options.Keywords.Add("CAMISA");
        options.Keywords.Add("PANTALLA");
        options.Keywords.Add("CRUCE_CON_TOPO");
        options.Keywords.Add("EMPEDRADO");
        options.Keywords.Add("VIGA_EN_CONCRETO");
        options.Keywords.Add("ESPIRAL");

        PromptResult result = editor.GetKeywords(options);
        if (result.Status != PromptStatus.OK)
            return null;

        return result.StringResult;
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
            "CRUCE_CON_TOPO" => "CRUCE CON TOPO",
            "EMPEDRADO" => "EMPEDRADO",
            "VIGA_EN_CONCRETO" => "VIGA EN CONCRETO",
            _ => type
        };

        PromptStringOptions options = new PromptStringOptions(
            $"\n{label} - ingrese el valor de LONG.: (ENTER para aceptar, ESC o clic derecho para cancelar): ")
        {
            AllowSpaces = true
        };

        PromptResult result = editor.GetString(options);
        if (result.Status != PromptStatus.OK)
            return null;

        string value = result.StringResult.Trim();
        if (value.Length == 0)
            return null;

        return $"{label}\\P{value} LONG.:";
    }

    private static string? ReadFreeText(Editor editor)
    {
        var lines = new List<string>();
        editor.WriteMessage("\nLIBRE: escriba una línea y presione ENTER para pasar a la siguiente. Línea vacía para terminar.\n");

        while (true)
        {
            var options = new PromptStringOptions("Texto: ")
            {
                AllowSpaces = true
            };

            PromptResult result = editor.GetString(options);
            if (result.Status != PromptStatus.OK)
                return null;

            string line = result.StringResult.TrimEnd();
            if (line.Length == 0)
                break;

            lines.Add(line);
        }

        return lines.Count == 0 ? null : string.Join("\\P", lines);
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
        if (IsZero(saddles))
        {
            // No se necesita diámetro cuando no se generan sillas.
        }
        else
        {
            PromptStringOptions diameterOptions = new PromptStringOptions(
                "DIAMETRO DE SILLETA? (puede escribir signos y números): ")
            {
                AllowSpaces = true
            };
            PromptResult diameterResult = editor.GetString(diameterOptions);
            if (diameterResult.Status != PromptStatus.OK)
                return null;
            saddleDiameter = diameterResult.StringResult.Trim();
            if (saddleDiameter.Length == 0)
                return null;
        }

        string? peExt = ReadYesNo(editor, "PE.EXT.? [Y/N]: ");
        if (peExt is null) return null;

        var lines = new List<string>();

        if (!IsZero(pipe))
            lines.Add($"{pipe}ML TUBERIA 3/4\"");

        if (!IsZero(unions))
            lines.Add($"{unions} UNIONES DE 3/4\"");

        if (!IsZero(tees))
            lines.Add($"{tees} TEE DE 3/4\"");

        if (!IsZero(valves))
            lines.Add($"{valves} VALVULA DE 3/4\"");

        if (!IsZero(saddles))
            lines.Add($"{saddles} SILLETA DE {saddleDiameter}");

        if (peExt.Equals("Y", StringComparison.OrdinalIgnoreCase))
            lines.Add("PE.EXT.");

        if (lines.Count == 0)
        {
            editor.WriteMessage("\nESPIRAL no generó texto porque todas las cantidades son cero y PE.EXT. = N.\n");
            return string.Empty;
        }

        return string.Join("\\P", lines);
    }

    private static string? ReadNumber(Editor editor, string prompt)
    {
        var options = new PromptStringOptions($"\n{prompt} (número): ")
        {
            AllowSpaces = false
        };

        while (true)
        {
            PromptResult result = editor.GetString(options);
            if (result.Status != PromptStatus.OK)
                return null;

            string value = result.StringResult.Trim();
            if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double number) && number >= 0.0)
                return value;

            editor.WriteMessage("\nIngrese una cantidad numérica mayor o igual a cero.\n");
        }
    }

    private static string? ReadYesNo(Editor editor, string prompt)
    {
        var options = new PromptKeywordOptions(prompt)
        {
            AllowNone = false
        };
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

    private static void CreateLineAndText(Database database, Point3d startPoint, Point3d endPoint, string text)
    {
        using Transaction transaction = database.TransactionManager.StartTransaction();
        BlockTableRecord currentSpace = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);

        var line = new Line(startPoint, endPoint)
        {
            ColorIndex = 256
        };
        currentSpace.AppendEntity(line);
        transaction.AddNewlyCreatedDBObject(line, true);

        if (!string.IsNullOrWhiteSpace(text))
        {
            Vector3d direction = endPoint - startPoint;
            direction = direction.GetNormal();
            Vector3d normal = new Vector3d(-direction.Y, direction.X, 0.0).GetNormal();

            // Mantiene el texto en el lado superior de la línea y evita que quede sobre ella.
            if (normal.Y < 0.0)
                normal = -normal;

            Point3d textPoint = endPoint + normal * TextOffset;

            var mtext = new MText
            {
                Location = textPoint,
                Contents = text,
                TextHeight = TextHeight,
                Width = MTextWidth,
                Attachment = AttachmentPoint.TopLeft,
                Rotation = 0.0,
                ColorIndex = 256
            };

            currentSpace.AppendEntity(mtext);
            transaction.AddNewlyCreatedDBObject(mtext, true);
        }

        transaction.Commit();
    }
}
