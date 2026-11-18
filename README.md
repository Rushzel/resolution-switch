# Resolution Switch

A taskbar icon for Windows that switches your screen between the resolutions you choose, always at the highest refresh rate your monitor offers for each one. One click to go from your desktop resolution to a stretched 4:3 resolution for a game and back.

## Why it exists

Players of competitive games often play at a stretched 4:3 resolution and switch back to native for everything else. Windows makes that slow:

- **Windows Settings resets the refresh rate.** Change the resolution there and a 144 Hz or 240 Hz monitor often drops to 60 Hz, so you have to fix it on a second page every time.
- **Graphics control panels are slow.** The NVIDIA, AMD and Intel panels take seconds to open and several clicks to reach the resolution and scaling options.

Resolution Switch does it in one click from the taskbar and puts the highest refresh rate back every time.

## What it does

- **Left-click** the icon to move to the next mode in your list. With two modes it is a toggle.
- **Right-click** for the menu: pick any mode, Edit modes, Start with Windows, or Exit. Each mode shows its resolution and the refresh rate it will use.
- **Hover** to see the current mode, resolution and refresh rate.
- **The icon shows the mode type:** a game controller for stretched modes, a monitor for the others, and a question mark when the screen is at a resolution that is not in your list.
- **It keeps the refresh rate up.** If anything lowers the refresh rate while the screen is at one of your modes, for example Windows Settings, it sets the highest rate again.
- **It looks like part of Windows 11.** The icons are Windows' own Segoe Fluent Icons glyphs, white on a dark taskbar and black on a light one, and the menu turns dark or light with the Windows theme.

## Download and first run

1. Download `ResolutionSwitch.exe` from the [latest release](../../releases/latest).
2. Put it in a folder of its own, for example `%LOCALAPPDATA%\Programs\Resolution Switch`, and double-click it.
3. The program is not signed, so Windows SmartScreen warns you the first time. Choose **More info**, then **Run anyway**. You can read and build the source yourself instead (see below).

The icon appears in the taskbar corner. If you do not see it, open the hidden icons arrow and drag it onto the taskbar.

On the first run it writes a modes file with up to two modes, worked out from your monitor:

- **Native**, the largest resolution your monitor lists.
- **4:3 stretched**, the native height at a 4:3 width (2560 x 1440 gives 1920 x 1440), stretched to fill the screen. It is only added when your monitor lists that resolution.

It needs .NET Framework 4.8, which comes with Windows 10 (version 1903 and later) and Windows 11.

## The modes file

Right-click the icon and choose **Edit modes**. The file opens in Notepad. It lives at `%APPDATA%\Resolution Switch\modes.txt`.

Each line is one mode:

```
Name | WIDTHxHEIGHT
Name | WIDTHxHEIGHT | stretch
```

- **Name** is what the menu and tooltip show.
- **WIDTHxHEIGHT** is the resolution, such as `1920x1080`. Your monitor or graphics driver must list it.
- **stretch** fills the screen with the image. Without it, Windows keeps the scaling it is already using.
- The order of the lines is the order a left-click goes through.

Save the file and the change applies the next time you click the icon or open the menu. Blank lines are ignored. A line it cannot read is skipped, and a notification names the line.

### Examples

A 2560 x 1440 monitor, native for work and stretched 4:3 for a game:

```
Native | 2560x1440
Stretched | 1920x1440 | stretch
```

A 1920 x 1080 monitor with two stretched resolutions to try:

```
Desktop | 1920x1080
4:3 | 1440x1080 | stretch
4:3 low | 1280x960 | stretch
```

To get back to the defaults, delete the file. The next click writes it again.

## Start with Windows

Right-click the icon and tick **Start with Windows**. It adds the program's path to your user's startup list in the registry (`HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run`, value `ResolutionSwitch`). Untick it to remove that entry. If you move the program to another folder, tick it again from the new copy.

## Limits

- **Primary display only.** It changes the main display and reads that display's resolutions. Other monitors are left alone.
- **Unsigned.** SmartScreen warns on the first run, as described above.
- **Resolutions come from your driver.** A resolution only works if Windows lists it for your monitor. Stretched 4:3 on some monitors needs scaling set to the graphics card (rather than the display) in your graphics control panel, once.
- **Native means the largest listed resolution.** If your driver offers virtual resolutions above your monitor's real one, such as NVIDIA Dynamic Super Resolution, the default Native line may pick one of those. Edit the line to your monitor's real resolution.

## Why it works this way

- **A taskbar toggle, not automation.** Switching automatically when a game opens sounds easier, but one program can run many games (an Android emulator, for example), and only some of them want a stretched screen. One click keeps you in control.
- **The highest refresh rate, every time.** The whole point is to avoid the drop to 60 Hz that Windows Settings causes, so every switch looks up the highest rate the monitor offers for that resolution, and the program puts it back if something else lowers it.
- **A text file instead of a settings window.** A settings window would be most of the code. A text file with one line per mode is easy to read, easy to share and reloads on every click, so there is nothing to restart.
- **It looks native.** The glyphs are drawn grid-fitted, so their one-pixel lines stay solid like the built-in taskbar icons. A smoothed outline split each line across two pixels and looked faint.
- **One small source file.** The whole program is `ResolutionSwitch.cs`, built with the C# compiler that ships with Windows. You can read all of it before you run it, and build it without installing anything.
- **Primary display only.** Stretched resolutions are mostly used on one gaming monitor. Supporting several displays would double the code for a case few people need.

## Build from source

You need Windows with .NET Framework 4.8. No Visual Studio or other developer tools are needed, because the compiler is already part of Windows.

```
git clone https://github.com/Rushzel/resolution-switch
cd resolution-switch
.\build.ps1
```

`build.ps1` compiles `ResolutionSwitch.exe` and runs its self-test, which checks the modes-file reader and the default modes with made-up display resolutions, without touching your screen. It exits with 0 when everything passes.

Two command-line checks are built in:

- `ResolutionSwitch.exe --self-test | Write-Output` runs the self-test.
- `ResolutionSwitch.exe --check | Write-Output` asks Windows whether every mode in your file would apply, without changing the screen, and prints the result for each. It exits with 0 when they all would.

The `| Write-Output` makes PowerShell wait for the program and show its output, because it is a Windows program rather than a console one.

GitHub Actions builds and runs the self-test on every push. Pushing a tag such as `v1.0.0` publishes a release with the program attached.

## License

[MIT](LICENSE)
