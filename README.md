# Window Groups

A small Windows app that links windows into groups (A, B, C, ...). Minimize one window in a group and the rest minimize too. Restore one and they all come back. Maximize can be linked as well.

Works on Windows 10 and 11 with nothing to install.

## Download

Get `WindowGroups.zip` from the [latest release](https://github.com/jithub1111/window-groups/releases/latest).

## Start it

Double-click `WindowGroups.exe`. You can put it anywhere (Desktop, Documents...).

The first time, Windows may show "Windows protected your PC" because the app isn't from a known publisher. Click **More info**, then **Run anyway**.

## Use it

1. Click **+ Add window** on a group, then click the window you want. It joins the group. Repeat for each window.
2. Minimize any window in the group: the others follow. Restore it: they all come back.
3. **+ New group** adds group B, C, and so on. Each group has one-click **Minimize** and **Restore** buttons for the whole group. The **•••** button lets you rename or delete it, plus grid options. The **✕** next to a window removes it from the group.
4. The green **On** button pauses or resumes linking.

## After a restart

Window Groups remembers your groups when the PC is shut down and turned back on:

- It **starts with Windows** automatically (switch it off in the tray menu if you like).
- It **reopens grouped apps** that aren't running about 8 seconds after you sign in, giving apps that start by themselves a head start. Turn this off with **Reopen my apps after restart** in the tray menu, or use **Open missing apps now** whenever you want.
- It **finds the windows again**, whether they reopen by themselves or you open them by hand. It goes by program, window type, where the window sits on screen, and its title, so it still works when titles change (like live prices).
- Each window goes **back where it was**. Grids are rebuilt in the same place and size.
- Windows that come back **minimized** (Opera does this) are recognised too, using where they sit when restored, so one click on **Restore** brings the group back.
- If it ever links the wrong window, or some stay "not open": **click the first "not open" entry, then click the right window**. It moves straight on to the next one, so you can fix a whole group in a few clicks. To fix an entry linked to the wrong window, **right-click it → Link to a different window**.
- Still not working? **⚙ → Show diagnostics log** opens the folder with `log.txt`, which records what the app saw and why it did or didn't link each window. Send that file to whoever is helping you.

Note: apps like browsers reopen their last tabs only if they're set to do so (in Chrome: Settings → On startup → Continue where you left off). Folders in File Explorer aren't reopened.

## Grids

Tick **Enable grid** on a group to lock its windows together into a grid. It works with any windows: browsers, Explorer, notes, charts, anything.

- **It doesn't take over your screen.** The grid starts in the area your windows already cover and arranges them neatly inside it (2 side by side, 4 as 2 × 2, 6 as 3 × 2...).
- **Move it anywhere:** drag any window in the grid by its title bar and the whole grid moves with it, even onto another monitor.
- **Resize the whole grid:** drag an outer edge or corner of the grid.
- **Resize inside the grid:** drag the edge between two windows and the neighbours adjust.
- **Odd numbers keep their shape:** with 3 windows in a 2 × 2 grid (or 5 in a 3 × 2), the leftover window stays the same size as the others and the spot next to it stays empty. Drag its edge into the empty spot to stretch it across; drag it back to shrink it again.
- **Swap windows:** use the **▲ ▼** arrows next to a window in the app to move it to an earlier or later spot in the grid (the numbers show each window's spot). Or hold **Shift** while dragging a window and drop it on another window's spot.
- **Minimize/restore** still works for the whole grid. Maximizing one grid window enlarges just that one; un-maximize it and it snaps back into place.
- The **•••** menu has **Even out sizes**, **Columns** (1 to 6, or automatic), **Fill the screen**, and **Re-fit around windows**.
- Adding, removing, opening or closing a window rearranges the grid inside the same area. Its position and size are saved.

Closing the window keeps the app running in the system tray (bottom-right, near the clock). Right-click the tray icon to reopen it, pause, turn maximize linking on or off, make it **Start with Windows**, or **Exit**.

Your groups are saved. When you open the app again, it finds the same windows automatically (same program and title, or the only open window of that program).

## Updates

Click the **⚙** button (or right-click the tray icon) → **Check for updates…**. The app downloads the new version, swaps itself and restarts, keeping your groups. It also checks once a day by itself and shows a notification when an update is ready (turn this off with **Check for updates automatically**).

Keep the `WindowGroups` folder somewhere you can write to, like Documents or the Desktop (not Program Files), so the app can replace itself.

## Good to know

- To link windows of apps running as Administrator, run Window Groups as Administrator too.
- A window can only be in one group at a time.

## Files

- `src/WindowGroups.cs`: the whole app (C#, .NET Framework 4, built into Windows 10/11).
- `src/app.ico`: the icon.
- `.github/workflows/release.yml`: every push to `main` with a new `Version` in `src/WindowGroups.cs` builds the exe on Windows and publishes it as a release, which the app's updater picks up.
- `release-notes.md`: the "What's new" text shown in the update prompt.
