using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    const int TooltipLimit = 63;
    const string AppName = "Resolution Switch";
    const string MonitorGlyph = "";
    const string GameGlyph = "";
    const string UnknownGlyph = "";
    const string ThemeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "ResolutionSwitch";

    static readonly string ModesFile = Environment.ExpandEnvironmentVariables(@"%APPDATA%\Resolution Switch\modes.txt");

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
    }

    static List<Mode> modes = new List<Mode>();
    static string lastApplied;
    static string lastWarning = "";
    static NotifyIcon tray;
    static Icon monitorIcon, gameIcon, unknownIcon;
    static bool lightTaskbar;

    static Mode ParseMode(string line)
    {
        var parts = line.Split('|').Select(p => p.Trim()).ToArray();
        if (parts.Length < 2 || parts.Length > 3 || parts[0].Length == 0) return null;
        if (parts.Length == 3 && !parts[2].Equals("stretch", StringComparison.OrdinalIgnoreCase)) return null;
        var size = parts[1].Split('x', 'X', '×');
        int width, height;
        if (size.Length != 2 || !int.TryParse(size[0], out width) || !int.TryParse(size[1], out height)) return null;
        if (width <= 0 || height <= 0) return null;
        return new Mode { Name = parts[0], Width = width, Height = height, Stretch = parts.Length == 3 };
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
        return string.Format("{0} | {1}x{2}{3}", mode.Name, mode.Width, mode.Height, mode.Stretch ? " | stretch" : "");
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

    static int HighestHz(Mode mode)
    {
        return DisplayModes()
            .Where(d => d.dmPelsWidth == mode.Width && d.dmPelsHeight == mode.Height)
            .Select(d => d.dmDisplayFrequency)
            .DefaultIfEmpty(0)
            .Max();
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
            if (!File.Exists(ModesFile))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ModesFile));
                var sizes = DisplayModes().Select(d => new Size(d.dmPelsWidth, d.dmPelsHeight)).Distinct().ToList();
                File.WriteAllLines(ModesFile, DefaultModes(sizes).Select(ToLine));
            }
            var skipped = new List<string>();
            modes = ParseModes(File.ReadAllLines(ModesFile), skipped);
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

    static Mode Active()
    {
        var current = Current();
        var matches = modes.Where(m => m.Width == current.dmPelsWidth && m.Height == current.dmPelsHeight).ToList();
        return matches.FirstOrDefault(m => m.Name == lastApplied) ?? matches.FirstOrDefault();
    }

    static void SwitchTo(Mode mode)
    {
        lastApplied = mode.Name;
        int result = Apply(mode, SaveToRegistry);
        if (result != 0) Balloon(string.Format("Windows refused {0} (code {1}).", mode.Name, result));
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
        tray.Icon = active == null ? unknownIcon : active.Stretch ? gameIcon : monitorIcon;
        string detail = string.Format("\n{0} × {1} · {2} Hz", current.dmPelsWidth, current.dmPelsHeight, current.dmDisplayFrequency);
        string name = active != null ? active.Name : "Other resolution";
        tray.Text = name.Substring(0, Math.Min(name.Length, TooltipLimit - detail.Length)) + detail;
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

    static bool TaskbarIsLight()
    {
        var value = Registry.GetValue(ThemeKey, "SystemUsesLightTheme", 0);
        return value is int && (int)value == 1;
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
        var font = IconFont();
        monitorIcon = Replace(monitorIcon, DrawGlyph(MonitorGlyph, font, color));
        gameIcon = Replace(gameIcon, DrawGlyph(GameGlyph, font, color));
        unknownIcon = Replace(unknownIcon, DrawGlyph(UnknownGlyph, font, color));
    }

    static string ModeLabel(Mode mode)
    {
        int hz = HighestHz(mode);
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
        items.Add("Edit modes", (s, a) => Process.Start("notepad.exe", "\"" + ModesFile + "\""));
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
            "Extra | 800x600 | stretch | more",
            "Words | widexhigh",
        }, skipped);
        expect("good lines parse", Lines(parsed), "Native | 2560x1440; SO2 stretched | 1920x1440 | stretch; Wide | 3440x1440");
        expect("bad lines are named", string.Join(", ", skipped.Select(s => s.Substring(0, s.IndexOf(':')))), "line 6, line 7, line 8, line 9, line 10, line 11, line 12");
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
        var roundTrip = ParseModes(DefaultModes(monitor).Select(ToLine).ToArray(), roundTripSkipped);
        expect("defaults parse back", Lines(roundTrip) + roundTripSkipped.Count, Lines(DefaultModes(monitor)) + 0);

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
            FollowTheme();
            tray = new NotifyIcon { Icon = unknownIcon, ContextMenu = new ContextMenu(), Visible = true };
            tray.ContextMenu.Popup += FillMenu;
            tray.MouseClick += ClickIcon;
            SystemEvents.DisplaySettingsChanged += (s, e) => { KeepHighestHz(); ShowState(); };
            SystemEvents.UserPreferenceChanged += ThemeMayHaveChanged;
            LoadModes();
            KeepHighestHz();
            ShowState();
            Application.Run();
        }
        return 0;
    }
}
