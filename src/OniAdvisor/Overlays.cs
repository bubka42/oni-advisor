namespace OniAdvisor
{
    /// <summary>
    /// What the advisor overlay shows outside planning mode. Ctrl+Shift+M cycles:
    /// off -> marks -> marks + projects -> projects only -> off.
    /// Ctrl+Shift+N cycles the project focus (all projects / one at a time).
    /// </summary>
    internal static class Overlays
    {
        static int mode; // 0 off, 1 marks, 2 marks+projects, 3 projects

        public static bool ShowMarks => mode == 1 || mode == 2 || PlanMode.Active;
        public static bool ShowProjects => mode == 2 || mode == 3;

        public static string Describe() => mode switch
        {
            0 => "advisor overlay off",
            1 => "showing marks",
            2 => "showing marks and projects",
            _ => "showing projects",
        };

        public static void Cycle()
        {
            mode = (mode + 1) % 4;
            Apply();
            Log.Info(Describe());
        }

        static float applyAt = -1f;

        public static void Apply() { applyAt = -1f; PlanMarks.SetVisible(ShowMarks); }

        /// <summary>Applies the visibility later (keeps marks visible for a pending screenshot).</summary>
        public static void ApplyAt(float unscaledTime) => applyAt = unscaledTime;

        public static void Tick(float now) { if (applyAt >= 0f && now >= applyAt) Apply(); }

        /// <summary>Turns projects on, keeping marks as they were (SHOW in the advisor panel).</summary>
        public static void ShowProjectsNow() { mode = mode == 1 ? 2 : mode == 0 ? 3 : mode; Apply(); }

        /// <summary>Turns marks on, keeping projects as they were (the advisor just drew new marks).</summary>
        public static void ShowMarksNow() { mode = mode == 3 ? 2 : mode == 0 ? 1 : mode; Apply(); }

        public static void Reset() => mode = 0;
    }
}
