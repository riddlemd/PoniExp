# PoniExp

A secure relay for agents on several machines. Every machine keeps one outbound connection to a
router (SignalR over TLS), so the router always knows who's online, and no machine ever has to reach
another directly: laptops behind VPNs, hotel Wi-Fi or NAT just connect out.

```
 ise ──────┐                      ┌───── blinky
           │  wss:// (TLS, pinned) │
           ▼                      ▼
        router (Linux container on wojtek)
          · knows each machine by its public key
          · approved / pending / rejected list
          · routes signed messages, queues them for machines that are offline
          · tells everyone who comes and goes
```

## How trust works

- **A machine is its key.** Each machine makes an ECDSA P-256 key pair on first use
  (`~/.poniexp/machine.key`, owner-only). The private key never leaves it. The machine id is a hash
  of the public key.
- **Joining.** A machine sends its public key, name and description (OS, architecture, host name,
  notes), signed with that key. The router lists it as **pending** and shows a **join code**, which is
  the start of the machine id. The joining machine prints the same code. You compare the two, then
  approve.
- **Connecting.** On every connect the router sends a random challenge, and the machine signs it.
  Only a connection that proves an approved key can send, receive, or see the machine list.
- **Messages are signed end to end.** The sender signs every field of each message. The router checks
  the signature before routing, and the recipient checks it again against the sender's key. The
  recipient also judges expiry by its own clock and drops duplicates, so a compromised router could
  drop or delay messages, but not forge, alter, replay or stretch them.
- **The router's own identity.** The router makes a self-signed TLS certificate on first start and
  keeps it in its data volume. Clients pin its SHA-256, so it needs no public CA or domain name, and a
  man in the middle is refused.
- **Message bodies are not encrypted end to end**, only in transit (TLS). The router can read them.

## Run the router (Wojtek)

```sh
docker compose up -d --build
docker logs poniexp-server            # prints the certificate SHA-256 to pin
docker exec poniexp-server poniexp admin certificate
```

It listens on port 5443. Machines away from home need that port reachable, through a router port
forward or the VPN.

## Join a machine

Build the client with `dotnet publish src/PoniExp.Cli -c Release -o <dir>` (puts `poniexp-client` in `<dir>`).

```sh
poniexp-client init https://<router-host>:5443 --pin <sha256>
poniexp-client join ise --notes "MacBook Pro"
#   asked to join as 'ise'. On the router, check this join code and approve:
#       3f2a-91c0-...
```

On the router:

```sh
docker exec poniexp-server poniexp admin list
docker exec poniexp-server poniexp admin approve 3f2a91c0     # id prefix or name
docker exec poniexp-server poniexp admin reject <id|name>
docker exec poniexp-server poniexp admin remove <id|name>     # forget it; it may ask again
```

Approval reaches the waiting `join` at once.

## Use it

Agents are addressed as `machine/endpoint` (a bare `machine` means its `default` endpoint).

```sh
poniexp-client status                                  # membership, who's online, message metrics
poniexp-client send --to wojtek/agent "hello"          # "pushed", or "queued" if wojtek is offline
poniexp-client send --to wojtek --to blinky "hi both"  # one separate message per recipient
poniexp-client send --to wojtek --subject sync --expires 2h --meta priority=high "sync please"
echo '{"task":"sync"}' | poniexp-client send --to wojtek/agent --content-type application/json -
poniexp-client send --to ise/cli --reply-to <id> --conversation <id> --status done "finished"
poniexp-client listen                                  # stay connected, print messages (--json for scripts)
```

Settings go in `~/.poniexp/settings.json` (or `PONIEXP_Messaging__*` environment variables):

```json
{ "Messaging": { "DefaultExpiry": "7.00:00:00", "MaxExpiry": "7.00:00:00", "ClockSkew": "00:05:00" } }
```

From code, use `PoniExp.Client.RelayClient`:

```csharp
using var identity = MachineIdentity.LoadOrCreate(ClientConfig.KeyPath);
var settings = new ConfigurationBuilder().AddJsonFile(ClientConfig.SettingsPath, optional: true, reloadOnChange: true).Build();
await using var client = new RelayClient(identity, ClientConfig.Load()!.ToOptions(),
    new ClientStore(ClientConfig.DatabasePath), MessagingSettings.Monitor(settings));
client.MessageReceived += async m => Console.WriteLine($"{m.From}: {m.Content.Body}");
await client.ConnectAsync();
await client.SendAsync("wojtek/agent", new OutgoingMessage("hi") { Subject = "greeting" });
```

## Delivery

- A message to an online machine is pushed at once. One to an offline machine waits on the router and
  is delivered when it connects, until it expires (7 days at most by default).
- The recipient stores each message in its client database (`~/.poniexp/client.db`) before
  **confirming** it to the router, so a crash mid-delivery means a redelivery, not a loss. A message
  already stored is recognised and dropped.
- Each sender numbers its messages per recipient machine; gaps (messages that never arrived) show up
  in `poniexp-client status`.
- Clients reconnect forever, and re-prove their key on each reconnect.

## Layout

| Path | What |
|---|---|
| `src/PoniExp.Core` | Keys, signing, protocol records. No dependencies. |
| `src/PoniExp.Server` | `poniexp`: the server executable. `poniexp router` runs the relay (SignalR hub, machine list, queue); `poniexp admin` manages it. Container image. |
| `src/PoniExp.Client` | `RelayClient`: connect, prove the key, join, send, receive. `ClientStore`: the client database. |
| `src/PoniExp.Cli` | `poniexp-client`: the command line each machine uses. |
| `tests/PoniExp.Tests` | Signing tests, and end-to-end tests against a real router. |

```sh
dotnet test PoniExp.slnx
```
