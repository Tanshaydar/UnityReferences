using System.Collections.Generic;

namespace UnityEngine.Rendering.HighDefinition
{
    class CullingGroupManager
    {
        static CullingGroupManager s_Instance;

#if UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void ResetStaticsOnLoad()
        {
            s_Instance?.Cleanup();
            s_Instance = null;
        }
#endif

        public static CullingGroupManager instance => s_Instance ??= new CullingGroupManager();

        private Stack<CullingGroup> m_FreeList = new Stack<CullingGroup>();

        public CullingGroup Alloc()
        {
            CullingGroup group;
            if (m_FreeList.Count > 0)
            {
                group = m_FreeList.Pop();
                group.enabled = true;
            }
            else
            {
                group = new CullingGroup();
            }
            return group;
        }

        public void Free(CullingGroup group)
        {
            // Disable group to ensure it is not being used anymore during culling
            group.enabled = false;
            m_FreeList.Push(group);
        }

        public void Cleanup()
        {
            foreach (CullingGroup group in m_FreeList)
            {
                group.Dispose();
            }
            m_FreeList.Clear();
        }
    }
}
