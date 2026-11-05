# FolderTwin · candidato de software

Copias locales, versiones recuperables. Para Windows 11 x64 y destinos locales NTFS.

Extrae **todo** este paquete en una carpeta nueva y abre **FolderTwin.exe**. Incluye el runtime: no necesitas instalar .NET. Empieza con [la guía de uso](GUIA.md). Usa únicamente datos sintéticos y carpetas desechables mientras continúan las validaciones externas.

- [Instalación, actualización y retirada](INSTALACION.md).
- [Funciones, cambios y límites](NOTAS.md).
- [Recuperación de formatos anteriores](COMPATIBILIDAD.md).
- [Procedimiento del piloto](PILOTO.md).

**COMPILACION.json** identifica el commit de este paquete, su runtime y el hash del archivo de fuentes incluido. **SHA256SUMS.txt** enumera el contenido distribuido. Ninguno acredita una firma del editor ni durabilidad física.

El informe de aceptación se produce **después** de compilar y probar el paquete. Se distribuye por separado, junto al ZIP y su hash, en el [borrador de lanzamiento](https://github.com/mikepchelper-spec/FolderTwin/releases). Comprueba que ese informe identifica el mismo commit y manifiesto. Este ZIP no contiene un informe que se declare aprobado a sí mismo. Los informes del archivo de fuentes son antecedentes de desarrollo, no la aceptación de este paquete.

El cierre de software es independiente de las validaciones externas: piloto de 7–14 días, USB físico, ciclos de Windows, otra cuenta o equipo, nube real, lector de pantalla y escalado real donde falte entorno. Permanecen pendientes hasta contar con evidencia. Esta distribución no se declara v1 definitiva.

Licencia GPL-3.0-or-later en **LICENSE**. El código correspondiente se entrega en **FolderTwin-source.tar**; las licencias y avisos del runtime acompañan los binarios.
