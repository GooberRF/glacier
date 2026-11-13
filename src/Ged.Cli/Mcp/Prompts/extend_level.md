Extend the level open in this Glacier session: {{goal}}

1. Call checkpoint with the name "before-extend" so you can show exactly what you changed.
2. Understand the surroundings first: level_summary, render_plan of the storeys involved, and query / inspect for the rooms, brushes and objects the new part connects to (their exact positions and sizes). Decide where the new part attaches; keep at least 1 m of solid between spaces that must stay separate.
3. Add the new part with run_lua and the design kit, one named script per coherent step; each run is one undo step labelled "AI: <name>". Leave the designer's existing content alone unless the goal requires changing it, and say so before you do.
4. Call diff_since with checkpoint "before-extend" and confirm that only what you meant to change changed.
5. Render the affected storeys and look at them, then run the checklist below.
