# YARP UI

[![NuGet](https://img.shields.io/nuget/v/YA-RP-UI.svg)](https://www.nuget.org/packages/YA-RP-UI)
[![downloads](https://img.shields.io/nuget/dt/YA-RP-UI.svg)](https://www.nuget.org/packages/YA-RP-UI)
[![docker](https://img.shields.io/docker/v/amrfswalha/yarp-ui.svg?label=docker)](https://hub.docker.com/r/amrfswalha/yarp-ui)
[![docker pulls](https://img.shields.io/docker/pulls/amrfswalha/yarp-ui.svg?label=docker%20pulls)](https://hub.docker.com/r/amrfswalha/yarp-ui)
[![tests](https://img.shields.io/badge/tests-92%20passed%20%202%20skipped-success)](https://github.com/advanced-software-solutions/yarp-ui/actions/workflows/build.yml)

> 🌐 Idiomas: [English](README.md) | [العربية](README.ar.md) | **Español** | [简体中文](README.zh-CN.md)

Una interfaz de gestión para [YARP](https://microsoft.github.io/reverse-proxy/) (Yet Another Reverse Proxy). Una sola aplicación que es **a la vez** un proxy inverso funcional y su sala de control:

- **Mapa de rutas** (`/`) — cada ruta → clúster → destino se representa como un gráfico interactivo. Haz clic en un nodo para trazar su cadena completa e inspeccionar su configuración; busca para resaltar coincidencias.
- **Editor** (`/editor`) — crea, edita y elimina rutas, clústeres y destinos. Al guardar se valida la configuración, se aplica al proxy en ejecución **sin reiniciar** y se persiste en disco.
- **Registros** (`/logs`) — una vista en vivo de las solicitudes proxificadas (método, ruta de acceso, estado, duración, IP del cliente, ruta, clúster y destino elegidos), las más recientes primero y con columnas ordenables. Los filtros de ruta/clúster/destino y el selector de intervalo de tiempo buscan en todo el historial retenido, no solo en el búfer en vivo. Además, un panel de rendimiento: duraciones por solicitud graficadas en el tiempo y coloreadas por clase de estado, tarjetas de estadísticas (media/P95/máximo/tasa de error) y agregados por ruta.

> **Ediciones** — este repositorio es la **edición comunitaria**, gratuita bajo Apache-2.0. Una edición premium separada añade funciones comerciales por encima y se distribuye bajo una licencia comercial. El código premium nunca vive en este repositorio.

## Documentación

Las guías completas están en la [wiki del proyecto](https://github.com/advanced-software-solutions/yarp-ui/wiki):

- [Primeros pasos](https://github.com/advanced-software-solutions/yarp-ui/wiki/Getting-Started)
- [Modos de hospedaje](https://github.com/advanced-software-solutions/yarp-ui/wiki/Hosting-Modes)
- [Configuración](https://github.com/advanced-software-solutions/yarp-ui/wiki/Configuration)
- [API REST](https://github.com/advanced-software-solutions/yarp-ui/wiki/REST-API)
- [Docker](https://github.com/advanced-software-solutions/yarp-ui/wiki/Docker)

## Modos de hospedaje

La interfaz se distribuye como una biblioteca de clases Razor (paquete NuGet [**YA-RP-UI**](https://www.nuget.org/packages/YA-RP-UI)) y puede hospedarse de tres maneras:

**1. Ejecutable independiente** — `YARPUI.Host` es un host ligero que ejecuta el proxy y la interfaz de gestión en una sola aplicación:

```bash
cd YARPUI.Host && dotnet run      # → http://localhost:5080
```

**2. Incrustada en tu propia aplicación** — añade el paquete y conéctalo (ver `samples/EmbeddedHost`):

```xml
<PackageReference Include="YA-RP-UI" Version="0.2.1" />
```

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddYarpUi();               // configuración del proxy, servicios, autenticación, Razor Pages

var app = builder.Build();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseYarpUiRequestLogging();     // registra las solicitudes proxificadas para la página de registros
app.MapYarpUi();                   // las páginas de la interfaz + /api/yarp/*
app.MapReverseProxy();             // el proxy en sí (público)
app.Run();
```

**3. Adjunta a una aplicación que ya configura YARP** — para puertas de enlace con sus propios `LoadFromConfig`/proveedores personalizados, transformaciones y filtros. La interfaz muestra toda la configuración viva de la aplicación **y puede editarla**: al guardar, cada cambio se escribe de vuelta en el archivo `appsettings.json` del que provenía la ruta o el clúster, y YARP recarga el archivo en caliente — las modificaciones surten efecto sin reiniciar, mientras que el código de la aplicación (transformaciones, middleware, pipeline personalizado) permanece intacto:

```csharp
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddTransforms(...);            // tu trabajo personalizado sigue mandando por completo

builder.AttachYarpUi();            // sin registro del proxy ni siembra de configuración

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseYarpUiRequestLogging();
app.MapReverseProxy();
app.MapYarpUi();
```

Comportamiento de la edición con escritura de vuelta:

- **Las ediciones se fusionan con los nodos JSON existentes** — los campos que el editor no modela (p. ej. `RateLimiterPolicy` o claves personalizadas) conservan sus valores; el contenido no relacionado del archivo se preserva.
- Las **rutas/clústeres nuevos** se añaden a `appsettings.json`; los **eliminados** se quitan de todos los archivos appsettings que los definan (incluidas las anulaciones por entorno).
- **Copias de seguridad**: la primera vez que la interfaz modifica un archivo, se conserva una copia `.yarpui.bak` junto a él; *Restaurar copia de seguridad de appsettings* revierte todos los archivos modificados.
- Los elementos que provienen de una **fuente que no es un archivo** (un `IProxyConfigProvider` personalizado respaldado por una base de datos, código, etc.) se muestran bloqueados y de solo lectura — no hay archivo al que escribir.
- Las rarezas preexistentes de la configuración (p. ej. una ruta que referencia a un clúster inexistente) no bloquean los guardados; solo se rechazan los problemas que la propia edición introduce.

Todos los modos leen la misma configuración (credenciales de `YarpUi:Auth`) y admiten `YarpUi:DataDirectory` para la persistencia respaldada por volúmenes. La interfaz autentica con su propio esquema de cookies (`YarpUi.Auth`) y nunca cambia el esquema de autenticación predeterminado del host, por lo que es segura junto a una configuración JWT/cookies existente de la aplicación.

## Inicio rápido

```bash
dotnet run
```

Abre http://localhost:5080 e inicia sesión. Credenciales predeterminadas (¡cámbialas!):

| Ajuste | Valor |
| --- | --- |
| Usuario | `admin` |
| Contraseña | `yarp-admin` |

Ambas se configuran en `appsettings.json` bajo `YarpUi:Auth`.

## Docker

Las imágenes precompiladas se publican en [Docker Hub (`amrfswalha/yarp-ui`)](https://hub.docker.com/r/amrfswalha/yarp-ui):

```bash
docker run -d -p 8090:8080 -v yarp-ui-data:/app/data amrfswalha/yarp-ui:latest
```

O construye desde el código fuente con la plantilla `docker-compose.yml` que se distribuye junto a la solución:

```bash
docker compose up -d --build
```

La interfaz se sirve entonces en **http://localhost:8090**. Toda la configuración mutable se persiste en un volumen en `./docker-data`, de modo que sobrevive a `docker compose down`:

| Archivo | Propósito |
| --- | --- |
| `docker-data/appsettings.json` | Credenciales (`YarpUi:Auth`) y la configuración semilla `ReverseProxy` — edítala en el host y se aplica en el siguiente arranque |
| `docker-data/yarp-ui.routes.json` | Se escribe automáticamente en cada guardado desde el editor de la interfaz |
| `docker-data/yarp-ui-logs.db` | Base de datos del registro de solicitudes (SQLite) — sobrevive a los reinicios y se depura según la política de retención |

Bajo el capó, el contenedor define `YarpUi__DataDirectory=/app/data` y monta el volumen allí; un `appsettings.json` en ese directorio prevalece sobre el incluido en la imagen (esto también funciona sin Docker — apunta `YarpUi:DataDirectory` a donde quieras). Para construir la imagen manualmente: `docker build -t yarp-ui:0.2.1 .` desde la raíz de la solución.

## IIS

Hospedar bajo IIS funciona con la identidad predeterminada del grupo de aplicaciones (`ApplicationPoolIdentity`), que tiene acceso de **solo lectura** a la carpeta del sitio. Al arrancar, YARP UI detecta que la raíz de contenido no es escribible y almacena todo el estado mutable — `yarp-ui-logs.db`, `yarp-ui.routes.json`, el `appsettings.json` opcional del directorio de datos — bajo `%ProgramData%\YarpUi\<nombre de la aplicación>` en su lugar, registrando una advertencia para que la reubicación sea visible. No hay que configurar nada; el proxy arranca con normalidad y las páginas del editor y de registros funcionan contra la carpeta de respaldo.

Para mantener el estado en una ubicación de tu elección, apunta `YarpUi:DataDirectory` a una carpeta escribible, o concede a la identidad del grupo de aplicaciones acceso de escritura a la carpeta del sitio:

```powershell
icacls "<site folder>" /grant "IIS AppPool\<YourAppPool>:(OI)(CI)(M)"
```

Un `YarpUi:DataDirectory` configurado explícitamente nunca lo anula el mecanismo de respaldo. La propia ubicación de respaldo puede redirigirse con `YarpUi:FallbackDataDirectory`.

## Cómo funciona la configuración

```
appsettings.json ("ReverseProxy" section)   ← hand-written seed
                │
                ▼  startup
   yarp-ui.routes.json (if present)         ← takes precedence once it exists
                │
                ▼
   InMemoryConfigProvider (live YARP config)
```

- Al arrancar, la aplicación carga `yarp-ui.routes.json` si existe; de lo contrario lee la sección `ReverseProxy` de `appsettings.json`.
- El primer **guardado** en el editor escribe la configuración completa en `yarp-ui.routes.json`. A partir de ese momento, ese archivo es la fuente de verdad — `appsettings.json` queda intacto.
- **Restablecer a appsettings.json** (editor, abajo a la izquierda) elimina el archivo gestionado por la interfaz y vuelve a la configuración semilla.
- Los guardados se validan con el propio validador de configuración de YARP; las configuraciones no válidas se rechazan y el proxy sigue ejecutándose con la última configuración buena.

## Registros de solicitudes

Solo se registran las solicitudes **proxificadas** (las solicitudes de la interfaz/API quedan excluidas). Las entradas se almacenan en una base de datos SQLite (`yarp-ui-logs.db` en el directorio de datos, junto a `yarp-ui.routes.json`) y sobreviven a los reinicios. Cada entrada captura el método, la ruta de acceso, el código de estado, la duración, la ruta/clúster/destino que YARP seleccionó y la IP del cliente. Las bases de datos creadas por versiones anteriores se migran en el sitio en el primer arranque.

La **IP del cliente** es la entrada más a la izquierda de `X-Forwarded-For` cuando la proporcionó un proxy frontal; de lo contrario, la dirección de conexión directa. La interfaz no instala por sí misma el middleware ForwardedHeaders — si toda la aplicación está detrás de un balanceador de carga, el encabezado refleja lo que ese proxy reenvió. Dado que `X-Forwarded-For` es controlable por quien llama, trata las IP registradas como informativas y no como autenticadas.

La página de Registros muestra las entradas **más recientes primero** y todas las columnas son ordenables. Los filtros de ruta / clúster / destino y el selector de intervalo de tiempo (últimos 15 min … 7 días, un rango personalizado o todo el historial) ejecutan una **búsqueda del lado del servidor sobre todo el historial retenido** mediante `GET /api/yarp/logs` con `from`/`to` (milisegundos Unix), `routeId`, `clusterId`, `destinationId`, `sort`, `desc` y `limit` (máx. 1000 por consulta). Sin parámetros de búsqueda, el endpoint mantiene su contrato de seguimiento en vivo: `after=<seq>` transmite las entradas nuevas de la más antigua a la más reciente. El filtrado de texto libre y por clase de estado se aplica sobre lo que esté cargado.

Una **política de retención** elimina registros automáticamente al superar cierta antigüedad: una tarea en segundo plano se ejecuta al arrancar y luego cada hora. La política se gestiona desde la barra de herramientas de la página de Registros (*Conservar registros: para siempre / 1 / 7 / 30 / 90 / 365 días*) y los cambios se aplican de inmediato; el valor predeterminado inicial proviene de `YarpUi:Logs:RetentionDays` en la configuración (30 días si no se establece). La política que definas en la interfaz se almacena en la propia base de datos y prevalece sobre el valor de configuración.

## Localización

La interfaz se distribuye en **inglés** (predeterminado), **árabe** (de derecha a izquierda), **español** y **chino simplificado**. La cultura de una solicitud se resuelve en este orden: la cadena de consulta `?culture=`, la cookie de cultura estándar de ASP.NET Core, el encabezado `Accept-Language` del navegador y, por último, la predeterminada. El selector de idioma de la barra superior (y de la página de inicio de sesión) escribe esa cookie y recarga.

El host no necesita ningún cableado: el paquete inserta su propio middleware de localización limitado a las rutas de la interfaz (`/login`, las páginas de la interfaz, `/api/yarp/*`), por lo que las aplicaciones host nunca necesitan llamar a `UseRequestLocalization` y sus propias páginas conservan el comportamiento de cultura que tengan configurado.

Dos opciones controlan el conjunto de idiomas (en `appsettings.json`):

| Opción | Predeterminado | Significado |
| --- | --- | --- |
| `YarpUi:Cultures` | `en,ar,es,zh-Hans,zh-CN` | Culturas separadas por comas en las que la interfaz puede responder |
| `YarpUi:DefaultCulture` | `en` | Cultura usada cuando una solicitud no coincide con ninguna admitida |

`zh-CN` se acepta como alias de `zh-Hans` (los navegadores envían la etiqueta regional); las culturas no admitidas vuelven a la predeterminada. Los errores de validación — tanto de la API de gestión como de las comprobaciones de configuración del editor — se localizan con la misma cultura de la solicitud.

## Sin conexión / sin red

Todas las bibliotecas de JavaScript (Cytoscape.js, dagre, cytoscape-dagre, Chart.js) se incluyen localmente bajo `wwwroot/lib/`. No se usa ningún CDN en tiempo de ejecución; la interfaz funciona completamente sin conexión.

## Notas de seguridad

- La interfaz de gestión requiere inicio de sesión (autenticación por cookies). **Las rutas del proxy en sí son públicas** — ese es precisamente el propósito de un proxy.
- Las credenciales están en texto plano en `appsettings.json`, lo cual está bien para una herramienta local/interna. Si expones esta aplicación más allá de localhost, ponla detrás de HTTPS, usa credenciales robustas y considera ampliar la autenticación con contraseñas con hash o un proveedor de identidad real.
- Sirve por HTTP solo en una red de confianza; la cookie no está marcada como `Secure` para que también funcione sobre HTTP plano durante el desarrollo.

## Licencia

Copyright 2026 Los autores de YARP UI.

Licenciado bajo la [Licencia Apache, Versión 2.0](LICENSE). Esta es la edición comunitaria de YARP UI; la edición premium se licencia por separado y se distribuye desde su propio repositorio.

«YARP UI» y el logotipo de YARP UI son marcas del proyecto; esta licencia no otorga derechos para usarlas con el fin de comercializar productos derivados.

Las bibliotecas de terceros incluidas (YARP, Microsoft.Data.Sqlite, Cytoscape.js, dagre, cytoscape-dagre, Chart.js) están licenciadas bajo MIT — consulta [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
