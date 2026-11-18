using System;
using System.Drawing;
using System.Drawing.Drawing2D;
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
    const string MonitorGlyph = "";
    const string GameGlyph = "";
    const string UnknownGlyph = "";
    const string ThemeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

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

    sealed class Mode
    {
        public string Name;
        public int Width, Height;
        public bool Stretch;
        public string Glyph;
        public Icon Icon;
        public MenuItem Item;
    }

    static readonly Mode[] Modes =
    {
        new Mode { Name = "Native", Width = 2560, Height = 1440, Glyph = MonitorGlyph },
        new Mode { Name = "SO2 stretched", Width = 1920, Height = 1440, Stretch = true, Glyph = GameGlyph },
    };

    static NotifyIcon tray;
    static Icon unknownIcon;
    static bool lightTaskbar;

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

    static int HighestHz(Mode mode)
    {
        int best = 0;
        var devMode = NewDevMode();
        for (int i = 0; EnumDisplaySettingsW(null, i, ref devMode); i++)
        {
            if (devMode.dmPelsWidth == mode.Width && devMode.dmPelsHeight == mode.Height && devMode.dmBitsPerPel == 32)
                best = Math.Max(best, devMode.dmDisplayFrequency);
        }
        return best;
    }

    static int Apply(Mode mode, int flags)
    {
        int hz = HighestHz(mode);
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

    static Mode Active()
    {
        var current = Current();
        return Modes.FirstOrDefault(m => m.Width == current.dmPelsWidth && m.Height == current.dmPelsHeight);
    }

    static void SwitchTo(Mode mode)
    {
        int result = Apply(mode, SaveToRegistry);
        if (result != 0)
            tray.ShowBalloonTip(4000, "Resolution Switch", string.Format("Windows refused {0} (code {1}).", mode.Name, result), ToolTipIcon.Warning);
        ShowState();
    }

    static void KeepHighestHz()
    {
        var active = Active();
        if (active != null && Current().dmDisplayFrequency < HighestHz(active)) Apply(active, SaveToRegistry);
    }

    static void ShowState()
    {
        var current = Current();
        var active = Active();
        foreach (var mode in Modes) mode.Item.Checked = mode == active;
        tray.Icon = active != null ? active.Icon : unknownIcon;
        string name = active != null ? active.Name : "Other resolution";
        tray.Text = string.Format("{0}\n{1} × {2} · {3} Hz", name, current.dmPelsWidth, current.dmPelsHeight, current.dmDisplayFrequency);
    }

    static bool TaskbarIsLight()
    {
        var value = Registry.GetValue(ThemeKey, "SystemUsesLightTheme", 0);
        return value is int && (int)value == 1;
    }

    static FontFamily IconFont()
    {
        return FontFamily.Families.FirstOrDefault(f => f.Name == "Segoe Fluent Icons") ?? new FontFamily("Segoe MDL2 Assets");
    }

    static Icon DrawGlyph(string glyph, FontFamily font, Color color)
    {
        int size = SystemInformation.SmallIconSize.Width;
        using (var bitmap = new Bitmap(size, size))
        using (var graphics = Graphics.FromImage(bitmap))
        using (var path = new GraphicsPath())
        using (var brush = new SolidBrush(color))
        using (var centre = new Matrix())
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            path.AddString(glyph, font, (int)FontStyle.Regular, size, Point.Empty, StringFormat.GenericTypographic);
            var bounds = path.GetBounds();
            centre.Translate((float)Math.Round((size - bounds.Width) / 2 - bounds.X), (float)Math.Round((size - bounds.Height) / 2 - bounds.Y));
            path.Transform(centre);
            graphics.FillPath(brush, path);
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
        var font = IconFont();
        foreach (var mode in Modes) mode.Icon = Replace(mode.Icon, DrawGlyph(mode.Glyph, font, color));
        unknownIcon = Replace(unknownIcon, DrawGlyph(UnknownGlyph, font, color));
    }

    static ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        foreach (var mode in Modes)
        {
            var target = mode;
            string label = string.Format("{0}\t{1} × {2} · {3} Hz", mode.Name, mode.Width, mode.Height, HighestHz(mode));
            mode.Item = new MenuItem(label, (s, e) => SwitchTo(target)) { RadioCheck = true };
            menu.MenuItems.Add(mode.Item);
        }
        menu.MenuItems.Add("-");
        menu.MenuItems.Add("Exit", (s, e) => { tray.Visible = false; Application.Exit(); });
        return menu;
    }

    static void Toggle(object sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) SwitchTo(Active() == Modes[0] ? Modes[1] : Modes[0]);
    }

    static void ThemeMayHaveChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (TaskbarIsLight() == lightTaskbar) return;
        FollowTheme();
        ShowState();
    }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--check")) return Modes.All(m => Apply(m, TestOnly) == 0) ? 0 : 1;
        bool firstCopy;
        using (new Mutex(true, "ResolutionSwitchTray", out firstCopy))
        {
            if (!firstCopy) return 0;
            SetProcessDPIAware();
            FollowTheme();
            tray = new NotifyIcon { ContextMenu = BuildMenu(), Visible = true };
            tray.MouseClick += Toggle;
            SystemEvents.DisplaySettingsChanged += (s, e) => { KeepHighestHz(); ShowState(); };
            SystemEvents.UserPreferenceChanged += ThemeMayHaveChanged;
            KeepHighestHz();
            ShowState();
            Application.Run();
        }
        return 0;
    }
}
