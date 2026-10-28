using System.Net;
using System.Net.Sockets;
using System.Text;

// A stand-in for a model API, for testing without a key: answers POST /v1/messages with a streamed reply in
// Anthropic's server-sent-event format, sent with chunked transfer encoding like the real APIs. The first event
// also reports how many request bytes arrived ("received_bytes"). ?slow=1 streams 20 pieces a second apart
// (for cancel tests). Plain sockets, so no admin rights or URL reservations are needed.
// Usage: LlmMock [port] [--exit-after-seconds N]
int port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 8765;
int exitAfter = Array.IndexOf(args, "--exit-after-seconds") is int i && i >= 0 && i + 1 < args.Length ? int.Parse(args[i + 1]) : 0;
if (exitAfter > 0) _ = Task.Delay(TimeSpan.FromSeconds(exitAfter)).ContinueWith(_ => Environment.Exit(0));

var listener = new TcpListener(IPAddress.Loopback, port);
listener.Start();
Console.WriteLine($"LlmMock listening on http://127.0.0.1:{port}");
while (true)
{
    var client = await listener.AcceptTcpClientAsync();
    _ = Task.Run(() => Serve(client));
}

static async Task Serve(TcpClient client)
{
    using var _ = client;
    try
    {
        var s = client.GetStream();
        var (requestLine, headers) = await ReadHead(s);
        int length = headers.TryGetValue("content-length", out var cl) ? int.Parse(cl) : 0;
        var buf = new byte[81920];
        long received = 0;
        while (received < length)
        {
            int n = await s.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, length - received)));
            if (n == 0) break;
            received += n;
        }
        Console.WriteLine($"{requestLine}  ({received} bytes)");
        bool slow = requestLine.Contains("slow=1");
        if (!requestLine.StartsWith("POST") || !requestLine.Contains("/v1/messages"))
        {
            await Write(s, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            return;
        }
        await Write(s, "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n");
        async Task Event(string type, string json)
        {
            var payload = Encoding.UTF8.GetBytes($"event: {type}\ndata: {json}\n\n");
            await Write(s, payload.Length.ToString("x") + "\r\n");
            await s.WriteAsync(payload);
            await Write(s, "\r\n");
            await s.FlushAsync();
        }
        await Event("message_start", $"{{\"type\":\"message_start\",\"received_bytes\":{received},\"message\":{{\"id\":\"msg_mock\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"mock\",\"content\":[],\"usage\":{{\"input_tokens\":1500,\"output_tokens\":1}}}}}}");
        await Event("content_block_start", "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}");
        var words = slow ? Enumerable.Range(1, 20).Select(n => $"piece{n} ").ToArray()
                         : "The electrolyzer makes 888 g/s of oxygen from 1 kg/s of water. ".Split(' ').Select(w => w + " ").ToArray();
        foreach (var w in words)
        {
            await Task.Delay(slow ? 1000 : 60);
            await Event("content_block_delta", $"{{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"text_delta\",\"text\":\"{w}\"}}}}");
        }
        await Event("content_block_stop", "{\"type\":\"content_block_stop\",\"index\":0}");
        await Event("message_delta", "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":20}}");
        await Event("message_stop", "{\"type\":\"message_stop\"}");
        await Write(s, "0\r\n\r\n");
    }
    catch (Exception e) { Console.WriteLine("connection ended: " + e.Message); }
}

static async Task<(string, Dictionary<string, string>)> ReadHead(NetworkStream s)
{
    var bytes = new List<byte>();
    var one = new byte[1];
    while (!(bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n'))
    {
        if (await s.ReadAsync(one) == 0) break;
        bytes.Add(one[0]);
    }
    var lines = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
    var headers = lines.Skip(1).Select(l => l.Split(':', 2)).Where(kv => kv.Length == 2)
        .ToDictionary(kv => kv[0].Trim().ToLowerInvariant(), kv => kv[1].Trim());
    return (lines.FirstOrDefault() ?? "", headers);
}

static Task Write(NetworkStream s, string text) => s.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask();
