using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PCOptimizer
{
    /// <summary>
    /// Мини-оверлей на рабочем столе: процессор, видеокарта, температуры и память.
    /// Намеренно НЕ TopMost — он живёт на рабочем столе и не лезет поверх игр
    /// и полноэкранного видео. Двигается мышкой, складывается в полоску,
    /// по крестику прячется в трей.
    /// </summary>
    internal sealed class MiniOverlayForm : Form
    {
        private readonly Label _titleLabel;
        private readonly Label _cpuCaption;
        private readonly Label _gpuCaption;
        private readonly Label _cpuValue;
        private readonly Label _gpuValue;
        private readonly Label _cpuTemp;
        private readonly Label _gpuTemp;
        private readonly Label _ramLabel;
        private readonly Label _slimLabel;
        private readonly Button _collapseButton;
        private readonly Button _hideButton;

        private const int FullHeight = 106;
        private const int SlimHeight = 26;
        private const int CardWidth = 214;

        private bool _collapsed;
        private bool _dragging;
        private Point _dragOffset;
        private Color _accent = Color.FromArgb(255, 43, 74);

        /// <summary>Крестик — спрятать в трей.</summary>
        public event EventHandler? HideRequested;

        /// <summary>Окно передвинули — надо запомнить новое место.</summary>
        public event EventHandler? Moved;

        public MiniOverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = false;
            Opacity = 0.86;
            BackColor = Color.FromArgb(12, 12, 17);
            ForeColor = Color.FromArgb(228, 226, 238);
            Size = new Size(CardWidth, FullHeight);
            DoubleBuffered = true;

            _titleLabel = MakeLabel("PCOptimizer", 12, 6, 110, 15, 8f, FontStyle.Bold, _accent);
            _slimLabel = MakeLabel("", 12, 5, 150, 16, 8.5f, FontStyle.Bold,
                Color.FromArgb(214, 212, 226));
            _slimLabel.Visible = false;

            _collapseButton = MakeButton("–", 158, 3);
            _collapseButton.Click += (sender, args) => ToggleCollapse();

            _hideButton = MakeButton("✕", 182, 3);
            _hideButton.Click += (sender, args) => HideRequested?.Invoke(this, EventArgs.Empty);

            _cpuCaption = MakeLabel("ПРОЦЕССОР", 12, 28, 100, 13, 6.8f, FontStyle.Bold,
                Color.FromArgb(122, 122, 146));
            _gpuCaption = MakeLabel("ВИДЕОКАРТА", 112, 28, 100, 13, 6.8f, FontStyle.Bold,
                Color.FromArgb(122, 122, 146));

            _cpuValue = MakeLabel("—", 12, 41, 100, 24, 15f, FontStyle.Bold, Color.White);
            _gpuValue = MakeLabel("—", 112, 41, 100, 24, 15f, FontStyle.Bold, Color.White);

            _cpuTemp = MakeLabel("—", 12, 65, 100, 15, 8.5f, FontStyle.Regular,
                Color.FromArgb(186, 184, 200));
            _gpuTemp = MakeLabel("—", 112, 65, 100, 15, 8.5f, FontStyle.Regular,
                Color.FromArgb(186, 184, 200));

            _ramLabel = MakeLabel("ОЗУ   — / — ГБ", 12, 84, 190, 15, 8.5f, FontStyle.Regular,
                Color.FromArgb(158, 156, 176));

            Controls.Add(_slimLabel);
            Controls.Add(_titleLabel);
            Controls.Add(_collapseButton);
            Controls.Add(_hideButton);
            Controls.Add(_cpuCaption);
            Controls.Add(_gpuCaption);
            Controls.Add(_cpuValue);
            Controls.Add(_gpuValue);
            Controls.Add(_cpuTemp);
            Controls.Add(_gpuTemp);
            Controls.Add(_ramLabel);

            WireDrag(this);
            WireDrag(_titleLabel);
            WireDrag(_slimLabel);
            WireDrag(_cpuCaption);
            WireDrag(_gpuCaption);
            WireDrag(_cpuValue);
            WireDrag(_gpuValue);
            WireDrag(_cpuTemp);
            WireDrag(_gpuTemp);
            WireDrag(_ramLabel);

            ApplyRoundedCorners();
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
                Size = new Size(22, 18),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                ForeColor = Color.FromArgb(196, 194, 210),
                BackColor = Color.FromArgb(28, 28, 38),
                TabStop = false,
                Cursor = Cursors.Hand
            };

            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = _accent;

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

            control.MouseUp += (sender, args) =>
            {
                if (!_dragging)
                {
                    return;
                }

                _dragging = false;
                Moved?.Invoke(this, EventArgs.Empty);
            };
        }

        private void ToggleCollapse()
        {
            _collapsed = !_collapsed;

            _titleLabel.Visible = !_collapsed;
            _cpuCaption.Visible = !_collapsed;
            _gpuCaption.Visible = !_collapsed;
            _cpuValue.Visible = !_collapsed;
            _gpuValue.Visible = !_collapsed;
            _cpuTemp.Visible = !_collapsed;
            _gpuTemp.Visible = !_collapsed;
            _ramLabel.Visible = !_collapsed;
            _slimLabel.Visible = _collapsed;

            _collapseButton.Text = _collapsed ? "+" : "–";
            Height = _collapsed ? SlimHeight : FullHeight;

            ApplyRoundedCorners();
        }

        /// <summary>Сменить акцентный цвет под выбранную схему.</summary>
        public void SetAccent(Color accent)
        {
            _accent = accent;
            _titleLabel.ForeColor = accent;
            _collapseButton.FlatAppearance.MouseOverBackColor = accent;
            _hideButton.FlatAppearance.MouseOverBackColor = accent;
        }

        /// <summary>Обновляет показания, вызывается из главного окна раз в секунду.</summary>
        public void Apply(Snapshot snapshot)
        {
            string cpuLoad = Math.Round(snapshot.Cpu.Load) + "%";
            string gpuLoad = snapshot.Gpu.Load >= 0 ? Math.Round(snapshot.Gpu.Load) + "%" : "н/д";

            string cpuTemp = snapshot.Cpu.Temp.HasValue
                ? Math.Round(snapshot.Cpu.Temp.Value) + " °C"
                : "темп. н/д";

            string gpuTemp = snapshot.Gpu.Temp > 0
                ? Math.Round(snapshot.Gpu.Temp) + " °C"
                : "темп. н/д";

            _cpuValue.Text = cpuLoad;
            _gpuValue.Text = gpuLoad;
            _cpuTemp.Text = cpuTemp;
            _gpuTemp.Text = gpuTemp;

            double usedGb = snapshot.Ram.UsedMb / 1024.0;
            double totalGb = snapshot.Ram.TotalMb / 1024.0;

            _ramLabel.Text = "ОЗУ   " + usedGb.ToString("0.0") + " / " + totalGb.ToString("0") + " ГБ   ("
                + snapshot.Ram.Percent + "%)";

            _slimLabel.Text = "CPU " + cpuLoad + "   " + cpuTemp.Replace("темп. ", "");
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
    }
}
