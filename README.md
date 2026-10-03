# Summs

Timers de hechizos de invocador enemigos para League of Legends, con superposición en pantalla.

## Uso

1. Ejecuta `Summs.exe` (aparece un icono en la bandeja del sistema). Pide permisos de
   administrador al abrirse: el juego se ejecuta con privilegios elevados y sin ellos
   Windows no deja que Summs reciba las teclas mientras el juego tiene el foco.
2. Juega en modo **Sin bordes** o **Ventana**: en Pantalla completa la superposición no se ve.
3. Cuando un enemigo use un hechizo de invocador, pulsa el atajo de su rol y hechizo:

   Cada rol tiene una tecla del teclado numérico, colocada como en el mapa:

   ```
   [7] TOP    8      9
   [4] JG    [5] MID 6
   [1] ADC   [2] SUP 3
   ```

   | Atajo              | Acción                                    |
   |--------------------|-------------------------------------------|
   | Ctrl+tecla del rol | Timer del Flash                           |
   | Shift+tecla del rol | Timer del otro hechizo (el que no es Flash) |
   | Alt+Ctrl+tecla del rol | Borra el timer del Flash              |
   | Alt+Shift+tecla del rol | Borra el timer del otro hechizo      |
   | Ctrl+Alt+Num0      | Borra todos los timers                    |

   Por ejemplo, Ctrl+Num7 pone el timer del Flash del TOP y Shift+Num1 el del otro
   hechizo del ADC. Da igual si el enemigo lleva Flash en la D o en la F. Si no lleva Flash, Ctrl es el
   hechizo de la D y Shift el de la F. Solo cuenta el Alt izquierdo; AltGr se ignora.
   Estos son los atajos predeterminados; se cambian en **Atajos...** del menú de la
   bandeja (la tecla de cada rol y los modificadores de cada acción, y el atajo de
   borrar todo).
4. Summs identifica, con la API local del juego, qué campeón enemigo juega ese rol, qué
   hechizos lleva y si tiene botas con aceleración de hechizos de invocador
   (Botas de Lucidez +10, Crimson Lucidity +20). Si la partida no asigna roles (por
   ejemplo, personalizadas o Herramienta de práctica) aparece un aviso y no se crea el timer.
5. Cuando falta 1 minuto la fila parpadea y muestra una cuenta atrás (`17:30 (0:59)`).
   Al volver el hechizo parpadea en verde y cuenta el tiempo que lleva disponible
   (`listo (+0:03)`) durante 10 segundos; luego desaparece. Repetir un atajo reemplaza
   ese timer. Al terminar la partida se borran todos.

Si el enemigo lleva la rama de **Inspiración** (principal o secundaria) se supone que lleva
**Perspicacia cósmica** (+18 de aceleración de hechizos de invocador) y el timer la incluye.
La API solo dice qué ramas lleva, no qué runas, así que esos timers se marcan con `insp.`
en turquesa (`Flash en 16:44 insp.`): si no la lleva, el hechizo vuelve algo más tarde.
Para confirmarlo, mira en el TAB el enfriamiento de su hechizo y pasa el ratón por `insp.`:
**clic** = la lleva (se quita la marca), **clic derecho** = no la lleva (se recalcula sin
ella). La respuesta vale para ese enemigo el resto de la partida.

El tiempo se calcula como `tiempo de partida + enfriamiento − 10 s` de margen de
reacción, con el enfriamiento reducido por las botas. Castigo usa la recarga de carga
(90 s). Desde el minuto 10 Teleport pasa a **Teleport desatado**, cuyo enfriamiento depende
del nivel del enemigo al pulsar el atajo: de 330 s a nivel 1 a 240 s a nivel 18 (Data Dragon
no lo publica; valores del parche 16.19 según la wiki de League). Cada hechizo tiene su color (Flash amarillo, Teleport morado, Ignite naranja…).

### Superposición

- Cuando no detecta ninguna partida (al abrir Summs o al terminar una) muestra durante unos
  4 segundos una nota diminuta, `● sin partida`, en el sitio de la superposición, y luego la
  oculta. Summs sigue en la bandeja esperando la siguiente partida.

- Al pasar el ratón por un timer, la fila se resalta y aparece una ✕ roja en lugar del icono
  del campeón: **clic** en la ✕ borra solo ese timer (útil si te equivocaste de atajo).
  Con la **rueda del ratón** sobre la fila, cada paso mueve el timer 5 s: hacia abajo vuelve
  antes (por ejemplo, si viste el Flash tarde), hacia arriba vuelve más tarde. La rueda
  sobre la superposición no llega al juego, así que la cámara no hace zoom.

- Se arrastra con el ratón en cualquier momento (como la de Blitz), sin quitar el foco
  al juego. Los clics sobre ella no llegan al juego. Crece hacia arriba desde su borde
  inferior y se desplaza a la izquierda si no cabe en la pantalla.
- Menú de la bandeja:
  - **Estilo de fila**: *Campeón + texto* (`[icono] Flash en 17:30`) o
    *Rol + iconos* (`ADC [campeón][hechizo] 17:30`).
  - **Mostrar para colocar**: muestra la superposición vacía para moverla.
  - **Atajos...**: cambia la tecla de cada rol, los modificadores de cada acción y el atajo
    de borrar todos los timers (este en un solo campo: pulsa la combinación entera). Haz clic
    en la casilla y pulsa la tecla, o los modificadores (por ejemplo Ctrl+Alt; Supr los
    quita). Mientras está abierto, los atajos no hacen nada.

### Compartir timers

Los compañeros de equipo que también usan Summs ven los mismos timers: lo que uno pone, borra
o mueve con la rueda, y las respuestas a `insp.`, aparece en los demás. No hay que configurar
nada: al empezar la partida cada Summs calcula la sala de su equipo a partir de los jugadores
de la partida (un hash de los 10 Riot ID y el equipo) y entra en ella; da igual el orden en que
lo abran o si alguien lo abre a mitad de partida. Los Riot ID no salen del PC, solo el hash.

- Al entrar o salir un compañero aparece un aviso en la superposición; el icono de la bandeja
  dice con cuántos se está compartiendo.
- **Compartir timers** (menú de la bandeja, activado por defecto) lo desactiva.
- **Clave de grupo...** (opcional): con clave, solo se comparte con quien ponga la misma, así
  nadie de fuera puede calcular la sala.
- Si se corta la conexión, Summs se reconecta solo y vuelve a enviar sus timers.

El servidor (`Summs.Server`, ASP.NET Core + SignalR) solo reparte los cambios entre los de la
misma sala y los guarda en memoria hasta 2 minutos después de que salga el último. Para
probar con un servidor local, añade `"SyncServer": "http://localhost:5000"` a `settings.json`.

La configuración se guarda en `%LOCALAPPDATA%\Summs\settings.json`.

### Datos

- Partida: API local del cliente (`https://127.0.0.1:2999/liveclientdata/allgamedata`).
- Enfriamientos, nombres de hechizos, objetos con aceleración e iconos: Data Dragon
  (`ddragon.leagueoflegends.com`), cacheados en `%LOCALAPPDATA%\Summs\ddragon`. Sin
  internet se usa el último parche descargado o valores fijos.

No se usa el portapapeles porque el chat del juego no acepta pegar texto copiado
fuera del juego, ni envía al portapapeles de Windows lo que se copia en él.

Registro de diagnóstico: `%LOCALAPPDATA%\Summs\summs.log`.

## Versión de práctica

`SummsPractica.exe` sirve para probar atajos y superposición en la **Herramienta de
práctica**, donde la API no da enemigos ni roles. Usa el tiempo real de la partida pero una
alineación enemiga fija, y divide los enfriamientos entre 4 (Flash 75 s) para ver pronto
el aviso de 60 s y la vuelta de los hechizos:

| Rol | Campeón | Hechizo 1 (Ctrl) | Hechizo 2 (Shift) |
|-----|---------|------------------|-------------------|
| TOP | Darius  | Flash            | Teleport          |
| JG  | Lee Sin | Flash            | Smite             |
| MID | Syndra (Inspiración) | Flash | Ignite          |
| ADC | Jinx (Botas de Lucidez, Inspiración) | Heal | Flash |
| SUP | Thresh  | Flash            | Exhaust           |

Las dos versiones no pueden ejecutarse a la vez.

Para probar **Compartir timers**, la versión de práctica entra siempre en una sala de prueba
fija (más la clave de grupo, si hay), así que dos personas, cada una en su Herramienta de
práctica con `SummsPractica.exe`, ven los timers del otro. Como cada una tiene su propio reloj
de partida, en esta versión los timers viajan con la hora del PC (sincronizada por Windows).

## Compilar

Requiere el SDK de .NET 8.

```
dotnet publish Summs -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
dotnet publish Summs -c Practice -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```

Los ejecutables quedan en `dist\Summs.exe` (partidas reales) y `dist\SummsPractica.exe`.
Necesitan el .NET 8 Desktop Runtime instalado.

Servidor de sincronización (Railway, con el `Dockerfile` de la carpeta):

```
cd Summs.Server
railway up
```

Su URL pública va en `SyncClient.DefaultServer`.

Para compartir, versión autocontenida (incluye .NET, ~68 MB cada uno, no requiere instalar nada):

```
dotnet publish Summs -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist\compartir
dotnet publish Summs -c Practice -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist\compartir
```
