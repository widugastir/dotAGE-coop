using System;
using System.IO;
using MelonLoader;
using MelonLoader.Utils;

namespace DotAgeCoop
{

    public static class SaveInstanceConfig
    {
        public const string FileName = "instance.cfg";

        private static bool _loaded;
        private static bool _enabled;
        private static string _profile = "A";
        private static int _slot;

        public static bool Enabled
        {
            get
            {
                EnsureLoaded();
                return _enabled;
            }
        }

        public static string Profile
        {
            get
            {
                EnsureLoaded();
                return _profile;
            }
        }

        public static int Slot
        {
            get
            {
                EnsureLoaded();
                return _slot;
            }
        }

        public static string ConfigPath
        {
            get
            {
                return Path.Combine(
                    MelonEnvironment.UserDataDirectory,
                    "DotAgeCoop",
                    FileName);
            }
        }

        public static void EnsureLoaded()
        {
            if (_loaded)
                return;
            _loaded = true;
            LoadOrCreate();
        }

        public static bool TryRewriteSlot(ref int slot)
        {
            EnsureLoaded();
            if (!_enabled)
                return false;
            if (slot == _slot)
                return false;
            slot = _slot;
            return true;
        }

        public static void LogStatus(MelonLogger.Instance log)
        {
            EnsureLoaded();
            if (log == null)
                return;
            if (!_enabled)
            {
                log.Msg("[SaveInstance] Vanilla profiles (profile=NONE / disabled) ← " + ConfigPath);
                return;
            }

            log.Msg("[SaveInstance] Forced profile=" + _profile + " (slot " + _slot + ") ← " + ConfigPath);
        }

        private static void LoadOrCreate()
        {
            _enabled = false;
            _profile = "A";
            _slot = 0;

            string path = ConfigPath;
            string dir = Path.GetDirectoryName(path);
            try
            {
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            }
            catch
            {
            }

            if (!File.Exists(path))
            {
                string suggested = SuggestDefaultProfile();
                try
                {
                    File.WriteAllText(path,
                        "# DotAgeCoop — per-install save profile (A, B, C, or NONE)." + Environment.NewLine +
                        "# NONE = vanilla profile switching (shared LocalLow selection)." + Environment.NewLine +
                        "# Use A vs B on each game copy when testing coop on one PC." + Environment.NewLine +
                        "profile=" + suggested + Environment.NewLine);
                }
                catch
                {
                }

                ApplyProfile(suggested);
                return;
            }

            try
            {
                string[] lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i] != null ? lines[i].Trim() : string.Empty;
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                        continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                        continue;

                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    if (string.Equals(key, "profile", StringComparison.OrdinalIgnoreCase))
                        ApplyProfile(val);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[DotAgeCoop] SaveInstanceConfig read failed: " + ex.Message);
            }
        }

        private static void ApplyProfile(string raw)
        {
            string p = (raw ?? string.Empty).Trim().ToUpperInvariant();
            if (p.Length == 0 ||
                p == "NONE" || p == "OFF" || p == "DISABLED" || p == "FALSE" || p == "-")
            {
                _enabled = false;
                _profile = "NONE";
                _slot = 0;
                return;
            }

            if (p == "0" || p == "1" || p == "2")
            {
                _slot = p[0] - '0';
                _profile = _slot == 1 ? "B" : (_slot == 2 ? "C" : "A");
                _enabled = true;
                return;
            }

            if (p == "A" || p == "B" || p == "C")
            {
                _profile = p;
                _slot = p == "B" ? 1 : (p == "C" ? 2 : 0);
                _enabled = true;
                return;
            }

            MelonLogger.Warning("[DotAgeCoop] Invalid profile='" + raw + "' in instance.cfg (use A, B, C, or NONE)");
            _enabled = false;
            _profile = "NONE";
        }

        private static string SuggestDefaultProfile()
        {
            try
            {
                string root = MelonEnvironment.GameRootDirectory ?? string.Empty;
                string name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!string.IsNullOrEmpty(name))
                {
                    string n = name.ToLowerInvariant();
                    if (n.Contains("_2") || n.EndsWith("2") || n.Contains("client"))
                        return "B";
                }
            }
            catch
            {
            }

            return "A";
        }
    }
}
