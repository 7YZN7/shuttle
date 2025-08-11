using Microsoft.Maui.Graphics;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace MauiApp1
{
    public partial class MainPage : ContentPage
    {
        private TcpClient? _client;
        private NetworkStream? _stream;
        private CancellationTokenSource? _clientCts;
        private bool _isConnected = false;
        private readonly ObservableCollection<string> _messages = new();

        public MainPage()
        {
            InitializeComponent();

            // Set default values
            string localIP = GetLocalIPAddress();
            LocalIpLabel.Text = $"This device IP: {localIP}";
            ServerIpEntry.Text = "192.168.1."; // Common IP range
            ServerPortEntry.Text = "8080";

            // Bind messages to UI
            MessagesCollectionView.ItemsSource = _messages;

            AddMessage("📱 Client app ready to connect");
        }

        private async void OnConnectClicked(object sender, EventArgs e)
        {
            if (_isConnected)
            {
                await DisconnectFromServer();
                return;
            }

            string serverIP = ServerIpEntry.Text?.Trim();
            string portText = ServerPortEntry.Text?.Trim();

            if (string.IsNullOrWhiteSpace(serverIP) || string.IsNullOrWhiteSpace(portText) || !int.TryParse(portText, out int port))
            {
                await DisplayAlert("Error", "Please enter a valid server IP address and port.", "OK");
                return;
            }

            await ConnectToServer(serverIP, port);
        }

        private async void OnFindServerClicked(object sender, EventArgs e)
        {
            try
            {
                StatusLabel.Text = "Scanning local network...";
                StatusLabel.TextColor = Colors.Orange;

                if (!int.TryParse(ServerPortEntry.Text?.Trim(), out int port))
                {
                    await DisplayAlert("Error", "Please enter a valid port before scanning.", "OK");
                    return;
                }

                string localIp = GetLocalIPAddress();
                if (string.IsNullOrWhiteSpace(localIp) || !IPAddress.TryParse(localIp, out var ip))
                {
                    await DisplayAlert("Error", "Could not determine local IP.", "OK");
                    return;
                }

                string subnet = string.Join('.', localIp.Split('.').Take(3));
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                string? found = await FindServerOnSubnetAsync(subnet, port, cts.Token);

                if (found != null)
                {
                    ServerIpEntry.Text = found;
                    StatusLabel.Text = $"Found server at {found}:{port}";
                    StatusLabel.TextColor = Colors.Green;
                    await ConnectToServer(found, port);
                }
                else
                {
                    StatusLabel.Text = "No server found";
                    StatusLabel.TextColor = Colors.Red;
                    await DisplayAlert("Not Found", "No server was found on your local /24 subnet.", "OK");
                }
            }
            catch (Exception ex)
            {
                StatusLabel.Text = $"Scan error: {ex.Message}";
                StatusLabel.TextColor = Colors.Red;
            }
        }

        private async Task<string?> FindServerOnSubnetAsync(string subnetPrefix, int port, CancellationToken cancellationToken)
        {
            // Example: subnetPrefix = "192.168.1"
            var ipCandidates = Enumerable.Range(1, 254).Select(last => $"{subnetPrefix}.{last}").ToList();

            var throttler = new SemaphoreSlim(32);
            var tasks = new List<Task<(string ip, bool ok)>>();

            foreach (var candidate in ipCandidates)
            {
                await throttler.WaitAsync(cancellationToken);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        using var tcp = new TcpClient();
                        tcp.NoDelay = true;
                        var connectTask = tcp.ConnectAsync(IPAddress.Parse(candidate), port);
                        var completed = await Task.WhenAny(connectTask, Task.Delay(300, cancellationToken));
                        if (completed == connectTask && tcp.Connected)
                        {
                            return (candidate, true);
                        }
                    }
                    catch
                    {
                        // ignore
                    }
                    finally
                    {
                        throttler.Release();
                    }
                    return (candidate, false);
                }, cancellationToken));
            }

            while (tasks.Count > 0)
            {
                var finished = await Task.WhenAny(tasks);
                tasks.Remove(finished);
                var (ip, ok) = await finished;
                if (ok)
                {
                    // Cancel remaining tasks
                    try { (cancellationToken as CancellationTokenSource)?.Cancel(); } catch { }
                    return ip;
                }
            }

            return null;
        }

        private async Task ConnectToServer(string serverIP, int port)
        {
            try
            {
                StatusLabel.Text = "Connecting...";
                StatusLabel.TextColor = Colors.Orange;
                AddMessage($"🔄 Connecting to {serverIP}:{port}...");

                if (!IPAddress.TryParse(serverIP, out IPAddress? ipAddress))
                {
                    throw new ArgumentException("Invalid IP address format");
                }

                _client = new TcpClient();
                _client.NoDelay = true;
                _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                _clientCts = new CancellationTokenSource();

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, _clientCts.Token);

                await _client.ConnectAsync(ipAddress, port, linked.Token);

                _stream = _client.GetStream();
                _isConnected = true;

                StatusLabel.Text = $"Connected to {serverIP}:{port}";
                StatusLabel.TextColor = Colors.Green;

                ConnectButton.Text = "Disconnect";
                ConnectButton.BackgroundColor = Colors.Red;
                SendButton.IsEnabled = true;
                MessageEntry.IsEnabled = true;

                AddMessage($"✅ Connected to server {serverIP}:{port}");

                await SendMessage("Hello from MAUI client!");

                _ = Task.Run(async () => await ListenForMessages(_clientCts.Token));

                await DisplayAlert("Connected", $"Successfully connected to server at {serverIP}:{port}", "OK");
            }
            catch (ArgumentException ex)
            {
                StatusLabel.Text = "Invalid IP address format";
                StatusLabel.TextColor = Colors.Red;
                AddMessage($"❌ Invalid IP address: {ex.Message}");
                await DisplayAlert("Invalid IP", "Please enter a valid IP address format (e.g., 192.168.1.100)", "OK");
            }
            catch (OperationCanceledException)
            {
                StatusLabel.Text = "Connection timeout";
                StatusLabel.TextColor = Colors.Red;
                AddMessage("❌ Connection timeout - server may not be running");
                await DisplayAlert("Timeout", "Connection timed out. Please check:\n• Server app is running\n• IP address is correct\n• Both devices on same WiFi", "OK");
            }
            catch (SocketException ex)
            {
                string errorMsg = ex.SocketErrorCode switch
                {
                    SocketError.ConnectionRefused => "Connection refused - server not running or wrong port",
                    SocketError.TimedOut => "Connection timed out - check network connection",
                    SocketError.NetworkUnreachable => "Network unreachable - check WiFi connection",
                    SocketError.HostUnreachable => "Host unreachable - check IP address",
                    SocketError.HostNotFound => "Host not found - check IP address format",
                    _ => $"Network error: {ex.SocketErrorCode}"
                };

                StatusLabel.Text = errorMsg;
                StatusLabel.TextColor = Colors.Red;
                AddMessage($"❌ {errorMsg}");

                string troubleshoot = ex.SocketErrorCode switch
                {
                    SocketError.ConnectionRefused => "• Make sure server app is running\n• Check port number (usually 8080)\n• Restart server app",
                    SocketError.HostNotFound or SocketError.HostUnreachable => "• Check IP address format\n• Both devices on same WiFi?\n• Try pinging the IP first",
                    _ => "• Check network connection\n• Restart both apps\n• Try different port"
                };

                await DisplayAlert("Connection Error", $"{errorMsg}\n\nTroubleshooting:\n{troubleshoot}", "OK");
            }
            catch (Exception ex)
            {
                StatusLabel.Text = $"Error: {ex.Message}";
                StatusLabel.TextColor = Colors.Red;
                AddMessage($"❌ Connection error: {ex.Message}");
                await DisplayAlert("Error", $"Connection failed: {ex.Message}", "OK");
            }
        }

        private async Task DisconnectFromServer()
        {
            try
            {
                _isConnected = false;
                _clientCts?.Cancel();
                _stream?.Close();
                _client?.Close();

                StatusLabel.Text = "Disconnected";
                StatusLabel.TextColor = Colors.Orange;

                // Update UI
                ConnectButton.Text = "Connect";
                ConnectButton.BackgroundColor = Colors.Green;
                SendButton.IsEnabled = false;
                MessageEntry.IsEnabled = false;

                AddMessage("🔴 Disconnected from server");
            }
            catch (Exception ex)
            {
                AddMessage($"❌ Error disconnecting: {ex.Message}");
            }
        }

        private async void OnSendMessageClicked(object sender, EventArgs e)
        {
            string message = MessageEntry.Text?.Trim();
            if (string.IsNullOrWhiteSpace(message) || !_isConnected || _stream == null)
                return;

            await SendMessage(message);
            MessageEntry.Text = string.Empty;
        }

        private async Task SendMessage(string message)
        {
            try
            {
                if (_stream == null || !_isConnected) return;

                byte[] messageBytes = Encoding.UTF8.GetBytes(message);
                await _stream.WriteAsync(messageBytes, 0, messageBytes.Length);

                AddMessage($"📤 Sent: {message}");
            }
            catch (Exception ex)
            {
                AddMessage($"❌ Error sending message: {ex.Message}");
                await DisconnectFromServer();
            }
        }

        private async Task ListenForMessages(CancellationToken cancellationToken)
        {
            var buffer = new byte[1024];

            try
            {
                while (_isConnected && !cancellationToken.IsCancellationRequested && _stream != null)
                {
                    int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);

                    if (bytesRead == 0)
                    {
                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            AddMessage("📱 Server disconnected");
                        });
                        break;
                    }

                    string receivedMessage = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        AddMessage($"📨 Server: {receivedMessage}");
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation
            }
            catch (Exception ex)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    AddMessage($"❌ Error receiving messages: {ex.Message}");
                });
            }
            finally
            {
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    await DisconnectFromServer();
                });
            }
        }

        private void AddMessage(string message)
        {
            string timestampedMessage = $"[{DateTime.Now:HH:mm:ss}] {message}";
            _messages.Insert(0, timestampedMessage);

            // Keep only last 100 messages
            while (_messages.Count > 100)
            {
                _messages.RemoveAt(_messages.Count - 1);
            }
        }

        private string GetLocalIPAddress()
        {
            var ipAddresses = new List<string>();

            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus == OperationalStatus.Up &&
                    iface.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                {
                    var props = iface.GetIPProperties();
                    foreach (var addr in props.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !IPAddress.IsLoopback(addr.Address))
                        {
                            ipAddresses.Add(addr.Address.ToString());
                        }
                    }
                }
            }

            if (ipAddresses.Count > 0)
            {
                // Prefer 192.168.x.x addresses (most common for home WiFi)
                var preferredIP = ipAddresses.FirstOrDefault(ip => ip.StartsWith("192.168.")) ?? ipAddresses.First();

                // Log all found IPs for debugging
                AddMessage($"🔍 Found IP addresses: {string.Join(", ", ipAddresses)}");
                AddMessage($"🔍 Using: {preferredIP}");

                return preferredIP;
            }

            return "No network adapter found";
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            _ = DisconnectFromServer();
        }
    }
}