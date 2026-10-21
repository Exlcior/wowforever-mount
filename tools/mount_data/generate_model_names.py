"""Names of the client's world, creature and character models for the mount's browsable tree (Sandbox spawn menu, asset browser).

Reads the community listfile (tools/mount_data/listfile.sqlite) and writes every world M2 and WMO root
file, creature and character model, loading screen, character creation screen (glue models and interface
textures) as "FileDataID;path" lines, gzip-compressed, to source/mount/wowforever/Data/model_names.txt.gz.
Only names are written: the mount registers the files the player's installation actually holds.
Usage: python -B tools/mount_data/generate_model_names.py
"""
import gzip, os, re, sqlite3

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, 'source', 'mount', 'wowforever', 'Data', 'model_names.txt.gz')


def main():
    db = sqlite3.connect(os.path.join(ROOT, 'tools', 'mount_data', 'listfile.sqlite'))
    rows = db.execute("select id, name from files where (name like 'world/%' and (name like '%.m2' or name like '%.wmo')) or (name like 'creature/%' and name like '%.m2') or (name like 'character/%' and name like '%.m2') or (name like 'interface/glues/loadingscreens/%' and name like '%.blp') or ((name like 'interface/glues/common/%' or name like 'interface/glues/charactercreate/%') and name like '%.blp') or (name like 'interface/glues/models/%' and name like '%.m2') order by name").fetchall()
    # WMO group files (name_000.wmo, name_000_lod1.wmo) belong to their root file.
    rows = [(i, n) for i, n in rows if not re.search(r'_\d{3}(_lod\d)?\.wmo$', n)]
    data = '\n'.join(f'{i};{n}' for i, n in rows).encode('utf-8')
    with gzip.open(OUT, 'wb', compresslevel=9) as f: f.write(data)
    print({'models': len(rows), 'bytes': os.path.getsize(OUT)})


if __name__ == '__main__':
    main()
