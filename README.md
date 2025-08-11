# File Shuttle - Quick Start

- Run the server on your PC:

```bash
dotnet run --project TcpServerHost
```

It listens on port 8080 and prints your local IPs. Keep your firewall open for TCP 8080 or allow the app.

- On the phone (MAUI app):
  - Ensure the phone and PC are on the same Wi‑Fi.
  - Enter the PC IP and port 8080, then tap "Connect", or tap "Find Server on WiFi" to auto-discover.

Troubleshooting:
- If connection fails, check Windows Firewall or antivirus; allow inbound on TCP 8080.
- Make sure you are not using the PC's VPN which changes routing.
- Verify both devices share the same subnet (e.g., 192.168.1.x).