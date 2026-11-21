# CubeKeeper

Panel web para gestionar un servidor **Bedrock Dedicated Server** (BDS) en casa, pensado para TrueNAS SCALE.
CubeKeeper ejecuta el `bedrock_server` oficial como proceso hijo dentro de su propio contenedor:
lo descarga, lo arranca y lo para, lee su consola y le envía comandos. No necesita el socket de Docker.

> CubeKeeper no está afiliado a Mojang ni a Microsoft. Al instalar el servidor aceptas la
> [EULA de Minecraft](https://www.minecraft.net/eula).

## Estado

- **Fase 1A ✅** Despliegue, login, instalación de BDS (con versión fijable), arrancar, parar y reiniciar,
  reinicio automático con límite, consola en vivo, `/health` por TCP y revisión de `server.properties`.
- **Fase 1B ✅** Jugadores conectados (log + `list`), historial de sesiones y tiempo jugado en SQLite, y editor de
  `server.properties` con validación que conserva los comentarios.
- **Fase 1C ✅** Copias del mundo consistentes en caliente, bajo demanda y programadas, con retención y segundo destino;
  restaurar y descargar como `.mcworld`; actualización con copia previa y vuelta atrás automática; comprobación diaria
  con avisos por el chat.
- **Fase 2A ✅** Acceso: modo abierto o «solo aprobados» con lista de permitidos por XUID, modo inscripción con
  solicitudes, permisos (visitante, miembro, operador), bloquear; controles rápidos (hora, clima, mensaje, expulsar) y
  reglas del juego.
- **Fase 2B ✅** Mundos (crear, cambiar, importar y exportar `.mcworld`, borrar con copia previa) y complementos
  (`.mcpack`, `.mcaddon`: instalar, activar, desactivar y quitar en el mundo activo).
- **Fase 3A ✅** Familia: horarios y tiempo máximo al día por jugador, con avisos y expulsión, tiempo extra para hoy e
  historial de 7 días; auditoría de las acciones del panel y de CubeKeeper.
- **Fase 3B ✅** Usuarios del panel con roles (administrador y operador), Mi cuenta (contraseña y verificación en dos
  pasos con TOTP y códigos de recuperación) y login en dos pasos.
- **Fase 3C ✅** Avisos al móvil con ntfy: solicitudes de acceso, caídas, copias fallidas, actualizaciones y versiones nuevas
  de BDS (y, si se activan, entradas y salidas).
- **Fase 3D ✅** Mapa del mundo con uNmINeD o bedrock-viz (se instalan desde el panel), cada noche y bajo demanda.
- **Mejoras ✅** Claves cifradas con `CUBEKEEPER_KEY_SECRET`, cabeceras de seguridad, avisos de fallos de
  mantenimiento y pruebas en el navegador en cada cambio.
- **Más ✅** Uso de CPU y memoria con gráficas y aviso, reinicio programado, panel instalable como app en el móvil y
  estadísticas de tiempo de juego.
- **Mantenimiento ✅** Instalar la versión nueva de BDS desde el Panel, espacio en disco con aviso, comprobación
  semanal de copias, diagnóstico descargable y menú plegable en el móvil.
- **Realms ✅** Conectar la cuenta de Microsoft con un código (microsoft.com/link) y descargar los mundos de tus
  Realms para importarlos.
- El MCP queda aparcado por ahora.

Plan completo y decisiones: [docs/ARQUITECTURA.md](docs/ARQUITECTURA.md) ·
Instalación en el NAS: [docs/TRUENAS.md](docs/TRUENAS.md) · Uso diario: [docs/USO.md](docs/USO.md) ·
Si algo falla: [docs/PROBLEMAS.md](docs/PROBLEMAS.md) · Pruebas en el NAS: [docs/PRUEBAS.md](docs/PRUEBAS.md).

## Configuración

Todo se configura con variables de entorno. Para los secretos se admite el sufijo `_FILE`
(ruta a un archivo con el valor).

| Variable | Por defecto | Descripción |
|---|---|---|
| `CUBEKEEPER_PORT` | `8765` | Puerto del panel web |
| `CUBEKEEPER_DATA` | `/data` | Carpeta de datos (Host Path del NAS) |
| `TZ` | `Europe/Madrid` | Zona horaria |
| `CUBEKEEPER_ADMIN_USER` | `admin` | Usuario inicial |
| `CUBEKEEPER_ADMIN_PASSWORD` | — | Contraseña inicial (mínimo 10 caracteres); solo se usa si no hay usuarios |
| `CUBEKEEPER_KEY_SECRET` | — | Secreto (16+ caracteres; mejor `openssl rand -base64 32`) con el que se cifran las claves de `/data/keys`. Va fuera del dataset; si se pierde, ver [PROBLEMAS.md](docs/PROBLEMAS.md) |
| `CUBEKEEPER_ADMIN_RESET` | `false` | Con `true`, pone `CUBEKEEPER_ADMIN_PASSWORD` al administrador, lo desbloquea y le quita la verificación en dos pasos (para recuperar el acceso) |
| `CUBEKEEPER_ACCEPT_EULA` | `false` | Aceptación de la EULA; sin ella no se descarga BDS |
| `CUBEKEEPER_CHANNEL` | `Stable` | `Stable` o `Preview` |
| `CUBEKEEPER_BDS_VERSION` | — | Versión fijada inicial (p. ej. `1.26.65.1`); vacía = la última. Desde el Panel se puede instalar una más nueva, que queda fijada |
| `CUBEKEEPER_DIRECT_DOWNLOAD_URL` | — | Enlace directo a un zip concreto (último recurso) |
| `CUBEKEEPER_AUTOSTART` | `true` | Arrancar BDS al iniciar el contenedor si estaba encendido |
| `CUBEKEEPER_PUBLIC_HOST` | host de la petición | Dirección que se muestra para conectarse |
| `CUBEKEEPER_BACKUP_INTERVAL_MINUTES` | `60` | Copias programadas cada N minutos (`0` = desactivadas) |
| `CUBEKEEPER_BACKUP_SKIP_IF_IDLE` | `true` | Saltarse la copia programada si nadie ha jugado desde la anterior |
| `CUBEKEEPER_BACKUP_KEEP_HOURLY` / `_DAILY` / `_WEEKLY` | `24` / `14` / `8` | Retención de las copias programadas |
| `CUBEKEEPER_BACKUP_KEEP_MANUAL` | `20` | Copias manuales que se conservan |
| `CUBEKEEPER_BACKUP_SECONDARY` | — | Segundo destino de las copias (p. ej. `/backups`) |
| `CUBEKEEPER_UPDATE_AUTO` | `true` | Comprobar y actualizar cada día (sin efecto con versión fijada) |
| `CUBEKEEPER_UPDATE_TIME` | `05:00` | Hora local de la comprobación |
| `CUBEKEEPER_UPDATE_WARN_MINUTES` | `5,1` | Avisos por el chat antes de reiniciar |
| `CUBEKEEPER_UPDATE_SKIP_IF_PLAYERS` | `false` | Si hay alguien jugando, dejarlo para el día siguiente |
| `CUBEKEEPER_NTFY_URL` / `_TOPIC` / `_TOKEN` | — | Avisos con ntfy. **Mejor desde la página Avisos**; estas variables solo se usan si allí no hay nada guardado |
| `CUBEKEEPER_PANEL_URL` | — | Dirección del panel para los avisos (también desde la página Avisos) |
| `CUBEKEEPER_ALLOWED_NETWORKS` | redes privadas | CIDR desde los que se permite el panel |
| `CUBEKEEPER_TRUSTED_PROXIES` | `172.16.0.0/12` | Proxies cuyo `X-Forwarded-For` se acepta |
| `CUBEKEEPER_DISK_WARN_PERCENT` / `_GB` | `10` / `5` | Avisar si queda menos de ese porcentaje o de esos GB libres |
| `CUBEKEEPER_REALMS_CLIENT_VERSION` | versión de BDS | Versión del juego que se manda a Realms (solo si Realms la rechaza) |

Otros ajustes avanzados se pueden dar con la notación de .NET, por ejemplo
`CubeKeeper__Bds__StopTimeoutSeconds=45` o `CubeKeeper__Bds__MaxRestarts=5`.

## Estructura de `/data`

```
/data
├── cubekeeper.db      usuarios del panel, jugadores y sesiones (SQLite)
├── state.json         estado deseado, versión instalada, modo de acceso, ajustes y sesión de Realms (cifrada)
├── keys/              claves de cookies, token de ntfy y sesión de Realms (cifradas con CUBEKEEPER_KEY_SECRET)
├── server/            directorio de trabajo de bedrock_server (worlds/, server.properties…)
├── versions/<v>/      versiones de BDS descargadas (se guardan 2)
├── downloads/         temporales
├── backups/           copias del mundo (.mcworld; el nombre lleva mundo, fecha UTC y tipo)
├── tools/<motor>/     motor de mapas instalado desde el panel
└── map/<motor>/       último mapa generado
```

## Desarrollo

Requisitos: SDK de .NET 10.

```bash
dotnet test
```

Para usar el panel en local sin el servidor real, se lanza el `FakeBedrockServer` de `tests/`, que imita la salida
de BDS y responde a `stop`, `list`, `say`, `join <nombre> <xuid>`, `leave …`, `crash` y `hang`.

En Windows (PowerShell):

```powershell
dotnet build tests/FakeBedrockServer
$env:CubeKeeper__Bds__LaunchCommand = "dotnet"
$env:CubeKeeper__Bds__LaunchArguments = "$PWD\tests\FakeBedrockServer\bin\Debug\net10.0\FakeBedrockServer.dll"
dotnet run --project src/CubeKeeper.Web
```

En Linux o macOS:

```bash
dotnet build tests/FakeBedrockServer
```

```bash
CubeKeeper__Bds__LaunchCommand=dotnet CubeKeeper__Bds__LaunchArguments="$PWD/tests/FakeBedrockServer/bin/Debug/net10.0/FakeBedrockServer.dll" dotnet run --project src/CubeKeeper.Web
```

Abre `http://localhost:5229` y entra con `admin` / `desarrollo123` (solo en desarrollo; los datos van a `.devdata/`).

### Pruebas en el navegador

`e2e/` tiene pruebas con Playwright que arrancan el panel publicado (como en el contenedor) con el servidor falso y
un Microsoft simulado para Realms. Se ejecutan en GitHub Actions en cada cambio; la imagen solo se publica si pasan.
En local (necesita Node 22):

```bash
dotnet publish src/CubeKeeper.Web -c Release -o artifacts/web
```

```bash
dotnet build tests/FakeBedrockServer -c Release
```

```bash
cd e2e && npm ci && npx playwright install chromium && npx playwright test
```
