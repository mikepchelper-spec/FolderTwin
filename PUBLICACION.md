# Publicación experimental de FolderTwin

**RC1 retirada durante la auditoría posterior: no usar ni iniciar nuevas ejecuciones.** El ZIP oficial se eliminó y su URL devuelve 404; se conservan etiqueta, fuentes, hashes e informe histórico. Conserva configuración, diarios y respaldos sintéticos sin purgarlos. La retirada no puede revocar descargas anteriores o cachés externas. [Auditoría y correcciones en curso](AUDITORIA.md).

El resto de esta sección registra la publicación y aceptación **históricas** de RC1. No describe una descarga actualmente disponible ni aprueba su uso. La candidata corregida tendrá otra etiqueta, aceptación y hashes. Sigue siendo una vista previa para datos sintéticos y carpetas desechables, sin v1 definitiva ni aceptación para respaldos personales.

Publicación: **5 de noviembre de 2026, 23:01:28, America/Bogota (UTC−05:00)**. La API registra `2026-11-06T04:01:28Z`. Se publicó el borrador ya revisado, ID `435666824`, sin reconstruir ni sustituir sus archivos.

## Identidad y acceso público

- [Página de la prerelease](https://github.com/mikepchelper-spec/FolderTwin/releases/tag/v1.0.0-rc.1).
- ZIP para Windows x64: retirado (el hash histórico se conserva abajo).
- [Etiqueta y fuentes](https://github.com/mikepchelper-spec/FolderTwin/tree/v1.0.0-rc.1), resuelta a `e4e87b840eedc8139fd49e389f2cd143cd6ef61c` mediante la API pública y un fetch de la etiqueta.
- [Metadatos públicos de la entrega](https://api.github.com/repos/mikepchelper-spec/FolderTwin/releases/tags/v1.0.0-rc.1): `draft=false`, `prerelease=true`, ID `435666824`. No se marcó Latest; la consulta pública `/releases/latest` devolvió 404 porque no hay una versión estable publicada.

La página respondió HTTP 200 sin redirigir a autenticación. Los cuatro adjuntos se descargaron desde sus URL públicas sin token de GitHub ni cookies de navegador. Se compararon tamaños y SHA-256 con el informe aceptado y los digests de GitHub:

| Adjunto | Bytes | SHA-256 |
|---|---:|---|
| `FolderTwin-win-x64-e4e87b8.zip` | 76.941.003 | `2f795b694fa700c9993cb8cf8817017b39bed83d6212cc199eec71071bea7760` |
| `SHA256SUMS.txt` | 49.742 | `265e119541eef24f735371ffa87190795427edddcd6f567c38be4af9baf31e02` |
| `SHA256SUMS-zip.txt` | 97 | `37eb36594f2fbea9365dec6a87634377001c241cb3344b30fc2ed7890be0ae23` |
| `ENTREGA.md` | 9.045 | `c68414a1272192f0a69c2b002edfb85469a24d9a1a332f4a4c7c5ee1e8358357` |

El ZIP público se extrajo en una carpeta nueva desechable y pasó `scripts/Test-Portable.ps1`: commit, runtime .NET 10.0.14, fuentes archivadas, inventario exacto de **499 archivos**, cada hash, licencias, guías y enlaces locales. SHA-256 de las fuentes incluidas: `40d49fc91a6d1022ee0bbbae35e0cc12a30a975bfb1acac453f895943e4e3ecc`. El texto de LICENSE coincide con el repositorio y la declaración de la distribución es GPL-3.0-or-later.

La [aceptación Windows 42124550998](https://github.com/mikepchelper-spec/FolderTwin/actions/runs/42124550998) sigue aprobada para ese commit: dos compilaciones limpias iguales y aceptación de motores, WPF, horarios, instalación, actualización, retirada y recuperación. No se repitió ni se recompiló el software para añadir las guías de comunidad. La evidencia de VHDX y las observaciones de escritorio conservan sus identidades y límites en [ENTREGA.md](ENTREGA.md).

El adjunto ENTREGA es el **informe congelado de aceptación anterior a publicar**. Sus referencias al borrador describen aquel momento y no significan que la entrega siga oculta. Este registro describe la publicación posterior; no se reescribe el informe ni se introduce en el ZIP para que se apruebe a sí mismo. Las notas públicas explican esa separación.

## Preparación de comunidad y mantenimiento

[Contribuir](CONTRIBUTING.md), [soporte](SUPPORT.md), [seguridad](SECURITY.md) y [mantenimiento](MANTENIMIENTO.md) están en la rama principal. Se añadieron dos formularios, para errores y recuperación, y una plantilla de solicitud de cambios. Sus definiciones usan JSON compatible con YAML y se validaron sin instalar dependencias: claves, tipos, IDs únicos, evidencia requerida y aceptación de privacidad. Las etiquetas usadas ya existían.

La [página comunitaria pública](https://github.com/mikepchelper-spec/FolderTwin/community) reconoce los formularios y la política de seguridad. El reporte privado de vulnerabilidades se habilitó y se confirmó por la API; la página pública de Security muestra su enlace. No se inventaron correos ni canales externos y no se crearon incidencias o reportes ficticios para comprobarlos. La vista interactiva de crear incidencias exige sesión y el navegador local está deshabilitado; no se acredita una prueba de envío mediante esa interfaz.

Los formularios y guías piden solo evidencia mínima, sin respaldos, credenciales, rutas personales, configuraciones exportadas ni logs completos. No prometen plazos de atención ni recuperación de datos personales. El procedimiento de mantenimiento deja clasificar P0/P1/P2, reproducir con datos inventados, retirar el ZIP oficial ante un problema grave, corregir con regresión y preparar otra entrega con etiqueta nueva, fuentes, reproducción e informe separado.

Antes de publicar se revisaron los hashes de los cuatro adjuntos, la aceptación, el destino de la etiqueta y el contenido versionado. La revisión no encontró patrones de rutas personales o credenciales ni archivos de respaldo, configuración o compilación incorporados. Los registros y descargas locales de comprobación permanecen fuera del repositorio.

## Pendientes y siguiente trabajo real

Permanecen pendientes **piloto de 7–14 días, USB NTFS físico, reinicio/suspensión/cierre de sesión, otra cuenta o equipo, nube real y aceptación completa de escritorio, lector de pantalla y escalado/DPI reales**. No hay fechas de piloto inventadas ni aprobaciones simuladas. El corte eléctrico físico, los respaldos personales y FreeFileSync siguen excluidos.

El siguiente trabajo es clasificar incidencias reales si llegan, reproducirlas solo con datos sintéticos y corregir los fallos confirmados; cuando exista entorno, ejecutar [PILOTO.md](PILOTO.md) y registrar las filas externas de [V1.md](V1.md). No se inventan funciones o fallos para prolongar este goal ni se espera indefinidamente a que aparezca una incidencia. La publicación experimental y su mantenimiento inicial quedan preparados sin declarar cerrada la v1 definitiva.
