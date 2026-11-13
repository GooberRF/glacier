Block out a new Red Faction level in Glacier for the designer, from this brief:

{{brief}}

Game type: {{gametype}}.

Work in small, reviewable steps:

1. If you have not read it in this conversation, read the agent guide: glacier://guide/index lists its sections; read glacier://guide/<n> for at least sections 3 to 7. Call session_info to see the mode, the open level and what you are allowed to do.
2. Plan before you build: the rooms and areas with sizes in metres, how they connect, the storeys and their floor heights, and where the Player Start, spawns, items and lights go. If the brief leaves out something that changes the layout (overall size, number of rooms, game type), ask the designer before building.
3. Unless the designer already opened a level for this work, call new_level.
4. Build with run_lua and the design kit (local kit = ged.import("kit")): rooms first, then the openings that join them (doorways, corridors, stairs, ramps), then detail, then lights, spawns and items. Give every script a name and every kit part and object a unique name. Look names up with api_reference, list_classes, describe_class and list_textures; never guess a class or texture name.
5. After each structural step, call render_plan for every storey and look at the image; fix what is wrong before going on.
6. When the layout is right, run the checklist below, then save_level where the designer asked (ask if they did not say).
7. Report what the designer should refine by hand or try in game: sightlines, item balance, door timing.
