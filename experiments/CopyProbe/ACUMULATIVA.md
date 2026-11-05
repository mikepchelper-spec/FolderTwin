# Copia acumulativa de producto

El trabajo de copia acumulativa usa una carpeta dedicada NTFS con identidad y marcador. Su carpeta navegable está en `mirror/current`, dentro del almacén de producto `FTVERS03`. La interfaz permite respaldar, verificar y recuperar el estado acumulado en una carpeta nueva. Los ensayos usan únicamente datos sintéticos y carpetas desechables.

## Comportamiento

El análisis completo del origen y de la carpeta actual decide qué se añade y actualiza. Los archivos y carpetas presentes solo en el destino se conservan. Un origen vacío conserva el contenido acumulado. Un origen ausente, sustituido o con inventario incompleto detiene la operación.

Antes de cambiar una ruta presente en el origen, compara el destino observado con el último contenido registrado. Una edición externa, una retirada externa de esa ruta o una colisión con contenido diferente queda como conflicto. No se sobrescribe ni se descarta para facilitar un reintento. Los cambios de tipo o las rutas incompatibles requieren revisión antes de publicar. Los exclusivos del destino se incluyen con sus bytes y metadatos observados en el nuevo catálogo. Si cambian entre revisión y publicación, el intento se detiene.

## Publicación recuperable

Se reutilizan objetos SHA-256, catálogos duplicados, recibos de copia, bloqueo del almacén, preparación, retiradas y publicación del motor de espejo. El diario acumulativo usa la versión 3. Añade dos hashes al plan existente, el del inventario real del origen y el del árbol observado en destino. Los planes de espejo v1 y v2 omiten esos campos y mantienen su codificación anterior.

Cada operación guarda `source.jsonl` y `source.location.json` para el origen, y `baseline.jsonl` para el destino revisado. El plan pendiente y los catálogos de la generación describen la unión final. Esto permite comprobar el origen real sin exigir que contenga los exclusivos conservados en destino. Los objetos de estos exclusivos se verifican antes de confirmar el punto. La retención protege el catálogo acumulado vigente y sus referencias compartidas.

Después de comprobar de nuevo origen y destino, publica `ready`. A partir de ahí puede completar los movimientos con el origen ausente. Un árbol parcial sigue pendiente hasta verificar el resultado y registrar su generación. Los archivos reemplazados se conservan de forma recuperable antes de publicar los nuevos. Los temporales sin recibo válido y los documentos parciales o contradictorios se preservan para revisión. Una interrupción no autoriza a descartarlos.

La importación de objetos del origen o de exclusivos del destino puede continuar un temporal sin recibo cuando se comprueban el SHA-256 del origen completo y todos los bytes del prefijo, sin truncarlo ni reemplazarlo. Conserva los conflictos y borradores de recibo para revisión. El preparado del espejo mantiene su propia conservación de temporales sin verificar. [Protocolo de continuación](VERSIONES.md).

El modo acumulativo presenta el estado actual en recuperación. Las generaciones internas sostienen la publicación, la recuperación y la retención manual. El historial por fechas es una función del modo Versionado. La verificación del estado actual revisa tanto los objetos recuperables como la carpeta navegable. Una edición de esta última impide informar una verificación completa, aunque el contenido guardado en los objetos siga intacto.

## Evidencia

`--accumulative` comprueba altas, actualizaciones, bajas del origen conservadas en destino, exclusivos, restauración sin origen, referencias de retención, conflictos externos, origen ocupado, inventario incompleto por junction, revisión obsoleta, origen ausente y sustituido. Interrumpe cada transición aplicable del diario y reanuda sin origen desde `ready`. También termina un trabajador real durante la retirada, recupera puntos desde otra instancia y completa la publicación pendiente.

`--product-smoke` ejecuta los tres modos desde controles WPF. Para el acumulativo prueba actualización, conservación de exclusivos y de bajas del origen, verificación del árbol actual, conflicto por edición externa y recuperación en carpeta nueva. El laboratorio VHDX amplía el ensayo a dos volúmenes NTFS; su resultado debe registrarse por separado. Estos ensayos no acreditan durabilidad ante una pérdida de alimentación ni cierran las validaciones externas de [V1.md](../../V1.md).
