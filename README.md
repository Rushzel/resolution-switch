# Resolution Switch

A taskbar icon that switches the monitor between two modes, always at the highest refresh rate the monitor offers for that resolution (240 Hz on the ASUS):

| Icon | Mode | Resolution |
|------|------|------------|
| Monitor | Native, for work and other games | 2560 x 1440 |
| Game controller | SO2 stretched, for Standoff 2 in MuMu | 1920 x 1440, stretched to fill |

- **Left-click** the icon to toggle between the two.
- **Right-click** for the menu: pick a mode, or Exit.
- Hover to see the current resolution and refresh rate. A question mark in a circle means some other resolution is set.
- The icons are Windows 11's own Segoe Fluent Icons glyphs, white on a dark taskbar and black on a light one, and the menu turns dark or light to match. It switches by itself when you change the Windows theme.
- If the refresh rate drops below the highest (Windows Settings sets 60 Hz when you change resolution there), it puts it back for either of the two resolutions.

It starts when you sign in (Task Manager → Startup apps → ResolutionSwitch turns that off).

## Change or rebuild

The two modes are at the top of `ResolutionSwitch.cs` (`Modes`). After editing, close the app from its menu and rebuild in PowerShell from this folder:

```
& C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /codepage:65001 /target:winexe /optimize+ /out:ResolutionSwitch.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll ResolutionSwitch.cs
.\ResolutionSwitch.exe --check; $LASTEXITCODE
```

`--check` asks Windows whether both modes would work without changing the screen; `0` means yes.
