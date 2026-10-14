using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PCOptimizer
{
    /// <summary>
    /// Полупрозрачное окошко поверх игры: FPS, загрузка и температуры.
    /// Фокус у игры не забирает и в панели задач не показывается,
    /// поэтому игра не сворачивается. Двигается мышкой за любую точку.
    /// </summary>
    internal sealed class OverlayForm : Form
    {
        private readonly Label _titleLabel;
        private readonly Label _fpsLabel;
        private readonly Label _cpuLabel;
        private readonly Label _gpuLabel;
        private readonly Label _hintLabel;
        private readonly Button _collapseButton;
        private readonly Button _closeButton;

        private const int FullHeight = 142;
        private const int SlimHeight = 30;

        private bool _collapsed;
        private bool _dragging;
        private Point _dragOffset;

        public event EventHandler? CloseRequested;

        public OverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            Opacity = 0.88;
            BackColor = Color.FromArgb(10, 10, 14);
            ForeColor = Color.FromArgb(230, 228, 238);
            Size = new Size(252, FullHeight);
            DoubleBuffered = true;
            KeyPreview = false;

            _titleLabel = MakeLabel("PCOptimizer · оверлей", 12, 8, 150, 16, 9f, FontStyle.Bold,
                Color.FromArgb(255, 90, 120));

            _collapseButton = MakeButton("–", 172, 5);
            _collapseButton.Click += (sender, args) => ToggleCollapse();

            _closeButton = MakeButton("✕", 208, 5);
            _closeButton.Click += (sender, args) => CloseRequested?.Invoke(this, EventArgs.Empty);

            _fpsLabel = MakeLabel("FPS   н/д", 12, 32, 230, 28, 18f, FontStyle.Bold, Color.White);
            _cpuLabel = MakeLabel("CPU   —", 12, 64, 230, 18, 10.5f, FontStyle.Regular,
                Color.FromArgb(196, 194, 208));
            _gpuLabel = MakeLabel("GPU   —", 12, 86, 230, 18, 10.5f, FontStyle.Regular,
                Color.FromArgb(196, 194, 208));
            _hintLabel = MakeLabel("", 12, 110, 230, 26, 7.5f, FontStyle.Regular,
                Color.FromArgb(126, 126, 148));

            Controls.Add(_titleLabel);
            Controls.Add(_collapseButton);
            Controls.Add(_closeButton);
            Controls.Add(_fpsLabel);
            Controls.Add(_cpuLabel);
            Controls.Add(_gpuLabel);
            Controls.Add(_hintLabel);

            // Перетаскивание за любую точку окна.
            WireDrag(this);
            WireDrag(_titleLabel);
            WireDrag(_fpsLabel);
            WireDrag(_cpuLabel);
            WireDrag(_gpuLabel);
            WireDrag(_hintLabel);

            ApplyRoundedCorners();
        }

        /// <summary>Окно не должно забирать фокус у игры.</summary>
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;

                // WS_EX_TOOLWINDOW — не показывать в Alt+Tab.
                // WS_EX_NOACTIVATE — не активировать по клику.
                parameters.ExStyle |= 0x00000080 | 0x08000000;

                return parameters;
            }
        }

        private Label MakeLabel(
            string text, int x, int y, int width, int height, float size, FontStyle style, Color color)
        {
            return new Label
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(width, height),
                Font = new Font("Segoe UI", size, style),
                ForeColor = color,
                BackColor = Color.Transparent,
                AutoSize = false
            };
        }

        private Button MakeButton(string text, int x, int y)
        {
            var button = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(30, 20),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(200, 198, 212),
                BackColor = Color.FromArgb(26, 26, 34),
                TabStop = false,
                Cursor = Cursors.Hand
            };

            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(255, 43, 74);

            return button;
        }

        private void WireDrag(Control control)
        {
            control.MouseDown += (sender, args) =>
            {
                if (args.Button != MouseButtons.Left)
                {
                    return;
                }

                _dragging = true;
                _dragOffset = new Point(args.X, args.Y);
            };

            control.MouseMove += (sender, args) =>
            {
                if (!_dragging)
                {
                    return;
                }

                Location = new Point(
                    Location.X + args.X - _dragOffset.X,
                    Location.Y + args.Y - _dragOffset.Y);
            };

            control.MouseUp += (sender, args) => _dragging = false;
        }

        private void ToggleCollapse()
        {
            _collapsed = !_collapsed;

            _fpsLabel.Visible = !_collapsed;
            _cpuLabel.Visible = !_collapsed;
            _gpuLabel.Visible = !_collapsed;
            _hintLabel.Visible = !_collapsed;

            _collapseButton.Text = _collapsed ? "+" : "–";
            Height = _collapsed ? SlimHeight : FullHeight;

            ApplyRoundedCorners();
        }

        private void ApplyRoundedCorners()
        {
            const int radius = 10;

            using var path = new GraphicsPath();
            int d = radius * 2;

            path.AddArc(0, 0, d, d, 180, 90);
            path.AddArc(Width - d, 0, d, d, 270, 90);
            path.AddArc(Width - d, Height - d, d, d, 0, 90);
            path.AddArc(0, Height - d, d, d, 90, 90);
            path.CloseFigure();

            Region?.Dispose();
            Region = new Region(path);
        }

        /// <summary>Обновляет показания. Вызывается из основного окна раз в секунду.</summary>
        public void Apply(Snapshot snapshot, double? fps, string hint)
        {
            _fpsLabel.Text = "FPS   " + (fps.HasValue
                ? Math.Round(fps.Value).ToString("0")
                : "н/д");

            string cpuTemp = snapshot.Cpu.Temp.HasValue
                ? Math.Round(snapshot.Cpu.Temp.Value) + " °C"
                : "н/д";

            _cpuLabel.Text = "CPU   " + Math.Round(snapshot.Cpu.Load) + "%      " + cpuTemp;

            string gpuLoad = snapshot.Gpu.Load >= 0
                ? Math.Round(snapshot.Gpu.Load) + "%"
                : "н/д";

            string gpuTemp = snapshot.Gpu.Temp > 0
                ? Math.Round(snapshot.Gpu.Temp) + " °C"
                : "н/д";

            _gpuLabel.Text = "GPU   " + gpuLoad + "      " + gpuTemp;
            _hintLabel.Text = hint;

            _titleLabel.Text = _collapsed ? "FPS: " + _fpsLabel.Text[^4..].Trim() : "PCOptimizer · оверлей";
        }
    }
}
