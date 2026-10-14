using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AutoKADN.CalcoIA;

public sealed class ResultadoHerramienta
{
    public string Texto { get; init; } = string.Empty;
    public byte[]? Png { get; init; }
    public bool Error { get; init; }
    public bool Entregado { get; init; }
}

// Las herramientas del calco. Las mismas para los dos motores: Claude Code las recibe por MCP (McpServidor) y la API
// de Anthropic como herramientas del mensaje (MotorApi). Todas trabajan sobre la carpeta de un trabajo.
public sealed class Herramientas
{
    private const int LadoImagen = 1400;  // px: lado largo de las imágenes que se le muestran a la IA
    private readonly Trabajo _trabajo;
    private int _enderezados;
    private int _superposiciones;
    private bool _pidioSuperponer, _pidioRevisar, _pidioPlano, _vioPlano;

    public Herramientas(Trabajo trabajo) { _trabajo = trabajo; }

    public static readonly (string Nombre, string Descripcion, string Esquema)[] Definiciones =
    [
        ("ver_hoja",
         "Muestra la zona de la hoja que eligió el usuario (el croquis), reducida y con una cuadrícula rotulada en píxeles de la hoja completa. Empiece por aquí.",
         """{"type":"object","properties":{},"additionalProperties":false}"""),
        ("ampliar",
         "Muestra una zona de la hoja ampliada, con cuadrícula en píxeles de la hoja completa, para leer letra a mano (números de predio, cotas, nombres de vías) y ver detalles (marcas de unión, tapones, reducciones).",
         """{"type":"object","properties":{"x":{"type":"number","description":"izquierda (px de la hoja)"},"y":{"type":"number","description":"arriba (px de la hoja)"},"ancho":{"type":"number"},"alto":{"type":"number"}},"required":["x","y","ancho","alto"],"additionalProperties":false}"""),
        ("medir_lineas",
         "Mide la posición exacta de líneas rectas en una franja de la hoja con el perfil de píxeles de tinta. 'horizontales' recorre filas (da la y de cada línea que cruza la franja), 'verticales' recorre columnas (da la x). Use franjas angostas (100 a 250 px) que crucen las líneas: las líneas a mano están algo inclinadas. Nada se ubica a ojo: mida bordes de manzana, líneas de predio y tubería.",
         """{"type":"object","properties":{"direccion":{"type":"string","enum":["horizontales","verticales"]},"x0":{"type":"number"},"y0":{"type":"number"},"x1":{"type":"number"},"y1":{"type":"number"},"fraccion":{"type":"number","description":"fracción mínima de la franja que debe ser tinta (0.45 por defecto; 0.3 para líneas cortadas)"},"color":{"type":"string","enum":["oscuro","amarillo","azul"],"description":"oscuro: lápiz o lapicero (por defecto); amarillo: resaltador; azul: tinta azul"},"umbral":{"type":"number","description":"luminancia máxima de la tinta oscura (120 por defecto; 175 para lápiz claro)"}},"required":["direccion","x0","y0","x1","y1"],"additionalProperties":false}"""),
        ("enderezar",
         "Endereza el croquis con las reglas de dibujo de AutoKADN (90°, tubería a la separación estándar, vías al doble, ajuste al marco, cruces, accesorios) y devuelve la vista previa del plano con un resumen y avisos. Úsela las veces que haga falta y compare la vista con la hoja antes de entregar.",
         """{"type":"object","properties":{"croquis":ESQUEMA_CROQUIS},"required":["croquis"],"additionalProperties":false}"""),
        ("superponer",
         "Dibuja el croquis encima de la hoja (aclarada) para compararlo con lo que hay dibujado a mano: rojo la manzana, naranja los predios, café los vecinos, azul la tubería (continua la nueva con su cota en medio de cada tramo, punteada la existente), magenta el cruce y los accesorios (círculo con su letra), verde los números de predio y los indicadores, morado las vías, verde azulado las cercas (con una x en cada punto) y café claro las casas (con su techo y su rótulo; punteadas si están en construcción). Todo lo de la hoja debe quedar cubierto y en su sitio. Sin x/y/ancho/alto muestra la zona elegida; con ellos, amplía esa parte. Úsela antes de entregar.",
         """{"type":"object","properties":{"croquis":ESQUEMA_CROQUIS,"x":{"type":"number"},"y":{"type":"number"},"ancho":{"type":"number"},"alto":{"type":"number"}},"required":["croquis"],"additionalProperties":false}"""),
        ("entregar",
         "Entrega el croquis final (lo endereza igual que 'enderezar' y lo deja listo para que AutoKADN lo dibuje). Llámela una sola vez, al final, con las dudas que el usuario deba revisar en el campo 'dudas' del croquis.",
         """{"type":"object","properties":{"croquis":ESQUEMA_CROQUIS},"required":["croquis"],"additionalProperties":false}"""),
        ("ver_paginas",
         "Miniaturas de todas las páginas del PDF de la hoja (el gasificado), con su número; la de este croquis va marcada. Sirve para encontrar el plano del proyecto (un plano impreso de Gases del Caribe, casi siempre girado, con los anillos numerados y resaltados).",
         """{"type":"object","properties":{},"additionalProperties":false}"""),
        ("ver_pagina",
         "Muestra otra página del PDF (p. ej. el plano del proyecto), con cuadrícula en píxeles de esa página. Con x/y/ancho/alto amplía una zona; con giro (90, 180, 270, antihorario) la endereza para leer lo escrito de lado. Las coordenadas de esa página NO son las de la hoja del croquis: úsela para saber qué hay alrededor y cómo se llaman las vías, no para medir.",
         """{"type":"object","properties":{"pagina":{"type":"integer"},"x":{"type":"number"},"y":{"type":"number"},"ancho":{"type":"number"},"alto":{"type":"number"},"giro":{"type":"integer","enum":[0,90,180,270]}},"required":["pagina"],"additionalProperties":false}"""),
    ];

    // Modo detallar: la hoja (ver_hoja, ampliar) y el anillo ya dibujado, con los ids a los que van los números.
    public static readonly (string Nombre, string Descripcion, string Esquema)[] DefinicionesDetalle =
    [
        Definiciones[0],
        Definiciones[1],
        ("ver_dibujo",
         "Muestra el anillo YA DIBUJADO en AutoCAD con sus ids: tramos nuevos T1, T2… (azul, flecha del inicio 0 al fin 1), existentes E1… (punteados) y accesorios A1… (rojo). Los números del croquis se asignan a estos ids. El dibujo no va a escala y puede estar girado o reflejado respecto al croquis.",
         """{"type":"object","properties":{},"additionalProperties":false}"""),
        ("revisar_detalle",
         "Revisa un detalle sin entregarlo: valida los ids y los valores, compara totales y accesorios, y devuelve el dibujo con cada número donde caería (largos en verde, ubicaciones y cotas de accesorio en magenta). Úsela antes de entregar.",
         """{"type":"object","properties":{"detalle":ESQUEMA_DETALLE},"required":["detalle"],"additionalProperties":false}"""),
        ("entregar_detalle",
         "Entrega el detalle final (una sola vez). Si la hoja NO corresponde al dibujo (otro anillo, otra O.T., otra forma o cantidad de tramos), entregue corresponde=false con los motivos y nada más.",
         """{"type":"object","properties":{"detalle":ESQUEMA_DETALLE},"required":["detalle"],"additionalProperties":false}"""),
    ];

    public static (string Nombre, string Descripcion, string Esquema)[] Para(Trabajo trabajo) => trabajo.EsDetalle ? DefinicionesDetalle : Definiciones;

    public static string EsquemaDetalle => """
    {"type":"object","properties":{
      "corresponde":{"type":"boolean","description":"la hoja es la de este anillo dibujado (mismo anillo, O.T., forma y cantidad de tramos)"},
      "motivos":{"type":"array","items":{"type":"string"},"description":"si no corresponde: por qué (p. ej. «la hoja es del anillo 2 y el dibujo del anillo 1»)"},
      "diametro":{"type":"string","description":"diámetro del anillo según el croquis: 1/2 o 3/4"},
      "largos":{"type":"array","items":{"type":"object","properties":{"tramo":{"type":"string","description":"id de un tramo nuevo (T1…)"},"texto":{"type":"string","description":"cota de largo con un decimal: 43.5, 13.0; 0.1 en los tramitos del cruce"},"diametro":{"type":"string","description":"solo si el tramo es de otro diámetro que el anillo y ver_dibujo no lo dice (p. ej. 3/4 en el cruce)"}},"required":["tramo","texto"]},"description":"una cota de largo por cada tramo nuevo (incluidos el cruce y sus tramitos)"},
      "ubicaciones":{"type":"array","items":{"type":"object","properties":{"tramo":{"type":"string","description":"id del tramo (nuevo o existente) donde está la cota"},"en":{"type":"number","description":"dónde está a lo largo del tramo: 0 = su inicio, 1 = su fin (según la flecha de ver_dibujo)"},"texto":{"type":"string","description":"la medida del croquis: 0.5, 1.6"},"hacia":{"type":"string","enum":["arriba","abajo","izquierda","derecha"],"description":"lado EN EL DIBUJO donde está la cara que acota (obligatorio si la tubería tiene caras a los dos lados, p. ej. una red en medio de la calle)"}},"required":["tramo","en","texto"]},"description":"TODAS las cotas de ubicación (tubería a la cara, predio o bordillo) del croquis, en el orden en que importan; AutoKADN pone las que quepan"},
      "accesorios":{"type":"array","items":{"type":"object","properties":{"accesorio":{"type":"string","description":"id del accesorio (A1…)"},"hacia":{"type":"string","enum":["arriba","abajo","izquierda","derecha"],"description":"dirección EN EL DIBUJO desde el accesorio hacia la cara que acota"},"texto":{"type":"string","description":"la medida; 0.0 si el accesorio está alineado con la cara (LC0.0)"}},"required":["accesorio","texto"]},"description":"cotas de ubicación de los accesorios a lo largo del tramo (de la unión a la cara de una torre, predio o esquina)"},
      "total":{"type":"string","description":"total de tubería del diámetro del anillo que dice el cuadro del croquis"},
      "cuadroAccesorios":{"type":"object","additionalProperties":{"type":"integer"},"description":"cantidad de cada accesorio del cuadro del croquis, p. ej. {\"UNION\": 3}"},
      "dudas":{"type":"array","items":{"type":"string"},"description":"lo que no se pudo leer con seguridad"}},
     "required":["corresponde"]}
    """;

    private const string Punto = """{"type":"array","items":{"type":"number"},"minItems":2,"maxItems":2}""";

    public static string EsquemaCroquis => """
    {"type":"object","description":"Lo que se ve en la hoja, en píxeles de la hoja completa (origen arriba a la izquierda). Coordenadas medidas, más o menos derechas: el enderezado pone los 90°.",
     "properties":{
      "manzana":{"type":"array","items":PUNTO,"description":"contorno de la manzana del anillo, en orden (con escalones si los tiene)"},
      "predios":{"type":"array","items":{"type":"array","items":PUNTO,"minItems":2,"maxItems":2},"description":"cada línea recta que separa predios, de extremo a extremo"},
      "construcciones":{"type":"array","items":{"type":"array","items":PUNTO},"description":"polígonos cerrados dibujados dentro de un predio que no son una casa (casetas, construcciones)"},
      "casas":{"type":"array","items":{"type":"object","properties":{"caja":{"type":"array","items":{"type":"number"},"minItems":4,"maxItems":4,"description":"[x0, y0, x1, y1]: el rectángulo de la casa"},"rotulo":{"type":"string","description":"lo que dice dentro: \"S.N.\" (sin número, en el croquis «SN») o el número de la casa (\"7BS-232\")"},"techo":{"type":"boolean","description":"el croquis la dibuja con el techo (líneas de las esquinas a una línea en medio); false si es un rectángulo solo o rayado"},"enConstruccion":{"type":"boolean","description":"el croquis dice «EN CONST» junto a ella o con una flecha a ella"}},"required":["caja"]},"description":"cada casa dibujada en el croquis (en la manzana del anillo o en las vecinas)"},
      "cercas":{"type":"array","items":{"type":"array","items":PUNTO},"description":"cada cerca «x x x x», como polilínea abierta por donde va (casi siempre sobre el borde de una manzana: dé ese borde)"},
      "textos":{"type":"array","items":{"type":"object","properties":{"texto":{"type":"string"},"x":{"type":"number"},"y":{"type":"number"},"giro":{"type":"number","description":"0 horizontal, 90 vertical"}},"required":["texto","x","y"]},"description":"número de cada predio (\"LOTE\" si no tiene) en el centro del predio, y los textos de las manzanas vecinas como «LOTES» donde están escritos"},
      "vecinos":{"type":"array","items":{"type":"object","properties":{"borde":{"type":"array","items":PUNTO}},"required":["borde"]},"description":"lo que se ve de manzanas vecinas, como polilínea abierta"},
      "indicadores":{"type":"array","items":{"type":"object","properties":{"numero":{"type":"string"},"diametro":{"type":"string","description":"1/2 o 3/4"},"viviendas":{"type":"string","description":"\"10 VIVIENDAS\" (solo el anillo propio)"},"x":{"type":"number"},"y":{"type":"number"}},"required":["numero","diametro","x","y"]},"description":"círculos n | ∅: el del anillo y los de anillos vecinos"},
      "vias":{"type":"array","items":{"type":"object","properties":{"nombre":{"type":"string","description":"formato NOMENK: \"KR 34D - T.N.\""},"x":{"type":"number"},"y":{"type":"number"},"giro":{"type":"number","description":"cómo está escrito el nombre: 0 horizontal, 90 vertical (sigue la dirección de la calle)"}},"required":["nombre","x","y"]},"description":"nombre de cada vía: un punto en medio de la calle que nombra, con su giro"},
      "tuberias":{"type":"array","items":{"type":"object","properties":{
         "tipo":{"type":"string","enum":["nueva","existente","cruce"],"description":"nueva: tramos con cota de longitud (continua); existente: sin cota (punteada); cruce: el tramo que atraviesa una calle entre dos redes"},
         "diametro":{"type":"string","description":"1/2 o 3/4 (nueva y existente)"},
         "puntos":{"type":"array","items":PUNTO,"description":"recorrido por el centro de la línea dibujada; en el cruce, inicio (en la tubería nueva) y fin (en la otra red)"},
         "cerrada":{"type":"boolean","description":"el anillo da toda la vuelta"},
         "cotas":{"type":"array","items":{"type":["string","null"]},"description":"cota de longitud de cada tramo (punto i a i+1, y el de cierre si es cerrada); null si el tramo no tiene. En el cruce: su largo"},
         "diametros":{"type":"array","items":{"type":"string"},"description":"solo cruce: diámetro de la red en el inicio y en el fin"}},
        "required":["tipo","puntos"]}},
      "accesorios":{"type":"array","items":{"type":"object","properties":{"nombre":{"type":"string","enum":["TAPON","TEE","UNION","VALVULA","SILLETA","REDUCCION"]},"x":{"type":"number"},"y":{"type":"number"},"diametro":{"type":"string"},"ubicacion":{"type":"object","properties":{"texto":{"type":"string","description":"la cota del croquis desde el accesorio a una cara (1.4); 0.0 si está alineado con ella"},"hacia":{"type":"string","enum":["arriba","abajo","izquierda","derecha"],"description":"dónde está esa cara respecto al accesorio, en la hoja"}},"required":["texto"]}},"required":["nombre","x","y"]},"description":"accesorios marcados en el croquis (no los del cruce: esos los pone el enderezado)"},
      "abierta":{"type":"array","items":{"type":"string","enum":["arriba","abajo","izquierda","derecha"]},"description":"lados de la manzana (o torres) que el croquis corta en el borde de la hoja y siguen más allá: no se cierran y llegan al borde del marco"},
      "superficie":{"type":"string","description":"UC del cuadro: ZONA VERDE, ANDEN CONCRETO…"},
      "cuadro":{"type":"object","additionalProperties":{"type":"string"},"description":"total de tubería por diámetro que dice el cuadro de UC, p. ej. {\"1/2\": \"157.7\", \"3/4\": \"2\"}"},
      "cuadroAccesorios":{"type":"object","additionalProperties":{"type":"integer"},"description":"cantidad de cada accesorio que dice el cuadro, p. ej. {\"UNION\": 1, \"TEE\": 2, \"REDUCCION\": 2}"},
      "dudas":{"type":"array","items":{"type":"string"},"description":"lo que no se pudo leer con seguridad, para que el usuario lo revise"}},
     "required":["manzana","tuberias"]}
    """.Replace("PUNTO", Punto);

    public static JsonObject Esquema(string esquema) => (JsonObject)JsonNode.Parse(esquema.Replace("ESQUEMA_CROQUIS", EsquemaCroquis).Replace("ESQUEMA_DETALLE", EsquemaDetalle))!;

    public ResultadoHerramienta Ejecutar(string nombre, JsonElement entrada)
    {
        try
        {
            _trabajo.Registrar($"herramienta {nombre} {Recortar(entrada.GetRawText(), 300)}");
            return nombre switch
            {
                "ver_hoja" => VerHoja(),
                "ampliar" => Ampliar(entrada),
                "medir_lineas" => Medir(entrada),
                "enderezar" => Enderezar(entrada, final: false),
                "entregar" => Enderezar(entrada, final: true),
                "superponer" => Superponer(entrada),
                "ver_paginas" => VerPaginas(),
                "ver_pagina" => VerPagina(entrada),
                "ver_dibujo" => VerDibujo(),
                "revisar_detalle" => Detallar(entrada, final: false),
                "entregar_detalle" => Detallar(entrada, final: true),
                _ => new ResultadoHerramienta { Texto = $"herramienta desconocida: {nombre}", Error = true },
            };
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
        {
            _trabajo.Registrar($"error en {nombre}: {ex.Message}");
            return new ResultadoHerramienta { Texto = $"Error: {ex.Message}", Error = true };
        }
    }

    private ResultadoHerramienta VerHoja()
    {
        Imagen imagen = _trabajo.Imagen;
        Rect recorte = _trabajo.Recorte;
        byte[] png = imagen.Zona(recorte, LadoImagen, out double escala);
        return new ResultadoHerramienta
        {
            Png = png,
            Texto = FormattableString.Invariant(
                $"Hoja de {imagen.Ancho} × {imagen.Alto} px. La imagen muestra x {recorte.X}–{recorte.X1}, y {recorte.Y}–{recorte.Y1} (la zona que eligió el usuario) a escala {escala:0.###}. Todas las coordenadas (herramientas y croquis) van en píxeles de la hoja completa, como los rótulos de la cuadrícula."),
        };
    }

    private ResultadoHerramienta Ampliar(JsonElement e)
    {
        var zona = new Rect((int)Numero(e, "x"), (int)Numero(e, "y"), (int)Numero(e, "ancho"), (int)Numero(e, "alto"));
        zona = _trabajo.Imagen.Recortar(zona);
        byte[] png = _trabajo.Imagen.Zona(zona, LadoImagen, out double escala);
        return new ResultadoHerramienta { Png = png, Texto = FormattableString.Invariant($"x {zona.X}–{zona.X1}, y {zona.Y}–{zona.Y1} a escala {escala:0.###}.") };
    }

    private ResultadoHerramienta Medir(JsonElement e)
    {
        bool filas = Texto(e, "direccion", "horizontales") == "horizontales";
        int x0 = (int)Numero(e, "x0"), y0 = (int)Numero(e, "y0"), x1 = (int)Numero(e, "x1"), y1 = (int)Numero(e, "y1");
        var zona = new Rect(Math.Min(x0, x1), Math.Min(y0, y1), Math.Abs(x1 - x0), Math.Abs(y1 - y0));
        string texto = _trabajo.Imagen.Perfil(filas, zona, Numero(e, "fraccion", 0.45), Texto(e, "color", "oscuro"), (int)Numero(e, "umbral", 120));
        return new ResultadoHerramienta { Texto = texto };
    }

    private ResultadoHerramienta Enderezar(JsonElement e, bool final)
    {
        // Antes de entregar, la IA tiene que haber comparado su croquis con la hoja (una vez lo pide; no lo bloquea).
        if (final && _superposiciones == 0 && !_pidioSuperponer)
        {
            _pidioSuperponer = true;
            return new ResultadoHerramienta
            {
                Error = true,
                Texto = "Todavía no se entregó: antes compare el croquis con la hoja usando superponer (con el mismo croquis) y amplíe las partes dudosas. Corrija lo que falte o esté corrido y vuelva a entregar.",
            };
        }
        if (!e.TryGetProperty("croquis", out JsonElement c)) throw new ArgumentException("falta 'croquis'");
        Croquis croquis = c.Deserialize<Croquis>(Json.Opciones) ?? throw new ArgumentException("croquis vacío");
        string? anillo = Anillo(_trabajo.Pedido.Layout);
        // La IA describe la hoja derecha; si en el dibujo está girada, el plano sale girado igual.
        int giro = Giro.Normalizar(_trabajo.Pedido.Giro);
        Imagen hoja = _trabajo.Imagen;
        Croquis girado = Giro.Croquis(croquis, giro, hoja.Ancho, hoja.Alto);
        Rect recorte = Giro.Rectangulo(_trabajo.Recorte, giro, hoja.Ancho, hoja.Alto);
        ResultadoEnderezado r = Enderezador.Enderezar(girado, _trabajo.Parametros, _trabajo.Pedido.Tamanos, recorte, anillo, _trabajo.Pedido.Diametro,
            RegistroProyecto.Leer(_trabajo.Pedido.Fuente, anillo));
        if (giro != 0) r.Resumen.Insert(0, $"Hoja girada {giro}° en el dibujo: el plano sale girado igual");
        byte[] vista = Vista.Dibujar(r.Plano, _trabajo.Parametros, _trabajo.Pedido.Tamanos);

        _enderezados++;
        File.WriteAllText(_trabajo.Archivo("croquis.json"), JsonSerializer.Serialize(croquis, Json.Indentado));
        File.WriteAllText(_trabajo.Archivo("plano.json"), JsonSerializer.Serialize(r.Plano, Json.Indentado));
        File.WriteAllBytes(_trabajo.Archivo("vista.png"), vista);

        var texto = new StringBuilder();
        texto.AppendLine(final ? "Entregado. AutoKADN lo dibujará así:" : $"Vista previa (intento {_enderezados}):");
        foreach (string linea in r.Resumen) texto.AppendLine("- " + linea);
        if (r.Avisos.Count > 0)
        {
            texto.AppendLine("Avisos:");
            foreach (string aviso in r.Avisos) texto.AppendLine("- " + aviso);
        }
        // Si al entregar quedan avisos de lo que no cuadra con el cuadro de la hoja, se pide revisarlos una vez.
        // Si quedan vías sin nombre o lados sin manzana de enfrente y la IA no miró el plano del proyecto, se le pide una vez.
        if (final && !_vioPlano && !_pidioPlano && (r.ViasSinNombre.Count > 0 || r.SinVecina.Count > 0) && _trabajo.EsPdf && _trabajo.Paginas > 1)
        {
            _pidioPlano = true;
            var pide = new StringBuilder("Todavía no se entregó: mire antes el plano del proyecto (ver_paginas y luego ver_pagina, con giro si está de lado).\n");
            if (r.ViasSinNombre.Count > 0) pide.AppendLine($"- Vías sin nombre en el croquis (lado {string.Join(", ", r.ViasSinNombre)} de la manzana): tome el nombre del plano (regla 17).");
            if (r.SinVecina.Count > 0) pide.AppendLine($"- Lados sin manzana de enfrente (lado {string.Join(", ", r.SinVecina)}): si el plano muestra manzana ahí, agréguela en vecinos (regla 21).");
            pide.AppendLine("Si el plano no trae lo que falta, entregue de nuevo y anótelo en dudas.");
            return new ResultadoHerramienta { Error = true, Texto = pide.ToString() };
        }
        List<string> pendientes = r.Avisos.Where(a => a.StartsWith("el cuadro dice", StringComparison.Ordinal) || a.StartsWith("el layout es de", StringComparison.Ordinal)).ToList();
        if (final && pendientes.Count > 0 && !_pidioRevisar)
        {
            _pidioRevisar = true;
            var pide = new StringBuilder("Todavía no se entregó: esto no cuadra con el cuadro de la hoja. Vuelva a mirar (superponer, ampliar el cuadro y las cotas) y corrija; si después de revisar sigue igual, entregue de nuevo y explíquelo en dudas.\n");
            foreach (string p in pendientes) pide.AppendLine("- " + p);
            return new ResultadoHerramienta { Error = true, Texto = pide.ToString() };
        }
        if (final)
        {
            if (r.Registro != null) RegistroProyecto.Guardar(_trabajo.Pedido.Fuente, r.Registro);
            var resultado = new { estado = "listo", resumen = r.Resumen, avisos = r.Avisos, dudas = croquis.Dudas };
            File.WriteAllText(_trabajo.Archivo("resultado.json"), JsonSerializer.Serialize(resultado, Json.Indentado));
        }
        return new ResultadoHerramienta { Texto = texto.ToString(), Png = final ? null : vista, Entregado = final };
    }

    private ResultadoHerramienta Superponer(JsonElement e)
    {
        if (!e.TryGetProperty("croquis", out JsonElement c)) throw new ArgumentException("falta 'croquis'");
        Croquis croquis = c.Deserialize<Croquis>(Json.Opciones) ?? throw new ArgumentException("croquis vacío");
        Rect zona = e.TryGetProperty("ancho", out _) ? new Rect((int)Numero(e, "x"), (int)Numero(e, "y"), (int)Numero(e, "ancho"), (int)Numero(e, "alto")) : _trabajo.Recorte;
        byte[] png = Superposicion.Dibujar(_trabajo.Imagen, zona, LadoImagen, croquis, out double escala);
        _superposiciones++;
        return new ResultadoHerramienta
        {
            Png = png,
            Texto = FormattableString.Invariant($"Croquis encima de la hoja (x {zona.X}–{zona.X1}, y {zona.Y}–{zona.Y1}, escala {escala:0.###}). Busque lo que está en la hoja y no tiene color encima (una unión, un tramo, una vecina, un número), y lo que está corrido. Amplíe una parte con x/y/ancho/alto."),
        };
    }

    private ResultadoHerramienta VerPaginas()
    {
        _vioPlano = true;
        var paginas = _trabajo.TodasLasPaginas().ToList();
        if (paginas.Count == 0) throw new InvalidDataException("el PDF no tiene páginas escaneadas");
        // Tantas columnas como hagan falta para que la hoja de miniaturas no pase de ~1.15 veces el ancho.
        int columnas = 4;
        while (columnas < 12 && Math.Ceiling(paginas.Count / (double)columnas) * (LadoImagen / columnas * 1.4 + 18) > 1.15 * LadoImagen) columnas++;
        int celda = LadoImagen / columnas, alto = (int)(celda * 1.4) + 18, filas = (int)Math.Ceiling(paginas.Count / (double)columnas);
        var visual = new System.Windows.Media.DrawingVisual();
        using (System.Windows.Media.DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(System.Windows.Media.Brushes.White, null, new System.Windows.Rect(0, 0, LadoImagen, filas * alto));
            for (int i = 0; i < paginas.Count; i++)
            {
                (int numero, byte[] bytes) = paginas[i];
                double x = i % columnas * celda, y = i / columnas * alto;
                var mini = Imagen.Miniatura(bytes, celda - 8);
                double escala = Math.Min((celda - 8.0) / mini.PixelWidth, (alto - 26.0) / mini.PixelHeight);
                dc.DrawImage(mini, new System.Windows.Rect(x + 4, y + 22, mini.PixelWidth * escala, mini.PixelHeight * escala));
                bool esta = numero == _trabajo.Pedido.Pagina;
                var texto = new System.Windows.Media.FormattedText(esta ? $"p. {numero} (este croquis)" : $"p. {numero}", CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                    new System.Windows.Media.Typeface("Arial"), 13, esta ? System.Windows.Media.Brushes.DarkRed : System.Windows.Media.Brushes.Black, 1.0);
                dc.DrawText(texto, new System.Windows.Point(x + 4, y + 3));
                if (esta) dc.DrawRectangle(null, new System.Windows.Media.Pen(System.Windows.Media.Brushes.Red, 3), new System.Windows.Rect(x + 2, y + 20, celda - 4, alto - 22));
            }
        }
        return new ResultadoHerramienta
        {
            Png = Imagen.Png(visual, LadoImagen, filas * alto),
            Texto = $"{paginas.Count} páginas. Vea una con ver_pagina (con giro si está de lado).",
        };
    }

    private ResultadoHerramienta VerPagina(JsonElement e)
    {
        int numero = (int)Numero(e, "pagina");
        if (numero != _trabajo.Pedido.Pagina) _vioPlano = true;
        Imagen pagina = _trabajo.Pagina(numero);
        Rect zona = e.TryGetProperty("ancho", out _)
            ? pagina.Recortar(new Rect((int)Numero(e, "x"), (int)Numero(e, "y"), (int)Numero(e, "ancho"), (int)Numero(e, "alto")))
            : new Rect(0, 0, pagina.Ancho, pagina.Alto);
        int giro = (int)Numero(e, "giro", 0);
        byte[] png = Imagen.Girar(pagina.Zona(zona, LadoImagen, out double escala), giro);
        return new ResultadoHerramienta
        {
            Png = png,
            Texto = FormattableString.Invariant($"Página {numero} ({pagina.Ancho} × {pagina.Alto} px): x {zona.X}–{zona.X1}, y {zona.Y}–{zona.Y1} a escala {escala:0.###}") +
                    (giro != 0 ? $", girada {giro}° para leerla (la cuadrícula sigue en píxeles de la página sin girar)." : "."),
        };
    }

    private ResultadoHerramienta VerDibujo() => new()
    {
        Png = VistaDibujo.Dibujar(_trabajo.Lectura, _trabajo.Parametros.Marco),
        Texto = _trabajo.Lectura.Describir(),
    };

    private ResultadoHerramienta Detallar(JsonElement e, bool final)
    {
        if (!e.TryGetProperty("detalle", out JsonElement de)) throw new ArgumentException("falta 'detalle'");
        DetalleIa detalle = de.Deserialize<DetalleIa>(Json.Opciones) ?? throw new ArgumentException("detalle vacío");
        LecturaDibujo lectura = _trabajo.Lectura;
        (List<string> errores, List<string> avisos, List<string> resumen) = RevisionDetalle.Revisar(detalle, lectura);

        var texto = new StringBuilder();
        if (errores.Count > 0)
        {
            texto.AppendLine(final ? "No se entregó: corrija esto y vuelva a entregar." : "Errores (corríjalos antes de entregar):");
            foreach (string error in errores) texto.AppendLine("- " + error);
            return new ResultadoHerramienta { Texto = texto.ToString(), Error = true, Png = final ? null : VistaDibujo.Dibujar(lectura, _trabajo.Parametros.Marco, detalle) };
        }

        if (!detalle.Corresponde)
        {
            texto.AppendLine(final ? "Entregado: la hoja no corresponde al dibujo." : "Marcado como que no corresponde:");
            foreach (string motivo in detalle.Motivos) texto.AppendLine("- " + motivo);
            if (final)
            {
                var rechazo = new { estado = "rechazado", motivos = detalle.Motivos, dudas = detalle.Dudas };
                File.WriteAllText(_trabajo.Archivo("resultado.json"), JsonSerializer.Serialize(rechazo, Json.Indentado));
            }
            return new ResultadoHerramienta { Texto = texto.ToString(), Entregado = final };
        }

        texto.AppendLine(final ? "Entregado. AutoKADN lo pondrá así:" : "Revisión:");
        foreach (string linea in resumen) texto.AppendLine("- " + linea);
        if (avisos.Count > 0)
        {
            texto.AppendLine("Avisos:");
            foreach (string aviso in avisos) texto.AppendLine("- " + aviso);
        }
        if (final)
        {
            var salida = new
            {
                diametro = detalle.Diametro.Length > 0 ? detalle.Diametro : lectura.Diametro,
                largos = detalle.Largos,
                ubicaciones = detalle.Ubicaciones,
                accesorios = detalle.Accesorios,
            };
            File.WriteAllText(_trabajo.Archivo("detalle.json"), JsonSerializer.Serialize(salida, Json.Indentado));
            var resultado = new { estado = "listo", resumen, avisos, dudas = detalle.Dudas };
            File.WriteAllText(_trabajo.Archivo("resultado.json"), JsonSerializer.Serialize(resultado, Json.Indentado));
        }
        return new ResultadoHerramienta { Texto = texto.ToString(), Png = final ? null : VistaDibujo.Dibujar(lectura, _trabajo.Parametros.Marco, detalle), Entregado = final };
    }

    // «ANILLO 3 DETALLE» → «3».
    private static string? Anillo(string layout)
    {
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(layout, @"ANILLO\s+(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static double Numero(JsonElement e, string campo, double? porDefecto = null)
    {
        if (e.TryGetProperty(campo, out JsonElement v))
        {
            if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
            if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) return n;
        }
        return porDefecto ?? throw new ArgumentException($"falta '{campo}'");
    }

    private static string Texto(JsonElement e, string campo, string porDefecto) =>
        e.TryGetProperty(campo, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim().ToLowerInvariant() : porDefecto;

    private static string Recortar(string texto, int largo) => texto.Length <= largo ? texto : texto[..largo] + "…";
}
