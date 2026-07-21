using System;
using HarmonyLib;
using DotAgeCoop.Sync;

namespace DotAgeCoop.Hooks
{
    [HarmonyPatch(typeof(ScalesHandler), "Increase", typeof(ScaleDefinition), typeof(int), typeof(FlowType))]
    public static class ScalesIncreasePatches
    {
        public static void Postfix(ScaleDefinition scale, int dValue, FlowType flowType)
        {
            LogScale("local+", scale, dValue, flowType);
            MarkDirty();
        }

        private static void MarkDirty()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.ScalesSync == null || mod.Session == null || !mod.Session.Active)
                return;
            if (!mod.Session.IsHost || mod.ScalesSync.ApplyingRemote)
                return;
            mod.ScalesSync.MarkScalesDirty();
        }

        private static void LogScale(string source, ScaleDefinition scale, int dValue, FlowType flowType)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.Session == null || !mod.Session.Active)
                return;
            if (mod.ScalesSync != null && mod.ScalesSync.ApplyingRemote)
                return;
            if (dValue == 0)
                return;
            EconDebugLog.ScaleDelta(source, scale, dValue, flowType, EconDebugLog.CurrentScaleActual(scale));
        }
    }

    [HarmonyPatch(typeof(ScalesHandler), "Decrease", typeof(ScaleDefinition), typeof(int), typeof(FlowType))]
    public static class ScalesDecreasePatches
    {
        public static void Postfix(ScaleDefinition scale, int dValue, FlowType flowType)
        {
            ModMain mod = ModMain.Instance;
            if (mod != null && mod.Session != null && mod.Session.Active &&
                (mod.ScalesSync == null || !mod.ScalesSync.ApplyingRemote) && dValue != 0)
            {
                EconDebugLog.ScaleDelta("local-", scale, -Math.Abs(dValue), flowType,
                    EconDebugLog.CurrentScaleActual(scale));
            }

            if (mod == null || mod.ScalesSync == null || mod.Session == null || !mod.Session.Active)
                return;
            if (!mod.Session.IsHost || mod.ScalesSync.ApplyingRemote)
                return;
            mod.ScalesSync.MarkScalesDirty();
        }
    }

    [HarmonyPatch(typeof(ScalesHandler), "ForceValue")]
    public static class ScalesForceValuePatches
    {
        public static void Postfix()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.ScalesSync == null || mod.Session == null || !mod.Session.Active)
                return;
            if (!mod.Session.IsHost || mod.ScalesSync.ApplyingRemote)
                return;
            mod.ScalesSync.MarkScalesDirty();
        }
    }

    [HarmonyPatch(typeof(ScalesHandler), "SwitchScale")]
    public static class ScalesSwitchPatches
    {
        public static void Postfix()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.ScalesSync == null || mod.Session == null || !mod.Session.Active)
                return;
            if (!mod.Session.IsHost || mod.ScalesSync.ApplyingRemote)
                return;
            mod.ScalesSync.MarkScalesDirty();
        }
    }

    [HarmonyPatch(typeof(ScalesHandler), "ForceScenarioIndex")]
    public static class ScalesForceScenarioPatches
    {
        public static void Postfix()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.ScalesSync == null || mod.Session == null || !mod.Session.Active)
                return;
            if (!mod.Session.IsHost || mod.ScalesSync.ApplyingRemote)
                return;
            mod.ScalesSync.MarkScalesDirty();
        }
    }

    [HarmonyPatch(typeof(ScalesHandler), "CheckScaleEnabling")]
    public static class ScalesCheckEnablingPatches
    {
        public static void Postfix()
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.ScalesSync == null || mod.Session == null || !mod.Session.Active)
                return;
            if (!mod.Session.IsHost || mod.ScalesSync.ApplyingRemote)
                return;
            mod.ScalesSync.MarkScalesDirty();
        }
    }
}
