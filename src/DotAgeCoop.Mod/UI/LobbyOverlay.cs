using DotAgeCoop.Lobby;
using DotAgeCoop.Net;
using UnityEngine;
using UnityEngine.EventSystems;

namespace DotAgeCoop.UI
{
    public sealed class LobbyOverlay
    {
        private readonly SteamLobbyService _steam;
        private readonly LocalLobbyService _local;
        private readonly CoopSession _session;
        private string _joinCode = string.Empty;
        private string _chatDraft = string.Empty;
        private Vector2 _chatScroll;
        private Rect _window = new Rect(40, 40, 460, 640);

        private GUIStyle _windowStyle;
        private GUIStyle _labelStyle;
        private Texture2D _windowBg;
        private Texture2D _cursorTex;
        private bool _cursorWasVisible = true;
        private bool _hidingGameCursor;

        private const int LobbyWindowId = 0xDA6E;

        public static bool BlocksGameInput { get; private set; }

        public bool Visible { get; set; }

        public LobbyOverlay(SteamLobbyService steam, LocalLobbyService local, CoopSession session)
        {
            _steam = steam;
            _local = local;
            _session = session;
            Visible = false;
        }

        public void UpdateInputBlock()
        {
            if (!Visible)
            {
                SetBlocking(false);
                return;
            }

            Vector2 screenMouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            SetBlocking(_window.Contains(screenMouse));
        }

        public void Draw()
        {
            if (!Visible)
            {
                RestoreGameCursor();
                SetBlocking(false);
                return;
            }

            EnsureStyles();

            Vector2 screenMouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            bool overLobby = _window.Contains(screenMouse);
            SetBlocking(overLobby);

            if (overLobby)
                HideGameCursor();
            else
                RestoreGameCursor();

            int prevDepth = GUI.depth;
            GUI.depth = -1000;

            Color prevBg = GUI.backgroundColor;
            Color prevContent = GUI.contentColor;
            GUI.backgroundColor = Color.white;
            GUI.contentColor = Color.white;

            _window = GUI.ModalWindow(LobbyWindowId, _window, DrawWindow, "DotAgeCoop Lobby (F8 to hide)", _windowStyle);
            GUI.BringWindowToFront(LobbyWindowId);

            GUI.backgroundColor = prevBg;
            GUI.contentColor = prevContent;
            GUI.depth = prevDepth;
        }

        private static void SetBlocking(bool block)
        {
            BlocksGameInput = block;
            if (!block)
                EnsureEventSystemEnabled();
        }

        private static void EnsureEventSystemEnabled()
        {
            try
            {
                EventSystem es = EventSystem.current;
                if (es != null && !es.enabled)
                    es.enabled = true;
            }
            catch
            {
            }
        }

        private void DrawWindow(int id)
        {
            string status = _local.IsActive ? _local.Status : _steam.Status;
            GUILayout.Label("Status: " + status, _labelStyle);
            GUILayout.Label(
                "Mode: " + _session.ModeLabel +
                "   Role: " + (_session.Active ? (_session.IsHost ? "HOST" : "CLIENT") : "-") +
                "   Members: " + _session.MemberCount, _labelStyle);
            GUILayout.Label("Steam: " + (_steam.IsReady ? "OK" : "NO") + "   AppID: " + _steam.AppId, _labelStyle);
            if (ModMain.Instance != null && ModMain.Instance.Bootstrap != null)
                GUILayout.Label("Run: " + ModMain.Instance.Bootstrap.Status, _labelStyle);

            GUILayout.Space(8);
            GUILayout.Label("--- Solo test (same PC, two game copies) ---", _labelStyle);
            bool inSteam = _steam.IsInLobby;
            bool inLocal = _local.IsActive;
            bool inAnyLobby = inSteam || inLocal;

            GUILayout.BeginHorizontal();
            GUI.enabled = !inAnyLobby;
            if (GUILayout.Button("Host Local", GUILayout.Height(28)))
            {
                if (_steam.IsInLobby)
                    _steam.LeaveLobby();
                _local.StartHost();
            }
            if (GUILayout.Button("Join Local", GUILayout.Height(28)))
            {
                if (_steam.IsInLobby)
                    _steam.LeaveLobby();
                _local.JoinLocalhost();
            }
            GUI.enabled = inLocal;
            if (GUILayout.Button("Leave Local", GUILayout.Height(28)))
                _local.Leave();
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Label("Host on one copy, Join Local on the other. Port " + LocalLobbyService.DefaultPort, _labelStyle);

            GUILayout.Space(8);
            GUILayout.Label("--- Steam (needs TWO Steam accounts) ---", _labelStyle);
            GUILayout.BeginHorizontal();
            GUI.enabled = !inAnyLobby && _steam.IsReady;
            if (GUILayout.Button("Create Lobby", GUILayout.Height(28)))
            {
                if (_local.IsActive)
                    _local.Leave();
                _steam.CreateLobby();
            }
            GUI.enabled = inSteam;
            if (GUILayout.Button("Leave", GUILayout.Height(28)))
                _steam.LeaveLobby();
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            if (inSteam)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Lobby code:", _labelStyle, GUILayout.Width(90));
                GUILayout.TextField(_steam.LobbyCode ?? string.Empty, GUILayout.Height(24));
                if (GUILayout.Button("Copy", GUILayout.Width(60), GUILayout.Height(24)))
                    _steam.CopyLobbyCodeToClipboard();
                GUILayout.EndHorizontal();
            }

            GUILayout.BeginHorizontal();
            GUI.enabled = !inAnyLobby;
            _joinCode = GUILayout.TextField(_joinCode, GUILayout.Height(24));
            if (GUILayout.Button("Join", GUILayout.Width(70), GUILayout.Height(24)))
            {
                if (_local.IsActive)
                    _local.Leave();
                _steam.JoinLobby(_joinCode);
            }
            if (GUILayout.Button("Paste", GUILayout.Width(60), GUILayout.Height(24)))
            {
                string clip = GUIUtility.systemCopyBuffer;
                _joinCode = clip != null ? clip.Trim() : string.Empty;
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            GUILayout.Label("Chat", _labelStyle);
            _chatScroll = GUILayout.BeginScrollView(_chatScroll, GUILayout.Height(80));
            for (int i = 0; i < _session.ChatLog.Count; i++)
                GUILayout.Label(_session.ChatLog[i], _labelStyle);
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            GUI.enabled = _session.Active;
            _chatDraft = GUILayout.TextField(_chatDraft, GUILayout.Height(22));
            if (GUILayout.Button("Send", GUILayout.Width(60), GUILayout.Height(22)))
            {
                if (!string.IsNullOrEmpty(_chatDraft) && _chatDraft.Trim().Length > 0)
                {
                    _session.SendChat(_chatDraft);
                    _chatDraft = string.Empty;
                }
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            GUILayout.Label("--- Hard Sync ---", _labelStyle);
            bool canHardSync = _session.Active && _session.IsHost &&
                               ModMain.Instance != null && ModMain.Instance.HardSync != null &&
                               !ModMain.Instance.HardSync.IsActive;
            try
            {
                if (canHardSync && (Game.I == null || !Game.I.GameIsStarted()))
                    canHardSync = false;
            }
            catch
            {
                canHardSync = false;
            }

            GUI.enabled = canHardSync;
            if (GUILayout.Button("Hard Sync", GUILayout.Height(30)))
            {
                if (ModMain.Instance != null && ModMain.Instance.HardSync != null)
                    ModMain.Instance.HardSync.RequestHostHardSync();
            }
            GUI.enabled = true;
            GUILayout.Label(
                "Host only: freeze everyone and push full world (buildings, resources, domains, events, research, pips/orders).",
                _labelStyle);

            GUILayout.Space(6);
            GUIStyle tipStyle = new GUIStyle(_labelStyle);
            tipStyle.wordWrap = true;
            tipStyle.normal.textColor = new Color(1f, 0.85f, 0.45f, 1f);
            GUILayout.Label(
                "Flow: both Join Local -> host starts New Game -> clients auto-join same seed. Clicks over this window are blocked from the game.",
                tipStyle);

            GUI.DragWindow(new Rect(0, 0, 10000, 24));

            if (_cursorTex != null)
            {
                Vector2 localMouse = Event.current.mousePosition;
                Color prev = GUI.color;
                GUI.color = Color.white;
                GUI.DrawTexture(new Rect(localMouse.x, localMouse.y, 22f, 22f), _cursorTex);
                GUI.color = prev;
            }
        }

        private void HideGameCursor()
        {
            if (_hidingGameCursor)
                return;

            _cursorWasVisible = Cursor.visible;
            Cursor.visible = false;
            _hidingGameCursor = true;
        }

        public void ForceRestoreCursor()
        {
            RestoreGameCursor();
            SetBlocking(false);
            EnsureEventSystemEnabled();
        }

        private void RestoreGameCursor()
        {
            if (!_hidingGameCursor)
                return;

            Cursor.visible = _cursorWasVisible;
            _hidingGameCursor = false;
        }

        private void EnsureStyles()
        {
            if (_windowStyle != null && _windowBg != null && _cursorTex != null)
                return;

            _windowBg = MakeSolid(8, 8, new Color(0.08f, 0.09f, 0.11f, 0.98f));
            _cursorTex = MakeCursorTexture();

            _windowStyle = new GUIStyle(GUI.skin.window);
            ApplyWindowBackground(_windowStyle, _windowBg);
            ApplyWindowTitleColor(_windowStyle, Color.white);
            _windowStyle.padding = new RectOffset(12, 12, 28, 12);
            _windowStyle.border = new RectOffset(8, 8, 8, 8);
            _windowStyle.alignment = TextAnchor.UpperCenter;
            _windowStyle.fontStyle = FontStyle.Bold;

            _labelStyle = new GUIStyle(GUI.skin.label);
            _labelStyle.normal.textColor = Color.white;
            _labelStyle.hover.textColor = Color.white;
            _labelStyle.active.textColor = Color.white;
            _labelStyle.focused.textColor = Color.white;
            _labelStyle.wordWrap = true;
        }

        private static void ApplyWindowBackground(GUIStyle style, Texture2D bg)
        {
            style.normal.background = bg;
            style.onNormal.background = bg;
            style.hover.background = bg;
            style.onHover.background = bg;
            style.active.background = bg;
            style.onActive.background = bg;
            style.focused.background = bg;
            style.onFocused.background = bg;
        }

        private static void ApplyWindowTitleColor(GUIStyle style, Color color)
        {
            style.normal.textColor = color;
            style.onNormal.textColor = color;
            style.hover.textColor = color;
            style.onHover.textColor = color;
            style.active.textColor = color;
            style.onActive.textColor = color;
            style.focused.textColor = color;
            style.onFocused.textColor = color;
        }

        private static Texture2D MakeSolid(int width, int height, Color color)
        {
            Color[] pixels = new Color[width * height];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = color;

            Texture2D tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.SetPixels(pixels);
            tex.Apply();
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Point;
            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }

        private static Texture2D MakeCursorTexture()
        {
            const int s = 22;
            Texture2D tex = new Texture2D(s, s, TextureFormat.RGBA32, false);
            Color clear = new Color(0, 0, 0, 0);
            Color fill = new Color(1f, 1f, 1f, 1f);
            Color outline = new Color(0f, 0f, 0f, 1f);

            for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
                tex.SetPixel(x, y, clear);

            for (int y = 0; y < 18; y++)
            {
                int maxX = y < 12 ? y : (18 - y);
                if (maxX < 1) maxX = 1;
                for (int x = 0; x <= maxX; x++)
                {
                    bool edge = x == 0 || x == maxX || y == 0;
                    tex.SetPixel(x, s - 1 - y, edge ? outline : fill);
                }
            }

            for (int y = 12; y < 20; y++)
            {
                tex.SetPixel(1, s - 1 - y, outline);
                tex.SetPixel(2, s - 1 - y, fill);
                tex.SetPixel(3, s - 1 - y, outline);
            }

            tex.Apply();
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.hideFlags = HideFlags.HideAndDontSave;
            return tex;
        }
    }
}
