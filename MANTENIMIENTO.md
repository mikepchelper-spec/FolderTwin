# Mantenimiento y entregas experimentales

Este procedimiento no inicia trabajos sobre datos personales ni autoriza pruebas de energía, reinicio o sesión del equipo habitual. La publicación es experimental: NTFS local, tres modos y retención manual. Los pendientes de [V1.md](V1.md) y [PILOTO.md](PILOTO.md) no se aprueban por publicar.

## Recibir y clasificar

Revisa primero [SUPPORT.md](SUPPORT.md) y [SECURITY.md](SECURITY.md). Una incidencia pública solo necesita versión, entorno general, modo, pasos sintéticos y resultado. Si contiene información privada, evita citarla o descargarla para reproducir: pide retirarla, limita la exposición mediante las herramientas de GitHub y no copies esos datos a otro lugar. Una vulnerabilidad pasa al reporte privado, sin duplicar detalles en una incidencia pública.

| Prioridad | Ejemplos | Acción |
|---|---|---|
| P0 | Escritura fuera del destino, retirada sin revisión, hash inesperado, pérdida de contenido o un parcial anunciado como completo | Contener la distribución del ZIP afectado, conservar evidencia y reproducir antes de continuar su promoción. |
| P1 | Recuperación bloqueada, reanudación rota, conflictos de identidad o concurrencia | Mantener los datos y diarios, preparar reproducción y corrección; evaluar si requiere retirar el ZIP. |
| P2 | Interfaz, mensajes o documentación sin riesgo de contenido | Corregir con comprobación acotada y describir su alcance. |
| Entorno | USB, ciclos de Windows, otra cuenta/equipo, nube o accesibilidad sin entorno disponible | Registrar pendiente con procedimiento y evidencia necesaria; no simular aprobación. |

Usa las etiquetas existentes `bug`, `question`, `documentation` o `accessibility` según corresponda. P0/P1/P2 pueden indicarse en el texto de clasificación; no hace falta crear una infraestructura de etiquetas o automatismos. No cierres una incidencia como resuelta por tener una hipótesis. Registra caso sintético, esperado, observado y límites.

## Reproducir y corregir

1. Fija commit, paquete y hash del informe. Crea un fixture nuevo con archivos inventados y guarda el contenido esperado.
2. Ejecuta la secuencia mínima, sin borrar diarios ni forzar aceptaciones. Registra si el origen o destino falta, qué generación quedó pendiente y si el punto previo verifica y recupera.
3. Añade una regresión que falle antes de corregir. Reutiliza el motor y corrige la causa en su punto compartido. Comprueba protección del origen, identidades, bloqueos y puntos anteriores.
4. Ejecuta las pruebas afectadas. Amplía la aceptación solo cuando cambie el motor, formato, flujo de producto, instalación o contenido distribuido. Un cambio de documentación de comunidad no obliga a reconstruir el candidato congelado.
5. Vincula incidencia, corrección y evidencia. Los formatos anteriores conservan su lector o un procedimiento explícito; no interpretes un diario viejo como autorización nueva. Para información privada, usa el reporte privado en vez de los enlaces públicos.

## Contener una entrega grave

Esta es una instrucción para un incidente futuro, **no una retirada del candidato actual**.

Conserva primero localmente el ZIP, manifiesto, informe, identificadores y hashes. Edita la release afectada con el título **RETIRADA — no usar** y un aviso que diga qué se conoce, qué permanece incierto y que deben detenerse nuevas ejecuciones del modo afectado. Quita del README sus enlaces de descarga y conserva el registro de la incidencia. Nunca recomiendes purgar o borrar diarios para solucionar el fallo.

Retira el adjunto ZIP oficial afectado mediante la interfaz de Releases o, después de revisar etiqueta y nombre exactos:

```powershell
gh release delete-asset <etiqueta-afectada> <nombre-exacto-del-zip> --yes
```

Comprueba sin autenticación que la URL original ya no entrega el ZIP. Mantén el aviso, hashes e informe como evidencia histórica; no muevas la etiqueta ni sustituyas silenciosamente el archivo bajo el mismo nombre. El código fuente, copias ya descargadas y cachés pueden seguir existiendo: retirar un adjunto no permite revocarlas ni garantiza su desaparición. Publica una corrección con etiqueta y paquete nuevos y con pruebas antes de volver a ofrecer descargas. Si se trata de una vulnerabilidad, coordina el detalle técnico por el canal privado.

## Preparar la siguiente entrega

1. Elige un commit completo, limpio y revisado. No uses solo el último `main` por su fecha. Revisa cambios, privacidad, fuentes y licencias; excluye configuraciones, respaldos, logs privados y salidas de compilación. Conserva el runtime y SDK fijados o justifica y prueba su actualización.
2. Usa [Publish-Portable.ps1](scripts/Publish-Portable.ps1) y [Test-Portable.ps1](scripts/Test-Portable.ps1). La [CI Windows](.github/workflows/copy-probe.yml) archiva ese commit, extrae dos árboles independientes, compila ambos, compara los manifiestos y ejecuta la aceptación desde el paquete. Exige resultado aprobado del commit elegido; no atribuyas una ejecución de otro commit al candidato.
3. Revisa los cambios para decidir si la evidencia anterior de VHDX sigue siendo aplicable. Solo reutilízala cuando el motor y las condiciones relevantes coincidan y documenta esa comprobación. Mantén las pruebas físicas separadas.
4. Descarga el artefacto original de Actions. Ejecuta la comprobación del paquete y calcula el hash del ZIP descargado, sin volver a comprimirlo:

```powershell
./scripts/Test-Portable.ps1 -PackageDirectory <carpeta-extraida-nueva> -SourceRevision <commit-completo>
Get-FileHash -LiteralPath <zip-original-descargado> -Algorithm SHA256
```

5. Conserva ZIP, COMPILACION.json, SHA256SUMS.txt y fuentes. Las fuentes incluidas deben corresponder al commit, su hash y licencia. El manifiesto compara archivos; el hash del ZIP identifica el contenedor. Ninguno acredita una firma del editor ni durabilidad física.
6. Produce el informe **después** de las pruebas y fuera del ZIP, con commit, CI, hashes, inventario, entorno y límites. Las guías interiores describen uso y fuentes; no contienen su propia aprobación. No reconstruyas para introducir el hash del ZIP o resultados posteriores.
7. Prepara un borrador con etiqueta nueva que fije el commit aceptado, estado prerelease y adjuntos revisados. Verifica que la etiqueta no exista con otro destino. Antes de publicar, comprueba título, notas, límites sintéticos, secretos y cada digest. Conserva las versiones anteriores; una corrección no mueve etiquetas publicadas.
8. Publica solo bajo autorización vigente. Mantén `prerelease=true` y no marques la entrega como estable o Latest mientras sigan abiertos sus requisitos. Comprueba después, sin credenciales, la página, API, etiqueta resuelta y descarga de cada adjunto; compara tamaño y hash y comprueba el inventario del ZIP en una carpeta desechable. Los enlaces del README deben abrir esa entrega.
9. Registra la publicación fuera del paquete, conserva un punto de reanudación y actualiza README y PLAN con el siguiente trabajo real. No uses el tiempo de desarrollo como piloto. Los artefactos de Actions caducan; las descargas de Releases son el punto público de la entrega elegida.

Fuentes de las operaciones de GitHub: [edición/publicación de releases](https://cli.github.com/manual/gh_release_edit) y [borrado de un adjunto](https://cli.github.com/manual/gh_release_delete-asset). Este procedimiento no instala un publicador automático ni envía mensajes a otros canales.
