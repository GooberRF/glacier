-- Glacier design kit: organic rock that FOLLOWS a plan (installed into the kit by kit.lua; use it as
-- local kit = ged.import("kit")).
--
-- The generative organic builders (kit.canyon, kit.cave, kit.rock_wall, kit.boulders) grow their own footprint
-- along a path; a recreation has a traced footprint instead. This module turns a traced outline into rock without
-- moving it:
--   * wall skins: eroded rock built at three scales against a wall line: tiers that lean in or out 10 to 30
--     degrees, buttresses and alcoves along the line, ledges carrying scree where a tier starts behind the one
--     below; rows (strata) inside each tier; faceted convex DETAIL cells of one shared lattice (watertight: no
--     grooves show the traced wall). At the floor nothing reaches past the skin's base protrusion (0.35 m by
--     default), so the walkable floor keeps the traced outline; bulges start above head height, so overhangs are
--     ones you can stand under; a cave's top tier is a haunch that runs into the ceiling (the vault);
--   * a cave's vault: one continuous irregular underside hanging from the ceiling, with stalactite-like drops;
--   * rock masses: a talus apron, rounded strata stepping in with ledges, a wandering rim (kit.rock_mass);
--   * resting boulders and gentle ground mounds.
-- Rock is one material: every face takes the rock's wall texture (ground mounds keep the floor's).
-- Everything is detail (it never seals or splits rooms), so the traced air keeps sealing the level exactly as
-- kit.trace built it; the chunks only cover its flat walls. kit.trace's canyon / cave kinds, rock_edges, walls =
-- "rock", organic paths, ground, boulders and rock_detail options use these (I.rock), as kit.rock_mass does.
--
-- Random numbers come from a seeded Park-Miller stream (exact in doubles); the kit never reads rng.

local function install(kit, I)
local fail, check_keys, number, positive, boolean = I.fail, I.check_keys, I.number, I.positive, I.boolean
local name_for, group_if_many, fmt = I.name_for, I.group_if_many, I.fmt

local R = {}
I.rock = R

-- A seeded stream: next() in [0, 1), range(a, b).
function R.stream(seed)
  local s = math.floor(math.abs(seed or 1)) % 2147483646 + 1
  local st = {}
  function st.next()
    s = (s * 16807) % 2147483647
    return (s - 1) / 2147483646
  end
  function st.range(a, b) return a + (b - a) * st.next() end
  return st
end

local function clamp(v, a, b) if v < a then return a elseif v > b then return b end return v end

-- Rock textures: explicit texture/textures, else kit.defaults.rock_texture(s), else the stock canyon rock.
-- Rock is one material: the faces under an overhang take the wall texture too (not a built ceiling's).
function R.textures(texture, textures)
  if texture ~= nil then return texture, nil end
  if textures == nil then
    if kit.defaults.rock_texture ~= nil then return kit.defaults.rock_texture, nil end
    textures = kit.defaults.rock_textures or { floor = "rck_cavernflr01.tga", wall = "rck_canyon_rock03.tga" }
  end
  return nil, { floor = textures.floor or textures.wall, wall = textures.wall, ceiling = textures.wall or textures.ceiling }
end

-- Emits hulls (point lists) as detail brushes in batches of at most 1500; returns the brushes in order.
-- o = {name, fn, part, texture, textures, air, detail (default true), one_material}. Rock UVs follow the kit's rock
-- rule: box-mapped at the kit's pixels per metre (each texture's own size), else at the rock tile.
-- Rock is one material: unless one_material is false (or the part is a ground mound, which blends into the floor),
-- every face takes the wall texture. Upward ledges, scree and strata tops would otherwise take the floor role's
-- texture, and thin up-facing slivers of a grey floor texture read as slashes through the rock.
function R.emit(hulls, o)
  local out = {}
  if #hulls == 0 then return out end
  local tile = kit.defaults.rock_tile_metres or kit.defaults.tile_metres
  local textures = o.textures
  local part = o.part or "rock"
  if textures ~= nil and o.one_material ~= false and not part:find("mound$") and textures.wall ~= nil then
    textures = { floor = textures.wall, wall = textures.wall, ceiling = textures.wall }
  end
  local i = 1
  while i <= #hulls do
    local batch = {}
    for k = i, math.min(#hulls, i + 1499) do batch[#batch + 1] = hulls[k] end
    local spec = { hulls = batch, tile = tile, snap = 1 / 256,
      tags = { kit = o.fn or "rock", ["kit.name"] = o.name, ["kit.part"] = part } }
    if o.air then spec.air = true elseif o.detail ~= false then spec.detail = true end
    if o.texture ~= nil then spec.texture = o.texture elseif textures ~= nil then spec.textures = textures end
    local q = level.brushes_from_points(spec)
    if kit.defaults.box_map and kit.defaults.ppm and kit.defaults.rock_tile_metres == nil then q:box_map{ ppm = kit.defaults.ppm } end
    for _, b in ipairs(q.brushes) do out[#out + 1] = b end
    i = i + 1500
  end
  return out
end

-- ---------------------------------------------------------------------------------------------
-- The organic rock budget. A level holds at most 32,767 lightmap surfaces (Alpine RED's limit); past it faces draw
-- unlit. Every rock facet a player can see is one surface (no two facets share a plane), so the organic builders
-- spend from one level-wide budget, kit.defaults.organic_budget (seen facets; nil or false turns it off):
--   * the playable view band (the foot zone plus kit.defaults.organic_view metres above the floor) keeps full facets;
--   * rock above it is coarser (kit.defaults.organic_far times the cells' size, half as much again from 8 m higher):
--     far and high faces are seen from afar;
--   * a call whose rock would not fit its share of what is left (0.6 of it) coarsens its view band (up to 1.6x) and
--     its far rock (up to 2x more).
-- What is left is read from the level itself (the faces of its organic detail brushes, a little under half of which are
-- seen; the others lie inside neighbouring chunks or the wall, and the build gives those no surface when the level
-- would pass the cap), so the budget holds across runs and scripts.
-- ---------------------------------------------------------------------------------------------
local ORGANIC_KITS = { "trace_rock", "rock_mass", "rock_wall", "boulders", "rock_detail", "cave", "canyon", "passage", "pit" }
local SEEN_SHARE = 0.45
R.ORGANIC_KITS = ORGANIC_KITS

-- The seen facets left in the level's budget, and the budget (nil when it is off).
function R.budget_left()
  local budget = kit.defaults.organic_budget
  if type(budget) ~= "number" or budget <= 0 then return math.huge, nil end
  local used = ged.import("kit_core"):organic_faces(ORGANIC_KITS).faces * SEEN_SHARE
  return math.max(0, budget - used), budget
end

-- The extra coarsening of a call's rock: band and far are its seen-facet estimates at the static rule. Both 1 when they
-- fit 0.6 of what is left. Otherwise the view band takes at most 60 % of that share (its cells grow up to 1.6x) and the
-- far rock fits what remains (its cells grow up to 2x more). Returns band_scale, far_scale.
function R.budget_scales(band, far)
  local left, budget = R.budget_left()
  if budget == nil then return 1, 1 end
  local allow = 0.6 * left
  if band + far <= allow then return 1, 1 end
  local bs = 1
  if band > 0.6 * allow then bs = math.min(1.6, math.sqrt(band / math.max(0.6 * allow, 1))) end
  local room = allow - band / (bs * bs)
  local fs = 1
  if far > 0 and far > room then fs = math.min(2, math.sqrt(far / math.max(room, far / 4))) end
  return bs, fs
end

-- The coarsening of the cells starting h metres above the floor: 1 in the view band (up to `view`), `far` above it,
-- and 1.5 x far from 8 m higher.
function R.lod_at(h, view, far)
  if far <= 1 or h < view then return 1 end
  if h < view + 8 then return far end
  return far * 1.5
end

-- Strata: band boundaries from y0 to y1, each about `band` metres (0.7 to 1.3 of it), a forced boundary at `at`.
local function strata(st, y0, y1, band, at)
  local cuts = { y0 }
  local y = y0
  while true do
    local h = band * st.range(0.7, 1.3)
    if at ~= nil and y < at - 0.4 and y + h > at - 0.4 then h = at - y end
    if y + h >= y1 - 0.5 * band then break end
    y = y + h
    cuts[#cuts + 1] = y
  end
  cuts[#cuts + 1] = y1
  local bands = {}
  for k = 1, #cuts - 1 do bands[k] = { cuts[k], cuts[k + 1] } end
  return bands
end

-- Smooth 1-D value noise over [x0, x1]: knots every `step` metres (values -1..1 from the stream), cosine-blended.
local function noise1(st, x0, x1, step)
  local n = math.max(1, math.ceil((x1 - x0) / step)) + 1
  local knots = {}
  for i = 0, n do knots[i] = st.range(-1, 1) end
  return function(x)
    local t = (x - x0) / step
    local i = math.floor(t)
    if i < 0 then return knots[0] end
    if i >= n then return knots[n] end
    local f = t - i
    local w = (1 - math.cos(f * math.pi)) / 2
    return knots[i] * (1 - w) + knots[i + 1] * w
  end
end
R.noise1 = noise1

local function smoothstep(e0, e1, x)
  local t = clamp((x - e0) / (e1 - e0), 0, 1)
  return t * t * (3 - 2 * t)
end

-- The skin of one wall line a -> b (plan {x, z}), n the unit normal INTO the space. o:
--   y0, y1        the floor and the top of the skin;
--   st            the stream; band (row height, the mid-scale strata), size (chunk length);
--   base = {lo, hi} protrusion of the foot (hi is the floor footprint's tolerance: nothing at the floor reaches
--                 further into the space);
--   lean          extra protrusion per metre of height (negative leans the wall back, down to the setback);
--   overhang      extra protrusion at the top (the rim leans out over the space);
--   cap           true: the top runs 0.3 m into the ceiling as a haunch (a cave's vault springs from it);
--   setback       {y, d}: the wall line steps d metres outward from height y (air carved there), so the rock
--                 above may recede behind the traced line;
--   reach         the largest bulge into the space above head height (default 0.2 x height, 0.5 to 3 m);
--   talus         how far scree at the foot may reach onto the floor (default base hi: inside the tolerance).
-- The form is built at three scales, as eroded rock is:
--   large: the height splits into tiers (3 to 7 m) above a foot zone (to head height); each tier leans in or out
--          (10 to 30 degrees where its reach allows), buttresses / spurs bulge out every 8 to 20 m with recesses
--          (alcoves) between, and a tier that starts behind the one below leaves a ledge carrying scree;
--   mid:   rows (strata) inside each tier, their boundaries wavering;
--   small: every lattice point jittered, so each chunk's hull is faceted.
-- Chunks are cells of ONE lattice of front points shared between neighbours (no grooves between chunks, so the
-- traced wall behind never shows), each reaching back behind the wall line. Bulges start above the foot zone, so
-- the walkable floor keeps the traced outline and overhangs are ones you can stand under.
-- Returns the hulls and the largest protrusion at the floor.
function R.wall_skin(a, b, n, o)
  local st = o.st
  local L = math.sqrt((b[1] - a[1]) ^ 2 + (b[2] - a[2]) ^ 2)
  local hulls = {}
  if L < 0.5 then return hulls, 0 end
  local u = { (b[1] - a[1]) / L, (b[2] - a[2]) / L }
  local y0, y1 = o.y0, o.y1
  local H = y1 - y0
  if H < 0.5 then return hulls, 0 end
  local base = o.base or { 0.05, 0.35 }
  local lean, overhang = o.lean or 0, o.overhang or 0
  local sb = o.setback
  local cap = o.cap
  local reach = o.reach or clamp(0.2 * H, 0.5, 3.0)
  local rowh = o.band or clamp(H / 6, 0.9, 2.2)
  local size = o.size or clamp(H / 3.5, 1.8, 4.2)
  local talus_reach = math.min(o.talus or base[2], base[2] + 2)
  local function P(s, d, y) return { a[1] + u[1] * s + n[1] * d, y, a[2] + u[2] * s + n[2] * d } end

  -- columns of one tier: cell boundaries along the line, each cell split by a centre column (fine columns:
  -- 2k-1 boundaries, 2k centres). Every tier has its own, so vertical joints do not line up into a grid.
  local sA, sB = -0.2, L + 0.2
  local function make_cols(c)
    local cols = { sA }
    local s = sA
    local sz = size * (c or 1)
    while true do
      local w = sz * st.range(0.6, 1.4)
      if s + w >= sB - 0.45 * sz then break end
      s = s + w
      cols[#cols + 1] = s
    end
    cols[#cols + 1] = sB
    local fine = {}
    for k = 1, #cols do
      fine[2 * k - 1] = cols[k]
      if k < #cols then fine[2 * k] = (cols[k] + cols[k + 1]) / 2 end
    end
    return fine
  end

  -- tiers: the foot zone, then tiers to the top (a forced boundary at the setback)
  local footH = math.min(2.4, math.max(0.9, 0.45 * H))
  local yF = math.min(y1, y0 + footH)
  local cuts = { y0, yF }
  if y1 - yF > 0.6 then
    local th = clamp((y1 - yF) / 3, 2.5, 6.5)
    local y = yF
    while true do
      local h = th * st.range(0.75, 1.25)
      if sb ~= nil and y < sb.y - 0.5 and y + h > sb.y - 0.5 then h = sb.y - y end
      if y + h >= y1 - 0.45 * th then break end
      y = y + h
      cuts[#cuts + 1] = y
    end
    cuts[#cuts + 1] = y1
  else
    cuts[2] = y1
  end
  local ntier = #cuts - 1

  -- large-scale form along the line: spurs (buttresses) and alcoves, and the slow wobble of each tier
  local spur = {}
  do
    local c = st.range(2, 8)
    while c < L + 4 do
      local w = st.range(2.5, 6)
      local amp = st.next() < 0.75 and st.range(0.6, 1.1) or -st.range(0.4, 0.8)
      spur[#spur + 1] = { c, w, amp }
      c = c + st.range(8, 20)
    end
  end
  local function spur_at(s)
    local v = 0
    for _, q in ipairs(spur) do
      local t = (s - q[1]) / q[2]
      if t > -0.5 and t < 0.5 then v = v + q[3] * math.cos(t * math.pi) ^ 2 end
    end
    return v
  end
  local edge_taper = function(s)
    -- near the ends of the span (a doorway or a corner) the bulges fade to the foot's reach
    local e = math.min(s, L - s)
    return smoothstep(0.3, 2.2, e)
  end

  -- each tier: its protrusion at its bottom and top (the lean), plus noise along the line
  local tiers = {}
  for k = 1, ntier do
    local t0, t1 = cuts[k], cuts[k + 1]
    local above = sb ~= nil and t0 >= sb.y - 1e-6
    local lo = above and (-sb.d + 0.08) or 0.05
    local hi = reach
    local top = k == ntier
    if top and not cap then hi = hi + 0.8 * overhang end
    if top and cap then hi = math.max(hi, math.max(overhang, 0.6) + 0.4) end
    local tr = { t0 = t0, t1 = t1, lo = lo, hi = hi, foot = k == 1 }
    if k == 1 then
      tr.v0, tr.v1 = base[1] + 0.3 * (base[2] - base[1]), base[1] + 0.5 * (base[2] - base[1])
    else
      local th = t1 - t0
      local span = hi - lo
      local want = math.min(span * 0.85, th * math.tan(math.rad(st.range(12, 30))))
      -- the tier's own lean stays in the lower part of its reach: the spurs carry the rest
      local v0 = st.range(lo, math.max(lo, lo + 0.6 * span - want))
      local v1 = v0 + want
      if st.next() < 0.5 then v0, v1 = v1, v0 end -- leans back (in) or out over the space
      if top and overhang > 0 and not cap then v1 = math.max(v1, lo + 0.6 * span) end
      if top and cap then v1 = hi end
      tr.v0, tr.v1 = v0, v1
      tr.spur = st.range(0.6, 1.0)
    end
    tr.wob = noise1(st, sA - 1, sB + 1, st.range(5, 9))
    tr.wob2 = noise1(st, sA - 1, sB + 1, st.range(2, 3.5))
    tiers[k] = tr
  end

  -- the least protrusion of tier k at s: behind the traced line only inside the set-back air (which spans [0, L]),
  -- so the set-back's end faces stay covered
  local function lo_at(k, s)
    local lo = tiers[k].lo
    if lo >= 0.05 then return lo end
    local e = math.min(s, L - s)
    return lo + (0.05 - lo) * (1 - smoothstep(0.2, 1.2, e))
  end

  -- the protrusion field D(s, y) of tier k (unclamped noise excluded)
  local function field(k, s, y)
    local tr = tiers[k]
    local f = (y - tr.t0) / math.max(1e-6, tr.t1 - tr.t0)
    local d = tr.v0 + (tr.v1 - tr.v0) * f + lean * (y - y0)
    if tr.foot then
      -- the foot: inside the footprint tolerance, rising a little toward head height (an undercut look)
      d = d + 0.06 * tr.wob(s)
      return clamp(d, base[1], base[2])
    end
    local span = tr.hi - tr.lo
    d = d + span * (0.28 * tr.wob(s) + 0.12 * tr.wob2(s))
    d = d + reach * tr.spur * spur_at(s)
    -- bulges fade in above the foot zone and out at the span's ends
    local hiE = tr.lo + (tr.hi - tr.lo) * edge_taper(s)
    hiE = math.max(hiE, math.min(tr.hi, base[2] + 0.3))
    return clamp(d, lo_at(k, s), hiE)
  end

  -- tier boundaries waver smoothly along the line (one function per boundary, so the rows either side meet)
  local bound = {}
  for k = 1, ntier + 1 do
    local amp = (k == 1 or k == ntier + 1) and 0 or 0.28
    local nz = noise1(st, sA - 1, sB + 1, st.range(3, 6))
    bound[k] = function(s) return amp * nz(s) end
  end
  -- the lattice of each tier: F[r][c] = {s, y, d}
  local foot = 0
  local prev = nil -- the lower tier's top row: {s, d} by fine column, sorted by s
  local function prev_at(s)
    if s <= prev[1][1] then return prev[1][2] end
    for i = 2, #prev do
      if s <= prev[i][1] then
        local p, q = prev[i - 1], prev[i]
        local f = (s - p[1]) / math.max(1e-9, q[1] - p[1])
        return p[2] + (q[2] - p[2]) * f
      end
    end
    return prev[#prev][2]
  end
  -- the organic budget's level of detail: tiers starting above the view band (the foot zone plus lod.view metres)
  -- are cut into coarser cells (R.lod_at)
  local lod = o.lod
  local view_h = lod and (footH + (lod.view or 5)) or math.huge
  for k = 1, ntier do
    local tr = tiers[k]
    local th = tr.t1 - tr.t0
    local cf = (k > 1 and lod) and math.max(lod.band or 1, R.lod_at(tr.t0 - y0, view_h, lod.far or 1)) or 1
    local fine = make_cols(cf)
    local nf = #fine
    local rowc = rowh * cf
    local nr = math.max(1, math.floor(th / rowc + 0.5))
    local fr = 2 * nr + 1
    local F = {}
    local r_jit = tr.foot and 0.08 or (0.1 + 0.06 * math.min(rowc, 2))
    local back = (sb ~= nil and tr.t0 >= sb.y - 1e-6) and (sb.d + 0.6) or 0.6
    for r = 1, fr do
      F[r] = {}
      local yr = tr.t0 + th * (r - 1) / (fr - 1)
      for c = 1, nf do
        local s, y = fine[c], yr
        local even_c, even_r = (c % 2) == 1, (r % 2) == 1
        if not even_c then s = s + st.range(-0.25, 0.25) * (fine[c + 1] - fine[c - 1]) / 2
        elseif c > 1 and c < nf then s = s + st.range(-0.12, 0.12) end
        if r == 1 then y = tr.t0 + bound[k](s)
        elseif r == fr then y = tr.t1 + bound[k + 1](s)
        elseif even_r then y = y + st.range(-0.15, 0.15) * rowc
        else y = y + st.range(-0.25, 0.25) * rowc end
        if k == 1 and r == 1 then y = y0 - 0.25 end
        if cap and k == ntier and r == fr then y = y1 + 0.3 end
        local d = field(k, s, y)
        d = d + st.range(-r_jit, r_jit)
        if not even_c or not even_r then d = d + st.range(0, r_jit) end -- centres bulge: faceted chunks
        if tr.foot then d = math.min(d, base[2]) end
        if prev ~= nil then
          -- an overhang's underside slopes (no flatter than about 40 degrees) from the tier below
          local pd = prev_at(s)
          d = math.min(d, pd + math.max(0, y - (tr.t0 + bound[k](s))) * 1.2)
          -- a tier that bulges further starts flush with the one below (no slot); one that starts further
          -- back leaves a ledge
          if r == 1 then
            d = math.min(d, pd)
            y = y - 0.15
          end
        end
        d = math.max(d, lo_at(k, s))
        F[r][c] = { s, y, d }
        if k == 1 and y <= y0 + 0.5 then foot = math.max(foot, d) end
      end
    end
    -- cells: 3 x 3 front points each plus the back corners (a coarsened far tier keeps the 4 corners and the bulging
    -- centre: 4 front facets instead of 8, the edges between neighbours straight on both sides)
    local five = cf > 1 and lod ~= nil and R.lod_at(tr.t0 - y0, view_h, lod.far or 1) > 1
    for r = 1, fr - 2, 2 do
      for c = 1, nf - 2, 2 do
        local pts = {}
        local smin, smax, ymin, ymax = math.huge, -math.huge, math.huge, -math.huge
        for rr = r, r + 2 do
          for cc = c, c + 2 do
            local q = F[rr][cc]
            if five and (rr == r + 1) ~= (cc == c + 1) then q = nil end
            if q then
            pts[#pts + 1] = P(q[1], q[3], q[2])
            smin, smax = math.min(smin, q[1]), math.max(smax, q[1])
            ymin, ymax = math.min(ymin, q[2]), math.max(ymax, q[2])
            end
          end
        end
        pts[#pts + 1] = P(smin - 0.05, -back, ymin)
        pts[#pts + 1] = P(smax + 0.05, -back, ymin)
        pts[#pts + 1] = P(smin - 0.05, -back, ymax)
        pts[#pts + 1] = P(smax + 0.05, -back, ymax)
        hulls[#hulls + 1] = pts
      end
    end
    -- scree on the ledge this tier leaves at its bottom (where it starts well behind the tier below)
    if prev ~= nil then
      for c = 2, nf - 1, 2 do
        local s0 = F[1][c][1]
        local dl, du = prev_at(s0), F[1][c][3]
        if dl - du > 0.55 and st.next() < 0.7 then
          local yl = F[1][c][2] + 0.15
          local sc = s0 + st.range(-0.3, 0.3)
          local w = math.min(fine[c + 1] - fine[c - 1], st.range(0.9, 2.2))
          local h = math.min(0.9, (dl - du) * st.range(0.4, 0.75))
          local toe = du + (dl - du) * st.range(0.55, 0.85)
          local toe2 = du + (toe - du) * st.range(0.85, 1.0)
          hulls[#hulls + 1] = {
            P(sc - w / 2, du - 0.3, yl - 0.12), P(sc + w / 2, du - 0.3, yl - 0.12),
            P(sc - w * 0.45, toe, yl - 0.12), P(sc + w * 0.4, toe2, yl - 0.12),
            P(sc - w * 0.3, du - 0.1, yl + h), P(sc + w * 0.25, du - 0.1, yl + h * st.range(0.6, 0.9)),
            P(sc + st.range(-0.2, 0.2) * w, du + (toe - du) * 0.45, yl + h * st.range(0.45, 0.7)),
          }
        end
      end
    end
    prev = {}
    for c = 1, nf do prev[c] = { F[fr][c][1], F[fr][c][3] } end
  end
  -- scree at the foot: low sloped heaps against the wall, their toe inside the talus reach
  local s = st.range(0.8, 3)
  while s < L - 1.0 do
    local w = st.range(0.8, 2.0)
    if s + w / 2 < L - 0.8 then
      local h = st.range(0.35, 0.9)
      local toe = talus_reach * st.range(0.8, 1.0)
      hulls[#hulls + 1] = {
        P(s - w / 2, -0.3, y0 - 0.2), P(s + w / 2, -0.3, y0 - 0.2),
        P(s - w * 0.45, toe, y0 - 0.2), P(s + w * 0.4, toe * st.range(0.85, 1.0), y0 - 0.2),
        P(s - w * 0.4, toe * st.range(0.75, 0.95), y0 + 0.05), P(s + w * 0.35, toe * st.range(0.7, 0.9), y0 + 0.05),
        P(s - w * 0.25, -0.1, y0 + h), P(s + w * 0.2, -0.1, y0 + h * st.range(0.6, 0.9)),
        P(s + st.range(-0.15, 0.15) * w, toe * 0.45, y0 + h * st.range(0.4, 0.6)),
      }
      foot = math.max(foot, toe)
    end
    s = s + w + st.range(1.5, 5)
  end
  return hulls, foot
end

-- Deterministic lattice noise (exact in doubles): hash01(i, j, k) in [0, 1), and smooth 2-D value noise.
local function hash01(i, j, k)
  local h = (i * 73856093 + j * 19349663 + k * 83492791) % 2147483647
  h = (h * 16807) % 2147483647
  h = (h * 16807 + 12345) % 2147483647
  h = (h * 16807) % 2147483647
  return h / 2147483647
end
local function vnoise(x, z, scale, k)
  local fx, fz = x / scale, z / scale
  local i, j = math.floor(fx), math.floor(fz)
  local tx, tz = fx - i, fz - j
  tx, tz = (1 - math.cos(tx * math.pi)) / 2, (1 - math.cos(tz * math.pi)) / 2
  local a, b = hash01(i, j, k), hash01(i + 1, j, k)
  local c, d = hash01(i, j + 1, k), hash01(i + 1, j + 1, k)
  return (a * (1 - tx) + b * tx) * (1 - tz) + (c * (1 - tx) + d * tx) * tz
end
R.hash01, R.vnoise = hash01, vnoise

local function in_convex(piece, x, z)
  local sgn = 0
  for i = 1, #piece do
    local p, q = piece[i], piece[i % #piece + 1]
    local c = (q[1] - p[1]) * (z - p[2]) - (q[2] - p[2]) * (x - p[1])
    if c > 1e-9 then if sgn < 0 then return false end sgn = 1
    elseif c < -1e-9 then if sgn > 0 then return false end sgn = -1 end
  end
  return true
end

-- A cave's vault over a convex plan piece: ONE continuous irregular underside, not separate lumps. A lattice
-- (anchored in world space, so neighbouring pieces of the same ceiling join seamlessly) of cells, each the hull of
-- the ceiling slab above it and 9 underside points that it SHARES with its neighbours, hanging 0.15 to 1.2 m below
-- the ceiling by a smooth noise field and fading to the ceiling near the walls (where the wall's haunch takes
-- over); some cells carry a stalactite-like drop. ok(x, z, bottom, rad) decides what may hang where (inside the
-- outline at least rad + 0.6 m from its walls, with head room under bottom): it is asked with a negative rad
-- (down to -0.55) to find points merely inside the outline. A cell is emitted by the piece holding its centre.
function R.ceiling_chunks(piece, yc, st, ok, spacing)
  local hulls = {}
  local sp = (spacing or 4) * 0.62
  local seed = math.floor(yc * 37 + 1000.5) % 100000
  local x0, z0, x1, z1 = math.huge, math.huge, -math.huge, -math.huge
  for _, q in ipairs(piece) do x0, z0, x1, z1 = math.min(x0, q[1]), math.min(z0, q[2]), math.max(x1, q[1]), math.max(z1, q[2]) end
  local org = 0.37
  local i0, i1 = math.floor((x0 - org) / sp) - 1, math.ceil((x1 - org) / sp) + 1
  local j0, j1 = math.floor((z0 - org) / sp) - 1, math.ceil((z1 - org) / sp) + 1
  -- underside point of fine lattice index (fi, fj) (half-cell steps), cached
  local cache = {}
  local function under(fi, fj)
    local key = fi * 100003 + fj
    local v = cache[key]
    if v then return v end
    local x = org + fi * sp / 2 + (hash01(fi, fj, seed + 1) - 0.5) * 0.3 * sp
    local z = org + fj * sp / 2 + (hash01(fi, fj, seed + 2) - 0.5) * 0.3 * sp
    local drop = 0.15 + 1.05 * (0.6 * vnoise(x, z, 5.3, seed) + 0.4 * vnoise(x, z, 2.2, seed + 7))
    drop = drop + (hash01(fi, fj, seed + 3) - 0.5) * 0.2
    -- distance to the walls (coarse): fade toward the ceiling near them
    local e = -1
    for _, r in ipairs({ 1.4, 0.6, 0, -0.55 }) do
      if ok(x, z, yc - 0.05, r) then e = r break end
    end
    local inside = e > -1
    if not inside then drop = 0.05
    else
      drop = drop * clamp((e + 0.75) / 2.0, 0.12, 1)
      for _ = 1, 3 do
        if ok(x, z, yc - drop, -0.55) then break end
        drop = drop * 0.5
      end
    end
    v = { x, yc - drop, z, inside }
    cache[key] = v
    return v
  end
  for i = i0, i1 do
    for j = j0, j1 do
      local cx, cz = org + (i + 0.5) * sp, org + (j + 0.5) * sp
      if in_convex(piece, cx, cz) and ok(cx, cz, yc - 0.05, -0.55) then
        local pts = {}
        local any_drop = false
        for a = 0, 2 do
          for b = 0, 2 do
            local q = under(2 * i + a, 2 * j + b)
            local x, y, z = q[1], q[2], q[3]
            if not q[4] then
              -- outside the outline: pulled toward the cell centre so nothing pokes through a thin wall
              x, z = (x + cx) / 2, (z + cz) / 2
              if not ok(x, z, yc - 0.05, -0.55) then x, z = cx, cz end
              y = yc - 0.05
            end
            if a == 1 and b == 1 then y = y - hash01(i, j, seed + 5) * 0.25 end
            if y < yc - 0.1 then any_drop = true end
            pts[#pts + 1] = { x, y, z }
            pts[#pts + 1] = { x, yc + 0.3, z }
          end
        end
        if any_drop then hulls[#hulls + 1] = pts end
        -- a stalactite-like drop from some cells
        if hash01(i, j, seed + 9) < 0.16 and ok(cx, cz, yc - 0.05, 0.9) then
          local c = under(2 * i + 1, 2 * j + 1)
          local rb = 0.35 + 0.35 * hash01(i, j, seed + 10)
          local len = 0.7 + 1.3 * hash01(i, j, seed + 11)
          local tipy = c[2] - len
          for _ = 1, 3 do
            if ok(c[1], c[3], tipy, rb - 0.6) then break end
            len = len * 0.6
            tipy = c[2] - len
          end
          if len > 0.35 and ok(c[1], c[3], tipy, rb - 0.6) then
            local cone = {}
            local off = hash01(i, j, seed + 12) * math.pi
            for k = 0, 5 do
              local ang = off + k * math.pi / 3
              local rr = rb * (0.75 + 0.25 * hash01(i, j, seed + 20 + k))
              cone[#cone + 1] = { c[1] + math.cos(ang) * rr, c[2] + 0.35, c[3] + math.sin(ang) * rr }
              cone[#cone + 1] = { c[1] + math.cos(ang + 0.5) * rr * 0.45, c[2] - len * 0.55, c[3] + math.sin(ang + 0.5) * rr * 0.45 }
            end
            cone[#cone + 1] = { c[1] + (hash01(i, j, seed + 13) - 0.5) * 0.2, tipy, c[3] + (hash01(i, j, seed + 14) - 0.5) * 0.2 }
            hulls[#hulls + 1] = cone
          end
        end
      end
    end
  end
  return hulls
end

-- A boulder resting on the ground at (x, gy, z): a squashed dome of diameter size, its foot 0.05 m into the ground
-- (resting, not sunk), widest a quarter of the way up.
function R.boulder(x, gy, z, size, st)
  local sx, sz, sy = size * st.range(0.85, 1.15), size * st.range(0.75, 1.1), size * st.range(0.55, 0.85)
  local rot = st.range(0, math.pi)
  local cr, sr = math.cos(rot), math.sin(rot)
  local rings = { { -0.05, 0.8 }, { sy * 0.25, 1.0 }, { sy * 0.6, 0.82 }, { sy * 0.88, 0.45 } }
  local pts = {}
  for ri, ring in ipairs(rings) do
    local n = ri == #rings and 4 or 7
    local off = st.range(0, 2 * math.pi / n)
    for k = 1, n do
      local ang = off + (k - 1) * 2 * math.pi / n
      local rad = ring[2] * st.range(0.8, 1.0)
      local lx, lz = math.cos(ang) * sx / 2 * rad, math.sin(ang) * sz / 2 * rad
      pts[#pts + 1] = { x + lx * cr - lz * sr, gy + ring[1] + (ri > 1 and st.range(-0.06, 0.06) * sy or 0), z + lx * sr + lz * cr }
    end
  end
  pts[#pts + 1] = { x + st.range(-0.1, 0.1) * sx, gy + sy, z + st.range(-0.1, 0.1) * sz }
  return pts, math.max(sx, sz) / 2
end

-- A gentle ground mound at (x, gy, z): radius rad, height h (slopes well under 30 degrees).
function R.mound(x, gy, z, rad, h, st)
  local pts = {}
  local rings = { { -0.1, 1.0, 9 }, { 0.3 * h, 0.8, 8 }, { 0.75 * h, 0.45, 6 } }
  for _, ring in ipairs(rings) do
    local off = st.range(0, math.pi)
    for k = 0, ring[3] - 1 do
      local ang = off + k * 2 * math.pi / ring[3]
      local rr = rad * ring[2] * st.range(0.85, 1.0)
      pts[#pts + 1] = { x + math.cos(ang) * rr, gy + ring[1], z + math.sin(ang) * rr }
    end
  end
  pts[#pts + 1] = { x, gy + h, z }
  return pts
end

local function area2(p)
  local a = 0
  for i = 1, #p do local q, r = p[i], p[i % #p + 1] a = a + q[1] * r[2] - r[1] * q[2] end
  return a / 2
end

local function ccw(p)
  if area2(p) >= 0 then return p end
  local r = {}
  for i = #p, 1, -1 do r[#r + 1] = p[i] end
  return r
end

local function crosses(a, b, c, d)
  local function cr(p, q, r) return (q[1] - p[1]) * (r[2] - p[2]) - (q[2] - p[2]) * (r[1] - p[1]) end
  local d1, d2, d3, d4 = cr(a, b, c), cr(a, b, d), cr(c, d, a), cr(c, d, b)
  return ((d1 > 1e-9 and d2 < -1e-9) or (d1 < -1e-9 and d2 > 1e-9)) and ((d3 > 1e-9 and d4 < -1e-9) or (d3 < -1e-9 and d4 > 1e-9))
end

local function simple(p)
  for i = 1, #p do
    for j = i + 2, #p do
      if not (i == 1 and j == #p) and crosses(p[i], p[i % #p + 1], p[j], p[j % #p + 1]) then return false end
    end
  end
  return true
end

-- A CCW polygon moved inward by d (each corner along its bisector, mitre capped at 3 d); nil when it degenerates.
local function inset(p, d)
  if d <= 1e-6 then return p end
  local out = {}
  local n = #p
  for i = 1, n do
    local a, b, c = p[(i - 2) % n + 1], p[i], p[i % n + 1]
    local e1 = { b[1] - a[1], b[2] - a[2] }
    local e2 = { c[1] - b[1], c[2] - b[2] }
    local l1, l2 = math.sqrt(e1[1] ^ 2 + e1[2] ^ 2), math.sqrt(e2[1] ^ 2 + e2[2] ^ 2)
    if l1 < 1e-9 or l2 < 1e-9 then return nil end
    local n1 = { -e1[2] / l1, e1[1] / l1 } -- left normal = inward for CCW
    local n2 = { -e2[2] / l2, e2[1] / l2 }
    local m = { n1[1] + n2[1], n1[2] + n2[2] }
    local ml = math.sqrt(m[1] ^ 2 + m[2] ^ 2)
    if ml < 1e-6 then return nil end
    local c2 = (m[1] / ml) * n1[1] + (m[2] / ml) * n1[2]
    local k = math.min(d / math.max(c2, 1e-3), 3 * d)
    out[i] = { b[1] + m[1] / ml * k, b[2] + m[2] / ml * k }
  end
  if area2(out) < 1 or not simple(out) then return nil end
  return out
end

-- The polygon scaled toward its centroid (the fallback inset).
local function shrink(p, f)
  local cx, cz = 0, 0
  for _, q in ipairs(p) do cx, cz = cx + q[1], cz + q[2] end
  cx, cz = cx / #p, cz / #p
  local out = {}
  for i, q in ipairs(p) do out[i] = { cx + (q[1] - cx) * f, cz + (q[2] - cz) * f } end
  return out
end

-- Long edges split every `step` metres (so the outline can bulge between corners).
local function subdivide(p, step)
  local out = {}
  for i = 1, #p do
    local a, b = p[i], p[i % #p + 1]
    out[#out + 1] = a
    local L = math.sqrt((b[1] - a[1]) ^ 2 + (b[2] - a[2]) ^ 2)
    local k = math.floor(L / step)
    for j = 1, k do
      local f = j / (k + 1)
      out[#out + 1] = { a[1] + (b[1] - a[1]) * f, a[2] + (b[2] - a[2]) * f }
    end
  end
  return out
end

-- Corners pushed outward (along the corner's outward bisector) by 0..r each, keeping the outline simple.
local function roughen(p, r, st)
  local n = #p
  local out = {}
  for i = 1, n do
    local a, b, c = p[(i - 2) % n + 1], p[i], p[i % n + 1]
    local e1 = { b[1] - a[1], b[2] - a[2] }
    local e2 = { c[1] - b[1], c[2] - b[2] }
    local l1, l2 = math.max(1e-9, math.sqrt(e1[1] ^ 2 + e1[2] ^ 2)), math.max(1e-9, math.sqrt(e2[1] ^ 2 + e2[2] ^ 2))
    local m = { e1[2] / l1 + e2[2] / l2, -e1[1] / l1 - e2[1] / l2 } -- outward (right normals) for CCW
    local ml = math.sqrt(m[1] ^ 2 + m[2] ^ 2)
    local k = ml > 1e-6 and st.range(0, r) / ml * 1 or 0
    out[i] = { b[1] + m[1] * k, b[2] + m[2] * k }
  end
  if simple(out) then return out end
  return p
end

-- The hulls of a rock mass over a plan polygon (CCW or not) from y0 to y1, as eroded rock: a talus apron at the
-- foot (sloping about 34 degrees up from the outline), then rounded strata (each bulging at its middle, its top
-- shoulder rounded back, the next starting further in so a ledge shows), and a rim whose height wanders (the
-- skyline). The outline is subdivided and its corners pushed out a little; each ring of the profile is the outline
-- moved in along its corners' bisectors by a smoothly varying amount. The solid between two rings is cut into
-- sectors around the centre (shared edges, so nothing gaps). o: st, band (stratum height), step (how far each
-- stratum steps in, on average; 0 = a sheer column: no talus, no shoulders), rough (corner push-out), top_vary
-- (share of the top stratum's height the rim may lose), talus (apron height in metres; default 0.18 x height,
-- 0.5 to 3, when step > 0; 0 = none), convex_pieces (the kit's decomposition, for the fallback). An outline that is
-- not star-shaped about its centre falls back to stacked strata pieces. Returns the hulls and the strata count.
local function mass_strata(poly, y0, y1, o)
  local st = o.st
  local H = y1 - y0
  local band = o.band or clamp(H / 4, 1.2, 4)
  local step = o.step or 0.5
  local rough = o.rough or math.min(1.2, 0.25 + 0.06 * H)
  local vary = o.top_vary or 0.35
  local hulls = {}
  local base = ccw(poly)
  local bands = strata(st, y0, y1, band, nil)
  local d = 0
  local last = base
  for k, bd in ipairs(bands) do
    local ya, yb = bd[1], bd[2]
    if k > 1 then d = d + step * st.range(0.1, 1.9) end
    local p = inset(base, d)
    if p == nil then
      p = shrink(last, 0.85)
      if area2(p) < 0.5 then break end
    end
    if k > 1 and step > 0 then
      local dx, dz = st.range(-0.4, 0.4) * step, st.range(-0.4, 0.4) * step
      for i, q in ipairs(p) do p[i] = { q[1] + dx, q[2] + dz } end
    end
    last = p
    local rp = roughen(subdivide(p, 1.8), rough, st)
    local pieces = o.convex_pieces(rp) or o.convex_pieces(p) or { p }
    local yA = k == 1 and (y0 - 0.25) or (ya - 0.1)
    local top = k == #bands
    for _, piece in ipairs(pieces) do
      local cx, cz = 0, 0
      for _, q in ipairs(piece) do cx, cz = cx + q[1], cz + q[2] end
      cx, cz = cx / #piece, cz / #piece
      local yTop = yb
      if top then yTop = yb - (yb - ya) * st.range(0, vary) end
      local pts = {}
      for _, q in ipairs(piece) do
        local qx, qz = cx + (q[1] - cx) * 1.03, cz + (q[2] - cz) * 1.03
        pts[#pts + 1] = { qx, yA, qz }
        local pull = st.range(0.1, 0.45) * math.min(1, (yb - ya) / 2)
        local lx, lz = qx + (cx - qx) * pull * 0.5, qz + (cz - qz) * pull * 0.5
        pts[#pts + 1] = { lx, yTop - (yTop - ya) * st.range(0, 0.25), lz }
      end
      pts[#pts + 1] = { cx, yTop, cz }
      hulls[#hulls + 1] = pts
    end
  end
  return hulls, #bands
end

function R.mass_hulls(poly, y0, y1, o)
  local st = o.st
  local H = y1 - y0
  local band = o.band or clamp(H / 4, 1.2, 4)
  local step = o.step or 0.5
  local rough = o.rough or math.min(1.2, 0.25 + 0.06 * H)
  local vary = o.top_vary or 0.35
  local column = step <= 0
  local talus = o.talus
  if talus == nil or talus == true then talus = (not column and H >= 2.5) and clamp(0.18 * H, 0.5, 3) or 0 end
  if talus == false then talus = 0 end
  talus = math.min(talus, 0.4 * H)
  -- the outline subdivided; its roughness is a push outward along the smooth outline's bisectors (so moving the
  -- rings in never folds them), strongest at the foot
  local corners = ccw(poly)
  local ncor = #corners
  local ring0, vdesc = {}, {} -- vdesc[i] = {corner, fraction along its edge}
  for e = 1, ncor do
    local a, b = corners[e], corners[e % ncor + 1]
    local k = math.floor(math.sqrt((b[1] - a[1]) ^ 2 + (b[2] - a[2]) ^ 2) / (1.6 * (o.coarse or 1)))
    for jj = 0, k do
      local f = jj / (k + 1)
      ring0[#ring0 + 1] = { a[1] + (b[1] - a[1]) * f, a[2] + (b[2] - a[2]) * f }
      vdesc[#vdesc + 1] = { e, f }
    end
  end
  local nv = #ring0
  local push = {}
  for i = 1, nv do push[i] = st.range(0, rough) end
  local A = area2(ring0)
  -- the area centroid
  local cx, cz = 0, 0
  for i = 1, nv do
    local p, q = ring0[i], ring0[i % nv + 1]
    local c = p[1] * q[2] - q[1] * p[2]
    cx, cz = cx + (p[1] + q[1]) * c, cz + (p[2] + q[2]) * c
  end
  cx, cz = cx / (6 * A), cz / (6 * A)
  -- outward bisectors and the distance from the centre (how far a corner may move in)
  local nrm, reachv, arc, mit = {}, {}, {}, {}
  local acc = 0
  for i = 1, nv do
    local p0, p, p1 = ring0[(i - 2) % nv + 1], ring0[i], ring0[i % nv + 1]
    local e1 = { p[1] - p0[1], p[2] - p0[2] }
    local e2 = { p1[1] - p[1], p1[2] - p[2] }
    local l1, l2 = math.max(1e-9, math.sqrt(e1[1] ^ 2 + e1[2] ^ 2)), math.max(1e-9, math.sqrt(e2[1] ^ 2 + e2[2] ^ 2))
    local m = { e1[2] / l1 + e2[2] / l2, -e1[1] / l1 - e2[1] / l2 }
    local ml = math.sqrt(m[1] ^ 2 + m[2] ^ 2)
    if ml < 1e-6 then m = { e2[2] / l2, -e2[1] / l2 } ml = 1 end
    nrm[i] = { m[1] / ml, m[2] / ml }
    -- the mitre: a corner moves further than its edges so the moved outline stays parallel
    mit[i] = 1 / math.max(0.4, (nrm[i][1] * e2[2] - nrm[i][2] * e2[1]) / l2)
    reachv[i] = math.sqrt((p[1] - cx) ^ 2 + (p[2] - cz) ^ 2)
    arc[i] = acc
    acc = acc + l2
  end
  local perim = acc
  -- smooth noise around the outline (periodic: sampled on a circle of the perimeter)
  local function ring_noise(scale)
    local ph1, ph2, ph3 = st.range(0, 6.283), st.range(0, 6.283), st.range(0, 6.283)
    local f1 = math.max(1, math.floor(perim / scale + 0.5))
    return function(i)
      local t = arc[i] / perim * 2 * math.pi
      return 0.55 * math.sin(f1 * t + ph1) + 0.3 * math.sin((2 * f1 + 1) * t + ph2) + 0.15 * math.sin((4 * f1 + 3) * t + ph3)
    end
  end
  local gnoise = ring_noise(st.range(6, 12))
  local pnoise = ring_noise(st.range(2.5, 4.5))
  -- the profile rows: {y, inset (mean), wobble scale}
  local rows = {}
  local function row(y, ins, wob, top) rows[#rows + 1] = { y = y, ins = ins, wob = wob, top = top } end
  -- how far rings may move in: a share of the outline's inradius about its centre
  local din = math.huge
  for i = 1, nv do
    local p, q = ring0[i], ring0[i % nv + 1]
    local ex, ez = q[1] - p[1], q[2] - p[2]
    local l2 = math.max(1e-9, ex * ex + ez * ez)
    local f = clamp(((cx - p[1]) * ex + (cz - p[2]) * ez) / l2, 0, 1)
    din = math.min(din, math.sqrt((p[1] + ex * f - cx) ^ 2 + (p[2] + ez * f - cz) ^ 2))
  end
  local budget = (column and 0.2 or 0.55) * din
  local cur = 0
  row(y0 - 0.25, 0, 0)
  local yT = y0
  if talus > 0 then
    yT = y0 + talus
    cur = talus / math.tan(math.rad(34)) * st.range(0.85, 1.15)
    row(y0 + 0.35 * talus, cur * 0.45, 0.25)
    row(yT, cur, 0.3)
  end
  local strata_n = 0
  local y = yT
  local th0 = column and 1.6 or band
  -- the organic budget's level of detail: strata starting above the view band (lod.view metres over the foot) are
  -- lod.far times thicker and keep only their bulge and shoulder rows
  local lod = o.lod
  while y < y1 - 0.3 do
    local far = (lod and y - y0 >= (lod.view or 5)) and R.lod_at(y - y0, lod.view or 5, lod.far or 1) or 1
    local thf = th0 * far
    local th = thf * st.range(0.75, 1.25)
    if y + th > y1 - 0.45 * thf then th = y1 - y end
    local last = y + th >= y1 - 1e-6
    strata_n = strata_n + 1
    local sh = column and 0 or (step * st.range(0.4, 1.0) + 0.3 * th) * math.min(1, th / 2)
    local bulge = (column and 0.08 or 0.25 * math.min(1, th / 2)) * st.range(0.6, 1.2)
    -- a ledge: this stratum starts further in than the one below finished
    if strata_n > 1 and not column then cur = cur + step * st.range(0.1, 1.2) * 0.6 end
    if strata_n > 1 and far <= 1 then row(y + 0.12 * math.min(th, 1.5), cur, 0.45) end
    row(y + 0.45 * th, math.max(-0.3, cur - bulge), 0.5)
    if last then
      if far <= 1 then row(y + 0.82 * th, cur + 0.4 * sh, 0.45) end
      row(y1, cur + sh + (column and 0 or 0.25), 0.35, true)
    else
      if far <= 1 then row(y + 0.85 * th, cur + 0.35 * sh, 0.45) end
      cur = cur + sh
      row(y + th, cur, 0.4)
    end
    y = y + th
  end
  -- the whole profile scaled to the outline's budget (a small mass keeps its proportions, only shallower)
  local mx = 0
  for _, rw in ipairs(rows) do mx = math.max(mx, rw.ins) end
  if mx > budget then
    for _, rw in ipairs(rows) do rw.ins = rw.ins * budget / mx end
  end
  -- per-row ring points (insets scaled down when a ring would fold over the centre)
  local rnoise, yj = {}, {}
  for r = 1, #rows do
    rnoise[r] = ring_noise(st.range(3, 7))
    yj[r] = {}
    for i = 1, nv do yj[r][i] = st.range(-0.05, 0.05) * math.min(1, th0 / 2) end
  end
  -- the corners' bisectors and mitres (a ring is the outline moved in parallel: corners move further)
  local cn, cmit = {}, {}
  for e = 1, ncor do
    local p0, p, p1 = corners[(e - 2) % ncor + 1], corners[e], corners[e % ncor + 1]
    local e1 = { p[1] - p0[1], p[2] - p0[2] }
    local e2 = { p1[1] - p[1], p1[2] - p[2] }
    local l1, l2 = math.max(1e-9, math.sqrt(e1[1] ^ 2 + e1[2] ^ 2)), math.max(1e-9, math.sqrt(e2[1] ^ 2 + e2[2] ^ 2))
    local m = { e1[2] / l1 + e2[2] / l2, -e1[1] / l1 - e2[1] / l2 }
    local ml = math.max(1e-6, math.sqrt(m[1] ^ 2 + m[2] ^ 2))
    cn[e] = { m[1] / ml, m[2] / ml }
    cmit[e] = math.min(3, 1 / math.max(0.33, (cn[e][1] * e2[2] - cn[e][2] * e2[1]) / l2))
  end
  local function build_rings(scale)
  local rings = {}
  for r, rw in ipairs(rows) do
    local rn = rnoise[r]
    local ring = {}
    local I = r == 1 and 0 or clamp(rw.ins * scale, -0.3, 0.6 * din)
    local moved = {}
    for e = 1, ncor do moved[e] = { corners[e][1] - cn[e][1] * I * cmit[e], corners[e][2] - cn[e][2] * I * cmit[e] } end
    for i = 1, nv do
      local wv = 0.6 * gnoise(i) + 0.4 * rn(i)
      local e, f = vdesc[i][1], vdesc[i][2]
      local A, B = moved[e], moved[e % ncor + 1]
      local base = { A[1] + (B[1] - A[1]) * f, A[2] + (B[2] - A[2]) * f }
      -- smooth wobble about the parallel ring; the foot ring is rough corner by corner
      local ins = r == 1 and 0 or (math.abs(I) * 0.35 * wv + rw.wob * (column and 0.15 or 0.5) * wv) * scale
      ins = ins - (r == 1 and push[i] or 0.5 * rough * (1 + pnoise(i)) * 0.6)
      ins = clamp(ins, -0.35 - rough, 0.25 * din)
      local yy = rw.y
      if rw.top then
        yy = y1 - vary * math.min(th0, H / 2) * (0.5 + 0.5 * wv) * (column and 0 or 1)
        yy = math.max(yy, rows[r - 1].y + 0.2)
      elseif r > 1 then
        yy = yy + yj[r][i]
      end
      ring[i] = { base[1] - nrm[i][1] * ins, yy, base[2] - nrm[i][2] * ins }
    end
    rings[r] = ring
  end
  -- star-shaped about the centre at every ring? (every sector turns counter-clockwise)
  for _, ring in ipairs(rings) do
    for i = 1, nv do
      local p, q = ring[i], ring[i % nv + 1]
      if (p[1] - cx) * (q[3] - cz) - (q[1] - cx) * (p[3] - cz) < 0.02 then return nil end
    end
  end
  return rings
  end
  local rings = build_rings(1) or build_rings(0.6) or build_rings(0.3)
  if rings == nil then return mass_strata(poly, y0, y1, o) end
  local hulls = {}
  for r = 1, #rings - 1 do
    local lo, hi = rings[r], rings[r + 1]
    local top = r == #rings - 1
    local ylo = rows[r].y
    local yhi = top and y1 or rows[r + 1].y
    for i = 1, nv do
      local j = i % nv + 1
      hulls[#hulls + 1] = {
        { cx, ylo, cz }, { cx, yhi, cz },
        lo[i], lo[j], hi[i], hi[j],
      }
    end
  end
  return hulls, math.max(2, strata_n + (talus > 0 and 1 or 0))
end

---@kit kit.rock_mass{name?, poly, y0, y1, units?, band?, step?, roughness?, talus?, top_vary?, seed?, group?, texture?|textures?} → rock_mass — A rock mass that follows a plan outline (mesa, outcrop, cliff, buttress) as eroded rock: a talus apron, rounded strata stepping in with ledges, a wandering rim; the foot keeps the outline. Build it after the air it stands in.
---@opt name: string = rock_mass<n> — Names the mass (tags, group).
---@opt poly: {x, z}[] — Required: the plan outline (3 to 256 points, world metres or with units = "px" picture pixels); concave allowed.
---@opt y0: number — Required: the foot height (the mass sinks 0.25 m below it so it never floats).
---@opt y1: number — Required: the top height (the highest stratum reaches it; others stop lower).
---@opt units: string = "m" — "m" world metres, "px" picture pixels through ref.calibrate.
---@opt band: number = (y1 - y0) / 4 (1.2 to 4) — Stratum height in metres.
---@opt step: number = 0.5 — How far each stratum steps in from the one below, metres on average (0 = sheer sides).
---@opt roughness: number = 0.25 + 0.06 × height (at most 1.2) — How far outline corners push out at random, metres.
---@opt talus: number = 0.18 × height (0.5 to 3; none when step is 0) — Height of the talus apron at the foot, metres (0 = none); it slopes up about 34° from the outline.
---@opt top_vary: number = 0.35 — The share of the top stratum's height the rim may lose around the outline (a wandering skyline).
---@opt seed: integer = 1 — The random stream.
---@opt group: boolean = true — Group the mass's brushes under its name.
---@opt texture: string = kit.defaults.rock_texture — One texture for every face.
---@opt textures: {floor=, wall=, ceiling=} = kit.defaults.rock_textures — Textures by player-relative role (tops are floor); without it the default rock is one texture on every face.
function kit.rock_mass(t)
  local api = "kit.rock_mass"
  check_keys(api, t, { "name", "poly", "y0", "y1", "units", "band", "step", "roughness", "talus", "top_vary", "seed", "group", "texture", "textures" })
  local name = name_for(api, "rock_mass", t.name)
  local y0, y1 = number(api, "y0", t.y0), number(api, "y1", t.y1)
  if y1 - y0 < 0.5 then fail(api, "y1 (" .. fmt(y1) .. ") must be at least 0.5 m above y0 (" .. fmt(y0) .. ").") end
  local units = t.units or "m"
  if units ~= "m" and units ~= "px" then fail(api, "'units' must be \"m\" or \"px\".") end
  if type(t.poly) ~= "table" or #t.poly < 3 then fail(api, "'poly' must list at least 3 points {x, z}.") end
  if #t.poly > 256 then fail(api, "'poly' has " .. #t.poly .. " points; at most 256.") end
  local poly = {}
  for i, q in ipairs(t.poly) do
    local x, z = nil, nil
    if type(q) == "table" then x, z = q[1] or q.x, q[2] or q.z or q.y end
    if type(x) ~= "number" or type(z) ~= "number" then fail(api, "'poly[" .. i .. "]' must be a point {x, z}.") end
    poly[i] = { x, z }
  end
  if units == "px" then
    if not ref.info().calibrated then fail(api, "units = \"px\" needs a calibrated reference (ref.calibrate).") end
    poly = ref.world_points(poly)
  end
  poly = ccw(poly)
  if area2(poly) < 0.5 then fail(api, "'poly' encloses less than 0.5 m².") end
  if not simple(poly) then fail(api, "'poly' crosses itself.", "List the corners in order around the outline.") end
  if t.texture ~= nil and t.textures ~= nil then fail(api, "give either 'texture' or 'textures', not both.") end
  local tex, texs = R.textures(t.texture, t.textures)
  local seed = t.seed ~= nil and number(api, "seed", t.seed) or 1
  local band = t.band ~= nil and positive(api, "band", t.band) or nil
  local step = t.step ~= nil and number(api, "step", t.step) or nil
  if step ~= nil and step < 0 then fail(api, "'step' must be 0 or more.") end
  local rough = t.roughness ~= nil and number(api, "roughness", t.roughness) or nil
  if rough ~= nil and (rough < 0 or rough > 2) then fail(api, "'roughness' must be 0 to 2 metres.") end
  local vary = t.top_vary ~= nil and number(api, "top_vary", t.top_vary) or nil
  if vary ~= nil and (vary < 0 or vary > 0.9) then fail(api, "'top_vary' must be 0 to 0.9.") end
  local talus = t.talus ~= nil and number(api, "talus", t.talus) or nil
  if talus ~= nil and (talus < 0 or talus > 0.4 * (y1 - y0) + 1e-9) then fail(api, "'talus' must be 0 to 0.4 of the height (" .. fmt(0.4 * (y1 - y0)) .. " m).") end
  -- The organic budget: full strata in the view band, coarser above; a mass that would not fit its share of what is
  -- left of the level's budget is cut coarser all round (its outline sectors and far strata), up to 3x. A sector of
  -- a stratum row shows about 2.5 facets.
  local budgeted = type(kit.defaults.organic_budget) == "number" and kit.defaults.organic_budget > 0
  local view, far0 = kit.defaults.organic_view or 5, kit.defaults.organic_far or 3
  local function make(coarse, far)
    return R.mass_hulls(poly, y0, y1, { st = R.stream(seed), band = band, step = step, rough = rough, top_vary = vary, talus = talus,
      convex_pieces = I.convex_pieces, coarse = coarse, lod = budgeted and { view = view, far = far } or nil })
  end
  local hulls, nb = make(1, far0)
  local scale = 1
  if budgeted then
    local left = R.budget_left()
    local est = 2.5 * #hulls
    if est > 0.6 * left then
      scale = math.min(3, math.sqrt(est / math.max(0.6 * left, est / 9)))
      hulls, nb = make(scale, far0 * scale)
    end
  end
  local brushes = R.emit(hulls, { name = name, fn = "rock_mass", part = "stratum", texture = tex, textures = texs, one_material = t.textures == nil })
  local x0, z0, x1, z1 = math.huge, math.huge, -math.huge, -math.huge
  for _, q in ipairs(poly) do x0, z0, x1, z1 = math.min(x0, q[1]), math.min(z0, q[2]), math.max(x1, q[1]), math.max(z1, q[2]) end
  return {
    kind = "rock_mass", name = name, brushes = brushes, count = #brushes, strata = nb, outline = poly, budget_scale = scale,
    min = I.vec(x0, y0, z0), max = I.vec(x1, y1, z1),
    group = group_if_many(name, brushes, boolean(api, "group", t.group, true)),
  }
end

end

return install
