using System;
using System.Text.Json;
using ChickenClient.Models;

namespace ChickenClient.Services;

public class ConfigManager
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ChickenClient",
        "servers.json");

    public List<ServerConfig> Servers { get; private set; } = new();

    public void Load()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                Servers = JsonSerializer.Deserialize<List<ServerConfig>>(json) ?? new();
            }
        }
        catch
        {
            Servers = new();
        }
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(ConfigPath);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(Servers, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }

    public void AddServer(ServerConfig server)
    {
        ValidateServerConfig(server);
        Servers.Add(server);
        Save();
    }

    public void RemoveServer(string name)
    {
        Servers.RemoveAll(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    public ServerConfig? GetServer(string name)
    {
        return Servers.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public void UpdateServer(string name, ServerConfig updated)
    {
        ValidateServerConfig(updated);
        var idx = Servers.FindIndex(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            Servers[idx] = updated;
            Save();
        }
    }

    private void ValidateServerConfig(ServerConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Name))
            throw new ArgumentException("Server name cannot be empty.");
        if (string.IsNullOrWhiteSpace(config.Host))
            throw new ArgumentException("Server host cannot be empty.");
        if (config.Port <= 0 || config.Port > 65535)
            throw new ArgumentException("Server port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(config.Nick))
            throw new ArgumentException("Nick cannot be empty.");
    }
}
