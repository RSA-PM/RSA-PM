using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using RSA_PM_Shared;

namespace RSA_PM_Client
{
    partial class Form1 : Form
    {
        byte[] replyTo = null;
        Public_Keys lookingAtPubkey = null;
        DB db;
        string proxy_addr, server_addr;
        Int16 proxy_port, server_port;
        byte[] server_pubkey;
        OutboxMsg[] outbox;
        Client.Message[] inbox;
        Public_Keys[] pubs;
        System.Security.Cryptography.RSAParameters server_pubkeyB;
        public Form1(DB db)
        {
            this.db = db;
            InitializeComponent();
        }

        void LoadSettings()
        {
            var d = db.GetSettings();
            proxy_addr = d["proxy_addr"];
            proxy_port = Int16.Parse(d["proxy_port"]);
            server_addr = d["server_addr"];
            server_port = Int16.Parse(d["server_port"]);
            server_pubkey = Convert.FromBase64String(d["server_pubkey"]);
            Shared.LoadKey2(Shared.pubToPem(server_pubkey), null, out server_pubkeyB);
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            comboBox1.DropDownStyle = ComboBoxStyle.DropDownList;
            listBox1.ContextMenuStrip = contextMenuStrip1;
            LoadSettings();
            outbox = db.GetOutbox();
            pubs = db.GetPubKeys();
            LoadIdent();
            listBox1.SelectedIndex = 0;
            button2.Visible = false;
            textBox12.Visible = false;
            textBox4.ReadOnly = textBox5.ReadOnly = textBox6.ReadOnly = textBox7.ReadOnly = textBox8.ReadOnly = textBox9.ReadOnly = textBox10.ReadOnly = textBox11.ReadOnly = textBox12.ReadOnly = true;
        }
        void LoadIdent()
        {
            listBox1.Items.Clear();
            listBox1.Items.Add("No User (Send Unsigned Messages)");
            var prvkeys = db.GetPrivateKeys();
            foreach (var v in prvkeys)
            {
                listBox1.Items.Add(v);
            }
        }
        int listbox1_MouseOverIndex = -1;
        private void contextMenuStrip1_Opening(object sender, CancelEventArgs e)
        {
            var indx = listBox1.IndexFromPoint(listBox1.PointToClient(Cursor.Position));
            if (indx == -1)
                e.Cancel = true;
            listbox1_MouseOverIndex = indx;
        }

        private void dToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            var obj = listBox1.Items[listbox1_MouseOverIndex];
            if (!(obj is Private_Keys))
            {
                MessageBox.Show("There is no public key. It is an option to send messages without signing it (thus no keys required)");
                return;
            }
            var k = (Private_Keys)obj;
            var pubpem = Utils.ExtractPublicKeyAsPem(k.key);
            try
            {
                Clipboard.SetText(pubpem);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error copying to clipboard?");
            }
        }

        private void createNewIdenityToolStripMenuItem_Click(object sender, EventArgs e)
        {
        redoKeysize:
            string keysize = "1024";
            if (Program.InputBox("How many bits? (512, 1024, 2048)", null, ref keysize) != System.Windows.Forms.DialogResult.OK)
            {
                return;
            }
            keysize = keysize.Trim();
            switch (keysize)
            {
                case "512":
                case "1024":
                case "2048":
                case "4096":
                    break; //ok
                default:
                    MessageBox.Show("Bad keysize");
                    goto redoKeysize;
            }
            var prvkey = Utils.openssl_CreatePrvKey(keysize);
            var name = Program.InputBox(@"What should the ""name"" be? (You may change this at anytime)", null);
            if (name == null) { return; }
            try
            {
                db.newprv(prvkey, name, MakeClient());
            }
            catch (Exception ex)
            {
                MessageBox.Show("An error occured. Perhaps the server is offline, your details are invalid or you selected the wrong server public key");
                return;
            }
            LoadIdent();
        }

        void ClearTab1()
        {
            listBox2.Items.Clear();
            textBox4.Text = textBox5.Text = textBox6.Text = "";
        }
        private void listBox1_SelectedIndexChanged(object sender, EventArgs e)
        {
            cancelReplyTo(); //very important
            if (listBox1.SelectedItem == null)
            {
                //ClearTab1();
                return;
            }
            tabControl1.SelectedTab = listBox1.SelectedIndex == 0 ? tabPage3 : tabPage1;
            if (listBox1.SelectedItem is string)
            {
                LoadOutbox(0);
                LoadPubkeys(0);
                return;
            }
            var k = (Private_Keys)listBox1.SelectedItem;
            //tab1
            LoadInbox(k.id);
            //tab2
            LoadOutbox(k.id);
            //tab3
            LoadPubkeys(k.id);
        }

        long loadPub_prvkey = 0;
        void LoadPubkeys(long prvkey, bool reloadPub = true)
        {
            if (reloadPub)
            {
                pubs = db.GetPubKeys();
                outbox = db.GetOutbox();
            }
            comboBox1.Items.Clear();
            var ids = new HashSet<long>();
            foreach (var v in outbox)
            {
                if (v.myprv == prvkey)
                {
                    ids.Add(v.theirpub);
                }
            }
            foreach (var v in pubs)
            {
                if (ids.Contains(v.id))
                    comboBox1.Items.Add(v);
            }
            loadPub_prvkey = prvkey;
        }
        void LoadInbox(long prvkey, bool reloadPub = true)
        {
            listBox3.Items.Clear();
            if (reloadPub)
            {
                inbox = db.GetInbox();
            }
            foreach (var v in inbox)
            {
                if (v.MyPrvId == prvkey)
                {
                    listBox3.Items.Add(v);
                }
            }
        }
        void LoadOutbox(long prvkey, bool reloadPub = true)
        {
            listBox2.Items.Clear();
            if (reloadPub)
            {
                outbox = db.GetOutbox();
            }
            foreach (var v in outbox)
            {
                if (v.myprv == prvkey)
                {
                    listBox2.Items.Add(v);
                }
            }
        }

        private void renameToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var obj = listBox1.Items[listbox1_MouseOverIndex];
            if (!(obj is Private_Keys))
                return;
            var sz = Program.InputBox("New name", null);
            if (sz == null)
                return;
            var k = (Private_Keys)obj;
            db.renamePrv(k.id, sz);
            LoadIdent();
        }

        private void tabPage3_Click(object sender, EventArgs e)
        {

        }

        private void label2_Click(object sender, EventArgs e)
        {

        }

        private void button1_Click(object sender, EventArgs e)
        {
            var name = textBox1.Text;
            var pubpem = textBox2.Text;
            var text = textBox3.Text;
            var o = listBox1.SelectedItem;
            if (o == null)
            {
                MessageBox.Show("Select an account or unsigned");//should i ever get this?
                return;
            }
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show("Please Enter A Name");
                return;
            }
            if (string.IsNullOrEmpty(pubpem))
            {
                MessageBox.Show("Please Enter A Public key (In the form of PEM text)");
                return;
            }
            byte[] their_pubkey;
            try
            {
                their_pubkey = Utils.ExtractPublicKey2(pubpem);
            }
            catch (Exception ex)
            {
                MessageBox.Show("I don't like their public key. Try again?");
                return;
            }
            long dbId;
            Client client;
            try
            {
                client = MakeClient();
            }
            catch (Client.MyException ex)
            {
                MessageBox.Show(ex.Message);
                return;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Some kind of error happened connecting to the server");
                return;
            }
            byte[] prvkey = null, pubkey = null;

            if (!(o is string))
            {
                var prv = (Private_Keys)o;
                prvkey = prv.key;
                pubkey = Utils.ExtractPublicKey(prv.key);
            }
            byte[] msgid;
            try
            {
                msgid = client.SendMessage(their_pubkey, replyTo, text, prvkey, pubkey);
            }
            catch (Client.MyException ex)
            {
                MessageBox.Show(ex.Message);
                return;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Some kind of error while sending the message");
                return;
            }
            dbId = db.SentMessage(name, their_pubkey, text, prvkey, msgid, replyTo);
            MessageBox.Show("Sent");
            cancelReplyTo();
            textBox3.Text = "";
            var hit = SelectCombo1(dbId);
            if (hit == false)
            {
                LoadPubkeys(loadPub_prvkey);
                SelectCombo1(dbId);
            }
            LoadOutbox(loadPub_prvkey, true);
        }
        bool SelectCombo1(long id)
        {
            bool hit = false;
            int i = 0;
            foreach (Public_Keys v in comboBox1.Items)
            {
                ++i;
                if (v.id == id)
                {
                    hit = true;
                    comboBox1.SelectedIndex = i - 1;
                    break;
                }
            }
            return hit;
        }
        Client MakeClient()
        {
            return new Client(server_addr, server_port, proxy_addr, proxy_port, server_pubkeyB);
        }

        bool SelectedIndexChangingIt = false;
        private void comboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (comboBox1.SelectedIndex == -1)
                return;
            var p = (Public_Keys)comboBox1.SelectedItem;
            textBox1.Text = p.name;
            SelectedIndexChangingIt = true;
            textBox2.Text = Shared.pubToPem(p.key);
            SelectedIndexChangingIt = false;
        }

        private void textBox2_TextChanged(object sender, EventArgs e)
        {
            if (SelectedIndexChangingIt == false)
            {
                comboBox1.SelectedIndex = -1;
            }
            cancelReplyTo();
        }

        private void tabControl1_TabIndexChanged(object sender, EventArgs e)
        {

        }

        private void getMessagesToolStripMenuItem_Click(object sender, EventArgs e)
        {
        }

        private void listBox2_SelectedIndexChanged(object sender, EventArgs e)
        {
            var box = (OutboxMsg)listBox2.SelectedItem;
            if (box == null)
                return;
            foreach (var v in db.pubkeys)
            {
                if (v.id == box.theirpub)
                {
                    lookingAtPubkey = v;
                    var sz = Convert.ToBase64String(v.key);
                    textBox4.Text = string.Format("{0} {1}", v.name, sz);
                    break;
                }
            }
            textBox5.Text = Convert.ToBase64String(box.msgId);
            textBox6.Text = box.txt;
        }

        private void tabControl1_SelectedIndexChanged(object sender, EventArgs e)
        {
        }

        private void tabPage2_Click(object sender, EventArgs e)
        {

        }

        private void button3_Click(object sender, EventArgs e)
        {
            var box = (OutboxMsg)listBox2.SelectedItem;
            ShowReplyTo(box.msgId, lookingAtPubkey.name, Shared.pubToPem(lookingAtPubkey.key));
        }

        private void button2_Click(object sender, EventArgs e)
        {
            cancelReplyTo();
        }
        void ShowReplyTo(byte[] msgid, string name, string key)
        {
            comboBox1.SelectedIndex = -1;
            textBox1.Text = name;
            textBox2.Text = key;
            textBox12.Text = Convert.ToBase64String(msgid);

            replyTo = msgid;
            button2.Visible = true;
            textBox12.Visible = true;
            tabControl1.SelectedIndex = 2;
        }
        void cancelReplyTo()
        {
            replyTo = null;
            button2.Visible = false;
            textBox12.Visible = false;
        }
        private void button5_Click(object sender, EventArgs e)
        {
            var prv = (Private_Keys)listBox1.SelectedItem;
            var store = db.GetStore(prv);
            int c = 0;
            Client c1;
            try
            {
                c1 = MakeClient();
            }
            catch (Client.MyException ex)
            {
                MessageBox.Show(ex.Message);
                return;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Some kind of error happened connecting to the server");
                return;
            }
            try
            {
                c = c1.GetMessage(prv.key, Utils.ExtractPublicKey(prv.key), store);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error while trying to get you message(s).");
                LoadInbox(prv.id);
                return;
            }

            if (c == 0)
            {
                MessageBox.Show("No Messages");
                return;
            }
            LoadInbox(prv.id);
            if (c == 1)
                MessageBox.Show("1 new message");
            else
                MessageBox.Show(string.Format("{0} new messages", c));
        }

        private void tabControl1_Selecting(object sender, TabControlCancelEventArgs e)
        {
            e.Cancel = (e.TabPageIndex == 0) && (listBox1.SelectedIndex <= 0);
            if (e.Cancel)
            {
                MessageBox.Show("You can't recieve messages. Only check unsigned messages you wrote or write unsigned messages");
            }
        }

        private void listBox3_SelectedIndexChanged(object sender, EventArgs e)
        {
            var msg = (Client.Message)listBox3.SelectedItem;
            if (msg == null)
                return;
            if (msg.forged)
            {
                textBox10.BackColor = Color.Red;
                textBox10.Text = "Forged!";
            }
            else if (msg.signed)
            {
                textBox10.BackColor = Color.LightGreen;
                textBox10.Text = "Verified";
            }
            else
            {
                textBox10.BackColor = Color.LightYellow;
                textBox10.Text = "Unsigned";
            }
            textBox8.Text = Convert.ToBase64String(msg.msgid);
            if (msg.replyTo.All(s => s == 0) == false)
                textBox11.Text = Convert.ToBase64String(msg.replyTo);
            else
                textBox11.Text = "0";
            textBox7.Text = msg.msg;
            textBox9.Text = GetNamePublicId(msg.their_pubkey, false);
        }

        public static string GetNamePublicId(byte[] theirPubkey, bool shortSz)
        {
            if (theirPubkey == null)
            {
                return @"Anonymous/Unsigned";
            }
            var sz = Convert.ToBase64String(theirPubkey);
            if (shortSz)
                sz = sz.Substring(sz.Length - 24).Substring(0, 16);
            foreach (var v in DB.db.pubkeys)
            {
                if (theirPubkey.ArraysEqual(v.key))
                {

                    return string.Format("{0} {1}", v.name, sz);
                }
            }
            return @"-Unknown- " + sz;
        }

        private void button4_Click(object sender, EventArgs e)
        {
            var item = (Client.Message)listBox3.SelectedItem;
            if (item == null)
                return;
            if (item.their_pubkey == null)
            {
                MessageBox.Show("This is not signed. Can not reply");
                return;
            }
            ShowReplyTo(item.msgid, db.GetName(item.their_pubkey) ?? "", Shared.pubToPem(item.their_pubkey));
        }

        private void dToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var ofd = new OpenFileDialog() { Filter = "PEM File|*.pem" };
            if (ofd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                using (var stream = ofd.OpenFile())
                using (var s2 = new StreamReader(stream))
                {
                    var pem = s2.ReadToEnd();
                    System.Security.Cryptography.RSAParameters rsap;
                    try
                    {
                        if (Shared.LoadKey2(pem, null, out rsap) == false)
                        {
                            MessageBox.Show("This appears to only have a public key. This option imports private keys");
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("I don't like this file");
                        return;
                    }
                    var name = Program.InputBox(@"What should the ""name"" be? (You may change this at anytime)", null);
                    if (name == null) { return; }
                    try
                    {
                        if (db.newprv(Utils.PemToPrivate_NoChecks(pem), name, MakeClient()) == false)
                        {
                            MessageBox.Show("Key already exists");
                        }
                    }
                    catch (Client.MyException ex)
                    {
                        MessageBox.Show(ex.Message);
                        return;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Error. Likely no connection to the server");
                        return;
                    }
                    LoadIdent();
                }
            }
        }

        private void runConfigWizardToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Program.startWizard(Program.password, null, 0, proxy_addr, proxy_port, server_addr, server_port, server_pubkey, db);
            LoadSettings();
        }

        private void fToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var obj = listBox1.Items[listbox1_MouseOverIndex];
            if (!(obj is Private_Keys))
                return;
            var k = (Private_Keys)obj;
            var pemsz = Shared.prvToPem(k.key);
            var s = new SaveFileDialog() { Title = "Export PRIVATE key", Filter = "PEM|*.pem" };
            if (s.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                using (var s1 = s.OpenFile())
                using (var sw = new StreamWriter(s1))
                {
                    sw.Write(pemsz);
                }
            }
        }

        private void gToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var obj = listBox1.Items[listbox1_MouseOverIndex];
            if (!(obj is Private_Keys))
                return;
            var k = (Private_Keys)obj;
            var pemsz = Utils.ExtractPublicKeyAsPem(k.key);
            var s = new SaveFileDialog() { Title = "Export PUBLIC key", Filter = "PEM|*.pem" };
            if (s.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                using (var s1 = s.OpenFile())
                using (var sw = new StreamWriter(s1))
                {
                    sw.Write(pemsz);
                }
            }
        }

        private void button6_Click(object sender, EventArgs e)
        {
            button6.Enabled = false;
            new System.Threading.Thread(GetAllMessages).Start();
        }
        void GetAllMessages()
        {
            GetAllMessages2();
            this.Invoke((MethodInvoker)delegate { 
                button6.Enabled = true;
                var o = listBox1.SelectedItem;
                if (o is string)
                    return;
                LoadInbox(((Private_Keys)o).id);
            });
        }
        class MessageBox
        {
            static public void Show(string msg) { System.Windows.Forms.MessageBox.Show(msg); }
        }
        void GetAllMessages2()
        {
            int timeRemaining = 10 * 60 * 1000;
            var rnd = new Random();
            var itemList = new List<Private_Keys>();
            var timeSpan = TimeSpan.FromMilliseconds(0);
            for (int i = 1; i < listBox1.Items.Count; ++i)
            {
                itemList.Add((Private_Keys)(listBox1.Items[i]));
            }
            var keys = itemList.Shuffle(rnd).ToArray();

            int c = 0;

            for (int i = 0; i < keys.Count(); ++i)
            {
                int dividedTime = timeRemaining / (keys.Count() - i);
                int rangeL = dividedTime / 4;
                int rangeH = (int)(dividedTime * 2.5);
                var SleepTime = rnd.Next(rangeL, rangeH);
                timeSpan += TimeSpan.FromMilliseconds(SleepTime);
                timeRemaining -= SleepTime;
                //System.Diagnostics.Trace.WriteLine(SleepTime);
                System.Threading.Thread.Sleep(SleepTime);
                var prv = keys[i];
                var store = db.GetStore(prv);
                Client c1;
                try
                {
                    c1 = MakeClient();
                }
                catch (Client.MyException ex)
                {
                    MessageBox.Show(ex.Message);
                    return;
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Some kind of error happened connecting to the server");
                    return;
                }
                try
                {
                    c += c1.GetMessage(prv.key, Utils.ExtractPublicKey(prv.key), store);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Error while trying to get you message(s).");
                    LoadInbox(prv.id);
                    return;
                }
            }
            if (c == 0)
            {
                MessageBox.Show("No Messages");
                return;
            }
            if (c == 1)
                MessageBox.Show("1 new message");
            else
                MessageBox.Show(string.Format("{0} new messages", c));
        }
    }
    static class Ext
    {
        //http://stackoverflow.com/questions/1287567/is-using-random-and-orderby-a-good-shuffle-algorithm
        public static IEnumerable<T> Shuffle<T>(this IEnumerable<T> source, Random rng)
        {
            T[] elements = source.ToArray();
            for (int i = elements.Length - 1; i >= 0; i--)
            {
                // Swap element "i" with a random earlier element it (or itself)
                // ... except we don't really need to swap it fully, as we can
                // return it immediately, and afterwards it's irrelevant.
                int swapIndex = rng.Next(i + 1);
                yield return elements[swapIndex];
                elements[swapIndex] = elements[i];
            }
        }
    }
}
