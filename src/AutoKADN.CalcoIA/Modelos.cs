namespace AutoKADN.CalcoIA;

// ───────────── Croquis: lo que la IA lee de la hoja, en píxeles de la hoja completa (origen arriba a la izquierda) ─────────────
// Se describe lo que se ve, más o menos derecho; el Enderezador lo pone a 90°, separa la tubería, dobla las vías y lo
// ajusta al marco con los parámetros. Formato documentado en kit/INSTRUCCIONES.md y en el esquema de la herramienta.
public sealed class Croquis
{
    public double[][] Manzana { get; set; } = [];                    // contorno de la manzana principal, en orden
    public double[][][] Predios { get; set; } = [];                  // líneas rectas que separan predios: [[x,y],[x,y]]
    public double[][][] Construcciones { get; set; } = [];           // polígonos cerrados dentro de predios
    public List<CasaCroquis> Casas { get; set; } = [];               // casas sueltas (rectángulo, con su techo si lo tiene)
    public double[][][] Cercas { get; set; } = [];                   // cercas «x—x—x», como polilíneas abiertas
    public double[][][] Bordillos { get; set; } = [];                // línea de bordillo (LB) que cierra el andén, como polilíneas
    public List<NotaCroquis> Notas { get; set; } = [];               // notas escritas con flecha (el material de un empalme)
    public List<TextoCroquis> Textos { get; set; } = [];             // números de predio como están escritos (sin texto si no tiene), «LOTES» en vecinas
    public List<VecinoCroquis> Vecinos { get; set; } = [];           // lo que se ve de manzanas vecinas
    public List<IndicadorCroquis> Indicadores { get; set; } = [];    // círculos "n | ∅" (el propio y los de vecinos)
    public List<ViaCroquis> Vias { get; set; } = [];                 // nombres de las vías, donde están escritos
    public List<TuberiaCroquis> Tuberias { get; set; } = [];
    public List<AccesorioCroquis> Accesorios { get; set; } = [];
    public string[]? Abierta { get; set; }                          // lados de la manzana que el croquis corta en el borde de la hoja
    public string? Superficie { get; set; }                         // UC del cuadro (ZONA VERDE…)
    public Dictionary<string, string>? Cuadro { get; set; }         // total de tubería por diámetro según el cuadro de UC
    public Dictionary<string, int>? CuadroAccesorios { get; set; }  // cantidad de cada accesorio según el cuadro (UNION: 1, TEE: 2…)
    public List<string> Dudas { get; set; } = [];
}

// Una nota del croquis con su flecha: el texto tal como está escrito (una línea por renglón) y a qué apunta.
public sealed class NotaCroquis
{
    public string Texto { get; set; } = string.Empty;
    public double X { get; set; }            // dónde está escrita (su esquina de arriba a la izquierda)
    public double Y { get; set; }
    public double[]? Flecha { get; set; }    // [x, y]: a qué apunta
}

public sealed class TextoCroquis
{
    public string Texto { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Giro { get; set; }   // grados: 0 horizontal, 90 vertical
}

// Una casa dibujada en el croquis: el rectángulo, con el techo (líneas de las esquinas a la cumbrera) si el croquis lo
// dibuja, su rótulo («S.N.» = sin número, o el número) y si está en construcción.
public sealed class CasaCroquis
{
    public double[] Caja { get; set; } = [];       // [x0, y0, x1, y1]
    public string? Rotulo { get; set; }
    public bool Techo { get; set; } = true;
    public bool EnConstruccion { get; set; }
}

public sealed class VecinoCroquis
{
    public double[][] Borde { get; set; } = [];   // polilínea abierta con el borde que se ve
}

public sealed class IndicadorCroquis
{
    public string Numero { get; set; } = string.Empty;
    public string Diametro { get; set; } = "3/4";
    public string? Viviendas { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
}

public sealed class ViaCroquis
{
    public string Nombre { get; set; } = string.Empty;  // formato NOMENK: "KR 34D - T.N."
    public double X { get; set; }
    public double Y { get; set; }
    public double? Giro { get; set; }   // cómo está escrito: 0 horizontal (la calle va de lado a lado), 90 vertical
}

public sealed class TuberiaCroquis
{
    public string Tipo { get; set; } = "nueva";       // nueva | existente (anillo existente, azul punteada) | matriz (red matriz existente, roja continua) | cruce
    public string Diametro { get; set; } = "3/4";      // nueva / existente
    public double[][] Puntos { get; set; } = [];
    public bool Cerrada { get; set; }
    public List<string?> Cotas { get; set; } = [];     // una por tramo (punto i → i+1); en el cruce, su largo
    public string[]? Diametros { get; set; }          // cruce: diámetro de la red en cada extremo (inicio, fin)
}

public sealed class AccesorioCroquis
{
    public string Nombre { get; set; } = string.Empty; // TAPON, TEE, UNION, VALVULA, SILLETA, REDUCCION
    public double X { get; set; }
    public double Y { get; set; }
    public string? Diametro { get; set; }
    public UbicacionAccesorio? Ubicacion { get; set; }  // su cota de ubicación a lo largo del tramo, si el croquis la trae
}

// «1.4» hacia abajo = a 1.4 m de la cara (torre, predio) que tiene abajo; «0.0» = alineado con la cara más cercana.
public sealed class UbicacionAccesorio
{
    public string Texto { get; set; } = string.Empty;
    public string? Hacia { get; set; }   // arriba, abajo, izquierda, derecha (en la hoja)
}

// ───────────── Plano: lo que dibuja el plugin (mismo formato que CalcoPlanoJson), en unidades del dibujo ─────────────
public sealed class Plano
{
    public double[] CentroManzana { get; set; } = [0, 0];
    public List<LineaPlano> Lineas { get; set; } = [];
    public List<TextoPlano> Textos { get; set; } = [];
    public List<CotaPlano> Cotas { get; set; } = [];
    public List<AccesorioPlano> Accesorios { get; set; } = [];
    public List<IndicadorPlano> Indicadores { get; set; } = [];
    public List<NotaPlano> Notas { get; set; } = [];
}

// Nota con flecha: texto de varios renglones (arriba a la izquierda en X, Y) y la punta de la flecha.
public sealed class NotaPlano
{
    public string Texto { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double[] Flecha { get; set; } = [0, 0];
}

public sealed class LineaPlano
{
    public string Tipo { get; set; } = "manzana";   // manzana, predio, construccion, casa, cerca, vecino, bordillo, tuberia_matriz, tuberia_nueva, tuberia_existente
    public List<double[]> Puntos { get; set; } = [];
    public bool Cerrada { get; set; }
}

public sealed class TextoPlano
{
    public string Texto { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Giro { get; set; }
    public string Tipo { get; set; } = "predial";   // predial, vial, libre
    public double? Escala { get; set; }              // alto relativo al de su tipo (los rótulos chicos de una casa); null = 1
    [System.Text.Json.Serialization.JsonIgnore] public bool Fijo { get; set; }   // ya ubicado (no se centra en el predio)
}

public sealed class CotaPlano
{
    public double[] A { get; set; } = [0, 0];
    public double[] B { get; set; } = [0, 0];
    public string Texto { get; set; } = string.Empty;
    public string Diametro { get; set; } = "3/4";
    public double[]? Lado { get; set; }
}

public sealed class AccesorioPlano
{
    public string Nombre { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Direccion { get; set; }   // grados (ver CalcoAccesorio en el plugin)
    public string Diametro { get; set; } = "3/4";
    public string Superficie { get; set; } = "ZONA VERDE";
    [System.Text.Json.Serialization.JsonIgnore] public bool Fija { get; set; }   // dirección ya decidida (reducciones del cruce)
}

public sealed class IndicadorPlano
{
    public string Numero { get; set; } = string.Empty;
    public string Diametro { get; set; } = "3/4";
    public string? Viviendas { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
}

// ───────────── Lectura por lados: la manzana y, de cada lado, lo que hay de la cara hacia afuera ─────────────
// Lo que se dibuja es siempre lo mismo (Isacc): la manzana (casi siempre un rectángulo; un quiebre solo si se ve un volado
// o una saliente) con sus predios pegados a la línea; de cada lado, en orden desde la cara, las tuberías (anillo azul o
// troncal roja; continua nueva, punteada existente), la línea de bordillo si hay andén, la vía con su nombre y lo que hay
// enfrente. Todo a la misma separación: la posición sale de los parámetros, no de dónde está dibujada en la hoja.
public sealed class CroquisLados
{
    public double[][] Manzana { get; set; } = [];                    // contorno en píxeles de la hoja
    public double[][][] Predios { get; set; } = [];                  // divisiones de predio (de línea a línea)
    public List<TextoCroquis> Textos { get; set; } = [];             // nombres de predio, donde están escritos
    public List<IndicadorCroquis> Indicadores { get; set; } = [];
    public Dictionary<string, LadoCroquis> Lados { get; set; } = [];  // arriba, abajo, izquierda, derecha
    public List<AccesorioLado> Accesorios { get; set; } = [];
    public List<NotaLado> Notas { get; set; } = [];
    public string? Superficie { get; set; }
    public Dictionary<string, string>? Cuadro { get; set; }
    public Dictionary<string, int>? CuadroAccesorios { get; set; }
    public List<string> Dudas { get; set; } = [];
}

public sealed class LadoCroquis
{
    public List<CarrilCroquis> Carriles { get; set; } = [];   // desde la cara de la manzana hacia afuera
    public bool Bordillo { get; set; }                        // hay andén: la línea de bordillo (LB) va después de las tuberías
    public string? Via { get; set; }                          // nombre de la vía (formato NOMENK)
    public string Enfrente { get; set; } = "nada";            // manzana | pegada (otro lote, sin calle) | nada
    public string? RotuloEnfrente { get; set; }               // lo que dice enfrente («FINCA (OTRO LOTE)», «LOTES»)
    public bool Abierta { get; set; }                         // la hoja corta la manzana de este lado: sigue hasta el borde del plano
}

// Una tubería a lo largo de un lado. Desde/hasta: dónde empieza y termina a lo largo del lado, en píxeles de la hoja (x en
// los lados de arriba y abajo, y en los de los lados); "borde" = sigue hasta el borde del plano; sin dar = todo el lado.
public sealed class CarrilCroquis
{
    public string Red { get; set; } = "anillo";         // anillo (azul) | troncal (roja)
    public string Estado { get; set; } = "nueva";       // nueva (continua) | existente (punteada)
    public string? Diametro { get; set; }
    public System.Text.Json.JsonElement? Desde { get; set; }
    public System.Text.Json.JsonElement? Hasta { get; set; }
    public string? Cota { get; set; }                   // cota verde (longitud), si el carril es un solo tramo
    public List<TramoLado> Tramos { get; set; } = [];   // si tiene varios: hasta dónde va cada uno y su cota, en orden
}

public sealed class TramoLado
{
    public double Hasta { get; set; }
    public string? Cota { get; set; }
    public string? Estado { get; set; }   // nueva | existente (si cambia a lo largo del carril; sin dar = el del carril)
}

public sealed class AccesorioLado
{
    public string Nombre { get; set; } = string.Empty;  // TAPON, TEE, UNION, VALVULA, SILLETA, REDUCCION
    public string Lado { get; set; } = string.Empty;
    public int Carril { get; set; } = 1;                // 1 = el más cercano a la manzana
    public double En { get; set; }                      // a lo largo del lado (px de la hoja)
    public string? Diametro { get; set; }
}

// Nota con flecha (el material de un empalme): apunta al carril de ese lado en esa posición.
public sealed class NotaLado
{
    public string Texto { get; set; } = string.Empty;
    public string Lado { get; set; } = string.Empty;
    public int Carril { get; set; } = 1;
    public double En { get; set; }
}
