using System;
using MelonLoader;

namespace DotAgeCoop.Sync
{

    public static class EconDebugLog
    {
        public static void ScaleDelta(string source, ScaleDefinition def, int delta, FlowType flow, int nowActual)
        {
            if (def == null || delta == 0)
                return;
            MelonLogger.Msg("[Econ] " + RoleTag() + " " + source + " scale " + ScaleName(def) +
                            " " + Signed(delta) + " (" + flow + ") now=" + nowActual);
        }

        public static void ScaleSnap(string scaleName, int oldActual, int newActual,
            int flow, int snow, int temp)
        {
            if (oldActual == newActual)
                return;
            MelonLogger.Msg("[Econ] " + RoleTag() + " snap scale " + scaleName +
                            " " + oldActual + "->" + newActual +
                            " (flow=" + flow + " snow=" + snow + " temp=" + temp + ")");
        }

        public static void ResourceDelta(string source, ResourceType type, int delta, int nowStock)
        {
            if (type == null || delta == 0)
                return;
            MelonLogger.Msg("[Econ] " + RoleTag() + " " + source + " res " + ResourceName(type) +
                            " " + Signed(delta) + " now=" + nowStock);
        }

        public static void ResourceSnap(string resName, int oldStock, int newStock)
        {
            if (oldStock == newStock)
                return;
            MelonLogger.Msg("[Econ] " + RoleTag() + " snap res " + resName +
                            " " + oldStock + "->" + newStock);
        }

        private static string RoleTag()
        {
            try
            {
                ModMain mod = ModMain.Instance;
                if (mod == null || mod.Session == null || !mod.Session.Active)
                    return "SOLO";
                return mod.Session.IsHost ? "HOST" : "CLIENT";
            }
            catch
            {
                return "?";
            }
        }

        private static string ScaleName(ScaleDefinition def)
        {
            try
            {
                if (!string.IsNullOrEmpty(def.name))
                    return def.name;
            }
            catch
            {
            }
            try
            {
                return "id=" + def.SortOrderOrID;
            }
            catch
            {
                return "?";
            }
        }

        private static string ResourceName(ResourceType type)
        {
            try
            {
                string pretty = type.GetPrettyName();
                if (!string.IsNullOrEmpty(pretty))
                    return pretty;
            }
            catch
            {
            }
            try
            {
                if (!string.IsNullOrEmpty(type.name))
                    return type.name;
            }
            catch
            {
            }
            try
            {
                return "id=" + type.UNIQUE_ENTITY_ID;
            }
            catch
            {
                return "?";
            }
        }

        private static string Signed(int v)
        {
            return v > 0 ? ("+" + v) : v.ToString();
        }

        public static int CurrentScaleActual(ScaleDefinition def)
        {
            try
            {
                if (Game.I == null || Game.I.scalesHandler == null || def == null)
                    return 0;
                ScaleBalance bal = Game.I.scalesHandler.GetBalance(def);
                return bal != null ? bal.ActualScaleValue : 0;
            }
            catch
            {
                return 0;
            }
        }

        public static int CurrentResourceStock(ResourceType type)
        {
            try
            {
                if (Game.I == null || Game.I.resourcesHandler == null || type == null)
                    return 0;
                ResourcesContainer c = Game.I.resourcesHandler.GetResourceContainer(type);
                if (c == null)
                    return 0;
                return c.CurrentWithoutAdded;
            }
            catch
            {
                return 0;
            }
        }
    }
}
