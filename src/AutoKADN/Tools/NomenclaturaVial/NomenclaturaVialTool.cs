using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AutoKADN.Core;

namespace AutoKADN.Tools.NomenclaturaVial;

public sealed class NomenclaturaVialTool
{
    private readonly TextCreationService _textCreationService = new();

    private static readonly string[] TiposNomenclatura = { "KR", "CL" };
    private static readonly string[] TiposPavimento = { "T.N.", "PAV", "ASF", "ADO" };

    public void Run()
    {
        var document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (document is null)
            return;

        Editor editor = document.Editor;
        editor.WriteMessage("\n[KARP_NOMVIAL] Seleccione un punto y complete la nomenclatura. ESC para salir.\n");

        while (true)
        {
            PromptPointResult pointResult = editor.GetPoint("\nPunto de inserción: ");

            if (pointResult.Status == PromptStatus.Cancel)
                break;

            if (pointResult.Status != PromptStatus.OK)
                continue;

            Point3d position = pointResult.Value;

            string? tipo = ObtenerTipo(editor);
            if (tipo is null)
                break;

            string? numero = ObtenerNumero(editor);
            if (numero is null)
                break;

            string? pavimento = ObtenerPavimento(editor);
            if (pavimento is null)
                break;

            string content = $"{tipo} {numero} - {pavimento}";

            _textCreationService.CreateText(position, content);
            editor.WriteMessage($"\nTexto creado: {content}\n");
        }

        editor.WriteMessage("\n[KARP_NOMVIAL] Herramienta finalizada.\n");
    }

    private static string? ObtenerTipo(Editor editor)
    {
        var options = new PromptKeywordOptions("\nTipo de vía [KR/CL]: ")
        {
            AllowNone = false
        };

        foreach (string tipo in TiposNomenclatura)
            options.Keywords.Add(tipo);

        PromptResult result = editor.GetKeywords(options);

        return result.Status == PromptStatus.OK
            ? result.StringResult.ToUpperInvariant()
            : null;
    }

    private static string? ObtenerNumero(Editor editor)
    {
        while (true)
        {
            var options = new PromptStringOptions("\nNúmero de nomenclatura (2 o 3 cifras): ")
            {
                AllowSpaces = false,
                UseDefaultValue = false
            };

            PromptResult result = editor.GetString(options);

            if (result.Status == PromptStatus.Cancel)
                return null;

            if (result.Status != PromptStatus.OK)
                continue;

            string numero = result.StringResult.Trim();

            if (numero.Length is >= 2 and <= 3 && numero.All(char.IsDigit))
                return numero;

            editor.WriteMessage("\nEl número debe contener exactamente 2 o 3 cifras.\n");
        }
    }

    private static string? ObtenerPavimento(Editor editor)
    {
        var options = new PromptKeywordOptions("\nTipo de superficie [T.N./PAV/ASF/ADO]: ")
        {
            AllowNone = false
        };

        foreach (string pavimento in TiposPavimento)
            options.Keywords.Add(pavimento);

        PromptResult result = editor.GetKeywords(options);

        return result.Status == PromptStatus.OK
            ? result.StringResult.ToUpperInvariant()
            : null;
    }
}
