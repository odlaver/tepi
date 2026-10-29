(function () {
  "use strict";

  function $(s, r) { return (r || document).querySelector(s); }
  function $$(s, r) { return Array.prototype.slice.call((r || document).querySelectorAll(s)); }
  function on(el, ev, fn, o) { if (el) el.addEventListener(ev, fn, o); }

  var EN = {
    "pk.skip": "Skip to download",
    "nav.download": "Download",
    "pk.eyebrow": "Theme packs",
    "pk.title": "Tepi, in your style.",
    "pk.lead": "Colors, emblem, mascot, progress knob, greetings, and sounds in one zip. Everything runs in your browser; nothing is uploaded.",
    "pk.ai.t": "Start with AI",
    "pk.ai.d": "Fill in a theme, copy the prompt to your favorite AI, then paste its answer below. Colors and greetings fill themselves in.",
    "pk.ai.theme": "Theme or character",
    "pk.ai.theme.ph": "Night ocean",
    "pk.ai.mood": "Mood and colors",
    "pk.ai.mood.ph": "Calm, navy, silver",
    "pk.ai.prompt": "Prompt",
    "pk.ai.copy": "Copy prompt",
    "pk.ai.claude": "Open in Claude",
    "pk.ai.gpt": "Open in ChatGPT",
    "pk.ai.paste": "AI answer",
    "pk.ai.paste.ph": "Paste the whole AI answer here",
    "pk.ai.apply": "Apply answer",
    "pk.ai.files": "Import theme.ini or greetings.txt",
    "pk.name.t": "Pack name",
    "pk.name.l": "Folder name",
    "pk.name.d": "Lowercase letters, digits, and hyphens. This name shows up in Settings > Appearance > Theme pack.",
    "pk.col.t": "Colors",
    "pk.col.presets": "Sample palettes",
    "pk.preset.mono": "Mono",
    "pk.preset.forest": "Forest",
    "pk.preset.ember": "Ember",
    "pk.col.bg": "Background",
    "pk.col.text": "Text",
    "pk.col.accent": "Accent",
    "pk.col.all": "All colors",
    "pk.col.all.d": "Derived from the three colors above. Edit one to override it; use #RRGGBB or #AARRGGBB.",
    "pk.col.reset": "Restore derived colors",
    "pk.img.t": "Images",
    "pk.emb.t": "Emblem",
    "pk.emb.d": "Square, at least 96 px. Cropped to a circle and shown at 24 px next to the clock and greetings. More than one: picked at random.",
    "pk.add": "Add",
    "pk.pick": "Choose",
    "pk.mas.t": "Mascot",
    "pk.mas.d": "PNG with a transparent background, longest side about 160 px. Sits in the bottom-right corner of the panel. Animation strip: frames side by side, enter the frame count.",
    "pk.knob.t": "Progress knob",
    "pk.knob.d": "A wide image with a transparent background for the music and teleprompter progress bars.",
    "pk.knob.h": "Height (px)",
    "pk.knob.a": "Row on the bar (0 to 1)",
    "pk.knob.x": "Column at the song position (0 to 1)",
    "pk.img.legal": "Images of characters or other people's work are best kept for personal use.",
    "pk.frames": "Frames",
    "pk.remove": "Remove",
    "pk.gr.t": "Greetings",
    "pk.gr.d": "One greeting per line, picked at random when Tepi opens. Lines under [morning], [afternoon], [evening], or [night] are used only at that time.",
    "pk.vo.t": "Greeting sounds",
    "pk.vo.d": "Played once when a greeting appears. Files other than WAV are converted to WAV. Silent in full screen and do not disturb.",
    "pk.vo.any": "Any time",
    "pk.vo.pagi": "Morning",
    "pk.vo.siang": "Afternoon",
    "pk.vo.sore": "Evening",
    "pk.vo.malam": "Night",
    "pk.vo.none": "No sound",
    "pk.vo.clear": "Clear",
    "pk.fx.t": "Animation and effects",
    "pk.fx.fps": "Frames per second",
    "pk.fx.sec": "Play length (seconds)",
    "pk.fx.idle": "Emblem moves again every (seconds, 0 off)",
    "pk.fx.water": "Bubbles and water ripples",
    "pk.fx.eyes": "Eyes on the Mini sliver",
    "pk.ex.t": "Download the pack",
    "pk.ex.btn": "Download zip",
    "pk.ex.s1": "Extract the zip into",
    "pk.ex.s2": "Pick the pack in Settings > Appearance > Theme pack.",
    "pk.copy": "Copy",
    "pk.copied": "Copied",
    "pk.pv.label": "Preview",
    "pk.pv.music": "Music",
    "pk.pv.song": "Song title",
    "pk.pv.artist": "Artist name",
    "pk.pv.row": "Drink water",
    "pk.pv.ask": "Agent asks permission",
    "pk.pv.askd": "Edit Island.cs, 2 lines to 3",
    "pk.pv.deny": "Deny",
    "pk.pv.allow": "Allow",
    "pk.pv.cap": "A rough preview of your pack's colors and images.",
    "pk.pv.next": "Another greeting",
    "pk.back": "Back to Tepi",
    "pk.docs": "Theme pack docs",
    "foot.project": "Tepi project",
    "foot.src": "Source code",
    "s.applied": "Applied: {n}.",
    "s.colors": "colors",
    "s.greet": "greetings",
    "s.opts": "options",
    "s.none": "Nothing recognized. Make sure the answer has a theme.ini block or greetings with [morning].",
    "s.badimg": "Not an image: {f}",
    "s.big": "Over Tepi's limit (8 MB or 8 million pixels), it will be skipped",
    "s.small": "Small, may look blurry",
    "s.badaudio": "Could not read sound: {f}",
    "s.building": "Building zip...",
    "s.done": "Downloaded {f}.",
    "sum.emb": "{n} emblem",
    "sum.mas": "{n} mascot",
    "sum.knob": "progress knob",
    "sum.greet": "{n} greetings",
    "sum.voice": "{n} sounds",
    "c.text": "Text",
    "c.sub": "secondary",
    "c.card": "accent on cards",
    "c.low": "too low",
    "doc.title": "Tepi theme packs",
    "doc.desc": "Build a Tepi theme pack: colors, emblem, mascot, progress knob, greetings, and sounds in one zip. Includes an AI prompt. Everything runs in your browser."
  };
  // Indonesian strings not in the markup
  var ID = {
    "pk.frames": "Frame",
    "pk.remove": "Hapus",
    "pk.vo.any": "Kapan saja",
    "pk.vo.pagi": "Pagi",
    "pk.vo.siang": "Siang",
    "pk.vo.sore": "Sore",
    "pk.vo.malam": "Malam",
    "pk.vo.none": "Tanpa suara",
    "pk.vo.clear": "Kosongkan",
    "pk.copied": "Tersalin",
    "s.applied": "Terpakai: {n}.",
    "s.colors": "warna",
    "s.greet": "sapaan",
    "s.opts": "opsi",
    "s.none": "Tidak ada yang dikenali. Pastikan jawaban berisi blok theme.ini atau sapaan dengan [pagi].",
    "s.badimg": "Bukan gambar: {f}",
    "s.big": "Melebihi batas Tepi (8 MB atau 8 juta piksel), akan dilewati",
    "s.small": "Kecil, bisa terlihat buram",
    "s.badaudio": "Suara tidak terbaca: {f}",
    "s.building": "Menyusun zip...",
    "s.done": "{f} terunduh.",
    "sum.emb": "{n} lambang",
    "sum.mas": "{n} maskot",
    "sum.knob": "pegangan bar",
    "sum.greet": "{n} sapaan",
    "sum.voice": "{n} suara",
    "c.text": "Teks",
    "c.sub": "sekunder",
    "c.card": "aksen di kartu",
    "c.low": "terlalu rendah"
  };

  var html = document.documentElement;
  var lang = "id";
  var hooks = [];

  function saved() { try { return localStorage.getItem("tepi.lang"); } catch (e) { return null; } }
  function capture() {
    $$("[data-i18n]").forEach(function (el) { ID[el.dataset.i18n] = el.textContent; });
    $$("[data-i18n-label]").forEach(function (el) { ID[el.dataset.i18nLabel] = el.getAttribute("aria-label"); });
    $$("[data-i18n-ph]").forEach(function (el) { ID[el.dataset.i18nPh] = el.getAttribute("placeholder"); });
    ID["doc.title"] = document.title;
    ID["doc.desc"] = $('meta[name="description"]').content;
  }
  function t(key, vars) {
    var d = lang === "en" ? EN : ID;
    var s = d[key] !== undefined ? d[key] : ID[key];
    if (vars) Object.keys(vars).forEach(function (k) { s = s.replace("{" + k + "}", vars[k]); });
    return s;
  }
  function apply(next) {
    lang = next;
    html.lang = next;
    $$("[data-i18n]").forEach(function (el) { el.textContent = t(el.dataset.i18n); });
    $$("[data-i18n-label]").forEach(function (el) { el.setAttribute("aria-label", t(el.dataset.i18nLabel)); });
    $$("[data-i18n-ph]").forEach(function (el) { el.setAttribute("placeholder", t(el.dataset.i18nPh)); });
    document.title = t("doc.title");
    $('meta[name="description"]').content = t("doc.desc");
    var b = $("#lang");
    b.textContent = next === "en" ? "ID" : "EN";
    b.setAttribute("aria-label", next === "en" ? "ID, ganti ke bahasa Indonesia" : "EN, switch to English");
    hooks.forEach(function (h) { h(); });
    try { localStorage.setItem("tepi.lang", next); } catch (e) { }
  }
  capture();
  var langs = navigator.languages && navigator.languages.length ? navigator.languages : [navigator.language || "id"];
  lang = (saved() || (langs.some(function (l) { return /^(id|in)\b/i.test(l); }) ? "id" : "en")) === "en" ? "en" : "id";
  on($("#lang"), "click", function () { apply(lang === "en" ? "id" : "en"); });

  // Color math
  function rgb(h) {
    h = h.replace("#", "");
    if (h.length === 8) h = h.slice(2);
    return [parseInt(h.slice(0, 2), 16), parseInt(h.slice(2, 4), 16), parseInt(h.slice(4, 6), 16)];
  }
  function hex(c) { return "#" + c.map(function (v) { return ("0" + Math.round(Math.max(0, Math.min(255, v))).toString(16)).slice(-2); }).join("").toUpperCase(); }
  function mix(a, b, k) { var x = rgb(a), y = rgb(b); return hex([0, 1, 2].map(function (i) { return x[i] + (y[i] - x[i]) * k; })); }
  function alpha(c, a) { return "#" + ("0" + a.toString(16)).slice(-2).toUpperCase() + c.slice(1).toUpperCase(); }
  function lum(c) {
    return rgb(c).map(function (v) { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); })
      .reduce(function (s, v, i) { return s + v * [0.2126, 0.7152, 0.0722][i]; }, 0);
  }
  function contrast(a, b) { var x = lum(a), y = lum(b); return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05); }
  function css(c) {
    if (c.length === 9) { var a = parseInt(c.slice(1, 3), 16) / 255, v = rgb(c); return "rgba(" + v.join(",") + "," + a.toFixed(3) + ")"; }
    return c;
  }
  var HEX = /^#([0-9a-f]{6}|[0-9a-f]{8})$/i;

  var KEYS = ["bg", "text", "sub", "faint", "fill", "fillHover", "line", "accent", "onAccent", "progress",
    "card.bg", "card.text", "card.sub", "card.faint", "card.fill", "card.fillHover", "card.line", "card.accent", "card.onAccent"];

  // Full palette from three colors
  function derive(bg, text, accent) {
    var d = { bg: bg, text: text, accent: accent, progress: accent };
    d.sub = mix(text, bg, 0.24);
    d.faint = mix(text, bg, 0.44);
    var tint = mix(accent, "#FFFFFF", 0.2);
    d.fill = alpha(tint, 0x2E);
    d.fillHover = alpha(tint, 0x4A);
    d.line = alpha(tint, 0x3A);
    d.onAccent = contrast(bg, accent) >= contrast("#FFFFFF", accent) ? bg : "#FFFFFF";
    var cbg = text, ca = accent;
    for (var i = 0; i < 14 && contrast(ca, cbg) < 4.5; i++) ca = mix(ca, bg, 0.22);
    d["card.bg"] = cbg;
    d["card.text"] = bg;
    d["card.sub"] = mix(bg, cbg, 0.3);
    d["card.faint"] = mix(bg, cbg, 0.48);
    d["card.accent"] = ca;
    d["card.fill"] = alpha(ca, 0x1E);
    d["card.fillHover"] = alpha(ca, 0x35);
    d["card.line"] = alpha(ca, 0x30);
    d["card.onAccent"] = contrast("#FFFFFF", ca) >= 4.5 ? "#FFFFFF" : bg;
    return d;
  }

  var S = { own: {}, emblems: [], corners: [], knob: null, voices: { "": [], pagi: [], siang: [], sore: [], malam: [] }, greetDirty: false };
  var cBg = $("#c-bg"), cText = $("#c-text"), cAccent = $("#c-accent");

  function palette() {
    var d = derive(cBg.value.toUpperCase(), cText.value.toUpperCase(), cAccent.value.toUpperCase());
    Object.keys(S.own).forEach(function (k) { d[k] = S.own[k]; });
    return d;
  }

  // Key grid in "All colors"
  var keysBox = $("#keys");
  KEYS.forEach(function (k) {
    var l = document.createElement("label");
    l.innerHTML = "<i></i><span></span><input type=\"text\" spellcheck=\"false\" maxlength=\"9\">";
    l.querySelector("span").textContent = k;
    var inp = l.querySelector("input");
    inp.setAttribute("data-key", k);
    inp.setAttribute("aria-label", k);
    on(inp, "input", function () {
      var v = inp.value.trim();
      if (HEX.test(v)) { S.own[k] = v.toUpperCase(); render(true); }
    });
    on(inp, "blur", function () { render(); });
    keysBox.appendChild(l);
  });
  on($("#keys-reset"), "click", function () { S.own = {}; render(); });

  var pv = $("#pv");
  function render(typing) {
    var d = palette();
    $$("input[data-key]", keysBox).forEach(function (inp) {
      var k = inp.getAttribute("data-key");
      inp.parentNode.style.setProperty("--c", css(d[k]));
      inp.classList.toggle("own", !!S.own[k]);
      if (!(typing && document.activeElement === inp)) inp.value = d[k];
    });
    var map = { "--bg-p": "bg", "--text-p": "text", "--sub-p": "sub", "--faint-p": "faint", "--fill-p": "fill", "--line-p": "line",
      "--accent-p": "accent", "--on-p": "onAccent", "--prog-p": "progress", "--cbg-p": "card.bg", "--ctext-p": "card.text",
      "--csub-p": "card.sub", "--cline-p": "card.line", "--caccent-p": "card.accent", "--con-p": "card.onAccent" };
    Object.keys(map).forEach(function (v) { pv.style.setProperty(v, css(d[map[v]])); });
    var a = contrast(d.text, d.bg), b = contrast(d.sub, d.bg), c = contrast(d["card.accent"], d["card.bg"]);
    function show(label, v, min) { return label + " <b>" + v.toFixed(1).replace(".", lang === "en" ? "." : ",") + ":1</b>" + (v < min ? " (" + t("c.low") + ")" : ""); }
    $("#contrast").innerHTML = [show(t("c.text"), a, 4.5), show(t("c.sub"), b, 3), show(t("c.card"), c, 3)].join(" · ");
    summary();
  }
  hooks.push(render);
  [cBg, cText, cAccent].forEach(function (i) { on(i, "input", function () { render(); }); });

  var PRESETS = {
    navy: ["#0E1734", "#EEF4FF", "#B8C4EA"],
    mono: ["#171717", "#F4F4F4", "#C4C4C4"],
    sakura: ["#2A1620", "#FFF0F5", "#F5A9C4"],
    forest: ["#102018", "#EAF5EC", "#8FD1A4"],
    ember: ["#1F120C", "#FFF3EA", "#F2A65A"]
  };
  $$("[data-preset]").forEach(function (b) {
    var p = PRESETS[b.getAttribute("data-preset")];
    b.style.setProperty("--a", p[0]);
    b.style.setProperty("--b", p[2]);
    on(b, "click", function () {
      cBg.value = p[0].toLowerCase(); cText.value = p[1].toLowerCase(); cAccent.value = p[2].toLowerCase();
      S.own = {};
      render();
    });
  });

  // Images: any format in, PNG out
  var LIMIT_BYTES = 8 * 1024 * 1024, LIMIT_PX = 8 * 1024 * 1024;
  function decode(blob) {
    return new Promise(function (res, rej) {
      var url = URL.createObjectURL(blob), img = new Image();
      img.onload = function () { res({ img: img, url: url }); };
      img.onerror = function () { URL.revokeObjectURL(url); rej(); };
      img.src = url;
    });
  }
  // Real PNG bytes, not just the name
  function isPng(file) {
    return file.slice(0, 8).arrayBuffer().then(function (b) {
      var u = new Uint8Array(b);
      return u.length === 8 && u[0] === 0x89 && u[1] === 0x50 && u[2] === 0x4E && u[3] === 0x47;
    });
  }
  function toPng(file) {
    return Promise.all([decode(file), isPng(file)]).then(function (x) {
      var r = x[0], w = r.img.naturalWidth, h = r.img.naturalHeight;
      if (x[1]) return { blob: file, url: r.url, w: w, h: h };
      var c = document.createElement("canvas");
      c.width = w; c.height = h;
      c.getContext("2d").drawImage(r.img, 0, 0);
      URL.revokeObjectURL(r.url);
      return new Promise(function (res) {
        c.toBlob(function (b) { res({ blob: b, url: URL.createObjectURL(b), w: w, h: h }); }, "image/png");
      });
    });
  }
  function note(it, min) {
    if (it.blob.size > LIMIT_BYTES || it.w * it.h > LIMIT_PX || Math.max(it.w, it.h) > 16384) return { cls: "warn", text: t("s.big") };
    if (Math.max(it.w / (it.frames || 1), it.h) < min) return { cls: "warn", text: t("s.small") };
    return null;
  }
  function addImages(files, list, max, done) {
    Array.prototype.slice.call(files).forEach(function (f) {
      if (!/^image\//.test(f.type)) { status(t("s.badimg", { f: f.name })); return; }
      toPng(f).then(function (it) {
        it.frames = /@(\d+)\.png$/i.test(f.name) ? Math.min(120, +RegExp.$1) : 1;
        if (list.length < max) list.push(it);
        done();
      }, function () { status(t("s.badimg", { f: f.name })); });
    });
  }
  var X = "<svg viewBox=\"0 0 12 12\" aria-hidden=\"true\"><path d=\"M2 2l8 8M10 2l-8 8\" fill=\"none\"/></svg>";
  function listView(ul, list, kind) {
    ul.innerHTML = "";
    list.forEach(function (it, i) {
      var li = document.createElement("li");
      var name = fileName(kind, it, i);
      var warn = note(it, kind === "emblem" ? 64 : kind === "corner" ? 100 : 30);
      li.innerHTML = "<span class=\"th" + (kind === "emblem" ? " round" : "") + "\"></span><span class=\"nm\"></span>" +
        (kind === "knob" ? "" : "<label><span></span><input type=\"number\" min=\"1\" max=\"120\"></label>") +
        "<button type=\"button\" class=\"pk-x\">" + X + "</button>";
      var th = li.querySelector(".th");
      th.style.backgroundImage = "url(" + it.url + ")";
      // First frame of a strip
      if (it.frames > 1) { th.style.backgroundSize = it.frames * 100 + "% auto"; th.style.backgroundPosition = "0 50%"; }
      var nm = li.querySelector(".nm");
      nm.textContent = name;
      var small = document.createElement("small");
      small.textContent = it.w + " × " + it.h + (warn ? " · " + warn.text : "");
      if (warn) small.className = warn.cls;
      nm.appendChild(small);
      var fr = li.querySelector("input");
      if (fr) {
        li.querySelector("label span").textContent = t("pk.frames");
        fr.value = it.frames;
        fr.setAttribute("aria-label", t("pk.frames") + " " + name);
        on(fr, "change", function () { it.frames = Math.max(1, Math.min(120, parseInt(fr.value, 10) || 1)); refreshImages(); });
      }
      var x = li.querySelector(".pk-x");
      x.setAttribute("aria-label", t("pk.remove") + " " + name);
      on(x, "click", function () {
        URL.revokeObjectURL(it.url);
        if (kind === "knob") S.knob = null; else list.splice(i, 1);
        refreshImages();
      });
      ul.appendChild(li);
    });
  }
  function pad(n) { return ("0" + n).slice(-2); }
  function fileName(kind, it, i) {
    if (kind === "knob") return "knob.png";
    return kind + pad(i + 1) + (it.frames > 1 ? "@" + it.frames : "") + ".png";
  }
  function refreshImages() {
    listView($("#l-emblem"), S.emblems, "emblem");
    listView($("#l-corner"), S.corners, "corner");
    listView($("#l-knob"), S.knob ? [S.knob] : [], "knob");
    $("#knob-opts").hidden = !S.knob;
    preview();
    summary();
  }
  hooks.push(refreshImages);
  on($("#f-emblem"), "change", function (e) { addImages(e.target.files, S.emblems, 20, refreshImages); e.target.value = ""; });
  on($("#f-corner"), "change", function (e) { addImages(e.target.files, S.corners, 30, refreshImages); e.target.value = ""; });
  on($("#f-knob"), "change", function (e) {
    var f = e.target.files[0];
    e.target.value = "";
    if (!f) return;
    if (!/^image\//.test(f.type)) { status(t("s.badimg", { f: f.name })); return; }
    toPng(f).then(function (it) { it.frames = 1; S.knob = it; refreshImages(); }, function () { status(t("s.badimg", { f: f.name })); });
  });
  ["o-kh", "o-ka", "o-kx", "o-fps"].forEach(function (id) { on($("#" + id), "input", preview); });

  // Sprite or still into a fixed box
  function paint(el, it, box, top) {
    el.classList.remove("strip");
    el.style.cssText = "";
    if (!it) return;
    var n = Math.max(1, it.frames || 1), fw = it.w / n, k = box / Math.max(fw, it.h);
    if (top) k = box / Math.min(fw, it.h);
    el.style.backgroundImage = "url(" + it.url + ")";
    if (n > 1) {
      el.classList.add("strip");
      el.style.width = (top ? box : fw * k) + "px";
      el.style.height = (top ? box : it.h * k) + "px";
      el.style.backgroundSize = (it.w * k) + "px " + (it.h * k) + "px";
      el.style.setProperty("--n", n);
      el.style.setProperty("--sx", -(it.w * k) + "px");
      el.style.setProperty("--dur", (n / Math.max(1, +$("#o-fps").value || 12)) + "s");
    }
  }
  function preview() {
    paint($("#pv-emb"), S.emblems[0], 30, true);
    paint($("#pv-mascot"), S.corners[0], 117, false);
    var head = $("#pv-head");
    head.innerHTML = "";
    head.classList.toggle("img", !!S.knob);
    if (S.knob) {
      var img = document.createElement("img");
      img.src = S.knob.url;
      img.alt = "";
      head.appendChild(img);
      head.style.setProperty("--kh", (Math.max(8, Math.min(48, +$("#o-kh").value || 15)) * 1.5) + "px");
      head.style.setProperty("--ka", Math.max(0, Math.min(1, +$("#o-ka").value || 0)));
      head.style.setProperty("--kx", Math.max(0, Math.min(1, +$("#o-kx").value || 0)));
    }
  }

  // Greetings with Tepi's day parts
  var greet = $("#greet");
  var GREET = {
    id: "Halo! Senang kamu kembali.\nTepi siap menemani hari ini.\nSiap mulai? Pelan-pelan saja.\n\n[pagi]\nSelamat pagi. Minum air dulu, ya.\nPagi! Satu hal kecil dulu, baru yang besar.\n\n[siang]\nSiang. Jangan lupa makan.\nSetengah hari lewat. Masih kuat?\n\n[sore]\nSore. Sedikit lagi selesai.\nLangit mulai jingga. Rehat sebentar.\n\n[malam]\nMalam. Jangan begadang terus.\nSatu tugas lagi, lalu istirahat.",
    en: "Hi! Good to see you again.\nTepi is here for the day.\nReady? Take it one step at a time.\n\n[morning]\nGood morning. Water first.\nMorning! Something small first, then the big one.\n\n[afternoon]\nAfternoon. Remember to eat.\nHalfway through. Still going?\n\n[evening]\nEvening. Almost done.\nThe sky is turning orange. Take a short break.\n\n[night]\nNight. Don't stay up too late.\nOne more task, then rest."
  };
  var PART = { pagi: "pagi", morning: "pagi", siang: "siang", noon: "siang", afternoon: "siang", sore: "sore", evening: "sore", malam: "malam", night: "malam" };
  function greetLines() {
    var out = { "": [] }, cur = "";
    greet.value.split(/\r?\n/).forEach(function (l) {
      l = l.trim();
      if (!l) return;
      var m = /^\[(\w+)\]$/.exec(l);
      if (m) { cur = PART[m[1].toLowerCase()] || "?"; out[cur] = out[cur] || []; return; }
      (out[cur] = out[cur] || []).push(l);
    });
    return out;
  }
  function dayPart() { var h = new Date().getHours(); return h < 4 ? "malam" : h < 11 ? "pagi" : h < 15 ? "siang" : h < 18 ? "sore" : "malam"; }
  function nextGreeting() {
    var g = greetLines(), pool = (g[""] || []).concat(g[dayPart()] || []);
    $("#pv-greet").textContent = pool.length ? pool[Math.floor(Math.random() * pool.length)] : "Tepi";
  }
  on($("#pv-next"), "click", nextGreeting);
  on(greet, "input", function () { S.greetDirty = true; summary(); });
  on(greet, "change", nextGreeting);
  hooks.push(function () { if (!S.greetDirty) { greet.value = GREET[lang]; nextGreeting(); } });

  // Sounds: any audio in, WAV out
  var SLOTS = ["", "pagi", "siang", "sore", "malam"];
  var voiceBox = $("#voices");
  SLOTS.forEach(function (s) {
    var row = document.createElement("div");
    row.className = "pk-voice";
    row.innerHTML = "<b></b><span></span><label class=\"pk-ghost pk-file\"><span></span><input type=\"file\" accept=\"audio/*,.wav\" multiple></label><button type=\"button\" class=\"pk-mini\"></button>";
    var inp = row.querySelector("input");
    on(inp, "change", function () {
      Array.prototype.slice.call(inp.files).forEach(function (f) {
        toWav(f).then(function (b) { S.voices[s].push({ name: slug(f.name.replace(/\.[^.]+$/, "")) || "suara", blob: b }); voicesView(); summary(); },
          function () { status(t("s.badaudio", { f: f.name })); });
      });
      inp.value = "";
    });
    on(row.querySelector(".pk-mini"), "click", function () { S.voices[s] = []; voicesView(); summary(); });
    row.setAttribute("data-slot", s);
    voiceBox.appendChild(row);
  });
  function voicesView() {
    $$(".pk-voice", voiceBox).forEach(function (row) {
      var s = row.getAttribute("data-slot"), list = S.voices[s];
      row.querySelector("b").textContent = t(s ? "pk.vo." + s : "pk.vo.any");
      row.querySelector("span").textContent = list.length ? list.map(function (v) { return v.name + ".wav"; }).join(", ") : t("pk.vo.none");
      row.querySelector(".pk-file span").textContent = t("pk.add");
      row.querySelector(".pk-file input").setAttribute("aria-label", t("pk.add") + " " + row.querySelector("b").textContent);
      var clr = row.querySelector(".pk-mini");
      clr.textContent = t("pk.vo.clear");
      clr.hidden = !list.length;
    });
  }
  hooks.push(voicesView);
  function toWav(file) {
    if (/\.wav$/i.test(file.name) || file.type === "audio/wav" || file.type === "audio/x-wav") return Promise.resolve(file);
    var Ctx = window.AudioContext || window.webkitAudioContext;
    if (!Ctx) return Promise.reject();
    return file.arrayBuffer().then(function (buf) {
      var ctx = new Ctx();
      return new Promise(function (res, rej) { ctx.decodeAudioData(buf, res, rej); }).then(function (a) { ctx.close(); return encodeWav(a); });
    });
  }
  // 16-bit PCM WAV
  function encodeWav(a) {
    var ch = Math.min(2, a.numberOfChannels), n = a.length, rate = a.sampleRate;
    var dv = new DataView(new ArrayBuffer(44 + n * ch * 2));
    function str(o, s) { for (var i = 0; i < s.length; i++) dv.setUint8(o + i, s.charCodeAt(i)); }
    str(0, "RIFF"); dv.setUint32(4, 36 + n * ch * 2, true); str(8, "WAVE"); str(12, "fmt ");
    dv.setUint32(16, 16, true); dv.setUint16(20, 1, true); dv.setUint16(22, ch, true); dv.setUint32(24, rate, true);
    dv.setUint32(28, rate * ch * 2, true); dv.setUint16(32, ch * 2, true); dv.setUint16(34, 16, true);
    str(36, "data"); dv.setUint32(40, n * ch * 2, true);
    var data = [];
    for (var c = 0; c < ch; c++) data.push(a.getChannelData(c));
    var o = 44;
    for (var i = 0; i < n; i++) for (c = 0; c < ch; c++) {
      var v = Math.max(-1, Math.min(1, data[c][i]));
      dv.setInt16(o, v < 0 ? v * 0x8000 : v * 0x7FFF, true);
      o += 2;
    }
    return new Blob([dv.buffer], { type: "audio/wav" });
  }

  // Prompt for any chat AI
  var P_ID = [
    "Buatkan paket tema untuk Tepi, aplikasi dynamic island di Windows.",
    "Tema: {theme}",
    "Suasana dan warna: {mood}",
    "",
    "Jawab dengan tiga bagian berikut, dalam urutan dan format yang sama.",
    "",
    "1) theme.ini dalam satu blok kode ```ini",
    "Warna memakai #RRGGBB, atau #AARRGGBB kalau perlu transparan (AA = kepekatan).",
    "Isi semua kunci ini:",
    "bg, text, sub, faint, fill, fillHover, line, accent, onAccent, progress,",
    "card.bg, card.text, card.sub, card.faint, card.fill, card.fillHover, card.line, card.accent, card.onAccent",
    "Aturan warna:",
    "- bg adalah latar island: gelap dan pekat, sesuai tema.",
    "- text kontras minimal 7:1 terhadap bg, sub minimal 4.5:1, faint minimal 3:1.",
    "- fill, fillHover, dan line adalah warna terang yang senada dengan kepekatan 2E, 4A, dan 3A, contohnya #2E8FC8FF.",
    "- accent dan progress adalah warna khas tema. onAccent kontras minimal 4.5:1 terhadap accent.",
    "- Kunci card. untuk kartu terang seperti kartu izin: card.bg terang, card.text gelap, card.accent kontras minimal 4.5:1 terhadap card.bg, dan card.fill, card.fillHover, card.line memakai warna card.accent dengan kepekatan 1E, 35, dan 30.",
    "Tambahkan juga baris ini:",
    "anim.fps=12",
    "anim.seconds=3",
    "anim.idle=0",
    "effects=water, hanya kalau temanya berhubungan dengan air.",
    "",
    "2) greetings.txt dalam satu blok kode ```text",
    "- Satu sapaan per baris, maksimal 70 karakter, bahasa Indonesia yang santai dan hangat, sesuai tema.",
    "- 4 sampai 6 baris umum di paling atas, lalu bagian [pagi], [siang], [sore], dan [malam], masing-masing 3 sampai 4 baris.",
    "- Tulis sendiri, tanpa menyalin kutipan dari game, film, lagu, atau buku.",
    "",
    "3) Prompt gambar, satu per berkas, dalam bentuk daftar:",
    "- emblem.png: lambang persegi 512x512, satu subjek di tengah dengan bentuk tegas. Nanti dipotong lingkaran dan tampil sekitar 24 px, jadi harus tetap terbaca di ukuran kecil.",
    "- corner01.png sampai corner03.png: maskot setinggi 512 px dengan pose berbeda, menghadap ke kiri, di atas latar polos satu warna supaya mudah dibuat transparan. Maskot ini duduk di pojok kanan bawah panel.",
    "- knob.png: pegangan bar progres yang melebar, sekitar 512x256, latar polos, ujung kanannya menunjuk posisi lagu."
  ].join("\n");
  var P_EN = [
    "Create a theme pack for Tepi, a dynamic island app for Windows.",
    "Theme: {theme}",
    "Mood and colors: {mood}",
    "",
    "Answer with the three parts below, in the same order and format.",
    "",
    "1) theme.ini in one ```ini code block",
    "Colors use #RRGGBB, or #AARRGGBB when transparency is needed (AA = opacity).",
    "Fill in every one of these keys:",
    "bg, text, sub, faint, fill, fillHover, line, accent, onAccent, progress,",
    "card.bg, card.text, card.sub, card.faint, card.fill, card.fillHover, card.line, card.accent, card.onAccent",
    "Color rules:",
    "- bg is the island background: dark and solid, fitting the theme.",
    "- text has at least 7:1 contrast with bg, sub at least 4.5:1, faint at least 3:1.",
    "- fill, fillHover, and line are a light matching color at opacity 2E, 4A, and 3A, for example #2E8FC8FF.",
    "- accent and progress are the theme's signature color. onAccent has at least 4.5:1 contrast with accent.",
    "- The card. keys style light cards such as the permission card: card.bg light, card.text dark, card.accent at least 4.5:1 against card.bg, and card.fill, card.fillHover, card.line use the card.accent color at opacity 1E, 35, and 30.",
    "Also add these lines:",
    "anim.fps=12",
    "anim.seconds=3",
    "anim.idle=0",
    "effects=water, only if the theme is about water.",
    "",
    "2) greetings.txt in one ```text code block",
    "- One greeting per line, at most 70 characters, warm and casual English, fitting the theme.",
    "- 4 to 6 general lines at the top, then the sections [morning], [afternoon], [evening], and [night], with 3 to 4 lines each.",
    "- Write them yourself; do not copy quotes from games, films, songs, or books.",
    "",
    "3) Image prompts, one per file, as a list:",
    "- emblem.png: a square 512x512 emblem, one subject in the middle with a bold shape. It is cropped to a circle and shown at about 24 px, so it must read at small sizes.",
    "- corner01.png to corner03.png: a mascot 512 px tall in different poses, facing left, on a plain single-color background so it is easy to make transparent. The mascot sits in the bottom-right corner of the panel.",
    "- knob.png: a wide progress bar knob, about 512x256, plain background, with its right end pointing at the song position."
  ].join("\n");
  function prompt() {
    var th = $("#ai-theme").value.trim() || t("pk.ai.theme.ph");
    var md = $("#ai-mood").value.trim() || t("pk.ai.mood.ph");
    return (lang === "en" ? P_EN : P_ID).replace("{theme}", th).replace("{mood}", md);
  }
  function promptView() {
    var p = prompt();
    $("#ai-prompt").value = p;
    $("#ai-claude").href = "https://claude.ai/new?q=" + encodeURIComponent(p);
    $("#ai-gpt").href = "https://chatgpt.com/?q=" + encodeURIComponent(p);
  }
  hooks.push(promptView);
  on($("#ai-theme"), "input", promptView);
  on($("#ai-mood"), "input", promptView);
  function copyText(text, btn, key) {
    var done = function () { btn.textContent = t("pk.copied"); setTimeout(function () { btn.textContent = t(key); }, 1500); };
    if (navigator.clipboard && window.isSecureContext) navigator.clipboard.writeText(text).then(done, done);
    else { var ta = document.createElement("textarea"); ta.value = text; document.body.appendChild(ta); ta.select(); try { document.execCommand("copy"); } catch (e) { } document.body.removeChild(ta); done(); }
  }
  on($("#ai-copy"), "click", function () { copyText($("#ai-prompt").value, $("#ai-copy"), "pk.ai.copy"); });
  on($("#copy-path"), "click", function () { copyText("%APPDATA%\\Tepi\\themes", $("#copy-path"), "pk.copy"); });

  // Read an AI answer or pack files
  function status(msg) { $("#ai-status").textContent = msg; }
  var OPT = { "anim.fps": "o-fps", "anim.seconds": "o-sec", "anim.idle": "o-idle", "knob.height": "o-kh", "knob.anchor": "o-ka", "knob.x": "o-kx" };
  function readIni(txt) {
    var got = { c: 0, o: 0 };
    txt.split(/\r?\n/).forEach(function (line) {
      line = line.trim();
      if (!line || line[0] === ";" || (line[0] === "#" && line.indexOf("=") < 0)) return;
      var i = line.indexOf("=");
      if (i < 1) return;
      var k = line.slice(0, i).trim(), v = line.slice(i + 1).trim().replace(/\s+[#;].*$/, "");
      if (KEYS.indexOf(k) >= 0 && HEX.test(v)) {
        v = v.toUpperCase();
        if (k === "bg") cBg.value = "#" + v.slice(-6).toLowerCase();
        else if (k === "text") cText.value = "#" + v.slice(-6).toLowerCase();
        else if (k === "accent") cAccent.value = "#" + v.slice(-6).toLowerCase();
        S.own[k] = v;
        got.c++;
      } else if (OPT[k] && v !== "" && !isNaN(+v)) { $("#" + OPT[k]).value = +v; got.o++; }
      else if (k === "effects") { $("#o-water").checked = /water/i.test(v); got.o++; }
      else if (k === "eyes") { $("#o-eyes").checked = v !== "0"; got.o++; }
    });
    ["bg", "text", "accent"].forEach(function (k) { delete S.own[k]; });
    return got;
  }
  function isGreet(b) { return /^\s*\[(pagi|siang|sore|malam|morning|noon|afternoon|evening|night)\]\s*$/mi.test(b); }
  function readAnswer(text) {
    var blocks = [], re = /```[^\n]*\n([\s\S]*?)```/g, m, parts = [];
    while ((m = re.exec(text))) blocks.push(m[1]);
    if (!blocks.length) blocks = [text];
    var c = 0, o = 0, g = 0;
    blocks.forEach(function (b) {
      if (/^\s*(card\.)?bg\s*=/m.test(b)) { var r = readIni(b); c += r.c; o += r.o; }
      else if (isGreet(b)) { greet.value = b.trim(); S.greetDirty = true; g = b.trim().split(/\r?\n/).filter(function (l) { return l.trim() && !/^\[/.test(l.trim()); }).length; }
    });
    if (c) parts.push(c + " " + t("s.colors"));
    if (g) parts.push(g + " " + t("s.greet"));
    if (o) parts.push(o + " " + t("s.opts"));
    status(parts.length ? t("s.applied", { n: parts.join(", ") }) : t("s.none"));
    render();
    refreshImages();
    nextGreeting();
  }
  on($("#ai-apply"), "click", function () { readAnswer($("#ai-answer").value); });
  on($("#ai-files"), "change", function (e) {
    var files = Array.prototype.slice.call(e.target.files);
    e.target.value = "";
    Promise.all(files.map(function (f) { return f.text(); })).then(function (texts) {
      readAnswer(texts.map(function (x) { return "```\n" + x + "\n```"; }).join("\n"));
    });
  });

  // Pack name as a folder slug
  function slug(s) { return s.toLowerCase().normalize("NFD").replace(/[̀-ͯ]/g, "").replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 40); }
  var nameIn = $("#pk-name");
  on(nameIn, "input", summary);
  on(nameIn, "blur", function () { nameIn.value = slug(nameIn.value) || "paket-saya"; summary(); });

  function summary() {
    var g = greetLines(), lines = 0;
    Object.keys(g).forEach(function (k) { if (k !== "?") lines += g[k].length; });
    var v = SLOTS.reduce(function (n, s) { return n + S.voices[s].length; }, 0);
    var bits = [(slug(nameIn.value) || "paket-saya") + ".zip"];
    if (S.emblems.length) bits.push(t("sum.emb", { n: S.emblems.length }));
    if (S.corners.length) bits.push(t("sum.mas", { n: S.corners.length }));
    if (S.knob) bits.push(t("sum.knob"));
    if (lines) bits.push(t("sum.greet", { n: lines }));
    if (v) bits.push(t("sum.voice", { n: v }));
    $("#summary").textContent = bits.join(" · ");
  }

  // Theme.ini text
  function ini(name) {
    var d = palette(), out = ["# " + name];
    KEYS.forEach(function (k, i) { if (i === 10) out.push(""); out.push(k + "=" + d[k]); });
    out.push("", "anim.fps=" + clamp("#o-fps", 1, 30, 12), "anim.seconds=" + clamp("#o-sec", 1, 20, 3), "anim.idle=" + clamp("#o-idle", 0, 3600, 0));
    if ($("#o-water").checked) out.push("effects=water");
    if (!$("#o-eyes").checked) out.push("eyes=0");
    if (S.knob) out.push("knob.height=" + clamp("#o-kh", 8, 48, 15), "knob.anchor=" + frac("#o-ka", 0.5), "knob.x=" + frac("#o-kx", 1));
    return out.join("\r\n") + "\r\n";
  }
  function clamp(sel, lo, hi, def) { var v = parseInt($(sel).value, 10); return isNaN(v) ? def : Math.max(lo, Math.min(hi, v)); }
  function frac(sel, def) { var v = parseFloat($(sel).value); return isNaN(v) ? def : Math.max(0, Math.min(1, v)); }

  // Minimal ZIP (stored)
  var CRC = (function () {
    var tb = new Uint32Array(256);
    for (var n = 0; n < 256; n++) { var c = n; for (var k = 0; k < 8; k++) c = c & 1 ? 0xEDB88320 ^ (c >>> 1) : c >>> 1; tb[n] = c >>> 0; }
    return tb;
  })();
  function crc32(u) { var c = 0xFFFFFFFF; for (var i = 0; i < u.length; i++) c = CRC[(c ^ u[i]) & 255] ^ (c >>> 8); return (c ^ 0xFFFFFFFF) >>> 0; }
  function zip(files) {
    var enc = new TextEncoder(), now = new Date(), parts = [], dir = [], off = 0;
    var time = (now.getHours() << 11) | (now.getMinutes() << 5) | (now.getSeconds() >> 1);
    var date = ((now.getFullYear() - 1980) << 9) | ((now.getMonth() + 1) << 5) | now.getDate();
    files.forEach(function (f) {
      var name = enc.encode(f.name), crc = crc32(f.data), size = f.data.length;
      var h = new DataView(new ArrayBuffer(30));
      h.setUint32(0, 0x04034b50, true); h.setUint16(4, 20, true); h.setUint16(6, 0x0800, true);
      h.setUint16(10, time, true); h.setUint16(12, date, true); h.setUint32(14, crc, true);
      h.setUint32(18, size, true); h.setUint32(22, size, true); h.setUint16(26, name.length, true);
      parts.push(new Uint8Array(h.buffer), name, f.data);
      var c = new DataView(new ArrayBuffer(46));
      c.setUint32(0, 0x02014b50, true); c.setUint16(4, 20, true); c.setUint16(6, 20, true); c.setUint16(8, 0x0800, true);
      c.setUint16(12, time, true); c.setUint16(14, date, true); c.setUint32(16, crc, true);
      c.setUint32(20, size, true); c.setUint32(24, size, true); c.setUint16(28, name.length, true); c.setUint32(42, off, true);
      dir.push(new Uint8Array(c.buffer), name);
      off += 30 + name.length + size;
    });
    var dirSize = dir.reduce(function (s, a) { return s + a.length; }, 0);
    var e = new DataView(new ArrayBuffer(22));
    e.setUint32(0, 0x06054b50, true); e.setUint16(8, files.length, true); e.setUint16(10, files.length, true);
    e.setUint32(12, dirSize, true); e.setUint32(16, off, true);
    return new Blob(parts.concat(dir, [new Uint8Array(e.buffer)]), { type: "application/zip" });
  }

  function bytes(blob) { return blob.arrayBuffer().then(function (b) { return new Uint8Array(b); }); }
  on($("#build"), "click", function () {
    var name = slug(nameIn.value) || "paket-saya", enc = new TextEncoder(), jobs = [];
    var files = [{ name: name + "/theme.ini", data: enc.encode(ini(name)) }];
    if (greet.value.trim()) files.push({ name: name + "/greetings.txt", data: enc.encode(greet.value.trim().replace(/\r?\n/g, "\r\n") + "\r\n") });
    function add(path, blob) { jobs.push(bytes(blob).then(function (u) { files.push({ name: name + "/" + path, data: u }); })); }
    S.emblems.forEach(function (it, i) { if (!note(it, 0)) add(fileName("emblem", it, i), it.blob); });
    S.corners.forEach(function (it, i) { if (!note(it, 0)) add(fileName("corner", it, i), it.blob); });
    if (S.knob && !note(S.knob, 0)) add("knob.png", S.knob.blob);
    SLOTS.forEach(function (s) {
      var used = {};
      S.voices[s].forEach(function (v) {
        var n = v.name, k = 2;
        while (used[n]) n = v.name + "-" + k++;
        used[n] = 1;
        add("voice/" + (s ? s + "/" : "") + n + ".wav", v.blob);
      });
    });
    var btn = $("#build");
    btn.disabled = true;
    $("#summary").textContent = t("s.building");
    Promise.all(jobs).then(function () {
      var a = document.createElement("a");
      a.href = URL.createObjectURL(zip(files));
      a.download = name + ".zip";
      document.body.appendChild(a);
      a.click();
      a.remove();
      setTimeout(function () { URL.revokeObjectURL(a.href); }, 4000);
      btn.disabled = false;
      summary();
      $("#summary").textContent += " · " + t("s.done", { f: name + ".zip" });
    });
  });

  apply(lang);
  render();
  refreshImages();
  voicesView();
  nextGreeting();
})();
