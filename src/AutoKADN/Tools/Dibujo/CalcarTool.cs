using System.IO;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AutoKADN.Tools.Layouts;
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AutoKADN.Tools.Dibujo;

// Importa una imagen o un PDF para calcar encima (con LINEARAPIDA, por ejemplo). Queda dentro del marco del plano
// (el área de dibujo de los layouts ANILLO N DETALLE / UC; en otro lugar se indica el rectángulo con dos clics), con
// transparencia, en la capa CALCO (que no se imprime) y al fondo. Antes de dejarla se dibuja un rectángulo sobre lo
// que se ve: solo esa parte queda a la vista y se agranda para llenar el marco. Enter o clic derecho = usar todo.
// ESC cancela y no deja nada. La imagen o el PDF quedan como referencia al archivo (no se incrustan).
// El trabajo sobre el dibujo está en CalcoImporter.
public sealed class CalcarTool
{
    private static readonly Regex DetailLayout = new Regex(@"^(?:ANILLO\s+\d+|TRONCAL)\s+DETALLE$", RegexOptions.IgnoreCase);
    private static readonly Regex UcLayout = new Regex(@"^(?:ANILLO\s+\d+|TRONCAL)\s+UC$", RegexOptions.IgnoreCase);
    private const string FileFilter =
        "Imágenes y PDF (*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif;*.pdf)|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif;*.pdf|Todos los archivos (*.*)|*.*";
    // Un rectángulo de recorte más chico que esta fracción de la imagen se toma como un clic accidental.
    private const double MinCropFraction = 0.01;

    public void Run()
    {
        var document = AcadApplication.DocumentManager.MdiActiveDocument;
        if (document is null) return;
        Editor editor = document.Editor;
        Database database = document.Database;

        object originalShortcutMenu = AcadApplication.GetSystemVariable("SHORTCUTMENU");
        ObjectId entityId = ObjectId.Null;
        bool keep = false;
        try
        {
            // Clic derecho = Enter (sin menú contextual), como en LINEARAPIDA.
            AcadApplication.SetSystemVariable("SHORTCUTMENU", 0);

            if (!TryGetFrame(editor, database, out ObjectId spaceId, out CalcoFrame frame, out string place)) return;

            string? path = AskFile(editor);
            if (path is null) return;

            int page = 1;
            if (CalcoImporter.IsPdf(path) && !AskPage(editor, path, out page)) return;

            CalcoImport import = CalcoImporter.Attach(database, spaceId, path, page, frame);
            if (!import.Ok)
            {
                editor.WriteMessage($"\n[CALCAR] No se pudo importar «{Path.GetFileName(path)}»: {import.Error}.\n");
                return;
            }
            entityId = import.EntityId;
            editor.UpdateScreen(); // que se vea entera antes de pedir el recorte

            bool cropped = false;
            if (!AskCrop(editor, import.Content, out CalcoFrame crop, out bool cancelled))
            {
                if (cancelled) return; // ESC: se borra lo importado (en finally)
            }
            else
            {
                string error = CalcoImporter.Crop(database, entityId, crop, frame);
                if (error.Length == 0) cropped = true;
                else editor.WriteMessage($"\n[CALCAR] No se pudo recortar ({error}); queda la imagen completa.\n");
            }

            keep = true;
            editor.UpdateScreen();
            editor.WriteMessage($"\n[CALCAR] «{Path.GetFileName(path)}»{(CalcoImporter.IsPdf(path) ? " (página " + page + ")" : string.Empty)} importado en {place}: "
                + $"{(cropped ? "recortado y ajustado" : "completo y ajustado")} al marco, transparencia {CalcoImporter.TransparencyPercent} %, capa {CalcoImporter.LayerName} (no se imprime).\n");
        }
        catch (System.Exception ex)
        {
            editor.WriteMessage($"\nERROR en CALCAR: {ex.Message}\n");
        }
        finally
        {
            AcadApplication.SetSystemVariable("SHORTCUTMENU", originalShortcutMenu);
            if (!keep && !entityId.IsNull) CalcoImporter.Remove(database, entityId);
        }
    }

    // El rectángulo donde tiene que caber: el marco del plano en los layouts DETALLE y UC; si no, el que se dibuje.
    private static bool TryGetFrame(Editor editor, Database database, out ObjectId spaceId, out CalcoFrame frame, out string place)
    {
        spaceId = database.CurrentSpaceId;
        frame = default;
        place = "el espacio modelo";

        if (!database.TileMode)
        {
            string layoutName;
            ObjectId layoutSpace;
            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                ObjectId layoutId = LayoutManager.Current.GetLayoutId(LayoutManager.Current.CurrentLayout);
                var layout = (Layout)transaction.GetObject(layoutId, OpenMode.ForRead);
                layoutName = layout.LayoutName.Trim();
                layoutSpace = layout.BlockTableRecordId;
                transaction.Commit();
            }
            if (database.CurrentSpaceId != layoutSpace)
            {
                editor.WriteMessage("\n[CALCAR] Está dentro de un viewport (espacio modelo). Salga al espacio papel del layout e intente de nuevo.\n");
                return false;
            }

            place = layoutName;
            if (DetailLayout.IsMatch(layoutName))
            {
                frame = new CalcoFrame(ClonarUcTool.FrameMinX, ClonarUcTool.DetalleFrameMinY, ClonarUcTool.FrameMaxX, ClonarUcTool.DetalleFrameMaxY);
                return true;
            }
            if (UcLayout.IsMatch(layoutName))
            {
                frame = new CalcoFrame(ClonarUcTool.FrameMinX, ClonarUcTool.UcFrameMinY, ClonarUcTool.FrameMaxX, ClonarUcTool.UcFrameMaxY);
                return true;
            }
        }

        return AskFrame(editor, out frame);
    }

    private static bool AskFrame(Editor editor, out CalcoFrame frame)
    {
        frame = default;
        PromptPointResult first = editor.GetPoint(new PromptPointOptions("\nIndique el rectángulo donde debe caber: primera esquina: "));
        if (first.Status != PromptStatus.OK) return false;
        PromptPointResult second = editor.GetCorner(new PromptCornerOptions("\nEsquina opuesta: ", first.Value));
        if (second.Status != PromptStatus.OK) return false;

        Matrix3d ucs = editor.CurrentUserCoordinateSystem;
        frame = CalcoFrame.FromCorners(first.Value.TransformBy(ucs), second.Value.TransformBy(ucs));
        if (frame.IsEmpty(1e-6))
        {
            editor.WriteMessage("\n[CALCAR] El rectángulo no tiene área.\n");
            return false;
        }
        return true;
    }

    private static string? AskFile(Editor editor)
    {
        var options = new PromptOpenFileOptions("\nSeleccione la imagen o el PDF que va a calcar: ")
        {
            Filter = FileFilter,
            DialogCaption = "Importar imagen o PDF para calcar",
            PreferCommandLine = false,
        };
        PromptFileNameResult result = editor.GetFileNameForOpen(options);
        if (result.Status != PromptStatus.OK) return null;
        if (!File.Exists(result.StringResult))
        {
            editor.WriteMessage("\n[CALCAR] No se encontró el archivo.\n");
            return null;
        }
        return Path.GetFullPath(result.StringResult);
    }

    // Un PDF de una sola página no pregunta nada; si tiene varias (o no se pudo contar) se pide la página.
    private static bool AskPage(Editor editor, string path, out int page)
    {
        page = 1;
        int pages = CalcoImporter.CountPdfPages(path);
        if (pages == 1) return true;

        var options = new PromptIntegerOptions(pages > 1 ? $"\nPágina del PDF, de 1 a {pages} <1>: " : "\nPágina del PDF <1>: ")
        {
            AllowNegative = false,
            AllowZero = false,
            AllowNone = true,
            DefaultValue = 1,
            UseDefaultValue = true,
            LowerLimit = 1,
        };
        if (pages > 1) options.UpperLimit = pages;
        PromptIntegerResult result = editor.GetInteger(options);
        if (result.Status == PromptStatus.None) return true;
        if (result.Status != PromptStatus.OK) return false;
        page = result.Value;
        return true;
    }

    // Pide el rectángulo de lo que se va a calcar. true = hay recorte; false = usar todo, o cancelar si cancelled.
    private static bool AskCrop(Editor editor, CalcoFrame content, out CalcoFrame crop, out bool cancelled)
    {
        crop = default;
        cancelled = false;

        var firstOptions = new PromptPointOptions("\nDibuje el rectángulo de lo que va a calcar: primera esquina (Enter o clic derecho para usar todo): ") { AllowNone = true };
        PromptPointResult first = editor.GetPoint(firstOptions);
        if (first.Status == PromptStatus.None) return false;
        if (first.Status != PromptStatus.OK) { cancelled = true; return false; }

        PromptPointResult second = editor.GetCorner(new PromptCornerOptions("\nEsquina opuesta: ", first.Value));
        if (second.Status != PromptStatus.OK) { cancelled = true; return false; }

        Matrix3d ucs = editor.CurrentUserCoordinateSystem;
        crop = CalcoFrame.FromCorners(first.Value.TransformBy(ucs), second.Value.TransformBy(ucs));
        CalcoFrame visible = crop.Intersect(content);
        if (visible.Width < content.Width * MinCropFraction || visible.Height < content.Height * MinCropFraction)
        {
            editor.WriteMessage("\n[CALCAR] El rectángulo quedó fuera de la imagen o es muy pequeño: se usa la imagen completa.\n");
            return false;
        }
        return true;
    }
}
