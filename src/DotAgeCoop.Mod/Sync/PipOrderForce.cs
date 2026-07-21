using System.Collections.Generic;

namespace DotAgeCoop.Sync
{

    public static class PipOrderForce
    {
        private static readonly Queue<int> _uids = new Queue<int>();
        private static bool _active;

        public static bool Active { get { return _active; } }

        public static void Begin(int[] workerUids)
        {
            _uids.Clear();
            _active = true;
            if (workerUids == null)
                return;
            for (int i = 0; i < workerUids.Length; i++)
            {
                if (workerUids[i] != 0)
                    _uids.Enqueue(workerUids[i]);
            }
        }

        public static void End()
        {
            _active = false;
            _uids.Clear();
        }

        public static bool TryTakeNextUid(out int uid)
        {
            uid = 0;
            if (!_active || _uids.Count == 0)
                return false;
            uid = _uids.Dequeue();
            return true;
        }
    }
}
