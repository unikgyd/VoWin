namespace VoWin.Models
{
    public class LogEntryModel
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string Level { get; set; } = "INFO";
        public string Source { get; set; } = "Kernel";
        public string Message { get; set; } = string.Empty;

        public string FormattedTime => Timestamp.ToString("HH:mm:ss.fff");

        public string LevelColor => Level switch
        {
            "ERROR" => "#DC2626",
            "WARN" or "WARNING" => "#F59E0B",
            "DEBUG" => "#6B7280",
            _ => "#2563EB"
        };

        public string LevelBackground => Level switch
        {
            "ERROR" => "#1ADC2626",
            "WARN" or "WARNING" => "#1AF59E0B",
            "DEBUG" => "#1A6B7280",
            _ => "#1A2563EB"
        };
    }
}
