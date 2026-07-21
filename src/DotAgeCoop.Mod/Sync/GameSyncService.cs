using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using Steamworks;
using DotAgeCoop.Net;
using UnityEngine;

namespace DotAgeCoop.Sync
{

    public sealed class GameSyncService
    {
        private readonly CoopSession _session;
        private readonly MelonLogger.Instance _log;
        private static readonly FieldInfo FoodAssignmentField =
            AccessTools.Field(typeof(BuildingsHandler), "FoodAssignment");
        private static readonly FieldInfo AdditionalDefsField =
            AccessTools.Field(typeof(MapTerrain), "additionalDefinitions");

        private bool _applyingRemote;
        private bool _applyingFood;
        private bool _applyingFoodBans;
        private bool _applyingTerrain;
        private bool _resourcesDirty;
        private float _lastResourceBroadcast;
        private FoodSnapshotPayload _lastFoodSnapshot;
        private bool _hasFoodSnapshot;
        private bool _foodBanUiDirty;
        private float _foodBanUiRetryAt;
        private bool _pendingFoodUiRefresh;
        private float _pendingFoodUiRefreshAt;
        private bool _pendingFrontMenusRestore;
        private float _pendingFrontMenusRestoreAt;
        private int _pendingFrontMenusRestorePasses;
        private bool _pendingReachableRefresh;
        private float _pendingReachableRefreshAt;

        private bool _bulkWorldApply;
        private byte[] _pendingWorldSnapshot;
        private float _pendingWorldSnapshotRetryAt;
        private int _pendingWorldSnapshotTries;

        public bool ApplyingRemote { get { return _applyingRemote; } }
        public bool ApplyingFood { get { return _applyingFood; } }
        public bool ApplyingFoodBans { get { return _applyingFoodBans; } }

        public GameSyncService(CoopSession session, MelonLogger.Instance log)
        {
            _session = session;
            _log = log;
            _session.MessageReceived += OnMessage;
        }

        public void Tick()
        {
            if (_foodBanUiDirty && Time.unscaledTime >= _foodBanUiRetryAt)
            {
                _foodBanUiDirty = false;
                RefreshFoodBanUi();
            }

            if (_pendingFoodUiRefresh && Time.unscaledTime >= _pendingFoodUiRefreshAt)
            {
                _pendingFoodUiRefresh = false;
                RefreshFoodUiFromCache();
            }

            if (_pendingFrontMenusRestore && Time.unscaledTime >= _pendingFrontMenusRestoreAt)
            {
                RestoreFrontMenusAfterClientPlace();
                _pendingFrontMenusRestorePasses--;
                if (_pendingFrontMenusRestorePasses <= 0)
                    _pendingFrontMenusRestore = false;
                else
                    _pendingFrontMenusRestoreAt = Time.unscaledTime + 0.2f;
            }

            if (_pendingReachableRefresh && Time.unscaledTime >= _pendingReachableRefreshAt)
            {
                _pendingReachableRefresh = false;
                RefreshReachableBordersClient();
            }

            if (_pendingWorldSnapshot != null && Time.unscaledTime >= _pendingWorldSnapshotRetryAt)
                RetryPendingWorldSnapshot();

            if (!_session.Active || !_session.IsHost || !_resourcesDirty)
                return;

            if (ShouldSuppressPassTurnResourceSync())
                return;

            if (Time.unscaledTime - _lastResourceBroadcast < 0.25f)
                return;

            _resourcesDirty = false;
            BroadcastResourcesSnapshot();
        }

        public static bool ShouldSuppressPassTurnResourceSync()
        {
            try
            {
                if (ModMain.Instance != null && ModMain.Instance.HardSync != null &&
                    ModMain.Instance.HardSync.IsActive)
                    return false;
            }
            catch
            {
            }

            try
            {
                ModMain mod = ModMain.Instance;
                if (mod != null && mod.TurnSync != null && mod.TurnSync.AllowsMorningResourceApply)
                    return false;
            }
            catch
            {
            }

            try
            {
                if (Game.I != null && Game.I.IsPassNightTime())
                    return true;
            }
            catch
            {
            }

            try
            {
                ModMain mod = ModMain.Instance;
                if (mod != null && mod.TurnSync != null &&
                    mod.TurnSync.CurrentPhase == TurnPhase.PassTurnRunning)
                    return true;
            }
            catch
            {
            }

            return false;
        }

        public void ScheduleFoodUiRefresh(float delaySeconds = 0.45f)
        {
            _pendingFoodUiRefresh = true;
            _pendingFoodUiRefreshAt = Time.unscaledTime + delaySeconds;
        }

        public void OnReturnedToMain()
        {
            _resourcesDirty = false;
            _applyingRemote = false;
            _applyingTerrain = false;
            _bulkWorldApply = false;
            _pendingFrontMenusRestore = false;
            _pendingFrontMenusRestorePasses = 0;
            _pendingReachableRefresh = false;
            _pendingWorldSnapshot = null;
            _pendingWorldSnapshotTries = 0;
        }

        public void MarkResourcesDirty()
        {
            if (!_session.Active || !_session.IsHost || _applyingRemote)
                return;
            _resourcesDirty = true;
        }

        public void HostBroadcastDelta(string kind, string body)
        {
            if (!_session.Active || !_session.IsHost)
                return;

            _session.Broadcast(CoopMessageType.StateDelta, CoopProtocol.PackStateDelta(kind, body));
            _log.Msg("[Delta] " + kind + " => " + body);
        }

        public void NotifyHostDayAdvanced(int day)
        {
            HostBroadcastDelta("day", day.ToString());
        }

        public bool OnClientTryConfirmPosition(BuildingPlacementHandler handler)
        {
            if (!_session.Active || _session.IsHost)
                return true;

            try
            {
                if (ModMain.Instance != null && ModMain.Instance.TurnSync != null &&
                    ModMain.Instance.TurnSync.BlocksWorldPlayUntilMorningReady)
                {
                    _log.Msg("[World] Client place blocked — morning world-lock");
                    return false;
                }

                if (Game.I != null && Game.I.isPerformingFirstPlacement)
                {
                    _log.Msg("[World] Client blocked first placement — waiting for host");
                    return false;
                }

                if (handler == null || handler.planningTerrain == null || handler.planningTerrain.cell == null)
                    return false;

                BuildingDefinition def = handler.SelectedDefinition;
                if (def == null && handler.planningTerrain.Building != null)
                    def = handler.planningTerrain.Building.definition;
                if (def == null)
                    return false;

                BuildingPlacementPayload data = default(BuildingPlacementPayload);
                data.TerrainI = handler.planningTerrain.cell.i;
                data.TerrainJ = handler.planningTerrain.cell.j;
                data.BuildingDefId = def.ID;

                _session.SendToHost(CoopMessageType.BuildingPlaceIntent, CoopProtocol.PackBuildingPlacement(data));
                _log.Msg("[World] Client place intent " + data.TerrainI + "," + data.TerrainJ + " def=" + data.BuildingDefId);
            }
            catch (Exception ex)
            {
                _log.Warning("[World] Client place intent failed: " + ex.Message);
            }

            return false;
        }

        public void OnHostBuildingConfirmed(Building building)
        {
            if (!_session.Active || !_session.IsHost || _applyingRemote)
                return;
            if (building == null || building.terrain == null || building.terrain.cell == null || building.definition == null)
                return;

            BuildingPlacementPayload data = default(BuildingPlacementPayload);
            data.TerrainI = building.terrain.cell.i;
            data.TerrainJ = building.terrain.cell.j;
            data.BuildingDefId = building.definition.ID;
            data.BuildingStage = building.CurrentBuildingStage;
            bool first = Game.I != null && Game.I.isPerformingFirstPlacement;
            bool instant = first || building.isInstantBonus || building.IsBuilt;
            if (instant)
                data.Flags = CoopProtocol.BuildingFlagInstant;
            else if (building.IsWip)
                data.Flags = CoopProtocol.BuildingFlagWip;
            else
                data.Flags = 0;
            data.BuilderUids = CaptureBuilderUids(building);

            BroadcastBuildingPlaced(data, isFirst: first);
            BroadcastResourcesSnapshot();

            BroadcastTerrainTileIfSafe(building.terrain);

            try
            {
                if (Game.I != null && Game.I.researchHandler != null)
                    Game.I.researchHandler.CheckResearchEnabling();
                if (ModMain.Instance != null && ModMain.Instance.ResearchSync != null)
                    ModMain.Instance.ResearchSync.MarkDirty();
            }
            catch
            {
            }
        }

        public void OnHostBuildingDestroyed(Building building, bool clearForReset)
        {
            if (!_session.Active || !_session.IsHost || _applyingRemote || clearForReset)
                return;
if (building == null || building.terrain == null || building.terrain.cell == null)
                return;

            BuildingRemovedPayload data = default(BuildingRemovedPayload);
            data.TerrainI = building.terrain.cell.i;
            data.TerrainJ = building.terrain.cell.j;

            _session.Broadcast(CoopMessageType.BuildingRemoved, CoopProtocol.PackBuildingRemoved(data));
            _log.Msg("[World] BuildingRemoved " + data.TerrainI + "," + data.TerrainJ);
            BroadcastResourcesSnapshot();
        }

        private void BroadcastBuildingPlaced(BuildingPlacementPayload data, bool isFirst)
        {
            byte[] packed = CoopProtocol.PackBuildingPlacement(data);
            _session.Broadcast(CoopMessageType.BuildingPlaced, packed);
            if (isFirst)
                _session.Broadcast(CoopMessageType.FirstBuildingPlaced, packed);

            _log.Msg("[World] BuildingPlaced " + data.TerrainI + "," + data.TerrainJ +
                     " def=" + data.BuildingDefId + (isFirst ? " (first)" : ""));
        }

        public void BroadcastResourcesSnapshot()
        {
            BroadcastResourcesSnapshot(forceDuringPassTurn: false);
        }

        public void BroadcastResourcesSnapshot(bool forceDuringPassTurn)
        {
            if (!_session.Active || !_session.IsHost)
                return;
            if (!forceDuringPassTurn && ShouldSuppressPassTurnResourceSync())
            {
                _resourcesDirty = true;
                return;
            }

            ResourcesSnapshotPayload snap = CaptureResourcesSnapshot();
            if (snap.Amounts == null || snap.Amounts.Length == 0)
                return;

            int total = 0;
            for (int i = 0; i < snap.Amounts.Length; i++)
                total += snap.Amounts[i].Available;
            if (total <= 0)
            {
                _log.Msg("[World] Skip ResourcesSnapshot (all zero)");
                return;
            }

            _lastResourceBroadcast = Time.unscaledTime;
            _resourcesDirty = false;
            _session.Broadcast(CoopMessageType.ResourcesSnapshot, CoopProtocol.PackResourcesSnapshot(snap));
            _log.Msg("[World] ResourcesSnapshot (" + snap.Amounts.Length + " entries, total=" + total + ")");

            BroadcastFoodSnapshot();
        }

        public void BroadcastFoodSnapshot()
        {
            if (!_session.Active || !_session.IsHost)
                return;
            if (Game.I == null || Game.I.buildingsHandler == null)
                return;

            try
            {
                FoodSnapshotPayload snap = CaptureFoodSnapshot();
                int spendN = snap.Spending != null ? snap.Spending.Length : 0;
                int assignN = snap.Assignments != null ? snap.Assignments.Length : 0;
                int banN = snap.Bans != null ? snap.Bans.Length : 0;

                if (spendN == 0 && assignN == 0)
                {
                    _log.Msg("[World] Skip FoodSnapshot (empty)");
                    return;
                }

                _lastFoodSnapshot = snap;
                _hasFoodSnapshot = true;
                _session.Broadcast(CoopMessageType.FoodSnapshot, CoopProtocol.PackFoodSnapshot(snap));
                _log.Msg("[World] FoodSnapshot (spend=" + spendN + " assign=" + assignN + " bans=" + banN + ")");
            }
            catch (Exception ex)
            {
                _log.Warning("[World] BroadcastFoodSnapshot: " + ex.Message);
            }
        }

        public void BroadcastWorldBuildingsSnapshot()
        {
            SendBuildingsTo(CSteamID.Nil, broadcast: true);
        }

        public void BroadcastCompactWorldBuildingsSnapshot()
        {
            if (!_session.Active || !_session.IsHost)
                return;
            try
            {
                WorldBuildingsSnapshotPayload snap = default(WorldBuildingsSnapshotPayload);
                snap.Buildings = CaptureAllBuildingsForSnapshot();
                byte[] packed = CoopProtocol.PackWorldBuildingsSnapshot(snap);
                _session.Broadcast(CoopMessageType.WorldBuildingsSnapshot, packed);
                _log.Msg("[World] STAGE HardSync WorldBuildingsSnapshot n=" +
                         (snap.Buildings != null ? snap.Buildings.Length : 0));
            }
            catch (Exception ex)
            {
                _log.Warning("[World] BroadcastCompactWorldBuildingsSnapshot: " + ex.Message);
            }
        }

        public void BroadcastTerrainTileIfSafe(MapTerrain terrain)
        {
            if (!ShouldBroadcastDaytimeTerrainTile())
                return;
            if (terrain == null || terrain.cell == null)
                return;

            try
            {
                TerrainTilePayload tile;
                if (!TryCaptureTerrainTile(terrain, out tile))
                    return;
                _session.Broadcast(CoopMessageType.TerrainChanged, CoopProtocol.PackTerrainTile(tile));
                _log.Msg("[World] TerrainChanged (day cell) " + tile.TerrainI + "," + tile.TerrainJ +
                         " def=" + tile.DefId);
            }
            catch (Exception ex)
            {
                _log.Warning("[World] BroadcastTerrainTileIfSafe: " + ex.Message);
            }
        }

        public void BroadcastTerrainSnapshot()
        {
            if (!_session.Active || !_session.IsHost)
                return;

            try
            {
                TerrainTilePayload[] tiles = CaptureAllTerrainTiles();
                TerrainSnapshotPayload snap = default(TerrainSnapshotPayload);
                snap.Tiles = tiles;
                byte[] packed = CoopProtocol.PackTerrainSnapshot(snap);
                _session.Broadcast(CoopMessageType.TerrainSnapshot, packed);
                _log.Msg("[World] STAGE TerrainSnapshot n=" + (tiles != null ? tiles.Length : 0) +
                         " bytes=" + packed.Length);
            }
            catch (Exception ex)
            {
                _log.Warning("[World] BroadcastTerrainSnapshot: " + ex.Message);
            }
        }

        private bool ShouldBroadcastDaytimeTerrainTile()
        {
            if (!_session.Active || !_session.IsHost || _applyingRemote || _applyingTerrain)
                return false;

            try
            {
                if (Game.I == null || !Game.I.GameIsStarted())
                    return false;
                if (Game.I.IsGeneratingGame || Game.I.IsCurrentlyLoading)
                    return false;
                if (Game.I.IsPassNightTime())
                    return false;
            }
            catch
            {
                return false;
            }

            try
            {
                ModMain mod = ModMain.Instance;
                if (mod != null && mod.TurnSync != null &&
                    mod.TurnSync.CurrentPhase == TurnPhase.PassTurnRunning)
                    return false;
            }
            catch
            {
            }

            return true;
        }

        private TerrainTilePayload[] CaptureAllTerrainTiles()
        {
            List<TerrainTilePayload> list = new List<TerrainTilePayload>(512);
            if (Game.I == null || Game.I.mapController == null)
                return new TerrainTilePayload[0];

            try
            {
                MapTerrain[] terrains = Game.I.mapController.EnumerateTerrains;
                if (terrains == null || terrains.Length == 0)
                {
                    List<MapTerrain> inner = Game.I.mapController.GetAllInnerTerrains();
                    if (inner == null)
                        return new TerrainTilePayload[0];
                    for (int i = 0; i < inner.Count; i++)
                    {
                        TerrainTilePayload tile;
                        if (TryCaptureTerrainTile(inner[i], out tile))
                            list.Add(tile);
                    }
                    return list.ToArray();
                }

                for (int i = 0; i < terrains.Length; i++)
                {
                    TerrainTilePayload tile;
                    if (TryCaptureTerrainTile(terrains[i], out tile))
                        list.Add(tile);
                }
            }
            catch (Exception ex)
            {
                _log.Warning("[World] CaptureAllTerrainTiles: " + ex.Message);
            }

            return list.ToArray();
        }

        private bool TryCaptureTerrainTile(MapTerrain terrain, out TerrainTilePayload tile)
        {
            tile = default(TerrainTilePayload);
            if (terrain == null || terrain.cell == null)
                return false;

            tile.TerrainI = terrain.cell.i;
            tile.TerrainJ = terrain.cell.j;
            tile.DefId = 0;
            try
            {
                TerrainDefinition def = terrain.Definition;
                if (def != null)
                    tile.DefId = def.ID;
            }
            catch
            {
            }

            tile.Height = terrain.Height;
            tile.Cap = terrain.Cap;
            tile.OuterDirection = (int)terrain.OuterDirection;
            tile.Explored = terrain.IsExplored() ? 1 : 0;
            tile.PrevDefId = 0;
            tile.SsDefs = new int[0];
            tile.AdditionalDefIds = new int[0];

            try
            {
                MapTerrainSaveData save = terrain.saveData;
                if (save != null)
                {
                    tile.PrevDefId = save.PrevDefID;
                    tile.Explored = save.Explored;
                    tile.OuterDirection = (int)save.OuterDirection;
                    if (save.ssDefs != null && save.ssDefs.Length > 0)
                    {
                        tile.SsDefs = new int[save.ssDefs.Length];
                        Array.Copy(save.ssDefs, tile.SsDefs, save.ssDefs.Length);
                    }
                }
            }
            catch
            {
            }

            try
            {
                if (AdditionalDefsField != null)
                {
                    List<TerrainDefinition> adds =
                        AdditionalDefsField.GetValue(terrain) as List<TerrainDefinition>;
                    if (adds != null && adds.Count > 0)
                    {
                        List<int> ids = new List<int>(adds.Count);
                        for (int i = 0; i < adds.Count; i++)
                        {
                            if (adds[i] != null)
                                ids.Add(adds[i].ID);
                        }
                        tile.AdditionalDefIds = ids.ToArray();
                    }
                }
            }
            catch
            {
            }

            return true;
        }

        private void ApplyTerrainChanged(byte[] payload)
        {
            TerrainTilePayload tile;
            if (!CoopProtocol.TryReadTerrainTile(payload, out tile))
            {
                _log.Warning("[World] Bad TerrainChanged");
                return;
            }

            if (_applyingTerrain || _applyingRemote)
                return;

            _applyingTerrain = true;
            try
            {
                if (ApplyOneTerrainTile(tile))
                    _log.Msg("[World] Applied TerrainChanged " + tile.TerrainI + "," + tile.TerrainJ +
                             " def=" + tile.DefId + " h=" + tile.Height);
            }
            catch (Exception ex)
            {
                _log.Error("[World] ApplyTerrainChanged: " + ex);
            }
            finally
            {
                _applyingTerrain = false;
            }
        }

        private void ApplyTerrainSnapshot(byte[] payload)
        {
            TerrainSnapshotPayload snap;
            if (!CoopProtocol.TryReadTerrainSnapshot(payload, out snap))
            {
                _log.Warning("[World] Bad TerrainSnapshot");
                return;
            }

            if (_applyingTerrain || _applyingRemote)
                return;

            _applyingTerrain = true;
            int applied = 0;
            try
            {
                TerrainTilePayload[] tiles = snap.Tiles ?? new TerrainTilePayload[0];
                for (int i = 0; i < tiles.Length; i++)
                {
                    if (ApplyOneTerrainTile(tiles[i]))
                        applied++;
                }
                _log.Msg("[World] Applied TerrainSnapshot " + applied + "/" + tiles.Length);
            }
            catch (Exception ex)
            {
                _log.Error("[World] ApplyTerrainSnapshot: " + ex);
            }
            finally
            {
                _applyingTerrain = false;
            }
        }

        private bool ApplyOneTerrainTile(TerrainTilePayload tile)
        {
            if (Game.I == null || Game.I.mapController == null)
                return false;

            MapTerrain terrain = Game.I.mapController.GetTerrainOrNull(tile.TerrainI, tile.TerrainJ);
            if (terrain == null)
            {
                _log.Warning("[World] Terrain apply missing cell " + tile.TerrainI + "," + tile.TerrainJ);
                return false;
            }

            MapController map = Game.I.mapController;
            if (tile.DefId > 0)
            {
                TerrainDefinition def = map.GetTerrainDefByID(tile.DefId);
                if (def != null)
                {
                    TerrainDefinition cur = null;
                    try { cur = terrain.Definition; }
                    catch { }

                    if (cur == null || cur.ID != tile.DefId)
                        terrain.ApplyDefinition(def, ignoreEdges: false, loading: true);
                }
                else
                {
                    _log.Warning("[World] Unknown terrain defId=" + tile.DefId +
                                 " at " + tile.TerrainI + "," + tile.TerrainJ);
                }
            }

            SyncAdditionalDefinitions(terrain, map, tile.AdditionalDefIds);

            terrain.Height = tile.Height;
            terrain.Cap = tile.Cap;
            terrain.OuterDirection = (Direction)tile.OuterDirection;
            terrain.SetExplored(tile.Explored != 0);

            try
            {
                MapTerrainSaveData save = terrain.saveData;
                if (save != null)
                {
                    save.Height = tile.Height;
                    save.Cap = tile.Cap;
                    save.PrevDefID = tile.PrevDefId;
                    save.Explored = tile.Explored;
                    save.OuterDirection = (Direction)tile.OuterDirection;
                    save.ssDefs = tile.SsDefs != null ? (int[])tile.SsDefs.Clone() : new int[0];
                }
            }
            catch
            {
            }

            try { terrain.RefreshExplored(); }
            catch { }
            try
            {
                MethodInfo refreshCap = AccessTools.Method(typeof(MapTerrain), "RefreshCap");
                if (refreshCap != null)
                    refreshCap.Invoke(terrain, null);
            }
            catch { }
            try
            {
                MethodInfo refreshTraits = AccessTools.Method(typeof(MapTerrain), "RefreshPostTraitsChange");
                if (refreshTraits != null)
                    refreshTraits.Invoke(terrain, null);
            }
            catch { }

            return true;
        }

        private void SyncAdditionalDefinitions(MapTerrain terrain, MapController map, int[] wantedIds)
        {
            if (AdditionalDefsField == null)
                return;

            int[] wanted = wantedIds ?? new int[0];
            HashSet<int> wantSet = new HashSet<int>(wanted);
            List<TerrainDefinition> current =
                AdditionalDefsField.GetValue(terrain) as List<TerrainDefinition>;

            if (current != null && current.Count > 0)
            {
                List<TerrainDefinition> toRemove = new List<TerrainDefinition>();
                for (int i = 0; i < current.Count; i++)
                {
                    TerrainDefinition d = current[i];
                    if (d == null)
                        continue;
                    if (!wantSet.Contains(d.ID))
                        toRemove.Add(d);
                }
                for (int i = 0; i < toRemove.Count; i++)
                {
                    try { terrain.RemoveAdditionalDef(toRemove[i]); }
                    catch { }
                }
            }

            HashSet<int> have = new HashSet<int>();
            current = AdditionalDefsField.GetValue(terrain) as List<TerrainDefinition>;
            if (current != null)
            {
                for (int i = 0; i < current.Count; i++)
                {
                    if (current[i] != null)
                        have.Add(current[i].ID);
                }
            }

            for (int i = 0; i < wanted.Length; i++)
            {
                int id = wanted[i];
                if (id <= 0 || have.Contains(id))
                    continue;
                TerrainDefinition add = map.GetTerrainDefByID(id);
                if (add == null)
                    continue;
                try
                {
                    terrain.AppendAdditionalDefinition(add);
                    have.Add(id);
                }
                catch
                {
                    try { terrain.SetTerrainDefinition(add, addition: true); }
                    catch { }
                }
            }
        }

        public void HostPushHardSyncWorld()
        {
            if (!_session.Active || !_session.IsHost)
                return;

            _log.Msg("[World] STAGE HardSync push begin");

            BroadcastTerrainSnapshot();
            BroadcastCompactWorldBuildingsSnapshot();

            BroadcastWorldBuildingsSnapshot();
            BroadcastResourcesSnapshot(forceDuringPassTurn: true);
            BroadcastFoodSnapshot();
            _log.Msg("[World] STAGE HardSync push terrain/world/resources/food done");
        }

        public void SendBuildingsTo(CSteamID remote)
        {
            SendBuildingsTo(remote, broadcast: false);
        }

        private void SendBuildingsTo(CSteamID remote, bool broadcast)
        {
            if (!_session.Active || !_session.IsHost)
                return;
            if (Game.I == null || Game.I.buildingsHandler == null)
                return;

            try
            {
                BuildingPlacementPayload[] buildings = CaptureAllBuildingsForSnapshot();
                int n = buildings != null ? buildings.Length : 0;
                _log.Msg("[World] Send buildings count=" + n +
                         (broadcast ? " (broadcast)" : " → " + remote.m_SteamID));

                for (int i = 0; i < n; i++)
                {
                    byte[] packed = CoopProtocol.PackBuildingPlacement(buildings[i]);
                    if (broadcast)
                        _session.Broadcast(CoopMessageType.BuildingPlaced, packed);
                    else
                        _session.SendTo(remote, CoopMessageType.BuildingPlaced, packed);
                }
            }
            catch (Exception ex)
            {
                _log.Warning("[World] SendBuildingsTo: " + ex.Message);
            }
        }

        public void SendResourcesAndFoodTo(CSteamID remote)
        {
            if (!_session.Active || !_session.IsHost)
                return;

            try
            {
                ResourcesSnapshotPayload snap = CaptureResourcesSnapshot();
                if (snap.Amounts != null && snap.Amounts.Length > 0)
                {
                    int total = 0;
                    for (int i = 0; i < snap.Amounts.Length; i++)
                        total += snap.Amounts[i].Available;
                    if (total > 0)
                    {
                        _session.SendTo(remote, CoopMessageType.ResourcesSnapshot,
                            CoopProtocol.PackResourcesSnapshot(snap));
                        _log.Msg("[World] Send ResourcesSnapshot → " + remote.m_SteamID);
                    }
                }

                if (Game.I != null && Game.I.buildingsHandler != null)
                {
                    FoodSnapshotPayload food = CaptureFoodSnapshot();
                    int spendN = food.Spending != null ? food.Spending.Length : 0;
                    int assignN = food.Assignments != null ? food.Assignments.Length : 0;
                    if (spendN > 0 || assignN > 0)
                    {
                        _session.SendTo(remote, CoopMessageType.FoodSnapshot,
                            CoopProtocol.PackFoodSnapshot(food));
                        _log.Msg("[World] Send FoodSnapshot → " + remote.m_SteamID);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warning("[World] SendResourcesAndFoodTo: " + ex.Message);
            }
        }

        private BuildingPlacementPayload[] CaptureAllBuildingsForSnapshot()
        {
            List<BuildingPlacementPayload> list = new List<BuildingPlacementPayload>(64);
            List<Building> buildings = Game.I.buildingsHandler.AllBuildings();
            if (buildings == null)
                return new BuildingPlacementPayload[0];

            for (int i = 0; i < buildings.Count; i++)
            {
                Building b = buildings[i];
                if (b == null || b.definition == null || b.terrain == null || b.terrain.cell == null)
                    continue;
                if (b.IsState(BuildingState.Planning))
                    continue;

                BuildingPlacementPayload data = default(BuildingPlacementPayload);
                data.TerrainI = b.terrain.cell.i;
                data.TerrainJ = b.terrain.cell.j;
                data.BuildingDefId = b.definition.ID;
                data.BuildingStage = b.CurrentBuildingStage;

                bool instant = b.IsBuilt || b.isInstantBonus || b.IsState(BuildingState.Ruins);
                if (instant)
                    data.Flags = CoopProtocol.BuildingFlagInstant;
                else if (b.IsWip)
                    data.Flags = CoopProtocol.BuildingFlagWip;
                else
                    data.Flags = 0;

                data.BuilderUids = instant ? new int[0] : CaptureBuilderUids(b);
                list.Add(data);
            }

            return list.ToArray();
        }

        private void ApplyWorldBuildingsSnapshot(byte[] payload)
        {
            WorldBuildingsSnapshotPayload snap;
            if (!CoopProtocol.TryReadWorldBuildingsSnapshot(payload, out snap))
            {
                _log.Warning("[World] Bad WorldBuildingsSnapshot");
                return;
            }

            if (!Game.Ready || Game.I == null || Game.I.mapController == null || !Game.I.GameIsStarted())
            {
                QueuePendingWorldSnapshot(payload, "game-not-ready");
                return;
            }

            if (_applyingRemote)
            {
                QueuePendingWorldSnapshot(payload, "busy");
                return;
            }

            _applyingRemote = true;
            _bulkWorldApply = true;
            try
            {
                Game game = Game.I;
                game.hasPlacedFirst = true;
                game.isPerformingFirstPlacement = false;
                if (game.BuildingPlacementHandler != null)
                    game.BuildingPlacementHandler.HideAndDeselect(ignoreTutorial: true);

                BuildingPlacementPayload[] buildings = snap.Buildings ?? new BuildingPlacementPayload[0];
                int removed = RemoveClientBuildingsMissingFromSnapshot(buildings);
                int applied = 0;
                int failed = 0;
                for (int i = 0; i < buildings.Length; i++)
                {
                    if (ApplyOneWorldBuilding(buildings[i]))
                        applied++;
                    else
                        failed++;
                }

                try
                {
                    if (game.BuildingPlacementHandler != null)
                        game.BuildingPlacementHandler.RecomputeDistances();
                    game.mapController.RefreshReachableBorders(
                        isFirstBuilding: false,
                        passThruLoadingCheck: false,
                        ignoreReachableDuringBuildingPhase: true);
                }
                catch
                {
                }

                ScheduleReachableBordersRefresh();
                RestoreFrontMenusAfterClientPlace();
                ScheduleFrontMenusRestore();

                _pendingWorldSnapshot = null;
                _pendingWorldSnapshotTries = 0;
                _log.Msg("[World] Applied WorldBuildingsSnapshot " + applied + "/" + buildings.Length +
                         " failed=" + failed + " removed=" + removed);
            }
            catch (Exception ex)
            {
                _log.Error("[World] ApplyWorldBuildingsSnapshot: " + ex);
                QueuePendingWorldSnapshot(payload, "exception");
            }
            finally
            {
                _bulkWorldApply = false;
                _applyingRemote = false;
            }
        }

        private void QueuePendingWorldSnapshot(byte[] payload, string reason)
        {
            _pendingWorldSnapshot = payload;
            _pendingWorldSnapshotTries++;
            _pendingWorldSnapshotRetryAt = Time.unscaledTime + 0.5f;
            _log.Msg("[World] Queue world snapshot retry (" + reason + ") try=" + _pendingWorldSnapshotTries);
            if (_pendingWorldSnapshotTries > 12)
            {
                _pendingWorldSnapshot = null;
                _log.Warning("[World] Gave up applying world snapshot");
            }
        }

        private int RemoveClientBuildingsMissingFromSnapshot(BuildingPlacementPayload[] hostBuildings)
        {
            if (Game.I == null || Game.I.buildingsHandler == null || Game.I.mapController == null)
                return 0;

            HashSet<long> keep = new HashSet<long>();
            if (hostBuildings != null)
            {
                for (int i = 0; i < hostBuildings.Length; i++)
                {
                    BuildingPlacementPayload b = hostBuildings[i];
                    keep.Add(((long)b.TerrainI << 32) ^ (uint)b.TerrainJ);
                }
            }

            int removed = 0;
            try
            {
                List<Building> all = Game.I.buildingsHandler.AllBuildings();
                if (all == null)
                    return 0;

                List<Building> copy = new List<Building>(all);
                for (int i = 0; i < copy.Count; i++)
                {
                    Building b = copy[i];
                    if (b == null || b.terrain == null || b.terrain.cell == null)
                        continue;
                    if (b.IsState(BuildingState.Planning))
                        continue;

                    int ti = b.terrain.cell.i;
                    int tj = b.terrain.cell.j;
                    long key = ((long)ti << 32) ^ (uint)tj;
                    if (keep.Contains(key))
                        continue;

                    try
                    {
                        b.FinalDestroy(forceClear: true, wasHidden: false, triggerRefresh: true, clearForReset: false);
                        removed++;
                    }
                    catch
                    {
                        try { b.terrain.ClearBuilding(); removed++; }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warning("[World] RemoveClientBuildingsMissingFromSnapshot: " + ex.Message);
            }

            if (removed > 0)
                _log.Msg("[World] STAGE HardSync removed " + removed + " extra client buildings");
            return removed;
        }

        private void RetryPendingWorldSnapshot()
        {
            byte[] payload = _pendingWorldSnapshot;
            if (payload == null)
                return;
            ApplyWorldBuildingsSnapshot(payload);
        }

        private bool ApplyOneWorldBuilding(BuildingPlacementPayload data)
        {
            Game game = Game.I;
            if (game == null || game.buildingsHandler == null || game.mapController == null)
                return false;

            BuildingDefinition def = game.buildingsHandler.GetDefinitionByID(data.BuildingDefId);
            if (def == null)
            {
                _log.Warning("[World] Snapshot unknown def " + data.BuildingDefId);
                return false;
            }

            MapTerrain terrain = game.mapController.GetTerrain(data.TerrainI, data.TerrainJ);
            if (terrain == null)
            {
                _log.Warning("[World] Snapshot missing terrain " + data.TerrainI + "," + data.TerrainJ);
                return false;
            }

            if (terrain.HasBuilding() && terrain.Building != null &&
                terrain.Building.definition != null &&
                terrain.Building.definition.ID == data.BuildingDefId &&
                !terrain.Building.IsState(BuildingState.Planning))
            {
                EnsureBuildersAssigned(terrain.Building, data.BuilderUids);
                ApplyConstructionProgress(terrain.Building, data);
                return true;
            }

            if (terrain.HasBuilding())
                terrain.ClearBuilding();

            bool instant = (data.Flags & CoopProtocol.BuildingFlagInstant) != 0;
            Building built = null;

            if (instant)
            {
                try
                {
                    int terrainId = MapController.MapId(data.TerrainI, data.TerrainJ);
                    built = game.buildingsHandler.InstantBuildBuildingForLoading(
                        terrainId, data.BuildingDefId, min_value: 1, forceBuild: true, autoCreatePavement: false);
                }
                catch (Exception ex)
                {
                    _log.Warning("[World] InstantBuildForLoading: " + ex.Message);
                }

                if (built == null)
                {
                    try
                    {
                        built = game.buildingsHandler.InstantBuildBuilding(
                            terrain, def, min_value: 1, forceBuild: true, forceActive: false,
                            forceFullDwelling: true, loading: true, autoCreatePavement: false,
                            manualBuilding: false, forLoading: true);
                    }
                    catch (Exception ex)
                    {
                        _log.Warning("[World] InstantBuildBuilding: " + ex.Message);
                    }
                }

                if (built == null)
                    built = PlaceBuildingViaGameLogic(terrain, def, instantMode: true, data.BuilderUids);
            }
            else
            {
                built = PlaceBuildingViaGameLogic(terrain, def, instantMode: false, data.BuilderUids);
            }

            if (built == null)
            {
                _log.Warning("[World] Snapshot place failed " + data.TerrainI + "," + data.TerrainJ +
                             " def=" + data.BuildingDefId + " instant=" + instant);
                return false;
            }

            ApplyConstructionProgress(built, data);
            return true;
        }

        private static void ApplyConstructionProgress(Building building, BuildingPlacementPayload data)
        {
            if (building == null)
                return;

            try
            {
                bool instant = (data.Flags & CoopProtocol.BuildingFlagInstant) != 0;
                if (instant)
                {
                    if (!building.IsBuilt)
                    {
                        building.InstantConstruction(loading: true);
                        MelonLogger.Msg("[DotAgeCoop] Building InstantConstruction " +
                                        data.TerrainI + "," + data.TerrainJ +
                                        " def=" + data.BuildingDefId);
                    }
                    return;
                }

                bool wantWip = (data.Flags & CoopProtocol.BuildingFlagWip) != 0 || data.BuildingStage > 0;
                if (wantWip && !building.IsWip && !building.IsBuilt)
                    building.SetState(BuildingState.WIP, clearAction: false, commit: false);

                int stage = data.BuildingStage;
                if (stage < 0)
                    stage = 0;
                int max = building.MaxBuildingStage;
                if (max > 0 && stage > max)
                    stage = max;

                if (wantWip || stage != building.CurrentBuildingStage)
                {
                    building.ForceBuildingStage(stage);
                    MelonLogger.Msg("[DotAgeCoop] Building stage " + data.TerrainI + "," + data.TerrainJ +
                                    " → " + stage + "/" + max +
                                    " (left=" + building.StagesLeftToBuild + ")");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[DotAgeCoop] ApplyConstructionProgress: " + ex.Message);
            }
        }

        private static FoodSnapshotPayload CaptureFoodSnapshot()
        {
            FoodSnapshotPayload snap = default(FoodSnapshotPayload);
            List<ResourceAmount> spending = new List<ResourceAmount>(16);
            List<FoodAssignEntry> assigns = new List<FoodAssignEntry>(64);

            BuildingsHandler bh = Game.I.buildingsHandler;
            if (bh.SpendingFood != null)
            {
                foreach (KeyValuePair<ResourceType, int> kv in bh.SpendingFood)
                {
                    if (kv.Key == null || kv.Value <= 0)
                        continue;
                    ResourceAmount ra = default(ResourceAmount);
                    ra.TypeId = kv.Key.ID;
                    ra.Available = kv.Value;
                    spending.Add(ra);
                }
            }

            if (FoodAssignmentField != null)
            {
                Dictionary<Creature, ResourceType> foodAssign =
                    FoodAssignmentField.GetValue(bh) as Dictionary<Creature, ResourceType>;
                if (foodAssign != null)
                {
                    foreach (KeyValuePair<Creature, ResourceType> kv in foodAssign)
                    {
                        if (kv.Key == null || kv.Value == null || kv.Key.UID == 0)
                            continue;
                        FoodAssignEntry e = default(FoodAssignEntry);
                        e.CreatureUid = kv.Key.UID;
                        e.TypeId = kv.Value.ID;
                        assigns.Add(e);
                    }
                }
            }

            snap.Spending = spending.ToArray();
            snap.Assignments = assigns.ToArray();
            snap.Bans = CaptureFoodBans();
            return snap;
        }

        private static FoodBanEntry[] CaptureFoodBans()
        {
            List<FoodBanEntry> bans = new List<FoodBanEntry>(32);
            try
            {
                if (Game.I == null || Game.I.resourcesHandler == null)
                    return bans.ToArray();

                List<ResourceType> types = Game.I.resourcesHandler.GetAllResourceTypes();
                for (int i = 0; i < types.Count; i++)
                {
                    ResourceType type = types[i];
                    if (type == null)
                        continue;
                    bool pips = type.DisabledAsFood_Pips;
                    bool creatures = type.DisabledAsFood_Creatures;
                    if (!pips && !creatures)
                        continue;

                    FoodBanEntry e = default(FoodBanEntry);
                    e.TypeId = type.ID;
                    e.Flags = 0;
                    if (pips)
                        e.Flags |= FoodBanFlags.PipsDisabled;
                    if (creatures)
                        e.Flags |= FoodBanFlags.CreaturesDisabled;
                    bans.Add(e);
                }
            }
            catch
            {
            }
            return bans.ToArray();
        }

        private bool DeferRemoteWhileClientJoining(CoopMessageType type)
        {
            if (!GameBootstrapService.ClientShouldIgnoreRemoteGameplaySync())
                return false;
            if (Time.frameCount % 180 == 0)
                _log.Msg("[World] STAGE client: defer " + type + " (joining/generating)");
            return true;
        }

        public void SendFoodBanIntent(ResourceType type)
        {
            if (!_session.Active || _session.IsHost || type == null)
                return;
            if (_applyingFoodBans || _applyingFood)
                return;

            if (GameBootstrapService.ClientShouldIgnoreRemoteGameplaySync())
                return;

            FoodBanIntentPayload data = default(FoodBanIntentPayload);
            data.TypeId = type.ID;
            data.Flags = 0;
            if (type.DisabledAsFood_Pips)
                data.Flags |= FoodBanFlags.PipsDisabled;
            if (type.DisabledAsFood_Creatures)
                data.Flags |= FoodBanFlags.CreaturesDisabled;

            _session.SendToHost(CoopMessageType.FoodBanIntent, CoopProtocol.PackFoodBanIntent(data));
            _log.Msg("[World] FoodBanIntent type=" + data.TypeId + " flags=" + data.Flags);
        }

        public void OnHostFoodBanChanged(ResourceType type)
        {
            if (!_session.Active || !_session.IsHost || _applyingFoodBans || _applyingFood)
                return;

            BroadcastFoodSnapshot();
        }

        private static ResourcesSnapshotPayload CaptureResourcesSnapshot()
        {
            ResourcesSnapshotPayload snap = default(ResourcesSnapshotPayload);
            List<ResourceAmount> list = new List<ResourceAmount>(64);

            try
            {
                if (Game.I == null || Game.I.resourcesHandler == null)
                {
                    snap.Amounts = new ResourceAmount[0];
                    return snap;
                }

                ResourcesHandler rh = Game.I.resourcesHandler;
                List<ResourceType> types = rh.GetAllResourceTypes();
                for (int i = 0; i < types.Count; i++)
                {
                    ResourceType type = types[i];
                    if (type == null || type.IsInformational || type.IsFake || type.isInternal)
                        continue;

                    ResourcesContainer container = rh.GetResourceContainer(type);
                    if (container == null)
                        continue;

                    int value = container.CurrentWithoutAdded;
                    if (value == 0 && !type.CountsAsPoolResource)
                        continue;

                    ResourceAmount ra = default(ResourceAmount);
                    ra.TypeId = type.ID;
                    ra.Available = value;
                    list.Add(ra);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[DotAgeCoop] CaptureResources: " + ex.Message);
            }

            snap.Amounts = list.ToArray();
            return snap;
        }

        private void OnMessage(CSteamID remote, CoopMessageType type, byte[] payload)
        {
            switch (type)
            {
                case CoopMessageType.BuildingPlaceIntent:
                    if (_session.IsHost)
                        HandlePlaceIntent(remote, payload);
                    break;

                case CoopMessageType.BuildingPlaced:
                    if (!_session.IsHost)
                    {
                        if (DeferRemoteWhileClientJoining(type))
                            break;
                        ApplyBuildingPlaced(payload);
                    }
                    break;

                case CoopMessageType.FirstBuildingPlaced:

                    if (!_session.IsHost)
                    {
                        if (DeferRemoteWhileClientJoining(type))
                            break;
                        ApplyBuildingPlaced(payload);
                    }
                    break;

                case CoopMessageType.BuildingRemoved:
                    if (!_session.IsHost)
                    {
                        if (DeferRemoteWhileClientJoining(type))
                            break;
                        ApplyBuildingRemoved(payload);
                    }
                    break;

                case CoopMessageType.ResourcesSnapshot:
                    if (!_session.IsHost)
                    {
                        if (DeferRemoteWhileClientJoining(type))
                            break;
                        ApplyResourcesSnapshot(payload);
                    }
                    break;

                case CoopMessageType.FoodSnapshot:
                    if (!_session.IsHost)
                    {
                        if (DeferRemoteWhileClientJoining(type))
                            break;
                        ApplyFoodSnapshot(payload);
                    }
                    break;

                case CoopMessageType.FoodBanIntent:
                    if (_session.IsHost)
                        HandleFoodBanIntent(payload);
                    break;

                case CoopMessageType.WorldBuildingsSnapshot:
                    if (!_session.IsHost)
                    {

                        if (DeferRemoteWhileClientJoining(type))
                            break;
                        ApplyWorldBuildingsSnapshot(payload);
                    }
                    break;

                case CoopMessageType.WorldStateRequest:
                    if (_session.IsHost)
                    {
                        _log.Msg("[World] STAGE host: WorldStateRequest from " + remote.m_SteamID +
                                 " reason=" + CoopProtocol.ReadString(payload) +
                                 " — pushing terrain/buildings/resources/food/research/appearance/roster");
                        BroadcastTerrainSnapshot();
                        BroadcastWorldBuildingsSnapshot();
                        BroadcastResourcesSnapshot();
                        BroadcastFoodSnapshot();
                        if (ModMain.Instance != null)
                        {
                            if (ModMain.Instance.ResearchSync != null)
                                ModMain.Instance.ResearchSync.BroadcastSnapshotImmediate();
                            if (ModMain.Instance.MechanicsSync != null)
                                ModMain.Instance.MechanicsSync.BroadcastSnapshotImmediate();
                            if (ModMain.Instance.ScalesSync != null)
                                ModMain.Instance.ScalesSync.BroadcastScalesSnapshotImmediate(forceDuringPassTurn: true);
                            if (ModMain.Instance.PipAppearance != null)
                                ModMain.Instance.PipAppearance.BroadcastSnapshotImmediate();
                            if (ModMain.Instance.PipOrders != null)
                                ModMain.Instance.PipOrders.FlushFullRosterForJoin("world-state-request");
                        }
                        _log.Msg("[World] STAGE host: world overlay push done");
                    }
                    break;

                case CoopMessageType.TerrainChanged:
                    if (!_session.IsHost)
                    {
                        if (DeferRemoteWhileClientJoining(type))
                            break;
                        ApplyTerrainChanged(payload);
                    }
                    break;

                case CoopMessageType.TerrainSnapshot:
                    if (!_session.IsHost)
                    {
                        if (DeferRemoteWhileClientJoining(type))
                            break;
                        ApplyTerrainSnapshot(payload);
                    }
                    break;

                case CoopMessageType.StateDelta:
                    if (!_session.IsHost)
                    {
                        StateDeltaPayload delta;
                        if (CoopProtocol.TryReadStateDelta(payload, out delta))
                            ApplyLegacyDelta(delta);
                    }
                    break;
            }
        }

        private void HandlePlaceIntent(CSteamID remote, byte[] payload)
        {
            BuildingPlacementPayload data;
            if (!CoopProtocol.TryReadBuildingPlacement(payload, out data))
            {
                _log.Warning("[World] Bad BuildingPlaceIntent");
                return;
            }

            try
            {
                if (Game.I == null || Game.I.isPerformingFirstPlacement)
                {
                    _log.Msg("[World] Ignoring client place during first placement");
                    return;
                }

                BuildingDefinition def = Game.I.buildingsHandler.GetDefinitionByID(data.BuildingDefId);
                MapTerrain terrain = Game.I.mapController.GetTerrain(data.TerrainI, data.TerrainJ);
                if (def == null || terrain == null)
                {
                    _log.Warning("[World] Place intent invalid def/terrain");
                    return;
                }

                bool road;
                bool req;
                bool buildable;
                bool upgrade;
                if (!Game.I.BuildingPlacementHandler.CanBuildBuildingOn(def, terrain, out road, out req, out buildable, out upgrade))
                {
                    _log.Msg("[World] Place intent rejected by CanBuildBuildingOn from " + remote.m_SteamID);
                    return;
                }

                if (terrain.HasBuilding() && terrain.Building != null && terrain.Building.IsState(BuildingState.Planning))
                    terrain.ClearBuilding();

                Building built;
                _applyingRemote = true;
                try
                {
                    built = PlaceBuildingViaGameLogic(terrain, def, instantMode: false);
                }
                finally
                {
                    _applyingRemote = false;
                }

                if (built == null)
                {
                    _log.Warning("[World] Host place-via-logic for client intent returned null");
                    return;
                }

                data.Flags = 0;
                data.BuilderUids = CaptureBuilderUids(built);
                Game.I.mapController.RefreshReachableBorders(
                    isFirstBuilding: false,
                    passThruLoadingCheck: false,
                    ignoreReachableDuringBuildingPhase: true);
                BroadcastBuildingPlaced(data, isFirst: false);
                BroadcastResourcesSnapshot();
                try
                {
                    if (Game.I.researchHandler != null)
                        Game.I.researchHandler.CheckResearchEnabling();
                    if (ModMain.Instance != null && ModMain.Instance.ResearchSync != null)
                        ModMain.Instance.ResearchSync.MarkDirty();
                }
                catch
                {
                }
            }
            catch (Exception ex)
            {
                _applyingRemote = false;
                _log.Error("[World] HandlePlaceIntent: " + ex);
            }
        }

        private void ApplyBuildingPlaced(byte[] payload)
        {
            BuildingPlacementPayload data;
            if (!CoopProtocol.TryReadBuildingPlacement(payload, out data))
                return;

            if (_applyingRemote)
                return;

            _applyingRemote = true;
            try
            {
                Game game = Game.I;
                if (game == null || game.buildingsHandler == null || game.mapController == null)
                    return;

                BuildingDefinition def = game.buildingsHandler.GetDefinitionByID(data.BuildingDefId);
                if (def == null && game.researchRandomizer != null)
                    def = game.researchRandomizer.StartingDwelling;
                if (def == null)
                {
                    _log.Warning("[World] Unknown building def " + data.BuildingDefId);
                    return;
                }

                MapTerrain terrain = game.mapController.GetTerrain(data.TerrainI, data.TerrainJ);
                if (terrain == null)
                {
                    _log.Warning("[World] Missing terrain " + data.TerrainI + "," + data.TerrainJ);
                    return;
                }

                if (terrain.HasBuilding() && terrain.Building != null &&
                    terrain.Building.definition != null &&
                    terrain.Building.definition.ID == data.BuildingDefId &&
                    !terrain.Building.IsState(BuildingState.Planning))
                {
                    bool wasFirst = game.isPerformingFirstPlacement;
                    if (wasFirst)
                        game.hasPlacedFirst = true;
                    EnsureBuildersAssigned(terrain.Building, data.BuilderUids);
                    ApplyConstructionProgress(terrain.Building, data);
                    if (wasFirst && ModMain.Instance != null && ModMain.Instance.Bootstrap != null)
                    {
                        try { ModMain.Instance.Bootstrap.NotifyFirstDwellingMirrored(); }
                        catch { }
                    }

                    RefreshReachabilityAfterPlace(game);
                    return;
                }

                if (game.BuildingPlacementHandler != null)
                    game.BuildingPlacementHandler.HideAndDeselect(ignoreTutorial: true);

                if (terrain.HasBuilding())
                    terrain.ClearBuilding();

                bool instant = (data.Flags & CoopProtocol.BuildingFlagInstant) != 0 ||
                               game.isPerformingFirstPlacement;

                Building built = null;
                if (instant)
                {
                    try
                    {
                        int terrainId = MapController.MapId(data.TerrainI, data.TerrainJ);
                        built = game.buildingsHandler.InstantBuildBuildingForLoading(
                            terrainId, data.BuildingDefId, min_value: 1, forceBuild: true,
                            autoCreatePavement: false);
                    }
                    catch
                    {
                    }

                    if (built == null)
                    {
                        try
                        {
                            built = game.buildingsHandler.InstantBuildBuilding(
                                terrain, def, min_value: 1, forceBuild: true, forceActive: false,
                                forceFullDwelling: true, loading: true, autoCreatePavement: false,
                                manualBuilding: false, forLoading: true);
                        }
                        catch
                        {
                        }
                    }
                }

                if (built == null)
                    built = PlaceBuildingViaGameLogic(terrain, def, instant, data.BuilderUids);

                if (built == null)
                {
                    _log.Warning("[World] Client place-via-logic returned null");
                    return;
                }

                bool wasFirstPlacement = game.isPerformingFirstPlacement;
                if (wasFirstPlacement)
                    game.hasPlacedFirst = true;

                ApplyConstructionProgress(built, data);

                if (wasFirstPlacement && ModMain.Instance != null && ModMain.Instance.Bootstrap != null)
                {
                    try { ModMain.Instance.Bootstrap.NotifyFirstDwellingMirrored(); }
                    catch { }
                }

                RefreshReachabilityAfterPlace(game);

                bool skipHeavyUi = _bulkWorldApply ||
                    (ModMain.Instance != null && ModMain.Instance.Bootstrap != null &&
                     ModMain.Instance.Bootstrap.ShowWaitingPrompt);

                int builders = data.BuilderUids != null ? data.BuilderUids.Length : 0;
                _log.Msg("[World] Applied BuildingPlaced " + data.TerrainI + "," + data.TerrainJ +
                         (instant ? " (instant)" : " (planned)") + " builders=" + builders);

                if (!skipHeavyUi)
                {
                    RestoreFrontMenusAfterClientPlace();
                    ScheduleFrontMenusRestore();
                }
            }
            catch (Exception ex)
            {
                _log.Error("[World] ApplyBuildingPlaced: " + ex);
            }
            finally
            {
                _applyingRemote = false;
            }
        }

        public void RestoreFrontMenusAfterClientPlace()
        {
            try
            {
                if (Game.I == null)
                    return;

                if (Game.I.researchHandler != null)
                    Game.I.researchHandler.CheckResearchEnabling();

                ResearchBarGUI bar = Game.I.researchBarGUI;
                if (bar != null)
                {
                    try { bar.Unlock(); } catch { }
                    try { bar.KeepCurrentPhase(false); } catch { }
                    try { bar.ResetAutoBlock(); } catch { }

                    try { bar.EnableManualControl(); } catch { }
                }

                Game.I.ShowFrontMenus(resetAutoBlock: true);

                if (GConfig.I != null && GConfig.I.EnabledResearch && bar != null)
                {
                    if (Game.I.researchHandler != null && Game.I.researchHandler.IsAvailable())
                    {
                        if (!bar.IsEnabled())
                            bar.Enable();

                        bar.SetPhaseHinted(resetAutoBlock: true, testing: true);
                    }
                }

                _log.Msg("[World] Restored front menus (research enabled=" +
                         (bar != null && bar.IsEnabled()) + ")");
            }
            catch (Exception ex)
            {
                _log.Warning("[World] RestoreFrontMenusAfterClientPlace: " + ex.Message);
            }
        }

        public void ScheduleFrontMenusRestore()
        {
            _pendingFrontMenusRestore = true;
            _pendingFrontMenusRestorePasses = 5;
            _pendingFrontMenusRestoreAt = Time.unscaledTime + 0.1f;
        }

        private void ScheduleReachableBordersRefresh()
        {
            _pendingReachableRefresh = true;
            _pendingReachableRefreshAt = Time.unscaledTime + 0.2f;
        }

        public void RequestReachableBordersRefresh()
        {
            try
            {
                Game game = Game.I;
                if (game != null)
                    RefreshReachabilityAfterPlace(game);
            }
            catch (Exception ex)
            {
                _log.Warning("[World] RequestReachableBordersRefresh: " + ex.Message);
            }
        }

        private void RefreshReachabilityAfterPlace(Game game)
        {
            if (game == null || game.mapController == null)
                return;
            try
            {
                if (game.BuildingPlacementHandler != null)
                    game.BuildingPlacementHandler.RecomputeDistances();
                game.mapController.RefreshReachableBorders(
                    isFirstBuilding: false,
                    passThruLoadingCheck: false,
                    ignoreReachableDuringBuildingPhase: true);
            }
            catch (Exception ex)
            {
                _log.Warning("[World] RefreshReachabilityAfterPlace: " + ex.Message);
            }
            ScheduleReachableBordersRefresh();
        }

        private void RefreshReachableBordersClient()
        {
            try
            {
                if (Game.I == null || Game.I.mapController == null)
                    return;
                if (Game.I.BuildingPlacementHandler != null)
                    Game.I.BuildingPlacementHandler.RecomputeDistances();
                Game.I.mapController.RefreshReachableBorders(
                    isFirstBuilding: false,
                    passThruLoadingCheck: false,
                    ignoreReachableDuringBuildingPhase: true);
                _log.Msg("[World] Deferred reachable borders refresh");
            }
            catch (Exception ex)
            {
                _log.Warning("[World] Reachable borders refresh: " + ex.Message);
            }
        }

        private static Building PlaceBuildingViaGameLogic(
            MapTerrain terrain,
            BuildingDefinition def,
            bool instantMode,
            int[] forcedBuilderUids = null)
        {
            if (terrain == null || def == null)
                return null;

            Building building = terrain.ActionPrepareHouse(def);
            if (building == null)
                return null;

            building.isInstantBonus = instantMode;

            WorkAction buildAction = building.definition.BuildAction;
            ActionAssignment assignment = new ActionAssignment(buildAction, 0);

            bool forceBuilders = forcedBuilderUids != null && forcedBuilderUids.Length > 0;
            bool doBookWorker;
            if (forceBuilders)
            {
                doBookWorker = !instantMode;
            }
            else
            {
                bool noWorkerAtAll;
                bool workersAreOccupied;
                doBookWorker = !instantMode && Game.I.pipsHandler.HasWorkableFreeFor(
                    building, buildAction, 0, out noWorkerAtAll, out workersAreOccupied);
            }

            if (!instantMode && doBookWorker && GConfig.I.canPlanWithoutResources &&
                !Game.I.resourcesHandler.CheckCanPayCost(building.BuildCost, pop: false, building))
            {
                if (!forceBuilders)
                    doBookWorker = false;
            }

            if (forceBuilders)
                PipOrderForce.Begin(forcedBuilderUids);

            try
            {

                if (!Building.PLAN_SEPARATE_FROM_WORKER || doBookWorker)
                    building.BookWorkers(assignment, 1, instantMode);

                MethodInfo confirm = typeof(Building).GetMethod(
                    "_ConfirmPlacementAction",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (confirm == null)
                {
                    MelonLogger.Error("[DotAgeCoop] _ConfirmPlacementAction not found");
                    return null;
                }

                confirm.Invoke(building, new object[] { instantMode, doBookWorker, false });
            }
            finally
            {
                if (forceBuilders)
                    PipOrderForce.End();
            }

            if (forceBuilders && !instantMode)
                EnsureBuildersAssigned(building, forcedBuilderUids);

            return building;
        }

        private static int[] CaptureBuilderUids(Building building)
        {
            if (building == null || building.workerPips == null || building.workerPips.Count == 0)
                return new int[0];

            List<int> uids = new List<int>(building.workerPips.Count);
            for (int i = 0; i < building.workerPips.Count; i++)
            {
                Worker w = building.workerPips[i];
                if (w != null && w.UID != 0)
                    uids.Add(w.UID);
            }
            return uids.ToArray();
        }

        private static void EnsureBuildersAssigned(Building building, int[] builderUids)
        {
            if (building == null || builderUids == null || builderUids.Length == 0)
                return;
            if (Game.I == null || Game.I.pipsHandler == null || building.definition == null)
                return;

            WorkAction buildAction = building.definition.BuildAction;
            ActionAssignment assignment = new ActionAssignment(buildAction, 0);

            HashSet<int> have = new HashSet<int>();
            if (building.workerPips != null)
            {
                for (int i = 0; i < building.workerPips.Count; i++)
                {
                    Worker w = building.workerPips[i];
                    if (w != null)
                        have.Add(w.UID);
                }
            }

            for (int i = 0; i < builderUids.Length; i++)
            {
                int uid = builderUids[i];
                if (uid == 0 || have.Contains(uid))
                    continue;

                Worker worker = Game.I.pipsHandler.GetPipByUID(uid);
                if (worker == null)
                {
                    MelonLogger.Warning("[DotAgeCoop] Builder UID " + uid + " missing on client");
                    continue;
                }

                building.DirectAddWorker(assignment, worker, getElder: false);
            }
        }

        private void ApplyBuildingRemoved(byte[] payload)
        {
            BuildingRemovedPayload data;
            if (!CoopProtocol.TryReadBuildingRemoved(payload, out data))
                return;

            if (_applyingRemote)
                return;

            _applyingRemote = true;
            try
            {
                MapTerrain terrain = Game.I.mapController.GetTerrain(data.TerrainI, data.TerrainJ);
                if (terrain == null)
                    return;

                if (terrain.HasBuilding() && terrain.Building != null)
                {
                    terrain.Building.FinalDestroy(forceClear: true, wasHidden: false, triggerRefresh: true, clearForReset: false);
                }
                else
                {
                    terrain.ClearBuilding();
                }

                _log.Msg("[World] Applied BuildingRemoved " + data.TerrainI + "," + data.TerrainJ);
            }
            catch (Exception ex)
            {
                _log.Error("[World] ApplyBuildingRemoved: " + ex);
            }
            finally
            {
                _applyingRemote = false;
            }
        }

        private void ApplyResourcesSnapshot(byte[] payload)
        {
            ResourcesSnapshotPayload snap;
            if (!CoopProtocol.TryReadResourcesSnapshot(payload, out snap))
                return;
            if (snap.Amounts == null || snap.Amounts.Length == 0)
                return;

            if (ShouldSuppressPassTurnResourceSync())
            {
                if (Time.frameCount % 180 == 0)
                    _log.Msg("[World] STAGE defer ResourcesSnapshot (pass-turn)");
                return;
            }

            if (_applyingRemote)
                return;

            _applyingRemote = true;
            try
            {
                ResourcesHandler rh = Game.I.resourcesHandler;
                int applied = 0;
                int changed = 0;
                for (int i = 0; i < snap.Amounts.Length; i++)
                {
                    ResourceType type = rh.GetResourceTypeByID(snap.Amounts[i].TypeId);
                    if (type == null)
                        continue;

                    ResourcesContainer container = rh.GetResourceContainer(type);
                    if (container == null)
                        continue;

                    int oldStock = container.CurrentWithoutAdded;
                    int value = snap.Amounts[i].Available;
                    if (value < 0)
                        value = 0;
                    container.ForceCurrentValue(value);
                    container.addedValue = 0;
                    container.reservedValue = 0;
                    container.reservedFood = 0;
                    applied++;
                    if (oldStock != value)
                    {
                        changed++;
                        string resName = "?";
                        try
                        {
                            resName = type.GetPrettyName();
                            if (string.IsNullOrEmpty(resName))
                                resName = type.name;
                        }
                        catch
                        {
                            try { resName = type.name; }
                            catch { resName = "id=" + snap.Amounts[i].TypeId; }
                        }
                        EconDebugLog.ResourceSnap(resName, oldStock, value);
                    }
                }

                try
                {

                    if (ModMain.Instance != null && ModMain.Instance.PipOrders != null)
                        ModMain.Instance.PipOrders.RebuildEconomicReservationsFromBuildings();

                    if (_hasFoodSnapshot)
                        ApplyFoodSnapshotPayload(_lastFoodSnapshot);

                    if (Game.I.resourcesPoolGui != null)
                    {
                        Game.I.resourcesPoolGui.UpdateModificationsDueToBuilding(null, commit: true);
                        Game.I.resourcesPoolGui.RefreshShownKnownPopUps(checkShowingOnly: false, forceRereadSettings: false);
                    }
                }
                catch (Exception ex)
                {
                    _log.Warning("[World] Resource UI refresh: " + ex.Message);
                }

                _log.Msg("[World] Applied ResourcesSnapshot (" + applied + "/" + snap.Amounts.Length +
                         ", changed=" + changed + ")");
            }
            catch (Exception ex)
            {
                _log.Error("[World] ApplyResourcesSnapshot: " + ex);
            }
            finally
            {
                _applyingRemote = false;
            }
        }

        private void HandleFoodBanIntent(byte[] payload)
        {
            FoodBanIntentPayload data;
            if (!CoopProtocol.TryReadFoodBanIntent(payload, out data))
                return;
            if (Game.I == null || Game.I.resourcesHandler == null)
                return;

            ResourceType type = Game.I.resourcesHandler.GetResourceTypeByID(data.TypeId);
            if (type == null)
                return;

            _applyingFoodBans = true;
            try
            {
                ResourcesContainer container = Game.I.resourcesHandler.GetResourceContainer(type);
                if (container != null && container.saveData != null)
                {
                    container.saveData.DisabledAsFood =
                        (data.Flags & FoodBanFlags.PipsDisabled) != 0;
                    container.saveData.DisabledAsFood_Creatures =
                        (data.Flags & FoodBanFlags.CreaturesDisabled) != 0;
                }
                else
                {
                    type.DisabledAsFood_Pips = (data.Flags & FoodBanFlags.PipsDisabled) != 0;
                    type.DisabledAsFood_Creatures = (data.Flags & FoodBanFlags.CreaturesDisabled) != 0;
                }
            }
            finally
            {
                _applyingFoodBans = false;
            }

            try
            {
                Game.I.buildingsHandler.HandleBuildingStatusChange(
                    forceRefreshFood: true,
                    forceSSChecksToo: false,
                    commit: true,
                    null);
            }
            catch (Exception ex)
            {
                _log.Warning("[World] FoodBanIntent refresh: " + ex.Message);
                BroadcastFoodSnapshot();
            }

            ScheduleFoodBanUiRefresh();
            _log.Msg("[World] Applied FoodBanIntent type=" + data.TypeId + " flags=" + data.Flags);
        }

        private void ApplyFoodBansPayload(FoodBanEntry[] bans)
        {
            if (Game.I == null || Game.I.resourcesHandler == null)
                return;

            _applyingFoodBans = true;
            try
            {
                ResourcesHandler rh = Game.I.resourcesHandler;
                List<ResourceType> types = rh.GetAllResourceTypes();
                HashSet<int> bannedIds = new HashSet<int>();
                if (bans != null)
                {
                    for (int i = 0; i < bans.Length; i++)
                        bannedIds.Add(bans[i].TypeId);
                }

                for (int i = 0; i < types.Count; i++)
                {
                    ResourceType type = types[i];
                    if (type == null)
                        continue;
                    ResourcesContainer container = rh.GetResourceContainer(type);
                    if (container == null || container.saveData == null)
                        continue;

                    if (!bannedIds.Contains(type.ID))
                    {
                        if (container.saveData.DisabledAsFood || container.saveData.DisabledAsFood_Creatures)
                        {
                            container.saveData.DisabledAsFood = false;
                            container.saveData.DisabledAsFood_Creatures = false;
                        }
                    }
                }

                if (bans != null)
                {
                    for (int i = 0; i < bans.Length; i++)
                    {
                        ResourceType type = rh.GetResourceTypeByID(bans[i].TypeId);
                        if (type == null)
                            continue;
                        ResourcesContainer container = rh.GetResourceContainer(type);
                        if (container == null || container.saveData == null)
                            continue;
                        container.saveData.DisabledAsFood =
                            (bans[i].Flags & FoodBanFlags.PipsDisabled) != 0;
                        container.saveData.DisabledAsFood_Creatures =
                            (bans[i].Flags & FoodBanFlags.CreaturesDisabled) != 0;
                    }
                }

                ScheduleFoodBanUiRefresh();
            }
            finally
            {
                _applyingFoodBans = false;
            }
        }

        private void ScheduleFoodBanUiRefresh()
        {
            RefreshFoodBanUi();
            _foodBanUiDirty = true;
            _foodBanUiRetryAt = Time.unscaledTime + 0.35f;
        }

        private static void RefreshFoodBanUi()
        {
            try
            {
                if (Game.I == null)
                    return;

                RefreshFoodDict(Game.I.ActionsUi);
                RefreshFoodDict(Game.I.ActionsUi_Left);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[DotAgeCoop] RefreshFoodBanUi: " + ex.Message);
            }
        }

        private static void RefreshFoodDict(ActionsUI actionsUi)
        {
            if (actionsUi == null || actionsUi.foodDict == null)
                return;

            try
            {
                actionsUi.foodDict.RefreshUI();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[DotAgeCoop] foodDict.RefreshUI: " + ex.Message);
            }
        }

        public void RefreshFoodUiFromCache()
        {
            if (!_hasFoodSnapshot)
                return;
            try
            {
                ApplyFoodSnapshotPayload(_lastFoodSnapshot);
            }
            catch (Exception ex)
            {
                _log.Warning("[World] RefreshFoodUiFromCache: " + ex.Message);
            }
        }

        private void ApplyFoodSnapshot(byte[] payload)
        {
            FoodSnapshotPayload snap;
            if (!CoopProtocol.TryReadFoodSnapshot(payload, out snap))
                return;

            int spendN = snap.Spending != null ? snap.Spending.Length : 0;
            int assignN = snap.Assignments != null ? snap.Assignments.Length : 0;

            if (spendN == 0 && assignN == 0)
            {
                _log.Msg("[World] Ignore empty FoodSnapshot");
                return;
            }

            _lastFoodSnapshot = snap;
            _hasFoodSnapshot = true;
            ApplyFoodSnapshotPayload(snap);
        }

        private void ApplyFoodSnapshotPayload(FoodSnapshotPayload snap)
        {
            if (Game.I == null || Game.I.buildingsHandler == null || Game.I.resourcesHandler == null)
                return;
            if (_applyingFood)
                return;

            _applyingFood = true;
            try
            {
                BuildingsHandler bh = Game.I.buildingsHandler;
                ResourcesHandler rh = Game.I.resourcesHandler;

                ApplyFoodBansPayload(snap.Bans);

                ClearLocalFoodReservations(bh, rh);

                if (bh.SpendingFood == null)
                    bh.SpendingFood = new Dictionary<ResourceType, int>();

                Dictionary<Creature, ResourceType> foodAssign = null;
                if (FoodAssignmentField != null)
                    foodAssign = FoodAssignmentField.GetValue(bh) as Dictionary<Creature, ResourceType>;
                if (foodAssign == null)
                    foodAssign = new Dictionary<Creature, ResourceType>();

                if (snap.Spending != null)
                {
                    for (int i = 0; i < snap.Spending.Length; i++)
                    {
                        ResourceType type = rh.GetResourceTypeByID(snap.Spending[i].TypeId);
                        int amount = snap.Spending[i].Available;
                        if (type == null || amount <= 0)
                            continue;

                        ResourcesContainer container = rh.GetResourceContainer(type);
                        if (container != null)
                        {
                            try
                            {
                                rh.Reserve(type, amount);
                            }
                            catch
                            {
                            }

                            if (container.reservedValue < amount)
                                container.reservedValue = amount;
                            container.reservedFood = amount;
                        }
                        else
                        {
                            rh.Reserve(type, amount);
                        }

                        bh.SpendingFood[type] = amount;
                    }
                }

                ResourceType noFood = rh.noFoodResourceType;
                if (snap.Assignments != null && Game.I.creaturesHandler != null)
                {
                    for (int i = 0; i < snap.Assignments.Length; i++)
                    {
                        Creature creature = Game.I.creaturesHandler.GetCreatureByUID(snap.Assignments[i].CreatureUid);
                        ResourceType type = rh.GetResourceTypeByID(snap.Assignments[i].TypeId);
                        if (creature == null || type == null)
                            continue;

                        foodAssign[creature] = type;
                        try
                        {
                            if (type == noFood)
                            {
                                if (!TConfig.I.CheatCureHunger &&
                                    !creature.HasSpecialState(Game.I.specialStatesHandler.HungryCSS))
                                    creature.AddSpecialState(Game.I.specialStatesHandler.HungryCSS);
                            }
                            else
                            {
                                creature.RemoveSpecialState(Game.I.specialStatesHandler.HungryCSS);
                            }
                            creature.RefreshFoodFeedback();
                        }
                        catch
                        {
                        }
                    }
                }

                if (FoodAssignmentField != null)
                    FoodAssignmentField.SetValue(bh, foodAssign);

                try
                {
                    if (Game.I.resourcesPoolGui != null)
                    {
                        Game.I.resourcesPoolGui.UpdateModificationsDueToBuilding(null, commit: true);
                        Game.I.resourcesPoolGui.RefreshShownKnownPopUps(checkShowingOnly: false, forceRereadSettings: false);
                    }
                    RefreshFoodDict(Game.I.ActionsUi);
                    RefreshFoodDict(Game.I.ActionsUi_Left);
                }
                catch (Exception ex)
                {
                    _log.Warning("[World] Food UI refresh: " + ex.Message);
                }

                int spendN = snap.Spending != null ? snap.Spending.Length : 0;
                int assignN = snap.Assignments != null ? snap.Assignments.Length : 0;
                _log.Msg("[World] Applied FoodSnapshot (spend=" + spendN + " assign=" + assignN + ")");
            }
            catch (Exception ex)
            {
                _log.Error("[World] ApplyFoodSnapshot: " + ex);
            }
            finally
            {
                _applyingFood = false;
            }
        }

        private static void ClearLocalFoodReservations(BuildingsHandler bh, ResourcesHandler rh)
        {
            if (bh.SpendingFood == null)
                bh.SpendingFood = new Dictionary<ResourceType, int>();

            if (bh.SpendingFood.Count > 0)
            {
                List<ResourceType> keys = new List<ResourceType>(bh.SpendingFood.Keys);
                for (int i = 0; i < keys.Count; i++)
                {
                    ResourceType type = keys[i];
                    int amount = bh.SpendingFood[type];
                    if (type != null && amount > 0 && rh.HasReserved(type, amount))
                        rh.UnreserveOrAdd(type, amount);

                    bh.SpendingFood[type] = 0;
                    ResourcesContainer container = rh.GetResourceContainer(type);
                    if (container != null)
                        container.reservedFood = 0;
                }
            }

            if (FoodAssignmentField != null)
            {
                Dictionary<Creature, ResourceType> foodAssign =
                    FoodAssignmentField.GetValue(bh) as Dictionary<Creature, ResourceType>;
                if (foodAssign != null)
                    foodAssign.Clear();
            }
        }

        private void ApplyLegacyDelta(StateDeltaPayload delta)
        {
            _log.Msg("[Delta apply] " + delta.Kind + " : " + delta.Body);
        }
    }
}
