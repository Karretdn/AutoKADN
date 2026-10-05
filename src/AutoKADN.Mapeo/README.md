# AutoKADN.Mapeo

Mapeo de las celdas de los formatos PDF (viven en `../AutoKADN.Proyectos/Recursos/Formatos`, junto a su PDF base) (FT-O-108, FT-O-111, FT-O-117, FT-T-127…) para que el flujo
pueda rellenarlos automáticamente.

Son dos piezas:

1. **Reconocimiento automático** (`AutoKADN.Mapeo`, consola .NET): lee cada PDF, encuentra las celdas
   vacías, las tablas con filas, las rayas para escribir, las casillas y los espacios dentro de párrafos,
   les propone un nombre a partir de los rótulos impresos y escribe `<código>.mapa.json`.
   Si el PDF no trae texto real (letras convertidas a curvas, como FT-O-117) usa el OCR de Windows.
   Es solo un borrador. Uso: `detectar.bat` (o `dotnet run -c Release -- ..\AutoKADN.Proyectos\Recursos\Formatos`).
2. **App web de mapeo** (`web/index.html`): carga el PDF y su `.mapa.json`, y permite revisar y corregir
   todo a mano. Uso: `abrir.bat` (necesita Python) o abrir `web/index.html` con doble clic y arrastrar
   el PDF y el `.mapa.json` a la ventana.

## Formato del `.mapa.json`

Medidas en **puntos PDF** (1 pt = 1/72 pulg.), origen en la esquina **superior izquierda**, Y hacia abajo.
Para escribir en el PDF: `yPdf = altoPágina - y - alto`.

- `campos[]`: rectángulos sueltos. `id` (nombre único), `etiqueta` (cómo se lee en el formato), `tipo`
  (`texto|numero|fecha|check|multilinea`), `pagina`, `x,y,ancho,alto`, `alineacion`, `tamanoFuente`,
  `prefijo` (texto ya impreso, p. ej. "Ø"), `fuente` (clave del flujo que lo rellena; vacío = sin conectar),
  `lineas[]` (renglones de un campo multilínea), `origen` (`auto|manual`), `confianza`, `revisado`.
- `tablas[]`: filas que se van llenando. `columnas[]` (`id`, `tipo`, `x`, `ancho`, `fuente`…) y `filas[]`
  (`y`, `alto`) con un renglón por registro disponible.
- `paginas[]`, `notas[]` (avisos del detector, p. ej. que el texto se leyó con OCR).

Las claves de `fuente` que hoy existen en el flujo son las de `datos_proyecto.json`
(`orden`, `proyecto`, `localidad`, `interventor`, `fechaInicioObra`…).

## Atajos de la app web

`Enter` marca como revisado y pasa al siguiente pendiente · `R` alterna revisado · `Supr` borra ·
flechas mueven (Shift = más) · `N` campo nuevo · `T` tabla nueva · `Ctrl+Z/Y` deshacer/rehacer ·
`Ctrl+D` duplica · `Ctrl+S` guarda · `Ctrl+rueda` zoom.

## Generar los formatos (app de proyectos)

El botón **Interventoría** de `AutoKADN.Proyectos` recorre FT-O-108, FT-O-117, FT-O-119 y FT-T-127 en ese orden.
Para cada uno abre «Guardar como» (por defecto en la carpeta raíz del proyecto); si se cancela, ese formato se
omite y sigue con el siguiente. Rellena el PDF base con los valores de `resumen_obra.json` según el campo `fuente` de cada celda del mapa:

- `fuente: "orden"` → valor simple (texto, número o fecha ISO; las fechas salen como dd/MM/aaaa).
- Columnas de tabla con `fuente: "lista[].campo"` → `resumen_obra.json` trae un arreglo
  (`"accesorios.union": [{"diametro":"3/4","cantidad":"30"}, …]`) y cada elemento llena una fila. Si hay más
  elementos que filas, los sobrantes no se escriben y el resultado lo avisa.
- Campos sueltos con `fuente: "polivalvulas[0].cantidad"` → el campo del elemento 0 de esa lista.
- Celdas sin `fuente`, o con una clave sin dato, quedan en blanco.

`RESUMENOBRA` (plugin) escribe `resumen_obra.json` con: `anillos.total`, `anillos.realizados[]`, `tuberia.anillos[]`,
`tuberia.troncal[]`, `polivalvulas[]`, `accesorios.<tipo>[]` (diametro, cantidad del proyecto, pruebas, total) y
`pruebas.<tipo>[]`, para union, tee, tapon, reduccion, silleta y codos. Las listas `anillos.fijo[]` y
`tuberia.anillos.fijo[]` traen siempre 1/2" y 3/4" (en ese orden, cantidad vacía si no hay) para las filas con
diámetro impreso.
