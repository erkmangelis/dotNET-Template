using System.Net;
using System.Net.Sockets;

namespace MyProject.Api.Infrastructure.Http;

/// <summary>
/// For URLs other people choose, such as a webhook's address: connect only to public internet
/// addresses. The check runs when the connection opens, on the addresses DNS returned for that
/// connection, so a name that resolves (or is switched to resolve) to 127.0.0.1, the cloud metadata
/// service at 169.254.169.254 or a private network is refused, not just a URL that spells one out.
/// </summary>
internal static class PublicNetwork
{
    private static readonly IPNetwork[] _reserved =
    [
        // IPv4: "this network", private, shared (carrier NAT), loopback, link-local (cloud metadata),
        // IETF protocol assignments, documentation, benchmarking, multicast and reserved.
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.0.0.0/24"),
        IPNetwork.Parse("192.0.2.0/24"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("198.18.0.0/15"),
        IPNetwork.Parse("198.51.100.0/24"),
        IPNetwork.Parse("203.0.113.0/24"),
        IPNetwork.Parse("224.0.0.0/4"),
        IPNetwork.Parse("240.0.0.0/4"),
        // IPv6: unspecified and loopback, NAT64 (which reaches IPv4 addresses), unique local,
        // link-local, documentation and multicast.
        IPNetwork.Parse("::/127"),
        IPNetwork.Parse("64:ff9b::/96"),
        IPNetwork.Parse("fc00::/7"),
        IPNetwork.Parse("fe80::/10"),
        IPNetwork.Parse("2001:db8::/32"),
        IPNetwork.Parse("ff00::/8"),
    ];

    /// <summary>True for an address on the public internet.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return !_reserved.Any(network => network.Contains(address));
    }

    /// <summary>
    /// A <see cref="SocketsHttpHandler.ConnectCallback"/> that resolves the host itself and connects
    /// only to its public addresses. Refuses with <see cref="OutboundRequestRefusedException"/> when it has none.
    /// </summary>
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken);
        var allowed = addresses.Where(IsPublic).ToArray();
        if (allowed.Length == 0)
        {
            throw new OutboundRequestRefusedException($"{host} has no public address; private and reserved networks are refused.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
