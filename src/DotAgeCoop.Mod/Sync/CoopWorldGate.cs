namespace DotAgeCoop.Sync
{

    public static class CoopWorldGate
    {
        public static bool Active
        {
            get
            {
                ModMain mod = ModMain.Instance;
                if (mod == null || mod.Session == null || !mod.Session.Active)
                    return false;
                if (!mod.Session.HasCoopPartner)
                    return false;

                try
                {
                    if (HardSyncService.BlocksPlayInput)
                        return true;
                    if (mod.HardSync != null && mod.HardSync.IsActive)
                        return true;
                }
                catch
                {
                }

                try
                {
                    if (GameBootstrapService.BlocksPlayInput)
                        return true;
                    if (mod.Bootstrap != null && mod.Bootstrap.IsPeerLoadWaitActive)
                        return true;
                }
                catch
                {
                }

                try
                {
                    if (mod.TurnSync != null && mod.TurnSync.BlocksWorldInteraction)
                        return true;
                }
                catch
                {
                }

                return false;
            }
        }

        public static bool BlocksContext
        {
            get
            {
                if (Active)
                    return true;

                ModMain mod = ModMain.Instance;
                if (mod == null || mod.Session == null || !mod.Session.Active)
                    return false;
                if (!mod.Session.HasCoopPartner)
                    return false;

                try
                {
                    if (mod.TurnSync != null && mod.TurnSync.BlocksContextActions)
                        return true;
                }
                catch
                {
                }

                return false;
            }
        }
    }
}
