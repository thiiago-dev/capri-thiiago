# Capri Signal

Capri avisa en el escritorio cuando un agente de programación (Claude Code, OpenAI Codex, Cursor o Google Antigravity) **termina su turno**, **necesita una respuesta**, **pide autorización** o **falla**.

> Solo funciona en la versión **Avalonia** de Capri (`cross/`). La versión WinForms (`app/`) no se ha tocado.

## Cómo funciona

```
herramienta (hook) ──stdin JSON──▶ signal/capri-signal.ps1 ──HTTP 127.0.0.1──▶ Capri
                                                                   │
              SignalReceiver → SignalRouter (adaptador por herramienta) → NotificationManager
                                                                   │
                                   tarjeta en el escritorio · tono · reacción de Capri
```

| Pieza | Archivo | Qué hace |
|---|---|---|
| Modelo | `cross/Signal/SignalEvent.cs` | Evento normalizado con tipo, herramienta, sesión, turno, proyecto, ruta y mensaje |
| Adaptadores | `cross/Signal/SignalAdapters.cs` | Traducen el JSON de cada herramienta al modelo común |
| Router | `cross/Signal/SignalRouter.cs` | Valida, elige el adaptador y sanea los datos (longitudes, caracteres) |
| Receptor | `cross/Signal/SignalReceiver.cs` | Servidor HTTP mínimo **solo en 127.0.0.1**, con token, límite de tamaño, límite de peticiones y detección de conflicto de puerto. Se ejecuta en segundo plano |
| Cola | `cross/Signal/NotificationManager.cs` | Elimina duplicados, une los eventos de una misma tarea en una tarjeta, ordena por urgencia y limita los sonidos |
| UI | `cross/Signal/SignalCards.cs` | Tarjetas provisionales nativas de Avalonia (sin imágenes) |
| Sonido | `cross/Signal/SignalSound.cs` | Tono provisional sintetizado (WAV), con volumen y duración máxima |
| Preferencias | `cross/Signal/SignalSettings.cs` | `%APPDATA%\Capri\signal.json` |
| Integración | `cross/Signal/SignalHub.cs`, `Program.cs`, `Pet.cs` | Conecta todo, añade los menús y llama a `Pet.Alert()` |
| Auto-prueba | `cross/Signal/SignalSelfTest.cs` | `Capri.exe --signal-selftest <carpeta>` |

### Tipos de evento

| Tipo | Significado | Tarjeta |
|---|---|---|
| `task.started` | El agente ha empezado | Solo se muestra si activas "Avisar también al empezar" |
| `task.turn_ended` | El agente **ha terminado su turno**. Eso no garantiza que el código funcione | Cian. Se oculta sola a los 25 s |
| `task.completed` | La herramienta dice explícitamente que la tarea está completada | Cian. Se oculta sola |
| `task.needs_input` | El agente espera tu respuesta | Magenta. Se queda hasta que la descartes |
| `task.permission_required` | Tienes que autorizar una acción | Magenta. Se queda |
| `task.failed` | Error | Roja, con un tono distinto. Se queda |

Cada tarjeta tiene **VER** (muestra el mensaje completo, el tipo, la sesión y la ruta), **ABRIR CARPETA** (solo si la ruta es una carpeta local que existe) y **DESCARTAR**. Capri nunca ejecuta nada que llegue en un evento.

## Puesta en marcha

1. Haz doble clic en `signal\Build-And-Test.cmd`. Compila y ejecuta la auto-prueba; el resultado queda en `signal\build-log.txt`.
2. Haz doble clic en `signal\Run-Capri.cmd`. Al arrancar, Capri crea `%APPDATA%\Capri\signal.json` con un token aleatorio y el puerto que está usando.
3. Haz la prueba desde PowerShell:

```powershell
cd "$HOME\Desktop\Capri\capri-thiiago\signal"
powershell -ExecutionPolicy Bypass -File .\Send-CapriTest.ps1                       # Codex termina su turno
powershell -ExecutionPolicy Bypass -File .\Send-CapriTest.ps1 -Type task.failed -Source claude-code -Message "Build roto"
powershell -ExecutionPolicy Bypass -File .\Send-CapriTest.ps1 -Type task.needs_input -Source cursor
```

4. Para la batería completa (necesita Capri abierto), haz doble clic en `signal\Run-E2E-Test.cmd`. El resultado queda en `signal\test-results.txt`.

## Conectar cada herramienta

Por defecto el instalador **solo muestra** los cambios. No escribe nada hasta que añades `-Apply`, y antes de escribir guarda una copia `*.capri-backup-FECHA` del archivo.

```powershell
cd "$HOME\Desktop\Capri\capri-thiiago\signal"
powershell -ExecutionPolicy Bypass -File .\Install-CapriSignal.ps1 -Tool all -Status          # qué hay instalado
powershell -ExecutionPolicy Bypass -File .\Install-CapriSignal.ps1 -Tool claude-code          # vista previa
powershell -ExecutionPolicy Bypass -File .\Install-CapriSignal.ps1 -Tool claude-code -Apply   # instalar
powershell -ExecutionPolicy Bypass -File .\Uninstall-CapriSignal.ps1 -Tool claude-code -Apply # quitar
```

| Herramienta | Archivo | Hooks | Estado |
|---|---|---|---|
| Claude Code | `~\.claude\settings.json` | `UserPromptSubmit`, `Stop`, `StopFailure`, `Notification` (`permission_prompt`, `idle_prompt`, `agent_needs_input`, `agent_completed`, `elicitation_*`) | Adaptador probado con payloads de la documentación. **Falta la prueba real** |
| OpenAI Codex | `~\.codex\hooks.json` | `UserPromptSubmit`, `Stop`, `PermissionRequest` (el hook no decide nada: deja pasar la aprobación). También acepta el payload antiguo de `notify` | Adaptador probado. **Falta la prueba real** |
| Cursor | `~\.cursor\hooks.json` | `stop` (`completed` / `aborted` / `error`). Responde `{}` | Adaptador probado. **Falta la prueba real**. Cursor no tiene un evento de "espera respuesta" ni de "pide permiso" |
| Google Antigravity | `~\.gemini\config\hooks.json` (grupo `capri-signal`) | `Stop`. Responde `{"decision":"stop"}`, como exige la documentación | Adaptador probado. **Falta la prueba real**. No tiene evento de permiso ni de "espera respuesta". `max_steps_exceeded` se muestra como "espera instrucciones" |

**Prueba real con una herramienta:** instala el hook, reinicia la herramienta, pídele algo corto (por ejemplo "di hola") y espera a que aparezca la tarjeta. Si no aparece, mira `%APPDATA%\Capri\signal-log.txt`.

Fuentes: [Claude Code hooks](https://code.claude.com/docs/en/hooks) · [Codex hooks](https://learn.chatgpt.com/docs/hooks) · [Cursor hooks](https://cursor.com/docs/hooks) · [Antigravity hooks](https://antigravity.google/docs/hooks/)

## Preferencias

Están en el menú contextual de Capri (**Capri Signal**), en el icono de la bandeja y en `%APPDATA%\Capri\signal.json`.

| Clave | Por defecto | Qué hace |
|---|---|---|
| `enabled` | `true` | Activa o desactiva el receptor y las tarjetas |
| `sound` | `true` | Tono |
| `silent` | `false` | Modo silencioso: muestra tarjetas, pero sin sonido ni animación |
| `volume` | `70` | 0–100. Solo afecta al audio de Capri; nunca pausa ni baja otro audio |
| `soundSeconds` | `4` | Duración máxima del tono |
| `soundCooldownSeconds` | `8` | Separación mínima entre tonos, salvo que una tarea pase a necesitarte |
| `soundFile` | `null` | Ruta a un `.wav` propio |
| `maxVisible` | `3` | Tarjetas visibles a la vez. El resto espera en cola |
| `showStarted` | `false` | Muestra una tarjeta al empezar |
| `autoHideSeconds` | `25` | Tiempo antes de ocultar las tarjetas de turno terminado o completado |
| `dedupSeconds` | `30` | Ventana para descartar duplicados |
| `port` | `47615` | Puerto preferido. Si está ocupado, usa el siguiente libre (hasta +9) |
| `token`, `activePort` | — | Los genera Capri y los leen los scripts. No los compartas |

## API local (para otras herramientas)

```
POST http://127.0.0.1:<activePort>/v1/event
X-Capri-Token: <token>
{"type":"task.turn_ended","source":"mi-herramienta","task_id":"abc","project":"Capri","message":"Listo","project_path":"C:\\ruta","event_id":"opcional"}
```

Solo son obligatorios `type` y `source`. Para los hooks crudos se usa `POST /v1/hook/{claude-code|codex|cursor|antigravity}?event=Nombre`. `GET /v1/health` no necesita token.

Respuestas: `200` (`shown`, `updated`, `duplicate`, `disabled` o `logged`), `202` (ignorado), `400` (malformado), `401` (token incorrecto), `403` (petición de navegador o Host extraño), `413` (cuerpo de más de 32 KB) y `429` (más de 40 peticiones en 10 s).

## Diagnóstico

| Síntoma | Qué mirar |
|---|---|
| No sale nada | ¿Capri está abierto? Mira el menú **Capri Signal** (debe decir "escuchando en 127.0.0.1:…") y `activePort` en `signal.json` |
| `401` | Has borrado o regenerado el token: reinicia Capri |
| La herramienta no llama al hook | `Install-CapriSignal.ps1 -Tool all -Status`, reinicia la herramienta. En Codex, revisa que no haya `[features] hooks = false` |
| PowerShell bloquea el script | Los hooks se lanzan con `-ExecutionPolicy Bypass`, solo para ese proceso |
| Registro | `%APPDATA%\Capri\signal-log.txt`: solo metadatos, sin token, mensajes ni rutas |

## Reversión

1. `Uninstall-CapriSignal.ps1 -Tool all -Apply`, o restaura los `*.capri-backup-*`.
2. Para quitar el código: `git checkout -- cross/Pet.cs cross/Program.cs .gitignore` y borra `cross/Signal/` y `signal/`.
3. Para borrar las preferencias: elimina `%APPDATA%\Capri\signal*.json`, `signal-log.txt` y `signal-*.wav`.

## Sprites que podremos añadir después

Todo lo visual está en `SignalCards.cs` y `Pet.Alert()`, así que se puede sustituir sin tocar el resto.

- `call_incoming`: Capri recibe una llamada (bucle mientras suena)
- `call_answer`: responde o mira la tarjeta
- `call_hangup`: vuelve a la animación normal
- `call_waiting`: espera tu respuesta (bucle, tono magenta)
- `call_permission`: levanta la mano o pide permiso
- `call_error`: glitch rojo o error
- Variantes sentadas (`perch_call_*`) para cuando está en una ventana
- Opcional: icono holográfico por herramienta y tono de llamada definitivo (`.wav`)
