namespace CourseXML.Models
{
    public class CurrencyServiceConfig
    {
        public int PollingIntervalSeconds { get; set; } = 2;
        public bool UseFileSystemWatcher { get; set; } = true;
        public bool EnableVerboseLogging { get; set; } = false;
        public bool EnableKeepAlive { get; set; } = true;
        public int KeepAliveIntervalSeconds { get; set; } = 20;

        public bool UseRemoteFile { get; set; } = false;
        public int RemotePollingIntervalSeconds { get; set; } = 10;

        public string SourceXmlPath { get; set; } = "";
        public string CurrentXmlPath { get; set; } = "";
        public string ArchiveFolder { get; set; } = "";

        public bool UseDynamicFileNames { get; set; } = true;
        public string FileNamePattern { get; set; } = "rates_*.xml"; 
        public string FileNameDateFormat { get; set; } = "yyyyMMddHHmm";
        public bool UseLatestFileByDate { get; set; } = true;
    }
}