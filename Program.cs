using ChickenClient.Models;
using ChickenClient.Services;

var configManager = new ConfigManager();
configManager.Load();

// State
Dictionary<string, IrcClient?> _connections = new(StringComparer.OrdinalIgnoreCase);
string? _activeServer = null;
string? _activeChannel = null;
Dictionary<string, ChannelInfo> _channelMessages = new(StringComparer.OrdinalIgnoreCase);
bool _running = true;

// UI Sync Lock and Input Buffer
string _currentInput = "";
var _consoleLock = new object();

// Redirect logger to our thread-safe UI writer
Logger.LogHandler = (msg) => SafeWriteLine(msg);

Console.Clear();
Console.WriteLine("=== ChickenClient IRC ===");
Console.WriteLine("Type /help for commands.");
Console.WriteLine();

while (_running)
{
    var input = SafeReadLine();

    if (string.IsNullOrWhiteSpace(input))
        continue;

    if (input.StartsWith("/"))
    {
        try
        {
            await HandleCommandAsync(input.Trim());
        }
        catch (Exception ex)
        {
            Logger.Error($"Error handling command: {ex.Message}");
        }
    }
    else if (_activeServer != null && _activeChannel != null && _connections[_activeServer]?.Connected == true)
    {
        var echo = $"<{_connections[_activeServer]!.CurrentNick}> {input}";
        try
        {
            await _connections[_activeServer]!.SendMessageAsync(_activeChannel, input);
        }
        catch (Exception ex)
        {
            Logger.Error($"Error sending message: {ex.Message}");
        }
        SafeWriteLine(echo);
        if (_channelMessages.ContainsKey(_activeChannel))
            _channelMessages[_activeChannel].Messages.Add(echo);
    }
    else
    {
        Logger.Warn("Not connected to a channel or user. Use /connect <server>, /join <channel>, or /switch <server> <target> first.");
    }
}

// --- Safe UI Thread-Safe Handling Methods ---

string BuildPrompt()
{
    var server = _activeServer ?? "no-server";
    var channel = _activeChannel ?? "status";
    
    // Dynamically retrieve your active nickname if connected to the current server
    string nick = "ChickenUser";
    if (_activeServer != null && _connections.TryGetValue(_activeServer, out var client) && client?.Connected == true)
    {
        nick = client.CurrentNick;
    }
    
    return $"[{server}/{channel}] {nick} > ";
}

string SafeReadLine()
{
    _currentInput = "";
    var prompt = BuildPrompt();
    
    lock (_consoleLock)
    {
        // Clear the line entirely before drawing the fresh prompt to prevent stacking
        Console.Write("\r" + new string(' ', Console.WindowWidth - 1) + "\r");
        Console.Write(prompt);
    }

    while (true)
    {
        var keyInfo = Console.ReadKey(intercept: true);

        if (keyInfo.Key == ConsoleKey.Enter)
        {
            lock (_consoleLock)
            {
                // Erase the prompt and input text from the current line completely on Enter
                Console.Write("\r" + new string(' ', Console.WindowWidth - 1) + "\r");
            }
            return _currentInput;
        }
        else if (keyInfo.Key == ConsoleKey.Backspace)
        {
            if (_currentInput.Length > 0)
            {
                _currentInput = _currentInput[..^1];
                lock (_consoleLock)
                {
                    // Erase character visually from console line
                    Console.Write("\b \b");
                }
            }
        }
        else if (keyInfo.KeyChar != '\0')
        {
            _currentInput += keyInfo.KeyChar;
            lock (_consoleLock)
            {
                Console.Write(keyInfo.KeyChar);
            }
        }
    }
}

void SafeWriteLine(string text)
{
    lock (_consoleLock)
    {
        // 1. Clear current user active line text completely
        int currentLineCursor = Console.CursorLeft;
        Console.Write(new string('\b', currentLineCursor) + new string(' ', currentLineCursor) + new string('\b', currentLineCursor));
        
        // Clear whatever might be sitting further right on prompt row 
        Console.Write(new string(' ', Console.WindowWidth - 1));
        Console.Write(new string('\b', Console.WindowWidth - 1));

        // 2. Print incoming message safely above active typing text
        Console.WriteLine(text);

        // 3. Redraw active entry line context perfectly intact
        Console.Write(BuildPrompt() + _currentInput);
    }
}

void ClearAndRedrawActiveBuffer()
{
    lock (_consoleLock)
    {
        Console.Clear();
        if (_activeChannel != null && _channelMessages.TryGetValue(_activeChannel, out var channelInfo))
        {
            foreach (var message in channelInfo.Messages)
            {
                Console.WriteLine(message);
            }
        }
        Console.Write(BuildPrompt() + _currentInput);
    }
}

// --- Command Handlers ---

async Task HandleCommandAsync(string cmd)
{
    var parts = ParseCommand(cmd);
    if (parts.Count == 0) return;

    var command = parts[0].ToLower();
    var args = parts.Skip(1).ToList();

    switch (command)
    {
        case "help":
            ShowHelp();
            break;

        case "quit":
        case "exit":
            await DisconnectAllAsync();
            _running = false;
            break;

        case "server":
        case "servers":
            await HandleServerCommand(args);
            break;

        case "bouncer":
        case "bnc":
            await HandleBouncerCommand(args);
            break;

        case "connect":
            await HandleConnectCommand(args);
            break;

        case "disconnect":
            await HandleDisconnectCommand(args);
            break;

        case "join":
            await HandleJoinCommand(args);
            break;

        case "part":
        case "leave":
            await HandlePartCommand(args);
            break;

        case "channel":
        case "channels":
            HandleChannelCommand(args);
            break;

        case "switch":
            HandleSwitchCommand(args);
            break;

        case "nick":
            await HandleNickCommand(args);
            break;

        case "raw":
        case "quote":
            if (args.Count > 0 && _activeServer != null && _connections[_activeServer]?.Connected == true)
            {
                await _connections[_activeServer]!.SendRawAsync(string.Join(" ", args));
            }
            else
            {
                SafeWriteLine("Not connected to a server.");
            }
            break;

        case "msg":
            if (args.Count >= 2)
            {
                var target = args[0];
                var message = string.Join(" ", args.Skip(1));
                if (_activeServer != null && _connections[_activeServer]?.Connected == true)
                {
                    await _connections[_activeServer]!.SendMessageAsync(target, message);
                    
                    // Track sent PMs in historical buffer
                    if (!_channelMessages.ContainsKey(target))
                        _channelMessages[target] = new ChannelInfo();
                    
                    var echo = $"<{_connections[_activeServer]!.CurrentNick}> {message}";
                    _channelMessages[target].Messages.Add(echo);
                    
                    if (_activeChannel?.Equals(target, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        SafeWriteLine(echo);
                    }
                }
                else
                {
                    SafeWriteLine("Not connected to a server.");
                }
            }
            else
            {
                SafeWriteLine("Usage: /msg <target> <message>");
            }
            break;

        default:
            SafeWriteLine($"Unknown command: {command}. Type /help for commands.");
            break;
    }
}

List<string> ParseCommand(string cmd)
{
    var result = new List<string>();
    var inQuote = false;
    var current = new System.Text.StringBuilder();

    foreach (var ch in cmd.TrimStart('/'))
    {
        if (ch == '"' && !inQuote)
        {
            inQuote = true;
        }
        else if (ch == '"' && inQuote)
        {
            inQuote = false;
        }
        else if (ch == ' ' && !inQuote)
        {
            if (current.Length > 0)
            {
                result.Add(current.ToString());
                current.Clear();
            }
        }
        else
        {
            current.Append(ch);
        }
    }

    if (current.Length > 0)
        result.Add(current.ToString());

    return result;
}

void ShowHelp()
{
    SafeWriteLine("");
    SafeWriteLine("=== ChickenClient Commands ===");
    SafeWriteLine("  /help                          - Show this help");
    SafeWriteLine("  /quit                          - Exit the client");
    SafeWriteLine("");
    SafeWriteLine("  -- Server Management --");
    SafeWriteLine("  /servers                       - List configured servers");
    SafeWriteLine("  /server add <name> <host> [port] [nick] [password] [ssl]");
    SafeWriteLine("                                 - Add a direct IRC server connection");
    SafeWriteLine("  /server remove <name>          - Remove a server config");
    SafeWriteLine("  /server edit <name> <field> <value>");
    SafeWriteLine("                                 - Edit a server field");
    SafeWriteLine("                                 - Fields: host, port, nick, password, ssl");
    SafeWriteLine("  /connect <name>                - Connect to a configured server");
    SafeWriteLine("  /disconnect                    - Disconnect from current server");
    SafeWriteLine("");
    SafeWriteLine("  -- Bouncer (ZNC/BNC) Support --");
    SafeWriteLine("  /bouncer add <name> <host> <port> <username> <password> <network> [ssl]");
    SafeWriteLine("                                 - Add a ZNC/BNC bouncer connection");
    SafeWriteLine("  /bouncer edit <name> <field> <value>");
    SafeWriteLine("                                 - Edit bouncer field");
    SafeWriteLine("                                 - Fields: host, port, username, password, network, ssl");
    SafeWriteLine("");
    SafeWriteLine("  -- Channel Management --");
    SafeWriteLine("  /join <#channel>               - Join a channel on current server");
    SafeWriteLine("  /part [#channel]               - Leave a channel (default: current)");
    SafeWriteLine("  /channels                      - List channels on current server");
    SafeWriteLine("");
    SafeWriteLine("  -- Navigation --");
    SafeWriteLine("  /switch <server> [channel]     - Switch active server/channel or user PM context");
    SafeWriteLine("  /channel <name>                - Switch active channel or user PM context");
    SafeWriteLine("");
    SafeWriteLine("  -- Communication --");
    SafeWriteLine("  /nick <newnick>                - Change your nickname");
    SafeWriteLine("  /msg <target> <message>        - Send a private message");
    SafeWriteLine("  /raw <command>                 - Send raw IRC command");
    SafeWriteLine("");
    SafeWriteLine("  -- Examples --");
    SafeWriteLine("  Direct server:");
    SafeWriteLine("    /server add mynet irc.example.com 6667 MyNick");
    SafeWriteLine("    /connect mynet");
    SafeWriteLine("    /join #chat");
    SafeWriteLine("");
    SafeWriteLine("  ZNC Bouncer:");
    SafeWriteLine("    /bouncer add myznc znc.example.com 6697 myuser mypass network true");
    SafeWriteLine("    /connect mync");
    SafeWriteLine("    /join #chat");
    SafeWriteLine("");
}

async Task HandleServerCommand(List<string> args)
{
    if (args.Count == 0)
    {
        SafeWriteLine("Configured servers:");
        if (configManager.Servers.Count == 0)
        {
            SafeWriteLine("  (none)");
        }
        else
        {
            foreach (var s in configManager.Servers)
            {
                var status = _connections.ContainsKey(s.Name) && _connections[s.Name]?.Connected == true ? "connected" : "disconnected";
                var type = s.IsBouncer ? "bouncer" : "direct";
                var network = s.NetworkName != null ? $" [{s.NetworkName}]" : "";
                SafeWriteLine($"  {s.Name} -> {s.Host}:{s.Port} [{type}]{network} [SSL: {s.UseSsl}] [{status}]");
            }
        }
        return;
    }

    var subCmd = args[0].ToLower();
    switch (subCmd)
    {
        case "add":
            if (args.Count < 3)
            {
                SafeWriteLine("Usage: /server add <name> <host> [port] [nick] [password] [ssl]");
                return;
            }
            var newServer = new ServerConfig
            {
                Name = args[1],
                Host = args[2],
                Nick = args.Count > 4 ? args[4] : "ChickenUser",
                Password = args.Count > 5 ? args[5] : null
            };
            if (args.Count > 3 && int.TryParse(args[3], out var portVal))
                newServer.Port = portVal;
            newServer.UseSsl = (args.Count > 6 && args[6].Equals("true", StringComparison.OrdinalIgnoreCase))
                               || (newServer.Port == 6697);
            try
            {
                configManager.AddServer(newServer);
                SafeWriteLine($"Server '{newServer.Name}' added ({newServer.Host}:{newServer.Port}, SSL: {newServer.UseSsl}).");
            }
            catch (Exception ex)
            {
                SafeWriteLine($"Error adding server: {ex.Message}");
            }
            break;

        case "remove":
        case "rm":
            if (args.Count < 2)
            {
                SafeWriteLine("Usage: /server remove <name>");
                return;
            }
            configManager.RemoveServer(args[1]);
            SafeWriteLine($"Server '{args[1]}' removed.");
            break;

        case "edit":
            if (args.Count < 4)
            {
                SafeWriteLine("Usage: /server edit <name> <field> <value>");
                Console.WriteLine("Fields: host, port, nick, password, ssl");
                return;
            }
            var existing = configManager.GetServer(args[1]);
            if (existing == null)
            {
                SafeWriteLine($"Server '{args[1]}' not found.");
                return;
            }
            var field = args[2].ToLower();
            var val = args[3];
            switch (field)
            {
                case "host": existing.Host = val; break;
                case "port": existing.Port = int.TryParse(val, out var p) ? p : existing.Port; break;
                case "nick": existing.Nick = val; break;
                case "password": existing.Password = val; break;
                case "ssl": existing.UseSsl = val.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                default: SafeWriteLine($"Unknown field: {field}"); return;
            }
            try
            {
                configManager.UpdateServer(args[1], existing);
                SafeWriteLine($"Server '{args[1]}' updated (SSL: {existing.UseSsl}).");
            }
            catch (Exception ex)
            {
                SafeWriteLine($"Error updating server: {ex.Message}");
            }
            break;

        default:
            SafeWriteLine($"Unknown subcommand: {subCmd}");
            break;
    }
}

async Task HandleBouncerCommand(List<string> args)
{
    if (args.Count == 0)
    {
        SafeWriteLine("Usage: /bouncer add <name> <host> <port> <username> <password> <network> [ssl]");
        SafeWriteLine("       /bouncer edit <name> <field> <value>");
        SafeWriteLine("Fields: host, port, username, password, network, ssl");
        return;
    }

    var subCmd = args[0].ToLower();
    switch (subCmd)
    {
        case "add":
            if (args.Count < 7)
            {
                SafeWriteLine("Usage: /bouncer add <name> <host> <port> <username> <password> <network> [ssl]");
                return;
            }
            var bouncer = new ServerConfig
            {
                Name = args[1],
                Host = args[2],
                IsBouncer = true,
                BouncerUsername = args[4],
                Password = args[5],
                NetworkName = args[6],
                Nick = args[4]
            };
            if (int.TryParse(args[3], out var bPort))
                bouncer.Port = bPort;
            bouncer.UseSsl = (args.Count > 7 && args[7].Equals("true", StringComparison.OrdinalIgnoreCase))
                             || (bouncer.Port == 6697);
            try
            {
                configManager.AddServer(bouncer);
                SafeWriteLine($"Bouncer '{bouncer.Name}' added ({bouncer.Host}:{bouncer.Port}, network: {bouncer.NetworkName}, SSL: {bouncer.UseSsl}).");
            }
            catch (Exception ex)
            {
                SafeWriteLine($"Error adding bouncer: {ex.Message}");
            }
            break;

        case "edit":
            if (args.Count < 4)
            {
                SafeWriteLine("Usage: /bouncer edit <name> <field> <value>");
                SafeWriteLine("Fields: host, port, username, password, network, ssl");
                return;
            }
            var existing = configManager.GetServer(args[1]);
            if (existing == null)
            {
                SafeWriteLine($"Bouncer '{args[1]}' not found.");
                return;
            }
            var field = args[2].ToLower();
            var val = args[3];
            switch (field)
            {
                case "host": existing.Host = val; break;
                case "port": existing.Port = int.TryParse(val, out var p) ? p : existing.Port; break;
                case "username": existing.BouncerUsername = val; existing.Nick = val; break;
                case "password": existing.Password = val; break;
                case "network": existing.NetworkName = val; break;
                case "ssl": existing.UseSsl = val.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                default: SafeWriteLine($"Unknown field: {field}"); return;
            }
            try
            {
                configManager.UpdateServer(args[1], existing);
                SafeWriteLine($"Bouncer '{args[1]}' updated.");
            }
            catch (Exception ex)
            {
                SafeWriteLine($"Error updating bouncer: {ex.Message}");
            }
            break;

        default:
            SafeWriteLine($"Unknown subcommand: {subCmd}");
            break;
    }
}

async Task HandleConnectCommand(List<string> args)
{
    if (args.Count == 0)
    {
        SafeWriteLine("Usage: /connect <servername>");
        return;
    }

    var serverName = args[0];
    var config = configManager.GetServer(serverName);
    if (config == null)
    {
        SafeWriteLine($"Server '{serverName}' not found. Add it first with /server add or /bouncer add.");
        return;
    }

    if (_connections.ContainsKey(serverName) && _connections[serverName]?.Connected == true)
    {
        SafeWriteLine($"Already connected to '{serverName}'.");
        _activeServer = serverName;
        return;
    }

    var typeLabel = config.IsBouncer ? $"bouncer ({config.NetworkName})" : "direct";
    SafeWriteLine($"Connecting to {config.Host}:{config.Port} [{typeLabel}] (SSL: {config.UseSsl})...");
    var client = new IrcClient(config);

    client.OnMessageReceived += msg =>
    {
        // 1. Match typical channel routing [##channel] or [#channel]
        var channelMatch = System.Text.RegularExpressions.Regex.Match(msg, @"^\[(?<channel>[#&][^\]]+)\]");
        
        // 2. Extracted helper to process PM notifications mapping directly to a target destination
        var pmMatch = System.Text.RegularExpressions.Regex.Match(msg, @"^\[(?<target>[^\]#&]+)\]\s+<(?<nick>[^>]+)>\s+(?<message>.+)$");

        if (channelMatch.Success)
        {
            var ch = channelMatch.Groups["channel"].Value;
            if (!_channelMessages.ContainsKey(ch))
                _channelMessages[ch] = new ChannelInfo();
            _channelMessages[ch].Messages.Add(msg);

            if (_activeServer == serverName && _activeChannel == ch)
            {
                SafeWriteLine(msg);
            }
        }
        else if (pmMatch.Success)
        {
            var nick = pmMatch.Groups["nick"].Value;
            var target = pmMatch.Groups["target"].Value;
            
            // Determine if the target context is us (incoming) or another person (outgoing)
            string conversationKey = target.Equals(client.CurrentNick, StringComparison.OrdinalIgnoreCase) ? nick : target;

            if (!_channelMessages.ContainsKey(conversationKey))
                _channelMessages[conversationKey] = new ChannelInfo();
            
            _channelMessages[conversationKey].Messages.Add(msg);

            if (_activeServer == serverName && _activeChannel?.Equals(conversationKey, StringComparison.OrdinalIgnoreCase) == true)
            {
                SafeWriteLine(msg);
            }
        }
        else
        {
            // Fallback status/server logging
            if (_activeServer == serverName && (_activeChannel == null || _activeChannel == "status"))
            {
                SafeWriteLine(msg);
            }
        }
    };

    client.OnError += err =>
    {
        SafeWriteLine($"ERROR [{serverName}]: {err}");
    };

    client.OnDisconnected += () =>
    {
        SafeWriteLine($"Disconnected from '{serverName}'.");
    };

    await client.ConnectAsync();
    _connections[serverName] = client;
    _activeServer = serverName;

    foreach (var ch in config.AutoJoinChannels)
    {
        await client.JoinChannelAsync(ch);
        if (!_channelMessages.ContainsKey(ch))
            _channelMessages[ch] = new ChannelInfo();
    }

    if (config.AutoJoinChannels.Count > 0)
    {
        _activeChannel = config.AutoJoinChannels[0];
        ClearAndRedrawActiveBuffer();
    }
}

async Task HandleDisconnectCommand(List<string> args)
{
    var serverName = args.Count > 0 ? args[0] : _activeServer;
    if (serverName == null || !_connections.ContainsKey(serverName))
    {
        SafeWriteLine("Not connected to that server.");
        return;
    }

    await _connections[serverName]!.DisconnectAsync();
    _connections[serverName] = null;

    if (_activeServer == serverName)
    {
        _activeServer = null;
        _activeChannel = null;
    }
}

async Task HandleJoinCommand(List<string> args)
{
    if (args.Count == 0)
    {
        SafeWriteLine("Usage: /join <#channel>");
        return;
    }

    if (_activeServer == null || _connections[_activeServer]?.Connected != true)
    {
        SafeWriteLine("Not connected to a server. Use /connect first.");
        return;
    }

    var channel = args[0];
    if (!channel.StartsWith('#')) channel = "#" + channel;

    await _connections[_activeServer]!.JoinChannelAsync(channel);
    if (!_channelMessages.ContainsKey(channel))
        _channelMessages[channel] = new ChannelInfo();
    
    _activeChannel = channel;
    ClearAndRedrawActiveBuffer();
}

async Task HandlePartCommand(List<string> args)
{
    if (_activeServer == null || _connections[_activeServer]?.Connected != true)
    {
        SafeWriteLine("Not connected to a server.");
        return;
    }

    var channel = args.Count > 0 ? args[0] : _activeChannel;
    if (channel == null)
    {
        SafeWriteLine("Not in a channel.");
        return;
    }

    if (!channel.StartsWith('#')) channel = "#" + channel;
    await _connections[_activeServer]!.PartChannelAsync(channel);
    SafeWriteLine($"Left {channel}.");

    if (_activeChannel == channel)
    {
        _activeChannel = "status";
        ClearAndRedrawActiveBuffer();
    }
}

void HandleChannelCommand(List<string> args)
{
    if (args.Count > 0)
    {
        var ch = args[0];
        _activeChannel = ch;
        ClearAndRedrawActiveBuffer();
        return;
    }

    SafeWriteLine("Channels/PMs on current server:");
    var serverChannels = _channelMessages.Keys.ToList();

    if (serverChannels.Count == 0)
    {
        SafeWriteLine("  (none active)");
    }
    else
    {
        foreach (var ch in serverChannels)
        {
            var active = ch == _activeChannel ? " <-- active" : "";
            SafeWriteLine($"  {ch}{active}");
        }
    }
}

void HandleSwitchCommand(List<string> args)
{
    if (args.Count == 0)
    {
        SafeWriteLine("Usage: /switch <server> [channel/user]");
        SafeWriteLine("Current servers:");
        foreach (var kvp in _connections)
        {
            var status = kvp.Value?.Connected == true ? "connected" : "disconnected";
            var active = kvp.Key == _activeServer ? " <-- active" : "";
            SafeWriteLine($"  {kvp.Key} [{status}]{active}");
        }
        return;
    }

    var serverName = args[0];
    if (!_connections.ContainsKey(serverName) || _connections[serverName]?.Connected != true)
    {
        SafeWriteLine($"Server '{serverName}' is not connected.");
        return;
    }

    _activeServer = serverName;
    _activeChannel = args.Count > 1 ? args[1] : "status";
    ClearAndRedrawActiveBuffer();
}

async Task HandleNickCommand(List<string> args)
{
    if (args.Count == 0)
    {
        SafeWriteLine($"Current nick: {(_activeServer != null ? _connections[_activeServer]?.CurrentNick : null) ?? "N/A"}");
        return;
    }

    if (_activeServer == null || _connections[_activeServer]?.Connected != true)
    {
        SafeWriteLine("Not connected to a server.");
        return;
    }

    var newNick = args[0];
    await _connections[_activeServer]!.ChangeNickAsync(newNick);
    SafeWriteLine($"Nick changed to {newNick}.");
}

async Task DisconnectAllAsync()
{
    foreach (var kvp in _connections)
    {
        if (kvp.Value?.Connected == true)
        {
            await kvp.Value.DisconnectAsync("ChickenClient closing");
        }
        kvp.Value?.Dispose();
    }
    _connections.Clear();
}