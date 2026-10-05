using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AutoKADN.Proyectos.Services;

/// <summary>
/// datos_proyecto.json: el puente entre esta app y AutoKADN. Se guarda en la carpeta raíz del proyecto (junto a PLANOS); el comando
/// de AutoKADN que rellena el cajetín lo busca en la carpeta del dibujo abierto.
/// Es un objeto plano con valores de texto, para que cualquier versión del plugin pueda leerlo sin
/// librerías adicionales. Las fechas van en ISO (aaaa-mm-dd) y, para el plano, ya con su formato final.
/// </summary>
public static class ProjectDataFile
{
    public const string FileName = "datos_proyecto.json";
    public const string CurrentVersion = "1";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // los acentos se guardan legibles
    };

    public static SortedDictionary<string, string> Build(ProjectRequest request, DateTime createdAt)
    {
        string Iso(DateTime date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["version"] = CurrentVersion,
            ["creado"] = createdAt.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
            ["orden"] = request.Orden,
            ["idProyecto"] = request.IdProyecto,
            ["proyecto"] = request.Proyecto,
            ["localidad"] = request.Localidad,
            ["departamento"] = request.Departamento,
            ["municipio"] = $"{request.Localidad} - {request.Departamento}",
            ["interventor"] = request.InterventorFullName,
            ["supervisor"] = request.Supervisor,
            ["tuberia"] = request.Tuberia,
            ["diametro"] = request.Diametro,
            ["fechaInicioObra"] = Iso(request.InicioObra),
            ["fechaFinalObra"] = Iso(request.FinalObra),
            ["fechaPruebaInicial"] = Iso(request.PruebaInicial),
            ["fechaPruebaFinal"] = Iso(request.PruebaFinal),
            ["fechaGasificado"] = Iso(request.Gasificado),
            ["planoPruebaInicial"] = DateParser.ForPlano(request.PruebaInicial),
            ["planoPruebaFinal"] = DateParser.ForPlano(request.PruebaFinal),
            ["planoGasificado"] = DateParser.ForPlano(request.Gasificado),
        };
    }

    public static void Write(string path, ProjectRequest request, DateTime createdAt) =>
        File.WriteAllText(path, JsonSerializer.Serialize(Build(request, createdAt), Options), new UTF8Encoding(false));
}
