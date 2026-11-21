# Instalar CubeKeeper en TrueNAS SCALE 25.10

Cómo desplegar CubeKeeper como app personalizada en el NAS, en lugar del contenedor
`itzg/minecraft-bedrock-server`.

- Uso diario del panel: [USO.md](USO.md)
- Si algo falla: [PROBLEMAS.md](PROBLEMAS.md)
- Pruebas para comprobar cada función en el NAS: [PRUEBAS.md](PRUEBAS.md)

> **Volver atrás es fácil en cualquier momento:** se para CubeKeeper y se arranca la app de itzg
> ([sección 9](#9-volver-a-itzg)). Su dataset (`/mnt/vms/minecraft`) no se toca.

---

## 1. Preparar los datasets

**Datos de CubeKeeper** (`/mnt/vms/cubekeeper`):

1. **Datasets → `vms` → Add Dataset**, nombre `cubekeeper`, Dataset Preset **Apps**.
2. **Permissions → Edit**: usuario y grupo **`apps` (568)**, con *Apply recursively*.

**Segundo destino de las copias** (`/mnt/DATOS/backups/cubekeeper`). Opcional, pero recomendado:

1. **Datasets → `DATOS` → Add Dataset** `backups` (si no existe) y dentro `cubekeeper`, preset **Apps**.
2. Mismos permisos (568).

Desde la shell del NAS, los permisos de los dos:

```bash
sudo chown -R 568:568 /mnt/vms/cubekeeper /mnt/DATOS/backups/cubekeeper
```

Crea los datasets **antes** de instalar la app. Si una ruta no existe, Docker la crea como root y CubeKeeper no podrá
escribir en ella. Si no quieres segundo destino, quita del YAML el volumen `/backups` y `CUBEKEEPER_BACKUP_SECONDARY`.

`vms/cubekeeper` entra en los snapshots diarios y en la replicación de `vms` a `backup`.

## 2. Comprobar que el puerto del panel está libre

El panel usa el **8765** (se cambia con `CUBEKEEPER_PORT`). Con Host Network no puede coincidir con nada que ya
escuche en el NAS:

```bash
sudo ss -tlnp | grep -E ':8765\b' || echo "8765 libre"
```

## 3. Parar la app de itzg

**Apps → minecraft → Stop.** Las dos usan Host Network y los mismos puertos (TCP 19132, UDP 19140 y 7551): no pueden
estar en marcha a la vez. No la borres: es tu plan B.

Para llevarte un `server.properties` o un mundo de itzg, cópialo **después** de instalar CubeKeeper, con el servidor
parado, y ponle de propietario 568:

```bash
sudo cp /mnt/vms/minecraft/server.properties /mnt/vms/cubekeeper/server/
```

```bash
sudo chown 568:568 /mnt/vms/cubekeeper/server/server.properties
```

Un mundo también se puede subir desde el panel (**Mundos → Importar**) como `.mcworld`.

## 4. Generar el secreto de las claves

CubeKeeper cifra sus claves (sesiones del panel, token de ntfy y cuenta de Realms) con un secreto que va en la
configuración de la app, **fuera del dataset**: así una copia o un snapshot de `/mnt/vms/cubekeeper` no sirve para
descifrarlas. Genéralo en la shell del NAS:

```bash
openssl rand -base64 32
```

**Guárdalo también en tu gestor de contraseñas.** Si se pierde, la app no arranca hasta que borres `/data/keys`
(ver [PROBLEMAS.md](PROBLEMAS.md#la-app-no-arranca-cubekeeper_key_secret)).

## 5. Instalar la app

**Apps → Discover Apps → ⋮ → Install via YAML.** Nombre: `cubekeeper`. Pega
[`deploy/docker-compose.yml`](../deploy/docker-compose.yml) y revisa estos valores:

| Variable | Valor | Para qué |
|---|---|---|
| `CUBEKEEPER_ADMIN_PASSWORD` | una contraseña de **10 caracteres o más** | Crea el usuario `admin` en el primer arranque. Después, bórrala. |
| `CUBEKEEPER_KEY_SECRET` | el secreto del paso 4 | Cifra las claves de `/data/keys`. No lo cambies después. |
| `CUBEKEEPER_BDS_VERSION` | `1.26.65.1` | Versión de BDS fijada. Vacía = la última publicada. |
| `CUBEKEEPER_ACCEPT_EULA` | `true` | Aceptas la [EULA de Minecraft](https://www.minecraft.net/eula). Sin esto no descarga nada. |
| `CUBEKEEPER_PORT` | `8765` | Puerto del panel. |
| `CUBEKEEPER_PUBLIC_HOST` | `192.168.1.21` | Dirección que se muestra para conectarse. |
| `CUBEKEEPER_ALLOWED_NETWORKS` | `192.168.1.0/24,10.6.0.0/24` | Redes desde las que se puede abrir el panel. |
| `CUBEKEEPER_TRUSTED_PROXIES` | `172.16.0.0/12,192.168.1.21` | Desde dónde llega NPM (bridge de Docker o el propio NAS). |
| `CUBEKEEPER_BACKUP_SECONDARY` | `/backups` | Segundo destino de las copias (el volumen del paso 1). |

El resto de variables están en el [README](../README.md#configuración).

> **Ojo con el símbolo `$`** en la contraseña: Docker Compose lo toma como una variable. Escríbelo doble (`$$`) o
> elige una contraseña sin `$`. Pon los valores siempre entre comillas.

La imagen `ghcr.io/vgege86/cubekeeper:latest` la publica GitHub Actions al fusionar en `main`.

## 6. Publicarlo en Nginx Proxy Manager

**Hosts → Proxy Hosts → Add Proxy Host:**

- Domain: `cubekeeper.alucard86.fyi` (AdGuard ya lo resuelve con la regla `*.alucard86.fyi`).
- Scheme `http`, Forward Hostname/IP `192.168.1.21`, Forward Port `8765`.
- **Websockets Support: activado.** Sin esto, la página carga pero los botones no responden.
- Access List: la de casa + VPN (`192.168.1.0/24`, `10.6.0.0/24`).
- SSL: el certificado wildcard `*.alucard86.fyi`, *Force SSL*.

El panel **no se expone a Internet**: la Access List de NPM y el filtro de redes de CubeKeeper lo limitan a casa y
a la VPN.

## 7. Primer arranque

1. Abre `https://cubekeeper.alucard86.fyi` y entra con `admin` y la contraseña del paso 5.
2. **Versión de BDS → Comprobar**: propone *Instalar 1.26.65.1*. Pulsa **Instalar** (descarga unos 100 MB y crea
   `server.properties` con la configuración probada).
3. **Estado → Arrancar.** En **Consola** aparece `Server started.`.
4. **Apps → cubekeeper → Edit**: borra `CUBEKEEPER_ADMIN_PASSWORD` y guarda.
5. Recomendado: activa la verificación en dos pasos en **Mi cuenta** (clic en tu nombre) y configura los avisos al
   móvil en **Avisos** ([USO.md](USO.md#avisos-al-móvil)).

Para conectarte desde el juego en PC (ROG Ally): **Servidores → Añadir servidor**, dirección `192.168.1.21`, puerto
`19132`. En la Xbox, ver el apartado siguiente.

## 7b. Xbox: entrar con BedrockConnect

La Xbox no deja añadir servidores por IP, y unirse desde «Partidas LAN» falla con el error «Boat» (con NetherNet, la
consola no manda su identidad por LAN y el servidor, con `online-mode=true`, la rechaza). Lo que funciona es
[BedrockConnect](https://github.com/Pugmatt/BedrockConnect): se desvía uno de los servidores destacados a su menú, y
desde ahí se entra al tuyo.

Se desvían **todos** los destacados que consulta Minecraft (salen en el registro de AdGuard al abrir la pestaña
Servidores). Así, si uno deja de funcionar, se prueba con otro sin tocar nada:

| Servidor destacado | Dominio | En la Xbox (26.60) |
|---|---|---|
| The Hive | `nethernet.playhive.com` | ✅ Sale el menú |
| Lifeboat | `mco.lbsg.net` | Funcionó el primer día; luego entraba al Lifeboat real |
| Mineville / InPvP (MegaSMP, GenWars, OneBlockOnline) | `play.inpvp.net` | Sin probar |
| MegaSMP | `play.megasmp.gg` | Sin probar |
| Enchanted | `play.enchanted.gg` | Sin probar |
| CubeCraft | `mco.cubecraft.net` | Sin probar (BedrockConnect no lo da como compatible) |

1. En **AdGuard → Filtros → Reglas de filtrado personalizadas**, pega este bloque y guarda:

   ```
   ! CubeKeeper: destacados de Minecraft → BedrockConnect (menú para entrar al servidor de casa desde la Xbox)
   ||nethernet.playhive.com^$dnsrewrite=104.238.130.180
   ||mco.lbsg.net^$dnsrewrite=104.238.130.180
   ||play.inpvp.net^$dnsrewrite=104.238.130.180
   ||play.megasmp.gg^$dnsrewrite=104.238.130.180
   ||play.enchanted.gg^$dnsrewrite=104.238.130.180
   ||mco.cubecraft.net^$dnsrewrite=104.238.130.180
   ```

   `104.238.130.180` es el BedrockConnect público, que mantiene el autor del proyecto (es la dirección que da su
   [README](https://github.com/Pugmatt/BedrockConnect) para Xbox). Si montas el tuyo en casa (abajo), cambia esa IP
   en todas las líneas. Las reglas van todas juntas, en un solo sitio. Si ya tenías reescrituras en
   **Filtros → Reescrituras DNS** para estos dominios, bórralas para que no haya dos.
2. En la Xbox, DNS en **automático** (para que use AdGuard). Si cambiaste algo, desenchúfala de la corriente 30
   segundos: la consola recuerda las direcciones y no hay forma de borrarlas.
3. **Servidores → The Hive → Unirse.** Sale el menú de BedrockConnect: **Connect to a Server**, dirección
   `192.168.1.21`, puerto `19132`, marca **Add to server list** y **Submit**.
4. Las siguientes veces: The Hive → Unirse → elige tu servidor en la lista. Si The Hive deja de llevar al menú,
   prueba con los otros de la tabla (en el BedrockConnect público, tu lista guardada es la misma entres por donde
   entres).

Mientras estas reglas estén activas, esos servidores destacados no se pueden jugar desde casa (llevan siempre al
menú). Para jugar en uno, pon `!` delante de su línea para desactivarla. El servidor de BedrockConnect es de terceros:
solo hace de menú; la partida va directamente de la Xbox a tu NAS.

Si al unirte entras en el servidor real: en el registro de consultas de AdGuard, filtra por la Xbox y comprueba que
su dominio sale como «Reescrito». Si no aparece, la Xbox pregunta a otro DNS (por ejemplo, el de IPv6 del operador);
si aparece reescrito, desenchufa la Xbox 30 segundos, vuelve a cargar la lista de Servidores o prueba con otro de la
tabla. Si Mojang o los servidores cambian de nombre, en ese mismo registro se ve qué dominios consulta Minecraft al
abrir la pestaña Servidores: añade el nuevo con la misma forma.

### BedrockConnect en casa (opcional)

Para no depender del servidor público: BedrockConnect como app aparte en el NAS, con su propia IP en la red de casa
(macvlan), porque el puerto 19132 del NAS ya lo usa el servidor de juego. No hace falta otra tarjeta de red: va sobre
la misma que tiene la `192.168.1.21`.

Necesitas: una IP libre de la red que el router no reparta por DHCP (por ejemplo `192.168.1.22`), el nombre de la
tarjeta del NAS que tiene la `192.168.1.21` (**Network → Interfaces**: `enp…`, `eno1`; si está dentro de un puente
como `br0`, usa el puente) y la puerta de enlace (normalmente `192.168.1.1`).

1. **Dataset** `vms/bedrockconnect` (preset Apps) con una carpeta `players` que sea **de root**: el contenedor
   funciona como root pero sin capacidades, así que no puede escribir en carpetas de otro usuario.

   ```bash
   sudo mkdir -p /mnt/vms/bedrockconnect/players
   ```

   ```bash
   sudo chown -R root:root /mnt/vms/bedrockconnect/players
   ```

2. Copia [`deploy/bedrockconnect/custom_servers.json`](../deploy/bedrockconnect/custom_servers.json) a
   `/mnt/vms/bedrockconnect/custom_servers.json`. Hace que «CubeKeeper» salga en el menú para todos, sin escribir la
   dirección.
3. **Apps → Discover Apps → ⋮ → Install via YAML**, nombre `bedrockconnect`, con
   [`deploy/bedrockconnect/docker-compose.yml`](../deploy/bedrockconnect/docker-compose.yml). Cambia las tres líneas
   marcadas con `CAMBIA` (IP, tarjeta y puerta de enlace).
4. En AdGuard, en las reglas personalizadas del paso anterior, cambia `104.238.130.180` por la IP que le hayas dado
   (en todas las líneas).
5. Xbox: **Servidores → The Hive → Unirse** → «CubeKeeper».

Las listas de servidores que guardó la Xbox en el BedrockConnect público no pasan al tuyo (cada instancia tiene las
suyas), pero con `custom_servers.json` el tuyo ya sale sin guardarlo.

**Cuando se actualice el juego.** Si la Xbox se actualiza a una versión con protocolo nuevo, al entrar en The Hive
dirá que el servidor está desactualizado o no conectará. Mira la última versión en
[Docker Hub](https://hub.docker.com/r/pugmatt/bedrock-connect/tags), cámbiala en la línea `image:` de la app
(**Apps → bedrockconnect → Edit**) y guarda. Las listas de los jugadores se conservan (están en el dataset). Ese
mismo día suele hacer falta también la versión nueva de BDS: CubeKeeper avisa al móvil, y se cambia con
`CUBEKEEPER_BDS_VERSION` y **Instalar**.

**Si no funciona:**

- **La Xbox entra en el menú y la echa enseguida;** en el registro de la app (`sudo docker logs --tail 100
  ix-bedrockconnect-bedrockconnect-1`) sale `An error occurred saving to player file` y `Permission denied`: la
  carpeta `players` no es de root. `sudo chown -R root:root /mnt/vms/bedrockconnect/players` (no hace falta
  reiniciar).
- **Se para y arranca sola sin parar:** el YAML de la guía ya no lleva `user: "568:568"` ni el `/tmp` en memoria
  (`tmpfs`), que la hacían cerrarse al arrancar. Si los añadiste, quítalos.
- **TrueNAS rechaza la red** `macvlan`: comprueba el nombre de la tarjeta y que la IP no esté en uso.
- **La Xbox no llega a BedrockConnect** (en el registro de la app no aparece ninguna línea al intentar entrar):
  comprueba en el registro de consultas de AdGuard que el destacado se reescribe a la IP de tu BedrockConnect, y
  desenchufa la Xbox 30 segundos. Para ver si esa IP responde, abre `http://IP:19132/` desde el móvil en el wifi de
  casa: un error «404» quiere decir que sí llega.
- **Sale el menú pero no conecta a CubeKeeper**: la Xbox se conecta al NAS directamente, como con el público; revisa
  que en `custom_servers.json` esté `192.168.1.21` y el puerto `19132`.
- Desde el propio NAS no se puede hacer ping a esa IP (es normal con macvlan); desde otro equipo de casa sí.

## 8. Actualizar

Cuando se publica una versión nueva de CubeKeeper: **Apps → cubekeeper → Update** (o **⋮ → Pull Image** y reiniciar).
La base de datos se migra sola y el servidor de juego se para y arranca limpiamente.

Las versiones de **BDS** (el servidor de Minecraft) son otra cosa: se cambian con `CUBEKEEPER_BDS_VERSION` y el botón
**Instalar** del panel ([USO.md](USO.md#versión-de-bds)).

## 9. Volver a itzg

1. **Apps → cubekeeper → Stop.**
2. **Apps → minecraft → Start.**
