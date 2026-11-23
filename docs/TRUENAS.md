# Instalar CubeKeeper en TrueNAS SCALE 25.10

Cómo desplegar CubeKeeper como app personalizada en el NAS, en lugar del contenedor
`itzg/minecraft-bedrock-server`.

- Uso diario del panel: [USO.md](USO.md)
- Si algo falla: [PROBLEMAS.md](PROBLEMAS.md)
- Pruebas para comprobar cada función en el NAS: [PRUEBAS.md](PRUEBAS.md)
- Entrar desde la Xbox: [XBOX.md](XBOX.md)

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

## 7b. Xbox

Para entrar desde la Xbox (Amigos con MCXboxBroadcast, o destacados con BedrockConnect), ver [XBOX.md](XBOX.md): qué
opciones hay, cuál elegir y cómo se instala cada una.

## 8. Actualizar

Cuando se publica una versión nueva de CubeKeeper: **Apps → cubekeeper → Update** (o **⋮ → Pull Image** y reiniciar).
La base de datos se migra sola y el servidor de juego se para y arranca limpiamente.

Las versiones de **BDS** (el servidor de Minecraft) son otra cosa: se cambian con `CUBEKEEPER_BDS_VERSION` y el botón
**Instalar** del panel ([USO.md](USO.md#versión-de-bds)).

## 9. Volver a itzg

1. **Apps → cubekeeper → Stop.**
2. **Apps → minecraft → Start.**
