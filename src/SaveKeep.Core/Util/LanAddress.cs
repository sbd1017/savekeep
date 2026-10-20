using System.Net;
using System.Net.Sockets;

namespace SaveKeep.Core.Util;

public static class LanAddress
{
    /// <summary>
    /// Loopback, RFC 1918, IPv4 link-local, IPv6 unique-local and link-local. Carrier-grade NAT
    /// (100.64.0.0/10) is deliberately not counted as local.
    /// </summary>
    public static bool IsLocal(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80);
        }
        return false;
    }
}
