# Local Port Utility Privacy Policy

_Last updated: October 8, 2026_

### Overview

Local Port Utility is a Windows desktop utility for creating, inspecting, testing, and removing local Windows Firewall rules.

### Data collected by this application

Local Port Utility has no accounts, advertising, analytics, cloud backup, AI service, or developer-operated telemetry system. The app does not send firewall rules, settings, or general usage information to the developer.

The optional public port check contacts third-party services only after you start it. The app first requests your public IPv4 address from ipify's IPv4 endpoint, `https://api4.ipify.org`. The ipify server necessarily receives the source IP address of that request and returns the public address it observed. ipify states on its [API page](https://www.ipify.org/) that it does not log visitor information; that statement is made by ipify and is outside the app's control.

The app then sends the observed public IP address, selected port, and protocol-specific TCP and UDP check requests to [Check-Host.net](https://check-host.net/). Check-Host also receives the source IP address and ordinary request metadata from the network connection. Its API documentation says a check response includes a request ID and a permanent report link. This app polls for the result but does not display or save that report link locally. The app does not control Check-Host's retention, access, or publication practices; review [its API documentation](https://check-host.net/about/api?lang=en) before using the public check if this matters to you. The check result is not sent to the developer by Local Port Utility.

If your connection uses carrier-grade NAT (CGNAT), a VPN, or a proxy, the observed public IP address may be shared or belong to that network gateway rather than identify your individual computer. A public port result therefore does not necessarily describe inbound access to this device.

The optional Bore tunnel starts the Bore client included with the application only when you press **Start tunnel**. It connects to the Bore server you enter (by default, the community-operated `bore.pub` relay) and forwards TCP traffic between that server and the local IP address and port you selected. The relay operator can receive the forwarded traffic and connection metadata, and anyone who learns the public endpoint may connect to the forwarded service. Bore does not encrypt forwarded application traffic itself; use an application protocol that provides encryption, such as HTTPS or SSH. For a self-hosted relay, the server operator's privacy practices apply. See the [Bore documentation](https://github.com/ekzhang/bore#authentication) for its tunnel and authentication behavior.

### Local processing and storage

The application processes the following information locally on the user's device:

- Firewall rule names, ports, ranges, and protocols entered by the user.
- IP addresses entered for connectivity tests or discovered on the local network.
- Application settings such as theme and transparency preferences.
- History of firewall rules opened through the application.

Settings and firewall history are stored locally in the current Windows user's Local Application Data folder. They are not uploaded by Local Port Utility.

Temporary rule cleanup uses a Windows Task Scheduler task and a PowerShell script stored in a protected per-user folder under ProgramData. The task is scheduled for the selected expiry time and configured to start when available, so it may run late if the computer is off, asleep, or otherwise unavailable. It removes the app-created firewall rules and removes their history entry only after confirming that all of those rules are gone. If firewall cleanup fails, the history entry remains so the user can find and remove the rules manually.

### Windows and Microsoft platform services

The application is built with WinUI 3 and the Windows App SDK. Windows, Microsoft Windows components, or system services may process diagnostic, crash, security, or usage information according to the user's Windows settings and Microsoft's applicable privacy terms.

Those platform-level processes are outside Local Port Utility's control. Local Port Utility does not add its own telemetry or send data to Microsoft or any other service.

## PowerShell and network access

The application uses built-in Windows PowerShell cmdlets locally for firewall management and inspection. Firewall changes require administrator approval through the normal Windows User Account Control prompt.

When the user starts a local connectivity test, Windows checks the address and port selected by the user; that local test does not send the destination to a remote service. The optional public port check contacts `api4.ipify.org` only to obtain the observed public IPv4 address; the selected port is not sent to ipify. The app then contacts `check-host.net` with that address and port to request separate TCP and UDP probes. As with any internet request, each service can see the network address making the request. Check-Host's API returns a permanent report link for each check request; the app does not save or show that link. The test result is displayed locally and is not uploaded by Local Port Utility to the developer. If the connection uses CGNAT, a VPN, or a proxy, the observed public address may be shared or belong to a gateway rather than this computer. UDP services may not respond to a generic probe, so no response does not prove that a UDP port is closed.

When a Bore tunnel is started, its bundled client connects to the configured relay and forwards the selected local TCP service through it. The relay sees the tunnel connection and forwarded application traffic. Bore's tunnel does not provide encryption for that application traffic, so the service should use its own encryption. A custom relay is controlled by its operator; using the default public relay means trusting its operator with the forwarded traffic.

The application may inspect local network interfaces and nearby LAN addresses to populate its device list. This information remains local to the device.

### User controls

Users can remove firewall history entries from the application, delete the application's local settings and history files, and disable optional transparency effects in Settings. Removing an application history entry does not remove unrelated firewall rules.

### Contact

For privacy questions, contact the application publisher through the project repository or distribution channel where Local Port Utility was obtained.
