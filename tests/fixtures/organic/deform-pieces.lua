-- Organic corpus (ORGANIC-GEOMETRY-SPEC §4.3): structural columns in a room, twisted, bent and tapered with
-- 1, 4 and 12 pieces, and a jittered one (geo edits; the seed varies the jitter and the twist angles).
-- Run by OrganicCorpusTests with SEED set; returns "" (no tubes).
local seed = SEED or 1
kit.room{ name = "hall", pos = { 0, 0, 0 }, size = { 40, 10, 40 } }
local function col(x, z) return level.brush{ shape = "box", pos = { x, 2.5, z }, size = { 1.5, 5, 1.5 } } end
local spin = 60 + 10 * (seed % 4)
geo.twist{ brushes = col(-12, -12), degrees = spin }
geo.twist{ brushes = col(-6, -12), degrees = spin, pieces = 4 }
geo.twist{ brushes = col(0, -12), degrees = spin, pieces = 12 }
geo.taper{ brushes = col(6, -12), scale = 0.4, pieces = 4 }
geo.taper{ brushes = col(12, -12), scale = 1.6, pieces = 12 }
geo.bend{ brushes = col(-8, 8), axis = "y", toward = "x", degrees = 40, pieces = 4 }
geo.jitter{ brushes = col(8, 8), amount = { 0.2, 0, 0.2 }, seed = seed }
return ""
