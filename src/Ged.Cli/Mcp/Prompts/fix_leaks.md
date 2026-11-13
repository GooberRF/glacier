Find and fix the leaks (holes) in the level open in this Glacier session.

1. Call check_holes. It compiles a sealed build (without applying it) and returns the positions of open edges; zero holes means the level is watertight.
2. For the first few positions: render_plan at that height (y about 1 m below the hole, a region around it, overlays including holes) and look at the image; query brushes in a small region around each position to find the brushes there, and inspect them.
3. Find the cause before changing anything: two spaces that do not meet their opening exactly, a gap or a mis-sized brush, a solid created before the air around it (CSG is time-ordered), a detail brush used where a structural one is needed. Never patch a leak by stacking overlapping faces or extra brushes over it.
4. Make the smallest repair with one named run_lua step, then call check_holes again. Repeat until it reports zero holes.
5. Then call build (sealed) and lint, render the affected storeys, and run the checklist below.
