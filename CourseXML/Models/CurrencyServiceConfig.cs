namespace CourseXML.Models
{
    public class CurrencyServiceConfig
    {
        // Основные параметры
        public int PollingIntervalSeconds { get; set; } = 2;
        public bool UseFileSystemWatcher { get; set; } = true;
        public bool EnableVerboseLogging { get; set; } = false;
        public bool EnableKeepAlive { get; set; } = true;
        public int KeepAliveIntervalSeconds { get; set; } = 20;

        // Удаленный файл
        public bool UseRemoteFile { get; set; } = false;
        public int RemotePollingIntervalSeconds { get; set; } = 10;

        // Локальные пути
        public string SourceXmlPath { get; set; } = "";
        public string CurrentXmlPath { get; set; } = "";
        public string ArchiveFolder { get; set; } = "";
    }
}
