-- Glacier design kit: the population modules' space probe (ged.import("kit_probe")).
--
-- A 2.5D column map of the level's brushes, read straight from the editor document (no compile):
-- every 0.5 m floor column holds the open-air intervals left by the time-ordered CSG fold (the world
-- starts solid, later air carves, later solid fills). On top of it: a floor lattice with walk and foot
-- graphs, point/line-of-sight tests, horizontal rays, exact point tests and a seeded random stream.
--
-- The column map, lattice, graphs and sight tests run natively in the kit's engine
-- (ged.import("kit_core"), C#), so populating a whole level stays far inside the script instruction
-- budget; this module wraps them in the shapes the population modules use (node tables, id sets).
-- kit.spawn_points and kit.items plan in the engine outright (kit_items.lua); their spawn exposure
-- and spawn-to-spawn sight are the design grader's own measure (spatial.spawn_report, G19).
--
-- Rules the probe follows (the same numbers the study and the kit use):
--   * walk edges: up or down steps of at most 1.5 m (Alpine Faction game_patch/misc/waypoints.h:41,
--     kRouteMaxUpwardStepHeight) with at least 1 m of clear air above the higher floor; one-way
--     drops of at most 8 m (the exemplar study's walk graph).
--   * standing: at least kit.limits.min_headroom (2 m, waypoints.h:49) of air over the floor.
--   * foot graph: steps of at most 0.6 m only (no jumping) on floor at least 1.5 m wide (the floor
--     around a node is eroded by one lattice step), so a route that survives it is walkable without
--     a jump and at least a player wide.
--   * mover brushes count as open (doors open). An air portal brush carves after every ordinary brush
--     (RED applies its portal list after its brush list), so a kit.doorway{portal = true} or
--     kit.opening{portal = true} opening is walkable; a solid portal brush adds nothing. Portal
--     brushes are openings between spaces, never spaces of their own (P.spaces leaves them out).
--
-- This module only defines functions at its top level.

local P = {}

P.PITCH = 0.5
P.STEP = 1.5        -- walk step (waypoints.h:41)
P.DROP = 8.0        -- one-way drop (exemplar study walk graph)
P.FOOT_STEP = 0.6   -- foot-graph step (no jump)
P.HEADROOM = 2.0    -- standing headroom (waypoints.h:49)
P.EYE = 1.5         -- eye height above the floor for sight tests

-- Set kit_probe.trace = true to log the population functions' phases (log.info "probe: ...").
P.trace = false
P.stats = { los = 0, col = 0, expo = 0 }
function P.mark(label) if P.trace then log.info("probe: " .. label .. " los=" .. P.stats.los .. " col=" .. P.stats.col .. " expo=" .. P.stats.expo) end end

local floor, ceil, sqrt, abs, min, max = math.floor, math.ceil, math.sqrt, math.abs, math.min, math.max
local PITCH = P.PITCH
local OFF = 1048576

local function ckey(i, k) return (i + OFF) * 2097152 + (k + OFF) end
P.ckey = ckey

-- ---------------------------------------------------------------------------------------------
-- A seeded stream (Park-Miller minimal standard): the kit never reads the run's rng.
-- ---------------------------------------------------------------------------------------------

function P.stream(seed)
  local s = floor((seed or 1) * 7919 + 104729) % 2147483646
  if s <= 0 then s = s + 2147483646 end
  local st = {}
  function st.next()
    s = (s * 48271) % 2147483647
    return s / 2147483647
  end
  function st.range(a, b) return a + (b - a) * st.next() end
  function st.int(a, b)
    local v = a + floor(st.next() * (b - a + 1))
    if v > b then v = b end
    return v
  end
  function st.pick(list) return list[st.int(1, #list)] end
  -- list of {item, weight} (or {name, weight}); returns the item.
  function st.weighted(list)
    local total = 0
    for _, e in ipairs(list) do total = total + e[2] end
    local r = st.next() * total
    for _, e in ipairs(list) do
      r = r - e[2]
      if r <= 0 then return e[1] end
    end
    return list[#list][1]
  end
  return st
end

-- ---------------------------------------------------------------------------------------------
-- Sorting: a stable merge sort in plain Lua. (table.sort with a comparator calls back from the host
-- on every comparison, which is several milliseconds each in the sandbox; never use it here.)
-- less(a, b) is true when a sorts before b. Returns the list (sorted in place).
-- ---------------------------------------------------------------------------------------------

function P.sort(list, less)
  local n = #list
  if n < 2 then return list end
  local src, dst = list, {}
  local width = 1
  while width < n do
    local i = 1
    while i <= n do
      local mid = min(i + width, n + 1)
      local hi = min(i + 2 * width, n + 1)
      local a, b, o = i, mid, i
      while a < mid and b < hi do
        if less(src[b], src[a]) then dst[o] = src[b] b = b + 1 else dst[o] = src[a] a = a + 1 end
        o = o + 1
      end
      while a < mid do dst[o] = src[a] a = a + 1 o = o + 1 end
      while b < hi do dst[o] = src[b] b = b + 1 o = o + 1 end
      i = i + 2 * width
    end
    src, dst = dst, src
    width = width * 2
  end
  if src ~= list then for i = 1, n do list[i] = src[i] end end
  return list
end

-- ---------------------------------------------------------------------------------------------
-- The probe (the engine's column map, shared by the population calls of a run).
-- ---------------------------------------------------------------------------------------------

local CORE = ged.import("kit_core")
local Probe = {}

-- Methods are copied onto each probe (the sandbox has no metatables).
local function instance(methods)
  local o = {}
  for k, f in pairs(methods) do o[k] = f end
  return o
end
P.instance = instance

local wrapped = nil

--- The level's shared probe: rebuilt by the engine when the brushes changed since it was made, and
-- extended when brushes were only added at the end of time order.
function P.shared()
  local h = CORE:probe()
  if wrapped and wrapped.h == h then return wrapped end
  local self = instance(Probe)
  self.h = h
  wrapped = self
  return self
end

--- Marks the shared probe current again after its owner added the brushes it made (add_brush).
function P.touch(probe) probe.h:touch() end

function P.index(v) return floor(v / PITCH) end
function P.center(i) return (i + 0.5) * PITCH end

--- Adds a brush made after the probe was built (it is the newest in time order).
function Probe:add_brush(b) self.h:add_brush(b) end

--- Adds a virtual solid box (an object's collision footprint, e.g. clutter) on top of every brush.
function Probe:add_block(x0, y0, z0, x1, y1, z1) self.h:add_block(x0, y0, z0, x1, y1, z1) end

--- True when the world point is open air.
function Probe:air(x, y, z) return self.h:air(x, y, z) end

--- True when the segment a -> b is open air (sampled every 0.5 m, the column pitch).
function Probe:los(ax, ay, az, bx, by, bz)
  P.stats.los = P.stats.los + 1
  return self.h:los(ax, ay, az, bx, by, bz)
end

--- How far a horizontal ray from (x, y, z) along (dx, dz) runs in open air, up to maxd (0.5 m steps).
function Probe:ray(x, y, z, dx, dz, maxd) return self.h:ray(x, y, z, dx, dz, maxd) end

--- Share of 8 horizontal chest-height rays (every 45 degrees) from (x, y, z) that hit something within d metres.
function Probe:protection(x, y, z, d) return self.h:protection(x, y, z, d) end

--- The heading (unit dx, dz) of the longest of 16 chest-height rays (ties: lower index), and its length.
function Probe:longest_ray(x, y, z, maxd)
  local r = self.h:longest_ray(x, y, z, maxd)
  return r[1], r[2], r[3]
end

--- The open-air state of a world point from the brush planes (exact, not quantized to columns).
function Probe:air_exact(x, y, z) return self.h:air_exact(x, y, z) end

--- Exact distance from (x, y, z) along the horizontal unit (dx, dz) to the first solid, up to maxd
-- (0.05 m resolution); maxd when nothing is hit.
function Probe:wall_distance(x, y, z, dx, dz, maxd) return self.h:wall_distance(x, y, z, dx, dz, maxd) end

--- Existing dressing brushes whose centre is in the box (beams, slabs, small solids, detail; light
-- fixtures left out).
function Probe:dressing_in(x0, y0, z0, x1, y1, z1) return self.h:dressing_in(x0, y0, z0, x1, y1, z1) end

--- Centres {x, z, x, z, ...} of the detail solids whose centre is in the box.
function Probe:detail_centres(x0, y0, z0, x1, y1, z1) return self.h:detail_centres(x0, y0, z0, x1, y1, z1) end

-- ---------------------------------------------------------------------------------------------
-- The floor lattice and its graphs.
-- ---------------------------------------------------------------------------------------------

local N4 = { { 1, 0 }, { -1, 0 }, { 0, 1 }, { 0, -1 } }
local N8 = { { 1, 0 }, { -1, 0 }, { 0, 1 }, { 0, -1 }, { 1, 1 }, { 1, -1 }, { -1, 1 }, { -1, -1 } }
P.N4, P.N8 = N4, N8
local KEYM = 2097152

--- The floor lattice over [x0, x1] x [z0, z1] for floors with y in [ylo, yhi]: standing nodes
-- {i, k, x, z, y, top, id} and at = {[ckey] = ids}; L.h is the engine's lattice (graphs, sets).
-- stride > 1 keeps every stride-th column only; zone = a layout zone's name keeps the floor inside its air brushes.
function Probe:lattice(x0, z0, x1, z1, ylo, yhi, headroom, stride, zone)
  local h = self.h:lattice(x0, z0, x1, z1, ylo, yhi, headroom or P.HEADROOM, stride or 1, zone)
  local a = h:arrays()
  local I, Kk, X, Z, Y, T = a.i, a.k, a.x, a.z, a.y, a.top
  local nodes, at = {}, {}
  for id = 1, #X do
    local i, k = I[id], Kk[id]
    nodes[id] = { i = i, k = k, x = X[id], z = Z[id], y = Y[id], top = T[id], id = id }
    local key = (i + OFF) * KEYM + (k + OFF)
    local l = at[key]
    if not l then l = {} at[key] = l end
    l[#l + 1] = id
  end
  return { nodes = nodes, at = at, probe = self, h = h, x0 = x0, z0 = z0, x1 = x1, z1 = z1, ylo = ylo, yhi = yhi }
end

-- The node of column (i, k) nearest height y within tol, or nil.
function P.node_at(L, i, k, y, tol)
  local a = L.at[(i + OFF) * KEYM + (k + OFF)]
  if not a then return nil end
  local nodes = L.nodes
  local best, bd = nil, tol or 0.6
  for q = 1, #a do
    local id = a[q]
    local d = nodes[id].y - y
    if d < 0 then d = -d end
    if d <= bd then best, bd = id, d end
  end
  return best
end

-- The node nearest a world point (searched within r metres), or nil.
function P.nearest(L, x, y, z, r, ytol)
  r = r or 1.5
  ytol = ytol or 1.0
  local ci, ck = floor(x / PITCH), floor(z / PITCH)
  local s = ceil(r / PITCH)
  local nodes = L.nodes
  local best, bd = nil, math.huge
  for i = ci - s, ci + s do
    for k = ck - s, ck + s do
      local a = L.at[(i + OFF) * KEYM + (k + OFF)]
      if a then
        for q = 1, #a do
          local n = nodes[a[q]]
          local dy = n.y - y
          if dy <= ytol and dy >= -ytol - 1.5 then
            local d = (n.x - x) ^ 2 + (n.z - z) ^ 2 + 0.25 * dy * dy
            if d < bd then best, bd = a[q], d end
          end
        end
      end
    end
  end
  return best
end

--- The "wide" nodes of the foot graph (all 8 neighbouring columns hold floor within the foot step):
-- a list indexed by node id (true / false).
function P.wide(L) return L.h:wide() end

--- The nodes within r metres (same floor, +-0.3 m) of a floor edge (a list indexed by node id).
function P.near_edge(L, r) return L.h:near_edge(r) end

--- Flat nodes: no neighbouring floor within a foot step at another height (more than tol metres).
function P.flat(L, tol) return L.h:flat(tol or 0.02) end

--- Nodes beside a step, ramp or ledge edge (a list of ids).
function P.step_nodes(L, tol) return L.h:step_nodes(tol or 0.02) end

--- Foot-graph components over the node set `inside` (a set or a list by id): {[id] = component}.
function P.components(L, inside)
  local list = {}
  for id = 1, #L.nodes do list[id] = inside[id] and true or false end
  return L.h:components(list)
end

--- The walkable area (m^2) of a lattice: nodes reachable (walk graph) from every foot component
-- holding at least 8 m^2 of floor. Returns area, reach set (indexed by node id).
function P.walk_area(L)
  local r = L.h:walk_area()
  return r.area, r.reach
end

-- ---------------------------------------------------------------------------------------------
-- Spaces: the rooms a population call works on.
-- ---------------------------------------------------------------------------------------------

local function sky_of(brushes)
  for _, b in ipairs(brushes) do
    if b.air then
      for _, f in ipairs(b:faces_facing("up")) do
        if f.show_sky then return true end
      end
    end
  end
  return false
end

-- A space from a kit record (min/max box, optional brush/brushes) or a name (the kit.name tag).
local function space_of(api, key, r, fail)
  if type(r) == "string" then
    local x0, y0, z0, x1, y1, z1 = math.huge, math.huge, math.huge, -math.huge, -math.huge, -math.huge
    local airs = {}
    local zone = false
    -- A kit.build_layout zone's name (kit.zone: every air brush of the zone, whatever its parts are named), else a
    -- kit record's name (kit.name).
    for _, tag in ipairs({ "kit.zone", "kit.name" }) do
      if #airs == 0 then
        for _, b in ipairs(level.brushes_tagged(tag, r).brushes) do
          if b.air and not b.portal then
            airs[#airs + 1] = b
            local bb = b.bounds
            x0, y0, z0 = min(x0, bb.min.x), min(y0, bb.min.y), min(z0, bb.min.z)
            x1, y1, z1 = max(x1, bb.max.x), max(y1, bb.max.y), max(z1, bb.max.z)
          end
        end
        zone = (#airs > 0 and tag == "kit.zone")
      end
    end
    if #airs == 0 then fail(api, "'" .. key .. "': no kit room, corridor, cave or layout zone is named '" .. r .. "'.", "Pass the record a kit function returned, a name its brushes carry (kit.name tag), or a kit.build_layout zone's name (report.zones[i].name).") end
    -- A layout zone's floor is the floor inside its own air brushes: the box of a turned or traced zone also holds
    -- a neighbour's floor, so the engine keeps only the nodes that stand inside them (an unturned box zone's floor
    -- is all inside them: the same nodes).
    if zone then
      local uids = {}
      for i, b in ipairs(airs) do uids[i] = b.uid end
      CORE:zone(r, uids)
    end
    local kind = airs[1]:tags().kit or "room"
    return { name = r, kind = kind, x0 = x0, y0 = y0, z0 = z0, x1 = x1, y1 = y1, z1 = z1, sky = sky_of(airs), brushes = airs, zone = zone or nil }
  end
  if type(r) ~= "table" or r.min == nil or r.max == nil then
    fail(api, "'" .. key .. "' must be a kit record (room, corridor, cave, canyon, passage) or a kit.name string.")
  end
  local a, b = r.min, r.max
  local brushes = r.brushes or (r.brush and { r.brush }) or {}
  local floor_y = r.floor_y or a.y
  return {
    name = r.name or key, kind = r.kind or "room",
    x0 = min(a.x, b.x), y0 = min(a.y, b.y), z0 = min(a.z, b.z), x1 = max(a.x, b.x), y1 = max(a.y, b.y), z1 = max(a.z, b.z),
    floor_y = floor_y, sky = sky_of(brushes), brushes = brushes, record = r,
  }
end

--- The spaces of a call: t.room (one), t.rooms (a list), t.area ({min=, max=}), or — none given —
-- every air space of the level (kit rooms by name, other air brushes one by one; at least 16 m^2;
-- sky boxes, doorways, portal brushes and stair envelopes left out).
function P.spaces(api, t, fail, no_sky)
  local given = (t.room ~= nil and 1 or 0) + (t.rooms ~= nil and 1 or 0) + (t.area ~= nil and 1 or 0)
  if given > 1 then fail(api, "give one of 'room', 'rooms' or 'area', not several.") end
  local out = {}
  if t.room ~= nil then
    out[1] = space_of(api, "room", t.room, fail)
  elseif t.rooms ~= nil then
    if type(t.rooms) ~= "table" or #t.rooms == 0 then fail(api, "'rooms' must list kit records or kit.name strings.") end
    for i, r in ipairs(t.rooms) do out[i] = space_of(api, "rooms[" .. i .. "]", r, fail) end
  elseif t.area ~= nil then
    local a = t.area
    if type(a) ~= "table" or a.min == nil or a.max == nil then fail(api, "'area' must be {min = {x, y, z}, max = {x, y, z}}.") end
    local function v3(p, k)
      local x, y, z = nil, nil, nil
      if type(p) == "userdata" then x, y, z = p.x, p.y, p.z elseif type(p) == "table" then
        if p[1] ~= nil then x, y, z = p[1], p[2], p[3] else x, y, z = p.x, p.y, p.z end
      end
      if type(x) ~= "number" or type(y) ~= "number" or type(z) ~= "number" then fail(api, "'area." .. k .. "' must be a point {x, y, z}.") end
      return x, y, z
    end
    local ax, ay, az = v3(a.min, "min")
    local bx, by, bz = v3(a.max, "max")
    out[1] = { name = t.name or "area", kind = "area", x0 = min(ax, bx), y0 = min(ay, by), z0 = min(az, bz), x1 = max(ax, bx), y1 = max(ay, by), z1 = max(az, bz), sky = false, brushes = {} }
  else
    local groups, order = {}, {}
    for _, b in ipairs(level.all_brushes().brushes) do
      if b.air and not b.portal and b.mover == nil then
        local tags = b:tags()
        local kind = tags.kit
        if kind ~= "sky_room" and kind ~= "doorway" and kind ~= "stairs" and kind ~= "ramp" then
          local key = tags["kit.name"] or ("brush#" .. b.uid)
          local g = groups[key]
          if not g then
            g = { name = tags["kit.name"] or ("air" .. b.uid), kind = kind or "room", brushes = {},
              x0 = math.huge, y0 = math.huge, z0 = math.huge, x1 = -math.huge, y1 = -math.huge, z1 = -math.huge }
            groups[key] = g
            order[#order + 1] = key
          end
          local bb = b.bounds
          g.brushes[#g.brushes + 1] = b
          g.x0, g.y0, g.z0 = min(g.x0, bb.min.x), min(g.y0, bb.min.y), min(g.z0, bb.min.z)
          g.x1, g.y1, g.z1 = max(g.x1, bb.max.x), max(g.y1, bb.max.y), max(g.z1, bb.max.z)
        end
      end
    end
    for _, key in ipairs(order) do
      local g = groups[key]
      if (g.x1 - g.x0) * (g.z1 - g.z0) >= 16 then
        g.sky = (not no_sky) and sky_of(g.brushes) or false
        out[#out + 1] = g
      end
    end
    if #out == 0 then fail(api, "the level has no air space to work on.", "Build rooms first (kit.room, kit.corridor, kit.cave …).") end
  end
  for _, s in ipairs(out) do s.floor_y = s.floor_y or s.y0 end
  return out
end

-- ---------------------------------------------------------------------------------------------
-- Space context (the intent layer's theme fit): the grader reads the same three contexts from the
-- compiled faces (SpatialAnalysis.Purpose.cs) — open (sky), rock_walls (natural rock) or built_interior.
-- ---------------------------------------------------------------------------------------------

--- True for a natural-rock texture name (stock rck_ / rock / cliff / boulder / cave families).
function P.rock_texture(name)
  local n = string.lower(name or "")
  return n:find("rck", 1, true) ~= nil or n:find("rock", 1, true) ~= nil or n:find("cliff", 1, true) ~= nil
    or n:find("boulder", 1, true) ~= nil or n:sub(1, 3) == "cav" or n:find("_cave", 1, true) ~= nil
end

--- The context of a space: "rock_walls" (a cave or canyon, or at least 35 % of its wall faces natural rock), "open"
-- (under the sky with no built walls: open ground, floating decks), "built_yard" (under the sky inside built walls)
-- or "built_interior". Rocks belong in the first two. A space without brushes of its own reads the air brushes in its box.
function P.context(s)
  if s.kind == "cave" or s.kind == "canyon" then return "rock_walls" end
  local sky = s.sky
  local brushes = s.brushes
  if brushes == nil or #brushes == 0 then
    brushes = {}
    for _, b in ipairs(level.brushes_in{ min = { s.x0, s.y0, s.z0 }, max = { s.x1, s.y1, s.z1 } }.brushes) do
      if b.air then brushes[#brushes + 1] = b end
    end
  end
  local rock, all = 0, 0
  for _, b in ipairs(brushes) do
    for _, f in ipairs(b.faces) do
      if f.show_sky then sky = true end
      local n = f.normal
      if abs(n.y) < 0.7 then
        all = all + 1
        if P.rock_texture(f.texture) then rock = rock + 1 end
      end
    end
  end
  if all > 0 and rock / all >= 0.35 then return "rock_walls" end
  if sky then return all > 0 and "built_yard" or "open" end
  return "built_interior"
end

--- Every air space of the level (P.spaces with no room given; sky flags not read), cached while the
-- shared probe's brushes are unchanged.
local all_spaces_cache = nil
function P.all_spaces(api, fail)
  local key = CORE:stamp()
  if all_spaces_cache and all_spaces_cache.key == key then return all_spaces_cache.list end
  local list = P.spaces(api, {}, fail, true)
  all_spaces_cache = { key = key, list = list }
  return list
end

--- The union box of spaces, grown by m metres (y grown by 2 m below and m above).
function P.union_box(spaces, m)
  local b = { x0 = math.huge, y0 = math.huge, z0 = math.huge, x1 = -math.huge, y1 = -math.huge, z1 = -math.huge }
  for _, s in ipairs(spaces) do
    b.x0, b.y0, b.z0 = min(b.x0, s.x0), min(b.y0, s.y0), min(b.z0, s.z0)
    b.x1, b.y1, b.z1 = max(b.x1, s.x1), max(b.y1, s.y1), max(b.z1, s.z1)
  end
  m = m or 0
  b.x0, b.z0, b.x1, b.z1 = b.x0 - m, b.z0 - m, b.x1 + m, b.z1 + m
  b.y0, b.y1 = b.y0 - 2, b.y1 + m
  return b
end

--- The lattice of a space (its box, floors between its floor and 1 m under its top).
function Probe:space_lattice(s, headroom)
  return self:lattice(s.x0, s.z0, s.x1, s.z1, s.y0 - 0.1, max(s.y0, s.y1 - 1.0), headroom, 1, s.zone and s.name or nil)
end

--- The walkable area (m^2) of a space's lattice, measured in the engine only (P.walk_area of space_lattice).
function Probe:walk_area_of(s, headroom)
  if s.zone then return self.h:lattice(s.x0, s.z0, s.x1, s.z1, s.y0 - 0.1, max(s.y0, s.y1 - 1.0), headroom or P.HEADROOM, 1, s.name):walk_area().area end
  return self.h:walk_area(s.x0, s.z0, s.x1, s.z1, s.y0 - 0.1, max(s.y0, s.y1 - 1.0), headroom or P.HEADROOM)
end

--- Door nodes of a space lattice: nodes on the space's edge whose outward neighbour column (outside
-- the box) holds open floor within a walk step — the openings the space is entered by.
function P.door_nodes(L, s) return L.h:door_nodes(s.x0, s.z0, s.x1, s.z1) end

return P
