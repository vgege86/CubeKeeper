# CubeKeeper: arquitectura y plan

## Decisión principal

CubeKeeper **sustituye** al contenedor `itzg/minecraft-bedrock-server`. Ejecuta el `bedrock_server` oficial de
Mojang como **proceso hijo**, con stdin y stdout redirigidos, dentro de su propio contenedor. No usa el socket
de Docker.

Hace falta así porque casi todo lo que hace el panel pasa por la consola del servidor: `allowlist reload`, `kick`,
`say`, `gamerule`, `list`, `save hold`/`save query` para copias consistentes, y detectar quién se conecta. Sin el
socket de Docker, otro contenedor no puede llegar a esa consola.

## Vista general

```
┌──────────── Contenedor cubekeeper (Host Network, UID 568, solo escribe en /data y /tmp) ────────────┐
│ ASP.NET Core 10 (Kestrel :8765)                                                                     │
│  ├─ Blazor Web App: login estático (SSR) + páginas interactivas (Interactive Server / SignalR)      │
│  ├─ /health: panel vivo + TCP a server-port si el servidor debe estar encendido                     │
│  ├─ Identity (cookie, roles; TOTP más adelante) · filtro de IP · cabeceras de NPM                   │
│  ├─ ServerSupervisor ──► bedrock_server (proceso hijo)                                              │
│  │      stdin ◄── comandos           stdout ──► ConsoleLog (búfer) + BdsLogParser (eventos)         │
│  ├─ VersionResolver (API de minecraft-services → índice de la comunidad) + ServerInstaller          │
│  └─ EF Core + SQLite (/data/cubekeeper.db)                                                          │
└─────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

### Proyectos

| Proyecto | Contenido |
|---|---|
| `CubeKeeper.Core` | Dominio y lógica pura, sin E/S: `BdsLogParser`, `PlayerTracker`, `ServerPropertiesDocument`, `PropertyDefinitions`, `RecommendedSettings`, `BdsVersion`, `DownloadLinksParser`, `CrashLoopGuard`, interfaces |
| `CubeKeeper.Infrastructure` | Proceso (`ServerSupervisor`, `ServerProcess`, `ConsoleLog`), versiones e instalador, salud, EF Core, estado en `state.json` |
| `CubeKeeper.Web` | Blazor, Identity, `/health`, mapeo de variables `CUBEKEEPER_*`, filtro de IP |
| `tests/CubeKeeper.Core.Tests` | Parser del log, `server.properties`, versiones, ajustes recomendados |
| `tests/CubeKeeper.Infrastructure.Tests` | Supervisor, instalador y salud de extremo a extremo con `FakeBedrockServer` |
| `tests/FakeBedrockServer` | Imitación de BDS para pruebas y para desarrollar sin el binario real |

### Decisiones

- **Supervisor con estado deseado.** El estado deseado (encendido o apagado) se guarda en `state.json`. Si BDS se cae
  y se quería encendido, se reinicia, como mucho 3 veces en 10 minutos; después queda en *Failed* y no se reintenta.
  Al parar el contenedor (SIGTERM) se envía `stop` y se espera, sin cambiar el estado deseado, así que vuelve a arrancar solo.
- **Las operaciones de ciclo de vida se serializan** (arrancar, parar, instalar). La tarea que vigila el proceso no
  toma ese cerrojo, para que una parada pueda esperar al proceso sin bloquearse.
- **La app es la dueña de la configuración.** Nada reescribe `server.properties`, `allowlist.json` ni `permissions.json`
  al arrancar. El editor conserva comentarios, orden y saltos de línea.
- **Configuración probada como referencia.** `RecommendedSettings` recoge las lecciones de itzg y el panel avisa si
  `server.properties` se desvía: `transport=nethernet`, `server-port=19132`, `server-udp-ports=19140`,
  `enable-lan-visibility=true`, `online-mode=true`, `allow-list=false` y `server-ip` vacío. Al instalar de cero se
  aplica junto con `level-name=Bedrock level`, `gamemode=survival` y `difficulty=normal`.
- **Descarga.** Como itzg: la API oficial `https://net.web.minecraft-services.net/api/v1.0/download/links`
  (`downloadType` = `serverBedrockLinux` / `serverBedrockPreviewLinux`) y, si falla, el índice de la comunidad
  `kittizz/bedrock-server-downloads`. Con `CUBEKEEPER_BDS_VERSION` se fija una versión: se cambia el número en el
  nombre del zip.
- **Actualización sin pisar el estado.** El zip se extrae en `versions/<v>/` y se copia a `server/` sin tocar `worlds/`,
  `server.properties`, `allowlist.json` ni `permissions.json`. Las claves nuevas de `server.properties` se añaden con
  sus comentarios. Se guardan 2 versiones para volver atrás.
- **Jugadores.** `PlayerTracker` (Core) deduce quién está conectado de `Player connected/disconnected` y de la respuesta
  de `list` (dos líneas: `There are N/M players online:` y los nombres). `PlayerTrackingService` lo alimenta con la
  consola a través de una cola con un único consumidor y guarda en SQLite un `Player` por XUID y una `PlayerSession`
  por conexión. Mientras haya alguien dentro, envía `list` cada 10 minutos por si se perdió una salida. Las sesiones
  que quedan abiertas tras un apagado brusco se cierran al arrancar como «interrumpidas».
- **Editor de `server.properties`.** `PropertyDefinitions` describe los ajustes conocidos (tipo, rango, opciones,
  ayuda en español). Los desconocidos se editan como texto libre y muestran el comentario original del archivo. Se
  valida todo antes de escribir (si algo falla no se guarda nada), se hace una copia `.bak` y solo cambian las líneas
  tocadas.
- **Copias.** `BackupService` genera `.mcworld` (zip con `level.dat` en la raíz). En marcha: `save hold` →
  `save query` (se repite hasta «Files are now ready to be copied» y la línea con `ruta:longitud`) → copiar cada archivo
  **truncado** a esa longitud → `save resume` siempre, en un `finally`. Parado: se comprime la carpeta. Los metadatos
  van en el nombre (`<mundo>__<AAAAMMDD-HHMMSS>Z__<tipo>.mcworld`), sin base de datos. Retención abuelo-padre-hijo
  (`RetentionPolicy`) para las programadas y contadores aparte para manuales y de seguridad, aplicada en los dos
  destinos. Restaurar: descomprimir y validar en una carpeta temporal, copia `prerestore` del mundo actual, parar,
  cambiar carpetas y arrancar. `ConsoleCommandRunner` serializa los comandos que esperan respuesta.
- **Actualizaciones.** `UpdateService`: avisos por el chat (5 y 1 min) si hay gente, copia `preupdate` (si falla, no
  se actualiza), instalación y espera a «En marcha». Si no arranca: parar, restaurar la copia y reinstalar la versión
  anterior desde `/data/versions`, en ese orden, para que la versión vieja no abra un mundo tocado por la nueva.
  `MaintenanceScheduler` lanza las copias cada N minutos (solo si alguien ha jugado) y la comprobación diaria, que no
  hace nada con una versión fijada.
- **Acceso (2A).** Dos capas. BDS: `allow-list` en server.properties y `allowlist.json` por XUID (`allowlist reload`,
  `permission reload`). CubeKeeper: `AccessService` recibe cada `Player connected`/`Player Spawned` y decide con
  `AccessPolicy` (bloqueado → expulsar; solo aprobados sin estar en la lista → expulsar; en inscripción → guardar la
  solicitud con su XUID y expulsar). Así se aplica sin reiniciar y sirve de «baneo», que Bedrock no tiene. El modo
  inscripción envía `allowlist off` durante N minutos (y de nuevo si el servidor se reinicia mientras dura) y al acabar
  `allowlist on`. Los permisos van a `permissions.json`; a un jugador conectado se le aplican al momento con `op`/`deop`.
  `allow-list` sale de la configuración recomendada: lo controla el modo de acceso.
- **Mundos y complementos (2B).** `WorldService`: cambiar de mundo y crear uno nuevo editan `level-name` (y semilla,
  modo y dificultad) tras una copia `prechange` del mundo activo, y reinician si estaba en marcha; importar valida el
  `.mcworld` en una carpeta temporal (`level.dat` en la raíz o en una subcarpeta, límite de tamaño, sin rutas fuera del
  destino) y exportar reutiliza la copia en caliente para el mundo activo. `AddonService`: descomprime `.mcpack` y
  `.mcaddon` (también anidados), lee cada `manifest.json` (`PackManifest`, con comentarios y nombres traducidos de
  `texts/*.lang`), copia cada paquete a `behavior_packs/` o `resource_packs/` del mundo y lo activa en
  `world_*_packs.json` (`WorldPacksDocument`). Un paquete con el mismo UUID se sustituye.
- **Familia (3A).** `FamilyPolicy` (Core) decide, para la hora local y el tiempo jugado hoy, si un jugador puede
  seguir y cuánto le queda (lo primero entre fin de la franja y fin del tiempo diario, con el extra de hoy).
  `FamilyService` lo aplica cada 20 s y al entrar alguien: `tell` privado a los 5 y a 1 minuto y, al llegar, `kick`
  con la explicación y cuándo puede volver. El tiempo de hoy sale de las sesiones de `PlayerStatsService` (la abierta
  cuenta hasta ahora), así que reiniciar el servidor no lo pone a cero.
- **Auditoría (3A).** `AuditLog` guarda quién (usuario del panel o «CubeKeeper»), qué acción, sobre qué y detalles.
  Las páginas heredan de `AuditedPage`, que toma el usuario de la sesión; el login guarda también la IP.
- **Usuarios y roles (3B).** Roles `Admin` y `Operator` de Identity. Las páginas de administración llevan
  `[Authorize(Roles = Admin)]`, las descargas `RequireAuthorization("Admin")`, y el menú y los botones delicados del panel
  se ocultan con `AuthorizeView` (y se comprueban otra vez en el servidor). `PanelUserService` no deja borrar ni degradar
  al último administrador. Cambiar rol o contraseña cambia el sello de seguridad: las cookies se revalidan cada minuto y los
  circuitos de Blazor también (`RevalidatingAuthStateProvider`). Verificación en dos pasos con el autenticador TOTP de
  Identity (QR con Net.Codecrete.QrCodeGenerator, el de Home365) y 10 códigos de recuperación; Mi cuenta usa formularios
  estáticos para poder renovar la cookie (`RefreshSignInAsync`) sin expulsar al usuario.
- **Avisos (3C).** La configuración de ntfy se guarda desde la página Avisos en `state.json` (el token cifrado con Data
  Protection, con las claves de `/data/keys`); las variables `CUBEKEEPER_NTFY_*` solo se usan si no hay nada guardado.
  `Notifier` publica en ntfy con la API JSON (título con tildes, prioridad, enlace `click` al panel y
  token opcional) y nunca lanza. Respeta los tipos activados (`NotificationKind`, en `state.json`) y no repite el mismo
  aviso (tipo + clave) en 10 minutos. Origen de cada aviso: `NotificationWatcher` (caídas y, si se activan, entradas y
  salidas), `AccessService` (solicitudes), `MaintenanceScheduler` (copias fallidas y, cada 6 h, versión nueva de BDS,
  una vez por versión y aunque esté fijada) y `UpdateService` (fallos siempre; éxitos de las programadas).
- **Mapa (3D).** Ningún motor va dentro de la imagen: la licencia de uNmINeD no permite redistribuirlo y bedrock-viz no
  se pudo verificar. El administrador lo instala desde la página Mapa (subiendo un .zip/.tar.gz o el ejecutable, o con un
  enlace https) en `/data/tools/<motor>`. `MapService` descomprime la copia más reciente del mundo (hace una si tiene más
  de 2 h) en una carpeta de trabajo, ejecuta el motor sin shell (`CommandTemplate` sustituye `{world}`, `{output}` y
  `{threads}` sin partir las rutas), con prioridad baja y límite de 2 h, y solo sustituye el mapa anterior si sale bien
  (hay al menos un .html). El comando de cada motor es editable por si una versión usa otros argumentos. Los archivos se
  sirven en `/mapa/ver/…` a cualquier usuario con sesión, sin salir de la carpeta. `MapScheduler` lo lanza cada noche.
- **Salud por TCP**, no por RakNet. Con NetherNet, BDS escucha en TCP en `server-port`. Se usa `server-ip` si está
  definida. Con el servidor parado a propósito, `/health` responde sano: el contenedor funciona.
- **Imagen** `mcr.microsoft.com/dotnet/aspnet:10.0` (Ubuntu 24.04, la base de BDS) con `curl` y `tzdata`. Sin la
  variante *chiseled*.
- **Seguridad.** Usuario 568, `read_only`, `cap_drop: ALL`, `no-new-privileges` y límites de CPU y memoria. Panel
  detrás de NPM (Access List) **y** filtro de IP propio, porque con Host Network el puerto también responde en la
  IP del NAS. Cookie `HttpOnly` y `SameSite=Strict`, bloqueo tras 5 intentos fallidos y contraseña inicial por
  variable de entorno (o `_FILE`).

## Valores acordados

| Tema | Valor |
|---|---|
| Puerto del panel | 8765, configurable (`CUBEKEEPER_PORT`) |
| Usuario del contenedor | 568 (`apps` de TrueNAS) |
| Datos | Host Path `/mnt/vms/cubekeeper` → `/data` (dataset propio; el de itzg queda intacto) |
| Versión de BDS | Fijada a 1.26.65.1, sin actualizaciones automáticas |
| Copias (fase 1C) | `/data/backups` + segundo destino en `DATOS`; 24 horarias, 14 diarias, 8 semanales (configurable) |
| Actualización programada (fase 1C) | 05:00, aviso a 5 y a 1 minuto, reinicio aunque haya gente (configurable; desactivada si la versión está fijada) |
| `/health` con el servidor parado a propósito | Sano |
| Desarrollo local | Windows con `FakeBedrockServer`; BDS real solo en el NAS |
| Idioma | Interfaz en español; código en inglés |
| Identificación de jugadores | XUID, capturado del log al conectar |
| Imagen | `ghcr.io/vgege86/cubekeeper` (repositorio público), publicada por GitHub Actions |

## Plan por fases

| Fase | Contenido |
|---|---|
| **1A** ✅ | Esqueleto, Dockerfile, compose, CI hacia GHCR, login, instalación de BDS (versión fijable, EULA), arrancar/parar/reiniciar, reinicio automático con límite, consola en vivo, `/health`, revisión de `server.properties`, ayuda para conectarse (PC y Xbox) |
| **1B** ✅ | Jugadores conectados (eventos del log + resincronización con `list`), historial en SQLite, editor de `server.properties` con validación |
| **1C** ✅ | Copias consistentes (`save hold` → `save query` → copiar truncando a la longitud indicada → `save resume`), programadas y con retención, restauración, actualización con rollback automático, tarea programada con avisos |
| **2A** ✅ | Lista de permitidos por XUID (`allowlist reload`), modo inscripción con solicitudes, permisos, bloquear, reglas del juego, controles rápidos |
| **2B** ✅ | Mundos (crear, cambiar, importar y exportar `.mcworld`, incluidos los de Realms), complementos (`.mcpack`, `.mcaddon`) |
| ~~2~~ | Lista de permitidos por XUID (`allowlist reload`), solicitudes o modo inscripción, permisos, bloquear, gamerules, controles rápidos, mundos (crear, cambiar, importar y exportar `.mcworld`, incluidos los de Realms), complementos |
| **3A** ✅ | Horarios y límite diario por jugador (avisos y expulsión), tiempo extra, historial de juego; auditoría |
| **3B** ✅ | Usuarios del panel con roles; verificación en dos pasos (TOTP) |
| **3C** ✅ | Avisos al móvil con ntfy |
| **3D** ✅ | Mapa del mundo con uNmINeD o bedrock-viz (MCP aparcado) |
| ~~3~~ | Horarios y límites por jugador, estadísticas, usuarios con roles, auditoría, notificaciones, MCP para Home365 (`ModelContextProtocol.AspNetCore`, como Home365), mapa con uNmINeD |

### Pendiente de comprobar en el NAS

- Las dependencias exactas de `bedrock_server` 1.26.65.1 en la imagen (`ldd`). Si falta alguna `.so`, se añade al Dockerfile.
- El enlace `minecraft://?addExternalServer=…` desde Edge en la Xbox.
- Si BDS registra en el log los intentos rechazados por la lista de permitidos. La fase 2A no depende de ello (usa el
  modo inscripción), pero si lo registra se podrían crear solicitudes sin abrir la lista.
- Que BDS 1.26 acepta `allowlist on`/`off` y `permission reload` en la consola. Si no, el modo de acceso se aplica igual
  (CubeKeeper expulsa), pero `allow-list` en server.properties solo cambia al reiniciar.
