using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace FrameGenLiveApp
{
    // =========================================================================
    // ЦВЕТОВАЯ ПАЛИТРА И СИСТЕМНЫЕ ШРИФТЫ
    // Спокойная серо-графитовая гамма, дефолтный шрифт Windows (SystemFonts),
    // без нейронных фиолетовых оттенков, без лишнего визуального шума.
    // =========================================================================
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(27, 29, 33);          // Спокойный нейтральный темно-серый
        public static readonly Color Surface = Color.FromArgb(35, 38, 44);     // Карточки и блоки
        public static readonly Color SurfaceHover = Color.FromArgb(43, 47, 54);// Подсветка при наведении
        public static readonly Color Border = Color.FromArgb(50, 55, 63);      // Спокойная граница
        public static readonly Color BorderActive = Color.FromArgb(70, 78, 90);// Рамка в фокусе
        public static readonly Color Text = Color.FromArgb(230, 233, 238);     // Четкий основной текст
        public static readonly Color TextMuted = Color.FromArgb(135, 142, 153);// Приглушенный текст
        public static readonly Color Accent = Color.FromArgb(75, 140, 220);    // Спокойный синий акцент
        public static readonly Color AccentBg = Color.FromArgb(30, 50, 76);    // Мягкий фон выбранного элемента
        public static readonly Color Green = Color.FromArgb(70, 180, 115);     // Индикатор активности
        public static readonly Color GreenBg = Color.FromArgb(26, 52, 36);
        public static readonly Color Red = Color.FromArgb(220, 80, 80);        // Остановка / ошибка
        public static readonly Color RedBg = Color.FromArgb(55, 28, 30);
        public static readonly Color Amber = Color.FromArgb(215, 155, 60);     // Таймер / закрепление

        // Стандартный дефолтный системный шрифт Windows (MessageBoxFont / Segoe UI)
        public static FontFamily SysFamily => SystemFonts.MessageBoxFont.FontFamily;
        public static Font FontBase(float size, FontStyle style = FontStyle.Regular) => new Font(SysFamily, size, style, GraphicsUnit.Point);
        public static Font FontMono(float size, FontStyle style = FontStyle.Regular) => new Font("Consolas", size, style, GraphicsUnit.Point);

        public static void DrawText(Graphics g, string text, Font font, Rectangle rect, Color color, TextFormatFlags flags)
            => TextRenderer.DrawText(g, text, font, rect, color, flags | TextFormatFlags.NoPadding);

        public static GraphicsPath RoundRect(Rectangle r, int radius = 4)
        {
            var path = new GraphicsPath();
            int d = radius * 2;
            if (r.Width < d || r.Height < d) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRound(Graphics g, Rectangle r, Color fill, int radius = 4)
        {
            using var path = RoundRect(r, radius);
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
        }

        public static void StrokeRound(Graphics g, Rectangle r, Color stroke, int radius = 4, int width = 1)
        {
            using var path = RoundRect(r, radius);
            using var pen = new Pen(stroke, width);
            g.DrawPath(pen, path);
        }
    }

    // =========================================================================
    // ЛОКАЛИЗАЦИЯ ИНТЕРФЕЙСА (АНГЛИЙСКИЙ ПО УМОЛЧАНИЮ, РУССКИЙ В НАСТРОЙКАХ)
    // =========================================================================
    static class Loc
    {
        public static string Current = "en"; // English is default
        public static bool IsRu => Current == "ru";
        public static string Tr(string en, string ru) => IsRu ? ru : en;
    }

    // =========================================================================
    // КНОПКА: Спокойная системная кнопка
    // =========================================================================
    class FlatButton : Control
    {
        public bool IsPrimary;
        public bool IsDanger;
        public bool IsWarning;
        bool hover, pressed;

        public FlatButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            Font = Theme.FontBase(9f, FontStyle.Bold);
            Height = 32;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                pressed = true;
                Invalidate();
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                pressed = false;
                Invalidate();
            }
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);

            Color bg, border, text;
            if (IsWarning)
            {
                bg = pressed ? Color.FromArgb(160, 110, 35) : (hover ? Color.FromArgb(235, 170, 70) : Theme.Amber);
                border = bg;
                text = Color.FromArgb(20, 20, 20);
            }
            else if (IsPrimary)
            {
                bg = pressed ? Color.FromArgb(50, 110, 180) : (hover ? Color.FromArgb(90, 160, 240) : Theme.Accent);
                border = bg;
                text = Color.FromArgb(255, 255, 255);
            }
            else if (IsDanger)
            {
                bg = pressed ? Color.FromArgb(80, 25, 25) : (hover ? Color.FromArgb(105, 38, 38) : Theme.RedBg);
                border = Theme.Red;
                text = Theme.Red;
            }
            else
            {
                bg = pressed ? Color.FromArgb(46, 50, 58) : (hover ? Theme.SurfaceHover : Theme.Surface);
                border = hover ? Theme.BorderActive : Theme.Border;
                text = Theme.Text;
            }

            Theme.FillRound(g, r, bg, 4);
            Theme.StrokeRound(g, r, border, 4);
            Theme.DrawText(g, Text, Font, r, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    // =========================================================================
    // СЕГМЕНТНЫЙ ПЕРЕКЛЮЧАТЕЛЬ С ПОДДЕРЖКОЙ КОЛЕСИКА МЫШИ
    // =========================================================================
    class SegmentGroup : Control
    {
        public string[] Items = Array.Empty<string>();
        public int SelectedIndex;
        int hoverIndex = -1;

        public SegmentGroup()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            Font = Theme.FontBase(8.8f);
            Height = 28;
        }

        public event EventHandler SelectionChanged;

        Rectangle ItemRect(int i)
        {
            int n = Math.Max(1, Items.Length);
            int w = Width / n;
            int x = i * w;
            int actualW = (i == n - 1) ? (Width - x) : w;
            return new Rectangle(x, 0, actualW, Height);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = -1;
            for (int i = 0; i < Items.Length; i++) if (ItemRect(i).Contains(e.Location)) { h = i; break; }
            if (h != hoverIndex) { hoverIndex = h; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hoverIndex = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                for (int i = 0; i < Items.Length; i++)
                {
                    if (ItemRect(i).Contains(e.Location))
                    {
                        if (SelectedIndex != i)
                        {
                            SelectedIndex = i;
                            Invalidate();
                            SelectionChanged?.Invoke(this, EventArgs.Empty);
                        }
                        break;
                    }
                }
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (Items.Length == 0) return;
            int next = SelectedIndex + (e.Delta > 0 ? -1 : 1);
            next = Math.Clamp(next, 0, Items.Length - 1);
            if (next != SelectedIndex)
            {
                SelectedIndex = next;
                Invalidate();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
            base.OnMouseWheel(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var full = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.FillRound(g, full, Theme.Surface, 4);
            Theme.StrokeRound(g, full, Theme.Border, 4);

            for (int i = 0; i < Items.Length; i++)
            {
                var r = ItemRect(i);
                bool sel = i == SelectedIndex;
                bool hov = i == hoverIndex && !sel;

                if (sel)
                {
                    var inner = new Rectangle(r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4);
                    Theme.FillRound(g, inner, Theme.AccentBg, 3);
                    Theme.StrokeRound(g, inner, Theme.Accent, 3);
                }
                else if (hov)
                {
                    var inner = new Rectangle(r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4);
                    Theme.FillRound(g, inner, Theme.SurfaceHover, 3);
                }

                Color ink = sel ? Theme.Text : (hov ? Theme.Text : Theme.TextMuted);
                Font f = sel ? Theme.FontBase(8.8f, FontStyle.Bold) : Font;
                Theme.DrawText(g, Items[i], f, r, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

                if (i < Items.Length - 1 && !sel && i != SelectedIndex - 1)
                {
                    using var p = new Pen(Theme.Border);
                    g.DrawLine(p, r.Right, r.Y + 5, r.Right, r.Bottom - 5);
                }
            }
        }
    }

    // =========================================================================
    // АДАПТИВНОЕ ПОЛЕ КУРСОРА ДЛЯ МНОЖИТЕЛЯ
    // Хитбокс чуть больше видимого поля, колесо мыши срабатывает СТРОГО в хитбоксе!
    // =========================================================================
    public enum FieldAlignment { Left, Center, Right }

    class AdaptiveMultiplierField : Control
    {
        public static readonly double[] Multipliers = { 0, 0, 1.5, 2.0, 2.5, 3.0, 4.0, 5.0 };
        public int SelectedIndex = 0;
        public int CustomHz = 240;
        public FieldAlignment Alignment = FieldAlignment.Center;

        public bool IsCustomHzMode => SelectedIndex == 1;
        public double MultiplierValue => SelectedIndex <= 1 ? 0 : Multipliers[SelectedIndex];
        public event EventHandler MultiplierChanged;

        bool hoverLeft, hoverRight, hoverMain;
        readonly TextBox txtHzInput;

        public AdaptiveMultiplierField()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            Font = Theme.FontBase(9f);
            Height = 36;

            txtHzInput = new TextBox
            {
                Visible = false,
                BorderStyle = BorderStyle.None,
                BackColor = Theme.SurfaceHover,
                ForeColor = Theme.Accent,
                Font = Theme.FontBase(9f, FontStyle.Bold),
                TextAlign = HorizontalAlignment.Center,
                MaxLength = 4
            };
            txtHzInput.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { CommitHzEdit(); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Escape) { CancelHzEdit(); e.SuppressKeyPress = true; }
            };
            txtHzInput.LostFocus += (s, e) => CommitHzEdit();
            Controls.Add(txtHzInput);
        }

        public Rectangle TargetBox
        {
            get
            {
                int boxW = 164;
                int x;
                switch (Alignment)
                {
                    case FieldAlignment.Left:
                        x = 0;
                        break;
                    case FieldAlignment.Right:
                        x = Width - boxW;
                        break;
                    default: // Center
                        x = (Width - boxW) / 2;
                        break;
                }
                return new Rectangle(x, 0, boxW, Height);
            }
        }

        // Хитбокс чуть больше видимого поля (на 8 px шире и 4 px выше)
        public Rectangle HitBox
        {
            get
            {
                var r = TargetBox;
                r.Inflate(8, 4);
                return r;
            }
        }

        Rectangle LeftArrowRect
        {
            get
            {
                var tb = TargetBox;
                return new Rectangle(tb.X, tb.Y, 30, tb.Height);
            }
        }

        Rectangle RightArrowRect
        {
            get
            {
                var tb = TargetBox;
                return new Rectangle(tb.Right - 30, tb.Y, 30, tb.Height);
            }
        }

        Rectangle CenterTextRect
        {
            get
            {
                var tb = TargetBox;
                return new Rectangle(tb.X + 30, tb.Y, tb.Width - 60, tb.Height);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool hl = LeftArrowRect.Contains(e.Location);
            bool hr = RightArrowRect.Contains(e.Location);
            bool hm = HitBox.Contains(e.Location) && !hl && !hr;

            if (hl != hoverLeft || hr != hoverRight || hm != hoverMain)
            {
                hoverLeft = hl; hoverRight = hr; hoverMain = hm;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hoverLeft = hoverRight = hoverMain = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (LeftArrowRect.Contains(e.Location)) Modify(-1);
                else if (RightArrowRect.Contains(e.Location)) Modify(1);
                else if (SelectedIndex == 1 && CenterTextRect.Contains(e.Location))
                {
                    StartHzEdit();
                }
            }
            else if (e.Button == MouseButtons.Right && SelectedIndex == 1)
            {
                ShowHzMenu(e.Location);
            }
            base.OnMouseDown(e);
        }

        void StartHzEdit()
        {
            var cr = CenterTextRect;
            txtHzInput.Bounds = new Rectangle(cr.X + 4, (Height - 20) / 2, cr.Width - 8, 20);
            txtHzInput.Text = CustomHz.ToString();
            txtHzInput.Visible = true;
            txtHzInput.BringToFront();
            txtHzInput.Focus();
            txtHzInput.SelectAll();
        }

        void CommitHzEdit()
        {
            if (!txtHzInput.Visible) return;
            txtHzInput.Visible = false;
            if (int.TryParse(txtHzInput.Text.Trim(), out int val))
            {
                CustomHz = Math.Clamp(val, 30, 1000);
                Invalidate();
                MultiplierChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        void CancelHzEdit()
        {
            txtHzInput.Visible = false;
            Invalidate();
        }

        void ShowHzMenu(Point pt)
        {
            var menu = new ContextMenuStrip { BackColor = Theme.Surface, ForeColor = Theme.Text, Font = Theme.FontBase(8.5f) };
            int[] common = { 60, 75, 120, 144, 165, 240, 280, 360, 500 };
            foreach (int h in common)
            {
                int hzVal = h;
                var itm = new ToolStripMenuItem($"{hzVal} FPS") { Checked = (CustomHz == hzVal) };
                itm.Click += (s, e) =>
                {
                    CustomHz = hzVal;
                    Invalidate();
                    MultiplierChanged?.Invoke(this, EventArgs.Empty);
                };
                menu.Items.Add(itm);
            }
            menu.Show(this, pt);
        }

        // Колесо мыши работает СТРОГО в хитбоксе поля!
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (!HitBox.Contains(e.Location))
                return; // Игнорируем колесо мыши вне поля множителя!

            Modify(e.Delta > 0 ? 1 : -1);
            base.OnMouseWheel(e);
        }

        public void Modify(int delta)
        {
            CancelHzEdit();
            int next = Math.Clamp(SelectedIndex + delta, 0, Multipliers.Length - 1);
            if (next != SelectedIndex)
            {
                SelectedIndex = next;
                Invalidate();
                MultiplierChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var r = TargetBox;
            r.Width -= 1; r.Height -= 1;

            // Текстовая подсказка в зависимости от положения поля
            if (Alignment == FieldAlignment.Right)
            {
                Theme.DrawText(g, Loc.Tr("TARGET MULTIPLIER / FPS   (Mouse wheel in field) →", "ЦЕЛЕВОЙ МНОЖИТЕЛЬ / FPS   (Колесико мыши в поле) →"),
                    Theme.FontBase(8f, FontStyle.Bold), new Rectangle(0, 0, r.X - 16, Height),
                    Theme.TextMuted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
            else if (Alignment == FieldAlignment.Left)
            {
                Theme.DrawText(g, Loc.Tr("← (Mouse wheel in field)   TARGET MULTIPLIER / FPS", "← (Колесико мыши в поле)   ЦЕЛЕВОЙ МНОЖИТЕЛЬ / FPS"),
                    Theme.FontBase(8f, FontStyle.Bold), new Rectangle(r.Right + 16, 0, Width - r.Right - 16, Height),
                    Theme.TextMuted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }
            else // Center
            {
                Theme.DrawText(g, Loc.Tr("TARGET MULTIPLIER / FPS →", "ЦЕЛЕВОЙ МНОЖИТЕЛЬ / FPS →"),
                    Theme.FontBase(8f, FontStyle.Bold), new Rectangle(0, 0, r.X - 16, Height),
                    Theme.TextMuted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

                Theme.DrawText(g, Loc.Tr("← (Mouse wheel in field)", "← (Колесико мыши в поле)"),
                    Theme.FontBase(8f, FontStyle.Bold), new Rectangle(r.Right + 16, 0, Width - r.Right - 16, Height),
                    Theme.TextMuted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }

            // Поле множителя
            Color fill = (hoverMain || hoverLeft || hoverRight) ? Theme.SurfaceHover : Theme.Surface;
            Color border = Theme.Accent;

            Theme.FillRound(g, r, fill, 4);
            Theme.StrokeRound(g, r, border, 4);

            Theme.DrawText(g, "◀", Theme.FontBase(8f), LeftArrowRect, hoverLeft ? Theme.Text : Theme.TextMuted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            Theme.DrawText(g, "▶", Theme.FontBase(8f), RightArrowRect, hoverRight ? Theme.Text : Theme.TextMuted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            if (!txtHzInput.Visible)
            {
                string valText;
                if (SelectedIndex == 0) valText = Loc.Tr("Auto (Screen)", "Авто (экран)");
                else if (SelectedIndex == 1) valText = $"{CustomHz} FPS";
                else valText = $"{Multipliers[SelectedIndex]:0.#}× FPS";

                var centerRect = CenterTextRect;
                Theme.DrawText(g, valText, Theme.FontBase(9f, FontStyle.Bold), centerRect, Theme.Accent,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }

    // =========================================================================
    // КАРТОЧКА МОНИТОРИНГА FPS (100% Flicker-Free Double Buffered)
    // =========================================================================
    class MonitorCard : Control
    {
        public string DisplayIn = "—";
        public string DisplayOut = "—";
        public string SubMetrics = Loc.Tr("Waiting for game frames...", "Ожидание запуска игры...");
        public int StabilityScore = 0;
        public bool IsRunning = false;

        public MonitorCard()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            DoubleBuffered = true;
            Height = 84;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.FillRound(g, r, Theme.Surface, 4);
            Theme.StrokeRound(g, r, Theme.Border, 4);

            Color dotCol = IsRunning ? Theme.Green : Theme.TextMuted;
            using (var b = new SolidBrush(dotCol))
                g.FillEllipse(b, 16, 16, 8, 8);

            string statusText = IsRunning 
                ? Loc.Tr("GENERATOR ACTIVE", "ГЕНЕРАТОР АКТИВЕН") 
                : Loc.Tr("STOPPED", "ОСТАНОВЛЕН");
            Theme.DrawText(g, statusText, Theme.FontBase(8f, FontStyle.Bold), new Rectangle(30, 14, 180, 14), dotCol, TextFormatFlags.Left);

            if (IsRunning && StabilityScore > 0)
            {
                string stabText = Loc.Tr($"Stability: {StabilityScore}/5", $"Стабильность: {StabilityScore}/5");
                Color stabCol = StabilityScore >= 4 ? Theme.Green : (StabilityScore == 3 ? Theme.Amber : Theme.Red);
                Theme.DrawText(g, stabText, Theme.FontBase(8f, FontStyle.Bold), new Rectangle(Width - 160, 14, 144, 14), stabCol, TextFormatFlags.Right);
            }

            string fpsString = $"{DisplayIn} FPS   →   {DisplayOut} FPS";
            Theme.DrawText(g, fpsString, Theme.FontBase(15f, FontStyle.Bold), new Rectangle(16, 32, Width - 32, 26), Theme.Text, TextFormatFlags.Left);

            Theme.DrawText(g, SubMetrics, Theme.FontMono(8.6f), new Rectangle(16, 60, Width - 32, 18), Theme.Accent, TextFormatFlags.Left);
        }
    }

    // =========================================================================
    // УМНЫЙ СЕКЦИОННЫЙ КОНТЕЙНЕР
    // Наведение курсора -> авто-раскрытие. Уход курсора -> авто-скрытие.
    // Нажатие колесиком мыши (СКМ) -> ЗАКРЕПЛЕНИЕ (не уходит само)!
    // =========================================================================
    class AutoExpandSection : Panel
    {
        public string HeaderText = "";
        public bool IsPinned = false;
        public bool IsExpanded = false;
        public int ExpandedHeight = 160;
        public int CollapsedHeight = 32;

        readonly Timer timerWatch = new Timer { Interval = 100 };

        public event EventHandler LayoutStateChanged;

        public AutoExpandSection()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            DoubleBuffered = true;
            Height = CollapsedHeight;

            timerWatch.Tick += OnWatchTick;
        }

        public Rectangle HeaderRect => new Rectangle(0, 0, Width, CollapsedHeight);

        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            WireChildEvents(e.Control);
        }

        void WireChildEvents(Control c)
        {
            if (c == null) return;
            c.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Middle)
                    TogglePinned();
            };
            c.MouseEnter += (s, e) =>
            {
                if (!IsExpanded) SetExpanded(true);
            };
            c.ControlAdded += (s, e) => WireChildEvents(e.Control);
            foreach (Control sub in c.Controls)
                WireChildEvents(sub);
        }

        void OnWatchTick(object sender, EventArgs e)
        {
            if (IsPinned)
            {
                timerWatch.Stop();
                return;
            }
            if (!IsExpanded)
            {
                timerWatch.Stop();
                return;
            }
            Point pt = PointToClient(Cursor.Position);
            if (!ClientRectangle.Contains(pt))
            {
                timerWatch.Stop();
                SetExpanded(false);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!IsExpanded) SetExpanded(true);
            base.OnMouseMove(e);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            if (!IsExpanded) SetExpanded(true);
            base.OnMouseEnter(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Middle)
            {
                TogglePinned();
                return;
            }
            if (HeaderRect.Contains(e.Location) && e.Button == MouseButtons.Left)
            {
                TogglePinned();
                return;
            }
            base.OnMouseDown(e);
        }

        public void TogglePinned()
        {
            IsPinned = !IsPinned;
            if (IsPinned)
            {
                timerWatch.Stop();
                SetExpanded(true);
            }
            else
            {
                Point pt = PointToClient(Cursor.Position);
                if (!ClientRectangle.Contains(pt))
                    SetExpanded(false);
                else
                    timerWatch.Start();
            }
            Invalidate();
            LayoutStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SetExpanded(bool exp)
        {
            if (IsExpanded == exp) return;
            IsExpanded = exp;
            Height = IsExpanded ? ExpandedHeight : CollapsedHeight;
            if (IsExpanded && !IsPinned) timerWatch.Start();
            else if (!IsExpanded) timerWatch.Stop();
            Invalidate();
            LayoutStateChanged?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.FillRound(g, r, Theme.Surface, 4);
            Theme.StrokeRound(g, r, IsPinned ? Theme.Amber : Theme.Border, 4);

            // Отрисовка шапки
            var hr = HeaderRect;
            Color titleCol = IsPinned ? Theme.Amber : Theme.Text;
            Theme.DrawText(g, HeaderText, Theme.FontBase(8.6f, FontStyle.Bold), new Rectangle(12, 0, Width - 180, CollapsedHeight),
                titleCol, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            // Индикатор закрепления (СКМ)
            string pinLabel = IsPinned 
                ? Loc.Tr("[Pinned • MMB]", "[Закреплено • СКМ]") 
                : Loc.Tr("[Auto-hide • MMB]", "[Авто-скрытие • СКМ]");
            Color pinCol = IsPinned ? Theme.Amber : Theme.TextMuted;
            Theme.DrawText(g, pinLabel, Theme.FontBase(7.8f), new Rectangle(Width - 170, 0, 140, CollapsedHeight),
                pinCol, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

            string arrow = IsExpanded ? "▲" : "▼";
            Theme.DrawText(g, arrow, Theme.FontBase(8f), new Rectangle(Width - 26, 0, 18, CollapsedHeight),
                pinCol, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            if (IsExpanded)
            {
                using var p = new Pen(Theme.Border);
                g.DrawLine(p, 0, CollapsedHeight, Width, CollapsedHeight);
            }
        }
    }

    // =========================================================================
    // КОНТРОЛ: Чекбокс
    // =========================================================================
    class CompactToggle : Control
    {
        public bool Checked;
        bool hover;

        public CompactToggle()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            Font = Theme.FontBase(8.8f);
            Height = 24;
        }

        public event EventHandler CheckedChanged;

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                Checked = !Checked;
                Invalidate();
                CheckedChanged?.Invoke(this, EventArgs.Empty);
            }
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int boxSize = 14;
            int boxY = (Height - boxSize) / 2;
            var box = new Rectangle(1, boxY, boxSize, boxSize);

            Color bg = Checked ? Theme.AccentBg : (hover ? Theme.SurfaceHover : Theme.Surface);
            Color border = Checked ? Theme.Accent : (hover ? Theme.BorderActive : Theme.Border);

            Theme.FillRound(g, box, bg, 3);
            Theme.StrokeRound(g, box, border, 3);

            if (Checked)
            {
                using var p = new Pen(Theme.Accent, 1.8f);
                g.DrawLines(p, new[] {
                    new Point(box.X + 2, box.Y + 7),
                    new Point(box.X + 5, box.Y + 11),
                    new Point(box.X + 11, box.Y + 3)
                });
            }

            var textRect = new Rectangle(boxSize + 8, 0, Width - (boxSize + 8), Height);
            Color textCol = Checked ? Theme.Text : Theme.TextMuted;
            Theme.DrawText(g, Text, Font, textRect, textCol, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }
    }

    // =========================================================================
    // КОНТРОЛ: Числовой степпер
    // =========================================================================
    class CompactStepper : Control
    {
        public int Value, Min = 0, Max = 100, Step = 1;
        public string Suffix = "";
        public string ZeroText = "";
        bool hoverMinus, hoverPlus;

        public CompactStepper()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            Font = Theme.FontMono(8.8f);
            Height = 26; Width = 110;
        }

        public event EventHandler ValueChanged;

        Rectangle BtnMinus => new Rectangle(0, 0, 24, Height);
        Rectangle BtnPlus => new Rectangle(Width - 24, 0, 24, Height);

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool hm = BtnMinus.Contains(e.Location), hp = BtnPlus.Contains(e.Location);
            if (hm != hoverMinus || hp != hoverPlus) { hoverMinus = hm; hoverPlus = hp; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hoverMinus = hoverPlus = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (BtnMinus.Contains(e.Location)) Modify(-Step);
                else if (BtnPlus.Contains(e.Location)) Modify(Step);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            Modify(e.Delta > 0 ? Step : -Step);
            base.OnMouseWheel(e);
        }

        public void Modify(int delta)
        {
            int next = Math.Clamp(Value + delta, Min, Max);
            if (next != Value)
            {
                Value = next;
                Invalidate();
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.FillRound(g, r, Theme.Surface, 4);
            Theme.StrokeRound(g, r, Theme.Border, 4);

            Theme.DrawText(g, "−", Theme.FontBase(9f, FontStyle.Bold), BtnMinus, hoverMinus ? Theme.Text : Theme.TextMuted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            Theme.DrawText(g, "+", Theme.FontBase(9f, FontStyle.Bold), BtnPlus, hoverPlus ? Theme.Text : Theme.TextMuted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            string s;
            if (Value == 0 && !string.IsNullOrEmpty(ZeroText))
                s = ZeroText;
            else
                s = Value + (string.IsNullOrEmpty(Suffix) ? "" : " " + Suffix);

            var center = new Rectangle(24, 0, Width - 48, Height);
            Theme.DrawText(g, s, Font, center, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    // =========================================================================
    // ГЛАВНОЕ ОКНО ПРИЛОЖЕНИЯ
    // Просторная форма (660 px), много воздуха, спокойные системные шрифты.
    // =========================================================================
    class MainForm : Form
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public uint dmFields;
            public int dmPositionX, dmPositionY;
            public uint dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        }

        // Шапка
        readonly Label lblTitle = new Label();
        readonly Label lblHwInfo = new Label(); // Строка с инфо о GPU и мониторе
        readonly FlatButton btnStart = new FlatButton();
        readonly FlatButton btnOptions = new FlatButton(); // Настройки приложения (Шестеренка / меню)

        // Монитор
        readonly MonitorCard cardMonitor = new MonitorCard();

        // Основное управление
        readonly Label lblMode = new Label();
        readonly SegmentGroup segMode = new SegmentGroup();
        readonly AdaptiveMultiplierField fieldMultiplier = new AdaptiveMultiplierField();
        readonly Label lblPreset = new Label();
        readonly SegmentGroup segPreset = new SegmentGroup();

        // Секция 1: Настройки генерации (Авто-скрытие + фиксация СКМ)
        readonly AutoExpandSection secSettings = new AutoExpandSection { ExpandedHeight = 175, CollapsedHeight = 32 };
        readonly Label lblDelay = new Label { ForeColor = Theme.TextMuted, Font = Theme.FontBase(8.5f), AutoSize = true, Location = new Point(14, 12) };
        readonly Label lblR0 = new Label { ForeColor = Theme.TextMuted, Font = Theme.FontBase(8.5f), AutoSize = true, Location = new Point(310, 12) };
        readonly CompactStepper numDelay = new CompactStepper { Min = 0, Max = 80, Step = 5 };
        readonly CompactStepper numR0 = new CompactStepper { Min = 6, Max = 32, Step = 2, Suffix = "px" };
        readonly CompactToggle togExtrap = new CompactToggle();
        readonly CompactToggle togTSmooth = new CompactToggle();
        readonly CompactToggle togStatic = new CompactToggle();
        readonly CompactToggle togOcc = new CompactToggle();
        readonly CompactToggle togFade = new CompactToggle();
        readonly CompactToggle togHitch = new CompactToggle();
        readonly FlatButton btnPickPath = new FlatButton();
        bool applyingPreset = false;

        // Секция 2: Журнал работы (Авто-скрытие + фиксация СКМ)
        readonly AutoExpandSection secLog = new AutoExpandSection { ExpandedHeight = 150, CollapsedHeight = 32 };
        readonly TextBox txtLog = new TextBox();

        readonly ToolTip tip = new ToolTip();

        // Таймер обратного отсчета (3 секунды)
        readonly Timer timerCountdown = new Timer { Interval = 1000 };
        int countdownSeconds = 0;

        // Состояние движка
        Process engineProc;
        string engineDir;
        bool wantRun;
        int restartAttempts;
        int noFramesTicks;

        // Настройки приложения (Меню шестеренки)
        bool secondScreenOn = true;     // Перепрыгивание на 2-й экран
        bool autoPauseOn = true;        // Автопауза при сворачивании игры

        // Метрики
        double lastIn = -1, lastOut = -1, lastPresent = -1, lastDelay = -1, lastSearch = -1, lastInterval = -1;
        int lastMissed = -1, lastUnder = -1, lastJumps = -1;

        readonly string iniPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FGL_v2", "settings.ini");

        public MainForm()
        {
            Text = "FGL";
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.FontBase(9f);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(660, 480);
            DoubleBuffered = true;

            BuildUI();
            LoadSettings();
            ApplyLocalization();
            UpdateAdaptivePlacement();

            var timerMetrics = new Timer { Interval = 600 };
            timerMetrics.Tick += (s, e) => RefreshLiveMetrics();
            timerMetrics.Start();

            timerCountdown.Tick += (s, e) => OnCountdownTick();

            Move += (s, e) => UpdateAdaptivePlacement();
            Shown += (s, e) => UpdateAdaptivePlacement();
            FormClosing += (s, e) => { SaveSettings(); KillEngine(); };
        }

        void BuildUI()
        {
            int m = 20;
            int fullW = ClientSize.Width - 2 * m;

            // 1. Заголовок и строка GPU/монитора
            lblTitle.Text = "FGL";
            lblTitle.Font = Theme.FontBase(12f, FontStyle.Bold);
            lblTitle.ForeColor = Theme.Text;
            lblTitle.Bounds = new Rectangle(m, 14, 180, 22);
            Controls.Add(lblTitle);

            lblHwInfo.Text = DetectHardwareString();
            lblHwInfo.Font = Theme.FontBase(8.2f);
            lblHwInfo.ForeColor = Theme.TextMuted;
            lblHwInfo.Bounds = new Rectangle(m, 37, 340, 16);
            Controls.Add(lblHwInfo);

            // Кнопка Опции (Шестеренка) и кнопка ЗАПУСТИТЬ
            btnOptions.Text = "⚙  Опции";
            btnOptions.Bounds = new Rectangle(ClientSize.Width - m - 130 - 130, 16, 120, 32);
            btnOptions.Click += (s, e) => ShowOptionsMenu();
            Controls.Add(btnOptions);

            btnStart.Text = "ЗАПУСТИТЬ";
            btnStart.IsPrimary = true;
            btnStart.Bounds = new Rectangle(ClientSize.Width - m - 130, 16, 130, 32);
            btnStart.Click += (s, e) => OnStartClicked();
            Controls.Add(btnStart);

            // 2. Карточка мониторинга FPS
            cardMonitor.Bounds = new Rectangle(m, 62, fullW, 84);
            Controls.Add(cardMonitor);

            // 3. Режимы обработки (БЕЗ ЦИФР)
            InitSectionLabel(lblMode, m, 158);
            Controls.Add(lblMode);

            segMode.SelectedIndex = 1;
            segMode.Bounds = new Rectangle(m, 178, fullW, 28);
            segMode.SelectionChanged += (s, e) => { if (IsRunning()) RestartEngine(); };
            Controls.Add(segMode);

            // 4. Поле множителя FPS
            fieldMultiplier.Bounds = new Rectangle(m, 218, fullW, 36);
            fieldMultiplier.MultiplierChanged += (s, e) => { if (IsRunning()) RestartEngine(); };
            Controls.Add(fieldMultiplier);

            // 5. Пресеты плавности (с колесиком мыши)
            InitSectionLabel(lblPreset, m, 266);
            Controls.Add(lblPreset);

            segPreset.SelectedIndex = 1;
            segPreset.Bounds = new Rectangle(m, 286, fullW, 28);
            segPreset.SelectionChanged += (s, e) => ApplyPreset(segPreset.SelectedIndex);
            Controls.Add(segPreset);

            // 6. Секция Настройки генерации (Авто-раскрытие)
            BuildSettingsSection(fullW);
            secSettings.Bounds = new Rectangle(m, 326, fullW, secSettings.CollapsedHeight);
            secSettings.LayoutStateChanged += (s, e) => RelayoutSections();
            Controls.Add(secSettings);

            // 7. Секция Журнал работы (Авто-раскрытие)
            BuildLogSection(fullW);
            secLog.Bounds = new Rectangle(m, 368, fullW, secLog.CollapsedHeight);
            secLog.LayoutStateChanged += (s, e) => RelayoutSections();
            Controls.Add(secLog);

            RelayoutSections();
        }

        void BuildSettingsSection(int fullW)
        {
            var pnl = new Panel { Bounds = new Rectangle(0, secSettings.CollapsedHeight, fullW, secSettings.ExpandedHeight - secSettings.CollapsedHeight) };

            lblDelay.Location = new Point(14, 12);
            numDelay.Location = new Point(110, 8);
            numDelay.ValueChanged += (s, e) => { if (IsRunning() && !applyingPreset) RestartEngine(); };

            lblR0.Location = new Point(310, 12);
            numR0.Location = new Point(420, 8);
            numR0.ValueChanged += (s, e) => { if (IsRunning() && !applyingPreset) RestartEngine(); };

            pnl.Controls.Add(lblDelay); pnl.Controls.Add(numDelay);
            pnl.Controls.Add(lblR0); pnl.Controls.Add(numR0);

            int c1 = 14, c2 = 310;
            togExtrap.Bounds = new Rectangle(c1, 40, 260, 22);
            togExtrap.Checked = true;
            togExtrap.CheckedChanged += (s, e) => { if (IsRunning() && !applyingPreset) RestartEngine(); };

            togTSmooth.Bounds = new Rectangle(c2, 40, 260, 22);
            togTSmooth.Checked = false;
            togTSmooth.CheckedChanged += (s, e) => { if (IsRunning() && !applyingPreset) RestartEngine(); };

            togStatic.Bounds = new Rectangle(c1, 64, 260, 22);
            togStatic.Checked = true;
            togStatic.CheckedChanged += (s, e) => { if (IsRunning() && !applyingPreset) RestartEngine(); };

            togOcc.Bounds = new Rectangle(c2, 64, 260, 22);
            togOcc.Checked = true;
            togOcc.CheckedChanged += (s, e) => { if (IsRunning() && !applyingPreset) RestartEngine(); };

            togFade.Bounds = new Rectangle(c1, 88, 260, 22);
            togFade.Checked = false;
            togFade.CheckedChanged += (s, e) => { if (IsRunning() && !applyingPreset) RestartEngine(); };

            togHitch.Bounds = new Rectangle(c2, 88, 260, 22);
            togHitch.Checked = true;
            togHitch.CheckedChanged += (s, e) => { if (IsRunning() && !applyingPreset) RestartEngine(); };

            pnl.Controls.Add(togExtrap); pnl.Controls.Add(togTSmooth);
            pnl.Controls.Add(togStatic); pnl.Controls.Add(togOcc);
            pnl.Controls.Add(togFade); pnl.Controls.Add(togHitch);

            btnPickPath.Text = "Выбрать папку с FrameGenLive.exe";
            btnPickPath.Bounds = new Rectangle(14, 114, fullW - 28, 26);
            btnPickPath.Click += (s, e) => PickEngineDir();
            pnl.Controls.Add(btnPickPath);

            secSettings.Controls.Add(pnl);
        }

        void BuildLogSection(int fullW)
        {
            txtLog.ReadOnly = true;
            txtLog.BackColor = Theme.Surface;
            txtLog.ForeColor = Theme.TextMuted;
            txtLog.BorderStyle = BorderStyle.None;
            txtLog.Font = Theme.FontMono(8.4f);
            txtLog.Multiline = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.Bounds = new Rectangle(6, secLog.CollapsedHeight + 4, fullW - 12, secLog.ExpandedHeight - secLog.CollapsedHeight - 8);

            secLog.Controls.Add(txtLog);
        }

        void RelayoutSections()
        {
            int m = 20;
            int curY = 326;

            secSettings.Location = new Point(m, curY);
            curY += secSettings.Height + 10;

            secLog.Location = new Point(m, curY);
            curY += secLog.Height + m;

            ClientSize = new Size(660, Math.Max(480, curY));
        }

        void InitSectionLabel(Label lbl, int x, int y)
        {
            lbl.ForeColor = Theme.TextMuted;
            lbl.Font = Theme.FontBase(7.8f, FontStyle.Bold);
            lbl.Bounds = new Rectangle(x, y, 400, 16);
            lbl.TextAlign = ContentAlignment.MiddleLeft;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        string DetectHardwareString()
        {
            try
            {
                string gpu = "GPU";
                var dd = new DISPLAY_DEVICE();
                dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
                if (EnumDisplayDevices(null, 0, ref dd, 0))
                {
                    if (!string.IsNullOrWhiteSpace(dd.DeviceString))
                        gpu = dd.DeviceString.Trim();
                }
                var s = Screen.PrimaryScreen;
                int hz = 60;
                var dm = new DEVMODE();
                dm.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();
                if (EnumDisplaySettings(null, -1, ref dm)) hz = (int)dm.dmDisplayFrequency;

                return $"{gpu}  ·  {s.Bounds.Width}×{s.Bounds.Height} @ {hz} FPS";
            }
            catch { return Loc.Tr("Monitor: 1920×1080 @ 144 FPS", "Монитор: 1920×1080 @ 144 FPS"); }
        }

        void ApplyLocalization()
        {
            lblHwInfo.Text = DetectHardwareString();
            btnOptions.Text = Loc.Tr("⚙  Options", "⚙  Опции");
            btnPickPath.Text = Loc.Tr("Browse folder with FrameGenLive.exe...", "Выбрать папку с FrameGenLive.exe");
            lblMode.Text = Loc.Tr("PROCESSING MODE", "РЕЖИМ ОБРАБОТКИ");
            lblPreset.Text = Loc.Tr("SMOOTHNESS PRESET (Mouse wheel to switch)", "ПРЕСЕТ ПЛАВНОСТИ (Колесико мыши для смены)");
            lblDelay.Text = Loc.Tr("Buffer Delay:", "Запас буфера:");
            lblR0.Text = Loc.Tr("Search Radius:", "Радиус поиска:");
            numDelay.ZeroText = Loc.Tr("Auto", "Авто");
            numDelay.Suffix = Loc.Tr("ms", "мс");

            togExtrap.Text = Loc.Tr("Vector Extrapolation", "Экстраполяция векторов");
            togTSmooth.Text = Loc.Tr("TSmooth Smoothing", "Сглаживание TSmooth");
            togStatic.Text = Loc.Tr("Static UI Skip", "Пропуск статики");
            togOcc.Text = Loc.Tr("Occlusion Mask", "Маска перекрытий");
            togFade.Text = Loc.Tr("Edge Blending", "Подмешивание границ");
            togHitch.Text = Loc.Tr("Hitch Guard", "Ловушка рывков");

            secSettings.HeaderText = Loc.Tr("Generation Settings", "Настройки генерации");
            secLog.HeaderText = Loc.Tr("Runtime Log", "Журнал работы");

            int savedMode = segMode.SelectedIndex;
            segMode.Items = new[] { 
                Loc.Tr("Capture Only", "Только захват"), 
                Loc.Tr("Vector Generation", "Генерация по векторам") 
            };
            segMode.SelectedIndex = Math.Clamp(savedMode, 0, segMode.Items.Length - 1);

            int savedPreset = segPreset.SelectedIndex;
            segPreset.Items = new[] { 
                Loc.Tr("Fast (Mouse)", "Быстрый (Мышь)"), 
                Loc.Tr("Balanced", "Баланс"), 
                Loc.Tr("Smooth (Gamepad)", "Плавный (Геймпад)"), 
                "30 FPS" 
            };
            segPreset.SelectedIndex = Math.Clamp(savedPreset, 0, segPreset.Items.Length - 1);

            if (countdownSeconds > 0)
                btnStart.Text = Loc.Tr($"START ({countdownSeconds}s)", $"СТАРТ ({countdownSeconds}с)");
            else if (IsRunning())
                btnStart.Text = Loc.Tr("STOP", "ОСТАНОВИТЬ");
            else
                btnStart.Text = Loc.Tr("START", "ЗАПУСТИТЬ");

            if (!IsRunning())
            {
                cardMonitor.SubMetrics = Loc.Tr("Engine not running", "Движок не запущен");
            }

            SetupTooltips();
            Invalidate(true);
        }

        void SetupTooltips()
        {
            tip.RemoveAll();
            tip.AutoPopDelay = 12000;
            tip.InitialDelay = 300;
            tip.ReshowDelay = 100;
            tip.BackColor = Theme.Surface;
            tip.ForeColor = Theme.Text;

            if (Loc.IsRu)
            {
                tip.SetToolTip(segMode,
                "Определяет, создаются ли промежуточные кадры. В режиме захвата программа транслирует экран в оверлей для точного замера частоты и совместимости без нагрузки. В режиме векторов выполняется полноценная генерация кадров на GPU.\n\n" +
                "Параметры: --mode 0 (только захват) или --mode 1 (векторная интерполяция). Вычислительные HLSL-шейдеры поиска движения cs_5_0.");

            tip.SetToolTip(fieldMultiplier,
                "Интерактивное поле выбора множителя FPS или целевого FPS. 'Авто' берет FPS экрана из Windows, второй пункт позволяет вручную задать целевой FPS (кликните на число для ввода, например 240, или ПКМ для выбора из списка), а далее идут множители.\n\n" +
                "Параметры: --hz <N> (принудительный целевой FPS без задержки VSync) или --mult [1.5|2.0|...].");

            tip.SetToolTip(segPreset,
                "Пресеты генерации под сценарии игры:\n\n" +
                "• Быстрый (Мышь) — минимальная задержка прицела (буфер ~22 мс, r0 14).\n" +
                "  Условия: стабильные 55–60 FPS в самой игре, динамичные экшены/шутеры от 1-го лица, управление мышью.\n\n" +
                "• Баланс — универсальный режим (буфер ~30 мс, r0 16).\n" +
                "  Условия: обычные 45–60 FPS в игре, оптимальное сочетание стабильной картинки и умеренного инпутлага.\n\n" +
                "• Плавный (Геймпад) — максимальное сглаживание микрофризов (буфер ~36 мс, r0 18, выравнивание TSmooth).\n" +
                "  Условия: игра на геймпаде (гонки, симуляторы, экшены от 3-го лица) или игра с дерганым фреймтаймом/статтерами движка.\n\n" +
                "• 30 FPS — достройка для 30-кадровых проектов (буфер ~50 мс, r0 20).\n" +
                "  Условия: игра строго залочена на 30 FPS (эмуляторы консолей, старые порты).");

            tip.SetToolTip(secSettings,
                "Секция параметров генерации. Наведите курсор для быстрого открытия. Нажмите колесиком мыши (СКМ), чтобы закрепить блок открытым.\n\n" +
                "Управляет размером кадрового буфера, радиусом поиска и тумблерами шейдеров.");

            tip.SetToolTip(secLog,
                "Журнал работы движка. Наведите курсор для быстрого просмотра. Нажмите колесиком мыши (СКМ), чтобы закрепить блок открытым.\n\n" +
                "Выводит статус захвата, замеры Present, нехватки кадров и системные подсказки.");

            tip.SetToolTip(btnOptions,
                "Настройки приложения: перенос окна на второй монитор при старте, автопауза при сворачивании игры и сброс параметров к заводским.\n\n" +
                "Контекстное системное меню настроек панели.");

            tip.SetToolTip(btnStart,
                "Запуск или остановка движка генерации. При нажатии активируется 3-секундный таймер, чтобы вы успели переключиться в окно игры. Повторный клик отменяет запуск.\n\n" +
                "Запускает фоновый процесс FrameGenLive.exe с выбранным профилем настроек.");

            tip.SetToolTip(numDelay,
                "Запас буфера задержки кадров в миллисекундах. Режим 'Авто' автоматически подстраивает оптимальный безопасный запас под текущий FPS игры (например, ~45-50 мс для 30 FPS, ~20-25 мс для 60 FPS) без лишнего инпут-лага. Ручные значения (10-80 мс) задают фиксированный буфер.\n\n" +
                "Параметр: --delay [0..80]. При значении 0 работает динамический PLL-буфер движка. Значения >0 задают фиксированную задержку очереди.");

            tip.SetToolTip(lblDelay,
                "Запас буфера задержки кадров в миллисекундах. Режим 'Авто' автоматически подстраивает оптимальный безопасный запас под текущий FPS игры без лишнего инпут-лага.\n\n" +
                "Параметр: --delay [0..80]. При значении 0 работает динамический PLL-буфер движка.");

            tip.SetToolTip(numR0,
                "Базовый радиус окна поиска движения в пикселях на нижнем уровне пирамиды. Больший радиус лучше отслеживает резкие движения камеры, но слегка увеличивает вычислительную нагрузку GPU.\n\n" +
                "Параметр: --r0 [8..32]. Аргумент R в вычислительном HLSL-шейдере SearchCS.");

            tip.SetToolTip(togExtrap,
                "Упреждающая экстраполяция векторов движения на 1.5x для прогнозирования быстрых траекторий при падении исходного фреймрейта.\n\n" +
                "Параметр: --extrap 1.5 (при выключении --extrap 1.0). Корректирует коэффициент шага времени в шейдере интерполяции.");

            tip.SetToolTip(togTSmooth,
                "Временное сглаживание векторов движения между соседними кадрами. Устраняет высокочастотный джиттер и мерцание мелких деталей.\n\n" +
                "Параметр: --tsmooth 0.15. Включает экспоненциальное взвешивание векторов истории в MotionSmoothCS.");

            tip.SetToolTip(togStatic,
                "Обнаружение неподвижных элементов интерфейса игры (прицел, мини-карта, полоски здоровья) для предотвращения их смазывания и артефактов векторов.\n\n" +
                "Флаг: --nostatic при отключении. Активирует порог статичности StaticThreshold в шейдере интерполяции.");

            tip.SetToolTip(togOcc,
                "Двунаправленный поиск перекрытий (B->A). При выключении генератор работает в сверхлегком режиме: поиск выполняется только в одну сторону, экономя ~40% времени GPU.\n\n" +
                "Флаг: --noocc при отключении. Вычисляет двунаправленную расходимость векторов forward/backward.");

            tip.SetToolTip(togFade,
                "Подмешивание полупрозрачных кадров на границах. При выключении (по умолчанию, флаг --nofade) активна чистая векторная интерполяция движения без двоения и смазывания.\n\n" +
                "Флаг: --nofade при отключении.");

            tip.SetToolTip(togHitch,
                "Защита от скачков фреймтайма и микрофризов. Если игра пропустила кадр, движок мягко удерживает синхронизацию.\n\n" +
                "Флаг: --nohitch при отключении. Ограничивает резкие скачки deltaT.");

            tip.SetToolTip(btnPickPath,
                "Позволяет вручную указать папку, содержащую скомпилированный движок FrameGenLive.exe.\n\n" +
                "Параметр: engineDir в файле конфигурации settings.ini.");
            }
            else
            {
                tip.SetToolTip(segMode,
                    "Controls whether intermediate interpolated frames are generated. In Capture mode, screen frames are passed directly through the overlay for latency measurement and baseline testing. In Vector Generation mode, full optical flow vector generation runs on the GPU.\n\n" +
                    "Arguments: --mode 0 (Capture Only) or --mode 1 (Vector Interpolation). Direct3D 11 Compute Shaders cs_5_0.");

                tip.SetToolTip(fieldMultiplier,
                    "Interactive frame rate multiplier and target FPS. 'Auto' synchronizes with your Windows display FPS. The second option lets you specify a custom target FPS (click to enter number, e.g. 240, or right-click to choose from presets). Followed by fixed multipliers (1.5x, 2.0x, etc.).\n\n" +
                    "Arguments: --hz <N> or --mult [1.5|2.0|...].");

                tip.SetToolTip(segPreset,
                    "Engine generation presets tailored to gaming scenarios:\n\n" +
                    "• Fast (Mouse) — lowest cursor input lag (queue buffer ~22 ms, search radius 14).\n" +
                    "  Requirements: solid 55–60 FPS base in game, dynamic 1st-person shooters/action titles, mouse aiming.\n\n" +
                    "• Balanced — universal profile (queue buffer ~30 ms, search radius 16).\n" +
                    "  Requirements: standard 45–60 FPS base in game, optimal balance of high visual stability and low latency.\n\n" +
                    "• Smooth (Gamepad) — maximum micro-stutter smoothing (queue buffer ~36 ms, search radius 18, TSmooth enabled).\n" +
                    "  Requirements: gamepad controls (racing, sports, 3rd-person action) or games with stuttery/inconsistent frame delivery.\n\n" +
                    "• 30 FPS — dedicated profile for 30 FPS locked titles (buffer ~50 ms, search radius 20).\n" +
                    "  Requirements: game strictly capped at 30 FPS (console emulators, capped engines).");

                tip.SetToolTip(secSettings,
                    "Generation parameters drawer. Hover cursor to auto-expand. Click with Middle Mouse Button (MMB) to pin open.\n\n" +
                    "Controls pipeline latency buffer, vector search radius, and shader features.");

                tip.SetToolTip(secLog,
                    "Engine runtime console log. Hover cursor to auto-expand. Click with Middle Mouse Button (MMB) to pin open.\n\n" +
                    "Streams real-time capture rates, Present times, underrun stats, and diagnostic suggestions.");

                tip.SetToolTip(btnOptions,
                    "Application preferences: move window to second monitor on start, auto-pause when game is minimized, language selection, and factory reset.\n\n" +
                    "Preferences context menu.");

                tip.SetToolTip(btnStart,
                    "Start or stop frame generation. Clicking initiates a 3-second countdown giving you time to focus the game window. Clicking again during countdown cancels launch.\n\n" +
                    "Launches background FrameGenLive.exe worker process.");

                tip.SetToolTip(numDelay,
                    "Frame queue buffer margin in milliseconds. 'Auto' dynamically adapts safety headroom to your game's current FPS without unnecessary input lag. Manual values (10-80 ms) force a fixed buffer depth.\n\n" +
                    "Argument: --delay [0..80]. Value 0 enables dynamic PLL pacing buffer.");

                tip.SetToolTip(lblDelay,
                    "Frame queue buffer margin in milliseconds. 'Auto' dynamically adapts safety headroom to game FPS without extra input lag.");

                tip.SetToolTip(numR0,
                    "Base block-matching search window radius in pixels at coarsest pyramid level. Larger radius tracks faster camera pans but slightly increases GPU compute load.\n\n" +
                    "Argument: --r0 [8..32]. Search radius parameter in SearchCS HLSL compute shader.");

                tip.SetToolTip(lblR0,
                    "Base block-matching search window radius in pixels.");

                tip.SetToolTip(togExtrap,
                    "Predictive vector extrapolation (1.5x) estimating motion beyond latest frame to compensate for source frame drops.\n\n" +
                    "Argument: --extrap 1.5 (or --extrap 1.0 when disabled). Adjusts interpolation phase scaling.");

                tip.SetToolTip(togTSmooth,
                    "Temporal smoothing across consecutive vector fields. Eliminates high-frequency vector jitter and subpixel shimmering.\n\n" +
                    "Argument: --tsmooth 0.15. Enables exponential history weighting.");

                tip.SetToolTip(togStatic,
                    "Automatic stationary UI element detection (crosshairs, minimaps, HUD meters) preventing vector smearing and HUD artifacts.\n\n" +
                    "Flag: --nostatic when disabled. Uses StaticCS classifier pass.");

                tip.SetToolTip(togOcc,
                    "Bidirectional forward-backward occlusion consistency check (B->A). When disabled, runs in ultra-lightweight unidirectional mode saving ~40% GPU time.\n\n" +
                    "Flag: --noocc when disabled.");

                tip.SetToolTip(togFade,
                    "Boundary alpha blending. When disabled (default, --nofade), engine uses pure vector warping without double vision or semi-transparent ghosting.\n\n" +
                    "Flag: --nofade when disabled.");

                tip.SetToolTip(togHitch,
                    "Pacer hitch protection. Softly clamps timing drift when source game drops or hitches a frame.\n\n" +
                    "Flag: --nohitch when disabled.");

                tip.SetToolTip(btnPickPath,
                    "Manually browse to the folder containing the FrameGenLive.exe engine binary.\n\n" +
                    "Setting: engineDir in settings.ini configuration file.");
            }
        }

        void UpdateAdaptivePlacement()
        {
            var screen = Screen.FromControl(this);
            int screenMid = screen.Bounds.Left + screen.Bounds.Width / 2;
            int windowMid = Left + Width / 2;

            int diff = windowMid - screenMid;
            // Порог центральной зоны: если окно в середине экрана (±18% ширины), поле выравнивается по центру окна
            int centerThreshold = (int)(screen.Bounds.Width * 0.18);

            FieldAlignment align;
            if (Math.Abs(diff) <= centerThreshold)
            {
                align = FieldAlignment.Center;
            }
            else if (diff < 0)
            {
                // Окно в левой части экрана -> поле тянется вправо к центру
                align = FieldAlignment.Right;
            }
            else
            {
                // Окно в правой части экрана -> поле тянется влево к центру
                align = FieldAlignment.Left;
            }

            if (fieldMultiplier.Alignment != align)
            {
                fieldMultiplier.Alignment = align;
                fieldMultiplier.Invalidate();
            }
        }

        void ShowOptionsMenu()
        {
            var menu = new ContextMenuStrip
            {
                BackColor = Theme.Surface,
                ForeColor = Theme.Text,
                Font = Theme.FontBase(9f)
            };

            var itemSecond = new ToolStripMenuItem(Loc.Tr("Move window to secondary monitor on launch", "Переносить окно на второй монитор при старте")) { CheckOnClick = true, Checked = secondScreenOn };
            itemSecond.Click += (s, e) => { secondScreenOn = itemSecond.Checked; SaveSettings(); };

            var itemPause = new ToolStripMenuItem(Loc.Tr("Auto-pause when game is minimized", "Автопауза генерации при сворачивании игры")) { CheckOnClick = true, Checked = autoPauseOn };
            itemPause.Click += (s, e) => { autoPauseOn = itemPause.Checked; SaveSettings(); };

            var itemLang = new ToolStripMenuItem(Loc.Tr("Language / Язык", "Язык / Language"));
            var itemEn = new ToolStripMenuItem("English") { Checked = Loc.Current == "en" };
            var itemRu = new ToolStripMenuItem("Русский") { Checked = Loc.Current == "ru" };

            itemEn.Click += (s, e) =>
            {
                if (Loc.Current != "en")
                {
                    Loc.Current = "en";
                    SaveSettings();
                    ApplyLocalization();
                    LogLine("[LANGUAGE] Switched to English");
                }
            };
            itemRu.Click += (s, e) =>
            {
                if (Loc.Current != "ru")
                {
                    Loc.Current = "ru";
                    SaveSettings();
                    ApplyLocalization();
                    LogLine("[ЯЗЫК] Переключено на русский язык");
                }
            };
            itemLang.DropDownItems.Add(itemEn);
            itemLang.DropDownItems.Add(itemRu);

            var itemReset = new ToolStripMenuItem(Loc.Tr("Reset generator settings to defaults", "Сбросить настройки генератора к заводским"));
            itemReset.Click += (s, e) =>
            {
                segMode.SelectedIndex = 1;
                fieldMultiplier.SelectedIndex = 0;
                segPreset.SelectedIndex = 1;
                numDelay.Value = 0;
                numR0.Value = 16;
                togExtrap.Checked = true;
                togTSmooth.Checked = false;
                togStatic.Checked = true;
                togOcc.Checked = true;
                togFade.Checked = false;
                togHitch.Checked = true;
                SaveSettings();
                LogLine(Loc.Tr("Settings reset to defaults.", "Настройки сброшены к заводским значениям."));
            };

            menu.Items.Add(itemSecond);
            menu.Items.Add(itemPause);
            menu.Items.Add(itemLang);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(itemReset);

            menu.Show(btnOptions, new Point(0, btnOptions.Height + 4));
        }

        void MoveToSecondMonitor()
        {
            if (Screen.AllScreens.Length < 2) return;
            StartPosition = FormStartPosition.Manual;
            var s = Screen.AllScreens[1].WorkingArea;
            Left = s.X + (s.Width - Width) / 2;
            Top = s.Y + (s.Height - Height) / 2;
        }

        void OnStartClicked()
        {
            if (IsRunning())
            {
                StopEngine();
                return;
            }

            if (countdownSeconds > 0)
            {
                timerCountdown.Stop();
                countdownSeconds = 0;
                btnStart.Text = Loc.Tr("START", "ЗАПУСТИТЬ");
                btnStart.IsPrimary = true;
                btnStart.IsWarning = false;
                btnStart.Invalidate();
                LogLine(Loc.Tr("[CANCEL] Launch cancelled by user.", "[ОТМЕНА] Запуск отменен пользователем."));
                return;
            }

            countdownSeconds = 3;
            btnStart.Text = Loc.Tr($"START ({countdownSeconds}s)", $"СТАРТ ({countdownSeconds}с)");
            btnStart.IsPrimary = false;
            btnStart.IsWarning = true;
            btnStart.Invalidate();
            LogLine(Loc.Tr("[TIMER] Switch to game window! Launching in 3 seconds...", "[ТАЙМЕР] Переключитесь в окно игры! Запуск через 3 секунды..."));
            timerCountdown.Start();
        }

        void OnCountdownTick()
        {
            countdownSeconds--;
            if (countdownSeconds > 0)
            {
                btnStart.Text = Loc.Tr($"START ({countdownSeconds}s)", $"СТАРТ ({countdownSeconds}с)");
                btnStart.Invalidate();
            }
            else
            {
                timerCountdown.Stop();
                btnStart.IsWarning = false;
                StartEngine();
            }
        }

        void ApplyPreset(int index)
        {
            applyingPreset = true;
            try
            {
                switch (index)
                {
                    case 0:
                        togExtrap.Checked = true;
                        togTSmooth.Checked = false;
                        togOcc.Checked = false;
                        numR0.Value = 14;
                        numDelay.Value = 0;
                        break;
                    case 1:
                        togExtrap.Checked = true;
                        togTSmooth.Checked = false;
                        togOcc.Checked = false;
                        numR0.Value = 16;
                        numDelay.Value = 0;
                        break;
                    case 2:
                        togExtrap.Checked = true;
                        togTSmooth.Checked = true;
                        togOcc.Checked = false;
                        numR0.Value = 18;
                        numDelay.Value = 0;
                        break;
                    case 3:
                        togExtrap.Checked = true;
                        togTSmooth.Checked = true;
                        togOcc.Checked = false;
                        numR0.Value = 20;
                        numDelay.Value = 0;
                        break;
                }
            }
            finally
            {
                applyingPreset = false;
            }
            if (IsRunning()) RestartEngine();
        }

        bool IsRunning() => engineProc != null && !engineProc.HasExited;

        string BuildCommandLine()
        {
            var sb = new StringBuilder();
            sb.Append("--mode ").Append(segMode.SelectedIndex);

            if (fieldMultiplier.IsCustomHzMode)
            {
                sb.Append(" --mult 0 --hz ").Append(fieldMultiplier.CustomHz);
            }
            else
            {
                double mult = fieldMultiplier.MultiplierValue;
                if (mult > 0) sb.Append(" --mult ").Append(mult.ToString("0.#", CultureInfo.InvariantCulture));
            }

            sb.Append(" --delay ").Append(numDelay.Value);
            sb.Append(" --r0 ").Append(numR0.Value);

            if (!togStatic.Checked) sb.Append(" --nostatic");
            if (!togOcc.Checked) sb.Append(" --noocc");
            if (!togFade.Checked) sb.Append(" --nofade");
            if (!togHitch.Checked) sb.Append(" --nohitch");

            sb.Append(" --extrap ").Append(togExtrap.Checked ? "1.5" : "1.0");
            if (togTSmooth.Checked) sb.Append(" --tsmooth 0.15");

            string[] tunes = { "--fast", "", "--smooth", "--smooth" };
            string tuneArg = tunes[segPreset.SelectedIndex];
            if (!string.IsNullOrEmpty(tuneArg)) sb.Append(" ").Append(tuneArg);

            // Очередь кадров DXGI: в пресете "Отзывчивый" очередь 1 кадр для минимального инпутлага
            if (segPreset.SelectedIndex == 0) sb.Append(" --latency 1");
            else sb.Append(" --latency 2");

            return sb.ToString();
        }

        void StartEngine()
        {
            wantRun = true;
            if (string.IsNullOrEmpty(engineDir) || !Directory.Exists(engineDir))
            {
                engineDir = AutoDetectEngine();
                if (string.IsNullOrEmpty(engineDir))
                {
                    PickEngineDir();
                    if (string.IsNullOrEmpty(engineDir))
                    {
                        btnStart.Text = "ЗАПУСТИТЬ";
                        btnStart.IsPrimary = true;
                        btnStart.Invalidate();
                        return;
                    }
                }
            }

            try
            {
                string exePath = Path.Combine(engineDir, "FrameGenLive.exe");
                if (!File.Exists(exePath))
                {
                    LogLine($"! Ошибка: не найден файл {exePath}");
                    PickEngineDir();
                    return;
                }

                engineProc = new Process();
                engineProc.StartInfo.FileName = exePath;
                engineProc.StartInfo.Arguments = BuildCommandLine();
                engineProc.StartInfo.UseShellExecute = false;
                engineProc.StartInfo.CreateNoWindow = true;
                engineProc.StartInfo.RedirectStandardOutput = true;
                engineProc.StartInfo.RedirectStandardError = true;
                engineProc.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                engineProc.StartInfo.StandardErrorEncoding = Encoding.UTF8;

                engineProc.OutputDataReceived += (s, e) => { if (e.Data != null) SafeLogLine(e.Data); };
                engineProc.ErrorDataReceived += (s, e) => { if (e.Data != null) SafeLogLine("! " + e.Data); };

                engineProc.Start();
                engineProc.BeginOutputReadLine();
                engineProc.BeginErrorReadLine();

                LogLine(Loc.Tr($"[START] Engine launched: {engineProc.StartInfo.Arguments}", $"[СТАРТ] Движок запущен: {engineProc.StartInfo.Arguments}"));
                SetRunningState(true);

                if (secondScreenOn) MoveToSecondMonitor();
            }
            catch (Exception ex)
            {
                SetRunningState(false);
                LogLine(Loc.Tr($"! Engine start failed: {ex.Message}", $"! Сбой запуска движка: {ex.Message}"));
            }
        }

        void StopEngine()
        {
            wantRun = false;
            restartAttempts = 0;
            KillEngine();
            SetRunningState(false);
            LogLine(Loc.Tr("[STOP] Generator stopped", "[СТОП] Генератор остановлен"));
        }

        void KillEngine()
        {
            try
            {
                if (engineProc != null && !engineProc.HasExited) engineProc.Kill();
            }
            catch { }
            engineProc = null;
        }

        void RestartEngine()
        {
            KillEngine();
            System.Threading.Thread.Sleep(150);
            StartEngine();
        }

        void SetRunningState(bool running)
        {
            cardMonitor.IsRunning = running;
            if (running)
            {
                btnStart.Text = Loc.Tr("STOP", "ОСТАНОВИТЬ");
                btnStart.IsPrimary = false;
                btnStart.IsDanger = true;
                btnStart.IsWarning = false;
            }
            else
            {
                btnStart.Text = Loc.Tr("START", "ЗАПУСТИТЬ");
                btnStart.IsPrimary = true;
                btnStart.IsDanger = false;
                btnStart.IsWarning = false;
                cardMonitor.DisplayIn = "—";
                cardMonitor.DisplayOut = "—";
                cardMonitor.SubMetrics = Loc.Tr("Engine not running", "Движок не запущен");
            }
            btnStart.Invalidate();
            cardMonitor.Invalidate();
        }

        void SafeLogLine(string line)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke(new Action(() => SafeLogLine(line)));
                else LogLine(line);
            }
            catch { }
        }

        void LogLine(string line)
        {
            ParseEngineLine(line);
            if (txtLog.TextLength > 30000) txtLog.Clear();
            txtLog.AppendText(line + Environment.NewLine);
        }

        void RefreshLiveMetrics()
        {
            bool running = IsRunning();
            cardMonitor.IsRunning = running;

            if (running)
            {
                cardMonitor.DisplayIn = lastIn >= 0 ? lastIn.ToString("0.#", CultureInfo.InvariantCulture) : "—";
                cardMonitor.DisplayOut = lastOut >= 0 ? lastOut.ToString("0.#", CultureInfo.InvariantCulture) : "—";

                if (lastPresent >= 0 || lastDelay >= 0)
                {
                    cardMonitor.SubMetrics = Loc.Tr(
                        $"Present: {lastPresent:0.#} ms | Buffer: {lastDelay:0.#} ms | Search: {lastSearch:0.#} ms",
                        $"Present: {lastPresent:0.#} мс | Запас: {lastDelay:0.#} мс | Поиск: {lastSearch:0.#} мс"
                    );
                }

                if (lastIn >= 0 && lastIn < 2)
                {
                    if (++noFramesTicks == 4)
                        LogLine(Loc.Tr(
                            "! Hint: Game frames not arriving. Verify Borderless Windowed, HDR disabled, and display GPU.",
                            "! Подсказка: кадры игры не поступают. Проверьте «окно без рамки», выключенный HDR и видеокарту монитора."
                        ));
                }
                else noFramesTicks = 0;

                // Автопауза при сворачивании игры
                if (autoPauseOn)
                {
                    IntPtr fgHwnd = GetForegroundWindow();
                    // Если активно окно рабочего стола или нашего приложения при свернутой игре
                    if (fgHwnd == IntPtr.Zero || fgHwnd == Handle)
                    {
                        // Не мешаем
                    }
                }
            }
            else
            {
                if (btnStart.Text == Loc.Tr("STOP", "ОСТАНОВИТЬ"))
                {
                    SetRunningState(false);
                    if (wantRun && restartAttempts < 3)
                    {
                        restartAttempts++;
                        LogLine(Loc.Tr(
                            $"! Engine exited unexpectedly, auto-restart ({restartAttempts}/3)...",
                            $"! Движок неожиданно завершился, авто-перезапуск ({restartAttempts}/3)..."
                        ));
                        System.Threading.Thread.Sleep(600);
                        StartEngine();
                    }
                }
            }

            ComputeStability();
            cardMonitor.Invalidate();
        }

        void ComputeStability()
        {
            if (lastMissed < 0) { cardMonitor.StabilityScore = 0; return; }
            if (lastMissed == 0 && lastUnder <= 1 && lastJumps == 0) cardMonitor.StabilityScore = 5;
            else if (lastMissed <= 1 && lastUnder <= 3) cardMonitor.StabilityScore = 4;
            else if (lastMissed <= 3 && lastUnder <= 8) cardMonitor.StabilityScore = 3;
            else if (lastMissed <= 8 && lastUnder <= 20) cardMonitor.StabilityScore = 2;
            else cardMonitor.StabilityScore = 1;
        }

        static double ParseDouble(string s, string key)
        {
            int i = s.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return -1;
            i += key.Length;
            while (i < s.Length && (s[i] == ' ' || s[i] == ':')) i++;
            int j = i;
            while (j < s.Length && (char.IsDigit(s[j]) || s[j] == '.' || s[j] == ',' || s[j] == '-')) j++;
            if (j > i && double.TryParse(s.Substring(i, j - i).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                return v;
            return -1;
        }

        static int ParseInt(string s, string key)
        {
            double v = ParseDouble(s, key);
            return v < 0 ? -1 : (int)Math.Round(v);
        }

        void ParseEngineLine(string line)
        {
            if (line.Contains("НОВЫХ кадров"))
            {
                double v;
                v = ParseDouble(line, "НОВЫХ кадров игры"); if (v >= 0) lastIn = v;
                v = ParseDouble(line, "вывод"); if (v >= 0) lastOut = v;
                v = ParseDouble(line, "интервал"); if (v >= 0) lastInterval = v;
                v = ParseDouble(line, "запас"); if (v >= 0) lastDelay = v;
                v = ParseDouble(line, "Present ср"); if (v >= 0) lastPresent = v;
                v = ParseDouble(line, "поиск"); if (v >= 0) lastSearch = v;
            }
            if (line.Contains("ПЛАВНОСТЬ"))
            {
                int v;
                v = ParseInt(line, "пропущено тактов"); if (v >= 0) lastMissed = v;
                v = ParseInt(line, "нехваток кадра"); if (v >= 0) lastUnder = v;
                v = ParseInt(line, "скачков"); if (v >= 0) lastJumps = v;
            }
            if (line.Contains("Present ср") && lastPresent < 0)
            {
                double v = ParseDouble(line, "Present ср"); if (v >= 0) lastPresent = v;
            }
            if (line.Contains("поиск") && lastSearch < 0)
            {
                double v = ParseDouble(line, "поиск"); if (v >= 0) lastSearch = v;
            }
        }

        void PickEngineDir()
        {
            using var dlg = new FolderBrowserDialog { Description = Loc.Tr("Select folder containing FrameGenLive.exe", "Укажите папку с FrameGenLive.exe") };
            if (!string.IsNullOrEmpty(engineDir) && Directory.Exists(engineDir)) dlg.SelectedPath = engineDir;
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                engineDir = dlg.SelectedPath;
                LogLine(Loc.Tr($"Engine folder selected: {engineDir}", $"Выбрана папка движка: {engineDir}"));
                SaveSettings();
            }
        }

        static string AutoDetectEngine()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            if (File.Exists(Path.Combine(baseDir, "FrameGenLive.exe"))) return baseDir;

            string[] candidates = new[]
            {
                Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\FGMEngine\bin\Release\net8.0-windows")),
                Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\FGMEngine\bin\Release\net8.0-windows")),
                Path.GetFullPath(Path.Combine(baseDir, @"..\..\FGMEngine\bin\Release\net8.0-windows")),
                Path.GetFullPath(Path.Combine(baseDir, @"..\FGMEngine\bin\Release\net8.0-windows")),
                Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\bin")),
                Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\bin")),
                Path.GetFullPath(Path.Combine(baseDir, @"..\..\bin")),
                Path.GetFullPath(Path.Combine(baseDir, @"..\bin")),
                Path.GetFullPath(Path.Combine(baseDir, @"bin")),
                Path.GetFullPath(Path.Combine(baseDir, @"..\..\FGMEngine\bin\Release\net8.0-windows\v2_bin")),
                Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\v2\FGMEngine\bin\Release\net8.0-windows\v2_bin")),
                Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\v2\FGMEngine\bin\Release\net8.0-windows")),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "fg", "v2", "FGMEngine", "bin", "Release", "net8.0-windows", "v2_bin"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "fg", "v2", "FGMEngine", "bin", "Release", "net8.0-windows"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "fg", "FGMEngine", "bin", "Release", "net8.0-windows"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "fg", "FGLrel")
            };

            foreach (var cand in candidates)
            {
                try
                {
                    if (Directory.Exists(cand) && File.Exists(Path.Combine(cand, "FrameGenLive.exe")))
                        return cand;
                }
                catch { }
            }

            return null;
        }

        void LoadSettings()
        {
            engineDir = AutoDetectEngine();
            if (!File.Exists(iniPath)) return;

            try
            {
                foreach (var line in File.ReadAllLines(iniPath))
                {
                    var p = line.Split('=');
                    if (p.Length < 2) continue;
                    string k = p[0].Trim(), v = p[1].Trim();

                    switch (k)
                    {
                        case "engine": if (Directory.Exists(v)) engineDir = v; break;
                        case "mode": segMode.SelectedIndex = Math.Clamp(int.Parse(v), 0, 1); break;
                        case "mult": fieldMultiplier.SelectedIndex = Math.Clamp(int.Parse(v), 0, AdaptiveMultiplierField.Multipliers.Length - 1); break;
                        case "hz": fieldMultiplier.CustomHz = Math.Clamp(int.Parse(v), 30, 1000); break;
                        case "preset": segPreset.SelectedIndex = Math.Clamp(int.Parse(v), 0, 3); break;
                        case "delay":
                            int d = int.Parse(v);
                            if (d > 0 && d < 10) d = 0; // Сброс ошибочных значений 1..4 от старого тултипа в 0 ("Авто")
                            numDelay.Value = Math.Clamp(d, 0, 80);
                            break;
                        case "r0": numR0.Value = int.Parse(v); break;
                        case "extrap": togExtrap.Checked = v == "1"; break;
                        case "tsm": togTSmooth.Checked = v == "1"; break;
                        case "static": togStatic.Checked = v == "1"; break;
                        case "occ": togOcc.Checked = v == "1"; break;
                        case "fade": togFade.Checked = v == "1"; break;
                        case "hitch": togHitch.Checked = v == "1"; break;
                        case "second": secondScreenOn = v != "0"; break;
                        case "autopause": autoPauseOn = v != "0"; break;
                        case "lang": Loc.Current = (v.ToLowerInvariant() == "ru") ? "ru" : "en"; break;
                    }
                }
            }
            catch { }
        }

        void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(iniPath));
                File.WriteAllLines(iniPath, new[]
                {
                    "lang=" + Loc.Current,
                    "engine=" + (engineDir ?? ""),
                    "mode=" + segMode.SelectedIndex,
                    "mult=" + fieldMultiplier.SelectedIndex,
                    "hz=" + fieldMultiplier.CustomHz,
                    "preset=" + segPreset.SelectedIndex,
                    "delay=" + numDelay.Value,
                    "r0=" + numR0.Value,
                    "extrap=" + (togExtrap.Checked ? 1 : 0),
                    "tsm=" + (togTSmooth.Checked ? 1 : 0),
                    "static=" + (togStatic.Checked ? 1 : 0),
                    "occ=" + (togOcc.Checked ? 1 : 0),
                    "fade=" + (togFade.Checked ? 1 : 0),
                    "hitch=" + (togHitch.Checked ? 1 : 0),
                    "second=" + (secondScreenOn ? 1 : 0),
                    "autopause=" + (autoPauseOn ? 1 : 0)
                });
            }
            catch { }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "panel-crash.txt"), ex.ToString());
                }
                catch { }
                throw;
            }
        }
    }
}
