namespace ChickenClient.Models;

public class ServerConfig
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 6667;
    public string Nick { get; set; } = "ChickenUser";
    public string? Password { get; set; }
    public List<string> AutoJoinChannels { get; set; } = new();
    public bool UseSsl { get; set; } = false;

    // Bouncer (ZNC/BNC) support
    public bool IsBouncer { get; set; } = false;
    public string? BouncerUsername { get; set; }
    public string? NetworkName { get; set; }
}