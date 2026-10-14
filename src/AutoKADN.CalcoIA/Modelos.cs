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
    public List<TextoCroquis> Textos { get; set; } = [];             // números de predio ("LOTE" si no tiene), «LOTES» en vecinas
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
    public string Tipo { get; set; } = "nueva";       // nueva | existente | cruce
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
}

public sealed class LineaPlano
{
    public string Tipo { get; set; } = "manzana";   // manzana, predio, construccion, casa, cerca, vecino, tuberia_nueva, tuberia_existente
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
