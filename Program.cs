using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ChickenClient.Models;
using ChickenClient.Services;
using Microsoft.Toolkit.Uwp.Notifications;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

var configManager = new ConfigManager();
configManager.Load();

// --- Windows API Native Imports for Focus and Sounds ---
[DllImport("kernel32.dll")]
static extern IntPtr GetConsoleWindow();

[DllImport("user32.dll")]
static extern IntPtr GetForegroundWindow();

[DllImport("user32.dll")]
static extern bool MessageBeep(uint uType);

[DllImport("kernel32.dll", SetLastError = true)]
static extern IntPtr GetStdHandle(int nStdHandle);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

// Enable 24-bit True Color Virtual Terminal processing for Windows Console
const int STD_OUTPUT_HANDLE = -11;
const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
var iStdOut = GetStdHandle(STD_OUTPUT_HANDLE);
if (GetConsoleMode(iStdOut, out uint outMode))
{
    SetConsoleMode(iStdOut, outMode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
}

// State
Dictionary<string, IrcClient?> _connections = new(StringComparer.OrdinalIgnoreCase);
string? _activeServer = null;
string? _activeChannel = null;
Dictionary<string, ChannelInfo> _channelMessages = new(StringComparer.OrdinalIgnoreCase);
bool _running = true;

// UI Sync Lock and Input Buffer
string _currentInput = "";
var _consoleLock = new object();
using var _httpClient = new HttpClient();

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
        
        // Scan our own sent messages for custom image previews too
        SafeWriteLine(echo);
        _ = CheckAndAttachImageAsync(_activeChannel, input);
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
        MessageBeep(0x00000040); 
        new ToastContentBuilder()
            .AddText(title)
            .AddText(body)
            .SetToastScenario(ToastScenario.Default)
            .Show();
    }
    catch (Exception) { }
}

// --- Image Attachment & Download Processing Methods ---

async Task CheckAndAttachImageAsync(string targetChannel, string rawMessage)
{
    string urlRegexPattern = @"\bhttps?://\S+\.(?:png|jpg|jpeg|gif)\b";
    var match = Regex.Match(rawMessage, urlRegexPattern, RegexOptions.IgnoreCase);
    if (!match.Success) return;

    string imageUrl = match.Value;

    try
    {
        // Download into RAM stream
        var bytes = await _httpClient.GetByteArrayAsync(imageUrl);
        using var image = Image.Load<Rgb24>(bytes);

        // Dynamic Size Changer
        int maxConsoleWidth = Math.Min(120, Console.WindowWidth - 8);
        if (maxConsoleWidth < 30) maxConsoleWidth = 30; // Safety floor for tiny windows

        if (image.Width > maxConsoleWidth)
        {
            int newHeight = (int)((double)image.Height * maxConsoleWidth / image.Width);
            image.Mutate(x => x.Resize(maxConsoleWidth, newHeight));
        }

        // Convert pixels to ANSI strings
        var asciiLines = ConvertImageToAnsiBlocks(image);

        lock (_consoleLock)
        {
            if (!_channelMessages.TryGetValue(targetChannel, out var info)) return;

            // Save inside persistent buffer array so it scrolls along beautifully with history
            info.Messages.Add("   [Image Attachment]");
            foreach (var line in asciiLines)
            {
                info.Messages.Add(line);
            }

            // If user is currently inside this view context, draw the blocks live
            if (_activeChannel != null && _activeChannel.Equals(targetChannel, StringComparison.OrdinalIgnoreCase))
            {
                ClearAndRedrawActiveBuffer();
            }
        }
    }
    catch (Exception)
    {
        // Fail down safely if link was dead or format bad
    }
}

List<string> ConvertImageToAnsiBlocks(Image<Rgb24> image)
{
    var lines = new List<string>();
    // We sample 2 rows of pixels at a time using '▄' character to preserve true square aspect ratio blocks
    for (int y = 0; y < image.Height; y += 2)
    {
        var rowBuilder = new System.Text.StringBuilder("   "); // Left indent formatting padding
        for (int x = 0; x < image.Width; x++)
        {
            var topPixel = image[x, y];
            // If image has an odd pixel height count, fall back to black background on trailing block row
            var bottomPixel = (y + 1 < image.Height) ? image[x, y + 1] : new Rgb24(0, 0, 0);

            // TrueColor ANSI escape format: \x1b[48;2;R;G;Bm for background, \x1b[38;2;R;G;Bm for foreground
            rowBuilder.Append($"\x1b[48;2;{topPixel.R};{topPixel.G};{topPixel.B}m\x1b[38;2;{bottomPixel.R};{bottomPixel.G};{bottomPixel.B}m▄");
        }
        rowBuilder.Append("\x1b[0m"); // Reset styling wrap-up
        lines.Add(rowBuilder.ToString());
    }
    return lines;
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

        PrintWithCyanImageHighlighting(text);
        Console.WriteLine();
        Console.Write(BuildPrompt() + _currentInput);
    }
}

void PrintWithCyanImageHighlighting(string text)
{
    string urlRegexPattern = @"\bhttps?://\S+\b";
    int lastPos = 0;

    foreach (Match match in Regex.Matches(text, urlRegexPattern, RegexOptions.IgnoreCase))
    {
        Console.Write(text.Substring(lastPos, match.Index - lastPos));
        var url = match.Value;

        if (url.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            url.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            url.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            url.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Blue;
        }

        Console.Write(url);
        Console.ResetColor();
        lastPos = match.Index + match.Length;
    }
    Console.Write(text.Substring(lastPos));
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
                // Print using highlighting function to make sure history links stay colored correctly
                PrintWithCyanImageHighlighting(message);
                Console.WriteLine();
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
                    _ = CheckAndAttachImageAsync(target, message);
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
        if (ch == '"' && !inQuote) inQuote = true;
        else if (ch == '"' && inQuote) inQuote = false;
        else if (ch == ' ' && !inQuote)
        {
            if (current.Length > 0)
            {
                result.Add(current.ToString());
                current.Clear();
            }
        }
        else current.Append(ch);
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
        foreach (var s in configManager.Servers)
        {
            var status = _connections.ContainsKey(s.Name) && _connections[s.Name]?.Connected == true ? "connected" : "disconnected";
            SafeWriteLine($"  {s.Name} -> {s.Host}:{s.Port} [{status}]");
        }
        return;
    }

    var subCmd = args[0].ToLower();
    if (subCmd == "add" && args.Count >= 3)
    {
        var newServer = new ServerConfig { Name = args[1], Host = args[2] };
        if (args.Count > 3 && int.TryParse(args[3], out var p)) newServer.Port = p;
        configManager.AddServer(newServer);
        SafeWriteLine($"Server '{newServer.Name}' added.");
    }
}

async Task HandleBouncerCommand(List<string> args) { }

async Task HandleConnectCommand(List<string> args)
{
    if (args.Count == 0) return;
    var serverName = args[0];
    var config = configManager.GetServer(serverName);
    if (config == null) return;

    var client = new IrcClient(config);

    client.OnMessageReceived += msg =>
    {
        var isWindowFocused = IsConsoleWindowFocused();
        var channelMatch = Regex.Match(msg, @"^\[(?<channel>[#&][^\]]+)\]");
        var pmMatch = Regex.Match(msg, @"^\[(?<target>[^#&\]\s]+)\]\s+<(?<sender>[^>]+)>\s+(?<message>.+)$");

        if (channelMatch.Success)
        {
            var ch = channelMatch.Groups["channel"].Value;
            if (!_channelMessages.ContainsKey(ch)) _channelMessages[ch] = new ChannelInfo();
            _channelMessages[ch].Messages.Add(msg);

            bool isOurChannel = (_activeServer == serverName && _activeChannel == ch);
            if (msg.Contains(client.CurrentNick, StringComparison.OrdinalIgnoreCase) && (!isWindowFocused || !isOurChannel))
            {
                TriggerWindowsNotification($"ChickenClient - {ch}", msg);
            }

            if (isOurChannel) SafeWriteLine(msg);
            
            // Asynchronously pull and hook visual attachments
            _ = CheckAndAttachImageAsync(ch, msg);
        }
        else if (pmMatch.Success)
        {
            var sender = pmMatch.Groups["sender"].Value;
            var target = pmMatch.Groups["target"].Value;
            var payload = pmMatch.Groups["message"].Value;
            string convKey = target.Equals(client.CurrentNick, StringComparison.OrdinalIgnoreCase) ? sender : target;

            bool wasAlreadyViewing = (_activeServer == serverName && _activeChannel == convKey);
            if (!_channelMessages.ContainsKey(convKey)) _channelMessages[convKey] = new ChannelInfo { Name = convKey };

            _channelMessages[convKey].Messages.Add(msg);

            if (!isWindowFocused || !wasAlreadyViewing)
            {
                TriggerWindowsNotification($"ChickenClient - {convKey}", $"{sender} > {payload}");
            }

            _activeServer = serverName;
            _activeChannel = convKey;

            ClearAndRedrawActiveBuffer();
            _ = CheckAndAttachImageAsync(convKey, payload);
        }
    };

    await client.ConnectAsync();
    _connections[serverName] = client;
    _activeServer = serverName;
}

async Task HandleDisconnectCommand(List<string> args) { }

async Task HandleJoinCommand(List<string> args)
{
    if (args.Count == 0 || _activeServer == null) return;
    var channel = args[0];
    if (!channel.StartsWith('#')) channel = "#" + channel;
    await _connections[_activeServer]!.JoinChannelAsync(channel);
    if (!_channelMessages.ContainsKey(channel)) _channelMessages[channel] = new ChannelInfo();
    _activeChannel = channel;
    ClearAndRedrawActiveBuffer();
}

async Task HandlePartCommand(List<string> args) { }

void HandleChannelCommand(List<string> args)
{
    if (args.Count > 0)
    {
        _activeChannel = args[0];
        ClearAndRedrawActiveBuffer();
    }
}

void HandleSwitchCommand(List<string> args) { }
async Task HandleNickCommand(List<string> args) { }
async Task DisconnectAllAsync() { }