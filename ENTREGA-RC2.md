# FolderTwin v1.0.0-rc.2 · aceptación de la candidata experimental

Esta entrega es para Windows 11 x64, datos sintéticos y carpetas desechables sobre NTFS local. Incluye acumulativo, versionado, espejo protegido y retención manual. El piloto y las validaciones de entorno siguen pendientes.

| Identidad | Valor |
|---|---|
| Fuentes | d902f4ca9608480933d7cb392dcb557aba36ea33 |
| Aceptación Windows | [42212336681](https://github.com/mikepchelper-spec/FolderTwin/actions/runs/42212336681) |
| Artefacto de CI | 13500581176 |
| ZIP | FolderTwin-win-x64-d902f4c.zip |
| Bytes | 77025240 |
| SHA-256 ZIP | 9c701f84c58b2420618296b31bfe75c2fc6082383fdd906ff64e149391385e4a |
| SHA-256 manifiesto | 15c7ebb6b2a504a859b5cb2dd1cbb86b1d43af01a7f35e8b008acd724f48b694 |
| SHA-256 fuentes archivadas | 5792f611c09ad1695f8f2caf5c388b1a8b65b8076756b68203951739cb34033d |
| SHA-256 CopyProbe.dll | d88d3b9d4f35b07aa80c1dbc7d90c41f0aa462f3d27f6fce75b92e7168cb2a05 |
| Runtime incluido | .NET y Windows Desktop 10.0.14 |
| Inventario | 499 archivos con hash, más SHA256SUMS.txt |

La aceptación Windows ejecutó las pruebas del motor, generó dos distribuciones desde árboles limpios con el mismo manifiesto y probó WPF y el Programador de tareas desde el paquete. El ZIP corresponde al digest del artefacto de CI. La comprobación local verificó la identidad, el archivo de fuentes, el inventario, cada hash, las licencias y los enlaces de las guías. Este informe se generó después y se distribuye como adjunto separado.

## Auditoría y regresiones

- P0 del proyecto. Se corrigió una frontera de escritura del caché local SQLite. Seis intercalaciones sintéticas preservan una base centinela independiente. Los controles de base ajena incluyen WAL pendiente. Ante pérdida parcial se conservan los diarios y se lee desde catálogos. Pasa la reconstrucción de una caché ausente.
- P1. El respaldo y la revisión de origen importado usan ajustes leídos bajo el bloqueo de ejecución. Se comprobaron deshabilitación, retirada, habilitación y conservación de una revisión concurrente.
- P2. Bandeja y lista mantienen incidencias de respaldo y verificación separadas. Acumulativo conserva exclusivos y reconoce nombres que solo cambian de mayúsculas. Los lectores rechazan elementos nulos y secuencias agotadas. El análisis del horario detecta siete alteraciones de su política.
- El paquete abre formatos anteriores, conserva planes e historial y reanuda trabajadores propios. Pasa la retirada con archivos ajenos incorporados después del inventario.
- WPF pasa arranque sin argumentos en perfil sintético limpio, asistente, trabajos, progreso y controles, historial, recuperación, configuración, retención, gestión de aplicación y bandeja. Los ensayos automatizados incluyen claro y oscuro, foco, teclado y tamaños de ventana.

Resultados de esta ejecución:

- Reader audit: 24 bounded semantic cases; failures=0; no source/target from user configuration.
- Differential audit: cases=9; points=52; failures=0; seconds=14.860; peakResident=79835136. Budget: <64 MiB synthetic payload, 120 seconds, 256 MiB free reserve.
- PASS recovery audit seed=58203: 40 independent calendar selections; 6 points, UTC offset equivalence, independently hashed reference union, offline quarantine/undo/purge, shared-expired rescue and corruption fail-closed; seconds=2.031.

El laboratorio VHDX pasó con el commit anterior 6ba2c91. En los tres modos provocó 0x80070070 en un volumen desechable, preservó el punto previo y reanudó la misma generación. También comprobó ausencia, sustitución de letra, retorno y recuperación sin origen. La diferencia hasta d902f4c se limita al caché WPF, su prueba y documentación. El motor de esos recorridos no cambió. [Laboratorio VHDX 42210313861](https://github.com/mikepchelper-spec/FolderTwin/actions/runs/42210313861)

## Compatibilidad y consumo

El binario de RC1 con SHA-256 2bdfc41d7685bfd1dab5f268c2b2b3ae49d9c1aae848160fe29c1461ca5c4893 coincide con la entrada del ZIP histórico 2f795b694fa700c9993cb8cf8817017b39bed83d6212cc199eec71071bea7760. Creó los tres trabajos, puntos, historiales y configuraciones exportadas del fixture. El motor de este ZIP reabrió los registros, verificó y restauró sin origen y coincidió con un modelo independiente de bytes y árbol. Tres modos en 18.980 s para la campaña con dos compilaciones del arnés. Downgrade, otra cuenta y otro equipo quedan fuera de la evidencia.

La medición empaquetada tomó cinco muestras calientes por tamaño, con reserva de 2 GiB y payload limitado a 32 MiB.

| Intentos | Mediana ms | Lecturas | Bytes leídos | Bytes asignados | Delta máx. handles/muestra |
|---:|---:|---:|---:|---:|---:|
| 100 | 13.52 | 407 | 58,887 | 2,166,256 | 1 |
| 1000 | 129.08 | 4007 | 573,688 | 20,697,640 | 0 |

Son contadores del proceso. Se conserva el historial completo y la consulta asíncrona. La escala previa de 329.000 rutas y las observaciones de escritorio conservan la revisión en que se hicieron.

## Límites pendientes

Piloto de 7–14 días, USB NTFS físico, reinicio, suspensión y sesión, otra cuenta o equipo, nube real y accesibilidad con lector y DPI reales. Las campañas usaron datos sintéticos y volúmenes desechables. Los resultados describen pruebas de software. [Procedimiento del piloto](https://github.com/mikepchelper-spec/FolderTwin/blob/d902f4ca9608480933d7cb392dcb557aba36ea33/PILOTO.md)
