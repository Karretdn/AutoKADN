namespace AutoKADN.Mapeo.Detection;

// Claves del flujo (datos_proyecto.json de AutoKADN.Proyectos) que se pueden conectar a una celda.
// Aquí solo se SUGIERE: la persona confirma o corrige en la app.
public static class FlowBindings
{
    // Claves que hoy escribe AutoKADN.Proyectos en PLANOS\datos_proyecto.json.
    public static readonly string[] KnownKeys =
    {
        "orden", "idProyecto", "proyecto", "localidad", "departamento", "municipio", "interventor", "supervisor",
        "tuberia", "diametro", "fechaInicioObra", "fechaFinalObra", "fechaPruebaInicial", "fechaPruebaFinal", "fechaGasificado",
    };

    public static string Suggest(string id)
    {
        string s = id.ToLowerInvariant();
        bool Has(string part) => s.Contains(part);

        if (Has("orden") && (Has("trabajo") || s == "orden")) return "orden";
        if (s is "interventor" or "interventor.nombre" or "interventor_nombre") return "interventor";
        if (s == "localidad") return "localidad";
        if (s == "departamento") return "departamento";
        if (s == "municipio") return "municipio";
        if (s is "obra" or "nombre_de_la_obra") return "proyecto";
        if (Has("fecha") && Has("inicio") && Has("obra")) return "fechaInicioObra";
        if (Has("fecha") && Has("final") && Has("obra")) return "fechaFinalObra";
        return "";
    }
}
