❄⛏ Glacier Changelog ⛏❄
===================================

Version 1.0.0: Not yet released
--------------------------------

### Editor
- Full stock RED editor parity: brush and CSG modeling, face, vertex and edge editing, and texturing with UV mapping and a dedicated UV unwrap editor
- All stock object types plus Alpine mesh, note, corona and bag objects, with data-driven property inspectors
- All 90 stock and 58 Alpine events, object links, triggers, movers, groups and cutscenes
- Automatic move, rotate and scale gizmos, marquee box-select, and unlimited branching undo
- Live CSG preview, command palette, and fully bindable keymaps with RED Classic and Modern presets
- Toast notifications alongside the status bar and log

### Geometry and lighting
- RED-authentic Shared BSP geometry compile, with an alternative Incremental build method
- Byte-exact lightmap baking, with optional quality upgrades:
  - Seam blend, corner leak fix, bounced light, ambient occlusion, soft shadows and mover shadows

### Files and assets
- Reads all RFL versions (180-305) and always saves Alpine v305, with byte-preserving round-trip
- Asset browser and VPP packfile builder with an automatic dependency scanner
- Link Graph and Dependency Graph panels
- Prefab system with parametric instances
- Import OBJ, FBX, glTF and DAE; export glTF, OBJ, VRML and v3m
- Lua scripting for editor automation

### Playtest and platform
- One-key playtest in stock Red Faction or Alpine Faction, including multiplayer
- Windows with Direct3D 11 or OpenGL, and a native Linux build
