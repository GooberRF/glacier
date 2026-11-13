Review the level open in this Glacier session and report what needs attention. Focus: {{focus}}.

This is a read-only review: do not call run_lua, build, save_level, undo, redo, open_level or new_level. Every tool used below leaves the level, its undo history and its compiled geometry unchanged.

1. Call session_info, then level_summary (detail normal) for counts, rooms, links, spawns and lint totals.
2. Call render_plan for each storey (y = that storey's floor height, from the summary's rooms and bounds) and look at every image: rooms that do not connect, objects inside solid, cramped spawns, unlit areas.
3. Call check_holes (it compiles a sealed build without applying it) and lint (min_severity warning first; info only when asked).
4. Call compare_to_norms (when offered) for proportions against the corpus norms: doorway and corridor sizes, headroom and light density outside the typical p5-p95 range are design hints, not errors. It measures the compiled geometry, so on an unbuilt or preview level say that the comparison needs a sealed build.
5. Use query and inspect to confirm each suspicion with exact values before you report it.
6. Report findings, most important first. For each: what it is, where (UIDs and positions in metres), why it matters, and a suggested fix. Keep what you measured apart from what you judge, and do not change anything until the designer asks.

Use the checklist below as review criteria: check each item without changing the level, and say which items need a build or an edit to confirm.
