'use strict';

/* ============================================================
   PCOptimizer — логика дашборда.
   C# раз в секунду присылает сюда пакет данных (JSON),
   а мы его рисуем. Обратно уходят только команды окна.
   ============================================================ */

const $ = id => document.getElementById(id);

const ARC_LEN = 565.5;      // длина окружности кольца: 2 * pi * 90
const MAX_POINTS = 60;      // сколько секунд помним

const history = { cpu: [], gpu: [] };

let coreEls = [];
let lastDataAt = 0;
let uiActive = true;      // окно активно? от этого зависит, идут ли анимации
let historyDirty = false;
let monitorVisible = true;   // открыта ли вкладка «Мониторинг»
let staleTimer = null;

/* ---------- утилиты ---------- */

const clamp = (v, a, b) => Math.min(b, Math.max(a, v));
const num = v => (typeof v === 'number' && isFinite(v)) ? v : 0;

function post(action) {
  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.postMessage({ action: action });
  }
}

/* ---------- умный режим: анимации только у активного окна ---------- */

function setUiActive(active) {
  if (uiActive === active) return;

  uiActive = active;
  applyRunState();

  // Вернулись в окно — дорисовываем график, который пропустили.

}

function setArc(el, percent) {
  const p = clamp(num(percent), 0, 100);
  el.style.strokeDashoffset = String(ARC_LEN * (1 - p / 100));
}

function setValue(el, text, isMissing) {
  el.textContent = text;
  el.classList.toggle('na', !!isMissing);
}

function gb(mb) {
  return (num(mb) / 1024).toFixed(1);
}

/* ---------- отрисовка данных ---------- */

function render(d) {
  lastDataAt = Date.now();
  document.body.classList.remove('stale');

  renderCpu(d.cpu || {});
  renderGpu(d.gpu || {});
  renderRam(d.ram || {});
  renderVram(d.gpu || {});
  renderCores(d.cpu || {});

  pushHistory(history.cpu, num((d.cpu || {}).load));
  pushHistory(history.gpu, num((d.gpu || {}).load));
  drawHistory();

  $('statusText').textContent = 'обновлено ' + (d.time || '');
}

function renderCpu(cpu) {
  setArc($('cpuArc'), cpu.load);
  setNumber($('cpuLoad'), num(cpu.load));

  const hasTemp = typeof cpu.temp === 'number';
  setValue($('cpuTemp'), hasTemp ? Math.round(cpu.temp) + '°C' : 'н/д', !hasTemp);

  // Наводишь мышкой — видно, откуда взялась цифра.
  $('cpuTemp').title = hasTemp
    ? ('Источник: ' + (cpu.tempFrom || 'встроенный датчик'))
    : (cpu.tempNote || 'Датчик температуры недоступен');

  const mhz = num(cpu.freqMhz);
  setValue($('cpuFreq'), mhz > 0 ? (mhz / 1000).toFixed(2) + ' ГГц' : '—', mhz <= 0);

  if (cpu.name) {
    $('cpuName').textContent = cpu.name;
  }
}

function renderGpu(gpu) {
  const failed = !!gpu.error || typeof gpu.load !== 'number' || gpu.load < 0;

  setArc($('gpuArc'), failed ? 0 : gpu.load);
  setNumber($('gpuLoad'), failed ? 0 : num(gpu.load));

  const hasTemp = typeof gpu.temp === 'number' && gpu.temp > 0;
  setValue($('gpuTemp'), hasTemp ? Math.round(gpu.temp) + '°C' : 'н/д', !hasTemp);

  const clk = num(gpu.clockMhz);
  setValue($('gpuClock'), clk > 0 ? clk + ' МГц' : '—', clk <= 0);

  $('gpuName').textContent = gpu.name || (failed ? 'недоступна' : '—');
}

function renderRam(ram) {
  const pct = clamp(num(ram.percent), 0, 100);
  $('ramBar').style.width = pct + '%';
  $('ramPercent').textContent = Math.round(pct) + '%';
  $('ramUsed').textContent = gb(ram.usedMb) + ' ГБ';
  $('ramTotal').textContent = 'из ' + gb(ram.totalMb) + ' ГБ';
}

function renderVram(gpu) {
  const used = num(gpu.memUsedMb);
  const total = num(gpu.memTotalMb);
  const pct = total > 0 ? clamp(used / total * 100, 0, 100) : 0;

  $('vramBar').style.width = pct + '%';
  $('vramPercent').textContent = Math.round(pct) + '%';
  $('vramUsed').textContent = total > 0 ? gb(used) + ' ГБ' : '—';
  $('vramTotal').textContent = total > 0 ? 'из ' + gb(total) + ' ГБ' : '';
}

function renderCores(cpu) {
  const cores = cpu.cores;
  if (!cores || !cores.length) {
    return;
  }

  if (coreEls.length !== cores.length) {
    buildCores(cores.length);
  }

  $('coresCount').textContent = cores.length + ' шт.';

  for (let i = 0; i < cores.length; i++) {
    const c = cores[i];
    const load = clamp(num(c.load), 0, 100);
    coreEls[i].lvl.style.height = load + '%';
    coreEls[i].val.textContent = String(Math.round(load));
    coreEls[i].mhz.textContent = c.mhz > 0 ? (c.mhz / 1000).toFixed(1) + ' GHz' : '';
  }
}

function buildCores(count) {
  const box = $('cores');
  box.innerHTML = '';
  coreEls = [];

  for (let i = 0; i < count; i++) {
    const cell = document.createElement('div');
    cell.className = 'core';

    const lvl = document.createElement('div');
    lvl.className = 'lvl';

    const val = document.createElement('div');
    val.className = 'core-val';
    val.innerHTML = '<span class="n">0</span><span class="p">%</span>';

    const mhz = document.createElement('div');
    mhz.className = 'mhz';

    cell.appendChild(lvl);
    cell.appendChild(val);
    cell.appendChild(mhz);
    box.appendChild(cell);

    coreEls.push({
      lvl: lvl,
      val: val.querySelector('.n'),
      mhz: mhz
    });
  }
}

/* ---------- история ---------- */

function pushHistory(arr, value) {
  arr.push(value);
  while (arr.length > MAX_POINTS) {
    arr.shift();
  }
}

function drawHistory() {
  // Пока окно не активно — не тратим силы на перерисовку.
  if (!uiActive || !monitorVisible) { historyDirty = true; return; }
  historyDirty = false;

  const cv = $('history');
  if (!cv) return;

  const w = cv.clientWidth;
  const h = cv.clientHeight;
  if (w <= 0 || h <= 0) return;

  const dpr = window.devicePixelRatio || 1;
  const pw = Math.round(w * dpr);
  const ph = Math.round(h * dpr);
  if (cv.width !== pw || cv.height !== ph) {
    cv.width = pw;
    cv.height = ph;
  }

  const ctx = cv.getContext('2d');
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, w, h);

  const padL = 26, padR = 8, padT = 10, padB = 16;
  const gw = w - padL - padR;
  const gh = h - padT - padB;
  if (gw <= 0 || gh <= 0) return;

  // сетка и подписи оси Y
  const inkRgb = cssRgb('--ink');

  ctx.font = '9px "Bahnschrift", "Segoe UI", sans-serif';
  ctx.textBaseline = 'middle';
  for (let i = 0; i <= 4; i++) {
    const y = padT + gh * i / 4;
    ctx.strokeStyle = 'rgba(' + inkRgb + ',.06)';
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(padL, y + .5);
    ctx.lineTo(padL + gw, y + .5);
    ctx.stroke();

    ctx.fillStyle = 'rgba(' + inkRgb + ',.30)';
    ctx.fillText(String(100 - i * 25), 6, y);
  }

  // подписи оси X
  ctx.fillStyle = 'rgba(' + inkRgb + ',.26)';
  ctx.fillText('60 сек назад', padL, h - 7);
  const rightLabel = 'сейчас';
  ctx.fillText(rightLabel, padL + gw - ctx.measureText(rightLabel).width, h - 7);

  // Цвета линий берём из акцентной схемы, чтобы график менялся
  // вместе со всем интерфейсом.
  const cpuRgb = cssRgb('--accent-rgb');
  const gpuRgb = cssRgb('--gpu-rgb');

  const series = [
    { data: history.cpu, color: 'rgb(' + cpuRgb + ')',
      glow: 'rgba(' + cpuRgb + ',.85)', fillTop: 'rgba(' + cpuRgb + ',.22)' },
    { data: history.gpu, color: 'rgb(' + gpuRgb + ')',
      glow: 'rgba(' + gpuRgb + ',.85)', fillTop: 'rgba(' + gpuRgb + ',.18)' }
  ];

  for (const s of series) {
    if (s.data.length < 2) continue;

    const pts = s.data.map((v, i) => ({
      x: padL + gw * (i / (MAX_POINTS - 1)),
      y: padT + gh * (1 - clamp(v, 0, 100) / 100)
    }));

    // заливка под линией
    const grad = ctx.createLinearGradient(0, padT, 0, padT + gh);
    grad.addColorStop(0, s.fillTop);
    grad.addColorStop(1, 'rgba(0,0,0,0)');

    ctx.beginPath();
    ctx.moveTo(pts[0].x, padT + gh);
    for (const p of pts) {
      ctx.lineTo(p.x, p.y);
    }
    ctx.lineTo(pts[pts.length - 1].x, padT + gh);
    ctx.closePath();
    ctx.fillStyle = grad;
    ctx.fill();

    // сама линия со свечением
    ctx.save();
    ctx.beginPath();
    ctx.moveTo(pts[0].x, pts[0].y);
    for (let i = 1; i < pts.length; i++) {
      ctx.lineTo(pts[i].x, pts[i].y);
    }
    ctx.strokeStyle = s.color;
    ctx.lineWidth = 2;
    ctx.lineJoin = 'round';
    ctx.lineCap = 'round';
    ctx.shadowColor = s.glow;
    ctx.shadowBlur = 10;
    ctx.stroke();
    ctx.restore();

    // огонёк на конце линии
    const head = pts[pts.length - 1];
    ctx.save();
    ctx.beginPath();
    ctx.arc(head.x, head.y, 3, 0, Math.PI * 2);
    ctx.fillStyle = s.color;
    ctx.shadowColor = s.glow;
    ctx.shadowBlur = 14;
    ctx.fill();
    ctx.restore();
  }
}

/* ---------- подсказка внизу ---------- */

function setHint(text, warn, elevate) {
  const el = $('hint');
  el.textContent = text || '';
  el.classList.toggle('warn', !!warn);
  $('btnElevate').style.display = elevate ? '' : 'none';
}

/* ============================================================
   ЦВЕТ ИНТЕРФЕЙСА — 7 схем
   ============================================================ */

const ACCENTS = [
  { id: 'red',    name: 'Красный',    color: '#ff2b4a' },
  { id: 'orange', name: 'Оранжевый',  color: '#ff7a1a' },
  { id: 'yellow', name: 'Жёлтый',     color: '#f5c000' },
  { id: 'green',  name: 'Зелёный',    color: '#1fc45f' },
  { id: 'cyan',   name: 'Голубой',    color: '#00cfff' },
  { id: 'blue',   name: 'Синий',      color: '#3d7bff' },
  { id: 'purple', name: 'Фиолетовый', color: '#a855f7' }
];

let accent = 'red';
let soundOn = true;

// Значение CSS-переменной вида «255, 43, 74» — из него собираем rgba().
function cssRgb(name) {
  try {
    const v = getComputedStyle(document.body).getPropertyValue(name).trim();
    return v || '255, 43, 74';
  } catch (e) {
    return '255, 43, 74';
  }
}

function buildAccentGrid() {
  const box = $('accentGrid');
  if (!box) return;

  box.innerHTML = '';

  ACCENTS.forEach(function (item) {
    const b = document.createElement('button');
    b.type = 'button';
    b.className = 'accent-swatch';
    b.title = item.name;
    b.dataset.accent = item.id;
    b.style.background = 'linear-gradient(145deg, ' + item.color + ', ' + item.color + 'cc)';
    b.addEventListener('click', function () {
      applyAccent(item.id);
      saveSetting('accent', item.id);
    });
    box.appendChild(b);
  });
}

function applyAccent(name) {
  const found = ACCENTS.filter(function (a) { return a.id === name; })[0];
  accent = found ? found.id : 'red';

  document.body.dataset.accent = accent;

  const swatches = document.querySelectorAll('.accent-swatch');
  for (let i = 0; i < swatches.length; i++) {
    swatches[i].classList.toggle('on', swatches[i].dataset.accent === accent);
  }

  const state = $('accentState');
  if (state && found) {
    state.textContent = found.name.toLowerCase();
  }

  // Линии графика рисуются на canvas и сами цвет не подхватят —
  // просим перерисовать.
  if (typeof history !== 'undefined') {
    historyDirty = true;
    drawHistory();
  }
}

/* ============================================================
   СЧЁТЧИК «СКОЛЬКО СЭКОНОМИЛ»
   ============================================================ */

function fmtStat(v) {
  const n = Math.round((v || 0) * 10) / 10;
  return n.toLocaleString('ru-RU');
}

function renderStats(s) {
  if (!s) return;

  $('stFreed').textContent = fmtStat(s.freedGb);
  $('stHours').textContent = fmtStat(s.savedHours);
  $('stOpts').textContent = String(s.optimizations || 0);

  $('statsSince').textContent = s.since
    ? ('с ' + s.since + ' · запусков: ' + (s.sessions || 0))
    : '—';
}

/* ============================================================
   МИНИ-ОВЕРЛЕЙ
   ============================================================ */

function showMiniAsk() {
  const box = $('miniAskModal');
  if (box) box.classList.add('show');
}

function applyMiniState(on) {
  cfg.mini = !!on;

  $('chkMini').checked = cfg.mini;
  $('miniState').textContent = cfg.mini ? 'включён' : 'выключен';
}

/* ============================================================
   «БОЛЬШАЯ ЧИСТКА» — анимация, частицы и звук
   ============================================================ */

// У каждой вкладки свой характер эффекта: цвет, форма частиц, направление.
const FX_THEMES = {
  clean: {
    title: 'Большая чистка', cap: 'освобождено!',
    pal: ['--accent-rgb', '--gpu-rgb', '--ram-rgb', '--vram-rgb'],
    shapes: ['box', 'box', 'dot'], origin: 'center'
  },
  scan: {
    title: 'Анализ мусора', cap: 'насчитано',
    pal: ['--accent-rgb', '--vram-rgb'],
    shapes: ['dot', 'spark'], origin: 'center'
  },
  optimize: {
    title: 'Оптимизация', cap: 'применено!',
    pal: ['--accent-rgb', '--accent2-rgb', '--vram-rgb'],
    shapes: ['spark', 'spark', 'ring'], origin: 'center'
  },
  game: {
    title: 'Турбо-режим', cap: 'готово!',
    pal: ['--accent-rgb', '--gpu-rgb', '--accent2-rgb'],
    shapes: ['pixel', 'pixel', 'spark'], origin: 'center'
  },
  system: {
    title: 'Сканирую компьютер', cap: 'компьютер определён',
    pal: ['--accent-rgb', '--gpu-rgb'],
    shapes: ['line', 'dot'], origin: 'sweep'
  }
};

const fx = {
  active: false,     // ждём сообщения от C#
  visible: false,    // плашка на экране
  finishing: false,  // показываем финал
  kind: 'clean',
  mode: 'percent',   // percent | steps | scan
  total: 12,
  finished: 0,
  freed: 0,
  percent: 0,
  stepIndex: 0,
  stepTotal: 7,
  lastMsgAt: 0,
  minUntil: 0,
  pending: '',       // какая операция ждёт начала: optimize | game | restore
  pendingTitle: '',
  raf: 0,
  parts: [],
  pal: [],
  shapes: [],
  origin: 'center',
  ctx: null,
  w: 0,
  h: 0,
  audio: null,
  hideTimer: null
};

// ---------- рисование частиц разных форм ----------

function fxDraw(ctx, p) {
  if (p.shape === 'ring') {
    ctx.strokeStyle = 'rgb(' + p.rgb + ')';
    ctx.lineWidth = 1.7;
    ctx.beginPath();
    ctx.arc(0, 0, p.size, 0, Math.PI * 2);
    ctx.stroke();
    return;
  }

  ctx.fillStyle = 'rgb(' + p.rgb + ')';

  if (p.shape === 'spark') {
    ctx.fillRect(-p.size * 1.6, -p.size * .17, p.size * 3.2, p.size * .34);
  } else if (p.shape === 'line') {
    ctx.fillRect(-p.size * 2.6, -p.size * .12, p.size * 5.2, p.size * .24);
  } else if (p.shape === 'pixel') {
    ctx.fillRect(-p.size * .55, -p.size * .55, p.size * 1.1, p.size * 1.1);
  } else if (p.shape === 'box') {
    ctx.fillRect(-p.size * .5, -p.size * .7, p.size, p.size * 1.4);
  } else {
    ctx.beginPath();
    ctx.arc(0, 0, p.size * .5, 0, Math.PI * 2);
    ctx.fill();
  }
}

// ---------- шаги операции точками ----------

function fxBuildSteps(count) {
  const box = $('fxSteps');
  if (!box) return;

  box.innerHTML = '';

  if (fx.mode !== 'steps') {
    box.style.display = 'none';
    return;
  }

  box.style.display = 'flex';

  for (let i = 0; i < count; i++) {
    const dot = document.createElement('span');
    dot.className = 'fx-step';
    box.appendChild(dot);
  }
}

// ---------- общий запуск плашки ----------

function fxBegin(opts) {
  const box = $('cleanFx');
  if (!box) return;

  // Плашка занята настоящей работой — не перебиваем её чужой анимацией.
  // А вот показанный финал перебить можно: человек уже нажал следующую кнопку.
  if (fx.visible && fx.active && !fx.finishing) return;

  const o = opts || {};
  const theme = FX_THEMES[o.kind] || FX_THEMES.clean;

  if (fx.hideTimer) {
    clearTimeout(fx.hideTimer);
    fx.hideTimer = null;
  }

  fx.active = true;
  fx.visible = true;
  fx.finishing = false;
  fx.kind = o.kind || 'clean';
  fx.mode = o.mode || 'percent';
  fx.total = Math.max(1, o.total || 10);
  fx.finished = 0;
  fx.freed = 0;
  fx.percent = 0;
  fx.stepIndex = 0;
  fx.stepTotal = Math.max(1, o.stepTotal || 7);
  fx.parts = [];
  fx.lastMsgAt = Date.now();
  fx.minUntil = Date.now() + (o.minMs || 0);
  fx.pal = theme.pal.map(cssRgb);
  fx.shapes = theme.shapes;
  fx.origin = theme.origin;

  $('fxTitle').textContent = o.title || theme.title;
  $('fxPercent').textContent = fx.mode === 'percent' ? '0%' : '···';
  $('fxBarFill').style.width = '0%';
  $('fxCurrent').textContent = o.caption || 'начинаю…';
  $('fxFreed').textContent = '';
  $('fxFinalNum').textContent = '';
  $('fxFinalCap').textContent = theme.cap;
  $('fxFinal').classList.remove('on');
  $('fxFlash').classList.remove('on');
  $('fxInner').classList.remove('shake');

  fxBuildSteps(fx.mode === 'steps' ? fx.stepTotal : 0);

  box.classList.add('on');
  box.dataset.kind = fx.kind;

  // Даём браузеру отрисовать плашку, чтобы у canvas появились размеры.
  setTimeout(function () {
    fxResize();
    fxSpawn(weakCpu ? 18 : 64, .95);
    if (!fx.raf) fx.raf = requestAnimationFrame(fxFrame);
  }, 40);

  fxTone(200, .2, 'sawtooth', .035);
}

function fxPercent(p) {
  fx.percent = Math.max(0, Math.min(100, p));
  $('fxBarFill').style.width = fx.percent + '%';

  if (fx.mode === 'percent') {
    $('fxPercent').textContent = Math.round(fx.percent) + '%';
  }
}

function fxText(text) {
  if (!fx.visible || !text) return;
  fx.lastMsgAt = Date.now();
  $('fxCurrent').textContent = text;
}

function fxStep(text) {
  if (!fx.active) return;

  fx.lastMsgAt = Date.now();
  fx.stepIndex++;

  const dots = document.querySelectorAll('#fxSteps .fx-step');
  if (dots[fx.stepIndex - 1]) dots[fx.stepIndex - 1].classList.add('on');

  fxPercent(Math.min(95, fx.stepIndex * 100 / fx.stepTotal));

  if (text) $('fxCurrent').textContent = text;

  fxSpawn(weakCpu ? 5 : 18, 1.15);

  if (!weakCpu) fxTone(620 + Math.random() * 320, .08, 'triangle', .025);
}

// ---------- финал ----------

function fxEnd(opts) {
  if (!fx.visible || fx.finishing) return;

  const o = opts || {};
  const wait = Math.max(0, fx.minUntil - Date.now());

  fx.finishing = true;
  fx.active = false;

  if (wait > 0) $('fxCurrent').textContent = 'почти готово…';

  fx.hideTimer = setTimeout(function () {
    fx.finishing = false;
    fxFinaleShow(o);
  }, wait);
}

function fxFinaleShow(o) {
  fxPercent(100);

  const dots = document.querySelectorAll('#fxSteps .fx-step');
  for (let i = 0; i < dots.length; i++) dots[i].classList.add('on');

  if (fx.mode !== 'percent') $('fxPercent').textContent = '✓';

  const big = o.big || 'Готово';
  const num = $('fxFinalNum');
  num.textContent = big;
  num.classList.toggle('long', big.length > 9);

  if (o.caption) $('fxFinalCap').textContent = o.caption;

  $('fxFinal').classList.add('on');
  $('fxFlash').classList.add('on');
  $('fxInner').classList.add('shake');

  fxSpawn(weakCpu ? 40 : 150, 2);

  if (o.tone === 'soft') {
    fxTone(320, .32, 'sine', .04);
  } else {
    fxFinale();
  }

  fx.hideTimer = setTimeout(fxHide, 2000);
}

/* ============================================================
   Какая операция что показывает
   ============================================================ */

function pcShortBig() {
  const cpu = (pc.report && pc.report.cpu) || {};
  if (cpu.cores) return cpu.cores + ' ' + plural(cpu.cores, 'ядро', 'ядра', 'ядер');
  return 'Готово';
}

function trackFxMessage(msg) {
  switch (msg.type) {

    // ---------- оптимизация ----------
    case 'optBusy':
      if (!msg.busy || fx.pending !== 'optimize') break;

      // Это не работа, а чтение состояния при открытии вкладки.
      if (msg.text && msg.text.indexOf('Читаю состояние') === 0) {
        fx.pending = '';
        break;
      }

      if (!fx.visible) {
        fxBegin({
          kind: 'optimize',
          mode: 'steps',
          stepTotal: Math.max(3, countOptChanges() + 1),
          title: 'Оптимизация',
          caption: msg.text || 'применяю…',
          minMs: 900
        });
      } else if (fx.active) {
        fxStep(msg.text || 'применяю…');
      }
      break;

    case 'optResult':
      if (fx.pending !== 'optimize') break;
      fx.pending = '';
      fxEnd({
        big: 'Готово',
        caption: msg.ok === false ? 'с замечаниями' : 'применено!',
        tone: msg.ok === false ? 'soft' : 'finale'
      });
      break;

    // ---------- авто-оптимизация ----------
    case 'autoBusy':
      if (!msg.busy) break;

      if (!fx.visible) {
        fxBegin({
          kind: 'optimize',
          mode: 'steps',
          stepTotal: 9,
          title: 'Авто-оптимизация',
          caption: msg.text || 'работаю…',
          minMs: 900
        });
      } else if (fx.active) {
        fxStep(msg.text || 'работаю…');
      }
      break;

    case 'autoDone':
      fxEnd({
        big: msg.error ? 'Ошибка' : (msg.freedText || 'Готово'),
        caption: msg.error ? 'не получилось' : 'освобождено',
        tone: msg.error ? 'soft' : 'finale'
      });
      break;

    // ---------- игры ----------
    case 'gameBusy':
      if (!msg.busy || fx.pending !== 'game') break;

      if (!fx.visible) {
        fxBegin({
          kind: 'game',
          mode: 'steps',
          stepTotal: 9,
          title: fx.pendingTitle || 'Турбо-режим',
          caption: msg.text || 'работаю…',
          minMs: 900
        });
      } else if (fx.active) {
        fxStep(msg.text || 'работаю…');
      }
      break;

    case 'gameStep':
      if (fx.visible && fx.active && fx.pending === 'game') {
        fxText(msg.title ? (msg.title + ' — ' + (msg.text || '')) : msg.text);
      }
      break;

    case 'gameDone':
      if (fx.pending !== 'game') break;
      fx.pending = '';
      fxEnd({ big: 'Готово', caption: 'можно играть', tone: 'finale' });
      break;

    // ---------- мой ПК ----------
    case 'systemBusy':
      if (msg.busy) {
        fxBegin({
          kind: 'system',
          mode: 'scan',
          title: 'Сканирую компьютер',
          caption: msg.text || 'читаю железо…',
          minMs: 1500
        });
      } else {
        fxEnd({ big: pcShortBig(), caption: 'компьютер определён', tone: 'finale' });
      }
      break;

    // ---------- настройки: точка восстановления ----------
    case 'setBusy':
      if (msg.busy && fx.pending === 'restore' && !fx.visible) {
        fxBegin({
          kind: 'optimize',
          mode: 'scan',
          title: 'Точка восстановления',
          caption: msg.text || 'создаю…',
          minMs: 1300
        });
      }
      break;

    case 'setResult':
      if (fx.pending === 'restore') {
        fx.pending = '';
        fxEnd({ big: 'Готово', caption: 'точка создана', tone: 'finale' });
      }
      break;
  }
}

/* ============================================================
   Переход между вкладками и мелкие эффекты
   ============================================================ */

function animateTab(pageId) {
  const page = $(pageId);
  if (!page) return;

  const items = page.querySelectorAll('.card, .sys-hero, .auto-card, .clean-total, .actions');

  for (let i = 0; i < items.length; i++) {
    const el = items[i];

    // Перезапускаем анимацию: без этого она сработает только один раз.
    el.classList.remove('tab-in-item');
    void el.offsetWidth;
    el.style.animationDelay = Math.min(i * 42, 380) + 'ms';
    el.classList.add('tab-in-item');
  }
}

function moveTabSweep() {
  const bar = document.getElementById('tabSweep');
  const active = document.querySelector('.tabs .tab.active');
  if (!bar || !active) return;

  bar.style.left = active.offsetLeft + 'px';
  bar.style.width = active.offsetWidth + 'px';
  bar.classList.add('on');
}

// Плавная смена чисел: значение «докручивается», а не прыгает.
function setNumber(el, value) {
  if (!el) return;

  const target = Math.round(value);

  if (el._numTarget === target) return;
  el._numTarget = target;

  if (!uiActive) {
    if (el._numRaf) {
      cancelAnimationFrame(el._numRaf);
      el._numRaf = 0;
    }

    el.textContent = String(target);
    return;
  }

  const from = Number(el.textContent) || 0;
  const started = performance.now();
  const duration = 420;

  if (el._numRaf) cancelAnimationFrame(el._numRaf);

  function tick(now) {
    const k = Math.min(1, (now - started) / duration);
    const eased = 1 - Math.pow(1 - k, 3);

    el.textContent = String(Math.round(from + (target - from) * eased));

    el._numRaf = k < 1 ? requestAnimationFrame(tick) : 0;
  }

  el._numRaf = requestAnimationFrame(tick);
}

function rippleAt(btn, x, y) {
  const rect = btn.getBoundingClientRect();
  const size = Math.max(rect.width, rect.height);

  const span = document.createElement('span');
  span.className = 'ripple';
  span.style.width = size + 'px';
  span.style.height = size + 'px';
  span.style.left = (x - rect.left - size / 2) + 'px';
  span.style.top = (y - rect.top - size / 2) + 'px';

  btn.appendChild(span);
  setTimeout(function () { span.remove(); }, 620);
}

// ---------- звук: всё синтезируем на лету, никаких файлов ----------

function fxAudio() {
  if (!soundOn) return null;

  try {
    const AC = window.AudioContext || window.webkitAudioContext;
    if (!AC) return null;

    if (!fx.audio) fx.audio = new AC();
    if (fx.audio.state === 'suspended') fx.audio.resume();

    return fx.audio;
  } catch (e) {
    return null;
  }
}

function fxTone(freq, dur, type, gain) {
  const ac = fxAudio();
  if (!ac) return;

  try {
    const o = ac.createOscillator();
    const g = ac.createGain();
    const t = ac.currentTime;

    o.type = type || 'sine';
    o.frequency.setValueAtTime(freq, t);

    g.gain.setValueAtTime(0, t);
    g.gain.linearRampToValueAtTime(gain === undefined ? .05 : gain, t + .012);
    g.gain.exponentialRampToValueAtTime(.0001, t + dur);

    o.connect(g);
    g.connect(ac.destination);
    o.start(t);
    o.stop(t + dur + .03);
  } catch (e) {
    // Звук — украшение, без него всё работает.
  }
}

function fxWhoosh() {
  const ac = fxAudio();
  if (!ac) return;

  try {
    const len = Math.floor(ac.sampleRate * .4);
    const buf = ac.createBuffer(1, len, ac.sampleRate);
    const data = buf.getChannelData(0);

    for (let i = 0; i < len; i++) {
      const k = 1 - i / len;
      data[i] = (Math.random() * 2 - 1) * k * k;
    }

    const src = ac.createBufferSource();
    src.buffer = buf;

    const filter = ac.createBiquadFilter();
    filter.type = 'bandpass';
    filter.frequency.setValueAtTime(500, ac.currentTime);
    filter.frequency.exponentialRampToValueAtTime(2800, ac.currentTime + .36);

    const g = ac.createGain();
    g.gain.value = .07;

    src.connect(filter);
    filter.connect(g);
    g.connect(ac.destination);
    src.start();
  } catch (e) {
    // Тихо игнорируем.
  }
}

function fxFinale() {
  const notes = [523.25, 659.25, 783.99, 1046.5];

  notes.forEach(function (f, i) {
    setTimeout(function () { fxTone(f, .55, 'triangle', .06); }, i * 85);
  });
}

// ---------- canvas ----------

function fxResize() {
  const cv = $('fxCanvas');
  if (!cv) return;

  const dpr = Math.min(window.devicePixelRatio || 1, 2);
  fx.w = cv.clientWidth;
  fx.h = cv.clientHeight;

  if (fx.w <= 0 || fx.h <= 0) return;

  cv.width = Math.round(fx.w * dpr);
  cv.height = Math.round(fx.h * dpr);

  fx.ctx = cv.getContext('2d');
  fx.ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
}

function fxSpawn(count, power) {
  if (!fx.ctx || fx.w <= 0) return;

  const pal = fx.pal.length ? fx.pal : ['255, 43, 74'];
  const shapes = fx.shapes.length ? fx.shapes : ['dot'];
  const p = power || 1;
  const cap = weakCpu ? 90 : 230;

  for (let i = 0; i < count; i++) {
    if (fx.parts.length > cap) break;

    let x, y, vx, vy, g;

    if (fx.origin === 'sweep') {
      // «Сканирование»: полосы проносятся слева направо.
      x = -40 + Math.random() * fx.w * .25;
      y = fx.h * (.16 + Math.random() * .68);
      vx = (5 + Math.random() * 7) * p;
      vy = (Math.random() - .5) * .5;
      g = 0;
    } else {
      const angle = -Math.PI / 2 + (Math.random() - .5) * 1.9;
      const speed = (1.2 + Math.random() * 2.4) * p;

      x = fx.w / 2 + (Math.random() - .5) * fx.w * .34;
      y = fx.h * .62 + (Math.random() - .5) * 40;
      vx = Math.cos(angle) * speed;
      vy = Math.sin(angle) * speed;
      g = .03 + Math.random() * .035;
    }

    fx.parts.push({
      x, y, vx, vy, g,
      size: 3 + Math.random() * 6,
      rot: Math.random() * Math.PI,
      vr: (Math.random() - .5) * .17,
      life: 1,
      decay: .0055 + Math.random() * .009,
      rgb: pal[(Math.random() * pal.length) | 0],
      shape: shapes[(Math.random() * shapes.length) | 0]
    });
  }
}

function fxFrame() {
  if (!fx.visible) {
    fx.raf = 0;
    return;
  }

  const ctx = fx.ctx;
  if (!ctx) {
    fx.raf = 0;
    return;
  }

  ctx.clearRect(0, 0, fx.w, fx.h);

  const parts = fx.parts;

  for (let i = parts.length - 1; i >= 0; i--) {
    const p = parts[i];

    p.vy += p.g;
    p.x += p.vx;
    p.y += p.vy;
    p.rot += p.vr;
    p.life -= p.decay;

    if (p.life <= 0 || p.y > fx.h + 60 || p.x > fx.w + 90 || p.x < -90) {
      parts.splice(i, 1);
      continue;
    }

    ctx.save();
    ctx.globalAlpha = p.life > 1 ? 1 : p.life;
    ctx.translate(p.x, p.y);
    ctx.rotate(p.rot);
    fxDraw(ctx, p);
    ctx.restore();
  }

  if (parts.length === 0 && !fx.active) {
    fx.raf = 0;
    return;
  }

  fx.raf = requestAnimationFrame(fxFrame);
}

// ---------- ход чистки ----------

function fxScanItem() {
  if (!fx.visible || !fx.active || fx.kind !== 'scan') return;

  fx.lastMsgAt = Date.now();
  fx.finished++;

  let found = 0;
  for (const k in clean.sizes) found += clean.sizes[k] || 0;

  fxPercent(fx.finished * 100 / fx.total);

  if (found > 0) $('fxFreed').textContent = 'Найдено: ' + fmtBytes(found);

  fxSpawn(weakCpu ? 5 : 14, 1.1);

  if (!weakCpu && fx.finished % 3 === 0) {
    fxTone(700 + Math.random() * 240, .07, 'triangle', .022);
  }
}

function fxStart(scan) {
  const isScan = !!scan;
  const total = Math.max(1, (clean.items && clean.items.length) ? clean.items.length : 12);

  fxBegin({
    kind: isScan ? 'scan' : 'clean',
    mode: 'percent',
    total: total,
    title: isScan ? 'Анализ мусора' : 'Большая чистка',
    caption: isScan ? 'считаю размеры…' : 'готовлюсь…'
  });
}

function fxOnStatus(text) {
  if (!fx.active || !text) return;

  fxText(String(text).replace(/^(Чищу|Считаю):\s*/, ''));
}

function fxOnItem(msg) {
  if (!fx.active) return;

  fx.lastMsgAt = Date.now();
  fx.finished++;
  fx.freed += msg.freed || 0;

  fxPercent(fx.finished * 100 / fx.total);
  $('fxFreed').textContent = fx.freed > 0 ? ('Освобождено: ' + fmtBytes(fx.freed)) : '';

  if (msg.freed > 0) {
    const burst = Math.min(40, Math.round(10 + Math.log(msg.freed / 1024 + 1) * 5));
    fxSpawn(weakCpu ? Math.max(3, Math.round(burst / 3)) : burst, 1.3);

    if (weakCpu) {
      fxTone(560, .07, 'triangle', .02);
    } else {
      fxWhoosh();
      fxTone(720 + Math.random() * 260, .1, 'triangle', .03);
    }
  } else {
    fxSpawn(5, .6);
    fxTone(280, .06, 'square', .014);
  }
}

function fxFinish(freed) {
  if (typeof freed === 'number') fx.freed = freed;

  $('fxFreed').textContent = '';

  fxEnd({
    big: fmtBytes(fx.freed),
    caption: 'освобождено!',
    tone: fx.freed > 0 ? 'finale' : 'soft'
  });
}

function fxHide() {
  const box = $('cleanFx');
  if (box) box.classList.remove('on');

  fx.visible = false;
  fx.active = false;
  fx.finishing = false;

  if (fx.hideTimer) {
    clearTimeout(fx.hideTimer);
    fx.hideTimer = null;
  }

  // Даём последним частицам догореть и останавливаем кадры.
  setTimeout(function () {
    if (fx.raf) {
      cancelAnimationFrame(fx.raf);
      fx.raf = 0;
    }

    fx.parts = [];

    if (fx.ctx) fx.ctx.clearRect(0, 0, fx.w, fx.h);
  }, 450);
}

// Страховка: если C# почему-то не ответил (занят, упал),
// плашка не должна висеть вечно.
setInterval(function () {
  if (!fx.active) return;
  if (Date.now() - fx.lastMsgAt > 9000) {
    fxEnd({ big: 'Готово', caption: 'завершено', tone: 'soft' });
  }
}, 1500);

window.addEventListener('resize', function () {
  if (fx.visible) fxResize();
});

buildAccentGrid();
/* ============================================================
   МОЙ ПК: отчёт о железе и подстройка под него
   ============================================================ */

const pc = { report: null, loaded: false };
const disk = { busy: false, letter: '' };

// Слабый процессор — тяжёлые анимации упрощаем.
let weakCpu = false;

function requestSystem() {
  window.chrome.webview.postMessage({ action: 'systemLoad' });
}

function optimizeDisk(letter) {
  if (disk.busy) return;
  disk.letter = letter;
  window.chrome.webview.postMessage({ action: 'diskOptimize', letter: letter });
}

function renderSystem(report) {
  if (!report) return;

  pc.report = report;
  pc.loaded = true;
  weakCpu = !!report.weakCpu;

  const cpu = report.cpu || {};
  const gpu = report.gpu || {};
  const ram = report.ram || {};
  const win = report.windows || {};

  // ---- шапка ----
  $('sysDeviceLine').textContent = cpu.name || 'Компьютер';

  $('sysScanLine').textContent = (report.fromCache ? 'отчёт из profile.json · ' : 'просканировано · ')
    + (report.scannedAt || '');

  // ---- процессор ----
  $('sysCpu').textContent = cpu.name || 'неизвестно';

  const cpuBits = [];
  if (cpu.cores) cpuBits.push(cpu.cores + ' ' + plural(cpu.cores, 'ядро', 'ядра', 'ядер'));
  if (cpu.threads) cpuBits.push(cpu.threads + ' потоков');
  if (cpu.maxMhz) cpuBits.push('до ' + (cpu.maxMhz / 1000).toFixed(2) + ' ГГц');
  $('sysCpuDesc').textContent = cpuBits.join(' · ') || 'подробности недоступны';

  $('sysCpuTier').textContent = cpu.tierName || '—';
  $('sysCpuTier').classList.toggle('warn', cpu.tier === 'VeryWeak' || cpu.tier === 'Weak');

  // ---- видеокарта ----
  $('sysGpu').textContent = gpu.name || 'не определена';

  const gpuBits = [];
  if (gpu.memoryMb > 0) {
    gpuBits.push(gpu.memoryMb >= 1024
      ? (gpu.memoryMb / 1024).toFixed(0) + ' ГБ видеопамяти'
      : gpu.memoryMb + ' МБ видеопамяти');
  } else {
    gpuBits.push('объём видеопамяти неизвестен');
  }

  if (gpu.driver) gpuBits.push('драйвер ' + gpu.driver);
  gpuBits.push(report.gpuMonitoring
    ? 'мониторинг: ' + report.gpuMonitoring
    : 'мониторинг недоступен');

  $('sysGpuDesc').textContent = gpuBits.join(' · ');
  $('sysGpuVendor').textContent = gpu.vendorName || '—';
  $('sysGpuVendor').classList.toggle('warn', !report.gpuMonitoring);

  // ---- память ----
  const totalGb = (ram.totalMb || 0) / 1024;
  $('sysRam').textContent = totalGb > 0 ? totalGb.toFixed(totalGb < 10 ? 1 : 0) + ' ГБ' : 'неизвестно';

  const ramBits = [];
  if (ram.typeName && ram.typeName !== 'неизвестно') ramBits.push(ram.typeName);
  if (ram.speedMhz) ramBits.push(ram.speedMhz + ' МГц');
  if (ram.slots) ramBits.push('занято слотов ' + ram.modules + ' из ' + ram.slots);
  $('sysRamDesc').textContent = ramBits.join(' · ') || 'подробности недоступны';

  $('sysRamBadge').textContent = ram.low ? 'мало' : 'норма';
  $('sysRamBadge').classList.toggle('warn', !!ram.low);

  // ---- система ----
  $('sysOs').textContent = (win.name || 'Windows') + (win.version ? ' ' + win.version : '');

  const osBits = ['сборка ' + (win.buildFull || '—')];
  if (win.displayVersion) osBits.push(win.displayVersion);
  if (win.arch) osBits.push(win.arch);
  $('sysOsDesc').textContent = osBits.join(' · ');
  $('sysOsBadge').textContent = win.version === '11' ? 'Windows 11' : 'Windows 10';
  $('sysWindows').textContent = (win.name || '—') + ' ' + (win.buildFull || '');

  // ---- диски ----
  renderDisks($('sysDisks'), $('sysDiskSummary'), report, false);
  renderDisks($('optDisks'), $('diskSummary'), report, true);

  // ---- что подстроили ----
  const notes = report.notes || [];
  const box = $('sysNotes');
  box.innerHTML = '';

  if (notes.length === 0) {
    const empty = document.createElement('div');
    empty.className = 'game-empty';
    empty.textContent = 'Пока нечего показать.';
    box.appendChild(empty);
  }

  notes.forEach(function (text) {
    const row = document.createElement('div');
    row.className = 'note-row';

    const dot = document.createElement('span');
    dot.className = 'note-dot';

    const body = document.createElement('span');
    body.className = 'note-text';
    body.textContent = text;

    row.appendChild(dot);
    row.appendChild(body);
    box.appendChild(row);
  });

  $('sysNotesCount').textContent = notes.length > 0 ? ('пунктов: ' + notes.length) : '—';

  // ---- отчёт о системе ----
  $('sysProfileState').textContent = report.fromCache ? 'взят готовый' : 'просканировано';
  $('sysProfilePath').textContent = 'Файл: profile.json рядом с settings.json';

  $('sysHint').textContent = report.weakCpu
    ? 'Процессор слабый — тяжёлые оптимизации не предлагаю, анимации упрощены'
    : 'Программа определила железо и подстроила под него свои решения';
}

function renderDisks(container, summary, report, withButtons) {
  if (!container) return;

  const volumes = (report && report.volumes) || [];
  container.innerHTML = '';

  if (volumes.length === 0) {
    const empty = document.createElement('div');
    empty.className = 'game-empty';
    empty.textContent = 'Накопители определить не удалось.';
    container.appendChild(empty);
    return;
  }

  volumes.forEach(function (v) {
    const row = document.createElement('div');
    row.className = 'disk-row';

    const letter = document.createElement('span');
    letter.className = 'disk-letter';
    letter.textContent = v.letter;

    const text = document.createElement('div');
    text.className = 'disk-text';

    const title = document.createElement('div');
    title.className = 'disk-title';
    title.textContent = (v.model && v.model.length > 0 ? v.model : 'модель неизвестна')
      + (v.isSystem ? ' · системный' : '');

    const desc = document.createElement('div');
    desc.className = 'disk-desc';

    const bits = [v.mediaName];
    if (v.totalBytes > 0) bits.push('всего ' + fmtBytes(v.totalBytes));
    if (v.totalBytes > 0) bits.push('свободно ' + fmtBytes(v.freeBytes));
    if (v.fileSystem) bits.push(v.fileSystem);
    desc.textContent = bits.join(' · ');

    text.appendChild(title);
    text.appendChild(desc);

    const badge = document.createElement('span');
    const kind = v.media === 'Ssd' ? 'ssd' : (v.media === 'Hdd' ? 'hdd' : 'unknown');
    badge.className = 'disk-badge ' + kind;
    badge.textContent = v.mediaName;

    row.appendChild(letter);
    row.appendChild(text);
    row.appendChild(badge);

    if (withButtons) {
      const button = document.createElement('button');
      button.className = 'neon-btn small';
      button.disabled = !report.diskTools;

      const glow = document.createElement('span');
      glow.className = 'glow';
      const txt = document.createElement('span');
      txt.className = 'txt';
      txt.textContent = v.action || 'Обслужить';

      button.appendChild(glow);
      button.appendChild(txt);
      button.title = (v.hint || '') + (report.diskTools ? '' : ' (недоступно)');
      button.addEventListener('click', function () { optimizeDisk(v.letter); });

      row.appendChild(button);
    }

    container.appendChild(row);
  });

  if (!summary) return;

  summary.textContent = withButtons
    ? (report.diskTools
      ? (report.hasSsd && report.hasHdd
        ? 'SSD — только TRIM, HDD — дефрагментация'
        : (report.hasSsd ? 'только TRIM: жёстких дисков нет' : 'только дефрагментация: SSD нет'))
      : 'средство обслуживания дисков недоступно')
    : (volumes.length + ' ' + plural(volumes.length, 'накопитель', 'накопителя', 'накопителей'));
}

function setSysBusy(msg) {
  const el = $('sysHint');
  el.innerHTML = '';

  if (msg.busy) {
    const dot = document.createElement('span');
    dot.className = 'spin';
    el.appendChild(dot);
  }

  el.appendChild(document.createTextNode(msg.text || (msg.busy ? 'работаю…' : '')));
  el.classList.remove('warn');
}

function setDiskBusy(busy, text) {
  disk.busy = !!busy;

  const el = $('optHint');
  el.innerHTML = '';

  if (busy) {
    const dot = document.createElement('span');
    dot.className = 'spin';
    el.appendChild(dot);
  }

  el.appendChild(document.createTextNode(text || ''));
  el.classList.remove('warn');

  document.querySelectorAll('#optDisks .neon-btn').forEach(function (b) {
    b.disabled = busy;
  });
}

function setDiskStatus(text) {
  if (!disk.busy) return;

  const el = $('optHint');
  el.innerHTML = '';

  const dot = document.createElement('span');
  dot.className = 'spin';
  el.appendChild(dot);
  el.appendChild(document.createTextNode(text || ''));
}

function showDiskResult(msg) {
  const el = $('optHint');
  el.innerHTML = '';
  el.textContent = (msg.text || '') + (msg.detail ? ' — ' + msg.detail : '');
  el.classList.toggle('warn', !!msg.warn);
}
/* ---------- связь с C# ---------- */

function onMessage(e) {
  const msg = e.data;
  if (!msg || typeof msg !== 'object') return;

  // Эффекты для операций всех вкладок — в одном месте.
  trackFxMessage(msg);

  if (msg.type === 'data') {
    render(msg.payload || {});
  } else if (msg.type === 'hint') {
    setHint(msg.text, msg.warn, msg.elevate);
  } else if (msg.type === 'state') {
    setUiActive(!!msg.active);
  } else if (msg.type === 'cleanupList') {
    renderCleanList(msg.items, msg.elevated);
  } else if (msg.type === 'cleanupItem') {
    updateCleanItem(msg);
  } else if (msg.type === 'cleanupFreed') {
    updateCleanFreed(msg);
  } else if (msg.type === 'cleanupStatus') {
    setCleanStatus(msg.text, msg.busy);
  } else if (msg.type === 'cleanupStart') {
    fxStart(msg.phase === 'scan');
  } else if (msg.type === 'cleanupDone') {
    setCleanStatus(msg.phase === 'scan' ? 'анализ завершён' : 'очистка завершена', false);

    if (msg.phase === 'clean') {
      fxFinish(msg.freed);
    } else {
      let found = 0;
      for (const k in clean.sizes) found += clean.sizes[k] || 0;

      fxEnd({
        big: fmtBytes(found),
        caption: 'можно освободить',
        tone: 'soft'
      });
    }

    renderStats(msg.stats);
  } else if (msg.type === 'optState') {
    renderOptState(msg);
  } else if (msg.type === 'optBusy') {
    setOptBusy(msg.busy, msg.text);
  } else if (msg.type === 'optResult') {
    showOptResult(msg.ok, msg.warn, msg.text);
  } else if (msg.type === 'autoStep') {
    autoStep(msg);
  } else if (msg.type === 'autoBusy') {
    autoBusy(msg);
  } else if (msg.type === 'autoDone') {
    autoDone(msg);
  } else if (msg.type === 'gameState') {
    renderGameState(msg);
  } else if (msg.type === 'gameStep') {
    gameStep(msg);
  } else if (msg.type === 'gameBusy') {
    gameBusy(msg);
  } else if (msg.type === 'gameDone') {
    gameDone(msg);
  } else if (msg.type === 'browserList') {
    showBrowserDialog(msg);
  } else if (msg.type === 'gameLibrary') {
    renderGameLibrary(msg);
  } else if (msg.type === 'settings') {
    renderSettings(msg);
  } else if (msg.type === 'setBusy') {
    setSetBusy(msg);
  } else if (msg.type === 'setResult') {
    showSetResult(msg);
  } else if (msg.type === 'theme') {
    applyTheme(msg.theme);

    if (msg.accent) {
      applyAccent(msg.accent);
    }
  } else if (msg.type === 'system') {
    renderSystem(msg.report);
  } else if (msg.type === 'systemBusy') {
    setSysBusy(msg);
  } else if (msg.type === 'systemError') {
    $('sysHint').textContent = 'Не удалось определить железо: ' + (msg.text || '');
  } else if (msg.type === 'diskBusy') {
    setDiskBusy(msg.busy, msg.text);
  } else if (msg.type === 'diskStatus') {
    setDiskStatus(msg.text);
  } else if (msg.type === 'diskResult') {
    showDiskResult(msg);
  } else if (msg.type === 'admin') {
    $('adminBanner').style.display = msg.ok ? 'none' : 'flex';
  } else if (msg.type === 'miniAsk') {
    showMiniAsk();
  } else if (msg.type === 'miniState') {
    applyMiniState(!!msg.on);
  } else if (msg.type === 'stats') {
    renderStats(msg.stats);
  } else if (msg.type === 'gameStatus') {
    gameBusy(msg);
  }
}

if (window.chrome && window.chrome.webview) {
  window.chrome.webview.addEventListener('message', onMessage);
}

/* ---------- кнопки и перетаскивание окна ---------- */

/* ---------- вкладки ---------- */

// Анимации идут, только когда окно в фокусе. Скрытую вкладку гасить
// отдельно не нужно: элементы с display:none браузер и так не рисует,
// а вот замороженная на первом кадре анимация делала бы плашку невидимой.
function applyRunState() {
  const run = uiActive;
  document.body.classList.toggle('paused', !run);

  if (run && historyDirty) {
    historyDirty = false;
    drawHistory();
  }
}

function switchTab(name) {
document.querySelectorAll('.tab').forEach(function (tab) {
    tab.classList.toggle('active', tab.dataset.page === name);
  });

  document.querySelectorAll('.page').forEach(function (page) {
    page.classList.toggle('active', page.id === 'page-' + name);
  });

  animateTab('page-' + name);
  moveTabSweep();

  monitorVisible = (name === 'monitor');

  if (name === 'settings') {
    requestSettings();
  }

  if (name === 'games' && !game.scanned) {
    game.scanned = true;
    findGame();
    window.chrome.webview.postMessage({ action: 'gamesList' });
  }

  if (name === 'system' || name === 'optimize') {
    requestSystem();
  }

  if (name === 'optimize') {
    requestOptState();
  }

  if (name === 'clean') {
    requestCleanList();
  }

  // Скрытый canvas не имеет размеров, поэтому при возврате
  // график нужно нарисовать заново.
  if (monitorVisible) {
    historyDirty = false;
    drawHistory();
  }

  applyRunState();
}

/* ---------- вкладка «Очистка» ---------- */

const clean = {
  loaded: false,
  busy: false,
  items: [],
  rows: {},
  sizes: {},
  freed: {},
  skipped: {}
};

function plural(n, one, few, many) {
  const m10 = n % 10, m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return one;
  if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) return few;
  return many;
}

function fmtBytes(v) {
  if (typeof v !== 'number' || v <= 0) return '—';
  const units = ['Б', 'КБ', 'МБ', 'ГБ', 'ТБ'];
  let i = 0, n = v;
  while (n >= 1024 && i < units.length - 1) { n /= 1024; i++; }
  const digits = (i === 0 || n >= 100) ? 0 : 1;
  return n.toFixed(digits).replace('.', ',') + ' ' + units[i];
}

function fmtCount(v) {
  if (typeof v !== 'number' || v <= 0) return '';
  return v.toLocaleString('ru-RU') + ' ' + plural(v, 'файл', 'файла', 'файлов');
}

function requestCleanList() {
  if (!clean.loaded) post('cleanupList');
}

function renderCleanList(items, elevated) {
  clean.loaded = true;
  clean.items = items || [];
  clean.rows = {};

  const box = $('cleanList');
  box.innerHTML = '';

  clean.items.forEach(function (item) {
    const row = document.createElement('label');
    row.className = 'clean-row';

    const cb = document.createElement('input');
    cb.type = 'checkbox';
    cb.checked = true;

    const main = document.createElement('div');
    main.className = 'cr-main';

    const title = document.createElement('div');
    title.className = 'cr-title';
    title.appendChild(document.createTextNode(item.title));

    if (item.admin) {
      const badge = document.createElement('span');
      badge.className = 'cr-badge';
      badge.textContent = 'нужен админ';
      title.appendChild(badge);
    }

    const desc = document.createElement('div');
    desc.className = 'cr-desc';
    desc.textContent = item.desc || '';

    main.appendChild(title);
    main.appendChild(desc);

    if (item.paths && item.paths.length) {
      const paths = document.createElement('div');
      paths.className = 'cr-paths';
      paths.textContent = item.paths.join('  ·  ');
      paths.title = item.paths.join('\n');
      main.appendChild(paths);
    }

    const size = document.createElement('div');
    size.className = 'cr-size empty';
    size.textContent = '—';

    const files = document.createElement('div');
    files.className = 'cr-files';

    row.appendChild(cb);
    row.appendChild(main);
    row.appendChild(size);
    row.appendChild(files);
    box.appendChild(row);

    clean.rows[item.id] = { size: size, files: files };
  });

  updateCleanTotals();
  setCleanStatus(elevated
    ? clean.items.length + ' пунктов · права администратора есть'
    : clean.items.length + ' пунктов · без прав администратора', false);
}

function updateCleanItem(msg) {
  const row = clean.rows[msg.id];
  if (!row) return;

  clean.sizes[msg.id] = msg.size || 0;

  row.size.className = 'cr-size' + (msg.size > 0 ? '' : ' empty');
  row.size.textContent = fmtBytes(msg.size);
  row.files.textContent = fmtCount(msg.files);

  updateCleanTotals();
  fxScanItem();
}

function updateCleanFreed(msg) {
  const row = clean.rows[msg.id];
  if (!row) return;

  clean.freed[msg.id] = msg.freed || 0;
  clean.skipped[msg.id] = msg.skipped || 0;

  row.size.className = 'cr-size' + (msg.freed > 0 ? '' : ' empty');
  row.size.textContent = msg.freed > 0 ? '−' + fmtBytes(msg.freed) : 'нечего';
  row.files.textContent = msg.skipped > 0
    ? 'пропущено ' + msg.skipped
    : fmtCount(msg.files);

  updateCleanTotals();
  fxOnItem(msg);
}

function updateCleanTotals() {
  let found = 0, freed = 0, skipped = 0;

  for (const k in clean.sizes) found += clean.sizes[k] || 0;
  for (const k in clean.freed) freed += clean.freed[k] || 0;
  for (const k in clean.skipped) skipped += clean.skipped[k] || 0;

  $('totalFound').textContent = found > 0 ? fmtBytes(found) : '—';
  $('totalFreed').textContent = freed > 0 ? fmtBytes(freed) : '—';
  $('totalSkipped').textContent = skipped > 0 ? skipped.toLocaleString('ru-RU') : '—';
}

function setCleanStatus(text, busy) {
  clean.busy = !!busy;

  if (clean.busy) {
    fxOnStatus(text);
  }

  const el = $('cleanStatus');
  el.innerHTML = '';

  if (clean.busy) {
    const dot = document.createElement('span');
    dot.className = 'spin';
    el.appendChild(dot);
  }

  el.appendChild(document.createTextNode(text || ''));

  $('btnScan').disabled = clean.busy;
  $('btnClean').disabled = clean.busy;
}

$('btnScan').addEventListener('click', function () {
  if (clean.busy) return;
  setCleanStatus('анализ…', true);
  post('cleanupScan');
});

$('btnClean').addEventListener('click', function () {
  if (clean.busy) return;
  setCleanStatus('очистка…', true);
  post('cleanupRun');
});

// Саму анимацию запускает C# сообщением cleanupStart — так она
// включается и по кнопке, и по горячей клавише Ctrl+Shift+C.

/* ---------- вкладка «Оптимизация» ---------- */

const opt = {
  loaded: false,
  busy: false,
  resultShown: false,
  svcDirty: {},
  startupDirty: {},
  bgDirty: {},
  bgGlobalOriginal: false,
  bgGlobalDirty: false,
  pendingAction: null
};

function requestOptState() {
  if (opt.loaded) return;
  opt.loaded = true;
  post('optLoad');
}

function sendOptAction(name, restoreFirst) {
  if (opt.busy) return;

  fx.pending = 'optimize';
  window.chrome.webview.postMessage({
    action: 'optAction',
    name: name,
    restoreFirst: !!restoreFirst
  });
}

function countOptChanges() {
  return Object.keys(opt.svcDirty).length
       + Object.keys(opt.startupDirty).length
       + Object.keys(opt.bgDirty).length
       + (opt.bgGlobalDirty ? 1 : 0);
}

function updateOptHint() {
  if (opt.busy || opt.resultShown) return;

  const n = countOptChanges();
  const el = $('optHint');
  el.textContent = n > 0 ? ('К применению: ' + n) : 'Изменения применяются только по кнопке';
  el.classList.toggle('warn', n > 0);
}

function setOptBusy(busy, text) {
  opt.busy = !!busy;

  const el = $('optHint');

  if (opt.busy) {
    opt.resultShown = false;
    el.innerHTML = '';
    const dot = document.createElement('span');
    dot.className = 'spin';
    el.appendChild(dot);
    el.appendChild(document.createTextNode(text || 'работаю…'));
    el.classList.remove('warn');
  } else if (!opt.resultShown) {
    el.textContent = 'Изменения применяются только по кнопке';
    el.classList.remove('warn');
  }

  $('btnApply').disabled = opt.busy;
}

function showOptResult(ok, warn, text) {
  opt.resultShown = true;

  const el = $('optHint');
  el.innerHTML = '';
  el.textContent = text || '';
  el.classList.toggle('warn', !!warn);
}

function makeOptRow(checkbox, titleText, badgeText, badgeClass, descText, stateNode, locked) {
  const row = document.createElement('label');
  row.className = 'opt-row' + (locked ? ' locked' : '');

  const main = document.createElement('div');
  main.className = 'or-main';

  const title = document.createElement('div');
  title.className = 'or-title';
  title.appendChild(document.createTextNode(titleText));

  if (badgeText) {
    const badge = document.createElement('span');
    badge.className = 'or-badge' + (badgeClass ? ' ' + badgeClass : '');
    badge.textContent = badgeText;
    title.appendChild(badge);
  }

  const desc = document.createElement('div');
  desc.className = 'or-desc';
  desc.textContent = descText || '';

  main.appendChild(title);
  main.appendChild(desc);

  const right = document.createElement('div');
  right.className = 'or-state';
  right.appendChild(stateNode);

  row.appendChild(checkbox);
  row.appendChild(main);
  row.appendChild(right);
  return row;
}

function svcStateNode(s) {
  const span = document.createElement('span');

  if (s.startMode === 'отключена') {
    span.className = 'off';
    span.textContent = 'отключена';
  } else if (s.running) {
    span.className = 'run';
    span.textContent = 'работает · ' + s.startMode;
  } else {
    span.className = 'stop';
    span.textContent = 'остановлена · ' + s.startMode;
  }

  return span;
}

function renderServices(list) {
  const box = $('svcList');
  box.innerHTML = '';

  list.forEach(function (s) {
    const disabledNow = s.startMode === 'отключена';

    const cb = document.createElement('input');
    cb.type = 'checkbox';
    cb.checked = disabledNow;
    cb.disabled = !!s.critical;

    cb.addEventListener('change', function () {
      if (cb.checked === disabledNow) {
        delete opt.svcDirty[s.name];
      } else {
        opt.svcDirty[s.name] = cb.checked;
      }
      updateOptHint();
    });

    const row = makeOptRow(cb, s.title, s.critical ? 'критичная' : s.name,
      s.critical ? 'lock' : '', s.about, svcStateNode(s), s.critical);

    if (s.critical) {
      row.addEventListener('click', function (ev) {
        ev.preventDefault();
        showOptResult(false, true,
          '«' + s.title + '» — критичная служба. Её отключение сломает работу Windows, поэтому она защищена.');
      });
    }

    box.appendChild(row);
  });
}

function renderStartup(list) {
  const box = $('startupList');
  box.innerHTML = '';

  list.forEach(function (item) {
    const wasEnabled = !!item.enabled;

    const cb = document.createElement('input');
    cb.type = 'checkbox';
    cb.checked = wasEnabled;

    cb.addEventListener('change', function () {
      if (cb.checked === wasEnabled) {
        delete opt.startupDirty[item.id];
      } else {
        opt.startupDirty[item.id] = cb.checked;
      }
      updateOptHint();
    });

    const span = document.createElement('span');
    span.className = wasEnabled ? 'run' : 'off';
    span.textContent = wasEnabled ? item.source : 'отключена';

    const short = item.command.length > 95 ? item.command.slice(0, 95) + '…' : item.command;
    box.appendChild(makeOptRow(cb, item.name, '', '', short, span, false));
  });
}

function renderBackground(list) {
  const box = $('bgList');
  box.innerHTML = '';

  list.forEach(function (app) {
    const wasEnabled = !!app.enabled;

    const cb = document.createElement('input');
    cb.type = 'checkbox';
    cb.checked = wasEnabled;

    cb.addEventListener('change', function () {
      if (cb.checked === wasEnabled) {
        delete opt.bgDirty[app.package];
      } else {
        opt.bgDirty[app.package] = cb.checked;
      }
      updateOptHint();
    });

    const span = document.createElement('span');
    span.className = wasEnabled ? 'run' : 'off';
    span.textContent = wasEnabled ? 'в фоне' : 'отключено';

    box.appendChild(makeOptRow(cb, app.title, '', '', app.package, span, false));
  });
}

function renderOptState(msg) {
  opt.svcDirty = {};
  opt.startupDirty = {};
  opt.bgDirty = {};
  opt.bgGlobalDirty = false;
  opt.bgGlobalOriginal = !!msg.backgroundGlobal;

  renderServices(msg.services || []);
  renderStartup(msg.startup || []);
  renderBackground(msg.background || []);

  $('bgGlobal').checked = opt.bgGlobalOriginal;

  $('powerState').textContent = 'схема: ' + (msg.power || '—');
  $('visualState').textContent = msg.visualDisabled
    ? 'сейчас отключены — система работает быстрее'
    : 'сейчас включены';
  $('storageState').textContent = msg.storageEnabled
    ? 'сейчас включён — Windows сам чистит временные файлы'
    : 'сейчас выключен';

  $('svcSummary').textContent = (msg.services || []).length + ' служб';
  $('startupSummary').textContent = (msg.startup || []).length + ' программ';
  $('bgSummary').textContent = (msg.background || []).length + ' приложений';

  opt.resultShown = false;
  updateOptHint();
}

function applyOptimization(restoreFirst) {
  fx.pending = 'optimize';
  const services = [];
  for (const name in opt.svcDirty) {
    services.push({ name: name, disable: opt.svcDirty[name] });
  }

  const startup = [];
  for (const id in opt.startupDirty) {
    startup.push({ id: id, enabled: opt.startupDirty[id] });
  }

  const background = [];
  for (const pkg in opt.bgDirty) {
    background.push({ package: pkg, enabled: opt.bgDirty[pkg] });
  }

  window.chrome.webview.postMessage({
    action: 'optApply',
    restoreFirst: !!restoreFirst,
    services: services,
    startup: startup,
    background: background,
    backgroundGlobal: opt.bgGlobalDirty ? $('bgGlobal').checked : null
  });
}

function askBeforeChanges(actionName) {
  if (opt.busy) return;

  if (actionName === null && countOptChanges() === 0) {
    showOptResult(true, false, 'Готово. Менять было нечего');
    return;
  }

  if (actionName === 'restore') {
    sendOptAction('restore', false);
    return;
  }

  opt.pendingAction = actionName;
  $('optModal').classList.add('show');
}

$('btnApply').addEventListener('click', function () { askBeforeChanges(null); });

$('btnModalRestore').addEventListener('click', function () {
  const pending = opt.pendingAction;
  opt.pendingAction = null;
  $('optModal').classList.remove('show');

  if (pending) {
    sendOptAction(pending, true);
  } else {
    applyOptimization(true);
  }
});

$('btnModalSkip').addEventListener('click', function () {
  const pending = opt.pendingAction;
  opt.pendingAction = null;
  $('optModal').classList.remove('show');

  if (pending) {
    sendOptAction(pending, false);
  } else {
    applyOptimization(false);
  }
});

$('btnModalCancel').addEventListener('click', function () {
  opt.pendingAction = null;
  $('optModal').classList.remove('show');
});

$('bgGlobal').addEventListener('change', function () {
  opt.bgGlobalDirty = ($('bgGlobal').checked !== opt.bgGlobalOriginal);
  updateOptHint();
});

$('btnPowerHigh').addEventListener('click', function () { askBeforeChanges('powerHigh'); });
$('btnPowerBalanced').addEventListener('click', function () { askBeforeChanges('powerBalanced'); });
$('btnVisualOff').addEventListener('click', function () { askBeforeChanges('visualOff'); });
$('btnVisualOn').addEventListener('click', function () { askBeforeChanges('visualOn'); });
$('btnStorageOn').addEventListener('click', function () { askBeforeChanges('storageOn'); });
$('btnStorageOff').addEventListener('click', function () { askBeforeChanges('storageOff'); });
$('btnRestore').addEventListener('click', function () { askBeforeChanges('restore'); });

/* ---------- авто-оптимизация ---------- */

function runAutoOptimization() {
  if (opt.busy) return;

  $('autoReport').innerHTML = '';
  $('autoStatus').textContent = 'Готовлюсь…';
  $('btnAutoClose').disabled = true;
  $('autoModal').classList.add('show');

  window.chrome.webview.postMessage({ action: 'autoRun' });
}

function autoStep(msg) {
  appendReport('autoReport', msg);
}

function appendReport(boxId, msg) {
  const row = document.createElement('div');
  row.className = 'ar-row ' + (msg.icon || 'ok');

  const icon = document.createElement('span');
  icon.className = 'ar-icon';
  icon.textContent = msg.icon === 'fail' ? '\u2715' : (msg.icon === 'skip' ? '\u2013' : '\u2713');

  const main = document.createElement('div');
  main.className = 'ar-main';

  const title = document.createElement('div');
  title.className = 'ar-title';
  title.textContent = msg.title || '';

  const text = document.createElement('div');
  text.className = 'ar-text';
  text.textContent = msg.text || '';

  main.appendChild(title);
  main.appendChild(text);
  row.appendChild(icon);
  row.appendChild(main);

  const box = $(boxId);
  box.appendChild(row);
  box.scrollTop = box.scrollHeight;
}

function autoBusy(msg) {
  opt.busy = !!msg.busy;
  $('btnAuto').disabled = opt.busy;

  const el = $('autoStatus');
  el.innerHTML = '';

  if (opt.busy) {
    const dot = document.createElement('span');
    dot.className = 'spin';
    el.appendChild(dot);
    el.appendChild(document.createTextNode(msg.text || 'работаю…'));
  } else {
    el.textContent = 'Готово';
  }
}

function autoDone(msg) {
  opt.busy = false;
  $('btnAuto').disabled = false;
  $('btnAutoClose').disabled = false;

  $('autoStatus').textContent = msg.error
    ? ('Ошибка: ' + msg.error)
    : ('Готово. Освобождено: ' + (msg.freedText || '0 Б'));
}

$('btnAuto').addEventListener('click', runAutoOptimization);

$('btnAutoClose').addEventListener('click', function () {
  $('autoModal').classList.remove('show');
});

/* ---------- вкладка «Игры» ---------- */

const game = {
  busy: false,
  reportShown: false,
  scanned: false,
  games: [],
  checked: {},
  targetPid: 0,
  browsers: [],
  browserChecked: {}
};

function findGame() {
  if (game.busy) return;

  $('gameStatus').textContent = 'ищу…';
  window.chrome.webview.postMessage({ action: 'gamesScan' });
}

function renderGameState(msg) {
  game.games = msg.games || [];
  game.targetPid = game.games.length > 0 ? game.games[0].pid : 0;

  const box = $('gameFound');
  box.innerHTML = '';

  if (game.games.length === 0) {
    const empty = document.createElement('div');
    empty.className = 'game-empty';
    empty.textContent = 'Игра не найдена. Запустите игру и нажмите «Найти игру» ещё раз.';
    box.appendChild(empty);
    $('gameStatus').textContent = 'игра не найдена';
  } else {
    game.games.forEach(function (g, index) {
      const row = document.createElement('div');
      row.className = 'game-item' + (index === 0 ? ' primary' : '');

      const name = document.createElement('div');
      name.className = 'gi-name';
      name.textContent = g.title + (g.guess ? ' — определено по папке' : '');

      const meta = document.createElement('div');
      meta.className = 'gi-meta';
      meta.textContent = g.exe + ' · PID ' + g.pid + ' · ' + g.memoryMb + ' МБ'
        + (g.path ? ' · ' + g.path : '');

      row.appendChild(name);
      row.appendChild(meta);
      box.appendChild(row);
    });

    $('gameStatus').textContent = 'найдено игр: ' + game.games.length;
  }

  renderBgProcesses(msg.processes || []);
}

function renderBgProcesses(list) {
  const box = $('bgProcList');
  box.innerHTML = '';
  game.checked = {};

  if (list.length === 0) {
    const empty = document.createElement('div');
    empty.className = 'game-empty';
    empty.textContent = 'Лишних программ не найдено — закрывать нечего.';
    box.appendChild(empty);
    $('bgProcSummary').textContent = 'нечего закрывать';
    return;
  }

  let totalMb = 0;

  list.forEach(function (p) {
    game.checked[p.pid] = true;
    totalMb += p.memoryMb;

    const cb = document.createElement('input');
    cb.type = 'checkbox';
    cb.checked = true;

    cb.addEventListener('change', function () {
      game.checked[p.pid] = cb.checked;
    });

    const span = document.createElement('span');
    span.className = 'run';
    span.textContent = p.category;

    box.appendChild(makeOptRow(cb, p.name, '', '', p.title + ' · ' + p.memoryMb + ' МБ', span, false));
  });

  $('bgProcSummary').textContent = list.length + ' программ · ' + totalMb + ' МБ';
}

function boostGame() {
  if (game.busy) return;
  window.chrome.webview.postMessage({ action: 'gamesBrowsers' });
}

function showBrowserDialog(msg) {
  game.browsers = msg.items || [];
  game.browserChecked = {};

  const box = $('browserList');
  box.innerHTML = '';

  // Браузеров нет — спрашивать нечего, сразу запускаем.
  if (game.browsers.length === 0) {
    startBoost([]);
    return;
  }

  game.browsers.forEach(function (b) {
    game.browserChecked[b.pid] = true;

    const cb = document.createElement('input');
    cb.type = 'checkbox';
    cb.checked = true;

    cb.addEventListener('change', function () {
      game.browserChecked[b.pid] = cb.checked;
    });

    const span = document.createElement('span');
    span.className = 'run';
    span.textContent = b.memoryMb + ' МБ';

    box.appendChild(makeOptRow(cb, b.title, '', '', b.name, span, false));
  });

  $('browserModal').classList.add('show');
}

function startBoost(browserPids) {
  const pids = [];
  for (const key in game.checked) {
    if (game.checked[key]) pids.push(parseInt(key, 10));
  }

  $('gameReport').innerHTML = '';
  $('gameReportStatus').textContent = 'Начинаю…';
  $('btnGameReportClose').disabled = true;
  $('gameModal').classList.add('show');

  window.chrome.webview.postMessage({
    action: 'gamesBoost',
    gamePid: game.targetPid || 0,
    pids: pids,
    browserPids: browserPids || []
  });
}

function restoreGame() {
  if (game.busy) return;

  $('gameReport').innerHTML = '';
  $('gameReportStatus').textContent = 'Начинаю…';
  $('btnGameReportClose').disabled = true;
  $('gameModal').classList.add('show');

  window.chrome.webview.postMessage({ action: 'gamesRestore' });
}

function gameStep(msg) {
  appendReport('gameReport', msg);
}

function gameBusy(msg) {
  game.busy = !!msg.busy;
  $('btnFindGame').disabled = game.busy;
  $('btnGameBoost').disabled = game.busy;

  const el = $('gameReportStatus');

  if (game.busy) {
    game.reportShown = false;
    el.innerHTML = '';
    const dot = document.createElement('span');
    dot.className = 'spin';
    el.appendChild(dot);
    el.appendChild(document.createTextNode(msg.text || 'работаю…'));
  } else if (!game.reportShown) {
    el.textContent = 'Готово';
  }
}

function gameDone(msg) {
  game.reportShown = true;
  game.busy = false;
  $('btnFindGame').disabled = false;
  $('btnGameBoost').disabled = false;
  $('btnGameReportClose').disabled = false;
  $('gameReportStatus').textContent = msg.text || 'Готово';
}

$('btnBrowsersYes').addEventListener('click', function () {
  const chosen = [];

  for (const key in game.browserChecked) {
    if (game.browserChecked[key]) {
      chosen.push(parseInt(key, 10));
    }
  }

  $('browserModal').classList.remove('show');
  startBoost(chosen);
});

$('btnBrowsersNo').addEventListener('click', function () {
  $('browserModal').classList.remove('show');
  startBoost([]);
});

$('btnFindGame').addEventListener('click', findGame);
$('btnGameBoost').addEventListener('click', boostGame);
$('btnGameRestore').addEventListener('click', restoreGame);

$('btnGameReportClose').addEventListener('click', function () {
  $('gameModal').classList.remove('show');
});

/* ---------- вкладка «Настройки» ---------- */

const cfg = { loaded: false, theme: 'dark' };

function requestSettings() {
  window.chrome.webview.postMessage({ action: 'settingsLoad' });
}

function applyTheme(theme) {
  cfg.theme = theme === 'light' ? 'light' : 'dark';
  document.body.classList.toggle('light', cfg.theme === 'light');
  $('themeState').textContent = cfg.theme === 'light' ? 'светлая' : 'тёмная';
}

function saveSettings(theme, autorun) {
  const payload = { action: 'settingsSave' };

  if (theme !== undefined) {
    payload.theme = theme;
  }

  if (autorun !== undefined) {
    payload.autorun = autorun;
  }

  window.chrome.webview.postMessage(payload);
}

function renderRestorePoints(points) {
  const box = $('rpList');
  box.innerHTML = '';

  if (points.length === 0) {
    const empty = document.createElement('div');
    empty.className = 'game-empty';
    empty.textContent = 'Точек нет. Возможно, защита системы выключена на диске C:.';
    box.appendChild(empty);
    $('rpState').textContent = 'нет точек';
    return;
  }

  points.forEach(function (p) {
    const row = document.createElement('div');
    row.className = 'log-line';

    const time = document.createElement('span');
    time.className = 'log-time';
    time.textContent = p.created;

    const txt = document.createElement('span');
    txt.className = 'log-text';
    txt.textContent = (p.description || 'Точка') + ' · №' + p.number;

    row.appendChild(time);
    row.appendChild(txt);
    box.appendChild(row);
  });

  $('rpState').textContent = 'точек: ' + points.length;
}

function renderLog(lines) {
  const box = $('logList');
  box.innerHTML = '';

  if (lines.length === 0) {
    const empty = document.createElement('div');
    empty.className = 'game-empty';
    empty.textContent = 'Журнал пуст — здесь появятся выполненные действия.';
    box.appendChild(empty);
    $('logState').textContent = 'пусто';
    return;
  }

  lines.forEach(function (l) {
    const row = document.createElement('div');
    row.className = 'log-line';

    const time = document.createElement('span');
    time.className = 'log-time';
    time.textContent = l.time;

    const cat = document.createElement('span');
    cat.className = 'log-cat';
    cat.textContent = l.category;

    const txt = document.createElement('span');
    txt.className = 'log-text';
    txt.textContent = l.text;

    row.appendChild(time);
    row.appendChild(cat);
    row.appendChild(txt);
    box.appendChild(row);
  });

  $('logState').textContent = 'записей: ' + lines.length;
}

function renderSettings(msg) {
  applyTheme(msg.theme || 'dark');
  applyAccent(msg.accent || 'red');
  applyMiniState(!!msg.mini);
  renderStats(msg.stats);

  soundOn = msg.sound !== false;
  $('chkSound').checked = soundOn;

  renderTemperature(msg);

  $('chkAutorun').checked = !!msg.autorun;
  $('autorunState').textContent = msg.autorun ? 'включён' : 'выключен';

  renderRestorePoints(msg.points || []);
  renderLog(msg.log || []);

  $('aboutVersion').textContent = 'версия ' + (msg.version || '—');
  $('abVersion').textContent = msg.version || '—';
  $('abAuthor').textContent = msg.author || '—';
  $('abSettings').textContent = msg.settingsPath || '—';
  $('logPath').textContent = msg.logPath ? ('Файл: ' + msg.logPath) : '';

  if (msg.repo) {
    $('abRepoRow').style.display = '';
    $('abRepo').textContent = msg.repo;
  } else {
    $('abRepoRow').style.display = 'none';
  }
}

/// Карточка «Температура процессора»: откуда берём и что делать, если никак.
function renderTemperature(msg) {
  const source = msg.tempSource || '';

  $('tempSource').textContent = source
    ? ('Источник: ' + source)
    : 'Датчик температуры процессора не найден';

  $('tempState').textContent = source ? 'работает' : 'нет данных';

  $('tempNote').textContent = msg.tempNote
    || (source
      ? 'Число показывается на вкладке «Мониторинг», в карточке процессора.'
      : '');

  $('chkAfterburner').checked = msg.useAfterburner !== false;

  if (msg.afterburnerInstalled) {
    $('abPath').textContent = 'MSI Afterburner: ' + (msg.afterburnerPath || 'найден')
      + (msg.afterburnerRunning ? ' · запущен' : ' · не запущен');
    $('btnAbStart').disabled = !!msg.afterburnerRunning;
  } else {
    $('abPath').textContent = 'MSI Afterburner не установлен — взять температуру неоткуда.';
    $('btnAbStart').disabled = true;
  }
}

function setSetBusy(msg) {
  const el = $('setHint');
  el.innerHTML = '';

  if (msg.busy) {
    const dot = document.createElement('span');
    dot.className = 'spin';
    el.appendChild(dot);
    el.appendChild(document.createTextNode(msg.text || 'работаю…'));
    el.classList.remove('warn');
  }
}

function showSetResult(msg) {
  const el = $('setHint');
  el.innerHTML = '';
  el.textContent = msg.text || '';
  el.classList.toggle('warn', !!msg.warn);

  const gameHint = $('gameHint');
  gameHint.textContent = msg.text || 'Найдите игру, отметьте лишние процессы и нажмите «Оптимизировать»';
  gameHint.classList.toggle('warn', !!msg.warn);
}

$('btnThemeDark').addEventListener('click', function () {
  applyTheme('dark');
  saveSettings('dark');
});

$('btnThemeLight').addEventListener('click', function () {
  applyTheme('light');
  saveSettings('light');
});

$('chkAutorun').addEventListener('change', function () {
  saveSettings(undefined, $('chkAutorun').checked);
});

$('btnClearLog').addEventListener('click', function () {
  window.chrome.webview.postMessage({ action: 'settingsClearLog' });
});

$('btnMakeRestore').addEventListener('click', function () {
  fx.pending = 'restore';
  window.chrome.webview.postMessage({ action: 'settingsRestorePoint' });
});

$('btnCheckUpdates').addEventListener('click', function () {
  window.chrome.webview.postMessage({ action: 'settingsUpdates' });
});

/* ---------- кнопки новых настроек ---------- */

function saveSetting(key, value) {
  const payload = { action: 'settingsSave' };
  payload[key] = value;
  window.chrome.webview.postMessage(payload);
}

$('chkSound').addEventListener('change', function () {
  soundOn = $('chkSound').checked;
  saveSetting('sound', soundOn);
});

$('chkMini').addEventListener('change', function () {
  const on = $('chkMini').checked;
  window.chrome.webview.postMessage({ action: 'miniSet', on: on });
});

$('btnStatsReset').addEventListener('click', function () {
  post('statsReset');
});

$('btnRescan').addEventListener('click', function () {
  post('systemRescan');
});

$('btnElevateBanner').addEventListener('click', function () {
  post('elevate');
});

$('btnTempRetry').addEventListener('click', function () {
  post('tempRetry');
});

$('btnAbStart').addEventListener('click', function () {
  post('abStart');
});

$('chkAfterburner').addEventListener('change', function () {
  saveSetting('useAfterburner', $('chkAfterburner').checked);
});

$('btnMiniYes').addEventListener('click', function () {
  $('miniAskModal').classList.remove('show');
  window.chrome.webview.postMessage({ action: 'miniSet', on: true });
});

$('btnMiniNo').addEventListener('click', function () {
  $('miniAskModal').classList.remove('show');
  window.chrome.webview.postMessage({ action: 'miniAsked' });
});

/* ---------- игры: турбо, оверлей, библиотека, профили ---------- */

function showGameResult(text, warn) {
  const el = $('gameHint');
  el.textContent = text || 'Найдите игру, отметьте лишние процессы и нажмите «Оптимизировать»';
  el.classList.toggle('warn', !!warn);
}

function openGameReport(caption) {
  $('gameReport').innerHTML = '';
  $('gameReportStatus').textContent = caption || 'Начинаю…';
  $('btnGameReportClose').disabled = true;
  $('gameModal').classList.add('show');

  // Пока операция идёт, сверху будет анимация, а отчёт откроется после неё.
  fx.pending = 'game';
  fx.pendingTitle = (caption || 'Оптимизация под игру').replace(/…+$/, '');
}

function runTurbo() {
  if (game.busy) return;
  openGameReport('Турбо-режим…');
  window.chrome.webview.postMessage({ action: 'turboRun' });
}

function toggleOverlay() {
  window.chrome.webview.postMessage({ action: 'overlayToggle' });
}

function launchGame(appId, title) {
  if (game.busy) return;
  openGameReport('Запускаю ' + title + '…');
  window.chrome.webview.postMessage({ action: 'gamesLaunch', appId: appId });
}

function saveProfile() {
  const name = $('profName').value.trim();

  if (name.length === 0) {
    showGameResult('Введите название профиля', true);
    return;
  }

  window.chrome.webview.postMessage({ action: 'profileSave', name: name, gameTitle: '' });
  $('profName').value = '';
}

function applyProfile(name) {
  if (game.busy) return;
  openGameReport('Применяю профиль «' + name + '»…');
  window.chrome.webview.postMessage({ action: 'profileApply', name: name });
}

function deleteProfile(name) {
  window.chrome.webview.postMessage({ action: 'profileDelete', name: name });
}

function renderLibrary(items) {
  const box = $('gameLibList');
  box.innerHTML = '';

  if (items.length === 0) {
    const empty = document.createElement('div');
    empty.className = 'game-empty';
    empty.textContent = 'Одиночных игр не нашлось. Проверьте, что Steam установлен и игры скачаны.';
    box.appendChild(empty);
    $('libState').textContent = 'нечего запускать';
    return;
  }

  items.forEach(function (g) {
    const row = document.createElement('div');
    row.className = 'opt-row';

    const main = document.createElement('div');
    main.className = 'or-main';

    const title = document.createElement('div');
    title.className = 'or-title';
    title.textContent = g.title;

    const desc = document.createElement('div');
    desc.className = 'or-desc';
    desc.textContent = g.exePath || g.exe;

    main.appendChild(title);
    main.appendChild(desc);

    const btn = document.createElement('button');
    btn.className = 'neon-btn small';
    btn.innerHTML = '<span class="glow"></span><span class="txt">Погнали</span>';
    btn.addEventListener('click', function () { launchGame(g.appId, g.title); });

    row.appendChild(main);
    row.appendChild(btn);
    box.appendChild(row);
  });

  $('libState').textContent = 'найдено: ' + items.length;
}

function renderProfiles(profiles) {
  const box = $('profList');
  box.innerHTML = '';

  if (profiles.length === 0) {
    const empty = document.createElement('div');
    empty.className = 'game-empty';
    empty.textContent = 'Профилей пока нет. Введите название и нажмите «Сохранить».';
    box.appendChild(empty);
    $('profState').textContent = 'нет профилей';
    return;
  }

  profiles.forEach(function (p) {
    const row = document.createElement('div');
    row.className = 'opt-row';

    const main = document.createElement('div');
    main.className = 'or-main';

    const title = document.createElement('div');
    title.className = 'or-title';
    title.textContent = p.name;

    const desc = document.createElement('div');
    desc.className = 'or-desc';
    desc.textContent = 'питание, эффекты, Game Bar, память, фоновые процессы, приоритет';

    main.appendChild(title);
    main.appendChild(desc);

    const apply = document.createElement('button');
    apply.className = 'neon-btn small';
    apply.innerHTML = '<span class="glow"></span><span class="txt">Применить</span>';
    apply.addEventListener('click', function () { applyProfile(p.name); });

    const del = document.createElement('button');
    del.className = 'neon-btn small';
    del.innerHTML = '<span class="glow"></span><span class="txt">Удалить</span>';
    del.addEventListener('click', function () { deleteProfile(p.name); });

    row.appendChild(main);
    row.appendChild(apply);
    row.appendChild(del);
    box.appendChild(row);
  });

  $('profState').textContent = 'профилей: ' + profiles.length;
}

function renderGameLibrary(msg) {
  const banner = $('onlineBanner');

  if (msg.blocked) {
    banner.style.display = '';
    banner.textContent = 'Обнаружено: ' + (msg.reason || 'онлайн-игра') +
      '. Игровые функции отключены — чтобы не поймать бан, я не вмешиваюсь.';
  } else {
    banner.style.display = 'none';
  }

  $('overlayState').textContent = msg.rtss
    ? 'RTSS запущен — FPS будет точным'
    : 'для FPS запустите RTSS (идёт вместе с MSI Afterburner)';

  renderLibrary(msg.items || []);
  renderProfiles(msg.profiles || []);
}

$('btnTurbo').addEventListener('click', runTurbo);
$('btnOverlay').addEventListener('click', toggleOverlay);
$('btnSaveProfile').addEventListener('click', saveProfile);

document.querySelectorAll('.tab').forEach(function (tab) {
  tab.addEventListener('click', function () { switchTab(tab.dataset.page); });
});

$('btnCpu').addEventListener('click', () => post('refreshCpu'));
$('btnGpu').addEventListener('click', () => post('refreshGpu'));
$('btnElevate').addEventListener('click', () => post('elevate'));
$('btnMin').addEventListener('click', () => post('minimize'));
$('btnClose').addEventListener('click', () => post('close'));

$('titlebar').addEventListener('mousedown', e => {
  if (e.button !== 0) return;
  if (e.target.closest('.win-btn')) return;
  post('drag');
});

window.addEventListener('resize', () => drawHistory());

// Подстраховка: если сигнал от C# почему-то не придёт,
// спрятанное окно всё равно остановит анимации.
document.addEventListener('visibilitychange', () => {
  if (document.visibilityState === 'hidden') setUiActive(false);
});

/* ---------- если данные перестали приходить ---------- */

setInterval(() => {
  if (lastDataAt && Date.now() - lastDataAt > 4000) {
    document.body.classList.add('stale');
    $('statusText').textContent = 'нет данных';
  }
}, 1000);

/* ---------- стартовая отрисовка пустых колец ---------- */

// Полоска-подсветка, которая ездит под активной вкладкой.
$('tabs').insertAdjacentHTML('beforeend', '<span class="tab-sweep" id="tabSweep"></span>');

// Волна от нажатия на любую неоновую кнопку.
document.addEventListener('pointerdown', function (e) {
  const t = e.target;
  if (!t || typeof t.closest !== 'function') return;

  const btn = t.closest('.neon-btn');
  if (!btn || btn.disabled) return;

  rippleAt(btn, e.clientX, e.clientY);
});

window.addEventListener('resize', function () { moveTabSweep(); });

animateTab('page-monitor');
moveTabSweep();
setTimeout(moveTabSweep, 120);

buildCores(24);
drawHistory();
