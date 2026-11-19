# CubeKeeper

Panel web para gestionar un servidor **Bedrock Dedicated Server** (BDS) en casa, pensado para TrueNAS SCALE.
CubeKeeper ejecuta el `bedrock_server` oficial como proceso hijo dentro de su propio contenedor:
lo descarga, lo arranca y lo para, lee su consola y le envía comandos. No necesita el socket de Docker.

> CubeKeeper no está afiliado a Mojang ni a Microsoft. Al instalar el servidor aceptas la
> [EULA de Minecraft](https://www.minecraft.net/eula).

## Estado

**Fase 1A (en curso):** despliegue, login, instalación de BDS (con versión fijable), arrancar, parar y reiniciar,
reinicio automático con límite, consola en vivo, `/health` por TCP y revisión de `server.properties`.

Plan completo y decisiones: [docs/ARQUITECTURA.md](docs/ARQUITECTURA.md) ·
Instalación en el NAS y pruebas: [docs/TRUENAS.md](docs/TRUENAS.md).

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
| `CUBEKEEPER_ADMIN_RESET` | `false` | Con `true`, pone `CUBEKEEPER_ADMIN_PASSWORD` al administrador y lo desbloquea (para recuperar el acceso) |
| `CUBEKEEPER_ACCEPT_EULA` | `false` | Aceptación de la EULA; sin ella no se descarga BDS |
| `CUBEKEEPER_CHANNEL` | `Stable` | `Stable` o `Preview` |
| `CUBEKEEPER_BDS_VERSION` | — | Versión fijada (p. ej. `1.26.65.1`); vacía = la última |
| `CUBEKEEPER_DIRECT_DOWNLOAD_URL` | — | Enlace directo a un zip concreto (último recurso) |
| `CUBEKEEPER_AUTOSTART` | `true` | Arrancar BDS al iniciar el contenedor si estaba encendido |
| `CUBEKEEPER_PUBLIC_HOST` | host de la petición | Dirección que se muestra para conectarse |
| `CUBEKEEPER_ALLOWED_NETWORKS` | redes privadas | CIDR desde los que se permite el panel |
| `CUBEKEEPER_TRUSTED_PROXIES` | `172.16.0.0/12` | Proxies cuyo `X-Forwarded-For` se acepta |

Otros ajustes avanzados se pueden dar con la notación de .NET, por ejemplo
`CubeKeeper__Bds__StopTimeoutSeconds=45` o `CubeKeeper__Bds__MaxRestarts=5`.

## Estructura de `/data`

```
/data
├── cubekeeper.db      usuarios del panel (SQLite)
├── state.json         estado deseado y versión instalada
├── keys/              claves de las cookies
├── server/            directorio de trabajo de bedrock_server (worlds/, server.properties…)
├── versions/<v>/      versiones de BDS descargadas (se guardan 2)
├── downloads/         temporales
└── backups/           copias del mundo (fase 1C)
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
