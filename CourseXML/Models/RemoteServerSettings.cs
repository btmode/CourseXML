namespace CourseXML.Models;

public class RemoteServerSettings
{
    public string? Host { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? RemoteDirectory { get; set; }
    public string? FileName { get; set; } 
    public int Port { get; set; } = 22;
    public bool UseSshKey { get; set; } = false;
    public string? SshKeyPath { get; set; }

    // ¬ычисл€емое свойство дл€ полного пути
    public string FullRemotePath =>
        $"{RemoteDirectory?.TrimEnd('/')}/{FileName}";

    public string SshConnectionString =>
        $"{Username}@{Host}";
}