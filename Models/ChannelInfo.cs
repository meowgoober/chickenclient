namespace ChickenClient.Models;

public class ChannelInfo
{
    public string Name { get; set; } = "";
    public List<string> Messages { get; set; } = new();
    public List<string> Users { get; set; } = new();
}