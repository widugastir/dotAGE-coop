using System;
using System.Collections.Generic;
using MelonLoader;
using Steamworks;
using DotAgeCoop.Net;
using UnityEngine;
using UnityEngine.UI;

namespace DotAgeCoop.Sync
{

    public sealed class CursorSyncService
    {
        private const float SendInterval = 0.05f;
        private const float HeartbeatInterval = 0.5f;
        private const float StaleSeconds = 2f;
        private const float MoveEpsilon = 0.04f;
        private const float RemoteAlpha = 0.5f;

        private readonly CoopSession _session;
        private readonly MelonLogger.Instance _log;
        private readonly Dictionary<ulong, RemoteCursor> _remotes = new Dictionary<ulong, RemoteCursor>();

        private float _lastSendTime;
        private float _lastSentX = float.NaN;
        private float _lastSentY = float.NaN;
        private Transform _visualRoot;
        private Sprite _cachedSprite;
        private Vector2 _cachedSize = new Vector2(32f, 32f);
        private Vector2 _cachedPivot = new Vector2(0f, 1f);

        private sealed class RemoteCursor
        {
            public float TargetX;
            public float TargetY;
            public float DisplayX;
            public float DisplayY;
            public float LastSeen;
            public GameObject Go;
            public Image Image;
            public RectTransform Rt;
        }

        public CursorSyncService(CoopSession session, MelonLogger.Instance log)
        {
            _session = session;
            _log = log;
            _session.MessageReceived += OnMessage;
        }

        public void Tick()
        {
            if (!_session.Active || _session.MemberCount < 2)
            {
                ClearAll();
                return;
            }

            try
            {
                MaybeSendLocalCursor();
                RefreshSpriteCache();
                UpdateRemoteVisuals();
                CullStale();
            }
            catch (Exception ex)
            {
                _log.Warning("[Cursor] Tick: " + ex.Message);
            }
        }

        public void OnReturnedToMain()
        {
            ClearAll();
        }

        private void OnMessage(CSteamID remote, CoopMessageType type, byte[] payload)
        {
            if (type != CoopMessageType.CursorUpdate)
                return;

            CursorUpdatePayload data;
            if (!CoopProtocol.TryReadCursorUpdate(payload, out data))
                return;

            ulong id = remote.m_SteamID;
            if (id == 0 || id == _session.SelfId)
                return;

            RemoteCursor cursor;
            if (!_remotes.TryGetValue(id, out cursor))
            {
                cursor = new RemoteCursor();
                cursor.DisplayX = data.X;
                cursor.DisplayY = data.Y;
                _remotes[id] = cursor;
            }

            cursor.TargetX = data.X;
            cursor.TargetY = data.Y;
            cursor.LastSeen = Time.unscaledTime;
        }

        private void MaybeSendLocalCursor()
        {
            if (!Game.Ready || Game.I == null || Game.I.cursorGui == null)
                return;

            float now = Time.unscaledTime;
            Vector3 village;
            try
            {
                village = UIUtils.CursorVillagePos();
            }
            catch
            {
                return;
            }

            float x = village.x;
            float y = village.y;

            float dt = now - _lastSendTime;
            float dx = x - _lastSentX;
            float dy = y - _lastSentY;
            bool first = float.IsNaN(_lastSentX);
            bool moved = first || (dx * dx + dy * dy) >= (MoveEpsilon * MoveEpsilon);
            bool heartbeat = dt >= HeartbeatInterval;
            if (dt < SendInterval && !heartbeat)
                return;
            if (!moved && !heartbeat)
                return;

            CursorUpdatePayload data = default(CursorUpdatePayload);
            data.X = x;
            data.Y = y;
            _session.Broadcast(CoopMessageType.CursorUpdate, CoopProtocol.PackCursorUpdate(data));
            _lastSendTime = now;
            _lastSentX = x;
            _lastSentY = y;
        }

        private void RefreshSpriteCache()
        {
            CursorGui gui = Game.I != null ? Game.I.cursorGui : null;
            if (gui == null)
                return;

            Sprite spr = gui.defaultCursorSprite;
            if (spr == null && gui.cursorImage != null)
                spr = gui.cursorImage.sprite;
            if (spr != null)
                _cachedSprite = spr;

            if (gui.cursorImage != null && gui.cursorImage.rectTransform != null)
            {
                Rect r = gui.cursorImage.rectTransform.rect;
                if (r.width > 1f && r.height > 1f)
                    _cachedSize = new Vector2(r.width, r.height);
                _cachedPivot = gui.cursorImage.rectTransform.pivot;
            }
        }

        private void UpdateRemoteVisuals()
        {
            if (!Game.Ready || Game.I == null || Game.I.cursorGui == null || _cachedSprite == null)
            {
                HideAllVisuals();
                return;
            }

            EnsureVisualRoot();
            if (_visualRoot == null)
                return;

            foreach (KeyValuePair<ulong, RemoteCursor> kv in _remotes)
            {
                RemoteCursor c = kv.Value;
                c.DisplayX = Mathf.Lerp(c.DisplayX, c.TargetX, 0.45f);
                c.DisplayY = Mathf.Lerp(c.DisplayY, c.TargetY, 0.45f);

                EnsureVisual(c, kv.Key);
                if (c.Go == null || c.Rt == null)
                    continue;

                if (c.Image != null)
                {
                    if (c.Image.sprite != _cachedSprite)
                        c.Image.sprite = _cachedSprite;
                    Color col = c.Image.color;
                    col.a = RemoteAlpha;
                    c.Image.color = col;
                    c.Rt.sizeDelta = _cachedSize;
                    c.Rt.pivot = _cachedPivot;
                }

                Vector3 village = new Vector3(c.DisplayX, c.DisplayY, 0f);
                Vector3 world = MovementBehaviour.FromVillagePos(village);
                UIUtils.FollowPosFromUI(c.Rt, world, keepInsideScreen: false, unaffectedByZoom: false);

                if (!c.Go.activeSelf)
                    c.Go.SetActive(true);
            }
        }

        private void EnsureVisualRoot()
        {
            CursorGui gui = Game.I != null ? Game.I.cursorGui : null;
            if (gui == null)
            {
                _visualRoot = null;
                return;
            }

            Transform parent = gui.transform.parent != null ? gui.transform.parent : gui.transform;
            if (_visualRoot != null && _visualRoot.parent == parent)
                return;

            ClearVisualObjects();
            GameObject root = new GameObject("DotAgeCoopRemoteCursors");
            root.hideFlags = HideFlags.HideAndDontSave;
            root.transform.SetParent(parent, false);
            root.transform.SetAsLastSibling();
            _visualRoot = root.transform;
        }

        private void EnsureVisual(RemoteCursor cursor, ulong peerId)
        {
            if (cursor.Go != null)
                return;
            if (_visualRoot == null || _cachedSprite == null)
                return;

            GameObject go = new GameObject("RemoteCursor_" + peerId);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.transform.SetParent(_visualRoot, false);

            Image img = go.AddComponent<Image>();
            img.sprite = _cachedSprite;
            img.raycastTarget = false;
            img.color = new Color(1f, 1f, 1f, RemoteAlpha);
            img.preserveAspect = true;

            RectTransform rt = img.rectTransform;
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = _cachedPivot;
            rt.sizeDelta = _cachedSize;
            rt.localScale = Vector3.one;
            rt.anchoredPosition = Vector2.zero;

            cursor.Go = go;
            cursor.Image = img;
            cursor.Rt = rt;
        }

        private void CullStale()
        {
            float now = Time.unscaledTime;
            List<ulong> dead = null;
            foreach (KeyValuePair<ulong, RemoteCursor> kv in _remotes)
            {
                if (now - kv.Value.LastSeen <= StaleSeconds)
                    continue;
                if (dead == null)
                    dead = new List<ulong>();
                dead.Add(kv.Key);
            }

            if (dead == null)
                return;

            for (int i = 0; i < dead.Count; i++)
            {
                RemoteCursor c;
                if (_remotes.TryGetValue(dead[i], out c))
                    DestroyVisual(c);
                _remotes.Remove(dead[i]);
            }
        }

        private void HideAllVisuals()
        {
            foreach (KeyValuePair<ulong, RemoteCursor> kv in _remotes)
            {
                if (kv.Value.Go != null && kv.Value.Go.activeSelf)
                    kv.Value.Go.SetActive(false);
            }
        }

        private void ClearAll()
        {
            ClearVisualObjects();
            _remotes.Clear();
            _visualRoot = null;
            _lastSentX = float.NaN;
            _lastSentY = float.NaN;
        }

        private void ClearVisualObjects()
        {
            foreach (KeyValuePair<ulong, RemoteCursor> kv in _remotes)
                DestroyVisual(kv.Value);

            if (_visualRoot != null)
            {
                UnityEngine.Object.Destroy(_visualRoot.gameObject);
                _visualRoot = null;
            }
        }

        private static void DestroyVisual(RemoteCursor cursor)
        {
            if (cursor == null || cursor.Go == null)
                return;
            UnityEngine.Object.Destroy(cursor.Go);
            cursor.Go = null;
            cursor.Image = null;
            cursor.Rt = null;
        }
    }
}
