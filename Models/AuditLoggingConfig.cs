namespace AuditLoggingApp.Models
{
    public class AuditLoggingConfig
    {
        public int BatchSize { get; set; } = 100;
        public int FlushIntervalSeconds { get; set; } = 10;
    }
}
