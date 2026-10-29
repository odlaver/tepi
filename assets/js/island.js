(function () {
  "use strict";

  var still = window.matchMedia("(prefers-reduced-motion: reduce)");
  var STRIP = 48;

  // Glass body colours, sampled from the renders
  var TONES = {
    mini: ["#48484e", "#343439"],
    pill: ["#494a50", "#36363b"],
    panel: ["#3d3e43", "#141416"],
    light: ["#dcdcdd", "#cbcbcc"]
  };

  // Body boxes measured from 2x renders
  var STATES = {
    mini: { w: 40, h: 14, r: 5.5, s: 1, tone: "mini" },
    media: { img: "media", iw: 290, ox: 6.5, oy: 0, w: 234, h: 38, r: 18, s: 1.5, tone: "pill", bub: { x: 249, y: 2, w: 34, h: 34 } },
    lyric: { img: "lyric", iw: 397, ox: 7, oy: 0.5, w: 345.5, h: 37.5, r: 15.5, s: 1.5, tone: "pill", bub: { x: 355.5, y: 1.5, w: 34, h: 34 } },
    notif: { img: "notif", iw: 427, ox: 11.5, oy: 0, w: 404, h: 66, r: 27, s: 1, tone: "pill" },
    done: { img: "done", iw: 437, ox: 11.5, oy: 0.5, w: 414, h: 69.5, r: 27, s: 1, tone: "pill" },
    pray: { img: "pray", iw: 253, ox: 6.5, oy: 0.5, w: 240, h: 43.5, r: 22.5, s: 1, tone: "pill" },
    volume: { img: "volume", iw: 313, ox: 6.5, oy: 0.5, w: 300, h: 43.5, r: 22.5, s: 1, tone: "pill" },
    power: { img: "power", iw: 357, ox: 7, oy: 0.5, w: 343.5, h: 47.5, r: 20.5, s: 1, tone: "pill" },
    approve: { img: "approve", iw: 547.5, ox: 13.5, oy: 0, w: 520, h: 237.5, r: 32, s: 0, tone: "light" }
  };
  var PANELS = [["ringkasan", 352], ["musik", 340], ["agen", 254], ["kuliah", 266], ["pesan", 268], ["rak", 288], ["kontrol", 366], ["pengingat", 262]];
  PANELS.forEach(function (p) {
    STATES["p-" + p[0]] = { img: "p-" + p[0], iw: 583.5, ox: 11.5, oy: 0, w: 560, h: p[1], r: 32, s: 0, tone: "panel", split: true };
  });

  function src(name) { return "assets/img/" + name + ".webp"; }
  function wait(ms) { return new Promise(function (r) { setTimeout(r, still.matches ? 0 : ms); }); }
  function px(n) { return n + "px"; }

  // Load once, decode before showing
  var cache = {};
  function load(name) {
    if (!cache[name]) {
      cache[name] = new Promise(function (resolve) {
        var img = new Image();
        img.decoding = "async";
        img.onload = function () { (img.decode ? img.decode() : Promise.resolve()).then(resolve, resolve); };
        img.onerror = resolve;
        img.src = src(name);
      });
    }
    return cache[name];
  }

  // Eyes on the Mini sliver
  function Eyes(svg) {
    this.svg = svg;
    this.gaze = svg.querySelector(".gaze");
    this.lids = svg.querySelector(".lids");
    this.x = 0; this.y = 0; this.tx = 0; this.ty = 0;
    this.raf = 0; this.timer = 0; this.on = false;
    this.base = "rest";
    this.mood(this.base);
  }
  Eyes.prototype.mood = function (m) {
    this.current = m;
    this.svg.setAttribute("data-mood", m);
  };
  Eyes.prototype.rest = function () { this.mood(this.base); };
  Eyes.prototype.target = function (dx, dy, pose) {
    if (still.matches) {
      if (!pose) { dx = 0; dy = 0; }
      this.x = this.tx = dx; this.y = this.ty = dy;
      this.gaze.setAttribute("transform", "translate(" + dx + " " + dy + ")");
      return;
    }
    this.tx = dx; this.ty = dy;
    if (!this.raf) this.raf = requestAnimationFrame(this.step.bind(this));
  };
  // Aim at a viewport point
  Eyes.prototype.look = function (cx, cy) {
    var r = this.svg.getBoundingClientRect();
    if (!r.width) return;
    var dx = cx - (r.left + r.width / 2), dy = cy - (r.top + r.height / 2);
    var d = Math.sqrt(dx * dx + dy * dy);
    if (d < 40) { this.target(0, 0); return; }
    var k = Math.min(1, d / 260);
    this.target(dx / d * k * 1.4, dy > 0 ? dy / d * k * 0.9 : dy / d * k * 0.5);
  };
  Eyes.prototype.step = function () {
    this.x += (this.tx - this.x) * 0.18;
    this.y += (this.ty - this.y) * 0.18;
    if (Math.abs(this.tx - this.x) < 0.02 && Math.abs(this.ty - this.y) < 0.02) {
      this.x = this.tx; this.y = this.ty; this.raf = 0;
    } else {
      this.raf = requestAnimationFrame(this.step.bind(this));
    }
    this.gaze.setAttribute("transform", "translate(" + this.x.toFixed(2) + " " + this.y.toFixed(2) + ")");
  };
  Eyes.prototype.blink = function () {
    var self = this;
    if (still.matches || !/rest|wide/.test(this.current)) return;
    this.lids.classList.remove("blink");
    void this.svg.getBoundingClientRect();
    this.lids.classList.add("blink");
    setTimeout(function () { self.lids.classList.remove("blink"); }, 260);
  };
  Eyes.prototype.start = function () {
    var self = this;
    if (this.on) return;
    this.on = true;
    (function next() {
      self.timer = setTimeout(function () { self.blink(); next(); }, 3000 + Math.random() * 2500);
    })();
  };
  Eyes.prototype.stop = function () {
    this.on = false;
    clearTimeout(this.timer);
  };

  // Island: shell, bubble, two views
  function Island(root) {
    this.root = root;
    this.views = root.querySelectorAll(".v");
    this.bub = root.querySelector(".isle-bub");
    this.front = 0;
    this.state = "";
    this.shown = "mini";
    this.token = 0;
    var svg = root.querySelector(".isle-eyes");
    this.eyes = svg ? new Eyes(svg) : null;
    this.geom(STATES.mini);
    this.state = "mini";
    root.classList.add("is-mini");
  }

  Island.prototype.geom = function (st) {
    var s = this.root.style, t = TONES[st.tone];
    s.setProperty("--w", px(st.w));
    s.setProperty("--h", px(st.h));
    s.setProperty("--r", px(st.r));
    s.setProperty("--s", px(st.s));
    s.setProperty("--it", t[0]);
    s.setProperty("--ib", t[1]);
  };

  // Position a view over the body box
  Island.prototype.place = function (v, st) {
    v.style.left = px(-(st.w / 2 + st.ox));
    v.style.top = px(-st.oy);
    v.style.width = px(st.iw);
    var imgs = v.querySelectorAll("img"), s = src(st.img);
    for (var i = 0; i < imgs.length; i++) if (imgs[i].getAttribute("src") !== s) imgs[i].src = s;
  };

  Island.prototype.bubble = function (st) {
    var b = this.bub;
    if (!b) return;
    if (!st.bub) { b.classList.remove("on"); return; }
    b.style.left = px(-st.w / 2 + st.bub.x);
    b.style.top = px(st.bub.y);
    b.style.width = px(st.bub.w);
    b.style.height = px(st.bub.h);
    b.style.setProperty("--from", px(-(st.bub.x + st.bub.w - st.w)));
    b.classList.remove("on");
    void b.offsetWidth;
    b.classList.add("on");
  };

  // Morph; dir switches panel tabs
  Island.prototype.set = function (name, dir) {
    var st = STATES[name], prev = STATES[this.state];
    if (!st || name === this.state) return Promise.resolve();
    var self = this, t = ++this.token, root = this.root;
    var shrink = st.w * st.h < prev.w * prev.h;
    root.classList.toggle("settle", shrink);
    this.geom(st);
    root.classList.toggle("is-mini", name === "mini");
    root.classList.toggle("is-light", st.tone === "light");
    root.setAttribute("data-state", name);

    var old = this.views[this.front], nxt = this.views[1 - this.front];
    var shown = STATES[this.shown] || STATES.mini;
    var tab = !!(dir && shown.split && st.split && old.getAttribute("data-shows") === this.shown);
    old.style.setProperty("--dir", dir || 1);
    nxt.style.setProperty("--dir", dir || 1);
    old.className = "v" + (shown.split ? " tabbed" : "") + (tab ? " tab-out" : " out");
    this.bubble(st);
    this.state = name;
    if (this.eyes) {
      if (name === "mini") { this.eyes.rest(); this.eyes.start(); } else { this.eyes.stop(); }
    }
    if (!st.img) { this.shown = "mini"; return wait(450); }

    this.place(nxt, st);
    nxt.setAttribute("data-shows", name);
    nxt.className = "v" + (st.split ? " tabbed" : "") + (tab ? " tab-pre" : " pre");
    return Promise.all([load(st.img), wait(tab ? 40 : 160)]).then(function () {
      if (t !== self.token) return;
      void nxt.offsetWidth;
      nxt.className = "v" + (st.split ? " tabbed" : "") + (tab ? " tab-in" : " in");
      self.front = 1 - self.front;
      self.shown = name;
      return wait(tab ? 300 : 260).then(function () {
        // Reveal the render's own edges
        if (tab && t === self.token) nxt.className = "v tabbed in";
      });
    });
  };

  Island.prototype.mood = function (m) {
    if (this.eyes) this.eyes.mood(m);
  };

  window.TepiIsland = { Island: Island, STATES: STATES, PANELS: PANELS, load: load, still: still, STRIP: STRIP };
})();
