"use strict";
// The phone's frame for the browser mirror (index.html), as the Windows mirror window draws it
// (windows/PhoneUseMirror: ContinuousCorner.cs, PhoneModel.cs, FrameFinish.cs, MainWindow.Frame.cs).
// The models and finishes come from phones.json, the same table the Windows app builds in.
//
// The page loads this file as a script (Frame.*); a Worker loads it too, and draws the lit band
// there (Frame.bandPixels), so a resize never stalls the video.

const Frame = (() => {
  // ---------------------------------------------------------------- iOS's continuous corner
  // UIKit's rounded-rectangle corner (ContinuousCorner.cs): it starts bending EXTENT radii from the
  // corner and eases into it. Points are (X, Y) in radii: X in from the edge the curve ends on, Y in
  // from the edge it starts on, from (EXTENT, 0) to (0, EXTENT).
  const EXTENT = 1.52866483;
  const FIRST_HALF = [
    [[1.08849323, 0], [0.86840689, 0], [0.66993427, 0.06549600]],
    [null, null, [0.63149399, 0.07491100]],  // a line
    [[0.37282392, 0.16905899], [0.16906013, 0.37282401], [0.07491176, 0.63149399]],
  ];
  // the whole corner: [c1, c2, to] cubics, and lines (c1 null); the second half mirrors the first
  const SEGMENTS = (() => {
    const swap = ([x, y]) => [y, x];
    const list = FIRST_HALF.map((s) => s.slice());
    const starts = [[EXTENT, 0], ...FIRST_HALF.map((s) => s[2])];
    for (let i = FIRST_HALF.length - 2; i >= 0; i--) {
      const [c1, c2] = FIRST_HALF[i];
      list.push(c1 ? [swap(c2), swap(c1), swap(starts[i])] : [null, null, swap(starts[i])]);
    }
    return list;
  })();
  // sampled finely, for distances
  const POINTS = (() => {
    const steps = 14, pts = [[EXTENT, 0]];
    let from = pts[0];
    for (const [c1, c2, to] of SEGMENTS) {
      if (!c1) pts.push(to);
      else for (let i = 1; i <= steps; i++) {
        const t = i / steps, u = 1 - t, a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, d = t * t * t;
        pts.push([a * from[0] + b * c1[0] + c * c2[0] + d * to[0], a * from[1] + b * c1[1] + c * c2[1] + d * to[1]]);
      }
      from = to;
    }
    return pts;
  })();

  /** The radius actually drawn: a corner can't reach past the middle of a side. */
  function limit(r, w, h) { return Math.max(0, Math.min(r, Math.min(w, h) / 2 / EXTENT)); }

  /** SVG path of a w x h rectangle at (x, y) with continuous corners of radius r. */
  function rect(x, y, w, h, r) {
    r = limit(r, w, h);
    const e = r * EXTENT, f = (v) => v.toFixed(2);
    // each corner: where it is, and the directions of its X and Y (clockwise from the top right)
    const corners = [
      [[x + w, y], [-1, 0], [0, 1]],
      [[x + w, y + h], [0, -1], [-1, 0]],
      [[x, y + h], [1, 0], [0, -1]],
      [[x, y], [0, 1], [1, 0]],
    ];
    const P = ([at, ux, uy], [vx, vy]) =>
      `${f(at[0] + (ux[0] * vx + uy[0] * vy) * r)} ${f(at[1] + (ux[1] * vx + uy[1] * vy) * r)}`;
    let d = `M${f(x + w - e)} ${f(y)}`;
    for (const c of corners) {
      d += `L${P(c, [EXTENT, 0])}`;
      for (const [c1, c2, to] of SEGMENTS) d += c1 ? `C${P(c, c1)} ${P(c, c2)} ${P(c, to)}` : `L${P(c, to)}`;
    }
    return d + "Z";
  }

  /** Signed distance from (px, py) to a rectangle of half size (bx, by) with continuous corners of
   *  radius r, centred on the origin; negative inside. */
  function sd(px, py, bx, by, r) {
    r = limit(r, 2 * bx, 2 * by);
    const e = r * EXTENT;
    const X = bx - Math.abs(px), Y = by - Math.abs(py);
    if (X >= e && Y >= e) return -Math.min(X, Y);
    if (Y >= e) return -X;
    if (X >= e) return -Y;
    if (r <= 0) return X < 0 && Y < 0 ? Math.hypot(X, Y) : -Math.min(X, Y);
    const qx = X / r, qy = Y / r;
    let best = Infinity, side = 1;
    for (let i = 0; i + 1 < POINTS.length; i++) {
      const sx = POINTS[i][0], sy = POINTS[i][1];
      const dx = POINTS[i + 1][0] - sx, dy = POINTS[i + 1][1] - sy, wx = qx - sx, wy = qy - sy;
      const t = Math.min(1, Math.max(0, (wx * dx + wy * dy) / Math.max(dx * dx + dy * dy, 1e-12)));
      const ex = wx - dx * t, ey = wy - dy * t, dist = ex * ex + ey * ey;
      if (dist < best) { best = dist; side = dx * wy - dy * wx; }  // < 0: on the inner side
    }
    const distance = Math.sqrt(best) * r;
    return side < 0 ? -distance : distance;
  }

  // ---------------------------------------------------------------- models and finishes (phones.json)
  let table = null;
  const models = new Map();
  const finishes = [];

  function hex(s) { const v = parseInt(s.slice(1), 16); return [(v >> 16) & 255, (v >> 8) & 255, v & 255]; }
  function lerp(a, b, t) { return a.map((v, i) => Math.round(v + (b[i] - v) * t)); }
  const lin = (v) => { const s = v / 255; return s <= 0.04045 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4; };
  const srgb = (l) => { l = Math.min(1, Math.max(0, l)); return l <= 0.0031308 ? l * 12.92 : 1.055 * l ** (1 / 2.4) - 0.055; };

  function model(m) {
    let buttons = null, camera = null;
    if (m.layout) {
      const at = (left, centre, half) => ({ left, top: (centre - half) / m.length, bottom: (centre + half) / m.length });
      buttons = table.layouts[m.layout].map((b) => at(b.left, b.centre, b.half));
      if (m.camera != null) camera = at(false, m.camera, table.cameraControlHalf);
    }
    return { name: m.name, glass: m.glass, material: m.material, finish: m.finish, corner: m.corner,
             buttons: buttons || table.estimatedButtons, camera };
  }

  /** Read phones.json (the parsed table). */
  function load(json) {
    table = json;
    models.clear();
    for (const m of json.models) { const made = model(m); for (const id of m.ids) models.set(id, made); }
    finishes.length = 0;
    for (const g of json.finishes) for (const f of g.finishes) {
      const base = hex(f.base), white = [255, 255, 255], black = [0, 0, 0];
      // the plain band's colours: given (Graphite), or from the base (FrameFinish.From)
      const plain = f.light
        ? { light: hex(f.light), dark: hex(f.dark), mid: hex(f.mid), buttonEdge: hex(f.buttonEdge), buttonMid: hex(f.buttonMid) }
        : { light: lerp(base, white, 0.28), dark: lerp(base, black, 0.48), mid: lerp(base, black, 0.12),
            buttonEdge: lerp(base, black, 0.40), buttonMid: lerp(base, white, 0.18) };
      // the band's tone: measured, or about 40% of the swatch's light (FrameFinish.Band)
      const band = f.band ? hex(f.band) : base.map((v) => Math.round(srgb(lin(v) * 0.4) * 255));
      finishes.push({ id: f.id, name: f.name, group: g.group, base, band, ...plain });
    }
  }

  const ready = () => !!table;
  /** The frame of a model identifier ("iPhone17,1"), or the default (a current iPhone). */
  const modelFor = (id) => (id && models.get(id)) || model(table.default);
  const finishFor = (id) => finishes.find((f) => f.id === id) || finishes.find((f) => f.id === table.defaultFinish) || finishes[0];
  /** What a finish group's band is made of (that model's), if any model has it. */
  function groupMaterial(group) { for (const m of models.values()) if (m.finish === group) return m.material; return null; }
  const frameUnits = () => table.frame;  // { band, knob }

  // ---------------------------------------------------------------- the lit band (MainWindow.Frame.cs)
  /** The band and its side buttons, w x h pixels, straight-alpha RGBA; k (the buttons' room), t (the
   *  band's width), r (its outer corner) and the buttons' [x, y, w, h] in pixels. band: the finish's
   *  band tone [r, g, b]; material: Titanium, Aluminium, Steel or PolishedTitanium. */
  function bandPixels(w, h, k, t, r, band, material, buttons) {
    // how the metal takes the light: highlight sharpness and strength, brushed lines, highlight tint
    const [shine, specAmount, brushed, tinted] = {
      Steel: [160, 0.9, 0, 0.85], PolishedTitanium: [160, 0.9, 0.012, 0.85], Aluminium: [14, 0.25, 0, 0.6],
    }[material] || [40, 0.5, 0.07, 0.8];
    // premultiplied, then straightened at the end
    const acc = new Float32Array(w * h * 4);
    const bx = w / 2 - k, by = h / 2 - k;      // the band's outer half size
    const ix = bx - t, iy = by - t;            // its inner one
    const ro = r, ri = Math.max(0, r - t), tw = Math.max(1, t);
    const riExtent = ri * EXTENT;
    const UNDER = 2;  // pixels the band reaches in under the black border

    const linv = (c) => c.map((v) => lin(v));
    const tone = linv(band);
    const toneMax = Math.max(...tone);
    const specColor = tone.map((v) => toneMax + (v - toneMax) * tinted);
    const steelTint = tone.map((v) => 1 + (v / Math.max(toneMax, 0.001) - 1) * 0.55);
    const plastic = tone.map((v) => srgb(v * 0.35));

    // light from the top left and in front (y down)
    const norm = (v) => { const l = Math.hypot(...v); return v.map((x) => x / l); };
    const L = norm([-0.45, -0.6, 0.66]);
    const H = norm([L[0], L[1], L[2] + 1]);
    const flatLit = 0.2 + 0.8 * L[2], flatSpec = H[2] ** shine;

    const smooth = (lo, hi, v) => { const x = Math.min(1, Math.max(0, (v - lo) / (hi - lo))); return x * x * (3 - 2 * x); };
    function shadeSteel(n, rim) {
      // reflect (0, 0, -1) about n
      const rx = 2 * n[2] * n[0], ry = 2 * n[2] * n[1], rz = -1 + 2 * n[2] * n[2];
      const across = -0.75 * rx - 0.66 * ry;
      const front = smooth(-0.12, 0.42, rz);
      const softbox = smooth(0.015, 0.18, across) * (1 - smooth(0.55, 0.82, across));
      const strip = smooth(0.10, 0.24, across) * (1 - smooth(0.38, 0.61, across));
      const fill = smooth(0.07, 0.28, -across) * (1 - smooth(0.57, 0.80, -across));
      const fres = (1 - n[2]) ** 5;
      const illum = 0.22 + front * (0.72 + 0.44 * softbox + 0.18 * strip + 0.30 * fill);
      const glint = front * (0.27 * strip + 0.10 * fill);
      const c = tone.map((v, i) => ((v + (1 - v) * fres) * illum + steelTint[i] * glint) * rim);
      // roll bright reflections into SDR together so no channel clips alone
      const shoulder = 0.82, peak = Math.max(...c);
      if (peak <= shoulder) return c;
      const range = 1 - shoulder, mapped = 1 - range / (1 + (peak - shoulder) / range);
      return c.map((v) => v * mapped / peak);
    }
    // returns sRGB; diff (the light's strength here) in out[0]
    function shade(n, out, rim = 1) {
      const diff = Math.max(0, n[0] * L[0] + n[1] * L[1] + n[2] * L[2]);
      if (out) out[0] = diff;
      if (material === "Steel") return shadeSteel(n, rim).map(srgb);
      const lit = (0.2 + 0.8 * diff) / flatLit;
      const room = -0.1 * n[1];  // facing up: a little of the room's light; down: its shade
      const spec = Math.max(0, Math.max(0, n[0] * H[0] + n[1] * H[1] + n[2] * H[2]) ** shine - flatSpec);
      const k2 = Math.max(0, lit + room) * rim;
      return tone.map((v, i) => srgb(v * k2 + specColor[i] * specAmount * spec));
    }
    const clamp01 = (v) => Math.min(1, Math.max(0, v));

    // the side buttons first, under the band: each a rounded bar, curving across its width
    for (const [x0f, y0f, bw, bh] of buttons) {
      const upright = bw <= bh;
      const hx = bw / 2, hy = bh / 2, cx = x0f + hx, cy = y0f + hy;
      const corner = k * 0.9, across = upright ? hx : hy;
      const x0 = Math.max(0, Math.floor(x0f)), x1 = Math.min(w - 1, Math.ceil(x0f + bw));
      const y0 = Math.max(0, Math.floor(y0f)), y1 = Math.min(h - 1, Math.ceil(y0f + bh));
      for (let y = y0; y <= y1; y++) for (let x = x0; x <= x1; x++) {
        const px = x + 0.5 - cx, py = y + 0.5 - cy;
        const cover = clamp01(0.5 - sd(px, py, hx, hy, corner));
        if (cover <= 0) continue;
        const s = Math.min(1, Math.max(-1, (upright ? px : py) / Math.max(across, 0.5)));
        const tilt = s * 1.1;
        const n = upright ? [Math.sin(tilt), 0, Math.cos(tilt)] : [0, Math.sin(tilt), Math.cos(tilt)];
        let rim = 0.3 + 0.7 * n[2];
        const toBand = sd(x + 0.5 - w / 2, y + 0.5 - h / 2, bx, by, ro);
        if (toBand < 1.6) rim *= 0.45 + 0.55 * clamp01(toBand / 1.6);
        const col = shade(n, null, rim);
        const i = (y * w + x) * 4;
        acc[i] = clamp01(col[0]) * cover; acc[i + 1] = clamp01(col[1]) * cover; acc[i + 2] = clamp01(col[2]) * cover;
        acc[i + 3] = cover;
      }
    }

    // antenna lines: across the long sides, just past where each corner's curve ends
    const tall = h >= w;
    const longHalf = tall ? by : bx, shortHalf = tall ? bx : by;
    const antennaAt = longHalf - ro * EXTENT * 1.05, antennaHalf = Math.max(0.6, tw * 0.2);
    const hash = (n) => { const s = Math.sin(n * 12.9898) * 43758.547; return s - Math.floor(s); };
    const diffOut = [0];
    const cx = w / 2, cy = h / 2;
    for (let y = 0; y < h; y++) {
      const py = y + 0.5 - cy, ay = Math.abs(py);
      for (let x = 0; x < w; x++) {
        const px = x + 0.5 - cx, ax = Math.abs(px);
        // quick skip: well inside the band's inner edge, or outside its outer one
        if ((ax < ix - riExtent - 1 - UNDER && ay < iy - 1 - UNDER) || (ay < iy - riExtent - 1 - UNDER && ax < ix - 1 - UNDER)) continue;
        if (ax > bx + 1 || ay > by + 1) continue;
        const d = sd(px, py, bx, by, ro);
        const din = sd(px, py, ix, iy, ri);
        const cover = clamp01(0.5 - d) * clamp01(0.5 + din + UNDER);
        if (cover <= 0) continue;

        // the outward direction here, and how far across the band (0 outer edge, 1 inner)
        let gx = sd(px + 1, py, bx, by, ro) - sd(px - 1, py, bx, by, ro);
        let gy = sd(px, py + 1, bx, by, ro) - sd(px, py - 1, bx, by, ro);
        const gl = Math.hypot(gx, gy);
        if (gl > 1e-4) { gx /= gl; gy /= gl; } else { gx = 0; gy = 0; }
        const v = clamp01(-d / tw);
        // a flat top that rolls off at both edges
        const acr = 1 - 2 * v;
        const tilt = Math.sign(acr) * Math.abs(acr) ** 2.2 * 1.2;
        const st = Math.sin(tilt);
        let col = shade([gx * st, gy * st, Math.cos(tilt)], diffOut);
        const diff = diffOut[0];

        // brushed metal: fine lines along the band, faint and slowly varying around it
        const along = Math.atan2(py, px);
        const streak = (hash(Math.floor(-d * 1.4)) - 0.5) * brushed + 0.3 * brushed * Math.sin(along * 9 + v * 3);
        col = col.map((c) => c * (1 + streak));

        // the antenna lines: matte, a darker shade of the finish
        const alongLong = tall ? ay : ax, acrossShort = tall ? ax : ay;
        if (acrossShort > shortHalf - tw - 1) {
          const mm = clamp01(antennaHalf + 0.5 - Math.abs(alongLong - antennaAt));
          if (mm > 0) col = col.map((c, j) => c + (plastic[j] * (0.7 + 0.4 * diff) - c) * mm * 0.9);
        }

        // over whatever is under it (a button's tucked-in end)
        const i = (y * w + x) * 4, keep = 1 - cover;
        acc[i] = clamp01(col[0]) * cover + acc[i] * keep;
        acc[i + 1] = clamp01(col[1]) * cover + acc[i + 1] * keep;
        acc[i + 2] = clamp01(col[2]) * cover + acc[i + 2] * keep;
        acc[i + 3] = cover + acc[i + 3] * keep;
      }
    }

    const px = new Uint8ClampedArray(w * h * 4);
    for (let i = 0; i < px.length; i += 4) {
      const a = acc[i + 3];
      if (a <= 0) continue;
      px[i] = acc[i] / a * 255 + 0.5; px[i + 1] = acc[i + 1] / a * 255 + 0.5; px[i + 2] = acc[i + 2] / a * 255 + 0.5;
      px[i + 3] = a * 255 + 0.5;
    }
    return px;
  }

  return { EXTENT, rect, sd, limit, load, ready, modelFor, finishFor, groupMaterial, frameUnits, bandPixels,
           finishes: () => finishes };
})();

// In a Worker: draw bands on request ({ id, w, h, k, t, r, band, material, buttons }).
if (typeof window === "undefined" && typeof self !== "undefined") {
  self.onmessage = (e) => {
    const m = e.data;
    try {
      const px = Frame.bandPixels(m.w, m.h, m.k, m.t, m.r, m.band, m.material, m.buttons);
      self.postMessage({ id: m.id, w: m.w, h: m.h, px }, [px.buffer]);
    } catch (err) {
      self.postMessage({ id: m.id, error: String(err) });
    }
  };
}
