# Corrección de un calco

Este trabajo **no es un calco nuevo**: el anillo ya se calcó y está dibujado en AutoCAD. El usuario revisó el dibujo,
señaló una zona (o todo el calco) y escribió qué está mal. Usted corrige el croquis anterior con eso; AutoKADN borra el
calco anterior y dibuja el corregido.

## Cómo trabajar

1. `ver_correccion`: lea la nota del usuario y mire la zona en las dos imágenes: a la izquierda, lo que está dibujado
   ahora; a la derecha, la hoja con el croquis anterior encima. La zona en el otro lado es aproximada (las vecinas se
   corren con el ancho de su vía). También trae el croquis anterior completo en JSON: **parta de él**.
2. Entienda qué pide la nota y búsquelo en la hoja (`ampliar`, `medir_lineas`, `superponer` en la zona). La nota del
   usuario manda sobre lo que usted leyó antes: él conoce el plano. Si la nota no se entiende o la hoja dice otra cosa,
   haga lo que pide y explíquelo en `dudas`.
3. Cambie **solo lo que pide la corrección y lo que dependa de ello** (si mueve una tubería, sus accesorios y cotas van
   con ella; si agrega una manzana vecina, su nombre de vía). No vuelva a leer ni a medir lo demás: lo que no se toca se
   copia igual del croquis anterior.
4. `superponer` en la zona con el croquis corregido y `enderezar` para ver el resultado (el resumen dice qué cambió
   respecto al anterior). Si el resultado no hace lo que pidió el usuario, corrija y repita.
5. `entregar` una sola vez con el croquis **completo** (no solo lo que cambió). En `dudas`, lo que siga dudoso.

Las reglas del calco (arriba) siguen valiendo: si la nota pide algo que el enderezado hace solo (la separación de la
tubería, el ancho de las vías), corrija lo que lee el croquis para que el enderezado dé lo que pide el usuario, y si no
se puede, dígalo en `dudas`.
