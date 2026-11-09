# FolderTwin · candidato de software

Respaldos automáticos y versiones recuperables. Para Windows 11 x64 y destinos locales NTFS. El asistente permite vigilar el origen, elegir una hora diaria o respaldar a demanda. El monitor funciona con la ventana cerrada mientras tu sesión está iniciada y Windows permanece despierto.

Extrae **todo** este paquete en una carpeta nueva y abre **FolderTwin.exe**. Incluye el runtime: no necesitas instalar .NET. Empieza con [la guía de uso](GUIA.md). Usa únicamente datos sintéticos y carpetas desechables mientras continúan las validaciones externas.

- [Instalación, actualización y retirada](INSTALACION.md).
- [Funciones, cambios y límites](NOTAS.md).
- [Recuperación de formatos anteriores](COMPATIBILIDAD.md).
- [Procedimiento del piloto](PILOTO.md).

**COMPILACION.json** identifica el commit de este paquete, su runtime y el hash del archivo de fuentes incluido. **SHA256SUMS.txt** enumera el contenido distribuido. Ninguno acredita una firma del editor ni durabilidad física.

El informe de aceptación se produce después de compilar y probar el paquete y se entrega por separado, con el hash del ZIP cuando esté disponible. Comprueba que el informe identifica el mismo commit y manifiesto. Los informes dentro del archivo de fuentes documentan el desarrollo.

El cierre de software es independiente de las validaciones externas: piloto de 7–14 días, USB físico, ciclos de Windows, otra cuenta o equipo, nube real, lector de pantalla y escalado real donde falte entorno. Permanecen pendientes hasta contar con evidencia. Esta distribución no se declara v1 definitiva.

Licencia GPL-3.0-or-later en **LICENSE**. El código correspondiente se entrega en **FolderTwin-source.tar**; las licencias y avisos del runtime acompañan los binarios.
