# Catálogo de datos de AutoKADN

Lista de los datos que el proyecto procesa hoy, por etapa, y qué puede usar la app de formatos.
Armado leyendo el código (5-oct-2026). Regla: los JSON solo transportan lo que ya se procesa en cada etapa.

Estado de cada dato: **JSON** = ya viaja en un JSON · **Dibujo** = existe en el DWG, aún no se exporta · **Excel** = lo usa el Excel de legalización · **No existe** = no está en ninguna etapa.

---

## 1. Creación del proyecto (app AutoKADN.Proyectos)

### 1.1 Lo que se captura

| Dato | De dónde sale | Clave en `datos_proyecto.json` |
|---|---|---|
| N.º de orden de trabajo | PDF de la orden (respaldo: nombre del archivo `OT {orden}-{id}-PROYECTO {nombre}`) | `orden` |
| ID del proyecto | PDF / nombre del archivo | `idProyecto` |
| Nombre del proyecto | PDF (observaciones) | `proyecto` |
| Localidad | PDF | `localidad` |
| Departamento | PDF | `departamento` |
| Interventor (nombre con código) | carpeta del interventor + `roles.xlsx` (si no está, se escribe a mano) | `interventor` |
| Supervisor (en el plano: "pegador") | `roles.xlsx` | `supervisor` |
| Fecha de inicio de obra | se digita (formas rápidas: `25`, `2509`, `250926`, `hoy`, `ayer`) | `fechaInicioObra` |
| Fecha de final de obra | se digita o copia la anterior | `fechaFinalObra` |
| Fecha de prueba inicial | se digita o copia la anterior | `fechaPruebaInicial` |
| Fecha de prueba final | se digita o copia la anterior | `fechaPruebaFinal` |
| Fecha de gasificado | se digita o copia la anterior | `fechaGasificado` |
| Diámetro del plano | selector 1/2" o 3/4" | `diametro` |
| Marca de tubería | selector EXTRUCOL o PAVCO | `tuberia` |
| Incluir libro de cantidades | casilla | (no va al JSON) |

### 1.2 Derivados que ya se calculan

| Dato | Cómo | Clave |
|---|---|---|
| Municipio | `localidad - departamento` | `municipio` |
| Fecha para el cajetín | `25 - SEPTIEMBRE - 2026` | `planoPruebaInicial`, `planoPruebaFinal`, `planoGasificado` |
| Fecha para Excel y formatos | `dd/MM/aaaa` | (se formatea al escribir) |
| Versión y fecha de creación | — | `version`, `creado` |
| Carpeta del proyecto | `orden - proyecto` (sin caracteres inválidos) | — |

### 1.3 Lo que crea

- `raíz\INTERVENTOR\orden - proyecto\` con **PLANOS**, **SOPORTES** y **FOTOS**.
- Copia de la orden de trabajo (PDF).
- `SOPORTES\XX X PULG.xlsx` con la cabecera: C6 municipio · C8 orden · C9 supervisor · F9 interventor · F11 proyecto · C11 ID · C12 inicio de obra · F12 final de obra.
- `FOTOS\FORMATO FOTOS.xlsx` (registro fotográfico: orientación EXIF, recorte/estirado, hasta 9 fotos por hoja, varias hojas).
- `PLANOS\orden - proyecto.dwg` (plano base).
- `datos_proyecto.json` en la carpeta raíz del proyecto (20 claves: las de arriba).
- Libro de cantidades (opcional).

---

## 2. Dibujo en AutoCAD (plugin)

Catálogos fijos: **diámetros** 1/2, 3/4, 2, 3, 4, 6 · **terrenos** ZONA VERDE, ANDEN TABLETA, CALZADA CONCRETO, DESTAPADO, CUNETA, ANDEN CONCRETO, ASFALTO, ADOQUIN.
Layouts por anillo: `ANILLO n DETALLE`, `ANILLO n UC`, `TRONCAL DETALLE`, `TRONCAL UC`.

### 2.1 Cajetín (RELLENARDATOS)
Escribe en cada layout: MUNICIPIO, OBRA/SECTOR/PROYECTO, INTERVENTOR, PEGADOR (= supervisor), TUBERIA, O.T.#, las tres FECHA (gasificado, prueba inicial, prueba final) y el diámetro de los títulos (3/4" o 1/2"). Lee `datos_proyecto.json`.

### 2.2 Cotas y textos
| Herramienta | Dato que genera |
|---|---|
| COTAK · Longitud | cota con ML manual, por diámetro (capas `COTA_1-2`, `COTA_3-4`, `COTA_2`, `COTA_3`, `COTA_4`, `COTA_6`) |
| COTAK · UC | cota con ML por diámetro (capas `UC_…`) **y terreno** (XData `UC_SURFACE`) |
| COTAK · Ubicación | distancia entre dos líneas paralelas (capa COTAS MAGENTA) |
| LIMIK | límites `LB`, `LP`, `LC` y `0.0` (LB0.0, LP0.0, LC0.0) |
| NOMENK · Vial | tipo (`KR`/`CL`) + número + pavimento (`T.N.`, `PAV`, `ASF`, `ADO`) → `KR 12 - ASF` |
| NOMENK · Predial | texto libre |
| PEGAS | N tramos, longitud de tubo y número de pega por división (solo visual, sin XData) |
| TAMANOS | 6 valores: altura de texto de Límites, Anotaciones, Vial y Predial; escala de Cotas y de Bloques (`%APPDATA%\AutoKADN\tamanos.txt`). Al aplicarlos solo se cambia el layout que está abierto |

### 2.3 Anotaciones (ANOTACIONES, con XData)
| Anotación | Datos |
|---|---|
| ESPIRAL | ML de tubería 3/4", uniones, tees, válvulas, silletas, diámetro de silleta (2x3/4, 3x3/4, 4x3/4, 6x3/4), PE.EXT (automático con silleta), terreno |
| CAMISA · PANTALLA · CRUCE CON TOPO · EMPEDRADO · VIGA EN CONCRETO | una o varias combinaciones diámetro × terreno × ML |
| CRUCE DE ARROYO | igual que las anteriores (el terreno es del que se resta). Se anota en el layout DETALLE y también en el UC. No es un terreno de la lista: los cálculos lo tratan como un terreno aparte «CRUCE DE ARROYO» y los metros salen de la UC donde se anotaron |
| LIBRE | texto |

Las válvulas y silletas del espiral se cuentan solo desde los bloques físicos; del XData del espiral se suman tubería, uniones y tees.

### 2.4 Material
- **Bloques** en la capa `Mat` con propiedades dinámicas `DIAMETRO` y `UC` (terreno); el nombre del bloque es el material.
- Catálogo de **36 materiales**: UNION, TUBERIA, TEE y TAPON en los 6 diámetros · SILLETA 2x3/4, 3x3/4, 4x3/4, 6x3/4 · VALVULA 3/4, 2, 3, 4, 6 · REDUCCION 3/4x1/2, 4x2, 6x4.
- **MATERIALPRUEBA** (XData `MATERIAL_PRUEBA`): UNION, TAPON y REDUCCION (3/4x1/2) con cantidad y la UC de origen (diámetro + terreno).

---

## 3. Resúmenes que ya quedan escritos en cada layout

| Comando | Qué resume |
|---|---|
| LISTABLOQUES | por layout: material, diámetro, unidad y cantidad (bloques, tubería de las cotas `COTA_…` y tubería/uniones/tees del espiral) |
| RESUMENUC | por layout: ML por diámetro × terreno, más los metros de espiral que se sumen a una UC; los CRUCE DE ARROYO anotados en ese layout UC se descuentan de su UC y salen en una línea aparte («… En Cruce De Arroyo») |
| CLONARUC · NUEVOANILLO · RECORTEANILLOS | solo estructura (copian layouts y contenido), no generan datos nuevos. RECORTEANILLOS crea ANILLO N DETALLE y UC y copia al DETALLE, ampliado al marco, el dibujo del plano general que cae en un rectángulo; los indicadores de anillo (círculo con número y diámetro) de los anillos que se alcanzan a ver se corren dentro del marco y los textos que estorban se corren o se omiten |
| RESUMENPROYECTO | un Excel junto al DWG con el resumen del proyecto por anillo y consolidado (ver 5.4); no escribe nada en el dibujo |

---

## 4. Excel de legalización (GENERAREXCEL, una hoja por UC = diámetro × terreno)

- Actividad elegida de la lista ACTIVIDAD y cantidades de materiales por código (36 códigos).
- Cantidades de actividades por código: canalización (anillo y troncal P80, por terreno), planos as-built, tendido y termofusión (2", 3", 4", 6"), cruce con topo (por diámetro), pantalla, viga en concreto y empedrado (ML × 0,4 = m²).
- Observaciones: camisa, cruce de arroyo, espiral de válvula, empedrado y material de prueba.
- **CRUCE DE ARROYO** (anotado en los layouts DETALLE): sus metros se restan de la UC donde se anotaron (tubería, canalización, tendido y as-built) y salen en un Excel aparte, el de la UC especial **ESPECIAL CRUCE SUBFLUVIAL POLIETILENO** (un solo formato para todos los diámetros): actividad 100005407 (anillo: 1/2" y 3/4") y 100005408 (troncal: 2", 3", 4" y 6"), planos as-built 100005412 y la TUBERIA de cada diámetro, todo con los mismos metros del cruce.

---

## 5. Informes de interventoría (nuevo)

### 5.1 `resumen_obra.json` (lo escribe RESUMENOBRA en la carpeta raíz del proyecto)
| Clave | Contenido |
|---|---|
| `anillos.total` | layouts `ANILLO n DETALLE` |
| `contratista` | empresa contratista: el texto que está justo arriba de «PLANO DE DETALLES» en el cajetín del primer plano de detalles (se lee en el orden de las pestañas), solo hasta el primer punto y sin LTDA (ej. «NOMBRE & CIA.»). Si no se encuentra, la clave no se escribe y los formatos la reportan como dato que falta |
| `anillos.realizados[]` | anillos por diámetro (1/2, 3/4): `diametro`, `cantidad` |
| `anillos.fijo[]` | siempre 1/2" y 3/4" (cantidad vacía si no hay) |
| `tuberia.anillos[]`, `tuberia.anillos.fijo[]`, `tuberia.anillos.total` | ML de tubería de anillos por diámetro |
| `tuberia.troncal[]`, `tuberia.troncal.total` | ML de tubería troncal (2, 3, 4, 6) |
| `polivalvulas[]`, `polivalvulas.total` | válvulas por diámetro |
| `accesorios.<tipo>[]` | `diametro`, `cantidad` (proyecto), `pruebas`, `total`; tipos: `union`, `tee`, `tapon`, `reduccion`, `silleta`, `codos` |
| `pruebas.<tipo>[]` | `diametro`, `cantidad` de prueba, **en la misma fila** que `accesorios.<tipo>` |
| `ft108.paginas[]` | una hoja del FT-O-108 (control de excavación): `terreno1..4`, `d1..d16`, `material1..16` (CALICHE), `registros[]` (`plano`, `c1..c16`), `t1..t16`; la primera hoja trae además `ucs[]` (`terreno`, `diametro`, `ml`, `m3`), `total_ml`, `total_m3` y `observaciones`. ML de excavación = cotas UC − camisa − cruce con topo (sin espiral ni pruebas); el cruce de arroyo sale de su UC y aparece como un terreno más, «CRUCE DE ARROYO», con sus propios diámetros; M³ = ML × 0,28 (1/2" y 3/4") o × 0,3 (2", 3", 4", 6"), truncado a 2 decimales |
| `ft127.paginas[]` | una hoja del FT-T-127: `terreno1..4`, `d1..d16`, `registros[]` (`fecha`, `plano`, `c1..c16`), `t1..t16`; la primera hoja trae además `consolidado_1_2 … consolidado_6`, `consolidado_total` y `observaciones`. El cruce de arroyo sale de su UC y aparece como un terreno más, «CRUCE DE ARROYO» (el consolidado por diámetro no cambia) |

### 5.2 Qué sabe hacer la app con un dato (campo `fuente` del mapa)
- Clave simple (`orden`) → un valor. Con formato: `fechaGasificado|dia`, `|mes|titulo`, `|anio2`, `interventor|sincodigo`, `|codigo`, `|marca` (X si el número es mayor que cero), `|mayus`, `|minus`. Texto fijo o con claves: `=TEXTO FIJO`, `=ID-{idProyecto}`. Tipos de celda: `texto`, `numero`, `fecha` (dd/MM/aaaa), `check` (true/1/si/x), `multilinea` (varios renglones, respeta saltos de línea).
- `lista[].campo` → una fila por elemento de la lista; `lista[0].campo` → el elemento 0.
- `paginaPorLista` → repite la hoja del formato una vez por elemento de una lista.
- Texto girado (`rotacion`: 90 se lee de abajo hacia arriba).
- Texto previo (ej. "Ø") con ancho propio; ajuste automático del tamaño de letra al ancho de la celda.

### 5.3 Formatos y sus datos
| Formato | Estado |
|---|---|
| FT-O-117 Informe final | conectado: encabezado (`interventor`, `orden`, `localidad`, `proyecto`, fechas), contratista (`contratista`, del plano) + `resumen_obra.json` |
| FT-T-127 Totales de tubería | conectado a `ft127.paginas`; firmas y SUBTOTAL vacíos |
| FT-O-108 Control de excavación | conectado a `ft108.paginas`; la fecha queda vacía (como en los formatos ya diligenciados), firmas y SUBTOTAL vacíos |
| FT-O-119 Acta de puesta en servicio | conectado: fecha de gasificado (día, mes y año), interventor, supervisor como representante del contratista y la empresa contratista (clave `contratista`, del plano de detalles), proyecto como dirección, localidad, departamento, orden y la observación estándar con el ID del proyecto; una X en anillos y otra en troncal cuando el proyecto los tiene (`anillos.total` y `tuberia.troncal.total` mayores que cero); en la línea de firma del Consorcio, `NOMBRE - código` |

### 5.4 Resumen del proyecto (RESUMENPROYECTO → `RESUMEN PROYECTO - <dwg>.xlsx`, junto al DWG)
Libro de lectura para comparar el plano con interventoría. Solo lee lo que el plano ya arroja (mismos escaneos y secuencia que GENERAREXCEL; cada anillo se calcula aparte con sus layouts DETALLE y UC, y el consolidado con todo el dibujo junto); no cambia el dibujo ni otros resultados.
| Hoja | Contenido |
|---|---|
| RESUMEN | datos del cajetín y contratista, cifras principales, tubería por terreno y diámetro, principales errores y avisos, índice |
| COMPARAR | por concepto (tubería por diámetro y por terreno, excavación y zanja, restas, accesorios, tubería por anillo): valor del plano, celda para el valor de interventoría, diferencia y estado (Coincide / Diferencia / Pendiente) con tolerancia editable |
| ANILLOS | una fila por anillo: cotas UC, cruce de arroyo, camisa, cruce con topo, espiral, pruebas, tubería por diámetro, excavación (ML y m³), material y alertas; total, consolidado de GENERAREXCEL y diferencia |
| UC POR ANILLO · UC CONSOLIDADO | cada diámetro × terreno por anillo y de todo el proyecto, con las restas y adiciones; el consolidado suma los anillos (fórmulas) y los compara con GENERAREXCEL |
| ACTIVIDADES · ACTIVIDADES POR UC | cantidad por código de actividad del Excel de legalización, por anillo y por UC |
| MATERIALES | material del proyecto y de pruebas del consolidado y su reparto por anillo |
| VERIFICACIONES | 15 revisiones: nombres de layout, pareja DETALLE/UC, numeración, cotas sin terreno o medida, bloques fuera del catálogo, restas mayores que las cotas, anotaciones sin cotas, tubería = cotas netas + espiral + pruebas, suma de anillos = consolidado, cruce de arroyo igual en DETALLE y UC, cajetín uniforme y completo, diámetro del título = diámetro de las cotas, totales = RESUMENOBRA, contratista |
| LEEME | cómo leer el libro y de dónde sale cada número |
- Excavación = cotas netas − camisa − cruce con topo (sin negativos), como el FT-O-108; zanja = ML × 0,28 (1/2" y 3/4") o × 0,30 (troncal); en el consolidado se trunca a 2 decimales por UC.
- Escalable: las hojas y las verificaciones son listas registradas, así que se pueden agregar (por ejemplo rendimientos por día) sin tocar las demás. Aún no incluye rendimientos ni días en obra.

---

## 6. Datos que hoy no existen en ninguna etapa
- Cédulas de los firmantes.
- Fecha de cada excavación por anillo/troncal (el FT-O-108 la deja vacía).
- Fecha de recibo de obra propia (hoy se toma la fecha final de obra).
- Dirección (hoy se usa el nombre del proyecto).

## 7. Existe en el dibujo pero todavía no se exporta
- Pantalla, empedrado y viga en concreto por anillo, terreno y diámetro.
- Detalle del espiral: uniones, tees, válvulas, silletas, diámetro de silleta y PE.EXT por anillo.
- Material por layout (resultado de LISTABLOQUES) y ML por UC por layout (resultado de RESUMEN UC).
- Cotas de longitud (`COTA_…`), ubicaciones, límites, nomenclaturas vial y predial, pegas.
- Códigos de material y de actividad del Excel.
- Tamaños configurados.

## 8. Códigos de material del Excel (36)
| Material | Códigos |
|---|---|
| UNION | 1/2: 100003150 · 3/4: 100003142 · 2: 100005477 · 3: 100005476 · 4: 100005474 · 6: 100003146 |
| TUBERIA | 1/2: 100003135 · 3/4: 100003130 · 2: 100003129 · 3: 100003136 · 4: 100003131 · 6: 100003132 |
| TEE | 1/2: 100003119 · 3/4: 100003118 · 2: 100003114 · 3: 100003120 · 4: 100003115 · 6: 100003116 |
| TAPON | 1/2: 100003108 · 3/4: 100003102 · 2: 100003101 · 3: 100003105 · 4: 100003106 · 6: 100003103 |
| SILLETA | 2x3/4: 100003085 · 3x3/4: 100003086 · 4x3/4: 100003087 · 6x3/4: 100003088 |
| VALVULA | 3/4: 100003160 · 2: 100003152 · 3: 100003157 · 4: 100003158 · 6: 100003151 |
| REDUCCION | 3/4x1/2: 100003075 · 4x2: 100003068 · 6x4: 100003069 |

## 9. Códigos de actividad del Excel
| Actividad | Códigos |
|---|---|
| Canalización anillo (1/2 y 3/4) | ZONA VERDE 100005377 · ANDEN CONCRETO 100005387 · CALZADA CONCRETO 100005379 · ANDEN TABLETA 100005385 · ADOQUIN 100006913 · ASFALTO 100005386 · CUNETA 100006912 · DESTAPADO 100006911 |
| Canalización troncal P80 (2, 3, 4, 6) | ZONA VERDE 100005388 · ANDEN CONCRETO 100005392 · CALZADA CONCRETO 100005389 · ANDEN TABLETA 100005390 · ADOQUIN 100006916 · ASFALTO 100005391 · CUNETA 100006915 · DESTAPADO 100006914 |
| Planos as-built | 100005412 |
| Tendido y termofusión | 2: 100005398 · 3: 100005399 · 4: 100005400 · 6: 100005401 |
| Cruce con topo | 1/2, 3/4 y 2: 100005403 · 3: 100005404 · 4: 100005405 · 6: 100005406 |
| Cruce de arroyo a cielo abierto (UC especial ESPECIAL CRUCE SUBFLUVIAL POLIETILENO) | anillo, 1/2 y 3/4: 100005407 · troncal, 2, 3, 4 y 6: 100005408 (más planos as-built 100005412 y la TUBERIA del diámetro) |
| Pantalla · Viga en concreto · Empedrado | 100006014 · 100006013 · 100006010 |
