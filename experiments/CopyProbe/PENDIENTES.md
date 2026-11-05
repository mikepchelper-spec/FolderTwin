# Conservación de planes incompletos

El motor incorpora una transición explícita para abandonar una generación incompleta de un trabajo de **versionado de producto** y permitir un análisis nuevo. Es una capacidad del motor en prueba, todavía sin acción pública en WPF. La integración del ejecutor, los borradores incompletos y las actualizaciones pendientes de acumulativo y espejo siguen pendientes. Los ensayos usan datos sintéticos.

## Invariantes

Un plan abandonado no se presenta como copia completa. No se publica un recibo de generación ni se inventan archivos ausentes. Se conservan el inventario y descriptor originales en `pending`, los objetos ya copiados y los catálogos o recibos temporales de publicación que existan. Una generación con recibo publicado no se puede abandonar: se verifica o reanuda como punto existente.

La primera implementación exige inventario y descriptor completos y coherentes, trabajo de versionado de producto, registro compatible y ausencia de espejo y limpieza pendientes. No usa el contenido actual del origen para completar el plan antiguo; puede terminar con el origen desconectado. Los archivos de publicación se trasladan sin reemplazo dentro del mismo volumen. Antes y después de un traslado se comprueban identificador del archivo, longitud y SHA-256.

## Diario y registro

La revisión conserva generación, hash del documento, estado esperado del registro después de publicar su expectativa, identidad de origen, número de archivos e identidades y hashes de los artefactos de publicación. Queda ligada al marcador e identidad del almacén en `abandoned/<generación>/plan`. La revisión se vuelve a calcular antes de guardar el diario para rechazar un estado obsoleto.

El diario se publica antes de reparar la expectativa de la generación, archivar metadatos o modificar el registro replicado. Solo permite reparar un temporal que sea prefijo exacto de los bytes esperados. Los pasos se reanudan comparando las ubicaciones e identidades conservadas, sin reemplazar archivos. La confirmación final queda ligada al hash del plan. Un abandono incompleto bloquea copia, actualización de espejo y limpieza hasta reanudarse.

El registro existente admite ahora el evento `abandon` con versión de registro 2. Conserva el hash original en un conjunto separado y retira la generación de las expectativas de puntos recuperables. No admite reutilizar su identificador. Los eventos anteriores de versión 1 mantienen su formato y hashes. Un lector antiguo rechaza el evento nuevo y no queda autorizado a limpiar el almacén. No se debe usar una versión anterior para escribir después de adoptar el protocolo nuevo.

El cálculo previo de una expectativa replicada es solo una revisión del evento exacto que podría repararse. No autoriza copiar ni borrar usando un prefijo incompleto. Para finalizar la transición se exige el diario durable, el inventario original y la coincidencia del evento. Un registro que contradiga un abandono confirmado suspende nuevas operaciones.

## Retención

La limpieza sigue exigiendo coincidencia exacta entre las generaciones recuperables esperadas, manifiestos y recibos. Los planes abandonados se comprueban por separado contra su diario completo, inventario, descriptor y artefactos archivados. Si falta alguno o cambia su contenido, se suspende la limpieza.

Los objetos existentes que solo pertenecen a planes abandonados pueden entrar en una revisión de retención junto con los objetos de fechas expiradas. Los compartidos por fechas conservadas permanecen protegidos. La revisión no elimina objetos; la cuarentena, deshacer y purga posterior conservan su diario y plazo de gracia. Los metadatos del plan abandonado permanecen incluso después de purgar los objetos no referenciados.

## Pruebas y alcance pendiente

`--version-abandon` interrumpe cada paso del diario y las cuatro escrituras del evento replicado, conserva los límites de publicación de ambos catálogos y del recibo temporal y rechaza el recibo final. Termina un trabajador real después de mover un catálogo y reanuda sin origen. Prueba una copia parcial cuyo origen cambia, el nuevo respaldo, referencias compartidas, cuarentena, deshacer, purga con reloj sintético y rechazo del diario dañado.

Faltan la acción y revisión en WPF, su ejecución persistente, el tratamiento de intenciones que todavía no tengan descriptor completo y el abandono seguro de acumulativo y espejo. Una actualización que haya empezado a modificar la carpeta actual requiere completar su diario verificado; no se descartará para evitar reanudarla. Estas comprobaciones de software no sustituyen cortes de energía, USB físico ni las observaciones de entorno de la matriz de v1.
