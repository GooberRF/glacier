using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Ged.Core.Modules;

namespace Ged.Automation;

/// <summary>
/// Writes the module contact sheet page: one self-contained HTML file (inline CSS and script, no external resources,
/// works opened from disk) with the module data and the library's current <c>curation.json</c> embedded. The page keeps
/// in-progress choices in the browser's local storage and exports <c>curation.json</c> (schema
/// <see cref="ModuleCuration.Schema"/>) merged with the entries it was generated with.
/// </summary>
internal static class ModuleSheetHtml
{
    public static string Build(ModuleLibrary lib, IReadOnlyList<ModuleInfo> modules, IReadOnlyDictionary<string, string?> status, IReadOnlyDictionary<string, string> warnings, ModuleSheetRequest request)
    {
        var rows = new JsonArray();
        foreach (ModuleInfo m in modules)
        {
            (string hc, string hr, IReadOnlyList<string> hs) = m.Harvested ?? (m.Category, m.Role, m.Styles);
            List<string> sources = m.Provenance.Select(p => p.Level).Distinct(StringComparer.Ordinal).ToList();
            string? error = status.TryGetValue(m.Id, out string? e) ? e : "not rendered";
            rows.Add(new JsonObject
            {
                ["id"] = m.Id,
                ["group"] = m.Group,
                ["category"] = m.Category,
                ["role"] = m.Role,
                ["attach"] = m.Attach,
                ["styles"] = Strings(m.Styles),
                ["size"] = new JsonArray(Math.Round(m.Size.X, 2), Math.Round(m.Size.Y, 2), Math.Round(m.Size.Z, 2)),
                ["occ"] = m.Occurrences,
                ["score"] = Math.Round(m.Score, 3),
                ["brushes"] = m.Brushes,
                ["stretch"] = string.Join(string.Empty, m.StretchAxes),
                ["custom"] = m.CustomTextures,
                ["sources"] = Strings(sources.Take(4)),
                ["more"] = Math.Max(0, sources.Count - 4),
                ["thumb"] = error is null ? "thumbs/" + Uri.EscapeDataString(m.Id) + ".png" : null,
                ["error"] = error,
                ["warn"] = warnings.TryGetValue(m.Id, out string? w) ? w : ModuleSheet.PlacementWarning(m),
                ["verdict"] = m.Verdict,
                ["note"] = m.CurationNote,
                ["h"] = new JsonObject { ["category"] = hc, ["role"] = hr, ["styles"] = Strings(hs) },
            });
        }

        // The file's own entries (all of them, also ids this sheet does not show) so the export merges, never drops.
        var curation = new JsonObject();
        var ignored = new List<string>();
        foreach ((string id, ModuleCurationEntry entry) in ModuleCuration.Load(lib.Directory, ignored).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var o = new JsonObject();
            if (entry.Verdict is { } v)
            {
                o["verdict"] = v;
            }

            if (entry.Category is { } c)
            {
                o["category"] = c;
            }

            if (entry.Role is { } r)
            {
                o["role"] = r;
            }

            if (entry.Styles is { } s)
            {
                o["styles"] = Strings(s);
            }

            if (entry.Note is { } n)
            {
                o["note"] = n;
            }

            curation[id] = o;
        }

        var data = new JsonObject
        {
            ["schema"] = ModuleCuration.Schema,
            ["library"] = lib.Directory,
            ["curationPath"] = lib.Directory is null ? ModuleCuration.FileName : Path.Combine(lib.Directory, ModuleCuration.FileName),
            ["filters"] = new JsonObject { ["group"] = request.Group, ["category"] = request.Category, ["style"] = request.Style, ["top"] = request.Top },
            ["categories"] = Strings(ModuleVocabulary.Categories),
            ["roles"] = Strings(ModuleVocabulary.Roles),
            ["styles"] = Strings(ModuleVocabulary.Styles),
            ["groups"] = Strings(modules.Select(m => m.Group).Distinct(StringComparer.Ordinal).OrderBy(g => g, StringComparer.Ordinal)),
            ["modules"] = rows,
            ["curation"] = curation,
        };

        // The default encoder escapes <, > and & (and non-ASCII), so the JSON cannot close the script element.
        string json = data.ToJsonString();
        return Template.Replace("/*GLACIER_SHEET_DATA*/", json, StringComparison.Ordinal);
    }

    private static JsonArray Strings(IEnumerable<string> items) => new(items.Select(s => (JsonNode?)s).ToArray());

    private const string Template = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Module Library Sheet</title>
<style>
:root {
  color-scheme: light dark;
  --bg: #f3f4f6; --panel: #ffffff; --ink: #1c2026; --muted: #5d6673; --line: #d5d9e0;
  --keep: #1f7a3d; --keep-bg: #e3f4e8; --reject: #b3261e; --reject-bg: #fbe5e3; --accent: #2457c5; --changed: #a15c00;
  --thumb: #1a1c20;
}
@media (prefers-color-scheme: dark) {
  :root {
    --bg: #15171b; --panel: #1f2228; --ink: #e6e8ec; --muted: #9aa3af; --line: #343944;
    --keep: #62c987; --keep-bg: #193324; --reject: #f08a80; --reject-bg: #3a1d1b; --accent: #7ea6ff; --changed: #f0b45c;
    --thumb: #0e0f12;
  }
}
* { box-sizing: border-box; }
body { margin: 0; background: var(--bg); color: var(--ink); font: 13px/1.4 system-ui, -apple-system, "Segoe UI", sans-serif; }
header { position: sticky; top: 0; z-index: 5; background: var(--panel); border-bottom: 1px solid var(--line); padding: 10px 16px; }
h1 { font-size: 16px; margin: 0 0 6px; }
.bar { display: flex; flex-wrap: wrap; gap: 8px 14px; align-items: center; }
.bar label { display: inline-flex; gap: 5px; align-items: center; color: var(--muted); }
select, input[type=text], textarea, button { font: inherit; color: var(--ink); background: var(--bg); border: 1px solid var(--line); border-radius: 4px; padding: 3px 6px; }
button { cursor: pointer; background: var(--panel); }
button.primary { background: var(--accent); color: #fff; border-color: var(--accent); }
.counts { color: var(--muted); }
.counts b { color: var(--ink); }
.help { color: var(--muted); margin: 6px 0 0; }
main { padding: 8px 16px 40px; }
section h2 { font-size: 15px; margin: 18px 0 8px; display: flex; gap: 10px; align-items: baseline; }
section h2 small { color: var(--muted); font-weight: normal; }
.grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(260px, 1fr)); gap: 10px; }
.card { background: var(--panel); border: 1px solid var(--line); border-radius: 6px; overflow: hidden; display: flex; flex-direction: column; }
.card.keep { border-color: var(--keep); box-shadow: 0 0 0 1px var(--keep) inset; }
.card.reject { border-color: var(--reject); }
.card.reject .thumb img { opacity: 0.45; }
.card.hot { outline: 2px solid var(--accent); outline-offset: -2px; }
.thumb { background: var(--thumb); aspect-ratio: 1 / 1; display: flex; align-items: center; justify-content: center; }
.thumb img { width: 100%; height: 100%; object-fit: contain; display: block; }
.thumb .fail { color: #f08a80; padding: 12px; text-align: center; font-size: 12px; }
.body { padding: 7px 9px 9px; display: flex; flex-direction: column; gap: 4px; }
.id { font-family: ui-monospace, Consolas, monospace; font-size: 12px; word-break: break-all; }
.meta { color: var(--muted); font-size: 12px; }
.meta b { color: var(--ink); font-weight: 600; }
.src { color: var(--muted); font-size: 11px; word-break: break-all; }
.warn { color: var(--reject); font-size: 12px; }
.verdict { display: flex; gap: 4px; }
.verdict label { flex: 1; text-align: center; border: 1px solid var(--line); border-radius: 4px; padding: 2px 0; cursor: pointer; user-select: none; }
.verdict input { position: absolute; opacity: 0; pointer-events: none; }
.verdict label.on.k { background: var(--keep-bg); color: var(--keep); border-color: var(--keep); font-weight: 600; }
.verdict label.on.r { background: var(--reject-bg); color: var(--reject); border-color: var(--reject); font-weight: 600; }
.verdict label.on.u { background: var(--bg); font-weight: 600; }
.row { display: grid; grid-template-columns: 62px 1fr; gap: 4px; align-items: center; }
.row span { color: var(--muted); font-size: 12px; }
.row select, .row input { width: 100%; min-width: 0; }
.changed { color: var(--changed) !important; }
input.bad { border-color: var(--reject); }
textarea { width: 100%; min-height: 30px; resize: vertical; }
#json { width: 100%; min-height: 200px; font-family: ui-monospace, Consolas, monospace; font-size: 12px; margin-top: 8px; }
.hidden { display: none !important; }
</style>
</head>
<body>
<header>
  <h1>Module library sheet</h1>
  <div class="bar">
    <label>Group <select id="fGroup"><option value="">all</option></select></label>
    <label>Category <select id="fCat"><option value="">all</option></select></label>
    <label>Style <select id="fStyle"><option value="">all</option></select></label>
    <label><input type="checkbox" id="fUndecided"> undecided only</label>
    <label><input type="checkbox" id="fChanged"> unsaved edits only</label>
    <label>Find <input type="text" id="fText" placeholder="id or level" size="14"></label>
    <span class="counts" id="counts"></span>
  </div>
  <div class="bar" style="margin-top:6px">
    <button class="primary" id="download">Download curation.json</button>
    <button id="show">Show JSON</button>
    <button id="discard" title="Forget the choices this browser kept for this sheet and show curation.json as it was">Discard local edits</button>
    <span class="help" id="where"></span>
  </div>
  <p class="help">Keys over a card: <b>K</b> keep, <b>R</b> reject, <b>U</b> undecided. Choices are kept in this browser until you download.
  The download already includes every entry of the current curation.json; save it as that file to apply it.</p>
  <textarea id="json" class="hidden" readonly></textarea>
</header>
<main id="main"></main>
<script id="sheet-data" type="application/json">/*GLACIER_SHEET_DATA*/</script>
<script>
(function () {
  "use strict";
  var D = JSON.parse(document.getElementById("sheet-data").textContent);
  var KEY = "glacier.module-sheet:" + (D.library || "");
  var byId = {};
  var state = {};
  var cards = {};
  var hot = null;

  function eqList(a, b) {
    if (a.length !== b.length) return false;
    for (var i = 0; i < a.length; i++) if (a[i] !== b[i]) return false;
    return true;
  }
  function initial(m) {
    return { verdict: m.verdict || "", category: m.category, role: m.role, styles: m.styles.slice(), note: m.note || "" };
  }
  function same(a, b) {
    return a.verdict === b.verdict && a.category === b.category && a.role === b.role && eqList(a.styles, b.styles) && a.note === b.note;
  }
  function loadDrafts() {
    try { var t = window.localStorage.getItem(KEY); return t ? JSON.parse(t) : {}; } catch (e) { return {}; }
  }
  function saveDrafts() {
    var drafts = {};
    D.modules.forEach(function (m) { if (!same(state[m.id], initial(m))) drafts[m.id] = state[m.id]; });
    try { window.localStorage.setItem(KEY, JSON.stringify(drafts)); } catch (e) { /* storage unavailable: choices live until the page closes */ }
  }
  function parseStyles(text) {
    var out = [], bad = [];
    text.split(/[\s,;/]+/).forEach(function (s) {
      s = s.trim().toLowerCase();
      if (!s) return;
      if (D.styles.indexOf(s) < 0) bad.push(s); else if (out.indexOf(s) < 0) out.push(s);
    });
    return { styles: out, bad: bad };
  }
  function el(tag, cls, text) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text !== undefined && text !== null) e.textContent = text;
    return e;
  }
  function options(select, values, current) {
    values.forEach(function (v) { var o = el("option", null, v); o.value = v; if (v === current) o.selected = true; select.appendChild(o); });
  }

  // ---- entries: the export ----
  function entryOf(m) {
    var s = state[m.id], e = {};
    if (s.verdict) e.verdict = s.verdict;
    if (s.category !== m.h.category) e.category = s.category;
    if (s.role !== m.h.role) e.role = s.role;
    if (!eqList(s.styles, m.h.styles) && s.styles.length > 0) e.styles = s.styles.slice();
    if (s.note.trim()) e.note = s.note.trim();
    return Object.keys(e).length ? e : null;
  }
  function exportJson() {
    var mods = {};
    Object.keys(D.curation).forEach(function (id) { mods[id] = D.curation[id]; });
    D.modules.forEach(function (m) { var e = entryOf(m); if (e) mods[m.id] = e; else delete mods[m.id]; });
    var sorted = {};
    Object.keys(mods).sort().forEach(function (id) { sorted[id] = mods[id]; });
    return JSON.stringify({ schema: D.schema, modules: sorted }, null, 2) + "\n";
  }

  // ---- cards ----
  function paint(m) {
    var c = cards[m.id], s = state[m.id];
    c.root.classList.toggle("keep", s.verdict === "keep");
    c.root.classList.toggle("reject", s.verdict === "reject");
    c.labels.forEach(function (l) { l.classList.toggle("on", l.dataset.v === s.verdict); });
    c.radios.forEach(function (r) { r.checked = r.value === s.verdict; });
    c.cat.classList.toggle("changed", s.category !== m.h.category);
    c.role.classList.toggle("changed", s.role !== m.h.role);
    c.styles.classList.toggle("changed", !eqList(s.styles, m.h.styles));
    c.summary.textContent = s.category + " · " + s.role + " · " + m.attach;
  }
  function setVerdict(m, v) {
    state[m.id].verdict = v;
    paint(m); saveDrafts(); counts(); filter();
  }
  function card(m) {
    var root = el("div", "card");
    root.dataset.id = m.id;
    var th = el("div", "thumb");
    if (m.thumb) {
      var img = el("img"); img.loading = "lazy"; img.src = m.thumb; img.alt = m.id; th.appendChild(img);
    } else {
      th.appendChild(el("div", "fail", "No thumbnail: " + (m.error || "render failed")));
    }
    root.appendChild(th);
    var b = el("div", "body");
    b.appendChild(el("div", "id", m.id));
    var summary = el("div", "meta");
    b.appendChild(summary);
    var meta = el("div", "meta");
    meta.textContent = m.size.join(" × ") + " m · ×" + m.occ + " · score " + m.score.toFixed(3) + " · " + m.brushes + " brush" + (m.brushes === 1 ? "" : "es")
      + (m.stretch ? " · stretch " + m.stretch : "") + (m.custom ? " · custom textures" : "") + " · " + m.group;
    b.appendChild(meta);
    var src = el("div", "src", m.sources.join(" | ") + (m.more ? " (+" + m.more + " more)" : ""));
    src.title = "Where it was found";
    b.appendChild(src);
    if (m.warn) b.appendChild(el("div", "warn", "⚠ " + m.warn));

    var vd = el("div", "verdict"), radios = [], labels = [];
    [["keep", "Keep", "k"], ["reject", "Reject", "r"], ["", "Undecided", "u"]].forEach(function (o) {
      var l = el("label", o[2]); l.dataset.v = o[0];
      var r = el("input"); r.type = "radio"; r.name = "v-" + m.id; r.value = o[0];
      r.addEventListener("change", function () { setVerdict(m, o[0]); });
      l.appendChild(r); l.appendChild(document.createTextNode(o[1]));
      vd.appendChild(l); radios.push(r); labels.push(l);
    });
    b.appendChild(vd);

    function row(name, input) { var r = el("div", "row"); r.appendChild(el("span", null, name)); r.appendChild(input); b.appendChild(r); return input; }
    var cat = row("category", el("select")); options(cat, D.categories, state[m.id].category);
    cat.title = "Harvested: " + m.h.category;
    cat.addEventListener("change", function () { state[m.id].category = cat.value; paint(m); saveDrafts(); counts(); });
    var role = row("role", el("select")); options(role, D.roles, state[m.id].role);
    role.title = "Harvested: " + m.h.role;
    role.addEventListener("change", function () { state[m.id].role = role.value; paint(m); saveDrafts(); counts(); });
    var st = row("styles", el("input")); st.type = "text"; st.value = state[m.id].styles.join(", ");
    st.title = "Primary first; harvested: " + m.h.styles.join(", ") + ". Known: " + D.styles.join(", ");
    st.setAttribute("list", "styleList");
    st.addEventListener("input", function () {
      var p = parseStyles(st.value);
      st.classList.toggle("bad", p.bad.length > 0);
      if (p.bad.length === 0) { state[m.id].styles = p.styles.length ? p.styles : m.h.styles.slice(); paint(m); saveDrafts(); counts(); }
    });
    var note = row("note", el("textarea")); note.value = state[m.id].note; note.rows = 1;
    note.addEventListener("input", function () { state[m.id].note = note.value; saveDrafts(); counts(); });

    root.appendChild(b);
    root.addEventListener("mouseenter", function () { if (hot) hot.classList.remove("hot"); hot = root; root.classList.add("hot"); });
    cards[m.id] = { root: root, radios: radios, labels: labels, cat: cat, role: role, styles: st, summary: summary };
    return root;
  }

  // ---- filters and counts ----
  var fGroup = document.getElementById("fGroup"), fCat = document.getElementById("fCat"), fStyle = document.getElementById("fStyle");
  var fUnd = document.getElementById("fUndecided"), fChg = document.getElementById("fChanged"), fText = document.getElementById("fText");
  var sections = [];
  function filter() {
    var g = fGroup.value, c = fCat.value, s = fStyle.value, und = fUnd.checked, chg = fChg.checked, t = fText.value.trim().toLowerCase();
    sections.forEach(function (sec) {
      var shown = 0;
      sec.mods.forEach(function (m) {
        var st = state[m.id];
        var ok = (!g || m.group === g) && (!c || sec.category === c || st.category === c) && (!s || st.styles.indexOf(s) >= 0)
          && (!und || !st.verdict) && (!chg || !same(st, initial(m)))
          && (!t || m.id.toLowerCase().indexOf(t) >= 0 || m.sources.join(" ").toLowerCase().indexOf(t) >= 0);
        cards[m.id].root.classList.toggle("hidden", !ok);
        if (!ok && hot === cards[m.id].root) { hot.classList.remove("hot"); hot = null; }
        if (ok) shown++;
      });
      sec.el.classList.toggle("hidden", shown === 0);
      sec.count.textContent = shown + " shown of " + sec.mods.length;
    });
  }
  function counts() {
    var k = 0, r = 0, u = 0, rl = 0;
    D.modules.forEach(function (m) {
      var s = state[m.id];
      if (s.verdict === "keep") k++; else if (s.verdict === "reject") r++; else u++;
      if (s.category !== m.h.category || s.role !== m.h.role || !eqList(s.styles, m.h.styles)) rl++;
    });
    document.getElementById("counts").innerHTML = "";
    var c = document.getElementById("counts");
    [["modules", D.modules.length], ["kept", k], ["rejected", r], ["undecided", u], ["relabelled", rl]].forEach(function (p, i) {
      if (i) c.appendChild(document.createTextNode(" · "));
      c.appendChild(el("b", null, String(p[1]))); c.appendChild(document.createTextNode(" " + p[0]));
    });
  }

  // ---- build ----
  var drafts = loadDrafts();
  D.modules.forEach(function (m) {
    byId[m.id] = m;
    var s = initial(m), d = drafts[m.id];
    if (d && typeof d === "object") {
      if (d.verdict === "" || d.verdict === "keep" || d.verdict === "reject") s.verdict = d.verdict;
      if (D.categories.indexOf(d.category) >= 0) s.category = d.category;
      if (D.roles.indexOf(d.role) >= 0) s.role = d.role;
      if (Array.isArray(d.styles) && d.styles.length && d.styles.every(function (x) { return D.styles.indexOf(x) >= 0; })) s.styles = d.styles.slice();
      if (typeof d.note === "string") s.note = d.note;
    }
    state[m.id] = s;
  });
  options(fGroup, D.groups, D.filters.group); options(fCat, D.categories, D.filters.category); options(fStyle, D.styles, D.filters.style);
  var dl = el("datalist"); dl.id = "styleList"; options(dl, D.styles); document.body.appendChild(dl);
  var main = document.getElementById("main");
  var current = null;
  D.modules.forEach(function (m) {
    if (!current || current.group !== m.group || current.category !== m.category) {
      var secEl = el("section"), h = el("h2", null, m.category), small = el("small", null, m.group);
      var count = el("small");
      h.appendChild(small); h.appendChild(count); secEl.appendChild(h);
      var grid = el("div", "grid"); secEl.appendChild(grid); main.appendChild(secEl);
      current = { group: m.group, category: m.category, el: secEl, grid: grid, count: count, mods: [] };
      sections.push(current);
    }
    current.mods.push(m);
    current.grid.appendChild(card(m));
    paint(m);
  });
  document.getElementById("where").textContent = "Save as: " + D.curationPath;
  [fGroup, fCat, fStyle, fUnd, fChg].forEach(function (x) { x.addEventListener("change", filter); });
  fText.addEventListener("input", filter);
  document.getElementById("download").addEventListener("click", function () {
    var blob = new Blob([exportJson()], { type: "application/json" });
    var a = document.createElement("a");
    a.href = URL.createObjectURL(blob); a.download = "curation.json";
    document.body.appendChild(a); a.click();
    setTimeout(function () { URL.revokeObjectURL(a.href); a.remove(); }, 1000);
  });
  document.getElementById("show").addEventListener("click", function () {
    var t = document.getElementById("json");
    t.value = exportJson(); t.classList.toggle("hidden");
  });
  document.getElementById("discard").addEventListener("click", function () {
    if (!window.confirm("Forget this browser's unsaved choices for this sheet?")) return;
    try { window.localStorage.removeItem(KEY); } catch (e) { /* ignore */ }
    window.location.reload();
  });
  document.addEventListener("keydown", function (e) {
    if (!hot || e.ctrlKey || e.metaKey || e.altKey) return;
    var tag = (e.target && e.target.tagName) || "";
    if (tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT") return;
    var m = byId[hot.dataset.id], k = e.key.toLowerCase();
    if (k === "k") setVerdict(m, "keep"); else if (k === "r") setVerdict(m, "reject"); else if (k === "u") setVerdict(m, "");
  });
  counts(); filter();
})();
</script>
</body>
</html>
""";
}
