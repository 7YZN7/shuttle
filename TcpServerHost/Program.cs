using System.Net;
using System.Net.Sockets;
using System.Text;

int port = Environment.GetEnvironmentVariable("PORT") is string p && int.TryParse(p, out var parsed) ? parsed : 8080;
var listener = new TcpListener(IPAddress.Any, port);
listener.Start();

Console.WriteLine("TCP Server Host");
Console.WriteLine($"Listening on 0.0.0.0:{port}");

// Print local IP addresses for convenience
var hostIPs = Dns.GetHostEntry(Dns.GetHostName()).AddressList
    .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
    .Select(a => a.ToString());
Console.WriteLine("Local IPs: " + string.Join(", ", hostIPs));

_ = Task.Run(async () =>
{
    while (true)
    {
        var client = await listener.AcceptTcpClientAsync();
        _ = HandleClientAsync(client);
    }
});

Console.WriteLine("Press Ctrl+C to stop.");
await Task.Delay(Timeout.InfiniteTimeSpan);

static async Task HandleClientAsync(TcpClient client)
{
    var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
    Console.WriteLine($"Client connected: {endpoint}");

    using var stream = client.GetStream();
    var buffer = new byte[4096];

    try
    {
        while (true)
        {
            int read = await stream.ReadAsync(buffer, 0, buffer.Length);
            if (read == 0)
            {
                Console.WriteLine($"Client disconnected: {endpoint}");
                break;
            }

            string message = Encoding.UTF8.GetString(buffer, 0, read);
            Console.WriteLine($"From {endpoint}: {message}");

            string response = "ECHO: " + message;
            byte[] responseBytes = Encoding.UTF8.GetBytes(response);
            await stream.WriteAsync(responseBytes, 0, responseBytes.Length);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Client error ({endpoint}): {ex.Message}");
    }
    finally
    {
        client.Close();
    }
}