# Catálogo experimental de un lote inicial

Formato 1 del laboratorio de FolderTwin. Se limita a un punto inicial por destino NTFS registrado. No es el formato definitivo del almacén de versiones ni una garantía de durabilidad ante apagones.

## Archivos y estados

En la raíz dedicada del destino:

- `.foldertwin-catalog-primary.jsonl`
- `.foldertwin-catalog-backup.jsonl`
- `.foldertwin-catalog.commit`

Cada publicación usa el mismo nombre con sufijo `.tmp`, creación exclusiva, flush, relectura y renombrado por handle sin reemplazar archivos existentes. Usa el bloqueo `.foldertwin-batch.lock`. Los archivos de datos se verifican antes de escribir los catálogos y de nuevo antes de confirmar.

Orden de publicación: temporal primario → primario → temporal secundario → secundario → temporal de confirmación → confirmación. Un temporal íntegro para el mismo documento puede promoverse al reintentar. Uno truncado, ilegible o diferente se conserva para revisión. Un archivo final existente solo se acepta si coincide con lo esperado.

Dos JSONL publicados sin confirmación siguen siendo candidatos pendientes. El lector normal exige una confirmación válida y al menos una réplica íntegra coincidente con ella. Si solo una réplica es válida, comunica la degradación. Si ambas son válidas pero distintas, rechaza la importación. No reconstruye ni sobrescribe automáticamente la réplica dañada.

## JSON Lines

UTF-8 estricto, sin BOM, un objeto JSON por línea y LF obligatorio después de cada registro, incluido el último. Se rechazan CR, líneas vacías, duplicados de propiedades, propiedades desconocidas, registros incompletos, JSON fuera del esquema y datos después del pie. Máximo 64 KiB por registro incluido su LF, 8 MiB por documento, 10.000 entradas y profundidad JSON 16. La lectura mantiene un buffer de registro acotado y las entradas validadas en memoria; no carga un DOM de todo el archivo.

Orden: cabecera, carpetas en orden ordinal por ruta, archivos en orden ordinal por ruta y pie. Las propiedades usan los nombres indicados y distinguen mayúsculas. Los campos siguientes son obligatorios; no hay campos opcionales.

| Registro | Propiedades |
|---|---|
| Cabecera | `kind: "header"`, `version: 1`, `jobId: GUID`, `generationId: "initial"`, `identity: base64` |
| Carpeta | `kind: "directory"`, `path: ruta relativa` |
| Archivo | `kind: "file"`, `path: ruta relativa`, `size: entero de 64 bits no negativo`, `hash: base64` |
| Pie | `kind: "footer"`, `files: recuento`, `directories: recuento`, `hash: base64` |

`jobId` es el GUID del marcador del destino. `identity` contiene los 24 bytes obtenidos con `FILE_ID_INFO`: número de serie del volumen y ID del directorio. `generationId` solo admite `initial` en este experimento. No se incluyen rutas absolutas del origen ni del usuario.

El hash de archivo es SHA-256 del flujo de datos principal completo. El hash del pie es SHA-256 de los bytes exactos de la cabecera y las entradas, incluidos sus LF. Todos estos hashes ocupan 32 bytes antes de codificarse en base64. Para enlazar el documento con su confirmación se calcula además SHA-256 del JSONL completo, incluido el pie y su LF. La suma de tamaños debe caber en un entero con signo de 64 bits.

Las rutas usan separadores Windows y nombres admitidos por el prototipo, sin normalización silenciosa. Se rechazan raíces, rutas que escapan, streams alternativos, componentes vacíos o de más de 255 unidades UTF-16, puntos/espacios finales, UTF-16 no válido y el prefijo reservado `.foldertwin-`. No se admiten rutas duplicadas ni colisiones entre archivo y carpeta, incluso cambiando mayúsculas. Cada padre debe constar como carpeta con la misma escritura de la ruta. Máximo 64 componentes de carpeta y un componente adicional para el nombre de archivo.

## Confirmación binaria

112 bytes exactos:

| Desplazamiento | Longitud | Contenido |
|---|---:|---|
| 0 | 8 | ASCII `FTCAT001` |
| 8 | 32 | SHA-256 del JSONL completo que deben compartir ambas réplicas |
| 40 | 24 | Identidad del directorio destino |
| 64 | 16 | GUID del marcador en el orden de `Guid.ToByteArray()` de .NET |
| 80 | 32 | SHA-256 de los primeros 80 bytes |

La identidad y el marcador deben coincidir con el directorio abierto. Copiar los tres archivos y el marcador a otra carpeta no autoriza su importación. La reasociación a un disco o equipo diferente queda pendiente.

## Restauración y límites

El lector reconstruye rutas, tamaños y hashes desde una réplica confirmada, valida todo el documento antes de preparar escrituras y usa el motor de lotes para restaurar a otra raíz registrada. Ese motor verifica cada archivo contra el hash del catálogo y puede dejar un resultado parcial ante contenido dañado. El origen inicial y SQLite no intervienen. La lectura/importación y la restauración coordinan el acceso al respaldo mediante el mismo bloqueo, sin modificar el contenido de la unidad de respaldo.

El checksum detecta corrupción accidental; no autentica frente a alguien que pueda modificar y recalcular el catálogo y la confirmación. Las réplicas comparten dispositivo y no son copias independientes frente a su fallo total. La confirmación todavía no está replicada: si falta o se daña, el lector normal se detiene aunque un JSONL siga siendo válido. El rescate explícito desde candidatos no confirmados queda pendiente. El punto contiene observaciones por archivo, sin instantánea coherente entre varios archivos. No incorpora objetos CAS, varias generaciones, retención, reparación automática ni metadatos NTFS. La intención de ejecución pendiente se define aparte en [PENDING.md](PENDING.md); un punto completo no depende de ese descriptor. Las pruebas de interrupción de esta publicación son inyectadas; no equivalen a un corte eléctrico.
