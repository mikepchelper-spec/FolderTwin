# Candidato de software de FolderTwin 1.0

Estado del 5 de noviembre de 2026: **e4e87b8 aceptado como candidato de software**. El cierre de este alcance es independiente del piloto y de los entornos externos ausentes. No se declara v1 definitiva ni se ha iniciado el piloto. Este informe se produce después de compilar y probar el paquete y se distribuye fuera del ZIP.

## Artefacto elegido

| Dato | Valor |
|---|---|
| Código | `e4e87b840eedc8139fd49e389f2cd143cd6ef61c` |
| Plataforma objetivo | Windows 11 x64, NTFS local, retención manual |
| Runtime incluido | .NET 10.0.14; no requiere instalación separada |
| Aceptación Windows | [42124550998 — aprobada](https://github.com/mikepchelper-spec/FolderTwin/actions/runs/42124550998) |
| Artefacto | `FolderTwin-win-x64-e4e87b840eedc8139fd49e389f2cd143cd6ef61c`, ID `13461805504` |
| ZIP descargado | `FolderTwin-win-x64-e4e87b8.zip`, 76.941.003 bytes |
| SHA-256 del ZIP | `2f795b694fa700c9993cb8cf8817017b39bed83d6212cc199eec71071bea7760` |
| SHA-256 de SHA256SUMS.txt | `265e119541eef24f735371ffa87190795427edddcd6f567c38be4af9baf31e02` |
| SHA-256 de fuentes archivadas | `40d49fc91a6d1022ee0bbbae35e0cc12a30a975bfb1acac453f895943e4e3ecc` |
| Contenido comprobado | 499 archivos, conjunto exacto y cada hash; manifiesto separado |
| Reproducción | Dos compilaciones desde árboles limpios en CI; manifiestos idénticos |

Se descargó el ZIP original de Actions, se cotejaron tamaño y SHA-256 con sus metadatos y se verificó su contenido mediante `scripts/Test-Portable.ps1`. El paquete conserva runtime, licencias, avisos de terceros, fuentes y guías. Su README y NOTAS ya no presentan un candidato anterior como actual. COMPILACION.json identifica fuentes y runtime sin anticipar aceptación. Los enlaces locales se comprobaron; las referencias a informes de desarrollo apuntan a su revisión de fuentes y se identifican como antecedentes, no como este informe posterior.

El borrador **v1.0.0-rc.1**, ID `435666824`, se gestiona en [Releases](https://github.com/mikepchelper-spec/FolderTwin/releases). Adjunta este ZIP, SHA256SUMS.txt, SHA256SUMS-zip.txt y este informe. Se mantiene como borrador y prerelease. No se ha publicado una versión final. Conservar los archivos durante el piloto: los artefactos de Actions caducan a los 30 días.

## Aceptación desde el paquete

La ejecución de Windows, en runner desechable `windows-2025`, aprobó los arneses de copia, raíces, registro, acumulativo, versiones, espejo, inventarios incompletos, catálogos, retención, configuración y trabajadores. Desde su paquete ejecutó el arranque público sin argumentos, compatibilidad WPF, el recorrido de producto de los tres modos, verificación y restauración, doce casos de planes incompatibles, cierre y reapertura con trabajadores activos y gestión de aplicación.

El ensayo nativo instaló el paquete, ejecutó horarios de los tres modos y conservó la incidencia del destino ausente y el reintento real de cinco minutos. Comprobó exclusión, cambio de hora y desactivación durante la espera. Preparó una segunda instalación verificada, redirigió solo horarios propios habilitados, ejecutó el trabajador actualizado y retiró tareas. Eliminó dos runtimes reales revisados y recuperó los tres modos después; una referencia ajena oculta impidió su retirada y se conservó intacta. Las pruebas WPF añadieron reanudación de una retirada parcial y reinstalación sin activar trabajos retirados.

El ZIP contiene el mismo motor `CopyProbe.dll` probado en el [VHDX de 76b7eed](https://github.com/mikepchelper-spec/FolderTwin/actions/runs/42105791375): SHA-256 `2bdfc41d7685bfd1dab5f268c2b2b3ae49d9c1aae848160fe29c1461ca5c4893`. Se comprobó esa igualdad contra el paquete conservado. Por ello se conserva aquella evidencia de disco lleno real después de comenzar la copia, reanudación de la misma generación, ausencia, sustitución, retorno con otra letra y recuperación sin origen; no se presenta como un VHDX nuevo de e4e87b8 ni como USB físico. Los binarios, runtime y JSON de ejecución del paquete también coincidieron con el paquete local de `0ac3ca5`, cuya [aceptación](https://github.com/mikepchelper-spec/FolderTwin/actions/runs/42123005640) pasó. Solo cambiaron documentos, fuentes e identidad de compilación entre esos candidatos.

La revisión de privacidad comprobó archivos versionados y fuentes: sin rutas personales encontradas por los patrones revisados, credenciales ni artefactos de compilación o configuración de usuario incorporados. Los fixtures y logs locales no se adjuntan al lanzamiento. El repositorio conserva decisiones y enlaces a la evidencia; las compilaciones permanecen fuera de él.

## Revisión de interfaz y límites de la evidencia

| Superficie | Evidencia de software |
|---|---|
| Asistente y trabajos | Estado vacío, tres modos, pasos y foco, hora inválida, revisión antes de guardar, edición, deshabilitación, retirada y reapertura. |
| Recuperación | Sin puntos, punto vacío, fechas, páginas, carpetas, búsqueda Unicode y Enter, comparación, eliminados, índice dañado, revisión de salida nueva y recuperación sin origen. |
| Historial y trabajadores | Incidencias por ruta, fechas separadas, pausa, continuación, cancelación, cierre de interfaz y reanudación. El historial sin cambios conserva selección, contenedor y foco al consultar estado. |
| Configuración y retención | Importación deshabilitada, identidades, activación expresa; revisión invalidada por cambio de política, cuarentena, deshacer, gracia, purga sintética e incidencias que bloquean limpieza. |
| Aplicación y bandeja | Revisión y aceptaciones, instalación, actualización y retirada; estado de tres modos, antigüedad, incidencias entre trabajos, avisos sin duplicación y exclusión de retirados. |
| Teclado y presentación | Regresión adicional WPF en claro y oscuro a 600 × 500; retorno entre paneles, foco y Tab en ajustes/retención. Distribución y acciones de recuperación a 1040 × 740, 760 × 620 y 600 × 500; nombres mediante peers. |

En el escritorio local, el paquete de revisión `586aa58` permitió observar historial y entrada con Tab a ajustes. Se detectó reconstrucción del historial cada segundo aun sin cambios. La regresión WPF falló antes del arreglo y pasó después, localmente y desde los paquetes de CI de 0ac3ca5 y e4e87b8. El refresco solo reemplaza las filas cuando cambia su contenido.

La inspección adicional de escritorio del paquete corregido no se concluyó: el controlador perdió una captura fiable de la ventana objetivo. Se detuvo la interacción y se cerró únicamente el proceso identificado del fixture, conservando su carpeta desechable. No se acredita ese intento como aprobación visual. Las comprobaciones automatizadas anteriores conservan su resultado independiente. La [documentación de interfaz](experiments/CopyLab/INTERFAZ.md) distingue estos alcances.

El recorrido normal sin comandos de los tres modos y las consultas consecutivas de teclado en claro/oscuro corresponden a la observación previa de **76b7eed**, no a una nueva observación completa de e4e87b8. Su [registro preservado](https://github.com/mikepchelper-spec/FolderTwin/blob/acb28b06d55b8db5c50ecacce351e8a00f9f9c82/ENTREGA.md) conserva hashes, perfil vacío y límites. Peers, pruebas programáticas y tamaños de ventana no equivalen a lector activo, DPI real ni aceptación completa del escritorio.

## Cierre de software y reanudación externa

El alcance de software de los tres modos NTFS, interfaz, retención manual y distribución queda cerrado con la [matriz V1](V1.md) y este paquete comprobado. Las guías se entregan dentro; este informe y el hash del ZIP, fuera. No se modifica ni reconstruye el ZIP para incluir su propia aceptación.

Siguen pendientes, por falta del entorno ya indicada por el usuario:

- EXT-01: piloto real de 7–14 días; no iniciado, sin fecha ficticia.
- EXT-02: USB NTFS físico, desconexión, sustitución, reconexión y geometría.
- EXT-03A/B/C: cierre e inicio de sesión, reinicio, reinicio abrupto de VM y suspensión/reanudación con horarios perdidos.
- EXT-04A/B: otra cuenta o equipo y proveedor de nube real.
- EXT-05A/B/C: aceptación completa de teclado/avisos en escritorio, lector activo y escalado real, incluidos cambios de DPI donde haya monitores disponibles.

Estas validaciones no impiden cerrar este goal de software y no se dan por aprobadas. Cuando exista entorno, conservar el ZIP y hashes de esta tabla y seguir [PILOTO.md](PILOTO.md), registrando inicio, entorno, caso, resultado y verificación de cada salida. Un fallo exige corregir y repetir las pruebas afectadas. Un cambio de binarios o contenido distribuido requiere identificar otro paquete y justificar su aceptación. No reiniciar ni cerrar sesión del equipo de trabajo. Mantener excluidos respaldos personales, FreeFileSync y corte eléctrico físico. VSS, vigilancia continua, NAS, nube propia y sincronización bidireccional siguen fuera de v1.
