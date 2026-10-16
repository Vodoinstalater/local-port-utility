# Local Port Utility

**Local Port Utility** is a user-friendly GUI frontend for Windows PowerShell cmdlets designed to simplify opening local firewall ports and testing port connectivity.

Managing the default Windows Defender Firewall interface can be really annoying, especially when you need to open multiple ports at once, and on top of it, you need to do double the work to open both TCP and UDP.

<img width="834" height="600" alt="Screenshot 2026-10-08 165459" src="https://github.com/user-attachments/assets/43d2742b-dfdc-48c4-9584-c2b1e8f26c33" />

## Features

| Tool | What it does |
| --- | --- |
| **Open ports** | Create inbound Windows Firewall allow rules for one port or a range. Add multiple entries and choose TCP, UDP, or both for each one. |
| **Temporary rules** | Set an auto-delete time. Windows Task Scheduler removes the app-created firewall rules even when the app is closed, then removes the matching history entry after successful cleanup. |
| **Connectivity** | Test a service at an IPv4 address and port using a TCP connection test. The app can also list IPv4 devices it discovers on the local network. |
| **Firewall inspection** | Check whether an enabled inbound allow rule covers the exact port for TCP or UDP. This inspects firewall configuration; it does not prove that an application is listening or that traffic can reach the computer. |
| **Public port check** | Ask an external service to probe your public IPv4 address and selected port separately over TCP and UDP. Results help diagnose reachability from outside your network. |
| **Bore tunnel** | Forward a local TCP service through a Bore relay. Choose the local address and port, select a Bore server, and copy the public endpoint when the tunnel starts. |
| **History** | Review rules created by the app, see their status and scheduled expiry, and close the rules together. |
| **Settings** | Choose a light, dark, or system theme and turn off transparency effects if desired. |

## Understand the connectivity results

The app has three different checks because each answers a different question:

- **Test service** makes a TCP connection from this computer to the IPv4 address and port you entered. A successful result means a TCP service responded to the connection attempt from this network location.
- **Inspect port** looks for enabled inbound Windows Firewall allow rules for that exact port. A matching firewall rule does not mean an application is listening, and a listening application does not guarantee that the firewall or router permits outside connections.
- **Scan public port** asks Check-Host to probe the public IPv4 address reported by ipify. A successful TCP probe means an external probe connected. UDP has no TCP-style connection handshake, so no reply is inconclusive: the app may report **OPEN OR FILTERED**. A UDP application-specific response or an ICMP “port unreachable” response gives more information.

The public check sends the selected port and the observed public IP address to Check-Host. Both ipify and Check-Host can see the network address making the requests. If your connection uses CGNAT, a VPN, or a proxy, the observed public address may be shared or belong to a gateway. A failed public check does not by itself identify whether the cause is Windows Firewall, router forwarding, the ISP, CGNAT, or the service not listening.

## Bore tunnel notes

<img width="841" height="600" alt="Screenshot 2026-10-08 165522" src="https://github.com/user-attachments/assets/d58f8eb7-33d1-4a1a-849b-cfb57ab28a4a" />

Bore forwards **TCP only**. The local service must already be running and listening on the address and port you enter. Set remote port to `0` to let the relay assign one; the app displays the endpoint and provides a copy button when it is ready. Closing the app stops the tunnel.

The default `bore.pub` relay is operated by a third party. Anyone who gets the public endpoint may connect to the forwarded service, and Bore does not encrypt the service's application traffic. Only expose a service you trust, and use that service's own encryption (for example, HTTPS or SSH) when appropriate. You can configure another Bore server if you operate or trust one.

## Auto-delete behavior

Auto-delete is scheduled through Windows Task Scheduler, so it can run after Local Port Utility has been closed. The task is configured to start when available; if the computer is off or asleep at the scheduled time, cleanup may happen later. The history entry is removed only after Windows confirms that all firewall rules created for that record are gone. If cleanup fails, the history entry is kept so you can inspect and close the rules manually.

## Download

Open [Releases](../../releases) and download the archive for the version you want to try. If you download a portable ZIP, extract it and run the application from the extracted folder. Keep the included `Tools` folder beside the application; it contains the Bore client used by the tunnel feature.

Firewall changes, firewall inspection, and automatic cleanup require administrator approval through Windows. The app uses the normal Windows elevation prompt for these operations.

## Build from source

The project uses **C#**, **WinUI 3**, and **.NET 8**. To build it with Visual Studio:

1. Install Visual Studio with the .NET desktop development and Windows App SDK/WinUI components.
2. Open `Local Port Utility.slnx` and allow NuGet packages to restore.
3. Select the `Release` configuration and a target platform (`x86`, `x64`, or `ARM64`), then build the project.

The Bore client binaries are included under `Tools`. Their source version, architecture, and release-archive checksums are documented in [`Tools/README.md`](Tools/README.md). The project uses the x86 Bore client for x86 and ARM64 builds and the x64 client for x64 builds.

## Privacy and licenses

Public port checks use [ipify](https://www.ipify.org/) to obtain the observed public IPv4 address and [Check-Host.net](https://check-host.net/) to run the external TCP and UDP probes. Bore connects to the relay configured in the tunnel page. Review the [privacy policy](privacy-policy.md) for details about these services, local data, and scheduled cleanup.

Local Port Utility is licensed under the GNU General Public License v3.0; see [`LICENSE`](LICENSE). The bundled Bore client has its own MIT license at [`Tools/Bore-LICENSE.txt`](Tools/Bore-LICENSE.txt).
