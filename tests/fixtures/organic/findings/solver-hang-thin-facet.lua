-- COMPILER FINDING: ONE valid convex air hull (12 faces, closed, convex, planar on the 1/1024 m grid) about
-- 117 m from the origin, with a short 0.125 m vertical edge (a terrace riser corner) beside a long, thin facet.
-- CsgPlane.FromPolygon fits each face plane with Newell's method in single precision on ABSOLUTE world
-- coordinates; for this facet the fitted plane misses the face's own corners by more than the 1e-4 m band. A
-- BSP node that took the facet's plane without consuming the facet would push it to a child, which would take
-- the same plane again without end. The BSP builders always consume the polygon that donated a node's plane,
-- and HullCompileTests.A_Thin_Far_Facet_Builds_Sealed_And_Watertight builds this hull sealed under both
-- methods. The generators also re-draw any piece whose faces the compiler would fit worse than half the band
-- (Organic/CompilerFit).
level.brush_from_points{ air = true, points = { { -10.0185546875, -1.0, 117.43359375 }, { 15.2470703125, -1.0, 111.1171875 }, { 14.986328125, -1.0, 114.287109375 }, { -7.0595703125, -1.0, 125.7509765625 }, { -10.0185546875, -0.875, 117.43359375 }, { -9.6279296875, 18.0, 117.3359375 }, { 15.8564453125, 18.0, 110.96484375 }, { 16.5439453125, 11.09765625, 110.79296875 }, { -10.013671875, 8.658203125, 127.287109375 }, { -12.8212890625, 18.0, 128.7470703125 }, { 20.357421875, 18.0, 111.494140625 }, { 17.5498046875, 7.96875, 112.9541015625 } } }
