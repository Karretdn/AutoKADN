using System.IO;

namespace AutoKADN.Proyectos.Services;

public sealed record ProjectRequest(
    string RootFolder,
    string ResourcesFolder,
    string InterventorFolder,
    string InterventorFullName,
    string Supervisor,
    string FolderName,
    string Orden,
    string IdProyecto,
    string Proyecto,
    string Localidad,
    string Departamento,
    DateTime InicioObra,
    DateTime FinalObra,
    DateTime PruebaInicial,
    DateTime PruebaFinal,
    DateTime Gasificado,
    string Diametro,
    string Tuberia,
    bool IncludeLibro,
    string OrderPdfPath);

public sealed record CreationStep(string Description, bool Ok, string? Detail = null);

public sealed record CreationResult(bool Success, string ProjectFolder, IReadOnlyList<CreationStep> Steps, string? Error);

/// <summary>
/// Crea la carpeta del proyecto (raíz\INTERVENTOR\orden - proyecto) con PLANOS, SOPORTES y FOTOS, copia la
/// orden de trabajo, prepara el formato de legalización con su cabecera, copia el formato de fotos y, si se
/// pidió, el libro de cantidades. Nunca toca una carpeta que ya exista; si algo falla a mitad, se retira la
/// carpeta que esta misma ejecución acababa de crear para poder reintentar.
/// </summary>
public static class ProjectCreator
{
    public const string SoporteFileName = "XX X PULG.xlsx";

    public static CreationResult Create(ProjectRequest request)
    {
        var steps = new List<CreationStep>();
        string interventorFolder = Path.Combine(request.RootFolder, request.InterventorFolder);
        string projectFolder = Path.Combine(interventorFolder, request.FolderName);

        if (!Directory.Exists(request.RootFolder))
            return Fail(projectFolder, steps, "La carpeta raíz no existe: " + request.RootFolder);
        if (Directory.Exists(projectFolder))
            return Fail(projectFolder, steps, "La carpeta del proyecto ya existe. No se sobrescribe nada.");

        List<string> missing = AppSettings.MissingResources(request.ResourcesFolder);
        if (missing.Count > 0)
            return Fail(projectFolder, steps, "Faltan archivos base: " + string.Join(", ", missing));
        if (!File.Exists(request.OrderPdfPath))
            return Fail(projectFolder, steps, "No se encuentra la orden de trabajo: " + request.OrderPdfPath);

        string current = "Carpeta del proyecto";
        try
        {
            Directory.CreateDirectory(projectFolder); // también crea la carpeta del interventor si faltara
            string planos = Directory.CreateDirectory(Path.Combine(projectFolder, "PLANOS")).FullName;
            string soportes = Directory.CreateDirectory(Path.Combine(projectFolder, "SOPORTES")).FullName;
            string fotos = Directory.CreateDirectory(Path.Combine(projectFolder, "FOTOS")).FullName;
            steps.Add(new CreationStep("Carpetas PLANOS, SOPORTES y FOTOS", true, projectFolder));

            current = "Orden de trabajo";
            string orderCopy = Path.Combine(projectFolder, Path.GetFileName(request.OrderPdfPath));
            File.Copy(request.OrderPdfPath, orderCopy);
            steps.Add(new CreationStep("Orden de trabajo (PDF)", true, Path.GetFileName(orderCopy)));

            current = "Formato de legalización";
            string soporte = Path.Combine(soportes, SoporteFileName);
            File.Copy(Path.Combine(request.ResourcesFolder, ResourceNames.Soportes), soporte);
            File.SetAttributes(soporte, FileAttributes.Normal); // la copia de la plantilla no debe quedar de solo lectura
            SoporteWriter.FillHeader(soporte, new Dictionary<string, object>
            {
                ["C6"] = $"{request.Localidad} - {request.Departamento}",
                ["C8"] = NumberOrText(request.Orden),
                ["C9"] = request.Supervisor,
                ["F9"] = request.InterventorFullName,
                ["F11"] = request.Proyecto,
                ["C11"] = NumberOrText(request.IdProyecto),
                ["C12"] = DateParser.ForExcel(request.InicioObra),
                ["F12"] = DateParser.ForExcel(request.FinalObra),
            });
            steps.Add(new CreationStep("Formato de legalización con la cabecera rellena", true, @"SOPORTES\" + SoporteFileName));

            current = "Formato de fotos";
            string fotosFile = Path.Combine(fotos, ResourceNames.Fotos);
            File.Copy(Path.Combine(request.ResourcesFolder, ResourceNames.Fotos), fotosFile);
            File.SetAttributes(fotosFile, FileAttributes.Normal);
            steps.Add(new CreationStep("Formato de fotos", true, @"FOTOS\" + ResourceNames.Fotos));

            // El plano se rellena desde AutoKADN: aquí solo se deja el DWG base y los datos junto a él.
            current = "Plano";
            string dwg = Path.Combine(planos, request.FolderName + ".dwg");
            File.Copy(Path.Combine(request.ResourcesFolder, ResourceNames.Plano), dwg);
            File.SetAttributes(dwg, FileAttributes.Normal);
            steps.Add(new CreationStep("Plano base (DWG)", true, @"PLANOS\" + Path.GetFileName(dwg)));

            current = "Datos del plano";
            // Los JSON de AutoKADN viven en la carpeta raíz del proyecto (junto a PLANOS, SOPORTES y FOTOS).
            ProjectDataFile.Write(Path.Combine(projectFolder, ProjectDataFile.FileName), request, DateTime.Now);
            steps.Add(new CreationStep("Datos para el cajetín y los formatos (AutoKADN)", true, ProjectDataFile.FileName));

            if (request.IncludeLibro)
            {
                current = "Libro de cantidades";
                string libro = Path.Combine(projectFolder, ResourceNames.Libro);
                File.Copy(Path.Combine(request.ResourcesFolder, ResourceNames.Libro), libro);
                File.SetAttributes(libro, FileAttributes.Normal);
                steps.Add(new CreationStep("Libro de cantidades", true, ResourceNames.Libro));
            }

            return new CreationResult(true, projectFolder, steps, null);
        }
        catch (Exception ex)
        {
            steps.Add(new CreationStep(current, false, ex.Message));
            RemoveFreshFolder(projectFolder);
            return new CreationResult(false, projectFolder, steps, $"Falló «{current}»: {ex.Message}. Se retiró la carpeta incompleta; puedes reintentar.");
        }
    }

    // Número si es solo dígitos (como en los formatos reales); si no, texto.
    private static object NumberOrText(string value) =>
        long.TryParse(value, out long number) ? number : value;

    private static CreationResult Fail(string projectFolder, List<CreationStep> steps, string error)
    {
        steps.Add(new CreationStep("Validación", false, error));
        return new CreationResult(false, projectFolder, steps, error);
    }

    // Solo se llama con la carpeta que esta misma ejecución creó (se comprobó antes que no existía).
    private static void RemoveFreshFolder(string projectFolder)
    {
        try
        {
            if (Directory.Exists(projectFolder)) Directory.Delete(projectFolder, recursive: true);
        }
        catch { /* si no se puede borrar, queda para que la persona la revise */ }
    }
}
