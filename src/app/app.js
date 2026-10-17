/* Push to Talk — связка интерфейса с нативной частью.
   В приложении работает через WebView2, в браузере включается предпросмотр (?demo=1). */
(() => {
  const $ = (s) => document.querySelector(s);
  const inApp = !!(window.chrome && window.chrome.webview);
  const demo = !inApp && (new URLSearchParams(location.search).has('demo') || !!window.__forceDemo);

  /* ------------------------- тексты (RU / EN) ------------------------- */
  const T = {
    ru: {
      subtitle: 'микрофон по кнопке',
      stateLive: 'Микрофон активен', hintLive: 'Говори — звук захватывается.',
      stateMuted: 'Микрофон заглушён', hintMuted: 'Зажми {key} — и говори, отпустишь — заглушится.',
      hintToggle: 'Нажмите {key} — микрофон включится, нажмите ещё раз — выключится.',
      stateOff: 'Push-to-talk выключен', hintOff: 'Микрофон всегда активен. Включи PTT, чтобы говорить по кнопке.',
      stateBad: 'Микрофон недоступен', hintBad: 'Нет устройства записи или оно занято другой программой.',
      keyTitle: 'Клавиша push-to-talk',
      pttTitle: 'Push-to-talk включён',
      toggleTitle: 'Переключение одним нажатием',
      blockTitle: 'Блокировать клавишу в других программах',
      talk: 'говорить', tray: 'трей', showWin: 'показать окно', exit: 'Выход',
      change: 'Изменить', capture: 'Нажми клавишу…',
      devicePrefix: 'Микрофон: ',
      ctxOn: 'PTT включён', ctxOff: 'PTT выключен', ctxLive: 'микрофон активен',
      ctxMuted: 'микрофон заглушён', ctxBad: 'нет устройства',
      stCapture: 'Нажми нужную клавишу (Esc — отмена)', stCaptured: 'Клавиша изменена: {key}',
      stAutostartOn: 'Автозапуск включён', stAutostartOff: 'Автозапуск выключен',
      stMicFail: 'Не удалось изменить состояние микрофона', stMicOk: 'Готово',
      demo: 'Предпросмотр интерфейса: данные демонстрационные'
    },
    en: {
      subtitle: 'microphone on a key',
      stateLive: 'Microphone is live', hintLive: 'Talk — sound is being captured.',
      stateMuted: 'Microphone is muted', hintMuted: 'Hold {key} and talk; release to mute.',
      hintToggle: 'Press {key} to switch the microphone on, press again to switch off.',
      stateOff: 'Push-to-talk is off', hintOff: 'The mic is always live. Turn PTT on to talk on a key.',
      stateBad: 'Microphone unavailable', hintBad: 'No recording device, or it is in use by another app.',
      keyTitle: 'Push-to-talk key',
      pttTitle: 'Push-to-talk enabled',
      toggleTitle: 'Switch by a single press',
      blockTitle: 'Block the key in other apps',
      talk: 'talk', tray: 'tray', showWin: 'show window', exit: 'Exit',
      change: 'Change', capture: 'Press a key…',
      devicePrefix: 'Microphone: ',
      ctxOn: 'PTT on', ctxOff: 'PTT off', ctxLive: 'mic live',
      ctxMuted: 'mic muted', ctxBad: 'no device',
      stCapture: 'Press the key you want (Esc — cancel)', stCaptured: 'Key changed: {key}',
      stAutostartOn: 'Start with Windows enabled', stAutostartOff: 'Start with Windows disabled',
      stMicFail: 'Could not change the microphone state', stMicOk: 'Done',
      demo: 'Interface preview: demonstration data'
    }
  };
  let tr = T.ru;

  /* ------------------------- мост ------------------------- */
  function send(obj) {
    if (inApp) window.chrome.webview.postMessage(JSON.stringify(obj));
    else if (demo) mockHandle(obj);
  }
  function onHost(fn) {
    if (!inApp) return;
    window.chrome.webview.addEventListener('message', (ev) => {
      let d = ev.data;
      if (typeof d === 'string') { try { d = JSON.parse(d); } catch (e) { return; } }
      fn(d);
    });
  }

  const state = {
    key: 'HOME', ptt: true, block: true, toggle: false, autostart: false,
    lang: 0, micState: 'muted', capturing: false, device: ''
  };
  let statusTimer = null;

  function setStatus(text, kind) {
    const s = $('#status');
    s.className = 'status-line' + (text ? ' show' : '') + (kind && kind !== 'ok' ? ' ' + kind : '');
    s.textContent = text || '';
    if (statusTimer) { clearTimeout(statusTimer); statusTimer = null; }
    if (text && (!kind || kind === 'ok')) statusTimer = setTimeout(() => { s.className = 'status-line'; s.textContent = ''; }, 7000);
  }

  /* часть подписей есть не во всех вариантах вёрстки (компактное окно) — ставим без падений */
  const setText = (sel, text) => { const e = $(sel); if (e) e.textContent = text; };

  /* ------------------------- отрисовка ------------------------- */
  function render() {
    tr = state.lang ? T.en : T.ru;
    document.documentElement.lang = state.lang ? 'en' : 'ru';
    const badge = $('#badge');
    badge.className = 'state-badge' +
      (state.micState === 'live' ? ' live' : state.micState === 'off' ? ' off' :
       state.micState === 'unavailable' ? ' bad' : '');
    $('#slash').style.display = state.micState === 'live' ? 'none' : '';

    let title, hint;
    if (state.micState === 'unavailable') { title = tr.stateBad; hint = tr.hintBad; }
    else if (!state.ptt) { title = tr.stateOff; hint = tr.hintOff; }
    else if (state.micState === 'live') { title = tr.stateLive; hint = tr.hintLive; }
    else {
      title = tr.stateMuted;
      hint = (state.toggle ? tr.hintToggle : tr.hintMuted).replace('{key}', state.key);
    }
    $('#state-text').textContent = title;
    $('#state-hint').textContent = hint;
    $('#state-device').textContent = state.device ? tr.devicePrefix + state.device : '';

    setText('#subtitle', tr.subtitle);
    setText('#key-row-title', tr.keyTitle);
    setText('#ptt-row-title', tr.pttTitle);
    setText('#toggle-row-title', tr.toggleTitle);
    setText('#block-row-title', tr.blockTitle);
    setText('#hk-key-label', tr.talk);
    setText('#hk-tray-label', tr.showWin);
    setText('#exit-label', tr.exit);

    $('#key-name').textContent = state.capturing ? '…' : state.key;
    $('#hk-key').textContent = state.key;
    const btn = $('#btn-key');
    btn.textContent = state.capturing ? tr.capture : tr.change;
    btn.classList.toggle('capture', !!state.capturing);

    $('#sw-ptt').classList.toggle('on', !!state.ptt);
    $('#sw-ptt').setAttribute('aria-checked', state.ptt ? 'true' : 'false');
    $('#sw-toggle').classList.toggle('on', !!state.toggle);
    $('#sw-toggle').setAttribute('aria-checked', state.toggle ? 'true' : 'false');
    $('#sw-block').classList.toggle('on', !!state.block);
    $('#sw-block').setAttribute('aria-checked', state.block ? 'true' : 'false');
    $('#btn-autostart').classList.toggle('off', !state.autostart);

    const dot = $('#ctx-dot');
    dot.className = 'dot' + (state.micState === 'unavailable' ? ' bad'
      : !state.ptt ? '' : state.micState === 'live' ? '' : ' warn');
    $('#ctx-text').textContent = state.micState === 'unavailable' ? tr.ctxBad
      : !state.ptt ? tr.ctxOff : state.micState === 'live' ? tr.ctxLive : tr.ctxMuted;
  }

  /* ------------------------- действия ------------------------- */
  $('#btn-key').addEventListener('click', () => send({ cmd: state.capturing ? 'captureCancel' : 'capture' }));
  $('#sw-ptt').addEventListener('click', () => send({ cmd: 'setPtt', value: !state.ptt }));
  $('#sw-toggle').addEventListener('click', () => send({ cmd: 'setToggle', value: !state.toggle }));
  $('#sw-block').addEventListener('click', () => send({ cmd: 'setBlock', value: !state.block }));
  $('#btn-autostart').addEventListener('click', () => send({ cmd: 'autostart' }));
  $('#btn-lang').addEventListener('click', () => send({ cmd: 'setLang', value: state.lang ? 0 : 1 }));
  $('#btn-exit').addEventListener('click', () => send({ cmd: 'exit' }));

  document.querySelectorAll('[data-win]').forEach((b) =>
    b.addEventListener('click', () => send({ cmd: 'win', act: b.dataset.win })));
  const bar = document.querySelector('[data-drag]');
  bar.addEventListener('mousedown', (e) => {
    if (e.target.closest('.win-btn') || e.button !== 0) return;
    e.preventDefault();
    send({ cmd: 'drag' });
  });
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && !state.capturing) send({ cmd: 'hide' }); });

  onHost((m) => {
    if (m.t === 'state' && m.state) { Object.assign(state, m.state); render(); }
    else if (m.t === 'status') setStatus(m.text, m.kind);
    else if (m.t === 'level') {
      const v = Math.max(0, Math.min(1, Number(m.v) || 0));
      $('#level-fill').style.width = (v * 100).toFixed(1) + '%';
    }
  });

  /* ------------------------- предпросмотр ------------------------- */
  function mockHandle(obj) {
    if (obj.cmd === 'setPtt') { state.ptt = obj.value; state.micState = state.ptt ? 'muted' : 'off'; render(); return; }
    if (obj.cmd === 'setToggle') { state.toggle = obj.value; state.micState = 'muted'; render(); return; }
    if (obj.cmd === 'setBlock') { state.block = obj.value; render(); return; }
    if (obj.cmd === 'autostart') { state.autostart = !state.autostart; render(); return; }
    if (obj.cmd === 'setLang') { state.lang = obj.value; render(); return; }
    if (obj.cmd === 'capture') { state.capturing = true; render(); setStatus(T[state.lang ? 'en' : 'ru'].stCapture); return; }
    if (obj.cmd === 'captureCancel') { state.capturing = false; render(); return; }
    if (obj.cmd === 'exit') { setStatus('предпросмотр: выход недоступен', 'warn'); return; }
    if (obj.cmd === 'win' || obj.cmd === 'drag' || obj.cmd === 'hide') return;
  }

  if (inApp) send({ cmd: 'ready' });
  else if (demo) {
    document.title = 'Push to Talk — предпросмотр';
    Object.assign(state, { key: 'HOME', ptt: true, block: true, autostart: true, lang: 0, micState: 'muted',
      device: 'Микрофон (демонстрационный)' });
    render();
    setStatus(T.ru.demo, 'warn');
  } else render();
})();
