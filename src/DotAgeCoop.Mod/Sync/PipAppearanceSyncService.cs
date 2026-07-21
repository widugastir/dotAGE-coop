using System;
using System.Collections.Generic;
using MelonLoader;
using Steamworks;
using DotAgeCoop.Net;
using UnityEngine;

namespace DotAgeCoop.Sync
{

    public sealed class PipAppearanceSyncService
    {
        private const float BroadcastInterval = 2f;

        private readonly CoopSession _session;
        private readonly MelonLogger.Instance _log;
        private float _lastBroadcast;
        private int _lastHash;
        private bool _dirty = true;

        private bool _publishEnabled;

        public PipAppearanceSyncService(CoopSession session, MelonLogger.Instance log)
        {
            _session = session;
            _log = log;
            _session.MessageReceived += OnMessage;
        }

        public void OnReturnedToMain()
        {
            _dirty = true;
            _lastHash = 0;
            _publishEnabled = false;
        }

        public void MarkDirty()
        {
            _dirty = true;
        }

        public void OnLoadGateUnlocked()
        {
            if (!_session.Active || !_session.IsHost)
                return;

            _publishEnabled = true;
            _dirty = true;
            _log.Msg("[PipVisual] Publish enabled — sending post-load snapshot");
            BroadcastSnapshotImmediate();
        }

        public void BroadcastSnapshotImmediate()
        {
            if (!_session.Active || !_session.IsHost)
                return;
            if (!CanPublish())
                return;

            try
            {
                _dirty = true;
                BroadcastSnapshot();
            }
            catch (Exception ex)
            {
                _log.Warning("[PipVisual] BroadcastSnapshotImmediate: " + ex.Message);
            }
        }

        public void SendSnapshotTo(CSteamID remote)
        {
            if (!_session.Active || !_session.IsHost)
                return;
            if (!CanPublish())
            {
                _log.Msg("[PipVisual] SendSnapshotTo skipped — publish not enabled yet");
                return;
            }
            if (!Game.Ready || Game.I == null || Game.I.pipsHandler == null)
                return;

            try
            {
                PipAppearanceSnapshotPayload snap = Capture();
                _session.SendTo(remote, CoopMessageType.PipAppearanceSnapshot,
                    CoopProtocol.PackPipAppearanceSnapshot(snap));
                _log.Msg("[PipVisual] Send snapshot → " + remote.m_SteamID + " pips=" +
                         (snap.Entries != null ? snap.Entries.Length : 0));
            }
            catch (Exception ex)
            {
                _log.Warning("[PipVisual] SendSnapshotTo: " + ex.Message);
            }
        }

        public void Tick()
        {
            if (!_session.Active || !_session.IsHost)
                return;
            if (!CanPublish())
                return;
            if (!_dirty && Time.unscaledTime - _lastBroadcast < BroadcastInterval)
                return;
            if (Time.unscaledTime - _lastBroadcast < 0.5f)
                return;
            if (!Game.Ready || Game.I == null || Game.I.pipsHandler == null)
                return;

            try
            {
                BroadcastSnapshot();
            }
            catch (Exception ex)
            {
                _log.Warning("[PipVisual] Tick: " + ex.Message);
            }
        }

        private bool CanPublish()
        {
            if (!_publishEnabled)
                return false;
            try
            {
                if (ModMain.Instance != null)
                {
                    if (ModMain.Instance.Bootstrap != null &&
                        ModMain.Instance.Bootstrap.IsPeerLoadWaitActive)
                        return false;
                    if (ModMain.Instance.LoadSync != null &&
                        ModMain.Instance.LoadSync.IsHostPreTransferActive)
                        return false;
                }
            }
            catch
            {
            }
            return true;
        }

        private void BroadcastSnapshot()
        {
            if (!CanPublish())
                return;

            PipAppearanceSnapshotPayload snap = Capture();
            int hash = HashSnapshot(snap);
            if (hash == _lastHash && !_dirty)
            {
                _lastBroadcast = Time.unscaledTime;
                return;
            }

            _lastHash = hash;
            _lastBroadcast = Time.unscaledTime;
            _dirty = false;
            _session.Broadcast(CoopMessageType.PipAppearanceSnapshot, CoopProtocol.PackPipAppearanceSnapshot(snap));
            _log.Msg("[PipVisual] Snapshot " + (snap.Entries != null ? snap.Entries.Length : 0) + " pips");
        }

        private void OnMessage(CSteamID remote, CoopMessageType type, byte[] payload)
        {
            if (type != CoopMessageType.PipAppearanceSnapshot)
                return;
            if (_session.IsHost)
                return;
            if (GameBootstrapService.ClientShouldIgnoreRemoteGameplaySync())
                return;

            PipAppearanceSnapshotPayload snap;
            if (!CoopProtocol.TryReadPipAppearanceSnapshot(payload, out snap))
                return;

            try
            {
                ApplySnapshot(snap);
            }
            catch (Exception ex)
            {
                _log.Warning("[PipVisual] Apply: " + ex.Message);
            }
        }

        private static PipAppearanceSnapshotPayload Capture()
        {
            PipAppearanceSnapshotPayload snap = default(PipAppearanceSnapshotPayload);
            List<PipAppearanceEntry> list = new List<PipAppearanceEntry>(32);
            List<Pipo> pips = Game.I.pipsHandler.GetAllPips_ExploringToo();
            if (pips != null)
            {
                for (int i = 0; i < pips.Count; i++)
                {
                    Pipo pip = pips[i];
                    if (pip == null || pip.UID == 0)
                        continue;
                    try
                    {
                        if (pip.IsDead())
                            continue;
                    }
                    catch
                    {
                    }

                    PipAppearanceEntry e = default(PipAppearanceEntry);
                    e.Uid = pip.UID;
                    e.IsMale = (byte)(pip.isMale ? 1 : 0);
                    e.ColorIndex = pip.pipColorIndex;
                    e.HairIndex = pip.pipHairIndex;
                    e.ChinIndex = pip.pipChinIndex;
                    e.RaceId = pip.RaceDefinition != null ? pip.RaceDefinition.ID : 0;
                    e.ClassId = pip.ClassDefinition != null ? pip.ClassDefinition.ID : 0;
                    e.Name = pip.OwnName(forceNoColor: true) ?? string.Empty;
                    e.HomeTerrainI = -1;
                    e.HomeTerrainJ = -1;
                    e.IsChild = 0;
                    try
                    {
                        if (pip.homeBuilding != null && pip.homeBuilding.terrain != null &&
                            pip.homeBuilding.terrain.cell != null)
                        {
                            e.HomeTerrainI = pip.homeBuilding.terrain.cell.i;
                            e.HomeTerrainJ = pip.homeBuilding.terrain.cell.j;
                        }
                        if (pip.definition != null &&
                            Game.I.creaturesHandler != null &&
                            Game.I.creaturesHandler.childTrait != null &&
                            pip.definition.HasTrait(Game.I.creaturesHandler.childTrait))
                            e.IsChild = 1;
                    }
                    catch
                    {
                    }
                    list.Add(e);
                }
            }

            snap.Entries = list.ToArray();
            return snap;
        }

        private void ApplySnapshot(PipAppearanceSnapshotPayload snap)
        {
            if (snap.Entries == null || Game.I == null || Game.I.pipsHandler == null)
                return;

            HashSet<int> hostUids = new HashSet<int>();
            for (int i = 0; i < snap.Entries.Length; i++)
            {
                if (snap.Entries[i].Uid != 0)
                    hostUids.Add(snap.Entries[i].Uid);
            }

            int applied = 0;
            int spawned = 0;
            int reclaimed = 0;
            for (int i = 0; i < snap.Entries.Length; i++)
            {
                PipAppearanceEntry e = snap.Entries[i];
                Worker worker = Game.I.pipsHandler.GetPipByUID(e.Uid);
                Pipo pip = worker as Pipo;
                if (pip == null)
                {
                    pip = TryReclaimOrphanPip(e, hostUids);
                    if (pip != null)
                        reclaimed++;
                    else
                    {
                        pip = TrySpawnMissingPip(e);
                        if (pip == null)
                            continue;
                        spawned++;
                    }
                }

                if (e.RaceId != 0 && (pip.RaceDefinition == null || pip.RaceDefinition.ID != e.RaceId))
                {
                    RaceDefinition race = FindRace(e.RaceId);
                    if (race != null)
                        pip.SetRace(race);
                }

                if (e.ClassId != 0 && (pip.ClassDefinition == null || pip.ClassDefinition.ID != e.ClassId))
                {
                    ClassDefinition cls = FindClass(e.ClassId);
                    if (cls != null)
                        pip.SetClass(cls);
                }

                bool wantMale = e.IsMale != 0;
                if (pip.isMale != wantMale)
                {
                    pip.isMale = wantMale;
                    pip.ResetupAnimator();
                }
                else
                {
                    pip.isMale = wantMale;
                }

                pip.ForceColor(e.ColorIndex);
                pip.ForceHair(e.HairIndex);
                pip.ForceChin(e.ChinIndex);
                pip.RefreshAll();

                if (!string.IsNullOrEmpty(e.Name))
                {
                    string current = pip.OwnName(forceNoColor: true);
                    if (!string.Equals(current, e.Name, StringComparison.Ordinal))
                        pip.SetCreatureName(e.Name);
                }

                TryAssignHome(pip, e);
                applied++;
            }

            int culled = CullClientOnlyPips(hostUids);

            if (applied > 0 || spawned > 0 || reclaimed > 0 || culled > 0)
                _log.Msg("[PipVisual] Applied visuals to " + applied + " pips (spawned=" +
                         spawned + " reclaimed=" + reclaimed + " culled=" + culled + ")");
        }

        private Pipo TryReclaimOrphanPip(PipAppearanceEntry e, HashSet<int> hostUids)
        {
            try
            {
                List<Pipo> locals = Game.I.pipsHandler.GetAllAlivePips_NoAlloc();
                if (locals == null)
                    return null;

                Pipo best = null;
                int bestScore = -1;
                for (int i = 0; i < locals.Count; i++)
                {
                    Pipo pip = locals[i];
                    if (pip == null || pip.UID == 0 || hostUids.Contains(pip.UID))
                        continue;
                    if (Game.I.elder != null && pip == Game.I.elder)
                        continue;

                    int score = 0;
                    bool isChild = IsChildPip(pip);
                    if (e.IsChild != 0 && isChild)
                        score += 10;
                    else if (e.IsChild == 0 && !isChild)
                        score += 3;
                    else if (e.IsChild != 0 && !isChild)
                        continue;

                    if (e.RaceId != 0 && pip.RaceDefinition != null && pip.RaceDefinition.ID == e.RaceId)
                        score += 5;

                    if (e.HomeTerrainI >= 0 && pip.homeBuilding != null &&
                        pip.homeBuilding.terrain != null && pip.homeBuilding.terrain.cell != null &&
                        pip.homeBuilding.terrain.cell.i == e.HomeTerrainI &&
                        pip.homeBuilding.terrain.cell.j == e.HomeTerrainJ)
                        score += 20;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = pip;
                    }
                }

                if (best == null || bestScore < 10)
                    return null;

                int oldUid = best.UID;
                best.UID = e.Uid;
                _log.Msg("[PipVisual] Reclaimed orphan uid " + oldUid + " → host " + e.Uid);
                return best;
            }
            catch (Exception ex)
            {
                _log.Warning("[PipVisual] Reclaim orphan: " + ex.Message);
                return null;
            }
        }

        private int CullClientOnlyPips(HashSet<int> hostUids)
        {
            if (hostUids == null || hostUids.Count == 0)
                return 0;

            int culled = 0;
            try
            {
                List<Pipo> locals = Game.I.pipsHandler.GetAllAlivePips_NoAlloc();
                if (locals == null)
                    return 0;

                List<Pipo> extras = new List<Pipo>();
                for (int i = 0; i < locals.Count; i++)
                {
                    Pipo pip = locals[i];
                    if (pip == null || pip.UID == 0)
                        continue;
                    if (hostUids.Contains(pip.UID))
                        continue;
                    if (Game.I.elder != null && pip == Game.I.elder)
                        continue;
                    extras.Add(pip);
                }

                for (int i = 0; i < extras.Count; i++)
                {
                    Pipo pip = extras[i];
                    try
                    {
                        _log.Warning("[PipVisual] Culling client-only pip uid=" + pip.UID +
                                     " (" + (pip.OwnName(forceNoColor: true) ?? "?") + ")");
                        if (pip.homeBuilding != null)
                        {
                            BuildingDwelling home = pip.homeBuilding as BuildingDwelling;
                            if (home != null)
                                home.RemoveFromHome(pip, checkHomeless: false, notify: false, ignoreHomeless: true);
                        }
                        pip.Kill(bloody: false, NotificationType.NONE, withCorpse: false, withSpirit: false);
                        culled++;
                    }
                    catch (Exception ex)
                    {
                        _log.Warning("[PipVisual] Cull failed uid=" + pip.UID + ": " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warning("[PipVisual] CullClientOnlyPips: " + ex.Message);
            }
            return culled;
        }

        private static bool IsChildPip(Pipo pip)
        {
            try
            {
                if (pip == null || pip.definition == null)
                    return false;
                if (Game.I == null || Game.I.creaturesHandler == null ||
                    Game.I.creaturesHandler.childTrait == null)
                    return false;
                return pip.definition.HasTrait(Game.I.creaturesHandler.childTrait);
            }
            catch
            {
                return false;
            }
        }

        private Pipo TrySpawnMissingPip(PipAppearanceEntry e)
        {
            try
            {
                RaceDefinition race = FindRace(e.RaceId);
                if (race == null)
                    race = Game.I.pipsHandler.defaultRaceDef;
                if (race == null)
                    return null;

                Pipo pip = Game.I.pipsHandler.SpawnNewPip(race, e.IsChild != 0);
                if (pip == null)
                    return null;

                pip.UID = e.Uid;
                _log.Msg("[PipVisual] Spawned missing pip uid=" + e.Uid +
                         " child=" + (e.IsChild != 0) + " race=" + e.RaceId);
                return pip;
            }
            catch (Exception ex)
            {
                _log.Warning("[PipVisual] Spawn missing pip uid=" + e.Uid + ": " + ex.Message);
                return null;
            }
        }

        private static void TryAssignHome(Pipo pip, PipAppearanceEntry e)
        {
            if (pip == null || e.HomeTerrainI < 0 || e.HomeTerrainJ < 0)
                return;
            try
            {
                if (Game.I == null || Game.I.mapController == null)
                    return;
                MapTerrain terrain = Game.I.mapController.GetTerrain(e.HomeTerrainI, e.HomeTerrainJ);
                if (terrain == null || !terrain.HasBuilding())
                    return;
                BuildingDwelling home = terrain.Building as BuildingDwelling;
                if (home == null)
                    return;
                if (pip.homeBuilding == home)
                    return;
                home.SetAsHomeFor(pip, addToGenStep: true, handleStatusChange: true, notify: false);
            }
            catch
            {
            }
        }

        private static RaceDefinition FindRace(int id)
        {
            List<RaceDefinition> all = Game.I.pipsHandler.GetAllRaceDefinitions();
            if (all == null)
                return null;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] != null && all[i].ID == id)
                    return all[i];
            }
            return null;
        }

        private static ClassDefinition FindClass(int id)
        {
            List<ClassDefinition> all = Game.I.pipsHandler.GetAllClassDefinitions();
            if (all == null)
                return null;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] != null && all[i].ID == id)
                    return all[i];
            }
            return null;
        }

        private static int HashSnapshot(PipAppearanceSnapshotPayload snap)
        {
            unchecked
            {
                int h = 17;
                if (snap.Entries == null)
                    return h;
                for (int i = 0; i < snap.Entries.Length; i++)
                {
                    PipAppearanceEntry e = snap.Entries[i];
                    h = h * 31 + e.Uid;
                    h = h * 31 + e.IsMale;
                    h = h * 31 + e.ColorIndex;
                    h = h * 31 + e.HairIndex;
                    h = h * 31 + e.ChinIndex;
                    h = h * 31 + e.RaceId;
                    h = h * 31 + e.ClassId;
                    h = h * 31 + e.HomeTerrainI;
                    h = h * 31 + e.HomeTerrainJ;
                    h = h * 31 + e.IsChild;
                    if (e.Name != null)
                        h = h * 31 + e.Name.GetHashCode();
                }
                return h;
            }
        }
    }
}
