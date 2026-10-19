using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace FudanNetLite
{
    // Pin individual sockets, rather than changing the machine's routing table.
    // Proxy=null alone cannot bypass a TUN adapter's IP routes.
    public sealed class CampusRoute
    {
        public int InterfaceIndex { get; private set; }
        public IPAddress Address { get; private set; }
        public string Mac { get; private set; }
        internal string Key { get { return InterfaceIndex + ":" + Address; } }
        private CampusRoute(int index, IPAddress address, string mac)
        { InterfaceIndex = index; Address = address; Mac = mac; }
        internal void Bind(Socket socket, int port)
        {
            // Windows IP_UNICAST_IF (31) takes the interface index in network order.
            socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder(InterfaceIndex));
            socket.Bind(new IPEndPoint(Address, port));
        }
        internal static List<CampusRoute> Candidates(string connection)
        {
            var result = new List<CampusRoute>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType != (connection == "wifi" ? NetworkInterfaceType.Wireless80211 : NetworkInterfaceType.Ethernet)) continue;
                var mac = nic.GetPhysicalAddress().GetAddressBytes();
                var properties = nic.GetIPProperties();
                if (mac.Length != 6 || properties.GetIPv4Properties() == null) continue;
                bool gateway = false;
                foreach (var entry in properties.GatewayAddresses)
                    if (entry.Address.AddressFamily == AddressFamily.InterNetwork && !entry.Address.Equals(IPAddress.Any)) gateway = true;
                if (!gateway) continue;
                foreach (var entry in properties.UnicastAddresses)
                {
                    byte[] bytes = entry.Address.GetAddressBytes();
                    // The campus access networks on this installation use RFC1918 10/8.
                    // Excludes fake-IP TUNs and host-only Hyper-V/WSL adapters.
                    if (bytes.Length != 4 || bytes[0] != 10) continue;
                    result.Add(new CampusRoute(properties.GetIPv4Properties().Index, entry.Address,
                        BitConverter.ToString(mac).Replace('-', ':').ToLowerInvariant()));
                }
            }
            return result;
        }
    }
}
