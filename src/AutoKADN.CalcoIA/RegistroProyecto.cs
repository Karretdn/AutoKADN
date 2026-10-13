using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AutoKADN.CalcoIA;

// Anillos ya calcados de un proyecto (el PDF del gasificado): cada «entregar» deja anillo-<n>.json en
// %LOCALAPPDATA%\AutoKADN\CalcoIA\proyectos\<huella del PDF>. Al calcar otra hoja del mismo PDF se leen los demás para
// completar la red de las manzanas vecinas que se ven en ella (ver Enderezador.CompletarVecinos).
public static class RegistroProyecto
{
    private static string Carpeta(string fuente)
    {
        byte[] huella = SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(fuente).ToLowerInvariant()));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoKADN", "CalcoIA", "proyectos", Convert.ToHexString(huella)[..16]);
    }

    public static Dictionary<string, AnilloRegistrado> Leer(string fuente, string? propio)
    {
        var anillos = new Dictionary<string, AnilloRegistrado>();
        if (string.IsNullOrWhiteSpace(fuente)) return anillos;
        string carpeta = Carpeta(fuente);
        if (!Directory.Exists(carpeta)) return anillos;
        foreach (string archivo in Directory.EnumerateFiles(carpeta, "anillo-*.json"))
        {
            try
            {
                AnilloRegistrado? a = JsonSerializer.Deserialize<AnilloRegistrado>(File.ReadAllText(archivo), Json.Opciones);
                if (a != null && a.Numero.Length > 0 && a.Numero != propio?.Trim()) anillos[a.Numero] = a;
            }
            catch (JsonException) { /* un registro dañado no tumba el calco */ }
        }
        return anillos;
    }

    public static void Guardar(string fuente, AnilloRegistrado anillo)
    {
        if (string.IsNullOrWhiteSpace(fuente) || anillo.Numero.Length == 0) return;
        try
        {
            string carpeta = Carpeta(fuente);
            Directory.CreateDirectory(carpeta);
            File.WriteAllText(Path.Combine(carpeta, $"anillo-{anillo.Numero}.json"), JsonSerializer.Serialize(anillo, Json.Indentado));
        }
        catch (IOException) { /* sin registro, las demás hojas se calcan como antes */ }
    }
}
