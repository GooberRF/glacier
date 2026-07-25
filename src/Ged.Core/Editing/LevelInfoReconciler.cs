using System;
using Ged.Core.IO.Rfl;
using Ged.Core.IO.Rfl.Sections;

namespace Ged.Core.Editing;

/// <summary>
/// Reconciles the editor-metadata <c>level_info.has_movers</c> flag when GED authors or changes a
/// level's movers, so a GED-authored mover level writes <c>has_movers = 1</c> like a well-formed RED
/// level (RED-authored dmabrupt: has_movers=1 with 8 movers), instead of the 0 GED shipped (the
/// movtest4 repro: has_movers=0 with 1 mover).
/// <para>
/// The flag is <b>only</b> reconciled when the <c>movers</c> section is dirty — i.e. the user created
/// or dissolved a mover this session (<see cref="MoverService"/> marks it). Two facts make a blanket
/// "always rewrite has_movers to match the movers section" wrong:
/// <list type="bullet">
/// <item>RF.exe's level loader (<c>FUN_00460820</c> @0x00460820) has no case for the level_info
/// section id (0x01000000) and skips it, so has_movers does not gate any in-game mover behaviour.</item>
/// <item>RED itself does not maintain it consistently — corpus levels exist with movers and
/// has_movers=0 (ctf01: 2 movers→0; dm17: 17 movers→0) and with has_movers=1 and no movers at all
/// (dm05). Rewriting the flag on an untouched load→save would flip those bytes and regress the
/// byte-identity ratchet.</item>
/// </list>
/// Gating on the dirty movers section keeps an untouched level byte-identical while making GED's own
/// mover authoring produce the correct flag. Idempotent: it only dirties level_info when the value
/// actually changes.
/// </para>
/// </summary>
public static class LevelInfoReconciler
{
    public static void ReconcileHasMovers(RflFile rfl)
    {
        ArgumentNullException.ThrowIfNull(rfl);

        RflSection? moversHost = null;
        RflSection? infoHost = null;
        foreach (RflSection s in rfl.Sections)
        {
            if (s.TypeId == (uint)SectionType.Movers)
            {
                moversHost = s;
            }
            else if (s.TypeId == (uint)SectionType.LevelInfo)
            {
                infoHost = s;
            }
        }

        // No level_info to carry the flag, or GED did not touch the movers this session → leave the
        // file exactly as it was (byte-identical).
        if (infoHost is null || moversHost is null || !moversHost.Dirty)
        {
            return;
        }

        rfl.ParseAllKnownSections();
        if (infoHost.Content is not LevelInfoSection info || moversHost.Content is not MoversSection movers)
        {
            return;
        }

        byte want = (byte)(movers.Movers.Count > 0 ? 1 : 0);
        if (info.HasMovers != want)
        {
            info.HasMovers = want;
            infoHost.Dirty = true;
        }
    }
}
