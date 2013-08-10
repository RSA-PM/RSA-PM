using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Configuration;
using System.Net;
using System.Net.Sockets;
using System.Collections.Specialized;
using System.Threading;
using System.Security.Cryptography;
using System.IO;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.OpenSsl;
using MySql.Data.MySqlClient;
using Dapper;
#pragma warning disable 0618, 0649
using RSA_PM_Shared;
using System.Text.RegularExpressions;
using System.IO.Compression;

namespace RSA_PM_Server
{
    class Program
    {
        public static ManualResetEvent tcpClientConnected = new ManualResetEvent(false);
        public static RSAParameters key { get { return key1; } }
        static RSAParameters key1;
        static void Main(string[] args)
        {
            var conn = new MySqlConnection(ConfigurationSettings.AppSettings["mysql_conn_str"]);
            conn.Open();
            try
            {
                using (var tns = conn.BeginTransaction())
                {
                    {
                        var sql = @"CREATE TABLE `PubList` (
    `id` BIGINT PRIMARY KEY AUTO_INCREMENT,
    `pubkey` VARBINARY(512) NOT NULL UNIQUE,
    `last_seen` DATETIME NOT NULL,
    INDEX pubkey_ind (pubkey),
    INDEX date_ind (last_seen)
) engine = InnoDB;
CREATE TABLE `Msg` (
    `id` BIGINT PRIMARY KEY AUTO_INCREMENT,
    `recipient_id` BIGINT NOT NULL,
    `msg` VARBINARY(8192) NOT NULL,
    INDEX reci_ind (recipient_id),
    FOREIGN KEY (recipient_id) REFERENCES PubList(id)
) engine = InnoDB;";
                        conn.Execute(sql, new { });
                        tns.Commit();
                    }
                }
            }
            catch (Exception ex)
            {
            }

            //openssl genrsa -out server.pem 1024
            var pemfn = ConfigurationSettings.AppSettings["pem_prv_key"];
            var pem_pw = ConfigurationSettings.AppSettings["pem_prv_key_pw"];
            if (Shared.LoadKey(pemfn, new PasswordFinder(pem_pw == null ? null : pem_pw.ToCharArray()), out key1) == false)
                throw new Exception("PEM file must have private key");

            var listen_ip_addr = IPAddress.Parse(ConfigurationSettings.AppSettings["ip_addr"]);
            var port = Int16.Parse(ConfigurationSettings.AppSettings["port"]);
            var backlog = int.Parse(ConfigurationSettings.AppSettings["backlog"]);
            var server = new TcpListener(listen_ip_addr, port);
            server.Start(backlog);
            while (true)
            {
                DoBeginAcceptTcpClient(server);
            }
        }
        public static void DoBeginAcceptTcpClient(TcpListener listener)
        {
            tcpClientConnected.Reset();
            listener.BeginAcceptTcpClient(new AsyncCallback(DoAcceptTcpClientCallback), listener);
            tcpClientConnected.WaitOne();
        }
        public static void DoAcceptTcpClientCallback(IAsyncResult ar)
        {
            TcpListener listener = (TcpListener)ar.AsyncState;
            TcpClient client = listener.EndAcceptTcpClient(ar);
            tcpClientConnected.Set();
            new ClientConnection(client).Run();
        }
        
    }
    class PubList
    {
        public long id;
        public byte[] pubkey;
        public DateTime last_seen;
    }
    class Messages
    {
        public long id, pubkey_id;
        public byte[] msg;
    }
    class ClientConnection
    {
        TcpClient c;
        Random rng = new Random();
        NetworkStream network;
        BinaryReader sr;
        BinaryWriter sw;
        MySqlConnection conn2;
        MySqlConnection conn
        {
            get
            {
                if (conn2 == null)
                {
                    conn2 = new MySqlConnection(ConfigurationSettings.AppSettings["mysql_conn_str"]);
                    conn2.Open();
                } return conn2;
            }
        }

        public ClientConnection(TcpClient client) { c = client; }
        public void Run()
        {
            network = c.GetStream();
            sr = new BinaryReader(network);
            sw = new BinaryWriter(network);
            try
            {
                Shared.AnswerRemotePubkeyTest(sr,sw, Program.key);
                while (true)
                {
                    switch ((ClientServerCmd)sr.ReadByte())
                    {
                        case ClientServerCmd.RegisterPubkey:
                            RegisterPublicKey();
                            break;
                        case ClientServerCmd.UnregisterPubkey:
                            UnregisterPublicKey();
                            break;
                        case ClientServerCmd.SendMessage:
                            SendMessage();
                            break;
                        case ClientServerCmd.GetMessage:
                            GetMessage();
                            break;
                        default:
                            throw new Exception();
                    }
                }
            }
            catch (Exception ex)
            {
            }
            finally
            {
                c.Close();
            }
        }

        byte[] RecvKey()
        {
            var len = sr.ReadShort();
            if (len >= 512)
            {
                sw.WriteByte((byte)ClientServerCmd.LengthToBig);
                throw new Exception();
            }
            var buf = new byte[len];
            var readlen = sr.Read(buf, 0, len);
            if (readlen != len) throw new Exception();

            sw.WriteByte((byte)ClientServerCmd.KeyLenOk);
            return buf;
        }

        void RegisterPublicKey()
        {
            var pubkey = RecvKey();

            RSAParameters pubp;
            Shared.LoadKey2(Shared.pubToPem(pubkey), null, out pubp);
            if (Shared.TestRemoteOnPubkey(sr, sw, pubp) == false)
                throw new Exception();

            var res = conn.Execute(@"insert ignore into PubList(pubkey, last_seen) select @k, UTC_TIMESTAMP()", new { k = pubkey });
            sw.WriteByte(res == 1 ? (byte)ClientServerCmd.RegisterPubkeyOk : (byte)ClientServerCmd.RegisterPubkeyAlreadyReg);
            sw.Flush();
        }
        void UnregisterPublicKey()
        {
            var pubkey = RecvKey();

            RSAParameters pubp;
            Shared.LoadKey2(Shared.pubToPem(pubkey), null, out pubp);
            if (Shared.TestRemoteOnPubkey(sr, sw, pubp) == false)
                throw new Exception();

            var id = conn.Query<long>("select id from PubList where pubkey=@k", new { k = pubkey });
            var res = conn.Execute(@"delete from Msg where recipient_id=@id; delete from PubList where id=@id", new { id=id });
            sw.WriteByte((byte)ClientServerCmd.Success);
            sw.Flush();
        }
        void SendMessage()
        {
            var recipient_pubkey = RecvKey();

            var len = sr.ReadShort();
            if (len > 1024 * 7)
            {
                sw.WriteByte((byte)ClientServerCmd.LengthToBig);
                throw new Exception();
            }
            var msg = new byte[len];
            var readlen = sr.Read(msg, 0, len);
            if (readlen != len) throw new Exception();

            var i = conn.Execute(@"insert into Msg(recipient_id, msg) select id, @msg from PubList where pubkey=@pubkey", new { pubkey = recipient_pubkey, msg = msg });
            if (i != 1)
            {
                sw.WriteByte((byte)ClientServerCmd.NotRegistered);
            }

            sw.WriteByte((byte)ClientServerCmd.Success);
        }
        void GetMessage()
        {
            var pubkey = RecvKey();

            RSAParameters pubp;
            Shared.LoadKey2(Shared.pubToPem(pubkey), null, out pubp);
            if (Shared.TestRemoteOnPubkey(sr, sw, pubp) == false)
                throw new Exception();

            var id = conn.Query<long>("select id from PubList where pubkey=@k", new { k = pubkey });
            conn.Execute("UPDATE PubList set last_seen=UTC_TIMESTAMP() where id=@id", new { id = id });
            while (true)
            {
                var res = conn.Query<Messages>(@"select id, msg from Msg where recipient_id=@id limit 1", new { id = id });
                if (res.Count()==0)
                {
                    sw.WriteByte((byte)ClientServerCmd.NoMessage);
                    return;
                }
                sw.WriteByte((byte)ClientServerCmd.HasMessage);
                var r = res.First();
                sw.WriteShort(r.msg.Length);
                //sw.Write((long)r.date.ToFileTimeUtc());
                sw.Write(r.msg, 0, r.msg.Length);
                sw.Flush();
                var clientReply = sr.ReadByte();
                if (clientReply != (byte)ClientServerCmd.Success)
                {
                    throw new Exception();
                }
                conn.Execute(@"delete from Msg where id=@id", new { r.id });
            }
        }
    };
}

