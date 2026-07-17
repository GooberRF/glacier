using System;
using System.Collections.Generic;
using Ged.Core.Editor;
using Ged.Core.Model;

namespace Ged.Core.Editing;

/// <summary>
/// The prefab-instance UNIT-selection state machine (Feature F). In normal editing modes a click
/// on any member of a tracked, non-orphaned instance selects the WHOLE instance as one unit (all
/// member brushes + objects), and a rigid move/rotate drives every member together while keeping
/// the instance's authoritative pose record fresh. Double-clicking a member (or the padlock badge)
/// ENTERS the instance for individual member editing; ESC / empty-click / re-clicking the badge
/// exits back to unit level. Entry state is transient — never persisted.
///
/// <para>Pure of UI: it operates on the Core document/brush/instance services and the mandatory
/// <see cref="SelectionRouter"/>, so the whole flow is unit-testable without a viewport.</para>
/// </summary>
public sealed class PrefabUnitController
{
    private readonly PrefabInstanceService _instances;
    private readonly EditorDocument _doc;
    private readonly BrushEditor _brushes;
    private readonly SelectionRouter _router;

    public PrefabUnitController(PrefabInstanceService instances, EditorDocument doc, BrushEditor brushes, SelectionRouter router)
    {
        _instances = instances ?? throw new ArgumentNullException(nameof(instances));
        _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        _brushes = brushes ?? throw new ArgumentNullException(nameof(brushes));
        _router = router ?? throw new ArgumentNullException(nameof(router));
    }

    /// <summary>The instance currently selected as a unit (all members), or null.</summary>
    public int? UnitInstanceId { get; private set; }

    /// <summary>The instance currently ENTERED for member-level editing (transient), or null.</summary>
    public int? EnteredInstanceId { get; private set; }

    /// <summary>The unit-selected instance's live record (null when none / it was removed).</summary>
    public PrefabInstanceRecord? UnitRecord => UnitInstanceId is int id ? _instances.ById(id) : null;

    /// <summary>The tracked instance a member UID belongs to, or null (not a member).</summary>
    public PrefabInstanceRecord? MemberInstance(int uid) => _instances.InstanceOfMember(uid);

    /// <summary>What a member click did.</summary>
    public enum ClickOutcome
    {
        /// <summary>Not a tracked member, or already inside this instance — normal selection applies.</summary>
        NotHandled,

        /// <summary>The whole instance was selected as a unit.</summary>
        UnitSelected,

        /// <summary>Double-click entered the instance for member editing.</summary>
        EnteredMember,

        /// <summary>The instance has a locked member and cannot be unit-selected.</summary>
        UnitBlockedLocked,
    }

    /// <summary>
    /// Handles a viewport click that resolved to <paramref name="memberUid"/>. Returns
    /// <see cref="ClickOutcome.NotHandled"/> when the caller should fall through to normal
    /// (individual) selection. The caller is responsible for gating on the clicked member's kind
    /// against the current chips BEFORE calling (so "if the clicked kind is selectable, the unit
    /// selects" holds); this method owns the unit/member state transition only.
    /// </summary>
    public ClickOutcome ClickMember(int memberUid, bool doubleClick)
    {
        if (_instances.InstanceOfMember(memberUid) is not { } rec)
        {
            return ClickOutcome.NotHandled;
        }

        // Already inside this instance: the member is individually selectable (normal path).
        if (EnteredInstanceId == rec.InstanceId)
        {
            return ClickOutcome.NotHandled;
        }

        if (doubleClick)
        {
            Enter(rec.InstanceId);
            return ClickOutcome.EnteredMember;
        }

        if (AnyMemberLocked(rec))
        {
            UnitInstanceId = null;
            return ClickOutcome.UnitBlockedLocked;
        }

        if (_router.SelectPrefabUnit(rec.MemberUids))
        {
            UnitInstanceId = rec.InstanceId;
            EnteredInstanceId = null;
            return ClickOutcome.UnitSelected;
        }

        UnitInstanceId = null;
        return ClickOutcome.NotHandled; // gate rejected the unit — fall through
    }

    /// <summary>True when the instance exists and has no locked member (selectable as a unit).</summary>
    public bool CanSelectAsUnit(int instanceId) =>
        _instances.ById(instanceId) is { } rec && !AnyMemberLocked(rec);

    /// <summary>Selects the whole instance as a unit (all members). False when blocked/absent.</summary>
    public bool SelectUnit(int instanceId)
    {
        if (_instances.ById(instanceId) is not { } rec)
        {
            return false;
        }

        if (AnyMemberLocked(rec))
        {
            UnitInstanceId = null;
            return false;
        }

        bool ok = _router.SelectPrefabUnit(rec.MemberUids);
        UnitInstanceId = ok ? instanceId : null;
        if (ok)
        {
            EnteredInstanceId = null;
        }

        return ok;
    }

    /// <summary>Enters an instance for member-level editing (transient). Does not select anything.</summary>
    public void Enter(int instanceId)
    {
        EnteredInstanceId = instanceId;
        UnitInstanceId = null;
    }

    /// <summary>Exits member editing back to unit level, re-selecting the exited instance. False when not entered.</summary>
    public bool ExitToUnit()
    {
        if (EnteredInstanceId is not int id)
        {
            return false;
        }

        EnteredInstanceId = null;
        SelectUnit(id);
        return true;
    }

    /// <summary>Clears all unit/entered state (document swap, non-member click, instance removed).</summary>
    public void Reset()
    {
        UnitInstanceId = null;
        EnteredInstanceId = null;
    }

    /// <summary>
    /// Drops any unit/entered state whose instance no longer exists (orphaned / propagated away).
    /// Returns true when state was invalidated.
    /// </summary>
    public bool ValidateExisting()
    {
        bool changed = false;
        if (UnitInstanceId is int u && _instances.ById(u) is null)
        {
            UnitInstanceId = null;
            changed = true;
        }

        if (EnteredInstanceId is int e && _instances.ById(e) is null)
        {
            EnteredInstanceId = null;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Applies a rigid transform (rotation about <paramref name="pivot"/> then
    /// <paramref name="translation"/>) to EVERY member of the unit-selected instance — brushes and
    /// objects — and refreshes the instance's authoritative pose record. Joins an already-open undo
    /// transaction (an interactive gizmo drag) so the whole drag is ONE undo step; otherwise it wraps
    /// itself in one transaction (a keyboard nudge / a test → one step). Returns false when no unit
    /// is selected. Member brush edits route through <see cref="BrushEditor.EditBrushes"/> (which
    /// reports no changed-UIDs) so a whole-unit move never self-flags the instance "modified" — that
    /// is reserved for genuine intra-instance member edits made while entered.
    /// </summary>
    public bool RigidTransformUnit(Mat3 rotation, Vec3 translation, Vec3 pivot)
    {
        if (UnitRecord is not { } rec)
        {
            return false;
        }

        bool rotate = !rotation.Equals(Mat3.Identity);
        bool translate = translation.LengthSquared() > 1e-12f;
        if (!rotate && !translate)
        {
            return true;
        }

        bool ownTx = !_doc.Undo.InTransaction;
        UndoStack.Transaction? tx = ownTx ? _doc.Undo.BeginTransaction("Transform prefab instance") : null;
        try
        {
            var brushUids = new List<int>();
            var objects = new List<LevelObject>();
            foreach (int uid in rec.MemberUids)
            {
                if (_brushes.FindBrush(uid) is not null)
                {
                    brushUids.Add(uid);
                }
                else if (_doc.FindByUid(uid) is { } o)
                {
                    objects.Add(o);
                }
            }

            if (brushUids.Count > 0)
            {
                _brushes.EditBrushes(brushUids, "Transform prefab instance", b =>
                {
                    if (rotate)
                    {
                        BrushTransform.RotateAboutPivot(b, rotation, pivot);
                    }

                    if (translate)
                    {
                        BrushTransform.Move(b, translation);
                    }

                    return OpResult.Ok();
                });
            }

            foreach (LevelObject o in objects)
            {
                Vec3 np = rotate ? pivot.Add(rotation.Transform(o.Position.Sub(pivot))).Add(translation) : o.Position.Add(translation);
                _doc.EditValue(o.Section, "Transform prefab instance", o.Position, np, v => o.Position = v);
                if (rotate && ObjectRotation.Get(o.Model) is Mat3 cur)
                {
                    Mat3 nr = Mat3Math.Compose(rotation, cur).Orthonormalize();
                    _doc.EditValue(o.Section, "Transform prefab instance", cur, nr, v => ObjectRotation.Set(o.Model, v));
                }
            }

            // Keep the explicit pose record fresh (all members are in the transformed set).
            _instances.ApplyRigidTransform(rec.MemberUids, rotation, translation, pivot);

            tx?.Commit();
            return true;
        }
        catch
        {
            tx?.Rollback();
            throw;
        }
    }

    private bool AnyMemberLocked(PrefabInstanceRecord rec)
    {
        foreach (int uid in rec.MemberUids)
        {
            if (_brushes.FindBrush(uid) is not null)
            {
                if (_brushes.IsBrushLocked(uid))
                {
                    return true;
                }
            }
            else if (_doc.FindByUid(uid) is { } o && _doc.IsLocked(o))
            {
                return true;
            }
        }

        return false;
    }
}
