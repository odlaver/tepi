(function () {
  "use strict";

  var TI = window.TepiIsland;
  var still = TI.still;
  function $(s, r) { return (r || document).querySelector(s); }
  function $$(s, r) { return Array.prototype.slice.call((r || document).querySelectorAll(s)); }
  function on(el, ev, fn, o) { if (el) el.addEventListener(ev, fn, o); }

  var EN = {
    "skip": "Skip to download",
    "nav.download": "Download",
    "hero.title": "A dynamic island for Windows.",
    "hero.lead": "It sits quietly at the top edge of your screen and grows when something happens: music, coding agents, deadlines, messages, timers.",
    "cta.download": "Download for Windows",
    "meta.free": "Free",
    "meta.os": "Windows 10 and 11",
    "note.platform": "Tepi is for Windows 10 and 11.",
    "hero.source": "Source code on GitHub",
    "hero.update": "Tepi 2.0 is the biggest Tepi update: Liquid Glass, Mini mode, and an engine that barely touches the CPU.",
    "alt.hero": "Tepi Overview panel: the song playing, a working agent, the next class, and messages",
    "live.title": "Alive when it matters.",
    "live.body": "Songs with lyrics that sweep word by word, incoming messages, finished agents, prayer times, volume, and battery. The island grows for a moment, then settles back.",
    "acts.label": "Island activities",
    "acts.pause": "Pause",
    "acts.play": "Play",
    "pill.lyric": "Synced lyrics",
    "pill.media": "Music",
    "pill.notif": "New message",
    "pill.done": "Agent finished",
    "pill.pray": "Prayer time",
    "pill.volume": "Volume",
    "pill.power": "Charging",
    "panels.title": "Eight pages, one gesture.",
    "panels.body": "The panel opens when the cursor rests on the island. Scrolling switches pages.",
    "panels.label": "Panel pages",
    "pg.home": "Overview",
    "pg.home.d": "What matters most, in one place",
    "pg.music": "Music",
    "pg.music.d": "Synced lyrics, spinning record, visualizer",
    "pg.agents": "Agents",
    "pg.class": "Classes",
    "pg.class.d": "Kulia, Notion, Todoist, iCal calendars",
    "pg.msg": "Messages",
    "pg.msg.d": "WhatsApp, Telegram, Discord, email",
    "pg.shelf": "Shelf",
    "pg.shelf.d": "Drag files in, drag them back out",
    "pg.ctl": "Controls",
    "pg.ctl.d": "Sound, display, Wi-Fi, Bluetooth, timer",
    "pg.rem": "Reminders",
    "pg.rem.d": "Prayer, water, eye breaks",
    "mini.eyebrow": "Mini mode",
    "mini.title": "Out of the way while you focus.",
    "mini.body": "The island shrinks to a small click-through sliver, so the browser tabs underneath stay clickable. Auto mode shrinks it when a browser is maximized or after a minute of quiet. Important alerts still come through.",
    "mini.eyes": "Its two eyes blink, follow the cursor, and react.",
    "mini.svg": "Mini sliver with two eyes",
    "mood.label": "Expressions",
    "mood.rest": "Blinking",
    "mood.right": "Following the cursor",
    "mood.happy": "Happy when an agent finishes",
    "mood.surprised": "Surprised by a message",
    "mood.sleepy": "Sleepy late at night",
    "agent.eyebrow": "Coding agents",
    "agent.title": "Your coding agents, always in view.",
    "agent.body": "Claude Code, Codex, Gemini CLI, and Antigravity. The island shows which agent is working, its plan step by step, and a summary when it is done.",
    "agent.approve.t": "Permissions, answered from the island",
    "agent.approve.d": "Permission requests from Claude Code and Codex appear in full with a colored diff. Allow, Always, or Deny without switching to the terminal.",
    "ap.always": "Always",
    "ap.deny": "Deny",
    "ap.allow": "Allow",
    "ap.ok": "Allowed. The agent carries on.",
    "ap.okall": "Allowed, and for identical requests in this session too.",
    "ap.no": "Denied.",
    "ap.again": "Replay",
    "ap.desc": "Sample request: Claude asks to edit Island.cs, replacing two lines with three.",
    "alt.plan": "Agents tab with a five-step plan",
    "agent.plan.t": "The plan",
    "agent.plan.d": "TodoWrite, update_plan, and write_todos show up as a segmented bar and a step list.",
    "alt.quota": "5-hour and 7-day quota row in the Agents tab",
    "quota.t": "AI agent quota",
    "quota.d": "Remaining 5-hour and 7-day limits with reset times, plus alerts at 80% and 95%.",
    "perf.eyebrow": "Performance",
    "perf.title": "Light as cotton.",
    "perf.body": "The island draws nothing while nothing moves. The glass reads the screen only when the backdrop changes.",
    "perf.idle.n": "0.22%",
    "perf.idle": "CPU at idle",
    "perf.idle.a": "1.7.0: 1.9%. 2.0: 0.22%.",
    "perf.idle.o": "1.9%",
    "perf.music.n": "2.1%",
    "perf.music": "CPU while music plays",
    "perf.music.a": "1.7.0: 7.0%. 2.0: 2.1%.",
    "perf.music.o": "7.0%",
    "perf.panel.n": "0.47%",
    "perf.panel": "CPU with the panel open",
    "perf.panel.a": "1.7.0: 7.3%. 2.0: 0.47%.",
    "perf.panel.o": "7.3%",
    "perf.mem.n": "3–5 MB",
    "perf.mem": "Memory at idle",
    "perf.mem.a": "1.7.0: 7 to 9 MB. 2.0: 3 to 5 MB.",
    "perf.mem.o": "7–9 MB",
    "perf.exe": "one exe, no installer.",
    "perf.frame": "Opening and closing the panel takes about 10 ms per frame at most, under the 16.7 ms a frame has at 60 fps.",
    "perf.note": "Measured on an Intel Core i3-1115G4 laptop, 60 seconds per condition. CPU percent is relative to one core.",
    "style.eyebrow": "Appearance",
    "style.title": "Make it yours.",
    "style.body": "Liquid Glass that reads the backdrop behind it, or the hazier Frost. Dark or light theme, an accent color, and theme packs with their own colors, emblem, mascot, and sounds.",
    "style.group": "Material and theme",
    "alt.liquid": "Controls panel in the Liquid Glass material",
    "alt.frost": "Controls panel in the Frost material",
    "alt.light": "Controls panel in the light theme",
    "style.liquid.d": "Clear glass that adapts to the brightness behind it",
    "style.frost.d": "Hazier, softer glass",
    "style.light": "Light",
    "style.light.d": "Every page follows",
    "style.pack": "Make your own theme pack",
    "foot.pack": "Theme packs",
    "win.eyebrow": "Windows",
    "win.title": "Windows that match.",
    "win.prefs": "Settings",
    "win.prefs.d": "Search, tidy groups, and every change applies instantly.",
    "win.tp.d": "Click-through while it runs, driven with Ctrl+Alt, with edges you drag to resize.",
    "win.bar.prefs": "Tepi settings",
    "win.bar.chat": "Chat with agent",
    "win.chat": "Agent chat",
    "win.chat.d": "Session status, plan, quota, prompt history, and templates.",
    "alt.tp": "Teleprompter being dragged wider",
    "video.title": "Tepi in 45 seconds.",
    "video.body": "Captions in the video are in Indonesian.",
    "more.title": "And there's more.",
    "more.shelf": "File shelf",
    "more.shelf.d": "Drag files onto the island, even onto the Mini sliver, then drag them out to another app.",
    "more.timer": "Timer and stopwatch",
    "more.timer.d": "25 or 50 minute focus, a free timer, and a stopwatch with laps.",
    "more.tasks": "Tasks and schedule",
    "more.tasks.d": "Kulia, Notion, Todoist, and iCal calendars in one place, with deadline alerts.",
    "more.remind": "Reminders",
    "more.remind.d": "Prayer times calculated on the laptop without internet, plus water, 20-20-20 eye breaks, and stretches.",
    "more.dl": "Downloads",
    "more.dl.d": "Download progress from Chrome, Edge, Firefox, Brave, and Opera, with speed.",
    "more.kb": "Full keyboard",
    "more.kb.d": "Ctrl+Alt+I to navigate. Toasts, tabs, buttons, and permission requests are read by Narrator or NVDA.",
    "more.lang": "Two languages",
    "more.lang.d": "Indonesian and English.",
    "more.script": "Scripts",
    "more.script.d": "Toasts and progress bars from any script through",
    "priv.eyebrow": "Privacy and security",
    "priv.title": "Stays on your laptop.",
    "priv.net": "Only the services it needs",
    "priv.net.d": "LRCLIB for lyrics and GitHub for version checks, both can be turned off. Kulia, Notion, Todoist, and calendars are contacted only if you connect them.",
    "priv.vault": "Encrypted tokens",
    "priv.vault.d": "Tokens and secret links are stored with DPAPI, sent only over HTTPS, and never written to logs.",
    "priv.hook": "Local hook server",
    "priv.hook.d": "Listens only on 127.0.0.1, with a random 48-character token and a random port on new installs.",
    "priv.ask": "Never approves on its own",
    "priv.ask.d": "If Tepi does not answer, the agent falls back to its own permission prompt.",
    "priv.update": "Verified updates",
    "priv.update.d": "A new exe is downloaded only when you click Update, then its SHA-256 is checked against the digest from GitHub.",
    "priv.open": "Open",
    "priv.open.d": "Full source code on GitHub under the MIT license.",
    "dl.title": "One exe. Free.",
    "dl.req": "Requirements",
    "dl.req.d": "Windows 10 version 2004 or later, or Windows 11, 64-bit. .NET Framework 4.8 ships with Windows.",
    "dl.ss.d": "The exe is not signed yet, so Windows SmartScreen may show a warning. Choose More info, then Run anyway.",
    "dl.copy": "Copy",
    "dl.copied": "Copied",
    "dl.releases": "All releases",
    "coffee.lead": "If Tepi helps you, buy it a coffee through QRIS.",
    "coffee.btn": "Buy Tepi a coffee",
    "coffee.short": "Buy a coffee",
    "coffee.alt": "QRIS code for Revaldo",
    "coffee.note": "Works with any bank or e-wallet app that supports QRIS.",
    "coffee.dl": "Download QRIS",
    "coffee.close": "Close",
    "foot.by": "Made by odlaver",
    "foot.social": "odlaver on social media",
    "foot.project": "Tepi project",
    "foot.src": "Source code",
    "foot.rel": "Releases",
    "foot.mit": "MIT License",
    "doc.title": "Tepi: a dynamic island for Windows",
    "doc.desc": "Tepi sits at the top edge of your Windows screen and grows when something happens: music and lyrics, coding agents, tasks, messages, timers. Free, one exe, open source."
  };
  // Indonesian strings not present in the markup
  var ID = {
    "dl.copied": "Tersalin",
    "acts.play": "Putar",
    "ap.ok": "Diizinkan. Agent lanjut bekerja.",
    "ap.okall": "Diizinkan, juga untuk permintaan yang persis sama di sesi ini.",
    "ap.no": "Ditolak.",
    "pg.home.d": "Yang paling penting, di satu tempat",
    "pg.music.d": "Lirik tersinkron, piringan, visualizer",
    "pg.class.d": "Kulia, Notion, Todoist, kalender iCal",
    "pg.msg.d": "WhatsApp, Telegram, Discord, email",
    "pg.shelf.d": "Seret berkas masuk, seret lagi keluar",
    "pg.ctl.d": "Suara, layar, Wi-Fi, Bluetooth, timer",
    "pg.rem.d": "Sholat, minum, istirahat mata",
    "style.liquid.d": "Kaca jernih yang menyesuaikan terang latar",
    "style.frost.d": "Kaca yang lebih berkabut dan lembut",
    "style.light.d": "Semua halaman ikut terang"
  };

  var html = document.documentElement;
  var lang = "id";
  var hooks = [];

  function saved() {
    try { return localStorage.getItem("tepi.lang"); } catch (e) { return null; }
  }

  // Remember Indonesian defaults from markup
  function capture() {
    $$("[data-i18n]").forEach(function (el) { ID[el.dataset.i18n] = el.textContent; });
    $$("[data-i18n-alt]").forEach(function (el) { ID[el.dataset.i18nAlt] = el.alt; });
    $$("[data-i18n-label]").forEach(function (el) { ID[el.dataset.i18nLabel] = el.getAttribute("aria-label"); });
    ID["doc.title"] = document.title;
    ID["doc.desc"] = $('meta[name="description"]').content;
  }

  function t(key) {
    var d = lang === "en" ? EN : ID;
    return d[key] !== undefined ? d[key] : ID[key];
  }

  function apply(next) {
    lang = next;
    html.lang = next;
    $$("[data-i18n]").forEach(function (el) { el.textContent = t(el.dataset.i18n); });
    $$("[data-i18n-alt]").forEach(function (el) { el.alt = t(el.dataset.i18nAlt); });
    $$("[data-i18n-label]").forEach(function (el) { el.setAttribute("aria-label", t(el.dataset.i18nLabel)); });
    document.title = t("doc.title");
    $('meta[name="description"]').content = t("doc.desc");
    var btn = $("#lang");
    btn.textContent = next === "en" ? "ID" : "EN";
    btn.setAttribute("aria-label", next === "en" ? "ID, ganti ke bahasa Indonesia" : "EN, switch to English");
    hooks.forEach(function (h) { h(); });
    try { localStorage.setItem("tepi.lang", next); } catch (e) { }
  }

  capture();
  var langs = navigator.languages && navigator.languages.length ? navigator.languages : [navigator.language || "id"];
  var first = saved() || (langs.some(function (l) { return /^(id|in)\b/i.test(l); }) ? "id" : "en");
  lang = first === "en" ? "en" : "id";
  on($("#lang"), "click", function () { apply(lang === "en" ? "id" : "en"); });

  // Island markup, shared by every instance
  var EYES = '<svg class="isle-eyes" viewBox="0 0 40 14" data-mood="rest" aria-hidden="true"><g class="gaze"><g class="lids">' +
    '<rect class="e rest" x="13" y="4.5" width="4" height="5" rx="1.7"/><rect class="e rest" x="22.75" y="4.5" width="4" height="5" rx="1.7"/>' +
    '<rect class="e wide" x="12.5" y="3.9" width="5" height="6.2" rx="2.2"/><rect class="e wide" x="22.25" y="3.9" width="5" height="6.2" rx="2.2"/>' +
    '<path class="e happy" d="M12.6 7.9Q15 4.7 17.4 7.9M22.35 7.9Q24.75 4.7 27.15 7.9"/>' +
    '<path class="e sleepy" d="M13.25 6.6h3.5v1.2a1.75 1.75 0 0 1-3.5 0zM23 6.6h3.5v1.2a1.75 1.75 0 0 1-3.5 0z"/>' +
    '<rect class="e shut" x="13.25" y="6.4" width="3.5" height="1.7" rx=".85"/><rect class="e shut" x="23" y="6.4" width="3.5" height="1.7" rx=".85"/>' +
    '</g></g></svg>';
  var VIEW = '<div class="v"><img class="vs" alt=""><img class="vb" alt=""></div>';
  $$("[data-isle]").forEach(function (el) {
    el.insertAdjacentHTML("afterbegin", '<div class="isle-bub"></div><div class="isle-shell"></div>' + VIEW + VIEW + EYES);
  });

  var top = new TI.Island($("#isle"));
  var hour = new Date().getHours();
  if (hour >= 23 || hour < 4) { top.eyes.base = "sleepy"; top.eyes.rest(); }
  top.eyes.start();

  var PANEL_NAMES = TI.PANELS.map(function (p) { return "p-" + p[0]; });
  var wide = window.matchMedia("(min-width: 720px)");
  var fine = window.matchMedia("(hover: hover) and (pointer: fine)");
  var lastScroll = 0;

  // Eyes follow the pointer
  var px = -1, py = -1;
  var watchers = [];
  function aim() {
    if (px < 0) return;
    watchers.forEach(function (w) { if (w.active()) w.eyes.look(px, py); });
  }
  on(window, "pointermove", function (e) {
    if (e.pointerType === "touch") return;
    px = e.clientX; py = e.clientY;
    aim();
  }, { passive: true });
  var touchT = 0;
  on(window, "pointerdown", function (e) {
    if (e.pointerType !== "touch") return;
    px = e.clientX; py = e.clientY;
    aim();
    clearTimeout(touchT);
    touchT = setTimeout(function () {
      px = -1;
      watchers.forEach(function (w) { w.eyes.target(0, 0); });
    }, 1200);
  }, { passive: true });

  // Top island moments, once per visit
  var ev = { busy: false, played: {}, timers: [] };
  function at(ms, fn) { ev.timers.push(setTimeout(fn, still.matches ? 0 : ms)); }
  function evClear() {
    ev.timers.forEach(clearTimeout);
    ev.timers = [];
    ev.busy = false;
    ev.looking = false;
    top.eyes.rest();
  }
  watchers.push({ eyes: top.eyes, active: function () { return top.state === "mini" && !ev.looking; } });

  var band = {};
  function play(name) {
    if (ev.played[name] || still.matches || document.hidden) return;
    if (name !== "wake" && !band[name]) return;
    if (ev.busy || sc.on || hov.open || top.state !== "mini" || (L.auto && L.vis) || Date.now() - lastScroll < 220) {
      if (name !== "wake") setTimeout(function () { play(name); }, 300);
      return;
    }
    ev.played[name] = true;
    ev.busy = true;
    var big = wide.matches;
    if (name === "wake") {
      top.eyes.mood("shut");
      ev.looking = true;
      at(420, function () { top.eyes.rest(); top.eyes.blink(); });
      at(950, function () { top.eyes.target(0, 0.9); });
      at(1550, function () { top.eyes.mood("wide"); top.eyes.target(0, 0); });
      if (big) {
        at(1850, function () { top.set("notif"); });
        at(5050, function () { top.set("mini"); });
        at(5600, function () { ev.looking = false; ev.busy = false; aim(); });
      } else {
        at(2700, function () { top.eyes.rest(); ev.looking = false; ev.busy = false; aim(); });
      }
    } else if (name === "pray" && big) {
      top.set("pray");
      at(2900, function () { top.set("mini"); });
      at(3400, function () { ev.busy = false; aim(); });
    } else {
      top.eyes.mood("happy");
      at(1700, function () { top.eyes.rest(); ev.busy = false; aim(); });
    }
  }

  // Panel scene: the page scroll switches tabs
  var sc = { el: $("#panel"), on: false, idx: -1, step: 0 };
  var tabs = $$(".tabs [role=tab]");
  var DESC = ["pg.home.d", "pg.music.d", "", "pg.class.d", "pg.msg.d", "pg.shelf.d", "pg.ctl.d", "pg.rem.d"];
  sc.el.classList.add("pinned");
  function sceneSize() {
    sc.step = Math.round(window.innerHeight * 0.42);
    sc.el.style.setProperty("--step", sc.step + "px");
  }
  function topFit() {
    var f = Math.min(1, (window.innerWidth - 24) / 584, window.innerHeight * 0.46 / 383);
    top.root.style.setProperty("--fit", f.toFixed(3));
  }
  function nowText() {
    var i = Math.max(0, Math.min(7, sc.idx));
    $("#now-name").textContent = tabs[i].textContent;
    $("#now-desc").textContent = DESC[i] ? t(DESC[i]) : "Claude Code, Codex, Gemini CLI, Antigravity";
  }
  hooks.push(nowText);
  function markTab(i) {
    tabs.forEach(function (b, k) {
      b.setAttribute("aria-selected", k === i ? "true" : "false");
      b.tabIndex = k === i ? 0 : -1;
    });
    nowText();
  }
  function sceneGo(i) {
    var prev = sc.idx;
    sc.idx = i;
    var inside = i >= 0 && i < 8;
    if (inside) {
      if (!sc.on) {
        sc.on = true;
        evClear();
        hovClose(true);
        document.body.classList.add("in-scene");
      fabSync();
        topFit();
        top.set(PANEL_NAMES[i]);
      } else {
        top.set(PANEL_NAMES[i], i > prev ? 1 : -1);
      }
      markTab(i);
    } else if (sc.on) {
      sc.on = false;
      document.body.classList.remove("in-scene");
      fabSync();
      top.root.style.setProperty("--fit", "1");
      top.set("mini").then(aim);
    }
  }
  function sceneTick() {
    var r = sc.el.getBoundingClientRect();
    var i = r.top > 0 ? -1 : Math.min(8, Math.floor(-r.top / sc.step));
    if (i !== sc.idx) sceneGo(i);
  }
  function tabScroll(i) {
    var y = sc.el.getBoundingClientRect().top + window.scrollY + i * sc.step + 2;
    window.scrollTo({ top: y, behavior: still.matches ? "auto" : "smooth" });
  }
  tabs.forEach(function (b, k) { on(b, "click", function () { tabScroll(k); }); });
  on($(".tabs"), "keydown", function (e) {
    var i = tabs.indexOf(document.activeElement);
    if (i < 0) return;
    var n = { ArrowRight: i + 1, ArrowLeft: i - 1, Home: 0, End: 7 }[e.key];
    if (n === undefined) return;
    e.preventDefault();
    n = (n + 8) % 8;
    tabs[n].focus();
    tabScroll(n);
  });
  sceneSize();

  // Hover the island to open panel
  var hov = { open: false, tab: 0, acc: 0, lock: 0, dwell: 0, leave: 0, hit: null };
  hov.hit = document.createElement("div");
  hov.hit.className = "isle-hit";
  top.root.appendChild(hov.hit);
  function hitSize(w, h) {
    hov.hit.style.width = w + "px";
    hov.hit.style.height = h + "px";
  }
  hitSize(120, 26);
  function hovOpen() {
    if (sc.on || ev.busy || top.state !== "mini") return;
    hov.open = true;
    topFit();
    top.set(PANEL_NAMES[hov.tab]);
    var st = TI.STATES[PANEL_NAMES[hov.tab]];
    hitSize(st.w, st.h);
  }
  function hovClose(silent) {
    clearTimeout(hov.dwell);
    clearTimeout(hov.leave);
    if (!hov.open) return;
    hov.open = false;
    hitSize(120, 26);
    if (!silent) { top.root.style.setProperty("--fit", "1"); top.set("mini").then(aim); }
  }
  on(hov.hit, "pointerenter", function (e) {
    if (e.pointerType !== "mouse" || !fine.matches) return;
    clearTimeout(hov.leave);
    if (!hov.open) hov.dwell = setTimeout(hovOpen, 450);
  });
  on(hov.hit, "pointerleave", function () {
    clearTimeout(hov.dwell);
    if (hov.open) hov.leave = setTimeout(function () { hovClose(false); }, 300);
  });
  on(hov.hit, "wheel", function (e) {
    if (!hov.open) return;
    var d = Math.abs(e.deltaX) > Math.abs(e.deltaY) ? e.deltaX : e.deltaY;
    var dir = d > 0 ? 1 : -1;
    var n = hov.tab + dir;
    if (n < 0 || n > 7) return;
    e.preventDefault();
    hov.acc += d;
    if (Math.abs(hov.acc) < 60 || Date.now() < hov.lock) return;
    hov.acc = 0;
    hov.lock = Date.now() + 220;
    hov.tab = n;
    var name = PANEL_NAMES[n];
    top.set(name, dir);
    hitSize(TI.STATES[name].w, TI.STATES[name].h);
  }, { passive: false });

  // Live stage: one island, seven activities
  var ACTS = ["lyric", "media", "notif", "done", "pray", "volume", "power"];
  var live = new TI.Island($("#isle-live"));
  var L = { i: -1, auto: false, vis: false, timer: 0, started: false };
  var actBtns = $$(".acts button");
  var pp = $("#pp");
  function dwell(i) { return i === 0 ? 4000 : 2800; }
  function ppSync() {
    pp.setAttribute("data-paused", L.auto ? "false" : "true");
    $("#pp-label").textContent = t(L.auto ? "acts.pause" : "acts.play");
  }
  hooks.push(ppSync);
  function showAct(i, run) {
    L.i = i;
    live.set(ACTS[i]);
    actBtns.forEach(function (b, k) {
      b.setAttribute("aria-pressed", k === i ? "true" : "false");
      b.classList.remove("run");
    });
    if (run) {
      var b = actBtns[i];
      b.style.setProperty("--dwell", dwell(i) + "ms");
      void b.offsetWidth;
      b.classList.add("run");
    }
  }
  function tick() {
    clearTimeout(L.timer);
    if (!L.auto || !L.vis) return;
    L.timer = setTimeout(function () {
      var n = L.i + 1;
      if (n >= ACTS.length) { stopAuto(); showAct(0, false); return; }
      showAct(n, true);
      tick();
    }, dwell(L.i));
  }
  function startAuto() {
    if (still.matches) return;
    L.auto = true;
    ppSync();
    showAct(L.i < 0 || L.i >= ACTS.length - 1 ? 0 : L.i + 1, true);
    tick();
  }
  function stopAuto() {
    L.auto = false;
    clearTimeout(L.timer);
    actBtns.forEach(function (b) { b.classList.remove("run"); });
    ppSync();
  }
  on(pp, "click", function () { if (L.auto) stopAuto(); else startAuto(); });
  pp.hidden = still.matches;
  actBtns.forEach(function (b, k) { on(b, "click", function () { stopAuto(); showAct(k, false); }); });

  // Mini, up close
  var mini = new TI.Island($("#isle-mini"));
  var M = { vis: false, hold: false, t: 0 };
  var moodBtns = $$(".moods button");
  watchers.push({ eyes: mini.eyes, active: function () { return M.vis && !M.hold; } });
  function moodPress(btn) { moodBtns.forEach(function (b) { b.classList.toggle("on", b === btn); }); }
  function moodSet(m, btn) {
    clearTimeout(M.t);
    M.hold = m !== "follow" || still.matches;
    if (m === "follow") {
      mini.eyes.rest();
      if (still.matches) mini.eyes.target(1.3, 0.3, true); else aim();
    } else if (m === "rest") {
      mini.eyes.target(0, 0);
      if (still.matches) mini.eyes.mood("shut"); else { mini.eyes.rest(); mini.eyes.blink(); }
    } else { mini.eyes.mood(m); mini.eyes.target(0, 0); }
    moodPress(btn);
  }
  function moodRelease(ms) {
    clearTimeout(M.t);
    M.t = setTimeout(function () { M.hold = false; mini.eyes.rest(); mini.eyes.target(0, 0); moodPress(null); aim(); }, ms);
  }
  moodBtns.forEach(function (b) {
    var m = b.getAttribute("data-mood");
    on(b, "click", function () { moodSet(m, b); moodRelease(2200); });
    on(b, "focus", function () { moodSet(m, b); });
    on(b, "blur", function () { moodRelease(400); });
    on(b, "pointerenter", function (e) { if (e.pointerType === "mouse") moodSet(m, b); });
    on(b, "pointerleave", function (e) { if (e.pointerType === "mouse") moodRelease(500); });
  });

  // Permission card that really answers
  var ap = new TI.Island($("#isle-ap"));
  var apStage = $("#stage-ap");
  var A = { locked: true, t: 0, key: "", asked: false };
  var ansBtns = $$("[data-ans]");
  var again = $("#ap-again");
  function apLock(v) {
    A.locked = v;
    ansBtns.forEach(function (b) { b.setAttribute("aria-disabled", v ? "true" : "false"); });
  }
  function apStatus(key) {
    A.key = key;
    $("#ap-status").textContent = key ? t(key) : "";
  }
  hooks.push(function () { apStatus(A.key); });
  function apAsk() {
    A.asked = true;
    apLock(true);
    apStatus("");
    apStage.classList.remove("answered");
    var back = document.activeElement === again;
    again.hidden = true;
    if (back) firstAnswer().focus();
    ap.set("approve");
    clearTimeout(A.t);
    A.t = setTimeout(function () { apLock(false); }, still.matches ? 0 : 800);
  }
  // First answer button that is visible
  function firstAnswer() {
    return ansBtns.filter(function (b) { return b.offsetParent !== null; })[0] || ansBtns[0];
  }
  function answer(kind) {
    if (A.locked || ap.state !== "approve") return;
    var had = ansBtns.indexOf(document.activeElement) >= 0;
    apLock(true);
    apStage.classList.add("answered");
    if (kind === "deny") { ap.set("mini"); apStatus("ap.no"); }
    else { ap.set("done"); apStatus(kind === "always" ? "ap.okall" : "ap.ok"); }
    again.hidden = false;
    if (had) again.focus();
  }
  ansBtns.forEach(function (b) {
    on(b, "click", function () { answer(b.getAttribute("data-ans")); });
    on(b, "focus", function () { if (!A.asked) apAsk(); });
  });
  on(again, "click", apAsk);

  // Fit inline islands to their stage
  var lastW = {};
  function fitStages() {
    var key = $("#stage-live").clientWidth + "/" + apStage.clientWidth + "/" + $("#stage-mini").clientWidth;
    if (key === lastW.k) return;
    lastW.k = key;
    var lw = $("#stage-live").clientWidth;
    live.root.style.setProperty("--fit", Math.min(1, (lw - 24) / 437).toFixed(3));
    $("#stage-live").style.setProperty("--fit", Math.min(1, (lw - 24) / 437).toFixed(3));
    var aw = apStage.clientWidth;
    var af = Math.min(1, (aw - 24) / 548).toFixed(3);
    ap.root.style.setProperty("--fit", af);
    apStage.style.setProperty("--fit", af);
    var mw = $("#stage-mini").clientWidth;
    $("#stage-mini").style.setProperty("--mag", mw < 300 ? 3 : 4);
  }
  fitStages();
  if (window.ResizeObserver) {
    var ro = new ResizeObserver(function () { requestAnimationFrame(fitStages); });
    [$("#stage-live"), apStage, $("#stage-mini")].forEach(function (el) { ro.observe(el); });
  }

  // Material and theme switch
  var seg = $("#seg");
  var radios = $$("[role=radio]", seg);
  var matStage = $("#stage-mat");
  var MAT = { liquid: "style.liquid.d", frost: "style.frost.d", light: "style.light.d" };
  var mat = "liquid";
  function pick(m, focus) {
    mat = m;
    matStage.setAttribute("data-mat", m);
    $$(".mats img", matStage).forEach(function (img) { img.classList.toggle("on", img.getAttribute("data-m") === m); });
    radios.forEach(function (r) {
      var sel = r.getAttribute("data-mat") === m;
      r.setAttribute("aria-checked", sel ? "true" : "false");
      r.tabIndex = sel ? 0 : -1;
      if (sel && focus) r.focus();
    });
    $("#mat-cap").textContent = t(MAT[m]);
  }
  hooks.push(function () { $("#mat-cap").textContent = t(MAT[mat]); });
  radios.forEach(function (r) { on(r, "click", function () { pick(r.getAttribute("data-mat"), false); }); });
  on(seg, "keydown", function (e) {
    var i = radios.indexOf(document.activeElement);
    if (i < 0) return;
    var d = { ArrowRight: 1, ArrowDown: 1, ArrowLeft: -1, ArrowUp: -1 }[e.key];
    if (!d) return;
    e.preventDefault();
    pick(radios[(i + d + radios.length) % radios.length].getAttribute("data-mat"), true);
  });

  // Visibility drives every stage
  if ("IntersectionObserver" in window) {
    new IntersectionObserver(function (es) {
      es.forEach(function (e) {
        L.vis = e.intersectionRatio >= 0.55;
        if (L.vis && !L.started) {
          L.started = true;
          if (still.matches) showAct(0, false); else startAuto();
        } else if (L.vis && L.auto) {
          showAct(L.i, true);
          tick();
        } else if (!L.vis) {
          clearTimeout(L.timer);
          actBtns.forEach(function (b) { b.classList.remove("run"); });
        }
      });
    }, { threshold: [0, 0.55] }).observe($("#stage-live"));

    new IntersectionObserver(function (es) {
      es.forEach(function (e) {
        M.vis = e.isIntersecting;
        if (M.vis) { mini.eyes.start(); aim(); } else { mini.eyes.stop(); }
      });
    }).observe($("#stage-mini"));

    new IntersectionObserver(function (es) {
      es.forEach(function (e) { if (e.intersectionRatio >= 0.5 && !A.asked) apAsk(); });
    }, { threshold: [0, 0.5] }).observe(apStage);

    var evIO = new IntersectionObserver(function (es) {
      es.forEach(function (e) {
        var n = e.target.getAttribute("data-ev");
        band[n] = e.isIntersecting;
        if (e.isIntersecting) play(n);
      });
    }, { rootMargin: "-45% 0px -45% 0px" });
    $$("[data-ev]").forEach(function (el) { evIO.observe(el); });
  } else {
    showAct(0, false);
    apAsk();
  }

  // Scroll: nav backdrop, scene, eyes
  var nav = $(".nav");
  var ticking = false;
  function onScroll() {
    ticking = false;
    nav.classList.toggle("scrolled", window.scrollY > 8);
    sceneTick();
    if (M.vis) aim();
  }
  on(window, "scroll", function () {
    lastScroll = Date.now();
    if (hov.open) hovClose(false);
    if (!ticking) { ticking = true; requestAnimationFrame(onScroll); }
  }, { passive: true });
  var lastVW = window.innerWidth;
  var coarse = window.matchMedia("(pointer: coarse)");
  on(window, "resize", function () {
    if (!(coarse.matches && window.innerWidth === lastVW)) sceneSize();
    lastVW = window.innerWidth;
    if (sc.on) topFit();
    onScroll();
  });
  onScroll();

  on(document, "visibilitychange", function () {
    var islands = [top, mini];
    if (document.hidden) {
      islands.forEach(function (i) { i.eyes.stop(); });
      clearTimeout(L.timer);
    } else {
      top.eyes.start();
      if (M.vis) mini.eyes.start();
      tick();
    }
  });

  // Wake on load, only from the top
  if (window.scrollY < 40 && !location.hash) {
    var woke = function () { play("wake"); };
    setTimeout(woke, 700);
    var cancel = function () { if (!ev.played.wake) ev.played.wake = true; };
    on(window, "keydown", cancel, { once: true });
  }

  // Warm the island images when idle
  var idle = window.requestIdleCallback || function (fn) { return setTimeout(fn, 1200); };
  idle(function () {
    ["notif", "lyric", "media", "done", "pray", "volume", "power", "approve"].concat(PANEL_NAMES).forEach(function (n, k) {
      setTimeout(function () { TI.load(n); }, k * 120);
    });
  });

  apply(lang);

  // Coffee QRIS, with a happy Tepi
  var qris = $("#qris");
  var cup = new TI.Island($("#isle-q"));
  cup.eyes.base = "happy";
  cup.eyes.rest();
  $$(".js-coffee").forEach(function (b) {
    on(b, "click", function () {
      var img = $("#qris-img");
      if (!img.getAttribute("src")) img.src = "assets/img/qris-revaldo.jpeg";
      if (qris.showModal) qris.showModal(); else qris.setAttribute("open", "");
      fabSync();
    });
  });
  // Floating button steps aside when redundant
  var fab = $("#fab");
  var F = { near: {}, ready: false };
  var narrow = window.matchMedia("(max-width: 599px)");
  setTimeout(function () { F.ready = true; fabSync(); }, still.matches ? 0 : 900);
  if ("IntersectionObserver" in window) {
    var fio = new IntersectionObserver(function (es) {
      es.forEach(function (e) { F.near[e.target.classList.contains("foot") ? "foot" : "coffee"] = e.isIntersecting; });
      fabSync();
    });
    fio.observe($(".coffee"));
    fio.observe($(".foot"));
  }
  on(qris, "close", fabSync);
  on(qris, "click", function (e) {
    var r = qris.getBoundingClientRect();
    if (e.clientX < r.left || e.clientX > r.right || e.clientY < r.top || e.clientY > r.bottom) qris.close();
  });

  // Hide or show the floating button
  function fabSync() {
    if (!fab) return;
    var away = !F.ready || qris.open || F.near.coffee || F.near.foot || (sc.on && narrow.matches);
    fab.classList.toggle("away", !!away);
  }

  // Copy SHA-256
  var copy = $("#copy");
  on(copy, "click", function () {
    var text = $(".js-sha").textContent.trim();
    var done = function () {
      copy.textContent = t("dl.copied");
      setTimeout(function () { copy.textContent = t("dl.copy"); }, 1600);
    };
    if (navigator.clipboard && window.isSecureContext) {
      navigator.clipboard.writeText(text).then(done, function () { select(text); done(); });
    } else { select(text); done(); }
  });
  function select(text) {
    var ta = document.createElement("textarea");
    ta.value = text; ta.setAttribute("readonly", ""); ta.style.position = "fixed"; ta.style.opacity = "0";
    document.body.appendChild(ta); ta.select();
    try { document.execCommand("copy"); } catch (e) { }
    document.body.removeChild(ta);
  }

  // Non-Windows visitors get a note
  var plat = (navigator.userAgentData && navigator.userAgentData.platform) || navigator.platform || navigator.userAgent;
  var phone = /Android|iPhone|iPad|iPod/i.test(navigator.userAgent);
  if (phone || !/win/i.test(plat)) {
    $$(".platform").forEach(function (p) { p.hidden = false; });
  }

  // Portrait poster for the video
  var film = $(".film");
  if (window.matchMedia("(orientation: portrait) and (max-width: 860px)").matches) film.poster = "assets/img/poster-v.jpg";

  // Latest release details, static fallback
  if (window.fetch) {
    fetch("https://api.github.com/repos/odlaver/tepi/releases/latest", { headers: { Accept: "application/vnd.github+json" } })
      .then(function (r) { if (!r.ok) throw new Error(r.status); return r.json(); })
      .then(function (rel) {
        var ver = String(rel.tag_name || "").replace(/^v/, "");
        if (/^\d+\.\d+/.test(ver)) $$(".js-version").forEach(function (el) { el.textContent = ver; });
        var exe = (rel.assets || []).filter(function (a) { return a.name === "Tepi.exe"; })[0];
        if (exe && exe.size) {
          var kb = Math.round(exe.size / 1024);
          $$(".js-size").forEach(function (el) { el.textContent = kb + " KB"; });
        }
        var sha = exe && exe.digest && /^sha256:([0-9a-f]{64})$/i.exec(exe.digest);
        var hex = sha ? sha[1] : (/SHA-256[\s\S]{0,40}?([0-9A-Fa-f]{64})/.exec(rel.body || "") || [])[1];
        if (hex) $(".js-sha").textContent = hex.toUpperCase();
      })
      .catch(function () { });
  }
})();
