using System.Runtime.InteropServices;
using ChickenClient.Models;
using ChickenClient.Services;
using Microsoft.Toolkit.Uwp.Notifications;

var configManager = new ConfigManager();
configManager.Load();

// --- Windows API Native Imports ---
[DllImport("kernel32.dll")]
static extern IntPtr GetConsoleWindow();

[DllImport("user32.dll")]
static extern IntPtr GetForegroundWindow();

[DllImport("user32.dll")]
static extern bool MessageBeep(uint uType);

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
        catch (Exception)
        {
            Logger.Error("Error handling command.");
        }
    }
    else if (_activeServer != null && _activeChannel != null && _connections[_activeServer]?.Connected == true)
    {
        var echo = $"<{_connections[_activeServer]!.CurrentNick}> {input}";
        try
        {
            await _connections[_activeServer]!.SendMessageAsync(_activeChannel, input);
        }
        catch (Exception)
        {
            Logger.Error("Error sending message.");
        }
        
        if (!_channelMessages.ContainsKey(_activeChannel))
            _channelMessages[_activeChannel] = new ChannelInfo();
            
        _channelMessages[_activeChannel].Messages.Add(echo);
        SafeWriteLine(echo);
    }
    else
    {
        Logger.Warn("Not connected to a channel or PM context. Use /connect <server>, /join <channel>, or /query <user>.");
    }
}

// --- Windows Notification Helper ---

bool IsConsoleWindowFocused()
{
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        return true; 

    var consoleHandle = GetConsoleWindow();
    var foregroundHandle = GetForegroundWindow();
    return consoleHandle != IntPtr.Zero && consoleHandle == foregroundHandle;
}

void TriggerWindowsNotification(string title, string body)
{
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        return;

    try
    {
        // Plays the standard Windows Asterisk/Notification chime
        MessageBeep(0x00000040); 

        // Build native Toast Notification 
        new ToastContentBuilder()
            .AddText(title)
            .AddText(body)
            .SetToastScenario(ToastScenario.Default)
            .Show();
    }
    catch (Exception)
    {
        // Fail silently
    }
}

// --- Safe UI Thread-Safe Handling Methods ---

string BuildPrompt()
{
    var server = _activeServer ?? "no-server";
    var channel = _activeChannel ?? "status";
    
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
        int currentLineCursor = Console.CursorLeft;
        Console.Write(new string('\b', currentLineCursor) + new string(' ', currentLineCursor) + new string('\b', currentLineCursor));
        Console.Write(new string(' ', Console.WindowWidth - 1));
        Console.Write(new string('\b', Console.WindowWidth - 1));

        Console.WriteLine(text);
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
        case "query":
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
                    
                    if (!_channelMessages.ContainsKey(target))
                        _channelMessages[target] = new ChannelInfo { Name = target };

                    var echo = $"<{_connections[_activeServer]!.CurrentNick}> {message}";
                    _channelMessages[target].Messages.Add(echo);

                    _activeChannel = target;
                    ClearAndRedrawActiveBuffer();
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
    SafeWriteLine("  /connect <name>                - Connect to a configured server");
    SafeWriteLine("  /disconnect                    - Disconnect from current server");
    SafeWriteLine("");
    SafeWriteLine("  -- Channel Management --");
    SafeWriteLine("  /join <#channel>               - Join a channel on current server");
    SafeWriteLine("  /part [#channel]               - Leave a channel (default: current)");
    SafeWriteLine("  /channels                      - List channels/PM windows on current server");
    SafeWriteLine("");
    SafeWriteLine("  -- Navigation & Query windows --");
    SafeWriteLine("  /switch <server> [channel]     - Switch active server/channel or PM window");
    SafeWriteLine("  /channel <name>                - Switch active channel context");
    SafeWriteLine("  /query <username>              - Switch view directly to PM stream with a user");
    SafeWriteLine("");
    SafeWriteLine("  -- Communication --");
    SafeWriteLine("  /nick <newnick>                - Change your nickname");
    SafeWriteLine("  /msg <target> <message>        - Send a private message (auto-switches screen)");
    SafeWriteLine("  /raw <command>                 - Send raw IRC command");
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
            newServer.UseSsl = (args.Count > 6 && args[6].Equals("true", StringComparison.OrdinalIgnoreCase)) || (newServer.Port == 6697);
            try
            {
                configManager.AddServer(newServer);
                SafeWriteLine($"Server '{newServer.Name}' added ({newServer.Host}:{newServer.Port}, SSL: {newServer.UseSsl}).");
            }
            catch (Exception)
            {
                SafeWriteLine("Error adding server.");
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
        return;
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
        SafeWriteLine($"Server '{serverName}' not found.");
        return;
    }

    if (_connections.ContainsKey(serverName) && _connections[serverName]?.Connected == true)
    {
        SafeWriteLine($"Already connected to '{serverName}'.");
        _activeServer = serverName;
        return;
    }

    var typeLabel = config.IsBouncer ? $"bouncer ({config.NetworkName})" : "direct";
    SafeWriteLine($"Connecting to {config.Host}:{config.Port} [{typeLabel}]...");
    var client = new IrcClient(config);

    client.OnMessageReceived += msg =>
    {
        var isWindowFocused = IsConsoleWindowFocused();

        // 1. Identify standard Channel target messages
        var channelMatch = System.Text.RegularExpressions.Regex.Match(msg, @"^\[(?<channel>[#&][^\]]+)\]");
        
        // 2. Identify PM target messages
        var pmMatch = System.Text.RegularExpressions.Regex.Match(msg, @"^\[(?<target>[^#&\]\s]+)\]\s+<(?<sender>[^>]+)>\s+(?<message>.+)$");

        if (channelMatch.Success)
        {
            var ch = channelMatch.Groups["channel"].Value;
            if (!_channelMessages.ContainsKey(ch))
                _channelMessages[ch] = new ChannelInfo();
            _channelMessages[ch].Messages.Add(msg);

            bool isOurChannel = (_activeServer == serverName && _activeChannel == ch);
            bool containsNickname = msg.Contains(client.CurrentNick, StringComparison.OrdinalIgnoreCase);

            // Notify only if offtask/unfocused
            if (containsNickname && (!isWindowFocused || !isOurChannel))
            {
                var msgContent = msg;
                var nickIndex = msg.IndexOf('>');
                if (nickIndex != -1 && nickIndex + 1 < msg.Length)
                {
                    var sender = msg.Substring(0, nickIndex).Trim('[', ']', ' ');
                    var actualMsg = msg.Substring(nickIndex + 1).Trim();
                    msgContent = $"{sender} > {actualMsg}";
                }
                TriggerWindowsNotification($"ChickenClient - {ch}", msgContent);
            }

            if (isOurChannel)
            {
                SafeWriteLine(msg);
            }
        }
        else if (pmMatch.Success)
        {
            var sender = pmMatch.Groups["sender"].Value;
            var target = pmMatch.Groups["target"].Value;
            var payloadMessage = pmMatch.Groups["message"].Value;

            string conversationKey = target.Equals(client.CurrentNick, StringComparison.OrdinalIgnoreCase) ? sender : target;

            // Track what context was active prior to the incoming PM switch
            bool wasAlreadyViewingPm = (_activeServer == serverName && _activeChannel == conversationKey);

            if (!_channelMessages.ContainsKey(conversationKey))
            {
                _channelMessages[conversationKey] = new ChannelInfo { Name = conversationKey };
            }

            _channelMessages[conversationKey].Messages.Add(msg);

            // Notify on incoming PM if unfocused or focused on a completely different room
            if (!isWindowFocused || !wasAlreadyViewingPm)
            {
                TriggerWindowsNotification($"ChickenClient - {conversationKey}", $"{sender} > {payloadMessage}");
            }

            // Move user view automatically
            _activeServer = serverName;
            _activeChannel = conversationKey;

            // Add the notification message inside the active PM room log
            if (isWindowFocused && !wasAlreadyViewingPm)
            {
                _channelMessages[conversationKey].Messages.Add("* You have been moved here because you have received a PM *");
            }

            ClearAndRedrawActiveBuffer();
        }
        else
        {
            if (_activeServer == serverName && (_activeChannel == null || _activeChannel == "status"))
            {
                SafeWriteLine(msg);
            }
        }
    };

    client.OnError += err => SafeWriteLine($"ERROR [{serverName}]: {err}");
    client.OnDisconnected += () => SafeWriteLine($"Disconnected from '{serverName}'.");

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
    if (serverName == null || !_connections.ContainsKey(serverName)) return;

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
    if (args.Count == 0) return;
    if (_activeServer == null || _connections[_activeServer]?.Connected != true) return;

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
    if (_activeServer == null || _connections[_activeServer]?.Connected != true) return;
    var channel = args.Count > 0 ? args[0] : _activeChannel;
    if (channel == null) return;

    if (!channel.StartsWith('#')) channel = "#" + channel;
    await _connections[_activeServer]!.PartChannelAsync(channel);

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
        var target = args[0];
        
        if (target.StartsWith('#') || target.StartsWith('&'))
        {
            if (!_channelMessages.ContainsKey(target))
                _channelMessages[target] = new ChannelInfo { Name = target };
        }
        else
        {
            if (!_channelMessages.ContainsKey(target))
            {
                _channelMessages[target] = new ChannelInfo { Name = target };
                _channelMessages[target].Messages.Add($"--- Private Message session started with {target} ---");
            }
        }

        _activeChannel = target;
        ClearAndRedrawActiveBuffer();
        return;
    }

    SafeWriteLine("Active windows/PM streams on current server:");
    if (_channelMessages.Count == 0)
    {
        SafeWriteLine("  (none active)");
    }
    else
    {
        foreach (var ch in _channelMessages.Keys)
        {
            var active = ch == _activeChannel ? " <-- active" : "";
            SafeWriteLine($"  {ch}{active}");
        }
    }
}

void HandleSwitchCommand(List<string> args)
{
    if (args.Count == 0) return;
    var serverName = args[0];
    if (!_connections.ContainsKey(serverName) || _connections[serverName]?.Connected != true) return;

    _activeServer = serverName;
    _activeChannel = args.Count > 1 ? args[1] : "status";
    ClearAndRedrawActiveBuffer();
}

async Task HandleNickCommand(List<string> args)
{
    if (args.Count == 0 || _activeServer == null) return;
    var newNick = args[0];
    await _connections[_activeServer]!.ChangeNickAsync(newNick);
}

async Task DisconnectAllAsync()
{
    foreach (var kvp in _connections)
    {
        if (kvp.Value?.Connected == true)
            await kvp.Value.DisconnectAsync("ChickenClient closing");
        kvp.Value?.Dispose();
    }
    _connections.Clear();
}