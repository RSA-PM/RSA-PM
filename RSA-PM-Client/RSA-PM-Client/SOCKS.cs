using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RSA_PM_Client
{
    class SOCKS
    {
        public static NetworkStream ConnectSocksProxy(string proxyDomain, short proxyPort, string host, short hostPort, TcpClient tc)
        {
            tc.Connect(proxyDomain, proxyPort);
            if (System.Text.RegularExpressions.Regex.IsMatch(host, @"[\:/\\]"))
                throw new Exception("Invalid Host name. Use FQDN such as www.google.com. Do not have http, a port or / in it");
            NetworkStream ns = tc.GetStream();
            var HostNameBuf = new ASCIIEncoding().GetBytes(host);
            var HostPortBuf = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(hostPort));
            if (true) //5
            {
                var bufout = new byte[128];
                var buflen = 0;
                ns.Write(new byte[] { 5, 1, 0 }, 0, 3);
                buflen = ns.Read(bufout, 0, bufout.Length);
                if (buflen != 2 || bufout[0] != 5 || bufout[1] != 0)
                    throw new Exception();

                var buf = new byte[] { 5, 1, 0, 3, (byte)HostNameBuf.Length };
                var mem = new MemoryStream();
                mem.Write(buf, 0, buf.Length);
                mem.Write(HostNameBuf, 0, HostNameBuf.Length);
                mem.Write(new byte[] { HostPortBuf[0], HostPortBuf[1] }, 0, 2);
                var memarr = mem.ToArray();
                ns.Write(memarr, 0, memarr.Length);
                buflen = ns.Read(bufout, 0, bufout.Length);
                if (bufout[0] != 5 || bufout[1] != 0)
                    throw new Exception();
            }
            else //4a
            {
                var bufout = new byte[128];
                var buflen = 0;
                var mem = new MemoryStream();
                mem.WriteByte(4);
                mem.WriteByte(1);
                mem.Write(HostPortBuf, 0, 2);
                mem.Write(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(1)), 0, 4);
                mem.WriteByte(0);
                mem.Write(HostNameBuf, 0, HostNameBuf.Length);
                mem.WriteByte(0);
                var memarr = mem.ToArray();
                ns.Write(memarr, 0, memarr.Length);
                buflen = ns.Read(bufout, 0, bufout.Length);
                if (buflen != 8 || bufout[0] != 0 || bufout[1] != 90)
                    throw new Exception();
            }
            return ns;
        }
    }
}
