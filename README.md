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
- **Messages are signed end to end.** The sender signs each message (id, from, to, kind, body,
  time). The router checks the signature before routing, and the recipient checks it again against the
  sender's key. A compromised router could drop or delay messages, but not forge or alter them.
- **The router's own identity.** The router makes a self-signed TLS certificate on first start and
  keeps it in its data volume. Clients pin its SHA-256, so it needs no public CA or domain name, and a
  man in the middle is refused.
- **Message bodies are not encrypted end to end**, only in transit (TLS). The router can read them.

## Run the router (Wojtek)

```sh
docker compose up -d --build
docker logs poniexp-router            # prints the certificate SHA-256 to pin
docker exec poniexp-router poniexp-router admin certificate
```

It listens on port 5443. Machines away from home need that port reachable, through a router port
forward or the VPN.

## Join a machine

```sh
dotnet run --project src/PoniExp.Cli -- init https://<router-host>:5443 --pin <sha256>
dotnet run --project src/PoniExp.Cli -- join ise --notes "MacBook Pro"
#   asked to join as 'ise'. On the router, check this join code and approve:
#       3f2a-91c0-...
```

On the router:

```sh
docker exec poniexp-router poniexp-router admin list
docker exec poniexp-router poniexp-router admin approve 3f2a91c0     # id prefix or name
docker exec poniexp-router poniexp-router admin reject <id|name>
docker exec poniexp-router poniexp-router admin remove <id|name>     # forget it; it may ask again
```

Approval reaches the waiting `join` at once.

## Use it

```sh
poniexp status                          # membership, and who's online
poniexp send wojtek chat "hello"        # "delivered" or "queued" if wojtek is offline
echo '{"task":"sync"}' | poniexp send wojtek task -
poniexp listen                          # stay connected, print messages (--json for scripts)
```

From code, use `PoniExp.Client.RelayClient`:

```csharp
using var identity = MachineIdentity.LoadOrCreate(ClientConfig.KeyPath);
await using var client = new RelayClient(identity, ClientConfig.Load()!.ToOptions());
client.MessageReceived += async m => Console.WriteLine($"{m.FromName}: {m.Envelope.Body}");
await client.ConnectAsync();
await client.SendAsync("wojtek", "chat", "hi");
```

## Delivery

- A message to an online machine is pushed at once. One to an offline machine waits on the router
  (up to 7 days) and is delivered when it connects.
- Messages stay queued until the recipient **acknowledges** them, so a crash mid-delivery means a
  redelivery, not a loss. Recipients drop duplicates by message id.
- Clients reconnect forever, and re-prove their key on each reconnect.

## Layout

| Path | What |
|---|---|
| `src/PoniExp.Core` | Keys, signing, protocol records. No dependencies. |
| `src/PoniExp.Router` | ASP.NET Core SignalR hub, machine list, queue, admin API/CLI, container image. |
| `src/PoniExp.Client` | `RelayClient`: connect, prove the key, join, send, receive. |
| `src/PoniExp.Cli` | `poniexp` command line. |
| `tests/PoniExp.Tests` | Signing tests, and end-to-end tests against a real router. |

```sh
dotnet test PoniExp.slnx
```
