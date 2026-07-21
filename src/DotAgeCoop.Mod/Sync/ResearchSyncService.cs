using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using Steamworks;
using DotAgeCoop.Net;
using UnityEngine;

namespace DotAgeCoop.Sync
{

    public sealed class ResearchSyncService
    {
        private static readonly FieldInfo CurrentResearchField =
            AccessTools.Field(typeof(ResearchHandler), "currentlyResearchingDefinitionBuilding");

        private readonly CoopSession _session;
        private readonly MelonLogger.Instance _log;
        private bool _applyingRemote;
        private float _lastBroadcastAt;
        private bool _pendingBroadcast;

        public bool ApplyingRemote { get { return _applyingRemote; } }

        public ResearchSyncService(CoopSession session, MelonLogger.Instance log)
        {
            _session = session;
            _log = log;
            _session.MessageReceived += OnMessage;
        }

        public void Tick()
        {
            if (!_session.Active || !_session.IsHost || !_pendingBroadcast)
                return;
            if (UnityEngine.Time.unscaledTime - _lastBroadcastAt < 0.15f)
                return;
            _pendingBroadcast = false;
            BroadcastSnapshot();
        }

        public void MarkDirty()
        {
            if (!_session.Active || !_session.IsHost || _applyingRemote)
                return;
            _pendingBroadcast = true;
        }

        public void BroadcastSnapshotImmediate()
        {
            if (!_session.Active || !_session.IsHost || _applyingRemote)
                return;
            _pendingBroadcast = false;
            BroadcastSnapshot();
        }

        public void SendSnapshotTo(CSteamID remote)
        {
            if (!_session.Active || !_session.IsHost || _applyingRemote)
                return;
            if (Game.I == null || Game.I.researchHandler == null)
                return;

            try
            {
                ResearchSnapshotPayload snap = CaptureSnapshot();
                _session.SendTo(remote, CoopMessageType.ResearchSnapshot,
                    CoopProtocol.PackResearchSnapshot(snap));
                _log.Msg("[Research] Send snapshot → " + remote.m_SteamID);
            }
            catch (Exception ex)
            {
                _log.Warning("[Research] SendSnapshotTo: " + ex.Message);
            }
        }

        public void SendIntent(int kind, int defId, int containerId)
        {
            if (!_session.Active || _session.IsHost || _applyingRemote)
                return;

            ResearchIntentPayload data = default(ResearchIntentPayload);
            data.Kind = kind;
            data.DefId = defId;
            data.ContainerId = containerId;
            _session.SendToHost(CoopMessageType.ResearchIntent, CoopProtocol.PackResearchIntent(data));
            _log.Msg("[Research] Intent kind=" + kind + " def=" + defId + " cont=" + containerId);
        }

        private void OnMessage(CSteamID remote, CoopMessageType type, byte[] payload)
        {
            switch (type)
            {
                case CoopMessageType.ResearchIntent:
                    if (_session.IsHost)
                        HandleIntent(payload);
                    break;
                case CoopMessageType.ResearchSnapshot:
                    if (!_session.IsHost)
                    {
                        if (GameBootstrapService.ClientShouldIgnoreRemoteGameplaySync())
                        {
                            if (Time.frameCount % 180 == 0)
                                _log.Msg("[Research] STAGE client: defer snapshot (joining/generating)");
                            break;
                        }
                        ApplySnapshot(payload);
                    }
                    break;
                case CoopMessageType.ResearchCheatUnlockAll:
                    if (!_session.IsHost)
                    {
                        _log.Msg("[Research] Host cheat unlock all tiers/tabs");
                        DotAgeCoop.Hooks.ResearchCheat.ApplyUnlockAllTiersAndTabs();
                    }
                    break;
            }
        }

        private void HandleIntent(byte[] payload)
        {
            ResearchIntentPayload data;
            if (!CoopProtocol.TryReadResearchIntent(payload, out data))
                return;
            if (Game.I == null || Game.I.researchHandler == null || Game.I.researchTree == null)
                return;

            try
            {
                switch (data.Kind)
                {
                    case ResearchIntentKind.SetCurrent:
                        ApplySetCurrent(data.DefId, data.ContainerId);
                        break;
                    case ResearchIntentKind.SelectChoice:
                        if (data.DefId <= 0)
                            ApplyRequestRandomChoice(data.ContainerId);
                        else
                            ApplySelectChoice(data.ContainerId, data.DefId);
                        break;
                    case ResearchIntentKind.QueueAdd:
                        ApplyQueueAdd(data.ContainerId);
                        break;
                    case ResearchIntentKind.QueueRemove:
                        ApplyQueueRemove(data.ContainerId);
                        break;
                    default:
                        _log.Warning("[Research] Unknown intent kind " + data.Kind);
                        return;
                }

                BroadcastSnapshotImmediate();
            }
            catch (Exception ex)
            {
                _log.Error("[Research] HandleIntent: " + ex);
            }
        }

        private void ApplySetCurrent(int defId, int containerId)
        {
            ResearchTree tree = Game.I.researchTree;
            BuildingResearchContainer cont = null;

            if (containerId > 0)
                cont = FindContainer(containerId);

            if (cont == null && defId > 0)
            {
                BuildingDefinition byDef = Game.I.buildingsHandler.GetDefinitionByID(defId);
                if (byDef != null && Game.I.researchRandomizer != null)
                    cont = Game.I.researchRandomizer.GetContainerForDefinition(byDef);
            }

            if (cont != null)
                EnsureContainerChosen(cont);

            tree.ClearQueue();
            if (cont == null && defId <= 0)
            {
                tree.SetCurrentResearchingBuilding(null);
                tree.UpdateInstances();
                _log.Msg("[Research] Host cleared current research");
                return;
            }

            BuildingDefinition def = null;
            if (defId > 0)
                def = Game.I.buildingsHandler.GetDefinitionByID(defId);
            if (def == null && cont != null)
                def = cont.CurrentDefinition;
            if (def == null)
            {
                _log.Warning("[Research] ApplySetCurrent: no definition cont=" + containerId +
                             " defId=" + defId);
                return;
            }

            if (cont != null && defId > 0)
            {
                int idx = cont.IndexOfUnlockedDef(defId);
                if (idx >= 0)
                {
                    if (!cont.AlreadyChosen || cont.CurrentDefinition == null ||
                        cont.CurrentDefinition.ID != defId)
                        cont.SelectChoice(idx + 1);
                    def = cont.CurrentDefinition ?? def;
                }
            }

            if (cont != null)
                tree.AddToResearchQueue(cont);

            tree.SetCurrentResearchingBuilding(def);
            tree.UpdateInstances();
            _log.Msg("[Research] Host set current def=" + def.ID +
                     " (" + def.name + ") cont=" + containerId);
        }

        private static void ApplySelectChoice(int containerId, int defId)
        {
            BuildingResearchContainer cont = FindContainer(containerId);
            if (cont == null || defId <= 0)
                return;

            int index = cont.IndexOfUnlockedDef(defId);
            cont.SelectChoice(index + 1);
            if (Game.I.researchTree != null)
                Game.I.researchTree.UpdateInstances();
        }

        private void ApplyRequestRandomChoice(int containerId)
        {
            BuildingResearchContainer cont = FindContainer(containerId);
            if (cont == null)
                return;

            EnsureContainerChosen(cont);
            if (Game.I.researchTree != null)
                Game.I.researchTree.UpdateInstances();
        }

        private void EnsureContainerChosen(BuildingResearchContainer cont)
        {
            if (cont == null || cont.AlreadyChosen)
                return;

            List<BuildingDefinition> unlocked = cont.GetAllUnlockedDefinitions();
            if (unlocked == null || unlocked.Count == 0)
                return;

            int pick = 0;
            try
            {
                pick = GameRandom.Int(0, unlocked.Count, RandomID.ResearchTree, cont.Id);
            }
            catch
            {
                pick = 0;
            }

            ResearchHandler rh = Game.I.researchHandler;
            for (int attempt = 0; attempt < unlocked.Count; attempt++)
            {
                int idx = (pick + attempt) % unlocked.Count;
                BuildingDefinition candidate = unlocked[idx];
                try
                {
                    if (rh != null &&
                        !rh.CouldSelectAsContainerChoice(candidate, doPrereqChecks: true,
                            doIgnoreGroupPathsForDefining: false, isChoosing: true))
                        continue;
                }
                catch
                {
                }

                cont.SelectChoice(idx + 1);
                _log.Msg("[Research] Committed choice cont=" + cont.Id +
                         " def=" + (candidate != null ? candidate.ID : 0) +
                         " (" + (candidate != null ? candidate.name : "?") + ")");
                return;
            }

            cont.SelectChoice(pick + 1);
            _log.Msg("[Research] Committed fallback choice cont=" + cont.Id + " idx=" + pick);
        }

        private void ApplyQueueAdd(int containerId)
        {
            BuildingResearchContainer cont = FindContainer(containerId);
            if (cont == null)
                return;
            EnsureContainerChosen(cont);
            Game.I.researchTree.AddToResearchQueue(cont);

            BuildingDefinition current = Game.I.researchHandler.GetCurrentResearchingBuilding();
            if (current == null && cont.CurrentDefinition != null)
                Game.I.researchTree.SetCurrentResearchingBuilding(cont.CurrentDefinition);
            else if (current != null)
                Game.I.researchTree.SetCurrentlyResearchingBasedOnQueue();

            Game.I.researchTree.UpdateInstances();
            _log.Msg("[Research] Host queue-add cont=" + containerId +
                     " current=" + (Game.I.researchHandler.GetCurrentResearchingBuilding() != null
                         ? Game.I.researchHandler.GetCurrentResearchingBuilding().ID : 0));
        }

        private static void ApplyQueueRemove(int containerId)
        {
            BuildingResearchContainer cont = FindContainer(containerId);
            if (cont == null)
                return;
            Game.I.researchTree.RemoveFromResearchQueue(cont);
            Game.I.researchTree.SetCurrentlyResearchingBasedOnQueue();
            Game.I.researchTree.UpdateInstances();
        }

        private static BuildingResearchContainer FindContainer(int containerId)
        {
            if (Game.I == null || Game.I.researchHandler == null || Game.I.researchHandler.allContainers_Cache == null)
                return null;
            return Game.I.researchHandler.allContainers_Cache
                .FirstOrDefault(c => c != null && c.Id == containerId);
        }

        private void BroadcastSnapshot()
        {
            if (Game.I == null || Game.I.researchHandler == null)
                return;

            try
            {
                ResearchSnapshotPayload snap = CaptureSnapshot();
                _lastBroadcastAt = UnityEngine.Time.unscaledTime;
                _session.Broadcast(CoopMessageType.ResearchSnapshot, CoopProtocol.PackResearchSnapshot(snap));
                int knownN = snap.KnownDefIds != null ? snap.KnownDefIds.Length : 0;
                int choiceN = snap.ChoiceDefIds != null ? snap.ChoiceDefIds.Length : 0;
                _log.Msg("[Research] Snapshot current=" + snap.CurrentDefId +
                         " known=" + knownN + " choices=" + choiceN +
                         " pts=" + snap.CurrentPoints);
            }
            catch (Exception ex)
            {
                _log.Warning("[Research] BroadcastSnapshot: " + ex.Message);
            }
        }

        private static ResearchSnapshotPayload CaptureSnapshot()
        {
            ResearchSnapshotPayload snap = default(ResearchSnapshotPayload);
            ResearchHandler rh = Game.I.researchHandler;
            ResearchTree tree = Game.I.researchTree;

            BuildingDefinition current = rh.GetCurrentResearchingBuilding();
            snap.CurrentDefId = current != null ? current.ID : 0;
            snap.CurrentPoints = rh.CurrentPoints;
            snap.OverflowPoints = rh.overflowPoints;
            snap.HasStartedResearch = (byte)(rh._hasStartedResearch ? 1 : 0);
            snap.AskForNewResearch = (byte)(rh.AskForNewResearch ? 1 : 0);
            snap.LatestCompletedDefId = (tree != null && tree.LatestCompletedBuilding != null)
                ? tree.LatestCompletedBuilding.ID
                : 0;

            List<int> known = rh.GetKnownBuildingsDefs();
            snap.KnownDefIds = known != null ? known.ToArray() : new int[0];

            List<IntPair> choices = new List<IntPair>(64);
            if (rh.containerSelectionChoices_DefID != null)
            {
                foreach (KeyValuePair<int, int> kv in rh.containerSelectionChoices_DefID)
                {
                    IntPair p = default(IntPair);
                    p.Key = kv.Key;
                    p.Value = kv.Value;
                    choices.Add(p);
                }
            }
            snap.ChoiceDefIds = choices.ToArray();

            List<IntPair> points = new List<IntPair>(64);
            if (rh.containerCurrentPoints != null)
            {
                foreach (KeyValuePair<int, int> kv in rh.containerCurrentPoints)
                {
                    IntPair p = default(IntPair);
                    p.Key = kv.Key;
                    p.Value = kv.Value;
                    points.Add(p);
                }
            }
            snap.ContainerPoints = points.ToArray();

            List<int> queue = new List<int>(8);
            if (tree != null && tree.ResearchQueue != null)
            {
                foreach (BuildingResearchContainer c in tree.ResearchQueue)
                {
                    if (c != null)
                        queue.Add(c.Id);
                }
            }
            snap.QueueContainerIds = queue.ToArray();
            snap.ChosenPaths = rh.chosenPaths != null ? (string[])rh.chosenPaths.Clone() : new string[0];
            return snap;
        }

        private void ApplySnapshot(byte[] payload)
        {
            ResearchSnapshotPayload snap;
            if (!CoopProtocol.TryReadResearchSnapshot(payload, out snap))
                return;
            if (Game.I == null || Game.I.researchHandler == null || Game.I.researchTree == null)
                return;
            if (_applyingRemote)
                return;

            _applyingRemote = true;
            try
            {
                ResearchHandler rh = Game.I.researchHandler;
                ResearchTree tree = Game.I.researchTree;

                if (snap.ChosenPaths != null)
                    rh.chosenPaths = snap.ChosenPaths;
                try
                {
                    rh.ComputeRacialPath(addToChosenPaths: false);
                }
                catch
                {
                }

                if (rh.containerSelectionChoices_DefID == null)
                    rh.containerSelectionChoices_DefID = new Dictionary<int, int>();
                else
                    rh.containerSelectionChoices_DefID.Clear();

                if (snap.ChoiceDefIds != null)
                {
                    for (int i = 0; i < snap.ChoiceDefIds.Length; i++)
                        rh.containerSelectionChoices_DefID[snap.ChoiceDefIds[i].Key] = snap.ChoiceDefIds[i].Value;
                }

                if (rh.allContainers_Cache != null)
                {
                    for (int i = 0; i < rh.allContainers_Cache.Count; i++)
                    {
                        BuildingResearchContainer cont = rh.allContainers_Cache[i];
                        if (cont == null)
                            continue;
                        int defId;
                        if (!rh.containerSelectionChoices_DefID.TryGetValue(cont.Id, out defId) || defId <= 0)
                        {
                            if (cont.AlreadyChosen)
                                cont.ChosenIndex = 0;
                            continue;
                        }
                        try
                        {
                            int index = cont.IndexOfUnlockedDef(defId);
                            cont.SelectChoice(index + 1, isLoading: true, doIgnoreGroupPathsForDefining: true);
                        }
                        catch (Exception ex)
                        {
                            _log.Warning("[Research] SelectChoice load cont=" + cont.Id + ": " + ex.Message);
                        }
                    }
                }

                if (snap.KnownDefIds != null)
                {
                    for (int i = 0; i < snap.KnownDefIds.Length; i++)
                    {
                        try
                        {
                            BuildingDefinition knownDef =
                                Game.I.buildingsHandler.GetDefinitionByID(snap.KnownDefIds[i]);
                            if (knownDef != null && rh.HasKnowledgeOf(knownDef))
                                continue;
                            rh.EnableKnowledgeOfBuilding(snap.KnownDefIds[i], isNovel: false);
                        }
                        catch
                        {
                        }
                    }
                }

                if (rh.containerCurrentPoints == null)
                    rh.containerCurrentPoints = new Dictionary<int, int>();
                else
                    rh.containerCurrentPoints.Clear();
                if (snap.ContainerPoints != null)
                {
                    for (int i = 0; i < snap.ContainerPoints.Length; i++)
                        rh.containerCurrentPoints[snap.ContainerPoints[i].Key] = snap.ContainerPoints[i].Value;
                }

                tree.ClearQueue();
                if (snap.QueueContainerIds != null)
                {
                    for (int i = 0; i < snap.QueueContainerIds.Length; i++)
                    {
                        BuildingResearchContainer cont = FindContainer(snap.QueueContainerIds[i]);
                        if (cont != null)
                            tree.ResearchQueue.Add(cont);
                    }
                }

                if (snap.LatestCompletedDefId > 0)
                    tree.LatestCompletedBuilding = Game.I.buildingsHandler.GetDefinitionByID(snap.LatestCompletedDefId);

                rh.AskForNewResearch = snap.AskForNewResearch != 0;
                rh.ForceStartedResearch(snap.HasStartedResearch != 0);
                rh.overflowPoints = snap.OverflowPoints;

                if (snap.CurrentDefId > 0)
                {
                    BuildingDefinition def = Game.I.buildingsHandler.GetDefinitionByID(snap.CurrentDefId);
                    if (def != null)
                    {
                        tree.SetCurrentResearchingBuilding(def);
                        rh.ForceCurrentPoints(snap.CurrentPoints);
                    }
                }
                else
                {

                    ClearCurrentResearchSafe(rh, snap.OverflowPoints, snap.CurrentPoints);
                }

                try
                {
                    tree.UpdateInstances();
                }
                catch
                {
                }

                try
                {
                    rh.CheckResearchEnabling();
                    if (Game.I.researchBarGUI != null && rh.IsAvailable() &&
                        snap.CurrentDefId <= 0)
                    {
                        Game.I.researchBarGUI.SetNotChosen(snap.OverflowPoints);
                    }

                    if (ModMain.Instance != null && ModMain.Instance.GameSync != null)
                        ModMain.Instance.GameSync.RestoreFrontMenusAfterClientPlace();
                }
                catch (Exception ex)
                {
                    _log.Warning("[Research] CheckResearchEnabling: " + ex.Message);
                }

                _log.Msg("[Research] Applied snapshot current=" + snap.CurrentDefId +
                         " known=" + (snap.KnownDefIds != null ? snap.KnownDefIds.Length : 0) +
                         " choices=" + (snap.ChoiceDefIds != null ? snap.ChoiceDefIds.Length : 0) +
                         " available=" + rh.IsAvailable());
            }
            catch (Exception ex)
            {
                _log.Error("[Research] ApplySnapshot: " + ex);
            }
            finally
            {
                _applyingRemote = false;
            }
        }

        private static void ClearCurrentResearchSafe(ResearchHandler rh, int overflowPoints, int currentPoints)
        {
            if (CurrentResearchField != null)
                CurrentResearchField.SetValue(rh, null);

            try
            {
                if (Game.I.researchBarGUI != null)
                    Game.I.researchBarGUI.SetNotChosen(overflowPoints);
            }
            catch
            {
            }

            try
            {
                rh.ForceCurrentPoints(currentPoints);
            }
            catch
            {
            }
        }
    }
}
