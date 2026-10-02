# Ultrawide Maximize

A tiny Windows tray utility that makes the **maximize button** behave better on super-ultrawide (32:9) monitors.

On a normal 16:9 monitor, maximize fills the screen, which is usually what you want. On a 32:9 monitor, a fully maximized window is often too wide. Ultrawide Maximize changes maximize on ultrawide monitors only: the window becomes a **centered 16:9 window** at full height instead.

| Action | Normal monitor | Ultrawide monitor |
| --- | --- | --- |
| Click the maximize button | Full maximize | Centered 16:9 |
| Win + Up | Full maximize | Centered 16:9 |
| Double-click the title bar | Full maximize | **Full-width maximize** |
| Hold Shift while maximizing | Full maximize | **Full-width maximize** |
| Maximize a window that is already centered | — | Back to its previous size and position |

A monitor counts as "ultrawide" when its aspect ratio is wider than about 2.2:1 (21:9 and 32:9 monitors both qualify). Other monitors aren't affected.

## Install

1. Download `UltrawideMaximize.exe` from the [latest release](../../releases/latest).
2. Put it somewhere permanent (for example `C:\Tools\UltrawideMaximize\`) and run it.
3. A small icon appears in the system tray. Right-click it to:
   - **Enabled**: pause or resume the utility
   - **Start with Windows**: run it automatically when you sign in
   - **Exit**

The program isn't code-signed, so Windows SmartScreen may warn you the first time you run it. Click **More info → Run anyway**, or build it yourself from source (below).

**Requirements:** Windows 10 or 11. It uses the .NET Framework 4.x that ships with Windows, so there's nothing else to install.

## Build from source

The whole program is one C# file. Windows already includes a compiler for it, so you don't need Visual Studio:

```bat
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /out:UltrawideMaximize.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll UltrawideMaximize.cs
```

## How it works

- A `SetWinEventHook` listener watches for windows changing size or position. When a window becomes maximized on an ultrawide monitor, it's restored and placed in the centered 16:9 area (full monitor height × 16/9 wide, clipped to the taskbar work area). The invisible resize borders of Windows 10/11 are compensated for, so the visible frame lines up exactly.
- A low-level mouse hook detects double-clicks. If a window maximizes within half a second of a double-click, it's left at full width.
- Windows that were already maximized when the utility started are left alone.
- It runs as a normal user process. It doesn't inject into other programs, needs no admin rights, and makes no network connections.

## Known limitations

- Windows draws the full maximize first, so you may see a brief jump as the window moves into the centered position.
- Windows running as administrator (Task Manager, for example) can't be moved by a non-admin program, so they maximize normally. Running Ultrawide Maximize as administrator would cover those too.
- A few programs with unusual custom title bars may not behave perfectly.
