using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AutoKADN.Core;

namespace AutoKADN.Tools.NomenclaturaVial;

public sealed class NomenclaturaVialTool
{
    private readonly TextCreationService _textCreationService = new();

    public void Run()
    {
        var document = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
        if (document is null)
            return;

        Editor editor = document.Editor;
        editor.WriteMessage("\n[KARP_NOMVIAL] Seleccione un punto y escriba la nomenclatura. ESC para salir.\n");

        while (true)
        {
            PromptPointResult pointResult = editor.GetPoint("\nPunto de inserción: ");

            if (pointResult.Status == PromptStatus.Cancel)
                break;

            if (pointResult.Status != PromptStatus.OK)
                continue;

            Point3d position = pointResult.Value;
            PromptStringOptions textOptions = new("Nomenclatura: ")
            {
                AllowSpaces = true
            };

            PromptResult textResult = editor.GetString(textOptions);

            if (textResult.Status == PromptStatus.Cancel)
                break;

            if (textResult.Status != PromptStatus.OK)
                continue;

            string content = textResult.StringResult.Trim();
            if (content.Length == 0)
            {
                editor.WriteMessage("\nLa nomenclatura no puede estar vacía.\n");
                continue;
            }

            _textCreationService.CreateText(position, content);
            editor.WriteMessage($"\nTexto creado: {content}\n");
        }

        editor.WriteMessage("\n[KARP_NOMVIAL] Herramienta finalizada.\n");
    }
}
