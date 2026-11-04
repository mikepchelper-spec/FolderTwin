# Descriptor experimental de ejecución pendiente

Versión 1, limitada a una copia inicial dentro de una raíz de laboratorio sintética. Define la intención de ejecución, no un punto de restauración confirmado. El formato de los puntos completos está en [CATALOG.md](CATALOG.md).

## Archivos y publicación

En la raíz registrada del destino:

- `.foldertwin-pending.jsonl`: lista de carpetas y archivos con tamaños y SHA-256, usando el formato JSONL del catálogo.
- `.foldertwin-pending-location.json`: ubicación relativa del origen e identidades que enlazan esa lista con el trabajo.

Ambos usan temporales con sufijo `.tmp`, creación exclusiva, flush, relectura y renombrado por handle sin reemplazo. Orden: JSONL temporal → JSONL final → ubicación temporal → ubicación final. El lector exige los dos finales válidos y enlazados. La copia de datos empieza después de publicarlos.

Al reintentar el guardado, un temporal completo del mismo contenido puede promoverse. Un temporal incompleto o un archivo final diferente se conserva y detiene el guardado. La selección del trabajo y la ejecución usan el bloqueo existente `.foldertwin-batch.lock`. El guardado, la lectura, la copia y la confirmación son fases separadas; no forman una transacción atómica del sistema de archivos.

## Documento de ubicación

Un objeto JSON UTF-8, máximo 64 KiB, propiedades camelCase y todos los campos obligatorios:

```json
{
  "payload": {
    "version": 1,
    "fixtureIdentity": "base64 de 24 bytes",
    "sourceRelative": "Origen",
    "sourceIdentity": "base64 de 24 bytes",
    "metadataHash": "base64 de 32 bytes"
  },
  "checksum": "base64 de 32 bytes"
}
```

`fixtureIdentity` identifica la raíz del laboratorio. `sourceRelative` es una ruta de directorio canónica dentro de esa raíz, distinta y separada del destino. `sourceIdentity` identifica el volumen y directorio de origen del análisis. Las identidades tienen la misma representación de 24 bytes que el catálogo. No se guardan rutas absolutas del usuario.

`metadataHash` es SHA-256 de todos los bytes del JSONL, incluidos el pie y su LF. `checksum` es SHA-256 de la serialización canónica del objeto `payload` con las opciones de `PendingBatch`: orden de campos declarado, UTF-8, camelCase, sin indentación y encoder predeterminado de `System.Text.Json`. El lector rechaza campos ausentes, duplicados, desconocidos o nulos, versiones distintas, identidades/tamaños inválidos y profundidad JSON mayor de 16. El checksum comprueba los valores del payload; no autentica frente a alguien que pueda recalcularlo.

La identidad de la raíz debe coincidir con el directorio abierto. El JSONL conserva el ID y marcador del destino; se verifican también contra su handle y marcador real. Copiar ambos documentos y el marcador a otra carpeta no autoriza ese destino. Un descriptor truncado o incoherente no se sustituye automáticamente.

## Reconstrucción del progreso

No se guardan flags de éxito ni porcentajes. Al reabrir, se recorren las rutas del plan y se usan el protocolo por archivo, sus registros e identidades y una nueva verificación de hashes:

1. Un archivo final con registro válido para el hash esperado se vuelve a verificar.
2. Un temporal con registro válido para ese hash puede publicarse.
3. Datos sin registro completo, conflictos y hashes incorrectos se conservan para revisión.
4. Solo los archivos sin copia verificada necesitan el origen. Antes de copiarlos se comprueban su identidad, el inventario y el hash esperado. Un reemplazo de la carpeta original no se acepta aunque tenga iguales nombres y bytes.

Si falta el origen, lo ya guardado puede reconocerse o recuperarse y el resto queda parcial con explicación por ruta. Si todos los archivos del plan ya están verificados en destino, el lote puede confirmarse sin abrir el origen. Las carpetas del plan se crean o validan relativas a la raíz registrada. No se retiran archivos.

La reanudación conserva el contenido analizado. No adopta cambios posteriores en el origen ni significa que el punto refleje todos sus cambios actuales. Un archivo nunca copiado no puede recuperarse sin el origen u otra copia independiente.

## Alcance de las pruebas y límites

`--resume` termina procesos de prueba en escritura, después del registro verificado y después de la publicación del primer archivo. Otros procesos reabren el descriptor, comprueban resultados parciales con el origen apartado y completan el lote al devolverlo. También se prueba confirmar con todos los datos ya guardados y el origen ausente, un origen sustituido, descriptores alterados, datos pendientes sin verificar y fallos inyectados al publicar el descriptor.

La ventana de laboratorio descarta el plan de memoria entre un resultado parcial y el reintento. Aún crea una sesión nueva al arrancar y retira sus archivos conocidos al cerrar normalmente; no ofrece un explorador de sesiones previas. La reapertura de un trabajo de otro proceso se valida mediante el arnés.

Los dos archivos del descriptor no están replicados. Si se dañan, la reanudación normal se detiene y se conserva el contenido para revisión. Un catálogo completo ya confirmado sigue teniendo su propio lector y no depende de estos archivos. No hay migración de formato, adopción de respaldos ni reasociación automática de fuentes/destinos. Las pruebas con terminación de procesos no demuestran durabilidad de energía.
