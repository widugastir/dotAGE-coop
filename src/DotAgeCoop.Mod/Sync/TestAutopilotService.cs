using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using MelonLoader;
using MelonLoader.Utils;
using DotAgeCoop.Lobby;
using UnityEngine;

namespace DotAgeCoop.Sync
{

    public sealed class TestAutopilotService
    {
        public const string FileName = "autopilot.txt";

        private readonly MelonLogger.Instance _log;
        private readonly LocalLobbyService _local;
        private bool _started;
        private string _overlay = string.Empty;

        public TestAutopilotService(LocalLobbyService local, MelonLogger.Instance log)
        {
            _local = local;
            _log = log;
        }

        public static string AutopilotPath
        {
            get
            {
                return Path.Combine(
                    MelonEnvironment.UserDataDirectory,
                    "DotAgeCoop",
                    FileName);
            }
        }

        public void Tick()
        {
            if (_started)
                return;

            string path = AutopilotPath;
            if (!File.Exists(path))
                return;

            string role;
            string action;
            float settleSeconds;
            float joinTimeout;
            if (!TryReadCommand(path, out role, out action, out settleSeconds, out joinTimeout))
            {
                TryDelete(path);
                return;
            }

            TryDelete(path);
            _started = true;
            _log.Msg("[Autopilot] Start role=" + role + " action=" + action +
                     " settle=" + settleSeconds.ToString("0.#", CultureInfo.InvariantCulture) + "s");
            MelonCoroutines.Start(RunCO(role, action, settleSeconds, joinTimeout));
        }

        public void DrawOverlay()
        {
            if (string.IsNullOrEmpty(_overlay))
                return;

            float w = 420f;
            float h = 48f;
            Rect r = new Rect((Screen.width - w) * 0.5f, 24f, w, h);
            Color prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.72f);
            GUI.Box(r, GUIContent.none);
            GUI.color = Color.white;
            GUI.Label(new Rect(r.x + 12f, r.y + 12f, w - 24f, 24f), _overlay);
            GUI.color = prev;
        }

        private IEnumerator RunCO(string role, string action, float settleSeconds, float joinTimeout)
        {
            _overlay = "Autopilot: wait title…";
            float bootDeadline = Time.unscaledTime + Mathf.Max(5f, settleSeconds + 45f);
            while (Time.unscaledTime < bootDeadline)
            {
                if (GameReady())
                    break;
                yield return null;
            }

            if (!GameReady())
            {
                Fail("Game.I / StartGameController not ready");
                yield break;
            }

            _overlay = "Autopilot: settle " + settleSeconds.ToString("0.#", CultureInfo.InvariantCulture) + "s…";
            float settleUntil = Time.unscaledTime + Mathf.Max(1f, settleSeconds);
            while (Time.unscaledTime < settleUntil)
                yield return null;

            if (string.Equals(role, "host", StringComparison.OrdinalIgnoreCase))
                yield return HostCO(action, joinTimeout);
            else if (string.Equals(role, "client", StringComparison.OrdinalIgnoreCase))
                yield return ClientCO(joinTimeout);
            else
                Fail("Unknown role '" + role + "'");

            _overlay = string.Empty;
        }

        private IEnumerator HostCO(string action, float joinTimeout)
        {
            _overlay = "Autopilot HOST: local lobby…";
            try
            {
                if (_local.IsActive)
                    _local.Leave();
                _local.StartHost();
            }
            catch (Exception ex)
            {
                Fail("Host Local failed: " + ex.Message);
                yield break;
            }

            _overlay = "Autopilot HOST: wait client…";
            float deadline = Time.unscaledTime + Mathf.Max(10f, joinTimeout);
            while (Time.unscaledTime < deadline)
            {
                if (_local.IsActive && _local.IsHost && _local.MemberCount >= 2)
                    break;
                yield return null;
            }

            if (!(_local.IsActive && _local.IsHost && _local.MemberCount >= 2))
            {
                Fail("Client did not join local lobby (members=" + _local.MemberCount + ")");
                yield break;
            }

            _log.Msg("[Autopilot] Client joined — members=" + _local.MemberCount);

            yield return null;
            yield return null;
            float pauseUntil = Time.unscaledTime + 1.5f;
            while (Time.unscaledTime < pauseUntil)
                yield return null;

            if (string.Equals(action, "newgame", StringComparison.OrdinalIgnoreCase))
                yield return HostNewGameCO();
            else if (string.Equals(action, "loadgame", StringComparison.OrdinalIgnoreCase))
                yield return HostLoadGameCO();
            else
                Fail("Unknown action '" + action + "'");
        }

        private IEnumerator ClientCO(float joinTimeout)
        {
            _overlay = "Autopilot CLIENT: join local…";
            float deadline = Time.unscaledTime + Mathf.Max(10f, joinTimeout);
            Exception last = null;
            while (Time.unscaledTime < deadline)
            {
                if (_local.IsActive && !_local.IsHost)
                {
                    _log.Msg("[Autopilot] Joined local lobby");
                    _overlay = "Autopilot CLIENT: waiting host start…";
                    yield break;
                }

                try
                {
                    _local.JoinLocalhost();
                }
                catch (Exception ex)
                {
                    last = ex;
                }

                float retryAt = Time.unscaledTime + 1f;
                while (Time.unscaledTime < retryAt)
                    yield return null;
            }

            Fail("Join Local timed out" + (last != null ? ": " + last.Message : ""));
        }

        private IEnumerator HostNewGameCO()
        {
            _overlay = "Autopilot HOST: delete save + New Game…";
            if (!GameReady())
            {
                Fail("Game not ready for New Game");
                yield break;
            }

            if (Game.I.GameIsStarted())
            {
                Fail("Already in a run — return to title first");
                yield break;
            }

            bool hasSave = false;
            try
            {
                if (Game.I.SaveGameData != null)
                    hasSave = Game.I.SaveGameData.HasGameToLoad();
            }
            catch (Exception ex)
            {
                _log.Warning("[Autopilot] HasGameToLoad: " + ex.Message);
            }

            if (hasSave)
            {
                _log.Msg("[Autopilot] Deleting current save…");
                try
                {
                    Game.I.SaveGameData.Delete();
                }
                catch (Exception ex)
                {
                    Fail("Delete save failed: " + ex.Message);
                    yield break;
                }
                yield return null;
                yield return null;
            }
            else
            {
                _log.Msg("[Autopilot] No save to delete — starting New Game");
            }

            try
            {
                _log.Msg("[Autopilot] NewGame_Immediate (defaults)");
                Game.I.startGameController.NewGame_Immediate();
                _overlay = "Autopilot HOST: New Game started";
            }
            catch (Exception ex)
            {
                Fail("New Game failed: " + ex.Message);
            }
        }

        private IEnumerator HostLoadGameCO()
        {
            _overlay = "Autopilot HOST: Load Game…";
            if (!GameReady())
            {
                Fail("Game not ready for Load Game");
                yield break;
            }

            if (Game.I.GameIsStarted())
            {
                Fail("Already in a run — return to title first");
                yield break;
            }

            bool hasSave = false;
            try
            {
                if (Game.I.SaveGameData != null)
                    hasSave = Game.I.SaveGameData.HasGameToLoad();
            }
            catch (Exception ex)
            {
                _log.Warning("[Autopilot] HasGameToLoad: " + ex.Message);
            }

            if (!hasSave)
            {
                Fail("No save to load (HasGameToLoad=false)");
                yield break;
            }

            try
            {
                try
                {
                    if (Game.I.PlayerProfileData != null)
                        Game.I.PlayerProfileData.ResetCache();
                }
                catch
                {
                }

                _log.Msg("[Autopilot] LoadGame_FromIntro (same path as manual Load)");
                Game.I.startGameController.LoadGame_FromIntro();
                _overlay = "Autopilot HOST: Load Game started";
            }
            catch (Exception ex)
            {
                Fail("Load Game failed: " + ex.Message);
            }
        }

        private void Fail(string message)
        {
            _log.Error("[Autopilot] " + message);
            _overlay = "Autopilot FAIL: " + message;
        }

        private static bool GameReady()
        {
            try
            {
                return Game.I != null && Game.I.startGameController != null;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadCommand(
            string path,
            out string role,
            out string action,
            out float settleSeconds,
            out float joinTimeout)
        {
            role = string.Empty;
            action = string.Empty;
            settleSeconds = 12f;
            joinTimeout = 90f;

            try
            {
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i] != null ? lines[i].Trim() : string.Empty;
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                        continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                        continue;

                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim();
                    if (key == "role")
                        role = val;
                    else if (key == "action")
                        action = val;
                    else if (key == "settle")
                    {
                        float parsed;
                        if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                            settleSeconds = parsed;
                    }
                    else if (key == "jointimeout")
                    {
                        float parsed;
                        if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                            joinTimeout = parsed;
                    }
                }
            }
            catch (Exception)
            {
                return false;
            }

            return !string.IsNullOrEmpty(role) && !string.IsNullOrEmpty(action);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
