# Lector USB (SerialBridge): lectora COM → WebSocket

En Windows la aplicación se muestra como **Lector USB** (icono de la barra de tareas, página de configuración, instalador, Aplicaciones instaladas y Servicios). Internamente conserva el nombre técnico `SerialBridge` (servicio, `SerialBridge.exe` y carpetas), para que las actualizaciones funcionen sobre instalaciones previas.

Servicio de Windows (.NET 8) que lee una lectora de códigos o de tarjetas configurada como **puerto COM virtual (USB CDC)** y reenvía cada trama por WebSocket al front.

```
LECTOR → USB (COM virtual) → SerialBridge (SerialPort.DataReceived) → ws://127.0.0.1:21818 → Front
```

Requisitos: Windows 10/11 o Windows Server 2012 en adelante. No funciona en Windows 7 ni 8.1.

## Lectoras USB: modo COM obligatorio

Las lectoras se conectan por **USB**, pero deben estar configuradas en modo **USB-COM / USB CDC / Virtual COM**. Así Windows les asigna un puerto COM (por ejemplo `COM3`), que es lo que lee el servicio.

De fábrica casi todas vienen en **modo teclado (HID)**. En ese modo no crean un puerto COM y el servicio no las ve. Para cambiarlas se escanea el código de configuración del manual (a veces figura como *USB Serial Emulation*).

Para comprobarlo, abra el Administrador de dispositivos: la lectora debe aparecer en **Puertos (COM y LPT)**, por ejemplo *Dispositivo serie USB (COM3)*. Si aparece con un signo de advertencia, instale el driver del fabricante (CH340, Prolific, FTDI o el de la marca).

## Compilar el instalador

Requiere el SDK de .NET 8 e Inno Setup 6 (`winget install JRSoftware.InnoSetup`).

```powershell
.\build.ps1
```

Todo lo generado queda en `output\` (la raíz del proyecto no se toca):

| Ruta | Contenido |
|---|---|
| `output\LectorUSB-Setup-<versión>.exe` | **Instalador para distribuir.** Sirve para Windows de 64 y 32 bits: instala automáticamente la versión que corresponde |
| `output\publish\x64\SerialBridge.exe` | Ejecutable para Windows de 64 bits |
| `output\publish\x86\SerialBridge.exe` | Ejecutable para Windows de 32 bits |
| `output\bin`, `output\obj` | Compilación intermedia |

`.\build.ps1 -SkipInstaller` genera solo los ejecutables. Los ejecutables son autocontenidos: el equipo destino no necesita tener .NET instalado.

## Instalación

Ejecute `LectorUSB-Setup-<versión>.exe` como administrador. El asistente:

1. Pide el puerto del WebSocket (21818 por defecto; en una actualización propone el que ya estaba).
2. Instala en `C:\Program Files\SerialBridge` y registra el servicio con inicio automático y reinicio si falla.
3. Deja el **icono del lector de códigos en la barra de tareas** de cada usuario, que se inicia con la sesión.

Se desinstala desde *Configuración → Aplicaciones*. Al desinstalar, el asistente pregunta si se borra también la configuración.

## Icono en la barra de tareas

Aparece en el área de notificación, junto al reloj. Al pasar el mouse muestra el estado de la lectora. Clic derecho:

| Opción | Qué hace |
|---|---|
| **Configuración** | Abre la página de configuración en el navegador (también con doble clic) |
| **Reiniciar** | Reinicia el servicio. Si el servicio está detenido, Windows pide permiso de administrador |
| **Desinstalar** | Abre el desinstalador (pide confirmación y permiso de administrador) |

Luego abra **http://127.0.0.1:21818/** para configurar:

| Campo | Descripción |
|---|---|
| Lectora USB (puerto COM virtual) | Lista de las lectoras USB conectadas en modo COM, con el nombre del dispositivo (p. ej. `COM3 — Dispositivo serie USB`). Solo se leen las tramas del puerto elegido; mientras no se elija ninguno, el servicio no lee nada |
| Puerto de salida del WebSocket | Puerto de `ws://127.0.0.1:<puerto>/`. Al guardarlo, el servicio pasa a escuchar en el nuevo puerto y la página se redirige sola |
| Baudios, bits de datos, paridad, bits de parada | Parámetros serie (normalmente 9600 8N1) |
| Formato de salida | `json` o `raw` (sólo el texto) |
| Fin de trama por silencio | Para lectoras que no envían CR/LF |
| Orígenes permitidos | Lista blanca de páginas que pueden conectarse al WebSocket |

La configuración se guarda en `C:\ProgramData\SerialBridge\settings.json`. También puede editarse a mano: el servicio aplica los cambios sin reiniciar.

## Consumo desde el front

```js
const ws = new WebSocket('ws://127.0.0.1:21818');
ws.onmessage = (e) => {
  const msg = JSON.parse(e.data);
  if (msg.type === 'frame') console.log('Leído:', msg.data);        // {"type":"frame","port":"COM3","data":"7501234567890","timestamp":"..."}
  if (msg.type === 'status') console.log('Lectora:', msg.connected, msg.message);
};
ws.onclose = () => setTimeout(/* reconectar */ () => {}, 2000);
```

En formato `raw`, `e.data` es directamente el texto leído y no se envían mensajes de estado.

## Comportamiento

- **Tramas:** se separan por CR y/o LF. Si la lectora no envía terminador, lo acumulado se emite tras el silencio configurado (100 ms por defecto).
- **Reconexión:** si se desconecta la lectora o el puerto está ocupado por otro programa, reintenta cada 3 s y avisa a los clientes con un mensaje `status`.
- **Red:** sólo escucha en `127.0.0.1`, así que no es accesible desde otros equipos.
- **Registros:** en el Visor de eventos → Registros de Windows → Aplicación, origen `SerialBridge`.

## Desarrollo

```powershell
dotnet run                      # corre el servicio en consola (sin instalarlo)
dotnet run -- --tray            # abre el icono de la barra de tareas
SerialBridge.exe --set-port N   # cambia el puerto del WebSocket y termina
```

API: `GET /api/status`, `GET /api/ports`, `GET /api/settings`, `PUT /api/settings`, `POST /api/service/restart`. WebSocket en `/` o `/ws`.

Instalación manual sin el asistente (como administrador): `.\build.ps1 -SkipInstaller` y luego `.\install-service.ps1`. Para quitarla: `.\uninstall-service.ps1 [-RemoveFiles]`.

El icono se genera con `assets\make-icon.ps1` (solo hace falta si se cambia su diseño).
# LECTORA-USB-COM
