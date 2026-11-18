# Notas del candidato de FolderTwin 1.0

Serie candidata experimental de FolderTwin 1.0 para ensayos con datos sintéticos y carpetas desechables. **`COMPILACION.json`** identifica el paquete distribuido. El informe de aceptación y el hash del ZIP se entregan por separado y deben identificar el mismo commit y manifiesto. Consulta [la guía de uso](GUIA.md) y [el protocolo del piloto](PILOTO.md).

## Novedades y mejoras técnicas

- **Separación estricta de producción y pruebas:** el paquete distribuye únicamente **`FolderTwin.exe`**, `FolderTwin.dll` y `FolderTwin.Core.dll`, sin código de arneses (`*Probe`/`*Smoke`) ni banderas CLI de ensayo. Mantiene compatibilidad de actualización y retirada para tareas creadas con instalaciones anteriores (`CopyLab.exe`).
- **Restauración local limpia:** la recuperación escribe mediante un área de preparación oculta (`.foldertwin-restore-staging`), agrupa handles por carpeta padre, verifica cada archivo y limpia el área temporal al terminar o cancelar, entregando únicamente los archivos y carpetas del usuario.
- **Protección reforzada en Copia acumulativa:** detecta borrados externos, alteraciones (*bitrot*) y colisiones archivo↔carpeta sobre archivos exclusivos previamente confirmados en el destino, deteniendo la operación con conflicto explícito antes de publicar un catálogo nuevo.
- **Vigilancia acotada en los tres modos:** el modo **Versionado** incorpora también comparación rápida acotada (`Scoped`) por metadatos y avisos del monitor, además de Copia acumulativa y Espejo protegido, con intervalo de reconciliación adaptable al tamaño real del árbol.
- **Retención, abandono y registro sin techos artificiales:** se elimina el interbloqueo entre cuarentena pasiva y abandono de planes incompletos, se evitan relecturas SHA-256 duplicadas del almacén de objetos durante la auditoría y purga, y el registro de generaciones archiva comprobantes históricos de forma encadenada para no agotar su límite vivo de 16 MiB.
- **Tercera copia en nube fuera de `C:`:** la preparación temporal de subidas se realiza en `.foldertwin-cloud-stage-<id>` dentro del volumen del respaldo local (sin llenar `%LOCALAPPDATA%`), con cachés de Kopia acotadas, bloqueos de rango de bytes no destructivos y exclusión cruzada entre restauraciones locales y remotas.
- **Fluidez de interfaz, SQLite y accesibilidad:** toda E/S de registro, procesos y discos se ejecuta fuera del hilo `Dispatcher`; la consulta periódica de espacio abre destinos externos en modo solo lectura (`writable: false`); el índice SQLite usa `CallingConvention.Winapi`, conexiones de solo lectura en consultas, filtrado/paginación en SQL y poda de vistas antiguas; y se añade soporte real de colores de sistema en Alto Contraste y navegación `Enter`/`Backspace` en `RecoveryFiles`.

## Funciones disponibles

- Entrada **`FolderTwin.exe`** sin argumentos y asistente de origen, destino dedicado, modo, disparadores y revisión.
- Copia acumulativa, versionado y espejo protegido sobre raíces locales NTFS, con identidades de volumen/directorio y bloqueos compartidos de lectura/escritura.
- Trabajadores independientes para respaldo, verificación, restauración, revisión del espejo, vigilancia y envíos a nube, con progreso, pausa, cancelación e historial paginado.
- Navegación y búsqueda por fecha y carpeta, comparación de cambios, conservación de `Zone.Identifier` (hasta 16 KiB) y restauración en carpeta nueva incluso con el origen ausente o mediante **Abrir respaldo existente**.
- Tercera copia cifrada opcional (S3, Backblaze B2, S3 compatible y perfiles experimentales Filen/MEGA mediante pasarela local `rclone`) con recuperación completa o selectiva desde un perfil vacío.
- Retención manual con revisión, cuarentena reversible, deshacer y purga tras el plazo de gracia.
- Exportación e importación de configuración con revisión de identidades y activación expresa; acceso a formatos previos mediante **Respaldos de alfa**.

## Distribución y actualización

El paquete portable incluye el runtime de .NET 10, `COMPONENTES.json` (CycloneDX), `SHA256SUMS.txt`, licencias, guías y `FolderTwin-source.tar` ( más `cloud-tools/` en el paquete con soporte de nube). Dos compilaciones desde árboles limpios producen el mismo manifiesto y el mismo ZIP determinista.

Extrae una actualización en una carpeta nueva y conserva la anterior hasta verificar reapertura y restauración. **Aplicación** instala el ejecutor verificado en `%LocalAppData%\FolderTwin\app\<hash>`, traspasa los horarios propios habilitados y actualiza el monitor registrado (incluidos trabajos con vigilancia o copia en nube activa). [Procedimiento detallado](INSTALACION.md) · [Compatibilidad](COMPATIBILIDAD.md).

## Pendientes antes de cerrar v1

- Piloto controlado de 7–14 días ([PILOTO.md](PILOTO.md)).
- Validaciones externas en hardware y entornos reales ([V1.md](V1.md)): USB NTFS físico, ciclos de sesión/suspensión/reinicio de Windows, otra cuenta o equipo, cuentas reales en proveedores de nube, lector de pantalla activo y escalado/DPI físico entre monitores.

> Los ensayos sintéticos, la inyección de fallos y los discos virtuales VHDX no garantizan recuperación frente a cortes eléctricos físicos ni durabilidad en servicios externos. Usa únicamente datos sintéticos y carpetas desechables; conserva aparte tus respaldos personales y FreeFileSync.
