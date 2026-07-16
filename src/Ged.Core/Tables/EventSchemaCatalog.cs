using System;
using System.Collections.Generic;
using System.Linq;

namespace Ged.Core.Tables;

/// <summary>
/// The one data table covering all 148 event classes (90 stock IDs 0–89 + 58
/// Alpine IDs 100–157). Every entry carries its game ID, browser category,
/// per-slot inspector field configs (encoding the disassembly-confirmed slot
/// traps), orientation flag, save-target version gate, signal-forwarding flag,
/// and expected link targets. The event inspector and factory render entirely
/// from this — there is no per-event dialog code.
/// </summary>
public static class EventSchemaCatalog
{
    private static readonly IReadOnlyList<EventSchema> AllList = Build();

    private static readonly Dictionary<string, EventSchema> ByNameMap =
        AllList.ToDictionary(e => e.ClassName, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<int, EventSchema> ByIdMap =
        AllList.GroupBy(e => e.GameId).ToDictionary(g => g.Key, g => g.First());

    /// <summary>All 148 event schemas, stock (0–89) then Alpine (100–157).</summary>
    public static IReadOnlyList<EventSchema> All => AllList;

    /// <summary>Placeable event classes (excludes the 3 auto/legacy classes).</summary>
    public static IEnumerable<EventSchema> Placeable => AllList.Where(e => e.Placeable);

    public static EventSchema? Find(string? className) =>
        className is not null && ByNameMap.TryGetValue(className, out EventSchema? s) ? s : null;

    public static EventSchema? FindById(int gameId) => ByIdMap.GetValueOrDefault(gameId);

    // ---- compact field builders ----------------------------------------------
    private static EventFieldSpec Txt(EventSlot s, string l) => new(s, EventEditor.Text, l);

    private static EventFieldSpec Int(EventSlot s, string l) => new(s, EventEditor.Int, l);

    private static EventFieldSpec Flt(EventSlot s, string l) => new(s, EventEditor.Float, l);

    private static EventFieldSpec Bln(EventSlot s, string l) => new(s, EventEditor.Bool, l);

    private static EventFieldSpec Uid(EventSlot s, string l) => new(s, EventEditor.UidPicker, l);

    private static EventFieldSpec Fil(EventSlot s, string l, EventFileKind k) =>
        new(s, EventEditor.FilePicker, l) { FileKind = k };

    private static EventFieldSpec Drop(EventSlot s, string l, params string[] opts) =>
        new(s, EventEditor.Dropdown, l) { Options = opts, SaveIndex = true };

    private static EventFieldSpec DropText(EventSlot s, string l, params string[] opts) =>
        new(s, EventEditor.Dropdown, l) { Options = opts, SaveIndex = false };

    private static EventFieldSpec IntFlt(EventSlot s, string l) => new(s, EventEditor.IntAsFloat, l);

    private static EventFieldSpec BlnInt(EventSlot s, string l) => new(s, EventEditor.BoolAsInt, l);

    private static EventFieldSpec Flag(EventSlot s, string l) => new(s, EventEditor.FlagChar, l);

    private static EventFieldSpec[] F(params EventFieldSpec[] fields) => fields;

    private static EventLinkTarget[] L(params EventLinkTarget[] t) => t;

    private static IReadOnlyList<EventSchema> Build()
    {
        var e = new List<EventSchema>();

        void Add(
            string name, int id, string cat, EventFieldSpec[]? fields = null,
            EventLinkTarget[]? links = null, bool orient = false, bool forwards = true,
            bool placeable = true, string desc = "")
        {
            e.Add(new EventSchema(name, id, cat)
            {
                Fields = fields ?? Array.Empty<EventFieldSpec>(),
                LinkTargets = links ?? Array.Empty<EventLinkTarget>(),
                HasOrientation = orient,
                ForwardsSignal = forwards,
                Placeable = placeable,
                Description = desc,
                MinVersion = id >= 100 ? 300 : 0,
            });
        }

        // ==== STOCK — AI_Actions ==============================================
        Add("Attack", 38, "AI_Actions", links: L(EventLinkTarget.Entity), desc: "Order AI to attack the linked target.");
        Add("Drop_Point_Marker", 27, "AI_Actions", links: L(EventLinkTarget.Entity));
        Add("Drop_Weapon", 81, "AI_Actions", links: L(EventLinkTarget.Entity));
        Add("Follow_Waypoints", 28, "AI_Actions",
            F(Txt(EventSlot.Str1, "Waypoint list name (str1):"), Txt(EventSlot.Str2, "Follow method (str2):"),
              Bln(EventSlot.Bool1, "Face player (bool1):"), Bln(EventSlot.Bool2, "Run (bool2):")),
            L(EventLinkTarget.Entity));
        Add("Goto", 5, "AI_Actions",
            F(Bln(EventSlot.Bool1, "Face player (bool1):"), Bln(EventSlot.Bool2, "Run (bool2):"),
              BlnInt(EventSlot.Int1, "Face when finished (int1==1):"), BlnInt(EventSlot.Int2, "Override behaviour (int2==1):")),
            L(EventLinkTarget.Entity));
        Add("Goto_Player", 6, "AI_Actions", F(Bln(EventSlot.Bool1, "Run (bool1):")), L(EventLinkTarget.Entity));
        Add("Headlamp_State", 53, "AI_Actions", links: L(EventLinkTarget.Entity));
        Add("Holster_Weapon", 64, "AI_Actions", links: L(EventLinkTarget.Entity));
        Add("Holster_Player_Weapon", 65, "AI_Actions");
        Add("Look_At", 7, "AI_Actions", F(Int(EventSlot.Int1, "Look flags (int1):")), L(EventLinkTarget.Entity));
        Add("Play_Animation", 11, "AI_Actions",
            F(Txt(EventSlot.Str1, "Animation name (str1):"), Bln(EventSlot.Bool1, "Hold last frame (bool1):")),
            L(EventLinkTarget.Entity),
            desc: "A custom .mvf makes RED emit a Play_Custom_Animation instead.");
        Add("Set_AI_Mode", 34, "AI_Actions",
            F(Txt(EventSlot.Str1, "AI mode name (str1):"), Int(EventSlot.Int1, "Mode value (int1):")),
            L(EventLinkTarget.Entity), forwards: false);
        Add("Set_Friendliness", 30, "AI_Actions", F(Int(EventSlot.Int1, "Friendliness (int1):")), L(EventLinkTarget.Entity));
        Add("Shoot_At", 8, "AI_Actions", links: L(EventLinkTarget.Entity));
        Add("Shoot_Once", 9, "AI_Actions", F(Int(EventSlot.Int1, "Shot flags (int1):")), L(EventLinkTarget.Entity));

        // ==== STOCK — Level ===================================================
        Add("Alarm", 46, "Level", orient: true, desc: "Facility alarm; orientation = facing.");
        Add("Alarm_Siren", 45, "Level");
        Add("Cutscene", 55, "Level", links: L(EventLinkTarget.Event));
        Add("Enable_Navpoint", 69, "Level", links: L(EventLinkTarget.NavPoint));
        Add("Endgame", 71, "Level");
        Add("Explode", 10, "Level",
            F(Bln(EventSlot.Bool1, "Causes geomod (bool1):"), Flt(EventSlot.Float1, "Radius (float1):"),
              Flt(EventSlot.Float2, "Damage (float2):"), Fil(EventSlot.Str1, "VClip (str1):", EventFileKind.Vclip)));
        Add("Goal_Create", 35, "Level",
            F(Int(EventSlot.Int1, "Initial count (int1):"), Bln(EventSlot.Bool1, "Persistent (bool1):"),
              Txt(EventSlot.Str1, "Goal name (str1):")));
        Add("Goal_Check", 36, "Level",
            F(Txt(EventSlot.Str1, "Goal name (str1):"), Int(EventSlot.Int1, "Minimum count (int1):")),
            L(EventLinkTarget.Event));
        Add("Goal_Set", 37, "Level", F(Txt(EventSlot.Str1, "Goal name (str1):")));
        Add("Item_Pickup_State", 54, "Level", links: L(EventLinkTarget.Item));
        Add("Load_Level", 22, "Level",
            F(Txt(EventSlot.Str1, "Level filename (str1):"), Bln(EventSlot.Bool1, "Hard level break (bool1):")));
        Add("Message", 15, "Level",
            F(Int(EventSlot.Int1, "Message table index 0-63 (int1):"), IntFlt(EventSlot.Float1, "Voice/extra id (int in float1):"),
              Bln(EventSlot.Bool1, "Flag (bool1):"), Int(EventSlot.Int2, "Persona (int2):")),
            desc: "int1 = strings.tbl index; the third integer lives in float1 via __ftol; str1/str2 unused.");
        Add("Monitor_State", 49, "Level",
            F(Flt(EventSlot.Float1, "Value (float1):"), Int(EventSlot.Int1, "State (int1):"), Bln(EventSlot.Bool1, "Flag (bool1):")),
            L(EventLinkTarget.Monitor));
        Add("Music_Start", 41, "Level",
            F(Fil(EventSlot.Str1, "Music/WAV filename (str1):", EventFileKind.Sound), Bln(EventSlot.Bool2, "Loop / no fade (bool2):")),
            desc: "Uses bool2, NOT bool1.");
        Add("Music_Stop", 42, "Level");
        Add("Play_Sound", 0, "Level",
            F(Fil(EventSlot.Str1, "Sound file (str1):", EventFileKind.Sound), Fil(EventSlot.Str2, "Alt sound file (str2):", EventFileKind.Sound),
              Flt(EventSlot.Float1, "Min distance (float1):"), Flt(EventSlot.Float2, "Duration (float2):"),
              Bln(EventSlot.Bool1, "Looping (bool1):"), Uid(EventSlot.Int1, "Speaker UID (int1):")),
            L(EventLinkTarget.Object));
        Add("Play_Vclip", 70, "Level",
            F(Fil(EventSlot.Str1, "VClip name (str1):", EventFileKind.Vclip), Flt(EventSlot.Float1, "Value (float1):")),
            orient: true);
        Add("Slay_Object", 1, "Level", links: L(EventLinkTarget.Object));
        Add("Spawn_Object", 23, "Level",
            F(Txt(EventSlot.Str1, "Object type (str1):"), Txt(EventSlot.Str2, "Class (str2):"),
              Bln(EventSlot.Bool1, "Gravity (bool1):"), Flt(EventSlot.Float1, "Lifetime seconds (float1):")));
        Add("Swap_Textures", 33, "Level",
            F(Int(EventSlot.Int1, "First texture index (int1):"), Int(EventSlot.Int2, "Second texture index (int2):"),
              Fil(EventSlot.Str1, "First texture file (str1):", EventFileKind.Bitmap), Fil(EventSlot.Str2, "Second texture file (str2):", EventFileKind.Bitmap)));
        Add("Remove_Object", 2, "Level", links: L(EventLinkTarget.Object), forwards: false);

        // ==== STOCK — Modifiers ===============================================
        Add("Armor", 14, "Modifiers", F(Int(EventSlot.Int1, "Armor delta (int1):"), Bln(EventSlot.Bool1, "Apply to player (bool1):")), L(EventLinkTarget.Entity));
        Add("Black_Out_Player", 61, "Modifiers",
            F(Bln(EventSlot.Bool1, "Kill after (bool1):"), Bln(EventSlot.Bool2, "End level after (bool2):"), Flt(EventSlot.Float1, "Time (float1):")));
        Add("Bolt_State", 43, "Modifiers", links: L(EventLinkTarget.BoltEmitter));
        Add("Continuous_Damage", 17, "Modifiers",
            F(Int(EventSlot.Int1, "Damage per second (int1):"), Int(EventSlot.Int2, "Damage type (int2):")), L(EventLinkTarget.Object));
        Add("Detach", 58, "Modifiers", links: L(EventLinkTarget.Object));
        Add("Clear_Endgame_If_Killed", 67, "Modifiers", links: L(EventLinkTarget.Entity));
        Add("Force_Monitor_Update", 60, "Modifiers", links: L(EventLinkTarget.Monitor));
        Add("Fog_State", 57, "Modifiers");
        Add("Give_Item_To_Player", 19, "Modifiers", F(Txt(EventSlot.Str1, "Item class (str1):")));
        Add("Go_Undercover", 47, "Modifiers");
        Add("Heal", 13, "Modifiers", F(Int(EventSlot.Int1, "Life delta (int1):"), Bln(EventSlot.Bool1, "Apply to player (bool1):")), L(EventLinkTarget.Entity));
        Add("Ignite_Entity", 82, "Modifiers", links: L(EventLinkTarget.Entity));
        Add("Make_Invulnerable", 24, "Modifiers", F(Flt(EventSlot.Float1, "Duration (float1):")), L(EventLinkTarget.Entity));
        Add("Make_Fly", 26, "Modifiers", links: L(EventLinkTarget.Entity));
        Add("Make_Walk", 25, "Modifiers", links: L(EventLinkTarget.Entity));
        Add("Modify_Rotating_Mover", 66, "Modifiers",
            F(DropText(EventSlot.Str1, "Mode (str1):", "Increase", "Decrease"), Flt(EventSlot.Float1, "Amount (float1):")),
            L(EventLinkTarget.Mover), desc: "str1 compared to the keyword \"Increase\".");
        Add("Mover_Pause", 72, "Modifiers", links: L(EventLinkTarget.Mover));
        Add("Particle_State", 39, "Modifiers", links: L(EventLinkTarget.ParticleEmitter), forwards: false);
        Add("Push_Region_State", 51, "Modifiers", links: L(EventLinkTarget.PushRegion));
        Add("Reverse_Mover", 89, "Modifiers", F(Bln(EventSlot.Bool1, "Only if moving forward (bool1):")), L(EventLinkTarget.Mover));
        Add("Set_Gravity", 44, "Modifiers", F(Flt(EventSlot.Float1, "Gravity (float1):")));
        Add("Set_Liquid_Depth", 40, "Modifiers",
            F(Flt(EventSlot.Float1, "Target depth (float1):"), Flt(EventSlot.Float2, "Duration seconds (float2):")),
            L(EventLinkTarget.Room), desc: "Alpine-parity leaf; drives the room's liquid depth over time.");
        Add("Shake_Player", 18, "Modifiers", F(Flt(EventSlot.Float1, "Magnitude (float1):"), Flt(EventSlot.Float2, "Time (float2):")));
        Add("Skybox_State", 59, "Modifiers",
            F(Flag(EventSlot.Str1, "On (str1[0]):"), Flt(EventSlot.Float1, "Value (float1):")),
            desc: "str1's first character is a flag; serialize a 1-char string.");
        Add("Strip_Player_Weapons", 56, "Modifiers");
        Add("Switch_Model", 21, "Modifiers",
            F(Fil(EventSlot.Str1, "Model filename (str1):", EventFileKind.Mesh), Bln(EventSlot.Bool1, "Flag (bool1):")),
            L(EventLinkTarget.Object));
        Add("Teleport", 4, "Modifiers", orient: true, links: L(EventLinkTarget.Object), desc: "Teleports linked objects to the event pos + orientation.");
        Add("Teleport_Player", 63, "Modifiers", orient: true);
        Add("Turn_Off_Physics", 62, "Modifiers", links: L(EventLinkTarget.Object));
        Add("UnHide", 50, "Modifiers", links: L(EventLinkTarget.Object), forwards: false);

        // ==== STOCK — Catalysts ==============================================
        Add("Cyclic_Timer", 20, "Catalysts",
            F(Flt(EventSlot.Float1, "Interval seconds (float1):"), Int(EventSlot.Int1, "Max sends (int1):"), Bln(EventSlot.Bool1, "Forever (bool1):")),
            L(EventLinkTarget.Event));
        Add("Delay", 48, "Catalysts", links: L(EventLinkTarget.Event), forwards: false, desc: "Forwards after the base delay.");
        Add("Invert", 3, "Catalysts", links: L(EventLinkTarget.Event), forwards: false);
        Add("Switch", 32, "Catalysts", F(Bln(EventSlot.Bool1, "Initial state (bool1):")), L(EventLinkTarget.Event), forwards: false);
        Add("When_Countdown_Over", 75, "Catalysts", links: L(EventLinkTarget.Event));
        Add("When_Countdown_Reaches", 84, "Catalysts", F(Int(EventSlot.Int1, "Seconds (int1):")), L(EventLinkTarget.Event));
        Add("When_Cutscene_Over", 83, "Catalysts", links: L(EventLinkTarget.Event));
        Add("When_Dead", 16, "Catalysts", F(Bln(EventSlot.Bool1, "Any (true) / All (false) (bool1):")), L(EventLinkTarget.Entity));
        Add("When_Enter_Vehicle", 77, "Catalysts", links: L(EventLinkTarget.Event));
        Add("When_Try_Exit_Vehicle", 78, "Catalysts", links: L(EventLinkTarget.Event));
        Add("When_Hit", 52, "Catalysts", links: L(EventLinkTarget.Object));
        Add("When_Life_Reaches", 87, "Catalysts", F(Int(EventSlot.Int1, "Life threshold (int1):")), L(EventLinkTarget.Entity));
        Add("When_Armor_Reaches", 88, "Catalysts", F(Int(EventSlot.Int1, "Armor threshold (int1):")), L(EventLinkTarget.Entity));

        // ==== STOCK — Special =================================================
        Add("Activate_Capek_Shield", 76, "Special", links: L(EventLinkTarget.Object));
        Add("Countdown_Begin", 73, "Special", F(Int(EventSlot.Int1, "Countdown seconds (int1):")));
        Add("Countdown_End", 74, "Special");
        Add("Display_Fullscreen_Image", 85, "Special",
            F(Fil(EventSlot.Str1, "Image filename (str1):", EventFileKind.Bitmap), Flt(EventSlot.Float1, "Duration (float1):")));
        Add("Defuse_Nuke", 86, "Special");
        Add("Fire_Weapon_No_Anim", 79, "Special", links: L(EventLinkTarget.Entity));
        Add("Never_Leave_Vehicle", 80, "Special", links: L(EventLinkTarget.Entity));
        Add("Win_PS2_Demo", 68, "Special");

        // ==== STOCK — non-placeable (auto/legacy) ============================
        Add("Play_Custom_Animation", 12, "AI_Actions",
            F(Fil(EventSlot.Str1, "MVF file (str1):", EventFileKind.Mvf), Bln(EventSlot.Bool1, "Hold last frame (bool1):"),
              Bln(EventSlot.Bool2, "Is action (bool2):")),
            L(EventLinkTarget.Entity), placeable: false, desc: "Auto-emitted by Play_Animation when a custom .mvf is set.");
        Add("Follow_Player", 29, "AI_Actions", links: L(EventLinkTarget.Entity), placeable: false, desc: "Legacy duplicate of Goto_Player.");
        Add("Set_Light_State", 31, "Modifiers", links: L(EventLinkTarget.Light), placeable: false, desc: "No stock browser leaf; Alpine ships Light_State.");

        // ==== ALPINE — AF_General ============================================
        Add("Clone_Entity", 101, "AF_General",
            F(Bln(EventSlot.Bool1, "Clone is hostile to player (bool1):"), Bln(EventSlot.Bool2, "Go to player (bool2):"),
              Uid(EventSlot.Int1, "Link event UID to clone (int1):")),
            L(EventLinkTarget.Entity), orient: true);
        Add("Set_Player_World_Collide", 102, "AF_General");
        Add("HUD_Message", 105, "AF_General",
            F(Txt(EventSlot.Str1, "Message text (str1):"), Flt(EventSlot.Float1, "Duration (float1):")));
        Add("Play_Video", 106, "AF_General", F(Fil(EventSlot.Str1, "Video filename (str1):", EventFileKind.Video)));
        Add("Set_Level_Hardness", 107, "AF_General", F(Int(EventSlot.Int1, "Hardness (int1):")));
        Add("Force_Unhide", 119, "AF_General", links: L(EventLinkTarget.Object));
        Add("Set_Difficulty", 120, "AF_General", F(Drop(EventSlot.Int1, "Difficulty (int1):", "Easy", "Medium", "Hard", "Impossible")));
        Add("Set_Fog_Far_Clip", 121, "AF_General", F(Flt(EventSlot.Float1, "Far clip distance (float1):")));
        Add("Set_Skybox", 125, "AF_General",
            F(Uid(EventSlot.Int1, "Skybox room UID (int1):"), Uid(EventSlot.Int2, "Eye anchor UID (int2):"),
              Bln(EventSlot.Bool1, "Use relative position (bool1):"), Flt(EventSlot.Float1, "Relative position scale (float1):")));
        Add("Set_Life", 126, "AF_General", F(Flt(EventSlot.Float1, "New life value (float1):")), L(EventLinkTarget.Entity));
        Add("Set_Debris", 127, "AF_General",
            F(Fil(EventSlot.Str1, "Debris filename (str1):", EventFileKind.Mesh), Int(EventSlot.Int1, "Explosion VClip index (int1):"),
              Flt(EventSlot.Float1, "Explosion VClip radius (float1):"), Txt(EventSlot.Str2, "Debris sound set (str2):"),
              Flt(EventSlot.Float2, "Debris velocity (float2):")));
        Add("Set_Fog_Color", 128, "AF_General", F(Txt(EventSlot.Str1, "Fog color RGB (str1):")));
        Add("Set_Entity_Flag", 129, "AF_General",
            F(Drop(EventSlot.Int1, "Flag to set (int1):", "Boarded (vehicles only)", "Cower from weapon", "Question unarmed player",
                "Fade corpse immediately", "Don't hum", "No shadow", "Perfect aim", "Permanent corpse", "Always face player",
                "Only attack player", "Deaf", "Ignore terrain when firing")),
            L(EventLinkTarget.Entity));
        Add("AF_Teleport_Player", 130, "AF_General",
            F(Bln(EventSlot.Bool1, "Reset player velocity (bool1):"), Bln(EventSlot.Bool2, "Eject player from vehicle (bool2):"),
              Fil(EventSlot.Str1, "Entrance VClip (str1):", EventFileKind.Vclip), Fil(EventSlot.Str2, "Exit VClip (str2):", EventFileKind.Vclip)),
            orient: true);
        Add("Set_Item_Drop", 131, "AF_General", F(Txt(EventSlot.Str1, "Item class to drop (str1):")), L(EventLinkTarget.Entity));
        Add("AF_Heal", 132, "AF_General",
            F(Int(EventSlot.Int1, "Amount (int1):"),
              Drop(EventSlot.Int2, "Apply change to (int2):", "Linked entities", "Triggering player", "All players", "Teammates",
                "Enemy team", "Players on red team", "Players on blue team", "Players in linked triggers"),
              Bln(EventSlot.Bool1, "Apply to armor instead (bool1):"), Bln(EventSlot.Bool2, "Allow super values (bool2):")),
            L(EventLinkTarget.Entity));
        Add("World_HUD_Sprite", 135, "AF_General",
            F(Bln(EventSlot.Bool1, "Start enabled (bool1):"),
              Drop(EventSlot.Int1, "Render mode (int1):", "No Overdraw", "No Overdraw (Glow)", "Overdraw"),
              Flt(EventSlot.Float1, "Render scale (float1):"), Fil(EventSlot.Str1, "Sprite filename (str1):", EventFileKind.Bitmap),
              Fil(EventSlot.Str2, "Sprite filename blue (str2):", EventFileKind.Bitmap)));
        Add("Set_Light_Color", 136, "AF_General",
            F(Txt(EventSlot.Str1, "Light color RGB (str1):"), Bln(EventSlot.Bool1, "Random color instead (bool1):")),
            L(EventLinkTarget.Light));
        Add("Modify_Respawn_Point", 139, "AF_General",
            F(Bln(EventSlot.Bool1, "Red team (bool1):"), Bln(EventSlot.Bool2, "Blue team (bool2):")),
            L(EventLinkTarget.RespawnPoint));
        Add("Set_Capture_Point_Owner", 141, "AF_General", F(Drop(EventSlot.Int1, "Owner (int1):", "Neutral", "Red", "Blue")));
        Add("Mesh_Animate", 145, "AF_General",
            F(Drop(EventSlot.Int1, "Type (int1):", "Action", "Action Hold Last", "State"),
              Fil(EventSlot.Str1, "Animation filename (str1):", EventFileKind.Animation), Flt(EventSlot.Float1, "Blend weight (float1):")));
        Add("Mesh_Set_Texture", 146, "AF_General",
            F(Int(EventSlot.Int1, "Texture slot (int1):"), Fil(EventSlot.Str1, "Texture filename (str1):", EventFileKind.Bitmap)));
        Add("Mesh_Set_Collision", 147, "AF_General", F(Drop(EventSlot.Int1, "Collision type (int1):", "None", "Only Weapons", "All")));
        Add("AF_Fullscreen_Image", 148, "AF_General",
            F(Fil(EventSlot.Str1, "Image filename (str1):", EventFileKind.Bitmap), Flt(EventSlot.Float1, "Hold seconds (0=forever) (float1):"),
              Flt(EventSlot.Float2, "Transition seconds (float2):"),
              Drop(EventSlot.Int1, "Transition type (int1):", "Instant", "Fade In + Instant Out", "Fade In + Fade Out", "Instant In + Fade Out"),
              Int(EventSlot.Int2, "Alpha at max (int2):")));
        Add("AF_Fullscreen_Color", 149, "AF_General",
            F(Txt(EventSlot.Str1, "RGB color (str1):"), Flt(EventSlot.Float1, "Hold seconds (0=forever) (float1):"),
              Flt(EventSlot.Float2, "Transition seconds (float2):"),
              Drop(EventSlot.Int1, "Transition type (int1):", "Instant", "Fade In + Instant Out", "Fade In + Fade Out", "Instant In + Fade Out"),
              Int(EventSlot.Int2, "Alpha at max (int2):")));
        Add("Unhide_Glare", 150, "AF_General", links: L(EventLinkTarget.Object));
        Add("Gas_Region_State", 151, "AF_General", links: L(EventLinkTarget.Object));
        Add("Modify_Gas_Region", 152, "AF_General",
            F(Txt(EventSlot.Str1, "RGB color (str1):"), Flt(EventSlot.Float1, "Density (float1):"), Flt(EventSlot.Float2, "Transition time (float2):")));
        Add("Resize_Gas_Region", 153, "AF_General",
            F(Drop(EventSlot.Int1, "Shape (int1):", "Sphere", "Box"), Flt(EventSlot.Float1, "Sphere radius (float1):"),
              Txt(EventSlot.Str1, "Box size HWD (str1):"), Flt(EventSlot.Float2, "Transition time (float2):")));
        Add("ATX_Set_Frame", 154, "AF_General", F(Txt(EventSlot.Str1, "ATX handle (str1):"), Int(EventSlot.Int1, "Frame index (int1):")));
        Add("ATX_Play", 155, "AF_General", F(Txt(EventSlot.Str1, "ATX handle (str1):")));
        Add("ATX_Pause", 156, "AF_General", F(Txt(EventSlot.Str1, "ATX handle (str1):")));
        Add("ATX_Set_Frame_Time", 157, "AF_General", F(Txt(EventSlot.Str1, "ATX handle (str1):"), Int(EventSlot.Int1, "Frame time ms (int1):")));

        // ==== ALPINE — AF_Flow ===============================================
        Add("Sequence", 108, "AF_Flow", F(Int(EventSlot.Int1, "Next index to activate (int1):")), L(EventLinkTarget.Event));
        Add("Switch_Random", 103, "AF_Flow", F(Bln(EventSlot.Bool1, "No repeats until all used (bool1):")), L(EventLinkTarget.Event));
        Add("Difficulty_Gate", 104, "AF_Flow", F(Drop(EventSlot.Int1, "Difficulty (int1):", "Easy", "Medium", "Hard", "Impossible")), L(EventLinkTarget.Event));
        Add("Route_Node", 111, "AF_Flow",
            F(Drop(EventSlot.Int1, "Node behavior (int1):", "Pass through", "Drop", "Invert", "Force on", "Force off"),
              Bln(EventSlot.Bool1, "Non-retriggerable delay (bool1):"), Bln(EventSlot.Bool2, "Clear trigger info (bool2):")),
            L(EventLinkTarget.Event));
        Add("Valid_Gate", 113, "AF_Flow", F(Uid(EventSlot.Int1, "Object UID to test (int1):")), L(EventLinkTarget.Event));
        Add("Goal_Gate", 115, "AF_Flow",
            F(Txt(EventSlot.Str1, "Goal to test (str1):"),
              Drop(EventSlot.Int1, "Test to run (int1):", "Equal to", "Not equal to", "Greater than", "Less than",
                "Greater than or equal to", "Less than or equal to", "Is odd", "Is even", "Divisible by", "Less than initial value",
                "Greater than initial value", "Less or equal initial value", "Greater or equal initial value", "Equal to initial value"),
              Int(EventSlot.Int2, "Value to test against (int2):")),
            L(EventLinkTarget.Event));
        Add("Scope_Gate", 116, "AF_Flow",
            F(Drop(EventSlot.Int1, "Scope to test against (int1):", "Multiplayer", "Single player", "Server", "Dedicated server",
                "Client", "Triggering player", "Blue team (spawned)", "Red team (spawned)", "Player that has flag", "Blue team",
                "Red team", "D3D11 renderer", "D3D8/9 renderer")),
            L(EventLinkTarget.Event));
        Add("Inside_Gate", 117, "AF_Flow",
            F(Uid(EventSlot.Int1, "UID (trigger/room) to check (int1):"),
              Drop(EventSlot.Int2, "What to check for (int2):", "Player", "Entity that triggered this", "All linked objects", "At least 1 linked object")),
            L(EventLinkTarget.Event));
        Add("Gametype_Gate", 123, "AF_Flow",
            F(Drop(EventSlot.Int1, "Check for gametype (int1):", "Deathmatch", "Capture the Flag", "Team Deathmatch", "King of the Hill",
                "Damage Control", "Revolt", "Run", "Escalation")),
            L(EventLinkTarget.Event));
        Add("Owner_Gate", 142, "AF_Flow",
            F(Uid(EventSlot.Int1, "Handler UID (int1):"), Drop(EventSlot.Int2, "Required owner (int2):", "Neutral", "Red", "Blue")),
            L(EventLinkTarget.Event));

        // ==== ALPINE — AF_Utility ============================================
        Add("Set_Variable", 100, "AF_Utility",
            F(Drop(EventSlot.Int1, "Variable handle (int1):", "delay", "int1", "int2", "float1", "float2", "bool1", "bool2", "str1", "str2"),
              Int(EventSlot.Int2, "Value for int1 or int2 (int2):"), Flt(EventSlot.Float1, "Value for delay/float1/float2 (float1):"),
              Bln(EventSlot.Bool1, "Value for bool1 or bool2 (bool1):"), Txt(EventSlot.Str1, "Value for str1 or str2 (str1):")),
            L(EventLinkTarget.Event));
        Add("Clear_Queued", 109, "AF_Utility", links: L(EventLinkTarget.Event));
        Add("Remove_Link", 110, "AF_Utility", F(Bln(EventSlot.Bool1, "Purge all links (bool1):")), L(EventLinkTarget.Event));
        Add("Add_Link", 112, "AF_Utility",
            F(Uid(EventSlot.Int1, "Source event UID (int1):"), Bln(EventSlot.Bool1, "Link inbound (bool1):")), L(EventLinkTarget.Event));
        Add("Goal_Math", 114, "AF_Utility",
            F(Txt(EventSlot.Str1, "Goal to edit (str1):"),
              Drop(EventSlot.Int1, "Operation to perform (int1):", "Add to goal", "Subtract from goal", "Multiply by goal", "Divide goal by",
                "Divide by goal", "Set goal to", "Modulo goal by", "Raise goal to power", "Negate goal", "Absolute value of goal",
                "Max of goal and value", "Min of goal and value", "Reset goal to initial value"),
              Int(EventSlot.Int2, "Value to use for operation (int2):")));
        Add("Anchor_Marker", 118, "AF_Utility");
        Add("Anchor_Marker_Orient", 133, "AF_Utility", orient: true, desc: "Orientation stored at RFL version ≥ 0x12D (301).");
        Add("Light_State", 134, "AF_Utility", links: L(EventLinkTarget.Light));
        Add("Respawn_Point_State", 138, "AF_Utility", links: L(EventLinkTarget.RespawnPoint));

        // ==== ALPINE — AF_Catalysts ==========================================
        Add("AF_When_Dead", 122, "AF_Catalysts", F(Bln(EventSlot.Bool1, "Activate on any dead (bool1):")), L(EventLinkTarget.Entity));
        Add("When_Picked_Up", 124, "AF_Catalysts", links: L(EventLinkTarget.Item));
        Add("When_Captured", 140, "AF_Catalysts", links: L(EventLinkTarget.Event));
        Add("When_Round_Ends", 144, "AF_Catalysts", links: L(EventLinkTarget.Event));

        // ==== ALPINE — AF_Gameplay ===========================================
        Add("Capture_Point_Handler", 137, "AF_Gameplay",
            F(Txt(EventSlot.Str1, "Name (str1):"), Flt(EventSlot.Float1, "Outline offset (float1):"), Flt(EventSlot.Float2, "Cap rate multiplier (float2):"),
              Int(EventSlot.Int1, "Stage (used in REV) (int1):"),
              Drop(EventSlot.Int2, "Position (used in ESC) (int2):", "Basic/Center", "Red base", "Blue base", "Red forward", "Blue forward"),
              Bln(EventSlot.Bool1, "Cylindrical trigger (bool1):")));
        Add("Set_Gameplay_Rule", 143, "AF_Gameplay", F(Drop(EventSlot.Int1, "Rule to set (int1):", "Player has headlamp")));

        return e;
    }
}
