using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using System.Text.RegularExpressions;
using RSA_PM_Shared;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Configuration;

namespace RSA_PM_Client
{
    static class Program
    {
        public static string password;
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            //new ClientTest().Run(); return;
            if (false)
            {
                var aes = new System.Security.Cryptography.RijndaelManaged();
                for (int i = 0; i < 20; ++i)
                {
                    var f = true;
                    foreach (var v in aes.IV)
                    {
                        if (f) { Console.Write(@"new byte[16] { "); f = false; } else { Console.Write(", "); }
                        Console.Write(@"{0}", v);
                    }
                    Console.WriteLine("}");
                    aes.GenerateIV();
                }
            }
            DB db;
            DB.filename = ConfigurationSettings.AppSettings["dbfile"] ?? "RSA-PM.sqlite3";
            var fi = new FileInfo(DB.filename);
            if (fi.Exists && fi.Length > 0)
            {
                db = new DB();
                var d = db.LoadInfo();
                var hasPw = d.ContainsKey("pw");
                while (hasPw)
                {
                    password = InputBox("What is the password?", null, true);
                    if (password == null)
                        break;
                    try
                    {
                        db.Load(password);
                        break;
                    }
                    catch (System.Security.Cryptography.CryptographicException ex)
                    {
                        MessageBox.Show("Wrong Password");
                    }
                }
                var hasPem = d.ContainsKey("pem_data");
                while (!db.IsLoaded && hasPem)
                {
                    RSAParameters rsap = new RSAParameters();
                    var pemfn = d.ContainsKey("pempath") ? Encoding.UTF8.GetString(d["pempath"]) : "";
                    if (File.Exists(pemfn))
                    {
                        LoadPemFile(pemfn, out rsap);
                    }
                    else
                    {
                        int optdummy = 0;
                        if (PemConfig(out optdummy, out rsap, false) == null)
                            break;
                    }
                    try
                    {
                        if (db.Load(rsap, d["pem_data"]))
                            break;
                    }
                    catch (System.Security.Cryptography.CryptographicException ex)
                    {
                        if (ex.Message.StartsWith(@"Bad Data."))
                        {
                            MessageBox.Show("Wrong PEM file?");
                            continue;
                        }
                        return;
                    }
                    catch
                    {
                        return;
                    }
                }
                if (db.IsLoaded == false)
                    return;
            }
            else
            {
                db = new DB();
                if (startWizard(null, null, 0, null, 0, null, 10101, null, db) != 0) { MessageBox.Show("Configuration failed. Existing application"); return; }
            }
            Application.Run(new Form1(db));
        }
        class ClientTest
        {
            byte[] prv, pub;
            public void Run()
            {
                RSAParameters serv_pub;
                Shared.LoadKey(@"pub.txt", null, out serv_pub);
                var client = new Client("localhost", 12345, null, 0, serv_pub);

                prv = Utils.openssl_CreatePrvKey();
                pub = Utils.ExtractPublicKey(prv);
                client.RegisterPublicKey(prv, pub);
                var prv2 = prv;
                //prv = null; //for unsigned notes
                var msgid = client.SendMessage(pub, null, "some text to test with", prv, pub);
                client.SendMessage(pub, msgid, "sidohfaodshgadshfsidohfaodshgadshfsidohfaodshgadshfsidohfaodshgadshfsidohfaodshgadshfsidohfaodshgadshfsidohfaodshgadshf", prv, pub);
                prv = prv2;
                client.GetMessage(prv, pub, Store);
                client.UnregisterPublicKey(prv, pub);
            }

            void Store(RSA_PM_Client.Client.Message msg)
            {
                //encrypt with aes + store into db
            }
        }
        public static int startWizard(string pw, string pemfn, int pemOption, string sockproxy, int proxyport, string server_addr, int server_port, byte[] server_pubkey, DB db)
        {
            string orgpw = pw, temppw = pw;
            byte[] serverPub;
            System.Security.Cryptography.RSAParameters load_pem_rsa = new RSAParameters();
            bool useRsa;
            while (true)
            {
                var r = pw == null ? InputBox("RSA-PM",
    @"How would you like your protection?
1) Password 
2) PEM file (it may be password protected) 
3) Password and PEM (only one required to unlock).") : InputBox("RSA-PM",
    @"How would you like your protection?
0) Leave Password
1) New Password
2) PEM file (it may be password protected) 
3) Password and PEM (only one required to unlock).");
                int i;
                if (r == null)
                {
                    return -1;
                }
                var b = int.TryParse(r, out i);
                if (b == false)
                {
                    if (MessageBox.Show("Error that isn't a number. Please select one of the options") == DialogResult.OK) { continue; }
                    else { return -1; }
                }
                serverPub = null;
                switch (i)
                {
                    case 0:
                        temppw = pw;
                        if (pw == null)
                            goto default;
                        break;
                    case 1:
                        temppw = PasswordConfig(pw);
                        if (temppw == null)
                            continue;
                        break;
                    case 2:
                        temppw = null;
                        pemfn = PemConfig(out pemOption, out load_pem_rsa, true);
                        if (pemOption == -1)
                            continue;
                        break;
                    case 3:
                        temppw = PasswordConfig(pw);
                        if (temppw == null)
                            continue;
                        pemfn = PemConfig(out pemOption, out load_pem_rsa, true);
                        if (pemOption == -1)
                            continue;
                        break;
                    default:
                        if (MessageBox.Show("Invalid Option") == DialogResult.OK) { continue; }
                        else { return -1; }
                }
                var restart = false;
                while (true)
                {
                    var r2 = MessageBox.Show("Would you like to use a SOCK proxy? (Http and other proxies are not supported)", "", MessageBoxButtons.YesNoCancel);
                    if (r2 != DialogResult.Yes)
                    {
                        restart = r2 == DialogResult.Cancel;
                        sockproxy = null;
                        break;
                    }
                //i am lazy
                addrpart:
                    var addr = sockproxy;
                    if (InputBox("What is the address?", null, ref addr) != DialogResult.OK)
                        continue;
                    if (Regex.IsMatch(addr, @"^\d+$") || !Regex.IsMatch(addr, @"^(\w+\:)?[\w\-_]+\.[\w\-_\.]+$")) { MessageBox.Show("Invalid Address"); goto addrpart; }
                portpart:
                    string szport = proxyport.ToString();
                    if (InputBox("What is the port?", null, ref szport) != DialogResult.OK)
                        continue;
                    Int16 iport;
                    if (Int16.TryParse(szport, out iport) == false) { MessageBox.Show("Invalid Port"); goto portpart; }
                    sockproxy = addr;
                    proxyport = iport;
                    break;
                }
                if (restart)
                    continue;
                while (true)
                {
                    useRsa = false;
                    if (false)
                    {
                        MessageBox.Show("What is the server public key? (pem file)");
                        var dia = new OpenFileDialog() { Filter = "PEM files|*.pem" };
                        if (dia.ShowDialog() != DialogResult.OK)
                        {
                            restart = true;
                            break;
                        }
                        if (Shared.LoadKey(dia.FileName, null, out load_pem_rsa))
                        {
                            MessageBox.Show("This file has a private key. This is either incorrect or the server is not secure as it gave away its private key. Pick another pem file (or server)");
                            continue;
                        }
                        using (var f = File.OpenText(dia.FileName))
                        {
                            serverPub = Utils.ExtractPublicKey2(f.ReadToEnd());
                        }
                    }
                    else
                    {
                        string res = "";
                        if (server_pubkey != null)
                        {
                            res = Shared.pubToPem(server_pubkey);
                        }
                        if (InputBox("What is the server public key?", null, ref res) != DialogResult.OK)
                        {
                            restart = true;
                            break;
                        }
                        try
                        {
                            RSAParameters rsap;
                            //The lib requires line returns so lets ->byte->pem this
                            res = Shared.pubToPem(Utils.ExtractPublicKey2(res));
                            if (Shared.LoadKey2(res, null, out rsap))
                            {
                                MessageBox.Show("This is a private key. This is either incorrect or the server is not secure as it gave away its private key. Pick paste another public key or choose another server");
                                continue;
                            }
                            serverPub = Utils.ExtractPublicKey2(res);
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show("I don't like this public key");
                            continue;
                        }
                        useRsa = true;
                    }

                addrpart:
                    var addr = server_addr ?? "";
                    if (InputBox("What is the address?", null, ref addr) != DialogResult.OK)
                        continue;
                    if (!(
                        addr == "localhost" ||
                        Regex.IsMatch(addr, @"^(\d{1,3}\.){3}\d+$") ||
                        Regex.IsMatch(addr, @"^[\w\-_]+\.[\w\-_\.]+$"))
                        ) { MessageBox.Show("Invalid Address"); goto addrpart; }
                portpart:
                    string szport = server_port.ToString();
                    if (InputBox("What is the port?", null, ref szport) != DialogResult.OK)
                        continue;
                    Int16 iport;
                    if (Int16.TryParse(szport, out iport) == false) { MessageBox.Show("Invalid Port"); goto portpart; }
                    server_addr = addr;
                    server_port = iport;
                    break;
                }
                if (restart)
                    continue;
                break;
            }
            db.Setup(temppw, pemfn, pemOption, sockproxy, proxyport, server_addr, server_port, serverPub, load_pem_rsa, useRsa);
            return 0;
        }

        static string PasswordConfig(string pw)
        {
            while (true)
            {
                if (string.IsNullOrEmpty(pw) == false)
                {
                    var p0 = InputBox("Please enter your *current* password", null, true);
                    if (p0 == null)
                        return null;
                    if (p0 != pw)
                    {
                        MessageBox.Show("Password does not match your current password");
                        continue;
                    }
                }
                var p1 = InputBox("Please enter your password", null, true);
                if (p1 == null)
                    return null;
                var p2 = InputBox("Confirm your password", null, true);
                if (p2 == null)
                    continue;
                if (p1 != p2)
                {
                    if (MessageBox.Show("Passwords do not match") == DialogResult.OK)
                        continue;
                    else
                        return null;
                }
                else
                    return p1;
            }
        }

        static string PemConfig(out int opt, out RSAParameters load_pem_rsa, bool isConfig)
        {
            load_pem_rsa = new RSAParameters();
            if (isConfig)
            {
                var rmsg = MessageBox.Show("Would you like to automatically load this PEM file?", "", MessageBoxButtons.YesNoCancel);
                if (rmsg == DialogResult.Cancel) { opt = -1; return null; }
                opt = rmsg == DialogResult.Yes ? 1 : 0;
            }
            else
                opt = -1;
            var dia = new OpenFileDialog() { Filter = "PEM files|*.pem" };

            while (true)
            {
                if (dia.ShowDialog() != DialogResult.OK) { opt = -1; return null; }

                if (LoadPemFile(dia.FileName, out load_pem_rsa) == false)
                    continue;
                break;
            }
            return dia.FileName;
        }
        public static bool LoadPemFile(string fn, out RSAParameters load_pem_rsa)
        {
            load_pem_rsa = new RSAParameters();
            bool tryWithPass = false;
            while (true)
            {
                PasswordFinder pempass = null;
                if (tryWithPass)
                {
                    var szpempass = InputBox("What is the PEM Passphrase", null, true);
                    if (szpempass == null)
                    {
                        return false;
                    }
                    pempass = new PasswordFinder(szpempass.ToCharArray());
                }
                try
                {
                    if (Shared.LoadKey(fn, pempass, out load_pem_rsa) == false)
                    {
                        MessageBox.Show("This does not have a private key. Select a different file");
                        return false;
                    }
                    break;
                }
                catch (CryptographicException ex)
                {
                    MessageBox.Show("I don't like this pem file. Try a different one");
                    return false;
                }
                catch (Org.BouncyCastle.Security.PasswordException ex)
                {
                    tryWithPass = true;
                    continue;
                }
                catch (Org.BouncyCastle.Crypto.InvalidCipherTextException ex)
                {
                    //if (ex.Message.IndexOf(@"how to load this as a key") != -1)
                    {
                        MessageBox.Show("Incorrect Passphrase");
                        continue;
                    }
                    throw;
                }
            }
            return true;
        }

        //Someone elses code but I rather not write 'StaticClass.' everytime
        public static string InputBox(string title, string promptText, bool isPassword = false)
        {
            promptText = promptText ?? title;
            string sz = "";
            if (InputBox(title, promptText, ref sz, isPassword) != DialogResult.OK)
                return null;
            else
                return sz;
        }
        public static DialogResult InputBox(string title, string promptText, ref string value, bool isPassword = false)
        {
            Form form = new Form();
            Label label = new Label();
            TextBox textBox = new TextBox();
            Button buttonOk = new Button();
            Button buttonCancel = new Button();

            form.Text = title;
            label.Text = promptText ?? title;
            textBox.Text = value;

            buttonOk.Text = "OK";
            buttonCancel.Text = "Cancel";
            buttonOk.DialogResult = DialogResult.OK;
            buttonCancel.DialogResult = DialogResult.Cancel;

            label.SetBounds(9, 20, 372, 13);
            textBox.SetBounds(12, 90, 372, 20);
            buttonOk.SetBounds(228, 122, 75, 23);
            buttonCancel.SetBounds(309, 122, 75, 23);

            label.AutoSize = true;
            textBox.Anchor = textBox.Anchor | AnchorStyles.Right;
            buttonOk.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            buttonCancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;

            if (isPassword)
                textBox.UseSystemPasswordChar = true;

            form.ClientSize = new Size(396, 157);
            form.Controls.AddRange(new Control[] { label, textBox, buttonOk, buttonCancel });
            form.ClientSize = new Size(Math.Max(300, label.Right + 10), form.ClientSize.Height);
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.StartPosition = FormStartPosition.CenterScreen;
            form.MinimizeBox = false;
            form.MaximizeBox = false;
            form.AcceptButton = buttonOk;
            form.CancelButton = buttonCancel;

            DialogResult dialogResult = form.ShowDialog();
            value = textBox.Text;
            return dialogResult;
        }
    }
}
