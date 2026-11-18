# Plan de arquitectura y seguridad de datos — FolderTwin

*Copias locales. Versiones recuperables.*

Aplicación de escritorio WPF para Windows 11 x64 sobre volúmenes locales NTFS, con separación estricta entre los ensamblados de producción (`src/FolderTwin.Core` y `src/FolderTwin.App`, que generan únicamente `FolderTwin.exe`) y los arneses de verificación sintética (`tests/`).

- **Estado actual y matriz de aceptación:** [V1.md](V1.md) · [Notas del candidato](NOTAS.md)
- **Evolución de versiones, retos resueltos y mediciones:** [HISTORIAL.md](HISTORIAL.md)

---

## 1. Objetivo de producto

En menos de dos minutos, una persona debe poder elegir qué carpeta proteger, en qué disco guardarla y con qué política conservarla, así como verificar o recuperar un archivo desde la misma aplicación sin depender de comandos, hashes manuales ni el origen original.

- **Alcance soportado de v1:** Windows 11 x64 y raíces locales NTFS con identidades físicas verificables ( más tercera copia cifrada opcional sobre S3 o pasarela local `rclone`). Una copia en el mismo disco físico no protege frente al fallo de ese disco.
- **Fuera de v1:** sincronización bidireccional, sistemas de archivos exFAT/FAT32 o recursos SMB/NAS sin identidad NTFS local soportada, archivos EFS, clonación arrancable del sistema operativo, deduplicación por bloques propia, cifrado casero y promesas de detección de ransomware.

---

## 2. Interfaz y semántica de los modos

El asistente inicial guía cuatro pasos explícitos (**Qué quieres respaldar → Dónde guardarlo → Cómo respaldarlo → Revisa el trabajo**) creando siempre una subcarpeta dedicada nueva en el destino; nunca se apropia silenciosamente de un disco completo ni sobrescribe un respaldo anterior.

| Modo | Comportamiento verificado | Propósito para el usuario |
| --- | --- | --- |
| **Copia acumulativa** | Añade archivos nuevos y actualiza los modificados en `mirror/current`; conserva los exclusivos del destino. Si un archivo previo o exclusivo del destino fue editado, borrado o cambiado de tipo por fuera de FolderTwin, detiene la operación con conflicto explícito. | «Conservar en el destino incluso lo que después borre del origen» |
| **Versionado** *(recomendado)* | Conserva puntos recuperables por fecha mediante objetos inmutables SHA-256 compartidos y catálogos JSONL duplicados, permitiendo comparar cambios y restaurar archivos modificados o eliminados hasta que se aplique retención manual. | «Poder volver a una fecha anterior verificable» |
| **Espejo protegido** | Mantiene `mirror/current` igual al origen actual, conservando antes el estado previo en puntos recuperables. Las retiradas, el origen vacío y los cambios masivos exigen revisión explícita o autorización por trabajo. | «Mantener un reflejo navegable guardando versiones de lo retirado» |

**Disparadores independientes del modo:** cada trabajo puede ejecutarse **a demanda**, con **horario diario** (Programador de tareas de Windows) y/o con **vigilancia continua** (`FileSystemWatcher` con ventana de calma, comparación rápida acotada `Scoped` en los tres modos y reconciliación completa periódica adaptable al tamaño del árbol).

**Criterios permanentes de la vista Ruta y paneles secundarios:**
- Separar siempre la fecha del último respaldo confirmado, la última verificación integral y el estado de la operación activa o de la tercera copia en nube.
- Mantener toda E/S de disco, registro y procesos fuera del hilo `Dispatcher`, pausar temporizadores de refresco en bandeja y consultar horarios con apertura de solo lectura (`writable: false`) para no despertar discos externos innecesariamente.
- Ofrecer ayuda contextual en los 100 controles (`F1` y [AYUDA-CONTROLES.md](AYUDA-CONTROLES.md)), navegación completa por teclado, temas claro/oscuro/Alto Contraste real del sistema y diagnóstico compartible sin rutas ni nombres privados.

---

## 3. Arquitectura mínima y componentes

| Capa | Elección técnica | Motivo y límite |
| --- | --- | --- |
| **Motor (`src/FolderTwin.Core`)** | C# / `.NET 10`, Win32 nativo por handles (`DirectoryFence`), sin dependencias NuGet externas | Control exacto de compartición de lectura, identidades NTFS (`FILE_ID_INFO`), `Zone.Identifier` (`BackupRead`/`BackupWrite`) y diarios verificados por SHA-256. |
| **Aplicación (`src/FolderTwin.App`)** | WPF `.NET 10` (`FolderTwin.exe`) + trabajadores en procesos hijos sin ventana | Cerrar la ventana principal o la bandeja no interrumpe copias, verificaciones, restauraciones ni envíos a nube en curso. |
| **Programación y vigilancia** | Programador de tareas de Windows (`IgnoreNew`, `StartWhenAvailable`) + monitor supervisor | Ejecución bajo la sesión interactiva del usuario actual sin servicios privilegiados ni elevación UAC. |
| **Índice de navegación** | SQLite del sistema Windows (`winsqlite3.dll`) en almacenamiento fijo local (`WAL` + consultas `SQLITE_OPEN_READONLY`) | Acelerador reconstruible para explorar y buscar cientos de miles de rutas; perder `index.db` nunca impide verificar ni restaurar desde los catálogos del respaldo. |
| **Tercera copia cifrada** | **Kopia 0.23.1** (adaptador S3, `layout 2`) + **rclone 1.76.0** en puente loopback `127.0.0.1:0` | Cifrado probado de extremo a extremo sin inventar criptografía propia, staging temporal en el volumen del respaldo local (fuera de `C:`) y recuperación autónoma con herramientas estándar. |

---

## 4. Contrato de seguridad de los datos

1. **Origen de solo lectura:** Rechazar `origen == destino`, carpetas anidadas en cualquier sentido, solapamiento con otros trabajos o con restauraciones activas locales/remotas y *reparse points* inesperados.
2. **Identidad física sobre letras de unidad:** Vincular cada raíz por `VOLUME_NAME_GUID`, identidad NTFS de 24 bytes y marcador `.foldertwin-destination`. Si el disco cambia o un alias reutiliza la letra de unidad, detenerse sin escribir.
3. **Incompleto nunca equivale a vacío:** Un origen ausente, denegado o con errores de enumeración jamás activa retiradas; un origen legítimamente vacío exige confirmación expresa antes de reflejarse en un espejo.
4. **Copia verificada antes de promover:** Leer el origen denegando escritura concurrente cuando Windows lo permita, escribir en temporal exclusivo en el destino, vaciar buffers (`Flush(true)`), releer para comprobar SHA-256 y publicar sin sobrescribir objetos existentes; ejecutar `FlushDirectory` en publicaciones críticas.
5. **Restauración limpia y aislada:** Preparar toda restauración local en `.foldertwin-restore-staging` dentro de una carpeta nueva vacía y retirar por completo los artefactos de preparación antes de confirmar.
6. **Presupuesto de espacio conservador:** Comprobar espacio libre (incluidos catálogos duplicados, descriptores, `ZoneData` y reserva) antes de copiar; ante disco lleno (`0x80070070`), conservar intacto el último punto confirmado.
7. **Metadatos explícitos y acotados:** Conservar fechas UTC de creación/modificación, atributo `Hidden` y flujo `:Zone.Identifier` de hasta 16 KiB; rechazar otros flujos alternativos NTFS o archivos cifrados con EFS informando la ruta afectada.
8. **Archivos bloqueados sin medidas intrusivas:** Ante `ERROR_SHARING_VIOLATION`, consultar Restart Manager fuera del hilo UI de forma orientativa sin cerrar procesos del usuario ni relajar la consistencia de lectura.
9. **Archivos de nube bajo demanda no hidratados:** Detectar marcadores pendientes de descarga sin forzar su hidratación automática ni interpretarlos como archivos borrados.
10. **Manifiestos y registro auto-verificables:** Catálogos JSONL UTF-8 sin BOM duplicados (`manifests/` y `manifests_backup/`, hasta 512 MiB y 350 000 entradas) sellados con confirmación binaria `FTCAT001`, más registro replicado de generaciones con archivo histórico encadenado y reparación de colas con ceros certificadas por la otra réplica.
11. **Retención en cuatro pasos:** Revisión de referencias activas → cuarentena reversible en el mismo volumen (`quarantine/<guid>/`) → plazo de gracia obligatorio → purga explícita de objetos y fin de vida verificado de metadatos. Nunca se borra automáticamente el último punto confirmado de un origen.
12. **Límites honestos frente al hardware:** `FlushFileBuffers` y la relectura verificada detectan corrupción accidental y fallos lógicos, pero no sustituyen varias copias físicas independientes frente a cortes eléctricos reales, fallos de controladora/firmware o pérdida del medio.

---

## 5. Especificaciones y trabajo externo pendiente

- **Protocolos detallados en `docs/`:** [Catálogos](docs/CATALOG.md) · [Versiones](docs/VERSIONES.md) · [Espejo](docs/ESPEJO.md) · [Acumulativa](docs/ACUMULATIVA.md) · [Retención](docs/RETENCION.md) · [Ejecución](docs/EJECUCION.md) · [Planes pendientes](docs/PENDIENTES.md) · [Descriptor inicial](docs/PENDING.md) · [Configuración](docs/CONFIGURACION.md) · [Zone.Identifier](docs/ZONE-IDENTIFIER.md).
- **Tercera copia y distribución:** [Nube](NUBE.md) · [Recuperación de emergencia](RECUPERACION-NUBE.md) · [Instalación](INSTALACION.md) · [Compatibilidad](COMPATIBILIDAD.md) · [Seguridad de distribución](SEGURIDAD-DISTRIBUCION.md) · [Mantenimiento](MANTENIMIENTO.md).
- **Cierre de la v1 definitiva:** Permanece sujeto exclusivamente a las validaciones externas listadas en [V1.md](V1.md) (`EXT-01` a `EXT-05C`) y al protocolo de observación de [PILOTO.md](PILOTO.md).
