# Conservación de planes incompletos

La vista previa WPF permite revisar y conservar una generación incompleta de un trabajo de **versionado de producto** y permitir un análisis nuevo en el mismo almacén. Revisión y conservación se ejecutan en procesos separados, con controles compartidos e historial durable. Para borradores incompletos y diarios de acumulativo o espejo, la v1 conserva el trabajo retirado y crea otro destino dedicado: no abandona ni modifica su diario. Los ensayos usan datos sintéticos.

En el trabajo de versionado, abre **Resolver un plan de versionado pendiente** y **Revisar plan pendiente**. El resumen indica la fecha y número de archivos previstos. Acepta **Conservar el plan incompleto y permitir uno nuevo** antes de aplicar. Después usa **Respaldar ahora** para analizar el origen actual. Conservar el plan no actualiza la fecha del último respaldo ni verifica datos nuevos. La bandeja mantiene un aviso hasta realizar un respaldo posterior. Si se cancela o interrumpe la conservación, vuelve a revisar: la interfaz ofrece reanudar su diario, también con el origen ausente. Los puntos completos anteriores permanecen disponibles.

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

## Conservar el trabajo y continuar en otro destino

Cuando el origen ya no permite completar un borrador o diario, se puede continuar sin descartarlo:

1. Espera a que termine el trabajador o cancélalo en un límite seguro. No retires el trabajo mientras esté activo.
2. Selecciona **Editar trabajo y horario → Retirar trabajo; conservar datos y recuperación** y **Guardar cambios**. Se retira su horario propio y queda deshabilitado. Conserva el nombre anterior para identificar la incidencia.
3. Usa **Nuevo trabajo** con el origen actual y el modo deseado. Revisa una **carpeta dedicada nueva**. No selecciones ni reutilices el destino del trabajo anterior.
4. Respalda y verifica el trabajo nuevo. El anterior conserva historial, intenciones, objetos, diarios y puntos confirmados; sigue disponible para recuperar. No se anuncia como terminado ni se traslada su estado parcial al nuevo trabajo.

Esta decisión evita introducir un segundo protocolo de abandono para borradores y para una carpeta actual que puede estar parcialmente modificada. Conserva el diario original y sus restricciones. El trabajo anterior sigue ocupando espacio y la limpieza continúa bloqueada cuando haya operaciones pendientes. Reactivarlo no descarta el diario ni cambia sus requisitos: intenta completar el plan original. Solo el abandono revisado de versionado completo permite comenzar otro análisis dentro del mismo almacén.

## Pruebas y límites

`--version-abandon` interrumpe cada paso del diario y las cuatro escrituras del evento replicado, conserva los límites de publicación de ambos catálogos y del recibo temporal y rechaza el recibo final. Termina un trabajador real después de mover un catálogo y reanuda sin origen. Prueba una copia parcial cuyo origen cambia, el nuevo respaldo, referencias compartidas, cuarentena, deshacer, purga con reloj sintético y rechazo del diario dañado.

El mismo arnés controla trabajadores de producto reales: pausa, continúa, cancela, termina el proceso y reanuda sin origen. Conserva las fechas de respaldo y rechaza usar un intento de copia como autorización. `--product-smoke` añade revisión y aceptación WPF, cierre de ventana con el proceso activo, recuperación del resultado y reanudación del diario después de cancelar. Termina con un respaldo nuevo verificado. Ambos recorridos pasaron localmente.

`--product-preserved-smoke`, incluido también en `--product-smoke`, recorre desde WPF las cuatro interrupciones de escritura del descriptor, dos borradores con JSONL o descriptor truncados y, en acumulativo y espejo, los límites de plan, preparación y promoción. Retira el trabajo, reabre la interfaz, crea otro mediante el asistente y verifica el respaldo nuevo. Con el origen ausente restaura el punto anterior del trabajo retirado. Compara nombres y SHA-256 de todos los archivos del destino anterior antes y después: sus bytes permanecen iguales. La consulta de horarios se inyecta en este recorrido; la retirada nativa conserva sus pruebas separadas de instalación y configuración.

Los doce casos pasaron también desde el paquete del candidato `8bde43f` en la [validación Windows 42068611369](https://github.com/mikepchelper-spec/FolderTwin/actions/runs/42068611369). [Identidad y evidencia del paquete](../../ENTREGA.md). Estas comprobaciones no sustituyen reinicio abrupto, USB físico ni las observaciones de entorno de la matriz de v1. El corte eléctrico físico queda excluido.
