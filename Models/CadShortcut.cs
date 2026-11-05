namespace AutoCAD.AI.Assistant.Models
{
    public sealed class CadShortcut
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Command { get; set; }
        public int UsageCount { get; set; }
        public long LastUsedTicks { get; set; }
        public bool BuiltIn { get; set; }

        public string CountText
        {
            get { return UsageCount > 0 ? "×" + UsageCount : string.Empty; }
        }
    }
}
