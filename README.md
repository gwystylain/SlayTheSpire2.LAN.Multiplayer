# SlayTheSpire2.LAN.Multiplayer

<img src="mod_image.png" width="256"/>

Used Local network with friends to play Slay The Spire 2 multiplayer.

## How Install
Extract the archive to get mods folder then place mods folder into the game directory.

Or subscribe to the [Steam Workshop item](https://steamcommunity.com/sharedfiles/filedetails/?id=3823079996),
and Steam keeps it up to date.
Remove any copy in the game's `mods` folder when you switch: with both installed the game keeps the higher
version, and the local copy on a tie.

Everyone in a run needs the same version. Joining with a different one fails and names the mismatch.

## Playing

- **Finding games.** The Join screen lists games hosted on your network under "Games on your network"; pick
  one to join it. Hosts answer on UDP port 33770, so a firewall that blocks it hides the game from the list,
  though typing the host's address still works.
- **Sleep and dropped connections.** If the connection drops mid-game, for example because a Steam Deck went
  to sleep, nobody is sent back to the menu. The client keeps reconnecting to the host and the run picks up
  where it left off, with a banner on screen in the meantime. This only works while the host keeps the same
  IP address, so a DHCP reservation for the host helps. Quitting from the menu still ends the game as before.
- **Unlocks.** Everything is unlocked, and every epoch on the timeline is marked revealed in the modded
  profile's progress save, so the main menu never waits for you to click through the timeline. The
  unmodded profile is untouched.

## Screenshot
![Screenshot1](Screenshot/screenshot1.png)
![Screenshot2](Screenshot/screenshot2.png)
![Screenshot3](Screenshot/screenshot3.png)
![Screenshot4](Screenshot/screenshot4.png)
![Screenshot5](Screenshot/screenshot5.png)

## Making a release

You need the .NET 9 SDK (in `~/.dotnet` or on `PATH`), Python 3, and the game's
`data_sts2_windows_x86_64` folder, which holds the `sts2.dll`, `0Harmony.dll`, `GodotSharp.dll` and
`Steamworks.NET.dll` the build references. The Linux `data_sts2_linuxbsd_x86_64` folder works too.

1. Bump the version. Set `"version"` in `mod_manifest.json` and `AssemblyVersion`/`FileVersion` in
   the `.csproj` (as `<version>.0`). The packaging script refuses to run if they disagree. Bump it on
   every release: the join check compares `id-version`, so a version bump is what turns a stale install
   into a clear error instead of a desynced run. Don't change `"id"`.
2. Commit and push.
3. Build the package:

   ```bash
   python tools/package.py --sts2-data-dir "C:/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2/data_sts2_windows_x86_64"
   ```

   This builds the DLL in Release, rebuilds the `.pck` from `localization/`, and writes to `dist/`:
   - `mods/SlayTheSpire2.LAN.Multiplayer/`: the mod folder (DLL, pck, manifest)
   - `SlayTheSpire2.LAN.Multiplayer-v<version>.zip`: that folder as `mods/...`
   - `workshop_item.vdf`: the Workshop upload config with this checkout's paths filled in
   - `release-notes.md`: install steps and upstream credit, with space for the changes

   Pass `--changes "..."` to say what changed: it leads the Workshop change note and
   `dist/release-notes.md`. You can also set `STS2_DATA_DIR` instead of passing `--sts2-data-dir`. When it finishes, it prints
   the `gh` and `steamcmd` commands for steps 4 and 5 with this build's version, commit and paths
   filled in, and warns if the tree has uncommitted changes or the commit isn't pushed.
4. Publish on GitHub. Write what changed at the top of `dist/release-notes.md`, then run the command
   the script printed, which has this form. `--target` tags the commit you built; without it the tag
   goes on the default branch.

   ```bash
   gh release create v1.7.0 dist/SlayTheSpire2.LAN.Multiplayer-v1.7.0.zip --repo gwystylain/SlayTheSpire2.LAN.Multiplayer --target "$(git rev-parse HEAD)" --title v1.7.0 --notes-file dist/release-notes.md
   ```

5. Upload to the Steam Workshop (see below).

### The pck

There is no Godot project here: the upstream author exported the `.pck` from Godot. `tools/pck_base.pck`
holds the six entries we can't regenerate (`.godot/*`, `icon.svg`, `project.binary`,
`mod_image.png.import`), taken from the 1.6.3 pck. `tools/package.py` adds every
`localization/<lang>/*.json` to it as `SlayTheSpire2.LAN.Multiplayer/localization/<lang>/...`, with line
endings normalized to LF. To edit text, edit `localization/`. To look inside a pck, run
`python tools/pck_tool.py list <file.pck>`.

### Steam Workshop

The item is public (`"visibility" "0"`; 1 would make it friends-only). Anyone who owns the game can
subscribe, and the game loads subscribed items on its own. `tools/workshop_item.vdf` is the
template: app 2868840, the staged mod folder as content, `mod_image.png` as the preview. Its title,
description (which credits upstream and links the source) and change note overwrite the Workshop page
on every upload, so edit them in the template rather than on the web.

1. Install [SteamCMD](https://developer.valvesoftware.com/wiki/SteamCMD) and run step 3 above.
2. Upload, logging in with the Steam account that owns the game. SteamCMD asks for the password and the
   Steam Guard code itself. The packaging script prints this command with the real path:

   ```bash
   steamcmd +login <your Steam account name> +workshop_build_item "<repo>/dist/workshop_item.vdf" +quit
   ```

3. `tools/workshop_item.vdf` holds the item's id, 3823079996, so each upload updates
   [the item](https://steamcommunity.com/sharedfiles/filedetails/?id=3823079996). With `publishedfileid`
   `0`, SteamCMD would create a new item instead and print its id, which then belongs in the template.
   A new item stays hidden from everyone else until its uploader accepts the Workshop legal agreement
   on its page.

Steam updates subscribed items when the client syncs, so players get each release by restarting the
game. A player still on the old version fails to join and sees the mismatch.

## License

GPL-3.0, like the upstream project this forks:
[kmyuhkyuk/SlayTheSpire2.LAN.Multiplayer](https://github.com/kmyuhkyuk/SlayTheSpire2.LAN.Multiplayer).
Releases credit upstream and link their source.
