# Contribuir a FolderTwin

FolderTwin es una vista previa experimental. Usa exclusivamente datos sintéticos y destinos desechables; no pruebes cambios con respaldos personales, FreeFileSync ni cortes eléctricos. Windows 11 x64, NTFS local y retención manual delimitan el alcance actual. [Guía](GUIA.md) · [Soporte](SUPPORT.md) · [Seguridad](SECURITY.md).

## Antes de cambiar código

Lee [AGENTS.md](AGENTS.md), [V1.md](V1.md), el protocolo del motor afectado y la incidencia correspondiente. Busca primero una incidencia existente. Para un error, aporta una reproducción mínima sintética; para un cambio de alcance, explica la necesidad antes de desarrollar una función extensa. No se exige abrir una incidencia para una corrección pequeña de documentación.

Conserva las invariantes: origen de solo lectura, raíces e identidades comprobadas, exclusión entre operaciones, publicación verificada y recuperación de estados pendientes. Una enumeración incompleta nunca autoriza retiradas. No borres diarios o parciales para hacer pasar una prueba. SQLite es reconstruible y no sustituye los registros del destino.

## Desarrollar y comprobar

Usa el SDK de [global.json](global.json) y PowerShell 7. Mantén las salidas fuera del repositorio cuando uses el publicador. Los arneses crean sus propios fixtures; no les pases datos personales.

```powershell
dotnet run --project experiments/CopyProbe -c Release
dotnet run --project experiments/CopyLab -c Release -- --product-surface-smoke
```

Elige después la prueba del cambio: por ejemplo `--accumulative`, `--retention`, `--product-config` o `--product-runs` en CopyProbe. Para integración WPF usa `--product-smoke` en CopyLab. Consulta los protocolos y el [workflow](.github/workflows/copy-probe.yml) para los argumentos exactos. Las pruebas nativas de horarios y VHDX se reservan a runners desechables. No repitas toda la aceptación ante cambios exclusivamente editoriales ni declares pruebas que no ejecutaste.

Una corrección de comportamiento debe incluir una reproducción o regresión que falle sin ella y pase con ella. Para documentación, valida enlaces, ejemplos y coherencia con el paquete elegido. Distingue automatización, observación de escritorio y validación externa: ninguna acredita por sí sola durabilidad física.

## Enviar una propuesta

Haz una rama y una solicitud de cambios pequeña. Describe problema, comportamiento resultante, pruebas ejecutadas y límites. Conserva los cambios ajenos y revisa el diff: sin rutas personales, credenciales, respaldos, configuraciones exportadas, logs completos ni binarios generados. Usa nombres relativos sintéticos como `Documentos/ejemplo.txt`.

El proyecto se distribuye bajo GNU GPL versión 3 o posterior, como declara el README del paquete; [LICENSE](LICENSE) contiene el texto de la licencia. Las contribuciones se reciben bajo esa misma licencia. No añadas dependencias o recursos cuya procedencia o licencia no esté documentada. No se exige un acuerdo de cesión separado.

Las propuestas y consultas no tienen plazo de revisión garantizado. Para una vulnerabilidad usa el canal privado de SECURITY, no una incidencia ni una solicitud pública con detalles explotables. [Procedimiento de mantenimiento y entregas](MANTENIMIENTO.md).
