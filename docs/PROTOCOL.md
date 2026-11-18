# Protocol

WebSocket, JSON text frames, `ws://<pc>:47800/ws`. Discovery: send UDP `STREAMDECK_DISCOVER` to broadcast port 47801; the PC answers
`{"app":"streamdeck-host","name":"<pc>","port":47800,"v":1}`.

The first message on a connection must be `pair` or `auth`; anything else gets `{"t":"error","error":"not_authenticated"}`.

## Handshake
| Client → PC | PC → client |
|---|---|
| `{"t":"pair","secret":"<from QR>","deviceId":"…","deviceName":"…"}` | `{"t":"paired","token":"…","host":"PC"}` or `{"t":"pair_fail","error":"…"}` |
| `{"t":"auth","deviceId":"…","token":"…"}` | `{"t":"auth_ok","host":"PC"}` or `{"t":"auth_fail"}` |

QR payload: `{"v":1,"n":"<pc name>","p":47800,"h":["192.168.1.20"],"s":"<secret>"}`.

## Requests (authenticated). Include `id` to match the reply.
| Request | Reply |
|---|---|
| `{"t":"ping"}` | `{"t":"pong"}` |
| `{"t":"status","id":1}` | `{"t":"status","id":1,"pc":"…","volume":0-100,"muted":bool,"micMuted":bool\|null,"brightness":0-100\|-1}` (also pushed unprompted on change) |
| `{"t":"action","id":2,"action":{…}}` | `{"t":"result","id":2,"ok":true,"message"?:"…"}` or `{"ok":false,"error":"…"}` |
| `{"t":"apps","id":3,"refresh":false}` | `{"t":"apps","id":3,"apps":[{"id","name","kind":"app\|game\|store","icon":"<base64 png>\|null"}]}` |
| `{"t":"wallpapers","id":4}` | `{"t":"wallpapers","id":4,"items":[{"name","thumb":"<base64 jpg>"}]}` |
| `{"t":"wallpaper_upload","id":5,"name":"x.jpg","data":"<base64>","apply":true}` | `{"t":"result","id":5,"ok":true}` |
| `{"t":"wallpaper_delete","id":6,"name":"x.jpg"}` | `{"t":"result","id":6,"ok":bool}` |

## Actions: `{"type": …, params}`
| type | params |
|---|---|
| `volume_up` / `volume_down` | `step` (default 5) |
| `volume_set` | `value` 0-100 |
| `mute_toggle`, `mute` | `value` (bool, for `mute`) |
| `mic_toggle`, `mic_mute` | `value` (bool, for `mic_mute`) |
| `media` | `key`: `playpause`, `next`, `prev`, `stop` |
| `brightness_up` / `brightness_down` | `step` (default 10) |
| `brightness_set` | `value` 0-100 |
| `hotkey` | `keys`: `"ctrl+shift+esc"`, `"win+d"`, `"alt+f4"`, `f1`–`f24`, arrows, … |
| `text` | `text` (typed with Unicode key events) |
| `app` | `appId` from the apps list |
| `launch` | `target` (path/URI), `args` |
| `url` | `url` |
| `close_app` | `name` (process name without .exe) |
| `shell` | `command` (only if enabled on the PC) |
| `lock`, `sleep`, `logout`, `restart`, `shutdown`, `screenshot` | – |
| `wallpaper` | `name` (uploaded file), or `path`, or `mode`: `next`\|`random` with optional `folder` |
| `delay` | `ms` (max 60000) |
| `macro` | `steps`: array of actions (nesting up to 4) |

Errors are returned in `result.error` (unknown type, bad hotkey, shell disabled, display unsupported, …).
