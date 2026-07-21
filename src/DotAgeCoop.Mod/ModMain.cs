using System;
using MelonLoader;
using DotAgeCoop.Lobby;
using DotAgeCoop.Net;
using DotAgeCoop.Sync;
using DotAgeCoop.UI;
using UnityEngine;

[assembly: MelonInfo(typeof(DotAgeCoop.ModMain), "DotAgeCoop", "0.1.179", "DotAgeCoop")]
[assembly: MelonGame("CKCGames", "dotAGE")]
[assembly: MelonColor(255, 180, 120, 40)]

namespace DotAgeCoop
{
    public sealed class ModMain : MelonMod
    {
        public static ModMain Instance { get; private set; }

        public SteamLobbyService Lobby { get; private set; }
        public LocalLobbyService LocalLobby { get; private set; }
        public CoopSession Session { get; private set; }
        public TurnSyncService TurnSync { get; private set; }
        public DialogueSyncService DialogueSync { get; private set; }
        public FirstPlacementSyncService FirstPlacement { get; private set; }
        public GameBootstrapService Bootstrap { get; private set; }
        public GameSyncService GameSync { get; private set; }
        public CursorSyncService CursorSync { get; private set; }
        public PipOrderSyncService PipOrders { get; private set; }
        public PipAppearanceSyncService PipAppearance { get; private set; }
        public ResearchSyncService ResearchSync { get; private set; }
        public MechanicsSyncService MechanicsSync { get; private set; }
        public ScalesSyncService ScalesSync { get; private set; }
        public EventSyncService EventSync { get; private set; }
        public LoadSyncService LoadSync { get; private set; }
        public HardSyncService HardSync { get; private set; }
        public TestAutopilotService Autopilot { get; private set; }
        public LobbyOverlay Overlay { get; private set; }

        private bool _ready;
        private bool _quitting;

        public override void OnInitializeMelon()
        {
            Instance = this;
            try
            {
                LoggerInstance.Msg("DotAgeCoop 0.1.179 - force NewCreature defs + RandomTerrain cells");
                SaveInstanceConfig.EnsureLoaded();
                SaveInstanceConfig.LogStatus(LoggerInstance);

                Lobby = new SteamLobbyService(LoggerInstance);
                LocalLobby = new LocalLobbyService(LoggerInstance);
                Session = new CoopSession(Lobby, LocalLobby, LoggerInstance);
                TurnSync = new TurnSyncService(Session, LoggerInstance);
                DialogueSync = new DialogueSyncService(Session, LoggerInstance);
                FirstPlacement = new FirstPlacementSyncService(Session, LoggerInstance);
                Bootstrap = new GameBootstrapService(Session, LoggerInstance);
                GameSync = new GameSyncService(Session, LoggerInstance);
                CursorSync = new CursorSyncService(Session, LoggerInstance);
                PipOrders = new PipOrderSyncService(Session, LoggerInstance);
                PipAppearance = new PipAppearanceSyncService(Session, LoggerInstance);
                ResearchSync = new ResearchSyncService(Session, LoggerInstance);
                MechanicsSync = new MechanicsSyncService(Session, LoggerInstance);
                ScalesSync = new ScalesSyncService(Session, LoggerInstance);
                EventSync = new EventSyncService(Session, LoggerInstance);
                LoadSync = new LoadSyncService(Session, LoggerInstance);
                HardSync = new HardSyncService(Session, LoggerInstance);
                Autopilot = new TestAutopilotService(LocalLobby, LoggerInstance);
                Overlay = new LobbyOverlay(Lobby, LocalLobby, Session);
                _ready = true;

                if (!Lobby.TryInitialize())
                {
                    LoggerInstance.Warning(
                        "Steam failed to init (optional for Local mode). " +
                        "Solo: Host Local / Join Local, then host starts a New Game.");
                }
            }
            catch (Exception ex)
            {
                _ready = false;
                LoggerInstance.Error("DotAgeCoop init failed: " + ex);
            }
        }

        public override void OnUpdate()
        {
            if (!_ready || _quitting)
                return;

            try
            {
                Lobby.Tick();
                Session.Tick();
                TurnSync.Tick();
                if (DialogueSync != null)
                    DialogueSync.Tick();
                if (FirstPlacement != null)
                    FirstPlacement.Tick();
                Bootstrap.Tick();
                GameSync.Tick();
                if (CursorSync != null)
                    CursorSync.Tick();
                if (PipOrders != null)
                    PipOrders.Tick();
                if (PipAppearance != null)
                    PipAppearance.Tick();
                if (ResearchSync != null)
                    ResearchSync.Tick();
                if (MechanicsSync != null)
                    MechanicsSync.Tick();
                if (ScalesSync != null)
                    ScalesSync.Tick();
                if (EventSync != null)
                    EventSync.Tick();
                if (HardSync != null)
                    HardSync.Tick();
                if (Autopilot != null)
                    Autopilot.Tick();
                if (Input.GetKeyDown(KeyCode.F8))
                {
                    Overlay.Visible = !Overlay.Visible;
                    if (!Overlay.Visible)
                        Overlay.ForceRestoreCursor();
                }

                Overlay.UpdateInputBlock();
            }
            catch (Exception ex)
            {
                LoggerInstance.Error("OnUpdate: " + ex);
            }
        }

        public override void OnGUI()
        {
            if (!_ready || _quitting || Overlay == null)
                return;

            try
            {
                if (DialogueSync != null)
                    DialogueSync.DrawWaitingOverlay();
                if (EventSync != null)
                    EventSync.DrawWaitingOverlay();
                if (TurnSync != null)
                    TurnSync.DrawWaitingOverlay();
                if (FirstPlacement != null)
                    FirstPlacement.DrawWaitingOverlay();
                if (Bootstrap != null)
                    Bootstrap.DrawWaitingOverlay();
                if (HardSync != null)
                    HardSync.DrawOverlay();
                if (Autopilot != null)
                    Autopilot.DrawOverlay();
                DotAgeCoop.Hooks.MemoryCheat.DrawHint();
                DotAgeCoop.Hooks.ResearchCheat.DrawCheatButton();
                Overlay.Draw();
            }
            catch (Exception ex)
            {
                LoggerInstance.Error("OnGUI: " + ex);
            }
        }

        public override void OnApplicationQuit()
        {
            if (!_ready || _quitting)
                return;

            _quitting = true;
            if (Overlay != null)
            {
                Overlay.Visible = false;
                Overlay.ForceRestoreCursor();
            }

            try
            {
                Session.Shutdown();
                Lobby.Shutdown();
            }
            catch
            {
            }
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            LoggerInstance.Msg("Scene loaded: " + sceneName + " (" + buildIndex + ")");
        }
    }
}
