using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class ResolutionSwitch
{
    const int EnumCurrentSettings = -1;
    const int FieldWidth = 0x80000;
    const int FieldHeight = 0x100000;
    const int FieldFrequency = 0x400000;
    const int FieldFixedOutput = 0x20000000;
    const int FixedOutputStretch = 1;
    const int SaveToRegistry = 1;
    const int TestOnly = 2;
    const int ForceDarkMenus = 2;
    const int ForceLightMenus = 3;
    const int DarkTitleBar = 20;
    const int TooltipLimit = 63;
    const string AppName = "Resolution Switch";
    const string NativeGlyph = "";
    const string StretchGlyph = "";
    const string UnknownGlyph = "";
    const string ThemeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "ResolutionSwitch";

    static readonly string ModesFile = Environment.ExpandEnvironmentVariables(@"%APPDATA%\Resolution Switch\modes.txt");
    static readonly Color DarkWindow = Color.FromArgb(32, 32, 32);
    static readonly Color DarkField = Color.FromArgb(45, 45, 45);
    static readonly Color DarkSelection = Color.FromArgb(61, 61, 61);
    static readonly Color LightSelection = Color.FromArgb(229, 229, 229);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettingsW(string device, int mode, ref DevMode devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int ChangeDisplaySettingsExW(string device, ref DevMode devMode, IntPtr window, int flags, IntPtr param);

    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr icon);

    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    static extern int SetPreferredAppMode(int mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    static extern void FlushMenuThemes();

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr window, string appName, string idList);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    sealed class Mode
    {
        public string Name;
        public int Width, Height, Hz;
        public bool Stretch;
        public string Icon;
    }

    sealed class IconChoice
    {
        public readonly string Key, Glyph, Label;

        public IconChoice(string key, string glyph, string label)
        {
            Key = key;
            Glyph = glyph;
            Label = label;
        }
    }

    static readonly IconChoice[] IconChoices =
    {
        new IconChoice("corners", NativeGlyph, "Screen corners"),
        new IconChoice("sides", StretchGlyph, "Stretched sides"),
        new IconChoice("monitor", "", "Monitor"),
        new IconChoice("laptop", "", "Laptop"),
        new IconChoice("fullscreen", "", "Full screen"),
        new IconChoice("game", "", "Game controller"),
        new IconChoice("video", "", "Video camera"),
        new IconChoice("windows", "", "Windows"),
    };

    static List<Mode> modes = new List<Mode>();
    static List<string> skippedLines = new List<string>();
    static FontFamily iconFont;
    static string lastApplied;
    static string lastWarning = "";
    static NotifyIcon tray;
    static Form settingsWindow;
    static Dictionary<string, Icon> trayIcons = new Dictionary<string, Icon>();
    static bool lightTaskbar;

    static Mode ParseMode(string line)
    {
        var parts = line.Split('|').Select(p => p.Trim()).ToArray();
        if (parts.Length < 2 || parts.Length > 5 || parts[0].Length == 0) return null;
        var size = parts[1].Split('x', 'X', '×');
        int width, height;
        if (size.Length != 2 || !int.TryParse(size[0], out width) || !int.TryParse(size[1], out height)) return null;
        if (width <= 0 || height <= 0) return null;
        var mode = new Mode { Name = parts[0], Width = width, Height = height };
        return parts.Skip(2).All(option => ReadOption(mode, option)) ? mode : null;
    }

    static bool ReadOption(Mode mode, string option)
    {
        if (option.Equals("stretch", StringComparison.OrdinalIgnoreCase) && !mode.Stretch)
        {
            mode.Stretch = true;
            return true;
        }
        if (option.StartsWith("icon ", StringComparison.OrdinalIgnoreCase))
        {
            string key = option.Substring(5).Trim().ToLowerInvariant();
            if (mode.Icon != null || !IconChoices.Any(c => c.Key == key)) return false;
            mode.Icon = key;
            return true;
        }
        int hz;
        if (mode.Hz != 0 || !option.EndsWith("hz", StringComparison.OrdinalIgnoreCase)) return false;
        if (!int.TryParse(option.Substring(0, option.Length - 2), out hz) || hz <= 0) return false;
        mode.Hz = hz;
        return true;
    }

    static List<Mode> ParseModes(string[] lines, List<string> skipped)
    {
        var parsed = new List<Mode>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0) continue;
            var mode = ParseMode(lines[i]);
            if (mode != null) parsed.Add(mode);
            else skipped.Add(string.Format("line {0}: {1}", i + 1, lines[i].Trim()));
        }
        return parsed;
    }

    static string ToLine(Mode mode)
    {
        return string.Format("{0} | {1}x{2}{3}{4}{5}", mode.Name, mode.Width, mode.Height,
            mode.Hz > 0 ? " | " + mode.Hz + " Hz" : "", mode.Stretch ? " | stretch" : "", mode.Icon != null ? " | icon " + mode.Icon : "");
    }

    static string AutomaticIcon(bool stretch)
    {
        return stretch ? "sides" : "corners";
    }

    static string GlyphFor(Mode mode)
    {
        string key = mode.Icon ?? AutomaticIcon(mode.Stretch);
        return IconChoices.First(c => c.Key == key).Glyph;
    }

    static List<Mode> DefaultModes(IList<Size> sizes)
    {
        var defaults = new List<Mode>();
        if (sizes.Count == 0) return defaults;
        var native = sizes.OrderByDescending(s => s.Width * s.Height).ThenByDescending(s => s.Width).First();
        defaults.Add(new Mode { Name = "Native", Width = native.Width, Height = native.Height });
        var fourByThree = new Size(native.Height * 4 / 3, native.Height);
        if (fourByThree != native && sizes.Contains(fourByThree))
            defaults.Add(new Mode { Name = "4:3 stretched", Width = fourByThree.Width, Height = fourByThree.Height, Stretch = true });
        return defaults;
    }

    static int TargetHz(int wanted, IList<int> listed)
    {
        if (wanted == 0) return listed.DefaultIfEmpty(0).Max();
        return listed.Contains(wanted) ? wanted : 0;
    }

    static DevMode NewDevMode()
    {
        var devMode = new DevMode();
        devMode.dmSize = (short)Marshal.SizeOf(typeof(DevMode));
        return devMode;
    }

    static DevMode Current()
    {
        var devMode = NewDevMode();
        EnumDisplaySettingsW(null, EnumCurrentSettings, ref devMode);
        return devMode;
    }

    static IEnumerable<DevMode> DisplayModes()
    {
        var devMode = NewDevMode();
        for (int i = 0; EnumDisplaySettingsW(null, i, ref devMode); i++)
            if (devMode.dmBitsPerPel == 32) yield return devMode;
    }

    static List<Size> DisplaySizes()
    {
        return DisplayModes()
            .Select(d => new Size(d.dmPelsWidth, d.dmPelsHeight))
            .Distinct()
            .OrderByDescending(s => s.Width * s.Height)
            .ThenByDescending(s => s.Width)
            .ToList();
    }

    static List<int> RatesFor(Size size)
    {
        return DisplayModes()
            .Where(d => d.dmPelsWidth == size.Width && d.dmPelsHeight == size.Height)
            .Select(d => d.dmDisplayFrequency)
            .Distinct()
            .OrderByDescending(hz => hz)
            .ToList();
    }

    static int RateFor(Mode mode)
    {
        return TargetHz(mode.Hz, RatesFor(new Size(mode.Width, mode.Height)));
    }

    static int Apply(Mode mode, int flags)
    {
        int hz = RateFor(mode);
        if (hz == 0) return -2;
        var devMode = Current();
        devMode.dmPelsWidth = mode.Width;
        devMode.dmPelsHeight = mode.Height;
        devMode.dmDisplayFrequency = hz;
        devMode.dmFields = FieldWidth | FieldHeight | FieldFrequency;
        if (mode.Stretch)
        {
            devMode.dmDisplayFixedOutput = FixedOutputStretch;
            devMode.dmFields |= FieldFixedOutput;
        }
        return ChangeDisplaySettingsExW(null, ref devMode, IntPtr.Zero, flags, IntPtr.Zero);
    }

    static void Balloon(string text)
    {
        if (tray != null) tray.ShowBalloonTip(5000, AppName, text, ToolTipIcon.Warning);
    }

    static void Warn(string warning)
    {
        if (warning.Length > 0 && warning != lastWarning) Balloon(warning);
        lastWarning = warning;
    }

    static void LoadModes()
    {
        try
        {
            if (!File.Exists(ModesFile)) WriteModes(DefaultModes(DisplaySizes()));
            var skipped = new List<string>();
            modes = ParseModes(File.ReadAllLines(ModesFile), skipped);
            skippedLines = skipped;
            Warn(skipped.Count == 0 ? "" : "Skipped in modes.txt:\n" + string.Join("\n", skipped));
        }
        catch (IOException e)
        {
            Warn("Could not read modes.txt: " + e.Message);
        }
        catch (UnauthorizedAccessException e)
        {
            Warn("Could not read modes.txt: " + e.Message);
        }
    }

    static void WriteModes(IEnumerable<Mode> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ModesFile));
        File.WriteAllLines(ModesFile, list.Select(ToLine));
    }

    static void SaveModes(List<Mode> list)
    {
        try
        {
            WriteModes(list);
            modes = new List<Mode>(list);
            lastWarning = "";
            ShowState();
        }
        catch (IOException e)
        {
            Balloon("Could not save modes.txt: " + e.Message);
        }
        catch (UnauthorizedAccessException e)
        {
            Balloon("Could not save modes.txt: " + e.Message);
        }
    }

    static Mode Active()
    {
        return ActiveIn(modes);
    }

    static Mode ActiveIn(List<Mode> list)
    {
        var current = Current();
        var matches = list.Where(m => m.Width == current.dmPelsWidth && m.Height == current.dmPelsHeight).ToList();
        return matches.FirstOrDefault(m => m.Name == lastApplied) ?? matches.FirstOrDefault();
    }

    static void SwitchTo(Mode mode)
    {
        lastApplied = mode.Name;
        int result = Apply(mode, SaveToRegistry);
        if (result != 0) Balloon(string.Format("Windows refused {0} (code {1}).", mode.Name, result));
        ShowState();
    }

    static void KeepModeRate()
    {
        var active = Active();
        if (active == null) return;
        int hz = RateFor(active);
        if (hz > 0 && Current().dmDisplayFrequency != hz) Apply(active, SaveToRegistry);
    }

    static void ShowState()
    {
        var current = Current();
        var active = Active();
        tray.Icon = trayIcons[active == null ? UnknownGlyph : GlyphFor(active)];
        string detail = string.Format("\n{0} × {1} · {2} Hz", current.dmPelsWidth, current.dmPelsHeight, current.dmDisplayFrequency);
        string name = active != null ? active.Name : "Other resolution";
        tray.Text = name.Substring(0, Math.Min(name.Length, TooltipLimit - detail.Length)) + detail;
        if (settingsWindow != null) settingsWindow.Invalidate(true);
    }

    static string RunCommand()
    {
        return "\"" + Application.ExecutablePath + "\"";
    }

    static bool StartsWithWindows()
    {
        var value = Registry.GetValue(@"HKEY_CURRENT_USER\" + RunKey, RunValue, null) as string;
        return RunCommand().Equals(value, StringComparison.OrdinalIgnoreCase);
    }

    static void ToggleStartWithWindows(object sender, EventArgs e)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (StartsWithWindows()) key.DeleteValue(RunValue, false);
            else key.SetValue(RunValue, RunCommand());
        }
    }

    static bool ThemeIsLight(string value)
    {
        var setting = Registry.GetValue(ThemeKey, value, 1);
        return !(setting is int) || (int)setting == 1;
    }

    static bool TaskbarIsLight()
    {
        return ThemeIsLight("SystemUsesLightTheme");
    }

    static bool AppsAreDark()
    {
        return !ThemeIsLight("AppsUseLightTheme");
    }

    static FontFamily IconFont()
    {
        return FontFamily.Families.FirstOrDefault(f => f.Name == "Segoe Fluent Icons") ?? new FontFamily("Segoe MDL2 Assets");
    }

    static Icon DrawGlyph(string glyph, FontFamily family, Color color)
    {
        int size = SystemInformation.SmallIconSize.Width;
        using (var bitmap = new Bitmap(size, size))
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font(family, size, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var brush = new SolidBrush(color))
        {
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.DrawString(glyph, font, brush, 0, 0, StringFormat.GenericTypographic);
            return Icon.FromHandle(bitmap.GetHicon());
        }
    }

    static Icon Replace(Icon old, Icon fresh)
    {
        if (old != null) DestroyIcon(old.Handle);
        return fresh;
    }

    static void FollowTheme()
    {
        lightTaskbar = TaskbarIsLight();
        SetPreferredAppMode(lightTaskbar ? ForceLightMenus : ForceDarkMenus);
        FlushMenuThemes();
        var color = lightTaskbar ? Color.Black : Color.White;
        if (iconFont == null) iconFont = IconFont();
        foreach (var glyph in IconChoices.Select(c => c.Glyph).Concat(new[] { UnknownGlyph }))
        {
            Icon old;
            trayIcons.TryGetValue(glyph, out old);
            trayIcons[glyph] = Replace(old, DrawGlyph(glyph, iconFont, color));
        }
    }

    static int Scaled(int pixels)
    {
        using (var graphics = Graphics.FromHwnd(IntPtr.Zero)) return (int)(pixels * graphics.DpiX / 96);
    }

    static Color HintColor()
    {
        return AppsAreDark() ? Color.FromArgb(170, 170, 170) : SystemColors.GrayText;
    }

    static IEnumerable<Control> AllControls(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var grandchild in AllControls(child)) yield return grandchild;
        }
    }

    static void ApplyTheme(Form form)
    {
        if (!AppsAreDark()) return;
        form.BackColor = DarkWindow;
        form.ForeColor = Color.White;
        form.HandleCreated += (s, e) =>
        {
            int on = 1;
            DwmSetWindowAttribute(form.Handle, DarkTitleBar, ref on, sizeof(int));
        };
        foreach (var control in AllControls(form))
        {
            bool field = control is TextBox || control is ComboBox || control is ListBox;
            if (field)
            {
                control.BackColor = DarkField;
                control.ForeColor = Color.White;
            }
            if (!field && !(control is Button)) continue;
            string theme = control is ComboBox || control is TextBox ? "DarkMode_CFD" : "DarkMode_Explorer";
            control.HandleCreated += (s, e) => SetWindowTheme(((Control)s).Handle, theme, null);
        }
    }

    static Form NewWindow(string title)
    {
        return new Form
        {
            Text = title,
            Font = SystemFonts.MessageBoxFont,
            AutoScaleMode = AutoScaleMode.None,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowIcon = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(Scaled(12)),
        };
    }

    static TableLayoutPanel Grid(int columns)
    {
        return new TableLayoutPanel { ColumnCount = columns, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
    }

    static FlowLayoutPanel Row(FlowDirection direction)
    {
        return new FlowLayoutPanel { FlowDirection = direction, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
    }

    static Label TextLabel(string text)
    {
        return new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(Scaled(3), Scaled(6), Scaled(12), Scaled(6)) };
    }

    static Label HintLabel(string text, int width)
    {
        return new Label { Text = text, AutoSize = true, MaximumSize = new Size(width, 0), ForeColor = HintColor(), Margin = new Padding(Scaled(3), Scaled(8), Scaled(3), Scaled(3)) };
    }

    static Button NewButton(string text)
    {
        return new Button { Text = text, FlatStyle = FlatStyle.System, AutoSize = true, MinimumSize = new Size(Scaled(96), Scaled(30)) };
    }

    static ComboBox DropDown()
    {
        return new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FormattingEnabled = true, Width = Scaled(220) };
    }

    static string Describe(Mode mode)
    {
        return string.Format("{0} × {1} · {2}{3}", mode.Width, mode.Height,
            mode.Hz > 0 ? mode.Hz + " Hz" : "highest refresh rate", mode.Stretch ? " · stretched" : "");
    }

    static void FillRates(ComboBox rate, Size size, int wanted)
    {
        var listed = RatesFor(size);
        rate.Tag = listed.DefaultIfEmpty(0).Max();
        rate.Items.Clear();
        rate.Items.Add(0);
        foreach (var hz in listed) rate.Items.Add(hz);
        rate.SelectedItem = listed.Contains(wanted) ? wanted : 0;
    }

    static Form ModeDialog(Mode start)
    {
        var sizes = DisplaySizes();
        var startSize = new Size(start.Width, start.Height);
        if (!sizes.Contains(startSize)) sizes.Insert(0, startSize);

        var form = NewWindow(start.Name.Length == 0 ? "Add mode" : "Edit mode");
        form.StartPosition = FormStartPosition.CenterParent;
        form.ShowInTaskbar = false;

        var name = new TextBox { Text = start.Name, Width = Scaled(220) };
        var resolution = DropDown();
        resolution.Format += (s, e) => e.Value = string.Format("{0} × {1}", ((Size)e.ListItem).Width, ((Size)e.ListItem).Height);
        foreach (var size in sizes) resolution.Items.Add(size);
        var rate = DropDown();
        rate.Format += (s, e) =>
        {
            int hz = (int)e.ListItem, highest = (int)rate.Tag;
            e.Value = hz > 0 ? hz + " Hz" : highest > 0 ? "Highest (" + highest + " Hz)" : "Highest";
        };
        resolution.SelectedIndexChanged += (s, e) => FillRates(rate, (Size)resolution.SelectedItem, rate.SelectedItem is int ? (int)rate.SelectedItem : start.Hz);
        rate.Tag = 0;
        rate.Items.Add(start.Hz);
        rate.SelectedIndex = 0;
        resolution.SelectedItem = startSize;
        var stretch = new CheckBox { Text = "Stretch to fill the screen", Checked = start.Stretch, AutoSize = true, Margin = new Padding(Scaled(3), Scaled(8), Scaled(3), Scaled(3)) };
        var picker = IconPicker(start.Icon ?? AutomaticIcon(start.Stretch));
        stretch.CheckedChanged += (s, e) =>
        {
            if (ChosenIcon(picker) == AutomaticIcon(!stretch.Checked)) SelectIcon(picker, AutomaticIcon(stretch.Checked));
        };
        var hint = HintLabel("", Scaled(300));

        var ok = NewButton("OK");
        var cancel = NewButton("Cancel");
        cancel.DialogResult = DialogResult.Cancel;
        ok.Click += (s, e) =>
        {
            string text = name.Text.Trim();
            if (text.Length == 0 || text.Contains("|"))
            {
                hint.Text = "Give the mode a name, without the | character.";
                name.Focus();
                return;
            }
            var size = (Size)resolution.SelectedItem;
            string icon = ChosenIcon(picker);
            form.Tag = new Mode { Name = text, Width = size.Width, Height = size.Height, Hz = (int)rate.SelectedItem, Stretch = stretch.Checked,
                Icon = icon == AutomaticIcon(stretch.Checked) ? null : icon };
            form.DialogResult = DialogResult.OK;
        };
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        var grid = Grid(2);
        grid.Controls.Add(TextLabel("Name"), 0, 0);
        grid.Controls.Add(name, 1, 0);
        grid.Controls.Add(TextLabel("Resolution"), 0, 1);
        grid.Controls.Add(resolution, 1, 1);
        grid.Controls.Add(TextLabel("Refresh rate"), 0, 2);
        grid.Controls.Add(rate, 1, 2);
        grid.Controls.Add(stretch, 1, 3);
        var iconLabel = TextLabel("Icon");
        iconLabel.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        iconLabel.Margin = new Padding(Scaled(3), Scaled(14), Scaled(12), Scaled(6));
        grid.Controls.Add(iconLabel, 0, 4);
        grid.Controls.Add(picker, 1, 4);
        grid.Controls.Add(hint, 0, 5);
        grid.SetColumnSpan(hint, 2);
        var buttons = Row(FlowDirection.RightToLeft);
        buttons.Anchor = AnchorStyles.Right;
        buttons.Margin = new Padding(0, Scaled(12), 0, 0);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        grid.Controls.Add(buttons, 0, 6);
        grid.SetColumnSpan(buttons, 2);
        form.Controls.Add(grid);
        ApplyTheme(form);
        return form;
    }

    static FlowLayoutPanel IconPicker(string selected)
    {
        bool dark = AppsAreDark();
        var normalText = dark ? Color.White : SystemColors.ControlText;
        int side = Scaled(34), glyphSize = Scaled(16);
        var picker = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MaximumSize = new Size(Scaled(228), 0), Margin = new Padding(Scaled(1), Scaled(6), 0, 0) };
        var name = HintLabel("", Scaled(220));
        name.Margin = new Padding(Scaled(2), Scaled(2), 0, 0);
        var tips = new ToolTip();
        var glyphFont = new Font(iconFont, glyphSize, FontStyle.Regular, GraphicsUnit.Pixel);
        picker.Disposed += (s, e) => { tips.Dispose(); glyphFont.Dispose(); };
        foreach (var choice in IconChoices)
        {
            var button = new RadioButton
            {
                Appearance = Appearance.Button,
                FlatStyle = FlatStyle.Flat,
                Tag = choice.Key,
                AccessibleName = choice.Label,
                Size = new Size(side, side),
                Margin = new Padding(Scaled(2)),
                BackColor = dark ? DarkField : SystemColors.Window,
                ForeColor = normalText,
            };
            button.FlatAppearance.BorderColor = dark ? Color.FromArgb(85, 85, 85) : Color.FromArgb(200, 200, 200);
            button.FlatAppearance.MouseOverBackColor = dark ? DarkSelection : LightSelection;
            button.FlatAppearance.CheckedBackColor = SystemColors.Highlight;
            button.CheckedChanged += (s, e) =>
            {
                button.ForeColor = button.Checked ? SystemColors.HighlightText : normalText;
                if (button.Checked) name.Text = choice.Label;
            };
            button.Paint += (s, e) =>
            {
                e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using (var brush = new SolidBrush(button.ForeColor))
                    e.Graphics.DrawString(choice.Glyph, glyphFont, brush, (side - glyphSize) / 2, (side - glyphSize) / 2, StringFormat.GenericTypographic);
            };
            tips.SetToolTip(button, choice.Label);
            picker.Controls.Add(button);
        }
        picker.SetFlowBreak(picker.Controls[picker.Controls.Count - 1], true);
        picker.Controls.Add(name);
        SelectIcon(picker, selected);
        return picker;
    }

    static string ChosenIcon(Control picker)
    {
        return (string)picker.Controls.OfType<RadioButton>().First(b => b.Checked).Tag;
    }

    static void SelectIcon(Control picker, string key)
    {
        picker.Controls.OfType<RadioButton>().First(b => (string)b.Tag == key).Checked = true;
    }

    static Mode EditMode(IWin32Window owner, Mode start)
    {
        using (var dialog = ModeDialog(start))
            return dialog.ShowDialog(owner) == DialogResult.OK ? (Mode)dialog.Tag : null;
    }

    static void DrawModeItem(ListBox box, List<Mode> list, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= list.Count) return;
        var mode = list[e.Index];
        var bounds = e.Bounds;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (var back = new SolidBrush(selected ? (AppsAreDark() ? DarkSelection : LightSelection) : box.BackColor))
            e.Graphics.FillRectangle(back, bounds);
        if (selected) e.Graphics.FillRectangle(SystemBrushes.Highlight, bounds.X + Scaled(2), bounds.Y + bounds.Height / 4, Scaled(3), bounds.Height / 2);
        int left = bounds.X + Scaled(14);
        int textLeft = left + Scaled(38);
        TextRenderer.DrawText(e.Graphics, mode.Name, box.Font, new Point(textLeft, bounds.Y + Scaled(6)), box.ForeColor);
        TextRenderer.DrawText(e.Graphics, Describe(mode), box.Font, new Point(textLeft, bounds.Y + Scaled(25)), HintColor());
        if (mode == ActiveIn(list))
            TextRenderer.DrawText(e.Graphics, "Current", box.Font, new Rectangle(bounds.X, bounds.Y + Scaled(6), bounds.Width - Scaled(10), Scaled(20)),
                AppsAreDark() ? Color.FromArgb(76, 194, 255) : SystemColors.HotTrack, TextFormatFlags.Right);
        using (var glyphFont = new Font(iconFont, Scaled(20), FontStyle.Regular, GraphicsUnit.Pixel))
        using (var glyphBrush = new SolidBrush(box.ForeColor))
        {
            e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            e.Graphics.DrawString(GlyphFor(mode), glyphFont, glyphBrush,
                left, bounds.Y + (bounds.Height - Scaled(20)) / 2, StringFormat.GenericTypographic);
        }
    }

    static Form SettingsWindow()
    {
        LoadModes();
        var list = new List<Mode>(modes);
        var form = NewWindow(AppName);
        form.StartPosition = FormStartPosition.CenterScreen;

        var box = new ListBox
        {
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = Scaled(48),
            IntegralHeight = false,
            Size = new Size(Scaled(360), Scaled(240)),
            BorderStyle = BorderStyle.FixedSingle,
        };
        box.DrawItem += (s, e) => DrawModeItem(box, list, e);

        var switchTo = NewButton("Switch to");
        var add = NewButton("Add");
        var edit = NewButton("Edit");
        var remove = NewButton("Remove");
        var up = NewButton("Move up");
        var down = NewButton("Move down");
        var close = NewButton("Close");
        close.Click += (s, e) => form.Close();
        form.CancelButton = close;
        const string orderHint = "Each left-click on the taskbar icon goes to the next mode in this list, top to bottom, then back to the first. Changes are saved straight away.";
        var hint = HintLabel(orderHint + (skippedLines.Count == 0 ? "" :
            "\n\nThese lines in modes.txt can't be read. They will be removed when you change anything here:\n" + string.Join("\n", skippedLines)), Scaled(360));

        Action showButtons = () =>
        {
            int i = box.SelectedIndex;
            switchTo.Enabled = edit.Enabled = remove.Enabled = i >= 0;
            up.Enabled = i > 0;
            down.Enabled = i >= 0 && i < list.Count - 1;
        };
        Action<int> changed = select =>
        {
            SaveModes(list);
            hint.Text = orderHint;
            box.Items.Clear();
            foreach (var mode in list) box.Items.Add(mode.Name);
            box.SelectedIndex = Math.Min(select, list.Count - 1);
            showButtons();
        };
        Action<int> move = offset =>
        {
            int from = box.SelectedIndex, to = from + offset;
            var mode = list[from];
            list.RemoveAt(from);
            list.Insert(to, mode);
            changed(to);
        };
        Action editSelected = () =>
        {
            int i = box.SelectedIndex;
            if (i < 0) return;
            var edited = EditMode(form, list[i]);
            if (edited == null) return;
            list[i] = edited;
            changed(i);
        };

        add.Click += (s, e) =>
        {
            var size = DisplaySizes().FirstOrDefault();
            var added = EditMode(form, new Mode { Name = "", Width = size.Width, Height = size.Height });
            if (added == null) return;
            list.Add(added);
            changed(list.Count - 1);
        };
        switchTo.Click += (s, e) => SwitchTo(list[box.SelectedIndex]);
        edit.Click += (s, e) => editSelected();
        box.DoubleClick += (s, e) => editSelected();
        remove.Click += (s, e) =>
        {
            int i = box.SelectedIndex;
            list.RemoveAt(i);
            changed(i);
        };
        up.Click += (s, e) => move(-1);
        down.Click += (s, e) => move(1);
        box.SelectedIndexChanged += (s, e) => showButtons();

        foreach (var mode in list) box.Items.Add(mode.Name);
        if (list.Count > 0) box.SelectedIndex = 0;
        showButtons();

        var side = Row(FlowDirection.TopDown);
        side.Margin = new Padding(Scaled(8), 0, 0, 0);
        switchTo.Margin = new Padding(Scaled(3), Scaled(3), Scaled(3), Scaled(15));
        side.Controls.AddRange(new Control[] { switchTo, add, edit, remove, up, down });
        var grid = Grid(2);
        grid.Controls.Add(box, 0, 0);
        grid.Controls.Add(side, 1, 0);
        grid.Controls.Add(hint, 0, 1);
        close.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        close.Margin = new Padding(0, Scaled(12), Scaled(3), 0);
        grid.Controls.Add(close, 1, 1);
        form.Controls.Add(grid);
        ApplyTheme(form);
        return form;
    }

    static void OpenSettings()
    {
        if (settingsWindow != null)
        {
            settingsWindow.Activate();
            return;
        }
        settingsWindow = SettingsWindow();
        settingsWindow.FormClosed += (s, e) => settingsWindow = null;
        settingsWindow.Show();
    }

    static string ModeLabel(Mode mode)
    {
        int hz = RateFor(mode);
        return string.Format("{0}\t{1} × {2} · {3}", mode.Name, mode.Width, mode.Height, hz > 0 ? hz + " Hz" : "not available");
    }

    static void FillMenu(object sender, EventArgs e)
    {
        LoadModes();
        var items = ((ContextMenu)sender).MenuItems;
        items.Clear();
        var active = Active();
        foreach (var mode in modes)
        {
            var target = mode;
            items.Add(new MenuItem(ModeLabel(mode), (s, a) => SwitchTo(target)) { RadioCheck = true, Checked = mode == active });
        }
        if (modes.Count > 0) items.Add("-");
        items.Add("Edit modes", (s, a) => OpenSettings());
        items.Add(new MenuItem("Start with Windows", ToggleStartWithWindows) { Checked = StartsWithWindows() });
        items.Add("-");
        items.Add("Exit", (s, a) => { tray.Visible = false; Application.Exit(); });
        ShowState();
    }

    static void ClickIcon(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        LoadModes();
        if (modes.Count == 0) Balloon("No modes yet. Right-click and choose Edit modes.");
        else SwitchTo(modes[(modes.IndexOf(Active()) + 1) % modes.Count]);
    }

    static void ThemeMayHaveChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (TaskbarIsLight() == lightTaskbar) return;
        FollowTheme();
        ShowState();
    }

    static int Check()
    {
        LoadModes();
        if (lastWarning.Length > 0) Console.WriteLine(lastWarning);
        bool allApply = true;
        foreach (var mode in modes)
        {
            int result = Apply(mode, TestOnly);
            Console.WriteLine("{0}: {1}", ToLine(mode), result == 0 ? "ok" : "refused, code " + result);
            allApply &= result == 0;
        }
        return allApply ? 0 : 1;
    }

    static List<Size> Sizes(params int[] widthsAndHeights)
    {
        var sizes = new List<Size>();
        for (int i = 0; i < widthsAndHeights.Length; i += 2) sizes.Add(new Size(widthsAndHeights[i], widthsAndHeights[i + 1]));
        return sizes;
    }

    static string Lines(IEnumerable<Mode> list)
    {
        return string.Join("; ", list.Select(ToLine));
    }

    static int SelfTest()
    {
        int checks = 0;
        var failures = new List<string>();
        Action<string, string, string> expect = (what, actual, wanted) =>
        {
            checks++;
            if (actual != wanted) failures.Add(string.Format("{0}: got [{1}], wanted [{2}]", what, actual, wanted));
        };

        var skipped = new List<string>();
        var parsed = ParseModes(new[]
        {
            "Native | 2560x1440",
            "",
            "   ",
            " SO2 stretched |1920 X 1440| Stretch ",
            "Wide | 3440×1440",
            "no size",
            "Zero | 0x1440",
            "Squash | 1920x1440 | squash",
            "Half | 1920x",
            " | 1920x1440",
            "Extra | 800x600 | stretch | 60 Hz | more",
            "Words | widexhigh",
            "Fast | 1920x1080 | 144 Hz",
            "Both | 1920x1440 | stretch | 240hz",
            "Twice | 1920x1440 | stretch | stretch",
            "Two rates | 1920x1080 | 60 Hz | 144 Hz",
            "No rate | 1920x1080 | 0 Hz",
            "Bad rate | 1920x1080 | fast Hz",
            "Game | 1920x1080 | Icon Game",
            "All | 1920x1440 | icon laptop | 144 Hz | stretch",
            "Bad icon | 1920x1080 | icon rocket",
            "Two icons | 1920x1080 | icon game | icon laptop",
        }, skipped);
        expect("good lines parse", Lines(parsed),
            "Native | 2560x1440; SO2 stretched | 1920x1440 | stretch; Wide | 3440x1440; Fast | 1920x1080 | 144 Hz; Both | 1920x1440 | 240 Hz | stretch; " +
            "Game | 1920x1080 | icon game; All | 1920x1440 | 144 Hz | stretch | icon laptop");
        expect("bad lines are named", string.Join(", ", skipped.Select(s => s.Substring(0, s.IndexOf(':')))),
            "line 6, line 7, line 8, line 9, line 10, line 11, line 12, line 15, line 16, line 17, line 18, line 21, line 22");
        expect("automatic icons", GlyphFor(parsed[0]) + GlyphFor(parsed[1]), NativeGlyph + StretchGlyph);
        expect("chosen icon wins over stretch", GlyphFor(parsed[6]), "");
        expect("skipped line keeps its text", skipped.FirstOrDefault(), "line 6: no size");
        expect("empty file", Lines(ParseModes(new string[0], new List<string>())), "");

        var monitor = Sizes(2560, 1440, 1920, 1440, 1920, 1080, 1024, 768);
        expect("native and 4:3 stretched", Lines(DefaultModes(monitor)), "Native | 2560x1440; 4:3 stretched | 1920x1440 | stretch");
        expect("order does not matter", Lines(DefaultModes(Sizes(1920, 1440, 1024, 768, 2560, 1440))), "Native | 2560x1440; 4:3 stretched | 1920x1440 | stretch");
        expect("no 4:3 size listed", Lines(DefaultModes(Sizes(1920, 1080, 1280, 720))), "Native | 1920x1080");
        expect("largest by area", Lines(DefaultModes(Sizes(1280, 720, 3840, 2160, 2880, 2160))), "Native | 3840x2160; 4:3 stretched | 2880x2160 | stretch");
        expect("native already 4:3", Lines(DefaultModes(Sizes(1600, 1200, 1024, 768))), "Native | 1600x1200");
        expect("no display sizes", Lines(DefaultModes(Sizes())), "");

        var roundTripSkipped = new List<string>();
        var written = parsed.Select(ToLine).ToArray();
        expect("written lines parse back", Lines(ParseModes(written, roundTripSkipped)) + roundTripSkipped.Count, Lines(parsed) + 0);

        var rates = new List<int> { 240, 144, 60 };
        expect("highest rate by default", TargetHz(0, rates).ToString(), "240");
        expect("chosen rate when listed", TargetHz(144, rates).ToString(), "144");
        expect("chosen rate not listed", TargetHz(165, rates).ToString(), "0");
        expect("no rates listed", TargetHz(0, new List<int>()).ToString(), "0");

        foreach (var failure in failures) Console.WriteLine("FAIL " + failure);
        Console.WriteLine("{0} of {1} self-test checks passed.", checks - failures.Count, checks);
        return failures.Count == 0 ? 0 : 1;
    }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--self-test")) return SelfTest();
        if (args.Contains("--check")) return Check();
        bool firstCopy;
        using (new Mutex(true, "ResolutionSwitchTray", out firstCopy))
        {
            if (!firstCopy) return 0;
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            FollowTheme();
            tray = new NotifyIcon { Icon = trayIcons[UnknownGlyph], ContextMenu = new ContextMenu(), Visible = true };
            tray.ContextMenu.Popup += FillMenu;
            tray.MouseClick += ClickIcon;
            SystemEvents.DisplaySettingsChanged += (s, e) => { KeepModeRate(); ShowState(); };
            SystemEvents.UserPreferenceChanged += ThemeMayHaveChanged;
            LoadModes();
            KeepModeRate();
            ShowState();
            Application.Run();
        }
        return 0;
    }
}
