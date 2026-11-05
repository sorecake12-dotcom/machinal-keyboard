using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace MechanicalKeyboardApp
{
    public class CustomTitleBar : Panel
    {
        public CustomTitleBar()
        {
            this.Height = 32;
            this.Dock = DockStyle.Top;
            this.BackColor = Color.FromArgb(25, 25, 35);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(this.FindForm().Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
            }
        }

        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();
        const int WM_NCLBUTTONDOWN = 0xA1;
        const int HT_CAPTION = 0x2;
    }

    public class MainForm : Form
    {
        private Panel _sidebar;
        private Panel _contentPanel;

        public MainForm()
        {
            this.FormBorderStyle = FormBorderStyle.None;
            this.Size = new Size(1000, 650);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(20, 20, 28);
            this.DoubleBuffered = true;

            // Header
            var titleBar = new CustomTitleBar();
            this.Controls.Add(titleBar);

            // Sidebar
            _sidebar = new Panel { Width = 200, Dock = DockStyle.Left, BackColor = Color.FromArgb(25, 25, 35) };
            this.Controls.Add(_sidebar);

            // Content
            _contentPanel = new Panel { Dock = DockStyle.Fill };
            this.Controls.Add(_contentPanel);

            // Sidebar Buttons
            string[] navItems = { "Main", "Themes", "Settings", "About" };
            for(int i = 0; i < navItems.Length; i++)
            {
                var btn = new Button {
                    Text = navItems[i],
                    Dock = DockStyle.Top,
                    Height = 50,
                    FlatStyle = FlatStyle.Flat,
                    ForeColor = Color.White,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Padding = new Padding(20,0,0,0)
                };
                btn.FlatAppearance.BorderSize = 0;
                btn.Click += (s, e) => SwitchPage(btn.Text);
                _sidebar.Controls.Add(btn);
            }
        }

        private void SwitchPage(string page)
        {
            _contentPanel.Controls.Clear();
            _contentPanel.Controls.Add(new Label {
                Text = page + " Page (Stub)",
                ForeColor = Color.White,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter
            });
        }
    }
}
