using System;
using MelonLoader;
using DotAgeCoop.Net;
using UnityEngine;

namespace DotAgeCoop.Sync
{

    public sealed class FirstPlacementSyncService
    {
        private readonly CoopSession _session;
        private readonly MelonLogger.Instance _log;
        private bool _hostBroadcastNoted;

        public FirstPlacementSyncService(CoopSession session, MelonLogger.Instance log)
        {
            _session = session;
            _log = log;
        }

        public bool ShowWaitingPrompt
        {
            get
            {
                if (!_session.Active || _session.IsHost)
                    return false;
                try
                {
                    return Game.Ready && Game.I != null && Game.I.isPerformingFirstPlacement && !Game.I.hasPlacedFirst;
                }
                catch
                {
                    return false;
                }
            }
        }

        public void Tick()
        {
            if (!ShowWaitingPrompt)
                return;

            try
            {
                BuildingPlacementHandler handler = Game.I != null ? Game.I.BuildingPlacementHandler : null;
                if (handler != null && handler.IsCurrentlyBuilding())
                    handler.HideAndDeselect(ignoreTutorial: true);
            }
            catch
            {
            }
        }

        public void OnReturnedToMain()
        {
            _hostBroadcastNoted = false;
        }

        public void OnHostConfirmedPlacement(Building building)
        {
            if (!_session.Active || !_session.IsHost || _hostBroadcastNoted)
                return;

            try
            {
                if (Game.I == null || !Game.I.isPerformingFirstPlacement)
                    return;
                if (building == null)
                    return;

                _hostBroadcastNoted = true;
                _log.Msg("[FirstPlace] Host starter confirmed (synced via GameSync)");
            }
            catch (Exception ex)
            {
                _log.Warning("[FirstPlace] " + ex.Message);
            }
        }

        public void DrawWaitingOverlay()
        {
            if (!ShowWaitingPrompt)
                return;

            float w = 460f;
            float h = 48f;
            Rect r = new Rect((Screen.width - w) * 0.5f, Screen.height * 0.18f, w, h);

            Color prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.72f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = Color.white;

            GUIStyle style = new GUIStyle(GUI.skin.label);
            style.alignment = TextAnchor.MiddleCenter;
            style.fontSize = 18;
            style.fontStyle = FontStyle.Bold;
            style.normal.textColor = new Color(1f, 0.92f, 0.75f, 1f);
            GUI.Label(r, "Ready: host is choosing the starting position…", style);
            GUI.color = prev;
        }
    }
}
