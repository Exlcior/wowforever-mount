"""Zone ambience: light positions from public Classic Light.db2, colours from Forever.

Approved exception (2026-10-17): Forever's Light.db2 has encrypted sections without a public
key, so only the positions and radii come from Classic 1.15.9.70003. Every LightParams entry
they reference exists in Forever, and the shared LightData rows are byte-identical; all colours,
fog values and time keys below are read from Forever's own tables.

Usage: python -B tools/mount_data/prepare_ambience.py
"""
import csv, json, os, sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
CLASSIC = os.path.join(ROOT, 'source', 'wow_metadata', 'classic-1.15.9.70003')
TABLES = os.path.join(ROOT, 'tools', 'mount_data', 'tables')
OUT = os.path.join(ROOT, 'source', 'mount', 'wowforever', 'Data', 'ambience.json')  # embedded in the mount DLL
COLORS = ['DirectColor', 'AmbientColor', 'SkyTopColor', 'SkyMiddleColor', 'SkyBand1Color', 'SkyBand2Color',
          'SkySmogColor', 'SkyFogColor', 'SunColor', 'OceanCloseColor', 'OceanFarColor', 'RiverCloseColor', 'RiverFarColor']


def rgb(value):
    v = int(value) & 0xFFFFFF
    return [round(((v >> 16) & 255) / 255, 4), round(((v >> 8) & 255) / 255, 4), round((v & 255) / 255, 4)]


def main():
    lights = list(csv.DictReader(open(os.path.join(CLASSIC, 'Light.csv'), encoding='utf-8')))
    data = json.load(open(os.path.join(TABLES, 'LightData.json'), encoding='utf-8'))
    params = {r['ID'] for r in json.load(open(os.path.join(TABLES, 'LightParams.json'), encoding='utf-8'))}
    catalog = json.load(open(os.path.join(ROOT, 'archive', 'converted-assets-2026-10-16', 'wow', 'world.json'), encoding='utf-8'))  # titles of the earlier catalogue
    map_ids = {c['Id']: 0 if c['Id'] == 'eastern_kingdoms' else 1 if c['Id'] == 'kalimdor' else int(c['Id'].split('_')[1])
               for c in catalog['Continents']}
    errors, continents, used = [], {}, set()
    for continent, map_id in map_ids.items():
        rows = [r for r in lights if int(r['ContinentID']) == map_id]
        if not rows:
            continue
        entry = {'Default': None, 'Lights': []}
        for r in rows:
            param = int(r['LightParamsID_0'])  # clear weather, above water
            if param not in params:
                errors.append({'light': r['ID'], 'missingParam': param})
                continue
            used.add(param)
            x, y, z = (float(r[f'GameCoords_{i}']) for i in range(3))
            inner, outer = float(r['GameFalloffStart']), float(r['GameFalloffEnd'])
            if x == 0 and y == 0 and z == 0 and outer == 0:
                entry['Default'] = param  # the map-wide light
            else:
                entry['Lights'].append({'Id': int(r['ID']), 'Position': [x, y, z], 'Inner': inner, 'Outer': max(outer, inner + 1), 'Param': param})
        continents[continent] = entry
    keys = {}
    for r in sorted(data, key=lambda r: (r['LightParamID'], r['Time'])):
        if r['LightParamID'] not in used:
            continue
        key = {'Time': r['Time'] / 2880 * 24, 'FogEnd': r['FogEnd'] / 36, 'FogScaler': r['FogScaler']}
        key.update({c.replace('Color', ''): rgb(r[c]) for c in COLORS})
        keys.setdefault(str(r['LightParamID']), []).append(key)
    missing = sorted(p for p in used if str(p) not in keys)
    errors += [{'paramWithoutData': p} for p in missing]
    result = {'SchemaVersion': 1,
              'Source': {'Positions': 'Classic 1.15.9.70003 Light.db2 (public, wago.tools)', 'Colours': 'Forever 1.60.1.70425 LightData/LightParams',
                         'Weather': 'clear (LightParamsID_0)', 'TimeUnit': 'hours'},
              'Continents': continents, 'Params': keys}
    tmp = OUT + '.tmp'
    json.dump(result, open(tmp, 'w', encoding='utf-8'), ensure_ascii=False, separators=(',', ':'))
    os.replace(tmp, OUT)
    report = {'continents': len(continents), 'lights': sum(len(c['Lights']) for c in continents.values()),
              'params': len(keys), 'withoutDefault': [k for k, c in continents.items() if c['Default'] is None], 'errors': errors}
    print(json.dumps(report))
    return 1 if errors else 0


if __name__ == '__main__':
    sys.exit(main())
