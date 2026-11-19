# Instalar CubeKeeper en TrueNAS SCALE 25.10

Guía para la fase 1A: desplegar CubeKeeper como app personalizada, sustituir al contenedor
`itzg/minecraft-bedrock-server` y comprobar que todo funciona.

> **Volver atrás es fácil en cualquier momento:** se para CubeKeeper y se arranca la app de itzg.
> Su dataset (`/mnt/vms/minecraft`) no se toca.

---

## 1. Preparar el dataset

1. **Datasets → `vms` → Add Dataset**.
   - Nombre: `cubekeeper` (queda en `/mnt/vms/cubekeeper`).
   - Dataset Preset: **Apps**.
2. Con el dataset seleccionado → **Permissions → Edit**: propietario usuario **`apps` (568)** y grupo **`apps` (568)**,
   marcando *Apply recursively*.

   O desde la shell del NAS:

   ```bash
   sudo chown -R 568:568 /mnt/vms/cubekeeper
   ```

El dataset ya entra en los snapshots diarios y en la replicación de `vms` a `backup`.

## 2. Comprobar que el puerto del panel está libre

El panel usa el **8765** por defecto (se cambia con `CUBEKEEPER_PORT`). Con Host Network no puede coincidir
con nada que ya escuche en el NAS:

```bash
sudo ss -tlnp | grep -E ':8765\b' || echo "8765 libre"
```

Si está ocupado, elige otro y cámbialo en `CUBEKEEPER_PORT`. El healthcheck lee esa misma variable.

## 3. Parar la app de itzg

**Apps → minecraft → Stop.** Las dos usan Host Network y los mismos puertos (TCP 19132, UDP 19140 y 7551):
no pueden estar en marcha a la vez. No la borres: es tu plan B.

Como no tienes ningún mundo que conservar, no hace falta migrar nada. Si quieres llevarte tu
`server.properties`, cópialo **después** de instalar CubeKeeper (paso 6), con el servidor parado:

```bash
sudo cp /mnt/vms/minecraft/server.properties /mnt/vms/cubekeeper/server/
```

```bash
sudo chown 568:568 /mnt/vms/cubekeeper/server/server.properties
```

Para un mundo existente se haría igual con `worlds/`, `allowlist.json` y `permissions.json`.

## 4. Instalar la app

**Apps → Discover Apps → ⋮ → Install via YAML.** Nombre: `cubekeeper`. Pega
[`deploy/docker-compose.yml`](../deploy/docker-compose.yml) y revisa estos valores:

| Variable | Valor | Para qué |
|---|---|---|
| `CUBEKEEPER_ADMIN_PASSWORD` | una contraseña de **10 caracteres o más** | Crea el usuario `admin` en el primer arranque. Después, bórrala de la app. |
| `CUBEKEEPER_ADMIN_RESET` | (no ponerla) | Solo para recuperar el acceso: con `true`, pone esa contraseña al `admin` y lo desbloquea. Quítala después. |
| `CUBEKEEPER_BDS_VERSION` | `1.26.65.1` | Versión de BDS fijada. Vacía = la última publicada. |
| `CUBEKEEPER_ACCEPT_EULA` | `true` | Aceptas la [EULA de Minecraft](https://www.minecraft.net/eula). Sin esto no descarga nada. |
| `CUBEKEEPER_PORT` | `8765` | Puerto del panel. |
| `CUBEKEEPER_PUBLIC_HOST` | `192.168.1.21` | Dirección que se muestra para conectarse. |
| `CUBEKEEPER_ALLOWED_NETWORKS` | `192.168.1.0/24,10.6.0.0/24` | Redes desde las que se puede abrir el panel. |
| `CUBEKEEPER_TRUSTED_PROXIES` | `172.16.0.0/12,192.168.1.21` | Desde dónde llega NPM (bridge de Docker o el propio NAS). |

> **Ojo con el símbolo `$` en la contraseña.** En el YAML, Docker Compose lo interpreta como una variable y la
> contraseña llega cambiada. Escríbelo doble (`$$`) o elige una contraseña sin `$`. Ponla siempre entre comillas.

La imagen `ghcr.io/vgege86/cubekeeper` la publica GitHub Actions al fusionar en `main` (y con cada tag `v*`).

> Si TrueNAS no puede descargar la imagen, revisa en GitHub → *Packages → cubekeeper → Package settings*
> que su visibilidad sea **Public**.

## 5. Publicarlo en Nginx Proxy Manager

**Hosts → Proxy Hosts → Add Proxy Host:**

- Domain: `cubekeeper.alucard86.fyi` (AdGuard ya lo resuelve con la regla `*.alucard86.fyi`).
- Scheme `http`, Forward Hostname/IP `192.168.1.21`, Forward Port `8765`.
- **Websockets Support: activado.** Sin esto, Blazor no funciona: la página carga, pero los botones no responden.
- Access List: la de casa + VPN (`192.168.1.0/24`, `10.6.0.0/24`).
- SSL: el certificado wildcard `*.alucard86.fyi`, *Force SSL*.

## 6. Primer arranque

1. Abre `https://cubekeeper.alucard86.fyi` y entra con `admin` y la contraseña del paso 4.
2. **Versión de BDS → Comprobar**: debe proponer *Instalar 1.26.65.1*. Pulsa **Instalar**: descarga unos 100 MB,
   lo descomprime y crea `server.properties` con la configuración probada.
3. **Estado → Arrancar.** En **Consola** debe aparecer `Server started.`.
4. Ve a **Apps → cubekeeper → Edit**, borra `CUBEKEEPER_ADMIN_PASSWORD` y guarda.

---

## 7. Pruebas de la fase 1A

| # | Prueba | Resultado esperado |
|---|---|---|
| 1 | Abrir `http://192.168.1.21:8765/health` | `{"status":"Healthy",…}` |
| 2 | TrueNAS → Apps → cubekeeper | Estado *Running* y sano (tras unos 2 minutos) |
| 3 | Login con contraseña mala 5 veces | Bloqueo de 15 minutos |
| 4 | Panel → Instalar 1.26.65.1 → Arrancar | Estado *En marcha*, versión en marcha 1.26.65.1 |
| 5 | Consola: `list` | `There are 0/10 players online:` |
| 6 | ROG Ally: Servidores → Añadir `192.168.1.21` : `19132` y entrar | Entras al mundo. La consola muestra `Player connected: …, xuid: …` |
| 7 | Consola: `say Hola` | El mensaje aparece en el chat del juego |
| 8 | Xbox: abrir con Edge la página de entrada de CubeKeeper (`https://cubekeeper.alucard86.fyi` o `http://192.168.1.21:8765`) y pulsar **Añadir el servidor en Xbox** | **A comprobar.** Si funciona, Minecraft se abre y el servidor aparece en «Servidores» |
| 9 | Xbox: «Partidas LAN» | Aparece el servidor (unirse probablemente siga dando «Boat») |
| 10 | Panel → Parar, con alguien dentro | La consola muestra `Quit correctly`; el mundo se guarda |
| 11 | Simular una caída: `sudo pkill -9 bedrock_server` en la shell del NAS | «Se ha cerrado inesperadamente» y, a los 5 s, vuelve a arrancar solo |
| 12 | Reiniciar la app desde TrueNAS con el servidor en marcha | Para BDS limpiamente y, al volver, lo arranca solo |
| 13 | Abrir el panel desde una IP fuera de las permitidas (o sin VPN) | 403 de NPM o de CubeKeeper |

Si **no arranca**, mira la consola del panel o los registros de la app en TrueNAS. Si sale un error
del tipo `error while loading shared libraries: libXXX.so`, dime qué librería falta y la añado a la imagen.

## 7b. Pruebas de la fase 1B

| # | Prueba | Resultado esperado |
|---|---|---|
| 1 | Entrar al servidor desde el Ally y abrir **Jugadores** | Apareces en «Conectados ahora» con tu XUID |
| 2 | **Panel** | En «Jugadores» pone «1 conectado · tu gamertag» |
| 3 | Salir del juego | Desapareces de «Conectados ahora»; en «Últimas sesiones» consta la salida («salió») y la duración |
| 4 | Entrar, y **Parar** el servidor desde el panel | La sesión se cierra como «servidor parado» |
| 5 | Pulsar **Comprobar** en Jugadores con alguien dentro | En la consola aparece `list` y la lista no cambia |
| 6 | **Ajustes** → cambiar «Dificultad» a `hard` → Guardar | Mensaje «Guardado: difficulty» y botón «Reiniciar ahora» |
| 7 | Reiniciar desde ese botón y entrar al juego | La dificultad es Difícil |
| 8 | Ajustes → «Jugadores máximos» = 0 → Guardar | Error en el campo; no se guarda nada |
| 9 | Abrir `/mnt/vms/cubekeeper/server/` en la shell | Hay un `server.properties.AAAAMMDD-HHMMSS.bak` por cada guardado, y los comentarios siguen en `server.properties` |
| 10 | Reiniciar la app de TrueNAS con alguien dentro | Al volver, su sesión anterior está cerrada y empieza una nueva |

Al actualizar, la base de datos se migra sola (se añaden las tablas de jugadores); los usuarios del panel se conservan.

## 7c. Fase 1C: copias y actualizaciones

### Preparar el segundo destino (pool DATOS)

1. **Datasets → `DATOS` → Add Dataset** `backups` (si no existe) y dentro `cubekeeper`, preset *Apps*.
2. Propietario 568:

   ```bash
   sudo chown -R 568:568 /mnt/DATOS/backups/cubekeeper
   ```

3. Edita la app y añade (ya viene en `deploy/docker-compose.yml`):
   - el volumen `- /mnt/DATOS/backups/cubekeeper:/backups`
   - la variable `CUBEKEEPER_BACKUP_SECONDARY: /backups`

   Crea el dataset **antes** de actualizar la app. Si la ruta no existe, Docker la crea como root y CubeKeeper no podrá
   escribir en ella (las copias se quedan solo en `/data/backups` y el error sale en los registros). Si no quieres segundo
   destino, quita esas dos líneas.

### Pruebas

| # | Prueba | Resultado esperado |
|---|---|---|
| 1 | **Copias** → «Destinos» | `/data/backups` y `/backups (montado)` |
| 2 | Entrar al juego, **Hacer copia ahora** | «Copia creada»; en la consola `save hold`, `save query`… y `Changes to the level are resumed`. Sigues jugando sin cortes |
| 3 | Mirar `/mnt/vms/cubekeeper/backups` y `/mnt/DATOS/backups/cubekeeper` | El mismo `.mcworld` en los dos |
| 4 | **Descargar** una copia y abrirla con doble clic en el Ally | Minecraft la importa como mundo |
| 5 | Romper algo en el juego, **Restaurar…** la copia anterior | El servidor se reinicia, el mundo vuelve a como estaba y aparece una copia «Antes de restaurar» |
| 6 | Jugar más de una hora | Aparece una copia «Programada» cada hora; si nadie juega, no se hacen |
| 7 | **Panel** → Versión de BDS | «Automática: No (versión fijada)» |
| 8 | (Opcional) Cambiar `CUBEKEEPER_BDS_VERSION` a otra versión, reiniciar la app y pulsar **Instalar** | Copia «Antes de actualizar», instala y arranca; aparece «Volver a la versión anterior» |
| 9 | (Opcional) **Volver a la versión anterior…** | Copia previa e instala la anterior sin volver a descargarla |

## 7d. Fase 2A: acceso y juego

| # | Prueba | Resultado esperado |
|---|---|---|
| 1 | Entrar desde el Ally con el modo **Abierto** y abrir **Acceso** | Apareces en «Otros jugadores» con tu XUID |
| 2 | **Aprobar** tu usuario y cambiar el permiso a **Operador** | En `allowlist.json` y `permissions.json` aparece tu XUID; en el juego puedes usar comandos |
| 3 | Cambiar a **Solo aprobados** | En la consola, `allowlist on`; en server.properties, `allow-list=true`. Tú sigues dentro |
| 4 | Que alguien no aprobado intente entrar (otra cuenta o un amigo) | El juego le dice que no está invitado. **Mira la consola**: ¿aparece alguna línea con su nombre? Cuéntamelo |
| 5 | **Activar inscripción** 10 min y que vuelva a intentarlo | Entra un instante, le expulsa con «Solicitud enviada…» y aparece en «Solicitudes» |
| 6 | **Aprobar** la solicitud como visitante y que vuelva a entrar | Entra y puede mirar pero no romper bloques |
| 7 | **Bloquear** a ese jugador | Le expulsa al momento y cada vez que intenta volver |
| 8 | **Juego** → Hora «Noche», Clima «Lluvia», Mensaje «hola» | Se ve en el juego |
| 9 | **Juego** → «Leer del servidor» | Los interruptores muestran los valores reales. Si sale un aviso de formato, cuéntamelo |
| 10 | Activar «Conservar el inventario» y morir | No pierdes los objetos |

## 7e. Fase 2B: mundos y complementos

| # | Prueba | Resultado esperado |
|---|---|---|
| 1 | **Mundos**: está tu mundo como «activo» con su tamaño | — |
| 2 | **Descargar** el mundo activo con alguien jugando | Baja un `.mcworld` sin cortar la partida; aparece también como copia «Manual» |
| 3 | **Crear un mundo nuevo** (nombre, semilla vacía, Creativo, Pacífico) | Copia del mundo anterior, reinicio y entras en un mundo nuevo en creativo |
| 4 | **Usar este** con el mundo anterior | Vuelves al mundo de antes, tal cual |
| 5 | **Importar** un `.mcworld` (por ejemplo, el descargado en el paso 2, con otro nombre) | Aparece en la lista; con «Usarlo ahora», el servidor arranca con él |
| 6 | **Borrar…** un mundo que no esté activo | Desaparece y queda una copia «Antes de un cambio de mundo» en Copias |
| 7 | **Complementos**: instalar un `.mcpack` de comportamiento (por ejemplo, uno de mcpedl) y **Reiniciar ahora** | Aparece activado y funciona en el juego |
| 8 | Instalar un `.mcaddon` (comportamiento + recursos) | Salen los dos paquetes; para ver texturas, activa «Exigir paquetes de recursos» en Ajustes |
| 9 | **Desactivar** y reiniciar | El complemento deja de funcionar |

Los complementos con scripts pueden necesitar activar funciones experimentales («API beta») en el mundo, y eso se hace
desde Minecraft al crear o editar el mundo, no desde CubeKeeper.

## 7f. Fase 3A: familia y auditoría

| # | Prueba | Resultado esperado |
|---|---|---|
| 1 | **Familia** → Editar a un jugador → «Tiempo máximo al día» 30 min, Guardar | En «Ahora» pone cuánto le queda |
| 2 | Que juegue hasta que falten 5 min | Le llega un mensaje privado «Te quedan 5 minutos…» y otro al quedar 1 |
| 3 | Al acabarse | Le expulsa con «Se acabó el tiempo de juego de hoy (30 min). Puedes volver mañana…» y no puede volver a entrar hoy |
| 4 | **+15 min** | Puede volver a entrar 15 minutos más |
| 5 | Añadir una franja «Entre semana 17:00–20:30» y probar fuera de ese horario | Le expulsa con «Ahora no es horario de juego. Puedes volver hoy/mañana a las 17:00» |
| 6 | «Últimos 7 días» | El tiempo jugado de cada día |
| 7 | **Auditoría** | Están tus acciones, los inicios de sesión (con la IP) y las expulsiones de «CubeKeeper» por las normas |

## 7g. Fase 3B: usuarios y verificación en dos pasos

| # | Prueba | Resultado esperado |
|---|---|---|
| 1 | **Usuarios** → Añadir «papa» como Operador | Aparece en la lista |
| 2 | Entrar como «papa» (otro navegador o ventana privada) | Solo ve Panel, Jugadores, Familia y Juego; en Panel no puede instalar ni cambiar de versión |
| 3 | Como «papa», abrir `/ajustes` a mano | Página «Sin permiso» |
| 4 | **Mi cuenta** (clic en tu nombre arriba a la derecha) → escanear el QR con Google/Microsoft Authenticator → escribir el código → Activar | Muestra 10 códigos de recuperación: guárdalos |
| 5 | Salir y volver a entrar | Tras la contraseña pide el código de la app |
| 6 | Entrar con un código de recuperación | Funciona una vez; «Te quedan 9» |
| 7 | Como administrador, cambiar el rol de «papa» a Administrador | En menos de un minuto, en su sesión aparecen las secciones de administrador |
| 8 | Intentar quitarte a ti mismo el rol de administrador siendo el único | «Tiene que quedar al menos un administrador» |

Si pierdes el móvil y los códigos de recuperación: otro administrador puede quitarte la verificación en dos pasos desde
Usuarios. Si eres el único administrador, usa `CUBEKEEPER_ADMIN_RESET` (sección 8): restablece la contraseña, desbloquea
la cuenta y quita la verificación en dos pasos. Después vuelve a activarla desde Mi cuenta.

## 7h. Fase 3C: avisos al móvil (ntfy)

1. Instala la app **ntfy** en el móvil.
2. En CubeKeeper, **Avisos** → pulsa «Generar» para crear un tema difícil de adivinar, escribe la dirección del panel
   (`https://cubekeeper.alucard86.fyi`) y **Guardar**. No hace falta tocar la app de TrueNAS.
3. En la app ntfy, suscríbete al tema que aparece debajo.

| # | Prueba | Resultado esperado |
|---|---|---|
| 1 | **Avisos** → Enviar aviso de prueba | Llega «CubeKeeper: prueba» al móvil; al tocarlo se abre el panel |
| 2 | Activar la inscripción y que alguien no aprobado intente entrar | Aviso «Solicitud de acceso: …» |
| 3 | `sudo pkill -9 bedrock_server` en el NAS | Aviso «El servidor se ha caído» |
| 4 | Esperar a que Mojang publique una versión más nueva que la fijada (o comprobarlo a las 6 h de arrancar) | Aviso «BDS x.y.z disponible», una sola vez |
| 5 | Activar «Entradas y salidas» y entrar al juego | Aviso «… ha entrado» |

## 8. Si no puedes entrar

- La pantalla de login dice por qué no hay usuario, por ejemplo si la contraseña es demasiado corta. Corrígelo en
  **Apps → cubekeeper → Edit** y reinicia la app.
- Si olvidas la contraseña, la cuenta queda bloqueada o pierdes el móvil de la verificación en dos pasos: añade
  `CUBEKEEPER_ADMIN_RESET: "true"` junto a `CUBEKEEPER_ADMIN_PASSWORD` con la nueva, reinicia, entra y quita las dos
  variables. (También quita la verificación en dos pasos de ese usuario.)
- En los registros de la app aparece `Administrador inicial 'admin' creado` o el motivo del fallo.

## 9. Volver a itzg

1. **Apps → cubekeeper → Stop.**
2. **Apps → minecraft → Start.**
