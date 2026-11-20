// A tiny fake OBS Studio: speaks obs-websocket v5 (JSON over WebSocket, with the password challenge) so tests need no real OBS.
import http from 'node:http';
import crypto from 'node:crypto';

export function startFakeObs(port, password) {
  const st = {
    scene: 'Gameplay', scenes: ['Starting Soon', 'Gameplay', 'Chatting'], streaming: false, recording: false, paused: false, vcam: false,
    inputs: { 'Mic/Aux': { mute: false, mul: 1 }, 'Desktop Audio': { mute: false, mul: 0.125 }, 'Logo': null },
    items: {   // stored bottom -> top, like OBS
      'Starting Soon': [{ id: 1, name: 'Banner', enabled: true, locked: false }],
      'Gameplay': [{ id: 1, name: 'Game Capture', enabled: true, locked: false }, { id: 2, name: 'Webcam', enabled: true, locked: false }, { id: 3, name: 'Overlay', enabled: true, locked: true }],
      'Chatting': [{ id: 1, name: 'Webcam', enabled: true, locked: false }],
    },
    studio: false, preview: 'Starting Soon', transitions: ['Cut', 'Fade', 'Slide'], transition: 'Fade', transitionMs: 300, replay: false, replaySaved: 0,
    received: [], clients: new Set(), identified: 0, badLogins: 0,
  };
  const salt = 'saltySALT123', challenge = 'challengeXYZ789';
  const sha = s => crypto.createHash('sha256').update(s).digest();
  const expected = sha(Buffer.from(sha(password + salt).toString('base64') + challenge)).toString('base64');

  const enc = text => { const p = Buffer.from(text); let h; if (p.length < 126) h = Buffer.from([0x81, p.length]); else if (p.length < 65536) h = Buffer.from([0x81, 126, p.length >> 8, p.length & 255]); else { h = Buffer.alloc(10); h[0] = 0x81; h[1] = 127; h.writeBigUInt64BE(BigInt(p.length), 2); } return Buffer.concat([h, p]); };
  const server = http.createServer();
  server.on('upgrade', (req, socket) => {
    const accept = crypto.createHash('sha1').update(req.headers['sec-websocket-key'] + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
    socket.write('HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ' + accept + '\r\nSec-WebSocket-Protocol: obswebsocket.json\r\n\r\n');
    const send = o => socket.write(enc(JSON.stringify(o)));
    const ev = (eventType, eventData) => send({ op: 5, d: { eventType, eventIntent: 1, eventData } });
    socket.send = send; socket.ev = ev; socket.ok = false;
    st.clients.add(socket); socket.on('close', () => st.clients.delete(socket)); socket.on('error', () => {});
    send({ op: 0, d: { obsWebSocketVersion: '5.5.0', rpcVersion: 1, authentication: { challenge, salt } } });
    let buf = Buffer.alloc(0);
    socket.on('data', d => {
      buf = Buffer.concat([buf, d]);
      while (buf.length >= 2) {
        const opc = buf[0] & 15; let len = buf[1] & 127, off = 2;
        if (len === 126) { if (buf.length < 4) return; len = buf.readUInt16BE(2); off = 4; } else if (len === 127) { if (buf.length < 10) return; len = Number(buf.readBigUInt64BE(2)); off = 10; }
        const masked = (buf[1] & 128) !== 0; if (buf.length < off + (masked ? 4 : 0) + len) return;
        const mask = masked ? buf.subarray(off, off + 4) : null; if (masked) off += 4;
        const payload = Buffer.from(buf.subarray(off, off + len)); if (mask) for (let i = 0; i < payload.length; i++) payload[i] ^= mask[i % 4];
        buf = buf.subarray(off + len);
        if (opc === 8) { socket.end(); return; } if (opc !== 1) continue;
        handle(socket, JSON.parse(payload.toString()));
      }
    });
  });
  const res = (s, d, data, ok = true, code = 100, comment) => s.send({ op: 7, d: { requestType: d.requestType, requestId: d.requestId, requestStatus: ok ? { result: true, code } : { result: false, code, comment }, responseData: data } });
  const bcast = (e, data) => st.clients.forEach(c => c.ok && c.ev(e, data));
  function handle(s, m) {
    if (m.op === 1) {
      if (m.d.authentication !== expected) { st.badLogins++; s.destroy(); return; }
      s.ok = true; st.identified++; s.send({ op: 2, d: { negotiatedRpcVersion: 1 } }); return;
    }
    if (m.op !== 6 || !s.ok) return;
    const d = m.d, a = d.requestData || {}; st.received.push({ type: d.requestType, data: a });
    switch (d.requestType) {
      case 'GetSceneList': return res(s, d, { currentProgramSceneName: st.scene, scenes: [...st.scenes].reverse().map((n, i) => ({ sceneName: n, sceneIndex: i })) });
      case 'SetCurrentProgramScene': st.scene = a.sceneName; res(s, d, {}); return bcast('CurrentProgramSceneChanged', { sceneName: st.scene });
      case 'GetStreamStatus': return res(s, d, { outputActive: st.streaming, outputTimecode: st.streaming ? '00:01:23.456' : '00:00:00.000' });
      case 'GetRecordStatus': return res(s, d, { outputActive: st.recording, outputPaused: st.paused, outputTimecode: st.recording ? '00:00:42.100' : '00:00:00.000' });
      case 'GetVirtualCamStatus': return res(s, d, { outputActive: st.vcam });
      case 'ToggleStream': st.streaming = !st.streaming; res(s, d, { outputActive: st.streaming }); return bcast('StreamStateChanged', { outputActive: st.streaming, outputState: st.streaming ? 'OBS_WEBSOCKET_OUTPUT_STARTED' : 'OBS_WEBSOCKET_OUTPUT_STOPPED' });
      case 'ToggleRecord': st.recording = !st.recording; st.paused = false; res(s, d, { outputActive: st.recording }); return bcast('RecordStateChanged', { outputActive: st.recording, outputState: st.recording ? 'OBS_WEBSOCKET_OUTPUT_STARTED' : 'OBS_WEBSOCKET_OUTPUT_STOPPED' });
      case 'ToggleRecordPause': st.paused = !st.paused; res(s, d, { outputPaused: st.paused }); return bcast('RecordStateChanged', { outputActive: st.recording, outputState: st.paused ? 'OBS_WEBSOCKET_OUTPUT_PAUSED' : 'OBS_WEBSOCKET_OUTPUT_RESUMED' });
      case 'ToggleVirtualCam': st.vcam = !st.vcam; res(s, d, { outputActive: st.vcam }); return bcast('VirtualcamStateChanged', { outputActive: st.vcam });
      case 'GetInputList': return res(s, d, { inputs: Object.keys(st.inputs).map(n => ({ inputName: n, inputKind: 'x' })) });
      case 'GetInputMute': return st.inputs[a.inputName] ? res(s, d, { inputMuted: st.inputs[a.inputName].mute }) : res(s, d, undefined, false, 604, 'Not an audio source');
      case 'GetInputVolume': return st.inputs[a.inputName] ? res(s, d, { inputVolumeMul: st.inputs[a.inputName].mul, inputVolumeDb: 0 }) : res(s, d, undefined, false, 604, 'Not an audio source');
      case 'ToggleInputMute': { const i = st.inputs[a.inputName]; if (!i) return res(s, d, undefined, false, 600, 'No such input'); i.mute = !i.mute; res(s, d, { inputMuted: i.mute }); return bcast('InputMuteStateChanged', { inputName: a.inputName, inputMuted: i.mute }); }
      case 'SetInputVolume': { const i = st.inputs[a.inputName]; if (!i) return res(s, d, undefined, false, 600, 'No such input'); i.mul = a.inputVolumeMul; res(s, d, {}); return bcast('InputVolumeChanged', { inputName: a.inputName, inputVolumeMul: i.mul, inputVolumeDb: 0 }); }
      case 'GetSceneItemList': return st.items[a.sceneName] ? res(s, d, { sceneItems: st.items[a.sceneName].map((i, n) => ({ sceneItemId: i.id, sourceName: i.name, sceneItemEnabled: i.enabled, sceneItemLocked: i.locked, sceneItemIndex: n, isGroup: false, sourceType: 'OBS_SOURCE_TYPE_INPUT', inputKind: 'x' })) }) : res(s, d, undefined, false, 600, 'No such scene');
      case 'GetSceneItemId': { const i = (st.items[a.sceneName] || []).find(x => x.name === a.sourceName); return i ? res(s, d, { sceneItemId: i.id }) : res(s, d, undefined, false, 600, 'No such source in scene'); }
      case 'GetSceneItemEnabled': { const i = (st.items[a.sceneName] || []).find(x => x.id === a.sceneItemId); return i ? res(s, d, { sceneItemEnabled: i.enabled }) : res(s, d, undefined, false, 600, 'No such item'); }
      case 'SetSceneItemEnabled': { const i = (st.items[a.sceneName] || []).find(x => x.id === a.sceneItemId); if (!i) return res(s, d, undefined, false, 600, 'No such item'); i.enabled = a.sceneItemEnabled; res(s, d, {}); return bcast('SceneItemEnableStateChanged', { sceneName: a.sceneName, sceneItemId: i.id, sceneItemEnabled: i.enabled }); }
      case 'SetSceneItemLocked': { const i = (st.items[a.sceneName] || []).find(x => x.id === a.sceneItemId); if (!i) return res(s, d, undefined, false, 600, 'No such item'); i.locked = a.sceneItemLocked; res(s, d, {}); return bcast('SceneItemLockStateChanged', { sceneName: a.sceneName, sceneItemId: i.id, sceneItemLocked: i.locked }); }
      case 'GetStudioModeEnabled': return res(s, d, { studioModeEnabled: st.studio });
      case 'SetStudioModeEnabled': st.studio = a.studioModeEnabled; res(s, d, {}); return bcast('StudioModeStateChanged', { studioModeEnabled: st.studio });
      case 'GetCurrentPreviewScene': return st.studio ? res(s, d, { currentPreviewSceneName: st.preview }) : res(s, d, undefined, false, 506, 'Studio mode not active');
      case 'SetCurrentPreviewScene': st.preview = a.sceneName; res(s, d, {}); return bcast('CurrentPreviewSceneChanged', { sceneName: st.preview });
      case 'TriggerStudioModeTransition': { const old = st.scene; st.scene = st.preview; st.preview = old; res(s, d, {}); bcast('CurrentProgramSceneChanged', { sceneName: st.scene }); return bcast('CurrentPreviewSceneChanged', { sceneName: st.preview }); }
      case 'GetSceneTransitionList': return res(s, d, { currentSceneTransitionName: st.transition, transitions: st.transitions.map(n => ({ transitionName: n })) });
      case 'GetCurrentSceneTransition': return res(s, d, { transitionName: st.transition, transitionDuration: st.transitionMs });
      case 'SetCurrentSceneTransition': st.transition = a.transitionName; res(s, d, {}); return bcast('CurrentSceneTransitionChanged', { transitionName: st.transition });
      case 'SetCurrentSceneTransitionDuration': st.transitionMs = a.transitionDuration; res(s, d, {}); return bcast('CurrentSceneTransitionDurationChanged', { transitionDuration: st.transitionMs });
      case 'GetReplayBufferStatus': return res(s, d, { outputActive: st.replay });
      case 'ToggleReplayBuffer': st.replay = !st.replay; res(s, d, { outputActive: st.replay }); return bcast('ReplayBufferStateChanged', { outputActive: st.replay, outputState: st.replay ? 'OBS_WEBSOCKET_OUTPUT_STARTED' : 'OBS_WEBSOCKET_OUTPUT_STOPPED' });
      case 'SaveReplayBuffer': st.replaySaved++; return res(s, d, {});
      default: return res(s, d, {});
    }
  }
  const addScene = name => { st.scenes.push(name); st.items[name] = []; bcast('SceneListChanged', {}); };
  return new Promise(r => server.listen(port, '127.0.0.1', () => r({ st, bcast, addScene, close: () => { st.clients.forEach(c => c.destroy()); server.close(); } })));
}
