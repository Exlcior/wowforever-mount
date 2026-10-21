# World of Warcraft Forever mount for s&box

A native s&box game mount (`wowforever`) that reads **World of Warcraft Forever** straight from the player's own
installation and converts it on demand: maps, terrain, models, creatures and NPCs, characters, items, liquids,
skies and effects. Like the game client, it downloads from Blizzard's CDN the files of the installed version that
the launcher has not written yet.

**This repository contains no game data.** You need your own installation of World of Warcraft Forever.

## What it exposes

Once installed, any s&box game that enables the `wowforever` mount (for example Sandbox) sees:

- `mount://wowforever/maps/...` scenes for the playable places;
- world models, creatures and NPCs, the playable characters, items and effects as spawnable models;
- data used by games built on top of it (creature catalogue, spells, mounts, loading screens, client fonts).

The gameplay (playing as a WoW character, character creation, menus, spells) lives in the separate gamemode
repository that depends on this mount.

## Build and install

Requirements: s&box, the .NET SDK used by s&box, Python 3, and a World of Warcraft Forever installation.

```
git clone --recurse-submodules <this repository>
python -B tools/install_wowforever_mount.py install   # with s&box closed
```

`install` builds the module and copies it with its shaders into the engine's `mount/wowforever` folder;
`remove` deletes only the file it installed.

## Optional local data

Two files are prepared locally and never published:

- `Data/spawns.txt.gz` (NPC and creature placements): `tools/mount_data/extract_spawns.py` on the CMaNGOS
  classic-db dump, which is licensed for non-commercial use only (see its COPYRIGHT.md). Without it, the world has
  no NPCs.
- `Data/ambience.json` (zone lights): `tools/mount_data/prepare_ambience.py`. Without it, lighting is neutral.

More details in [docs/mount.md](docs/mount.md) and [docs/characters.md](docs/characters.md).

## Credits

- [TACTSharp](https://github.com/wowdev/TACTSharp) (MIT) reads the local CASC storage (submodule).
- [WoWDBDefs](https://github.com/wowdev/WoWDBDefs) (MIT) field layouts, and the community listfile for file names.
- [wow.export](https://github.com/Kruithne/wow.export) (MIT) animation names.

## Licence

MIT, see [LICENSE](LICENSE). Fan project, not affiliated with or endorsed by Blizzard Entertainment.
World of Warcraft is a trademark of Blizzard Entertainment; no Blizzard content is distributed here.
