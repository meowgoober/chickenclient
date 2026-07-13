using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ChickenClient.Models;

namespace ChickenClient.Services;

public class IrcClient : IDisposable
{
    private TcpClient? _tcpClient;
    private Stream? _stream;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _cts;
    private Task? _readTask;

    public ServerConfig Config { get; private set; }
    public bool Connected => _tcpClient?.Connected ?? false;
    public string CurrentNick => Config.Nick;

    public event Action<string>? OnMessageReceived;
    public event Action<string>? OnRawReceived;
    public event Action<string>? OnError;
    public event Action? OnDisconnected;

    public IrcClient(ServerConfig config)
    {
        Config = config;
    }

    public async Task ConnectAsync()
    {
        try
        {
            _cts = new CancellationTokenSource();
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(Config.Host, Config.Port);

            Stream baseStream = _tcpClient.GetStream();

            if (Config.UseSsl)
            {
                var sslStream = new SslStream(baseStream, false, ValidateCertificate);
                await sslStream.AuthenticateAsClientAsync(Config.Host);
                _stream = sslStream;
            }
            else
            {
                _stream = baseStream;
            }

            _reader = new StreamReader(_stream, Encoding.UTF8);
            _writer = new StreamWriter(_stream, Encoding.UTF8) { NewLine = "\r\n", AutoFlush = true };

            // Send initial credentials
            await SendBouncerAuthAsync();

            // Start reading in background
            _readTask = Task.Run(() => ReadLoopAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Connection failed: {ex.Message}");
        }
    }

    private async Task SendBouncerAuthAsync()
    {
        if (Config.IsBouncer)
        {
            var nick = Config.Nick;
            var bouncerPass = Config.Password ?? "";
            var pass = bouncerPass;

            if (!string.IsNullOrEmpty(Config.BouncerUsername))
            {
                if (!string.IsNullOrEmpty(Config.NetworkName))
                    pass = $"{Config.BouncerUsername}/{Config.NetworkName}:{bouncerPass}";
                else
                    pass = $"{Config.BouncerUsername}:{bouncerPass}";
            }

            if (!string.IsNullOrEmpty(pass))
                await SendRawAsync($"PASS {pass}");

            await SendRawAsync($"NICK {nick}");
            await SendRawAsync($"USER {nick} 0 * :{nick}");
        }
        else
        {
            if (!string.IsNullOrEmpty(Config.Password))
                await SendRawAsync($"PASS {Config.Password}");

            await SendRawAsync($"NICK {Config.Nick}");
            await SendRawAsync($"USER {Config.Nick} 0 * :{Config.Nick}");
        }
    }

    public async Task SendMessageAsync(string target, string message)
    {
        if (!Connected) return;
        foreach (var line in message.Split('\n'))
        {
            var trimmed = line.Trim('\r');
            if (trimmed.Length > 0)
                await SendRawAsync($"PRIVMSG {target} :{trimmed}");
        }
    }

    public async Task JoinChannelAsync(string channel)
    {
        if (!channel.StartsWith('#')) channel = "#" + channel;
        await SendRawAsync($"JOIN {channel}");
    }

    public async Task PartChannelAsync(string channel)
    {
        await SendRawAsync($"PART {channel}");
    }

    public async Task ChangeNickAsync(string newNick)
    {
        await SendRawAsync($"NICK {newNick}");
        Config.Nick = newNick;
    }

    public async Task SendRawAsync(string raw)
    {
        if (_writer == null) return;
        try
        {
            await _writer.WriteLineAsync(raw);
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Send error: {ex.Message}");
        }
    }

    public async Task DisconnectAsync(string? quitMessage = null)
    {
        try
        {
            if (Connected)
            {
                await SendRawAsync($"QUIT :{quitMessage ?? "Leaving"}");
            }
            _cts?.Cancel();
            _stream?.Close();
            _tcpClient?.Close();
        }
        catch { }
        finally
        {
            OnDisconnected?.Invoke();
        }
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && _reader != null)
            {
                var line = await _reader.ReadLineAsync(token);
                if (line == null) break;

                OnRawReceived?.Invoke(line);
                ProcessLine(line);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            OnError?.Invoke($"Read error: {ex.Message}");
        }
        finally
        {
            await DisconnectAsync();
        }
    }

    private void ProcessLine(string line)
    {
        if (line.StartsWith("PING "))
        {
            var respondTo = line.Substring(5);
            _ = SendRawAsync($"PONG :{respondTo}");
            return;
        }

        // Parse NOTICE (Used to intercept ZNC password alerts)
        var noticeMatch = System.Text.RegularExpressions.Regex.Match(line,
            @"^:(?<sender>\S+)\s+NOTICE\s+(?<target>\S+)\s+:(?<message>.+)$");
        if (noticeMatch.Success)
        {
            var sender = noticeMatch.Groups["sender"].Value;
            var message = noticeMatch.Groups["message"].Value;

            OnMessageReceived?.Invoke($":{sender} NOTICE {message}");

            // If the bouncer states a password error or request notice, automatically blast authentication payloads back out.
            if (Config.IsBouncer && (message.Contains("Password required", StringComparison.OrdinalIgnoreCase) || 
                                     message.Contains("You need to send your password", StringComparison.OrdinalIgnoreCase)))
            {
                _ = SendBouncerAuthAsync();
            }
            return;
        }

        // Parse PRIVMSG
        var msgMatch = System.Text.RegularExpressions.Regex.Match(line,
            @"^:(?<nick>[^!]+)!(?<user>[^@]+)@(?<host>\S+)\s+PRIVMSG\s+(?<target>\S+)\s+:(?<message>.+)$");
        if (msgMatch.Success)
        {
            var nick = msgMatch.Groups["nick"].Value;
            var target = msgMatch.Groups["target"].Value;
            var message = msgMatch.Groups["message"].Value;
            OnMessageReceived?.Invoke($"[{target}] <{nick}> {message}");
            return;
        }

        // Parse JOIN
        var joinMatch = System.Text.RegularExpressions.Regex.Match(line,
            @"^:(?<nick>[^!]+)!(?<user>[^@]+)@(?<host>\S+)\s+JOIN\s+:(?<channel>\S+)$");
        if (joinMatch.Success)
        {
            var nick = joinMatch.Groups["nick"].Value;
            var channel = joinMatch.Groups["channel"].Value;
            OnMessageReceived?.Invoke($"[{channel}] * {nick} has joined {channel}");
            return;
        }

        // Parse PART
        var partMatch = System.Text.RegularExpressions.Regex.Match(line,
            @"^:(?<nick>[^!]+)!(?<user>[^@]+)@(?<host>\S+)\s+PART\s+(?<channel>\S+)");
        if (partMatch.Success)
        {
            var nick = partMatch.Groups["nick"].Value;
            var channel = partMatch.Groups["channel"].Value;
            OnMessageReceived?.Invoke($"[{channel}] * {nick} has left {channel}");
            return;
        }

        // Parse QUIT
        var quitMatch = System.Text.RegularExpressions.Regex.Match(line,
            @"^:(?<nick>[^!]+)!(?<user>[^@]+)@(?<host>\S+)\s+QUIT\s+:(?<message>.+)$");
        if (quitMatch.Success)
        {
            var nick = quitMatch.Groups["nick"].Value;
            var msg = quitMatch.Groups["message"].Value;
            OnMessageReceived?.Invoke($"* {nick} has quit ({msg})");
            return;
        }

        // Parse NICK change
        var nickMatch = System.Text.RegularExpressions.Regex.Match(line,
            @"^:(?<oldnick>[^!]+)!(?<user>[^@]+)@(?<host>\S+)\s+NICK\s+:(?<newnick>\S+)$");
        if (nickMatch.Success)
        {
            var oldNick = nickMatch.Groups["oldnick"].Value;
            var newNick = nickMatch.Groups["newnick"].Value;
            OnMessageReceived?.Invoke($"* {oldNick} is now known as {newNick}");
            return;
        }

        // Parse numeric replies (like 353 for NAMES list, 372 for MOTD, etc.)
        var numericMatch = System.Text.RegularExpressions.Regex.Match(line,
            @"^:(?<server>\S+)\s+(?<code>\d{3})\s+(?<target>\S+)\s+(?<message>.+)$");
        if (numericMatch.Success)
        {
            var code = numericMatch.Groups["code"].Value;
            var message = numericMatch.Groups["message"].Value;

            if (code == "464" && Config.IsBouncer) // Password Required numeric
            {
                _ = SendBouncerAuthAsync();
            }

            if (code == "353")
            {
                var namesPart = message;
                var namesMatch = System.Text.RegularExpressions.Regex.Match(namesPart,
                    @"[=@*]\s+(?<channel>\S+)\s+:(?<names>.+)$");
                if (namesMatch.Success)
                {
                    var channel = namesMatch.Groups["channel"].Value;
                    var names = namesMatch.Groups["names"].Value;
                    OnMessageReceived?.Invoke($"[{channel}] Users: {names}");
                }
                return;
            }

            if (code == "372" || code == "375" || code == "376")
            {
                OnMessageReceived?.Invoke($"- {message}");
                return;
            }

            OnMessageReceived?.Invoke($"[{code}] {message}");
            return;
        }

        OnMessageReceived?.Invoke($"> {line}");
    }

    private static bool ValidateCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
    {
        return true;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _stream?.Dispose();
        _tcpClient?.Dispose();
        _reader?.Dispose();
        _writer?.Dispose();
        _cts?.Dispose();
    }
}