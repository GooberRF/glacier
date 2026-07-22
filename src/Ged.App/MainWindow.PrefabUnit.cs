using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Ged.App.Viewport;
using Ged.Core.Editing;
using Ged.Core.Input;
using Ged.Rendering.Picking;
using Ged.Rendering.Scene;
using CoreVec3 = Ged.Core.Model.Vec3;
using LineSegment = Ged.Rendering.Scene.LineSegment;

namespace Ged.App;

/// <summary>
/// Feature F — prefab-instance UNIT selection. Clicking any member of a tracked, non-orphaned
/// instance selects the whole instance as a unit; double-click (or the padlock badge) enters the
/// instance for member editing; ESC / empty-click / the badge exits. The gizmo + keyboard drive the
/// whole unit as one rigid body through <see cref="PrefabUnitController"/> (Core). The unit padlock
/// badge is a screen-constant, line-drawn overlay glyph, CPU-picked through the pick ray — closed in
/// unit mode ("Edit prefab members"), open while inside ("Lock prefab as unit"). This is NOT the
/// Q-lock (Feature G): distinct mechanism, distinct wording.
/// </summary>
public sealed partial class MainWindow
{
    private PrefabUnitController? _prefabUnit;

    // Double-click detection for member "enter" (the native viewport has no double-tap event, so
    // we time consecutive picks that resolve to the same instance).
    private int _lastMemberClickInstance = -1;
    private long _lastMemberClickTicks;
    private const long DoubleClickMs = 400;

    // Screen-constant padlock badge sizing (pixels → world via the pane's world-per-pixel). The
    // badge sits up-and-right of the pivot so it clears the vertical gizmo axis arrow.
    private const float BadgeHalfPixels = 9f;
    private const float BadgeOffsetUpPixels = 46f;
    private const float BadgeOffsetRightPixels = 34f;
    private const float BadgePickPixels = 16f;

    /// <summary>True when an instance is unit-selected (its members drive the unit gizmo).</summary>
    internal bool PrefabUnitActive => _prefabUnit?.UnitRecord is not null;

    /// <summary>Creates the prefab-unit controller for the freshly-subscribed document.</summary>
    private void InitPrefabUnit()
    {
        _prefabUnit = Document is { } doc && _prefabInstances is { } inst && _session.BrushEditor is { } be
            ? new PrefabUnitController(inst, doc, be, _session.Selection)
            : null;
        _lastMemberClickInstance = -1;
    }

    // ---- Click handling (called from OnPicked, after the eyedropper) ----------

    /// <summary>
    /// Prefab-unit click handling. Returns true when the click was consumed (badge toggle, unit
    /// select, member enter, or an entered-mode empty-click exit) — the caller then stops. Returns
    /// false to fall through to normal per-kind selection (non-member hits, or member hits while
    /// inside that instance).
    /// </summary>
    private bool HandlePrefabPick(IViewportSurface surface, PickId id, bool additive)
    {
        if (_prefabUnit is null || Document is null)
        {
            return false;
        }

        // 1) The padlock badge (CPU-picked against the pick ray) toggles enter/exit.
        if ((_prefabUnit.UnitInstanceId is not null || _prefabUnit.EnteredInstanceId is not null) &&
            TryPrefabBadgeHit(surface))
        {
            if (_prefabUnit.EnteredInstanceId is not null)
            {
                ExitPrefabMember();
            }
            else if (_prefabUnit.UnitInstanceId is int unit)
            {
                EnterPrefabMember(unit, memberToSelect: -1);
            }

            return true;
        }

        // 2) A hit on a tracked member → unit select / double-click enter.
        int memberUid = id.Kind is PickKind.Object or PickKind.Mesh or PickKind.Brush ? id.Index : -1;
        if (memberUid >= 0 && _prefabUnit.MemberInstance(memberUid) is { } rec)
        {
            // While already inside this instance, members select individually (normal path).
            if (_prefabUnit.EnteredInstanceId == rec.InstanceId)
            {
                return false;
            }

            // Gate on the clicked member's kind under the current chips — only escalate to a unit
            // when that kind is itself selectable now (brief). Brush members need Brushes (or
            // Groups); object members need Objects (or Groups).
            bool isBrushMember = BrushEd?.FindBrush(memberUid) is not null;
            SelectKinds active = _filter.Active;
            bool selectable = isBrushMember
                ? (active & (SelectKinds.Brushes | SelectKinds.Groups)) != 0
                : (active & (SelectKinds.Objects | SelectKinds.Groups)) != 0;
            if (!selectable)
            {
                return false; // clicked kind not selectable in this mode → normal (no-op) handling
            }

            long now = Environment.TickCount64;
            bool doubleClick = _lastMemberClickInstance == rec.InstanceId && (now - _lastMemberClickTicks) < DoubleClickMs;
            _lastMemberClickInstance = rec.InstanceId;
            _lastMemberClickTicks = now;

            switch (_prefabUnit.ClickMember(memberUid, doubleClick))
            {
                case PrefabUnitController.ClickOutcome.EnteredMember:
                    EnterPrefabMember(rec.InstanceId, memberUid);
                    return true;
                case PrefabUnitController.ClickOutcome.UnitSelected:
                    LastPickHighlight = PickId.None;
                    UpdateGizmoState();
                    RefreshSelectionOverlay();
                    _properties.Refresh();
                    _dispatcher.ShowMessage($"Prefab instance {rec.InstanceId} selected as a unit — double-click a member to edit inside.");
                    return true;
                case PrefabUnitController.ClickOutcome.UnitBlockedLocked:
                    _notifications.Notify(Services.NotificationSeverity.Hint,
                        "Prefab instance has a locked member — unlock it to select the instance.");
                    return true;
                case PrefabUnitController.ClickOutcome.NotHandled:
                    return false;
            }
        }

        // 3) Empty-space click while inside an instance exits back to unit level (brief).
        if (_prefabUnit.EnteredInstanceId is not null && id.IsNone)
        {
            ExitPrefabMember();
            return true;
        }

        // 4) Clicking OTHER geometry while inside leaves member mode; normal selection then handles
        //    the click (selects whatever was hit) rather than snapping back to the unit.
        if (_prefabUnit.EnteredInstanceId is not null)
        {
            _prefabUnit.Reset();
            return false;
        }

        // 5) A non-member click clears any stale unit state, then normal handling proceeds.
        if (_prefabUnit.UnitInstanceId is not null)
        {
            _prefabUnit.Reset();
        }

        return false;
    }

    private void EnterPrefabMember(int instanceId, int memberToSelect)
    {
        _prefabUnit!.Enter(instanceId);

        // Enter member mode with a clean slate; the double-clicked member (if any) becomes the
        // individual selection so it can be edited straight away.
        _session.Selection.ClearAll();
        if (memberToSelect >= 0)
        {
            SelectMemberIndividually(memberToSelect);
        }

        UpdateGizmoState();
        RefreshSelectionOverlay();
        _properties.Refresh();
        _dispatcher.ShowMessage($"Editing prefab instance {instanceId} — ESC to exit");
    }

    private void ExitPrefabMember()
    {
        int? was = _prefabUnit!.EnteredInstanceId;
        _prefabUnit.ExitToUnit();
        UpdateGizmoState();
        RefreshSelectionOverlay();
        _properties.Refresh();
        if (was is int id)
        {
            _dispatcher.ShowMessage($"Prefab instance {id} locked as a unit.");
        }
    }

    /// <summary>Selects one member individually (brush or object) through the router.</summary>
    private void SelectMemberIndividually(int memberUid)
    {
        if (BrushEd?.FindBrush(memberUid) is not null)
        {
            _session.Selection.SelectBrush(memberUid);
        }
        else if (Document?.FindByUid(memberUid) is { } o)
        {
            _session.Selection.SelectObject(o);
        }
    }

    /// <summary>ESC exits member editing back to unit level (consumed before command dispatch).</summary>
    private bool TryPrefabExitKey(KeyGesture gesture)
    {
        if (gesture.Key == "Escape" && _prefabUnit?.EnteredInstanceId is not null)
        {
            ExitPrefabMember();
            return true;
        }

        return false;
    }

    /// <summary>Combined viewport key pre-dispatch: prefab exit first, then Face-mode texture keys.</summary>
    private bool ViewportKeyPreDispatch(KeyGesture gesture) => TryPrefabExitKey(gesture) || TryTextureModeKey(gesture);

    // ---- Padlock badge glyph + CPU picking ------------------------------------

    /// <summary>
    /// Computes the badge's world center, camera-facing basis and sizes for the active instance
    /// (entered instance wins over unit, so the "open" glyph shows while inside). Returns false when
    /// no instance is active / no camera.
    /// </summary>
    private bool TryPrefabBadge(IViewportSurface s, out Vector3 center, out Vector3 right, out Vector3 up, out float half, out float pickRadius, out bool open)
    {
        center = right = up = default;
        half = pickRadius = 0f;
        open = false;
        if (_prefabUnit is null || _prefabInstances is null)
        {
            return false;
        }

        int? id = _prefabUnit.EnteredInstanceId ?? _prefabUnit.UnitInstanceId;
        if (id is not int iid || _prefabInstances.ById(iid) is not { } rec || s.Camera is not { } cam)
        {
            return false;
        }

        CoreVec3 p = rec.PivotPosition;
        var pivot = new Vector3(p.X, p.Y, p.Z);
        float wpp = cam.WorldPerPixel(pivot, s.SurfaceHeight);
        right = SafeNormalize(cam.Right);
        up = SafeNormalize(cam.Up);
        half = BadgeHalfPixels * wpp;
        center = pivot + (up * (BadgeOffsetUpPixels * wpp)) + (right * (BadgeOffsetRightPixels * wpp));
        pickRadius = BadgePickPixels * wpp;
        open = _prefabUnit.EnteredInstanceId is not null;
        return true;
    }

    /// <summary>Tests the pick ray against the active instance's badge glyph.</summary>
    private bool TryPrefabBadgeHit(IViewportSurface s)
    {
        if (!TryPrefabBadge(s, out Vector3 center, out _, out _, out _, out float pickRadius, out _) ||
            s.LastPickRay is not (Vector3 ro, Vector3 rd))
        {
            return false;
        }

        return RayHitsPoint(center, ro, rd, pickRadius);
    }

    /// <summary>Closest-approach ray-vs-point hit test (screen-constant tolerance in world units).</summary>
    private static bool RayHitsPoint(Vector3 p, Vector3 ro, Vector3 rd, float tol)
    {
        float dd = Vector3.Dot(rd, rd);
        if (dd < 1e-12f)
        {
            return false;
        }

        float t = Vector3.Dot(p - ro, rd) / dd;
        if (t < 0f)
        {
            return false; // behind the camera
        }

        Vector3 closest = ro + (rd * t);
        return Vector3.Distance(p, closest) <= tol;
    }

    /// <summary>The padlock badge line set for the active instance (closed = unit, open = inside).</summary>
    private IEnumerable<LineSegment> BuildPrefabBadge()
    {
        IViewportSurface s = _viewportGrid.ActiveSurface;
        if (!TryPrefabBadge(s, out Vector3 center, out Vector3 right, out Vector3 up, out float half, out _, out bool open))
        {
            return Array.Empty<LineSegment>();
        }

        uint color = open ? Palette.Rgba(120, 230, 140) : Palette.Rgba(255, 200, 60);
        return PadlockGlyph(center, right, up, half, open, color);
    }

    private static List<LineSegment> PadlockGlyph(Vector3 center, Vector3 right, Vector3 up, float half, bool open, uint color)
    {
        var lines = new List<LineSegment>();
        float bw = half;
        float bh = half * 0.85f;
        Vector3 bl = center - (right * bw) - (up * bh);
        Vector3 br = center + (right * bw) - (up * bh);
        Vector3 tr = center + (right * bw) + (up * bh);
        Vector3 tl = center - (right * bw) + (up * bh);
        lines.Add(new LineSegment(bl, br, color));
        lines.Add(new LineSegment(br, tr, color));
        lines.Add(new LineSegment(tr, tl, color));
        lines.Add(new LineSegment(tl, bl, color));

        // Keyhole tick on the body.
        lines.Add(new LineSegment(center, center - (up * (bh * 0.5f)), color));

        // Shackle: a semicircular arc bridging the body top. Open = lifted/ajar (shifted off one leg).
        Vector3 arcCenter = center + (up * (bh + (half * 0.15f)));
        Vector3 off = open ? (right * (half * 0.55f)) + (up * (half * 0.35f)) : Vector3.Zero;
        float r = half * 0.6f;
        const int seg = 10;
        Vector3? prev = null;
        for (int i = 0; i <= seg; i++)
        {
            float a = MathF.PI * (i / (float)seg); // 0 → π, left leg to right leg over the top
            Vector3 pt = arcCenter + off - (right * (MathF.Cos(a) * r)) + (up * (MathF.Sin(a) * r));
            if (prev is Vector3 pv)
            {
                lines.Add(new LineSegment(pv, pt, color));
            }

            prev = pt;
        }

        return lines;
    }
}
