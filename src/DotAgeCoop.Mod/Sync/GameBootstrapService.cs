using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using DotAgeCoop.Net;
using UnityEngine;

namespace DotAgeCoop.Sync
{

    public sealed class GameBootstrapService
    {
        private readonly CoopSession _session;
        private readonly MelonLogger.Instance _log;
        private bool _joining;
        private bool _broadcastThisRun;
        private GameBootstrapPayload _lastHostPayload;
        private bool _hasHostPayload;
        private string _status = "Waiting for host to start a run";

        private bool _coopForcedSeeded;

        private bool _hostLoadGame;

        private bool _clientLoadJoin;

        private bool _waitingForLoad;
        private bool _loadUnlocked;
        private readonly HashSet<ulong> _loadReadyPeers = new HashSet<ulong>();
        private int _loadReadyCount;
        private int _loadNeededCount = 1;
        private bool _hostMarkedLoadReady;

        private int _runEpoch;

        private int _ignoreHostStartsThroughEpoch;
        private bool _loadInputLayerHeld;
        private object _joinRoutine;
        private float _loadWaitStartedAt;
        private float _nextClientInGameRetryAt;
        private GameBootstrapPayload _pendingClientInGamePayload;
        private bool _hasPendingClientInGame;

        private bool _firstDwellingHudRestored;

        public bool IsJoining { get { return _joining; } }
        public string Status { get { return _status; } }

        public bool IsClientLoadJoin { get { return _clientLoadJoin; } }

        public bool ShouldIgnoreRemoteGameplaySync
        {
            get
            {
                if (_session == null || !_session.Active || _session.IsHost)
                    return false;
                if (_joining)
                    return true;
                try
                {
                    Game g = Game.I;
                    if (g != null && (g.IsGeneratingGame || g.IsCurrentlyLoading))
                        return true;
                }
                catch
                {
                }
                return false;
            }
        }

        public static bool ClientShouldIgnoreRemoteGameplaySync()
        {
            ModMain mod = ModMain.Instance;
            return mod != null && mod.Bootstrap != null && mod.Bootstrap.ShouldIgnoreRemoteGameplaySync;
        }

        public static bool BlocksPlayInput { get; private set; }

        public bool ShowWaitingPrompt
        {
            get
            {
                if (!_session.Active)
                    return false;

                if (ModMain.Instance != null && ModMain.Instance.LoadSync != null)
                {
                    if (ModMain.Instance.LoadSync.IsHostPreTransferActive)
                        return true;
                    if (ModMain.Instance.LoadSync.IsClientStagedLoading)
                        return true;
                }

                return _waitingForLoad && !_loadUnlocked && _loadNeededCount > 1;
            }
        }

        public bool IsPeerLoadWaitActive
        {
            get { return _session.Active && _waitingForLoad && !_loadUnlocked; }
        }

        public GameBootstrapService(CoopSession session, MelonLogger.Instance log)
        {
            _session = session;
            _log = log;
            _session.MessageReceived += OnMessage;

            try
            {
                if (GConfig.I != null && GConfig.I.chosenManualSeed != 0)
                {
                    _log.Msg("[Bootstrap] Clearing persisted chosenManualSeed=" + GConfig.I.chosenManualSeed);
                    GConfig.I.chosenManualSeed = 0;
                    RunModsManager.SetModEnabled(RunModifier.Seeded, false);
                }
            }
            catch
            {
            }
        }

        public void Tick()
        {
            BlocksPlayInput = ShouldHardBlockInput();
            UpdateLoadInputLayer();

            if (!_waitingForLoad || _loadUnlocked || !_session.Active)
                return;

            if (_session.IsHost && !_hostMarkedLoadReady)
                TryMarkHostLoadReady();

            if (!_session.IsHost && _hasPendingClientInGame &&
                Time.unscaledTime >= _nextClientInGameRetryAt)
            {
                _nextClientInGameRetryAt = Time.unscaledTime + 2f;
                NotifyClientInGame(_pendingClientInGamePayload);
            }

            if (_session.IsHost &&
                _loadWaitStartedAt > 0f &&
                Time.unscaledTime - _loadWaitStartedAt >= 45f)
            {
                _log.Warning("[Bootstrap] Load-wait timeout (" + _loadReadyCount + "/" +
                             _loadNeededCount + ") — unlocking anyway");
                _session.Broadcast(CoopMessageType.LoadAllReady);
                OnLoadAllReady();
                return;
            }

            if (!_session.IsHost &&
                _loadWaitStartedAt > 0f &&
                Time.unscaledTime - _loadWaitStartedAt >= 35f)
            {
                _log.Warning("[Bootstrap] Client load-wait timeout — unlocking locally");
                OnLoadAllReady();
            }
        }

        private bool ShouldHardBlockInput()
        {
            if (!_session.Active)
                return false;
            if (ModMain.Instance != null && ModMain.Instance.LoadSync != null)
            {
                if (ModMain.Instance.LoadSync.IsHostPreTransferActive)
                    return true;
                if (ModMain.Instance.LoadSync.IsClientStagedLoading)
                    return true;
            }
            return false;
        }

        public void OnHostBeginningNewGame()
        {

            if (_broadcastThisRun)
                return;

            _hostLoadGame = false;
            ClearStaleCoopSeed();
            StageLog("host", "NewGame begin — broadcast HostGameStarted");
            BroadcastHostGameStarted(lockSeedForCoop: true, reason: "new-game-start");
        }

        public void OnHostBeginningLoadGame()
        {
            if (_broadcastThisRun)
                return;

            _hostLoadGame = true;
            BroadcastHostGameStarted(lockSeedForCoop: false, reason: "load-game");
        }

        public void OnHostEnteredGameplay()
        {
            if (!_hostLoadGame)
            {
                try
                {
                    if (GameReady() && Game.I.HasJustLoadedData())
                        _hostLoadGame = true;
                }
                catch
                {
                }
            }

            BroadcastHostGameStarted(lockSeedForCoop: false, reason: "on-screen");
        }

        public void OnReturningToMainBegin()
        {
            if (_hasHostPayload && _lastHostPayload.RunEpoch > 0)
                _ignoreHostStartsThroughEpoch = Math.Max(_ignoreHostStartsThroughEpoch, _lastHostPayload.RunEpoch);
            else if (_runEpoch > 0)
                _ignoreHostStartsThroughEpoch = Math.Max(_ignoreHostStartsThroughEpoch, _runEpoch);

            _joining = false;
            _clientLoadJoin = false;
            _hasPendingClientInGame = false;
            StopJoinRoutine();
            ReleaseLoadInputLayer();
            BlocksPlayInput = false;

            if (ModMain.Instance != null && ModMain.Instance.LoadSync != null)
                ModMain.Instance.LoadSync.OnReturnedToMain();

            try
            {
                if (_session != null)
                    _session.LeaveAllLobbies("return-to-main-begin");
            }
            catch (Exception ex)
            {
                _log.Warning("[Bootstrap] Leave lobby on return begin: " + ex.Message);
            }

            _log.Msg("[Bootstrap] Returning to main — block HostGameStarted through epoch " +
                     _ignoreHostStartsThroughEpoch);
        }

        public void OnReturnedToMain()
        {

            if (_hasHostPayload && _lastHostPayload.RunEpoch > 0)
                _ignoreHostStartsThroughEpoch = Math.Max(_ignoreHostStartsThroughEpoch, _lastHostPayload.RunEpoch);
            else if (_runEpoch > 0)
                _ignoreHostStartsThroughEpoch = Math.Max(_ignoreHostStartsThroughEpoch, _runEpoch);

            _broadcastThisRun = false;
            _joining = false;
            _hostLoadGame = false;
            _clientLoadJoin = false;
            _hasHostPayload = false;
            _firstDwellingHudRestored = false;
            StopJoinRoutine();
            ClearLoadWait();
            ReleaseLoadInputLayer();
            BlocksPlayInput = false;

            try
            {
                if (_session != null)
                    _session.LeaveAllLobbies("return-to-main");
            }
            catch (Exception ex)
            {
                _log.Warning("[Bootstrap] Leave lobby on return: " + ex.Message);
            }

            try { RestoreMainMenuUiAfterCoop(); }
            catch (Exception ex)
            {
                _log.Warning("[Bootstrap] RestoreMainMenuUi: " + ex.Message);
            }

            _status = "Waiting for host to start a run";
            ClearStaleCoopSeed();
            _log.Msg("[Bootstrap] Reset (returned to main) — ignore HostGameStarted through epoch " +
                     _ignoreHostStartsThroughEpoch);
        }

        private void RestoreMainMenuUiAfterCoop()
        {
            if (ModMain.Instance != null && ModMain.Instance.Overlay != null)
                ModMain.Instance.Overlay.ForceRestoreCursor();

            Game game = Game.I;
            if (game == null)
                return;

            try
            {
                StaticInput.StopAllFocus();
            }
            catch
            {
            }

            try
            {
                game.ClearPauseMenus();
                game.HideFrontMenus(forceHidden: true, performAnimations: false);
                game.HideAlwaysActiveMenus();
                game.ResetFrontMenuLockingToFalse();
            }
            catch (Exception ex)
            {
                _log.Warning("[Bootstrap] Hide game HUD on menu: " + ex.Message);
            }

            try
            {
                if (InputEnabler.Instance != null)
                    InputEnabler.Instance.CloseLayer(InputEnabler.InputLayer.LOADING);
            }
            catch
            {
            }

            CameraIntroController intro = game.cameraIntroController;
            if (intro != null)
            {
                if (intro.titleScreenButtons != null)
                {
                    try { intro.titleScreenButtons.gameObject.SetActive(true); }
                    catch { }
                    try { intro.titleScreenButtons.Initialise(); }
                    catch { }
                }

                try
                {
                    if (intro.CurrentPhase == CameraIntroController.IntroPhase.LEVEL ||
                        intro.IsInGamePhase)
                    {
                        intro.MoveTo_CurrentGameSlot(goUp: false);
                    }
                }
                catch (Exception ex)
                {
                    _log.Warning("[Bootstrap] MoveTo slot after coop: " + ex.Message);
                }
            }

            try
            {
                CurrentSlotMenu slot = CurrentSlotMenu.I;
                if (slot != null &&
                    intro != null &&
                    intro.CurrentPhase == CameraIntroController.IntroPhase.CurrentGameSlot)
                {
                    if (!slot.IsOpen)
                        slot.OpenMenu(skipAnimation: true);
                    UIManager.BaseMenu = slot;
                }
                else
                {
                    TitleMessagePanel title = TitleMessagePanel.I;
                    if (title != null &&
                        intro != null &&
                        intro.CurrentPhase == CameraIntroController.IntroPhase.TITLE)
                    {
                        if (title.TitleScreenPressToPlay != null)
                            title.TitleScreenPressToPlay.gameObject.SetActive(true);
                        if (!title.IsOpen)
                            title.OpenMenu(skipAnimation: true);
                        UIManager.BaseMenu = title;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warning("[Bootstrap] Reopen menu panel: " + ex.Message);
            }

            MelonLogger.Msg("[DotAgeCoop] Main menu UI restored after coop session");
        }

        private void StopJoinRoutine()
        {
            if (_joinRoutine == null)
                return;
            try { MelonCoroutines.Stop(_joinRoutine); }
            catch { }
            _joinRoutine = null;
        }

        public void PrepareClientLoadJoinOnScreen()
        {
            if (!_clientLoadJoin)
                return;

            try
            {
                if (Game.I == null)
                    return;

                Game.I.SetJustLoaded();
                Game.I.hasPlacedFirst = true;
                Game.I.isPerformingFirstPlacement = false;
                _log.Msg("[Bootstrap] Client load-join: SetJustLoaded (skip intro cutscene)");
            }
            catch (Exception ex)
            {
                _log.Warning("[Bootstrap] PrepareClientLoadJoinOnScreen: " + ex.Message);
            }
        }

        private void ClearStaleCoopSeed()
        {
            if (!_coopForcedSeeded)
                return;

            try
            {
                if (GConfig.I != null)
                    GConfig.I.chosenManualSeed = 0;
                RunModsManager.SetModEnabled(RunModifier.Seeded, false);
            }
            catch
            {
            }

            _coopForcedSeeded = false;
        }

        private void BroadcastHostGameStarted(bool lockSeedForCoop, string reason)
        {
            if (!_session.Active || !_session.IsHost)
                return;

            if (_joining)
                return;

            if (_broadcastThisRun)
                return;

            try
            {
                if (lockSeedForCoop)
                    LockSeedForCoop();

                GameBootstrapPayload payload = CaptureFromHost();
                if (_hostLoadGame)
                    payload.IsLoadGame = true;

                if (payload.Seed == 0)
                {
                    _log.Warning("[Bootstrap] Seed still 0 (" + reason + ") — defer broadcast");
                    return;
                }

                _runEpoch++;
                payload.RunEpoch = _runEpoch;

                _broadcastThisRun = true;
                _lastHostPayload = payload;
                _hasHostPayload = true;
                _status = "Host started run — seed " + payload.Seed;
                _log.Msg("[Bootstrap] Broadcasting HostGameStarted (" + reason + ") seed=" + payload.Seed +
                         " elder=" + payload.ElderId + " diff=" + payload.Difficulty +
                         " day=" + payload.Day + " load=" + payload.IsLoadGame +
                         " epoch=" + payload.RunEpoch);
                BeginLoadWait(isHost: true);
                _session.Broadcast(CoopMessageType.HostGameStarted, CoopProtocol.PackBootstrap(payload));
                BroadcastLoadReadyStatus();
                TryMarkHostLoadReady();
            }
            catch (Exception ex)
            {
                _log.Error("[Bootstrap] Capture/broadcast failed: " + ex);
            }
        }

        private void LockSeedForCoop()
        {
            int seed;
            unchecked
            {
                seed = UnityEngine.Random.Range(1, int.MaxValue);
                seed ^= Environment.TickCount;
                seed ^= Guid.NewGuid().GetHashCode();
                seed &= int.MaxValue;
            }
            if (seed <= 0)
                seed = 1;

            RunModsManager.SetModEnabled(RunModifier.Seeded, true);
            GConfig.I.chosenManualSeed = seed;
            _coopForcedSeeded = true;

            MelonLogger.Msg("[DotAgeCoop] Coop seed locked (fresh): " + seed);
        }

        private void OnMessage(Steamworks.CSteamID remote, CoopMessageType type, byte[] payload)
        {
            switch (type)
            {
                case CoopMessageType.HostGameStarted:
                    HandleHostGameStarted(payload);
                    break;

                case CoopMessageType.HostGameRequest:
                    if (!_session.IsHost)
                        break;

                    if (_hasHostPayload)
                    {

                        _log.Msg("[Bootstrap] HostGameRequest from " + remote.m_SteamID +
                                 " — resending epoch " + _lastHostPayload.RunEpoch);
                        _session.SendTo(remote, CoopMessageType.HostGameStarted, CoopProtocol.PackBootstrap(_lastHostPayload));
                    }
                    else if (GameReady() && Game.I.GameIsStarted())
                    {

                        OnHostEnteredGameplay();
                        if (_hasHostPayload)
                            _session.SendTo(remote, CoopMessageType.HostGameStarted, CoopProtocol.PackBootstrap(_lastHostPayload));
                    }
                    break;

                case CoopMessageType.ClientInGame:
                    if (_session.IsHost)
                    {
                        _log.Msg("[Bootstrap] Client in game: " + CoopProtocol.ReadString(payload));
                        ulong peerId = remote.m_SteamID;
                        if (peerId == 0)
                            peerId = 2UL;
                        MarkPeerLoadReady(peerId);

                        if (ModMain.Instance != null && ModMain.Instance.MechanicsSync != null)
                            ModMain.Instance.MechanicsSync.SendSnapshotTo(remote);
                        if (ModMain.Instance != null && ModMain.Instance.ScalesSync != null)
                            ModMain.Instance.ScalesSync.SendScalesSnapshotTo(remote);
                        TryUnlockLoadWait();
                    }
                    break;

                case CoopMessageType.LoadReadyStatus:
                    if (!_session.IsHost)
                        ApplyLoadReadyStatus(payload);
                    break;

                case CoopMessageType.LoadAllReady:
                    OnLoadAllReady();
                    break;
            }
        }

        private void HandleHostGameStarted(byte[] payload)
        {
            if (_session.IsHost)
                return;

            GameBootstrapPayload data;
            if (!CoopProtocol.TryReadBootstrap(payload, out data))
            {
                _log.Warning("[Bootstrap] Bad HostGameStarted payload");
                return;
            }

            if (data.RunEpoch > 0 && data.RunEpoch <= _ignoreHostStartsThroughEpoch)
            {
                _log.Msg("[Bootstrap] Ignoring HostGameStarted epoch " + data.RunEpoch +
                         " (blocked through " + _ignoreHostStartsThroughEpoch + ")");
                return;
            }

            if (_ignoreHostStartsThroughEpoch > 0 && data.RunEpoch <= 0 &&
                _hasHostPayload && data.Seed == _lastHostPayload.Seed)
            {
                _log.Msg("[Bootstrap] Ignoring legacy HostGameStarted for seed " + data.Seed);
                return;
            }

            _lastHostPayload = data;
            _hasHostPayload = true;
            BeginLoadWait(isHost: false);

            if (_joining)
            {
                _log.Msg("[Bootstrap] Already joining — ignore duplicate");
                return;
            }

            if (GameReady() && Game.I.GameIsStarted())
            {
                _status = "Already in a run (seed host=" + data.Seed + ")";
                _log.Msg("[Bootstrap] Client already in game — skip auto NewGame for now");
                if (data.IsLoadGame && ModMain.Instance != null && ModMain.Instance.LoadSync != null)
                    ModMain.Instance.LoadSync.BeginClientStagedLoad(data);
                else
                    BeginClientInGameNotify(data);
                return;
            }

            _status = "Joining host run seed " + data.Seed +
                      (data.IsLoadGame ? " (load)" : "") + "...";
            _joining = true;
            _clientLoadJoin = data.IsLoadGame;

            if (!data.IsLoadGame && ModMain.Instance != null && ModMain.Instance.LoadSync != null)
                ModMain.Instance.LoadSync.AbortClientTransferForNewGame();

            StopJoinRoutine();
            _joinRoutine = MelonCoroutines.Start(ClientJoinRoutine(data));
        }

        private IEnumerator ClientJoinRoutine(GameBootstrapPayload data)
        {

            if (data.IsLoadGame)
            {
                _log.Msg("[Bootstrap] Load-game join — load pre-received host save");
                _status = "Загрузка сохранения…";
                yield return null;

                try { PrepareClientUiForGameplay(); }
                catch { }

                if (ModMain.Instance == null || ModMain.Instance.LoadSync == null)
                {
                    _joining = false;
                    _clientLoadJoin = false;
                    _status = "LoadSync missing";
                    yield break;
                }

                ModMain.Instance.LoadSync.BeginClientLoadFromReceivedSave(data);

                float t0 = Time.unscaledTime;
                while (Time.unscaledTime - t0 < 180f)
                {
                    if (!ModMain.Instance.LoadSync.IsClientStagedLoading)
                        break;
                    try { PrepareClientUiForGameplay(); }
                    catch { }
                    yield return null;
                }

                try { PrepareClientUiForGameplay(); }
                catch { }

                _joining = false;
                _clientLoadJoin = false;

                if (GameReady() && Game.I.GameIsStarted())
                {
                    _status = "In host run (loaded save) — seed " + data.Seed;
                    _log.Msg("[Bootstrap] Client loaded host save");
                }
                else
                {
                    _status = "Save load failed or timed out";
                    _log.Warning("[Bootstrap] " + _status);
                }

                yield break;
            }

            _log.Msg("[Bootstrap] STAGE client: Applying host config and starting NewGame_Immediate seed=" +
                     data.Seed + " elder=" + data.ElderId);

            yield return null;
            yield return null;

            try
            {
                ApplyHostConfig(data);
                StageLog("client", "config applied — closing title UI before NewGame");
                PrepareClientUiForGameplay();
            }
            catch (Exception ex)
            {
                _joining = false;
                _clientLoadJoin = false;
                _status = "Apply config failed";
                _log.Error("[Bootstrap] STAGE client FAIL ApplyHostConfig: " + ex);
                yield break;
            }

            yield return null;

            try
            {
                if (Game.I == null || Game.I.startGameController == null)
                    throw new Exception("StartGameController missing");

                StageLog("client", "calling NewGame_Immediate()");
                Game.I.startGameController.NewGame_Immediate();
            }
            catch (Exception ex)
            {
                _joining = false;
                _clientLoadJoin = false;
                _status = "NewGame failed";
                _log.Error("[Bootstrap] STAGE client FAIL NewGame_Immediate: " + ex);
                yield break;
            }

            float tNew = Time.unscaledTime;
            StageLog("client", "waiting GameIsStarted (max 120s)…");
            while (Time.unscaledTime - tNew < 120f)
            {
                if (!GameReady())
                {
                    yield return null;
                    continue;
                }

                try
                {
                    if (Time.frameCount % 120 == 0)
                    {
                        StageLog("client", "wait… generating=" + Game.I.IsGeneratingGame +
                                 " loading=" + Game.I.IsCurrentlyLoading +
                                 " started=" + Game.I.GameIsStarted() +
                                 " elder=" + ElderStatus());
                    }
                }
                catch
                {
                }

                if (Game.I.GameIsStarted())
                    break;

                yield return null;
            }

            StageLog("client", "GameIsStarted=" + (GameReady() && Game.I.GameIsStarted()) +
                     " elder=" + ElderStatus() + " — post-start (preserve intro cutscene)");

            float cleanupUntil = Time.unscaledTime + 0.8f;
            while (Time.unscaledTime < cleanupUntil)
            {
                try { CloseTitleMenusOnly(resetCameraToLevel: false); }
                catch { }
                yield return null;
                yield return null;
            }

            bool started = GameReady() && Game.I.GameIsStarted();
            _joining = false;

            if (!data.IsLoadGame)
                _clientLoadJoin = false;
            _firstDwellingHudRestored = false;

            if (started)
            {
                _status = "In host run — seed " + data.Seed;
                StageLog("client", "entered OnScreen elder=" + ElderStatus() +
                         " — ClientInGame (intro cutscene continues)");
                BeginClientInGameNotify(data);

                if (data.IsLoadGame)
                {
                    try { RestoreGameplayHudAfterJoin(resetCameraToLevel: false); }
                    catch (Exception ex)
                    {
                        _log.Warning("[Bootstrap] RestoreGameplayHud: " + ex.Message);
                    }
                    RequestHostWorldOverlay("post-load-join");
                }
            }
            else
            {
                _status = "Join timed out / game not started";
                _log.Warning("[Bootstrap] STAGE client FAIL: " + _status + " elder=" + ElderStatus());
                try { RestoreGameplayHudAfterJoin(resetCameraToLevel: true); }
                catch { }
                BeginClientInGameNotify(data);
                RequestHostWorldOverlay("post-newgame-timeout");
            }
        }

        private void BeginClientInGameNotify(GameBootstrapPayload data)
        {
            _pendingClientInGamePayload = data;
            _hasPendingClientInGame = true;
            _nextClientInGameRetryAt = 0f;
            NotifyClientInGame(data);
        }

        private void NotifyClientInGame(GameBootstrapPayload data)
        {
            string tag = "seed=" + data.Seed + (data.IsLoadGame ? ";load=1" : "");
            _session.SendToHost(CoopMessageType.ClientInGame, CoopProtocol.StringPayload(tag));
            _log.Msg("[Bootstrap] Sent ClientInGame (" + tag + ")");
        }

        public void NotifyFirstDwellingMirrored()
        {
            if (_session.IsHost)
                return;
            if (_firstDwellingHudRestored)
                return;
            _firstDwellingHudRestored = true;

            StageLog("client", "first dwelling mirrored — HUD + ClientInGame");
            try
            {
                if (Game.I != null)
                {
                    Game.I.hasPlacedFirst = true;
                    Game.I.isPerformingFirstPlacement = false;
                }
            }
            catch
            {
            }
            try { RestoreGameplayHudAfterJoin(resetCameraToLevel: false); }
            catch { }
            if (_hasHostPayload)
                BeginClientInGameNotify(_lastHostPayload);

            try
            {
                if (ModMain.Instance != null && ModMain.Instance.GameSync != null)
                    ModMain.Instance.GameSync.RequestReachableBordersRefresh();
            }
            catch
            {
            }
            RequestHostWorldOverlay("first-dwelling");
        }

        private void RestoreGameplayHudAfterJoin(bool resetCameraToLevel = false)
        {
            Game game = Game.I;
            if (game == null)
                return;

            try { CloseTitleMenusOnly(resetCameraToLevel); }
            catch { }

            try
            {
                game.ResetFrontMenuLockingToFalse();
                game.ShowAlwaysActiveMenus();

                if (!game.isPerformingFirstPlacement || game.hasPlacedFirst)
                    game.ShowFrontMenus(resetAutoBlock: true);
                else
                {

                    try { game.ShowAlwaysActiveMenus(); }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                _log.Warning("[Bootstrap] RestoreGameplayHud: " + ex.Message);
            }

            MelonLogger.Msg("[DotAgeCoop] Gameplay HUD restored after coop join (cameraReset=" +
                            resetCameraToLevel + ")");
        }

        private static void CloseTitleMenusOnly(bool resetCameraToLevel)
        {
            Game game = Game.I;
            if (game == null)
                return;

            try { StaticInput.StopAllFocus(); }
            catch { }

            try { game.ClearPauseMenus(); }
            catch { }

            CameraIntroController intro = game.cameraIntroController;
            if (intro != null && intro.titleScreenButtons != null)
            {
                try { intro.titleScreenButtons.gameObject.SetActive(false); }
                catch { }
            }

            TitleMessagePanel title = TitleMessagePanel.I;
            if (title != null)
            {
                StopAllCoroutinesSafe(title);
                try { TitleMessagePanel.isSwitchingToMenuPhase = false; }
                catch { }
                if (title.TitleScreenPressToPlay != null)
                {
                    try { title.TitleScreenPressToPlay.gameObject.SetActive(false); }
                    catch { }
                }
            }

            ForceHideTitleLogo(intro, title);

            ForceCloseIntroPanel(title);
            ForceCloseIntroPanel(NewGameMenu.I);
            ForceCloseIntroPanel(CreditsMenu.I);
            ForceCloseIntroPanel(SaveSlotsMenu.I);
            ForceCloseIntroPanel(CurrentSlotMenu.I);

            if (resetCameraToLevel && intro != null)
            {
                bool alreadyInRun = false;
                try { alreadyInRun = game.GameIsStarted(); }
                catch { }
                if (!alreadyInRun)
                {
                    try { intro.ForceToLevel(); }
                    catch { }
                }
            }

            UIManager.BaseMenu = null;
        }

        private static void ForceHideTitleLogo(CameraIntroController intro, TitleMessagePanel title)
        {
            if (title != null && title.logoTweenController != null)
            {
                StopAllCoroutinesSafe(title.logoTweenController);
                try { title.logoTweenController.HideTitle(); }
                catch { }
                try { title.logoTweenController.gameObject.SetActive(false); }
                catch { }
            }

            if (intro == null || intro.logoAnimation == null)
                return;

            LogoAnimation logo = intro.logoAnimation;
            try
            {
                if (logo.logoIntroMessage != null)
                    logo.logoIntroMessage.SetActive(choice: false);
            }
            catch
            {
            }

            try { logo.SetActive(choice: false); }
            catch
            {
                try { logo.gameObject.SetActive(false); }
                catch { }
            }

            StopAllCoroutinesSafe(logo);
        }

        private void RequestHostWorldOverlay(string reason)
        {
            if (_session.IsHost || !_session.Active)
                return;
            try
            {
                StageLog("client", "request world overlay (" + reason + ")");
                _session.SendToHost(CoopMessageType.WorldStateRequest,
                    CoopProtocol.StringPayload("overlay:" + reason));
                if (ModMain.Instance != null && ModMain.Instance.GameSync != null)
                    ModMain.Instance.GameSync.RequestReachableBordersRefresh();
            }
            catch (Exception ex)
            {
                _log.Warning("[Bootstrap] World overlay request failed: " + ex.Message);
            }
        }

        private void StageLog(string who, string msg)
        {
            _log.Msg("[Bootstrap] STAGE " + who + ": " + msg);
        }

        private static string ElderStatus()
        {
            try
            {
                if (Game.I == null)
                    return "game=null";
                Elder e = Game.I.elder;
                if (e == null || !e)
                    return "missing";
                return "uid=" + e.UID + " name=" + (e.OwnName(forceNoColor: true) ?? "?");
            }
            catch (Exception ex)
            {
                return "err:" + ex.Message;
            }
        }

        private static void PrepareClientUiForGameplay()
        {
            Game game = Game.I;
            if (game == null)
                return;

            try
            {
                StaticInput.StopAllFocus();
            }
            catch
            {
            }

            game.ClearPauseMenus();
            game.HideFrontMenus(forceHidden: true, performAnimations: false);
            game.HideAlwaysActiveMenus();

            CameraIntroController intro = game.cameraIntroController;
            if (intro != null)
            {
                StopAllCoroutinesSafe(intro);
                if (intro.introAreaController != null)
                {
                    try
                    {
                        intro.introAreaController.StopAndForcePanel(TitleMessagePanel.I);
                    }
                    catch
                    {
                        StopAllCoroutinesSafe(intro.introAreaController);
                    }
                }

                if (intro.titleScreenButtons != null)
                    intro.titleScreenButtons.gameObject.SetActive(false);
            }

            TitleMessagePanel title = TitleMessagePanel.I;
            if (title != null)
            {
                StopAllCoroutinesSafe(title);
                try { TitleMessagePanel.isSwitchingToMenuPhase = false; }
                catch { }

                if (title.TitleScreenPressToPlay != null)
                {
                    try { title.TitleScreenPressToPlay.gameObject.SetActive(false); }
                    catch { }
                }
            }

            ForceHideTitleLogo(intro, title);

            ForceCloseIntroPanel(title);
            ForceCloseIntroPanel(NewGameMenu.I);
            ForceCloseIntroPanel(CreditsMenu.I);
            ForceCloseIntroPanel(SaveSlotsMenu.I);
            ForceCloseIntroPanel(CurrentSlotMenu.I);

            if (intro != null)
            {
                if (intro.introAreaController != null)
                {
                    try
                    {

                        ForceStopIntroSea(intro.introAreaController);
                        intro.introAreaController.DespawnEverything();
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Warning("[DotAgeCoop] DespawnEverything: " + ex.Message);
                    }
                }

                intro.ForceToLevel();
                intro.DisablePanels();
            }

            UIManager.BaseMenu = null;

            MelonLogger.Msg("[DotAgeCoop] Client UI forced into gameplay (intro panels closed)");
        }

        private static void StopAllCoroutinesSafe(MonoBehaviour behaviour)
        {
            if (behaviour == null)
                return;
            try
            {
                behaviour.StopAllCoroutines();
            }
            catch
            {
            }
        }

        private static void ForceStopIntroSea(IntroAreaController introArea)
        {
            if (introArea == null || introArea.seaArea == null)
                return;

            SeaAreaController sea = introArea.seaArea;
            try
            {
                sea.StopMoving(-1);
            }
            catch
            {
            }

            StopAllCoroutinesSafe(sea);

            try
            {
                sea.DespawnEveything();
            }
            catch
            {
            }

            try
            {
                FieldInfo field = AccessTools.Field(typeof(SeaAreaController), "spawnedObjects");
                if (field != null)
                {
                    System.Collections.IList list = field.GetValue(sea) as System.Collections.IList;
                    if (list != null)
                    {
                        for (int i = list.Count - 1; i >= 0; i--)
                        {
                            object obj = list[i];
                            if (obj == null)
                                continue;
                            MethodInfo free = obj.GetType().GetMethod("Free", BindingFlags.Instance | BindingFlags.Public);
                            if (free != null)
                                free.Invoke(obj, null);
                        }
                        list.Clear();
                    }
                }
            }
            catch
            {
            }

            try
            {
                if (sea.gameObject != null)
                    sea.gameObject.SetActive(false);
            }
            catch
            {
            }
        }

        private static void ForceCloseIntroPanel(MessagePanel panel)
        {
            if (panel == null)
                return;

            StopAllCoroutinesSafe(panel);

            try
            {

                if (panel.IsOpen)
                    panel.CloseMessage(forceClear: true);

                panel.Hide();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[DotAgeCoop] Close panel " + panel.name + ": " + ex.Message);
            }

            try
            {
                if (panel.gameObject != null)
                    panel.gameObject.SetActive(false);
            }
            catch
            {
            }
        }

        private static GameBootstrapPayload CaptureFromHost()
        {
            GameBootstrapPayload data = default(GameBootstrapPayload);

            if (GConfig.I.chosenManualSeed != 0)
                data.Seed = GConfig.I.chosenManualSeed;
            else if (Game.I.saveData != null && Game.I.saveData.seed != 0)
                data.Seed = Game.I.saveData.seed;
            else
            {
                try
                {
                    data.Seed = GameRandom.GetRunSeed();
                }
                catch
                {
                }
            }

            if (data.Seed == 0)
                data.Seed = GConfig.I.chosenManualSeed;

            data.Difficulty = (int)Game.I.difficultyHandler.GetCurrentDifficultyChoice();
            data.ElderId = Game.I.elderDefinitionHandler.GetCurrentElderDefinition().ID;
            data.Day = Game.CurrentDay;

            bool[] mods = GConfig.I.RunConfig.EnabledMods;
            data.EnabledMods = new bool[mods.Length];
            Array.Copy(mods, data.EnabledMods, mods.Length);

            data.IsLoadGame = false;
            try
            {
                if (Game.I != null && Game.I.HasJustLoadedData())
                    data.IsLoadGame = true;
            }
            catch
            {
            }

            return data;
        }

        public static void ApplyHostConfigPublic(GameBootstrapPayload data)
        {
            ApplyHostConfig(data);
        }

        private static void ApplyHostConfig(GameBootstrapPayload data)
        {
            Game.I.elderDefinitionHandler.SetElderDefinitionByID((ElderID)data.ElderId);
            Game.I.difficultyHandler.SetDifficultyChoice((DifficultyChoice)data.Difficulty);

            bool[] mods = data.EnabledMods;
            if (mods != null)
            {
                for (int i = 0; i < mods.Length && i < GConfig.I.RunConfig.EnabledMods.Length; i++)
                    GConfig.I.RunConfig.EnabledMods[i] = mods[i];
            }

            RunModsManager.SetModEnabled(RunModifier.Seeded, true);
            GConfig.I.chosenManualSeed = data.Seed;

            MelonLogger.Msg("[DotAgeCoop] Config applied: seed=" + data.Seed +
                            " elder=" + data.ElderId + " diff=" + data.Difficulty);
        }

        private void BeginLoadWait(bool isHost)
        {
            if (!_session.Active)
                return;

            int needed = Math.Max(1, _session.CoopMemberCount);
            if (needed <= 1)
            {
                ClearLoadWait();
                return;
            }

            if (_loadUnlocked && _broadcastThisRun)
                return;

            _waitingForLoad = true;
            _loadUnlocked = false;
            _loadNeededCount = needed;
            _loadReadyCount = 0;
            _hostMarkedLoadReady = false;
            _loadWaitStartedAt = Time.unscaledTime;
            if (isHost)
                _loadReadyPeers.Clear();

            BlocksPlayInput = ShouldHardBlockInput();
            _log.Msg("[Bootstrap] Load wait started — need " + needed + " peers");
        }

        private void ClearLoadWait()
        {
            _waitingForLoad = false;
            _loadUnlocked = false;
            _loadReadyCount = 0;
            _loadNeededCount = 1;
            _hostMarkedLoadReady = false;
            _loadReadyPeers.Clear();
            _loadWaitStartedAt = 0f;
            _hasPendingClientInGame = false;
            BlocksPlayInput = false;
            ReleaseLoadInputLayer();
        }

        private void TryMarkHostLoadReady()
        {
            if (!_session.IsHost || !_waitingForLoad || _loadUnlocked || _hostMarkedLoadReady)
                return;
            if (!GameReady())
                return;

            bool ready;
            try
            {
                if (_hostLoadGame || (_hasHostPayload && _lastHostPayload.IsLoadGame))
                    ready = Game.I.IsPlayTime();
                else
                    ready = Game.I.GameIsStarted();
            }
            catch
            {
                return;
            }

            if (!ready)
                return;

            _hostMarkedLoadReady = true;
            MarkPeerLoadReady(_session.SelfId);
            TryUnlockLoadWait();
        }

        private void MarkPeerLoadReady(ulong peerId)
        {
            if (!_session.IsHost || !_waitingForLoad || _loadUnlocked)
                return;
            if (peerId == 0)
                return;

            if (_loadReadyPeers.Add(peerId))
            {
                _loadReadyCount = _loadReadyPeers.Count;
                _loadNeededCount = Math.Max(_loadNeededCount, _session.CoopMemberCount);
                _log.Msg("[Bootstrap] Load ready peer " + peerId + " (" + _loadReadyCount + "/" + _loadNeededCount + ")");
                BroadcastLoadReadyStatus();
            }
        }

        private void BroadcastLoadReadyStatus()
        {
            if (!_session.IsHost || !_session.Active)
                return;

            _loadReadyCount = _loadReadyPeers.Count;
            _loadNeededCount = Math.Max(1, Math.Max(_loadNeededCount, _session.CoopMemberCount));
            _session.Broadcast(
                CoopMessageType.LoadReadyStatus,
                CoopProtocol.PackLoadReadyStatus(_loadReadyCount, _loadNeededCount));
        }

        private void ApplyLoadReadyStatus(byte[] payload)
        {
            int ready;
            int needed;
            if (!CoopProtocol.TryReadLoadReadyStatus(payload, out ready, out needed))
                return;

            if (_loadUnlocked)
                return;

            _waitingForLoad = true;
            if (_loadWaitStartedAt <= 0f)
                _loadWaitStartedAt = Time.unscaledTime;
            _loadReadyCount = ready;
            _loadNeededCount = Math.Max(1, needed);
            BlocksPlayInput = ShouldHardBlockInput();
        }

        private void TryUnlockLoadWait()
        {
            if (!_session.IsHost || !_waitingForLoad || _loadUnlocked)
                return;

            _loadNeededCount = Math.Max(1, _session.CoopMemberCount);
            _loadReadyCount = _loadReadyPeers.Count;
            if (_loadReadyCount < _loadNeededCount)
                return;

            _log.Msg("[Bootstrap] All peers loaded (" + _loadReadyCount + "/" + _loadNeededCount + ")");
            _session.Broadcast(CoopMessageType.LoadAllReady);
            OnLoadAllReady();
        }

        private void OnLoadAllReady()
        {
            _waitingForLoad = false;
            _loadUnlocked = true;
            _hasPendingClientInGame = false;
            _loadWaitStartedAt = 0f;
            BlocksPlayInput = false;
            ReleaseLoadInputLayer();
            _status = "All players loaded";
            _log.Msg("[Bootstrap] Load gate unlocked");

            try { RestoreGameplayHudAfterJoin(); }
            catch { }

            if (ModMain.Instance != null)
            {
                if (ModMain.Instance.DialogueSync != null)
                    ModMain.Instance.DialogueSync.ClearWaitingGate("load-all-ready");
                if (ModMain.Instance.TurnSync != null)
                    ModMain.Instance.TurnSync.ClearWaitingGate("load-all-ready");
            }

            if (_session.IsHost && ModMain.Instance != null)
            {
                if (ModMain.Instance.MechanicsSync != null)
                    ModMain.Instance.MechanicsSync.BroadcastSnapshotImmediate();
                if (ModMain.Instance.ScalesSync != null)
                    ModMain.Instance.ScalesSync.BroadcastScalesSnapshotImmediate(forceDuringPassTurn: true);
            }

            if (ModMain.Instance != null && ModMain.Instance.PipAppearance != null)
                ModMain.Instance.PipAppearance.OnLoadGateUnlocked();

            if (ModMain.Instance != null && ModMain.Instance.MechanicsSync != null)
                ModMain.Instance.MechanicsSync.FlushDeferredUnlockUi();
        }

        private void UpdateLoadInputLayer()
        {
            bool hold = ShouldHardBlockInput();
            if (hold)
                HoldLoadInputLayer();
            else
                ReleaseLoadInputLayer();
        }

        private void HoldLoadInputLayer()
        {
            if (_loadInputLayerHeld)
                return;
            try
            {
                if (InputEnabler.Instance != null)
                {
                    InputEnabler.Instance.ForceOpenLayer(InputEnabler.InputLayer.LOADING);
                    _loadInputLayerHeld = true;
                }
            }
            catch (Exception ex)
            {
                _log.Warning("[Bootstrap] Hold LOADING layer: " + ex.Message);
            }
        }

        private void ReleaseLoadInputLayer()
        {
            if (!_loadInputLayerHeld)
                return;
            _loadInputLayerHeld = false;
            try
            {
                if (InputEnabler.Instance != null)
                    InputEnabler.Instance.CloseLayer(InputEnabler.InputLayer.LOADING);
            }
            catch (Exception ex)
            {
                _log.Warning("[Bootstrap] Release LOADING layer: " + ex.Message);
            }
        }

        public void DrawWaitingOverlay()
        {
            if (!ShowWaitingPrompt)
                return;

            float w = 520f;
            float h = 64f;

            float y = (_waitingForLoad && !_loadUnlocked) ? 28f : (Screen.height - h) * 0.5f;
            Rect r = new Rect((Screen.width - w) * 0.5f, y, w, h);

            Color prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.78f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = Color.white;

            string text;
            if (ModMain.Instance != null && ModMain.Instance.LoadSync != null &&
                ModMain.Instance.LoadSync.IsHostPreTransferActive)
            {
                text = ModMain.Instance.LoadSync.StageLabel;
            }
            else if (ModMain.Instance != null && ModMain.Instance.LoadSync != null &&
                     ModMain.Instance.LoadSync.IsClientStagedLoading)
            {
                text = ModMain.Instance.LoadSync.StageLabel;
            }
            else if (_waitingForLoad && !_loadUnlocked)
            {

                text = "Готовы: старт коопа " + _loadReadyCount + "/" + _loadNeededCount;
            }
            else
            {
                text = "Готовы: загрузка… (" + _loadReadyCount + "/" + _loadNeededCount + ")";
            }

            GUIStyle style = new GUIStyle(GUI.skin.label);
            style.alignment = TextAnchor.MiddleCenter;
            style.fontSize = 20;
            style.fontStyle = FontStyle.Bold;
            style.normal.textColor = new Color(1f, 0.92f, 0.75f, 1f);
            GUI.Label(r, text, style);
            GUI.color = prev;
        }

        private static bool GameReady()
        {
            return Game.Ready && Game.I != null;
        }
    }
}
