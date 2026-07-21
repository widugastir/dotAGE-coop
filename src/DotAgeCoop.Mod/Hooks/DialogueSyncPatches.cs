using HarmonyLib;

namespace DotAgeCoop.Hooks
{
    [HarmonyPatch(typeof(TutorialInstance), "Complete")]
    public static class TutorialCompletePatches
    {
        public static bool Prefix(TutorialInstance __instance, bool forceCompletion)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.DialogueSync == null)
                return true;

            return mod.DialogueSync.ShouldAllowComplete(__instance, forceCompletion);
        }
    }

    [HarmonyPatch(typeof(TutorialController), "ForceShow")]
    public static class TutorialForceShowPatches
    {
        public static void Postfix(TutorialInstance ti)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.DialogueSync == null)
                return;

            mod.DialogueSync.OnDialogueShown(ti);
        }
    }

    [HarmonyPatch(typeof(TutorialInstance), "Show")]
    public static class TutorialInstanceShowPatches
    {
        public static void Postfix(TutorialInstance __instance)
        {
            ModMain mod = ModMain.Instance;
            if (mod == null || mod.DialogueSync == null)
                return;

            mod.DialogueSync.OnDialogueShown(__instance);
        }
    }
}
