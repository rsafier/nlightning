#!/usr/bin/env python3
"""Check the solution configuration mappings in NLightning.sln (NL-172).

For every C# project and every solution configuration it maps, the project configuration must:
  * keep the Debug/Release flavour of the solution configuration, and
  * use the same-named configuration when the project declares it in <Configurations>
    (so *.Native / *.Wasm builds never silently fall back to Debug or cross over to another backend).

Usage: python3 scripts/check-sln-configs.py [path/to/NLightning.sln]
Exits 1 and lists every bad mapping when a check fails.
"""
import os
import re
import sys

sln_path = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), '..', 'NLightning.sln')
sln_dir = os.path.dirname(os.path.abspath(sln_path))
sln = open(sln_path, encoding='utf-8-sig').read()

projects = {
    guid: (name, path.replace('\\', '/'))
    for name, path, guid in re.findall(
        r'Project\("\{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC\}"\) = "([^"]+)", "([^"]+)", "\{([0-9A-Fa-f-]+)\}"', sln)
}

declared = {}
for guid, (_, path) in projects.items():
    csproj = open(os.path.join(sln_dir, path), encoding='utf-8-sig').read()
    match = re.search(r'<Configurations>(.*?)</Configurations>', csproj)
    declared[guid] = set(c.strip() for c in match.group(1).split(';')) if match else {'Debug', 'Release'}

errors = []
for guid, sln_cfg, kind, proj_cfg in re.findall(
        r'\{([0-9A-Fa-f-]+)\}\.([^|]+)\|Any CPU\.(ActiveCfg|Build\.0) = ([^|\r\n]+)\|', sln):
    if guid not in projects:
        continue
    name = projects[guid][0]
    if proj_cfg.split('.')[0] != sln_cfg.split('.')[0]:
        errors.append(f'{name}: {sln_cfg}.{kind} maps to {proj_cfg} (Debug/Release flavour differs)')
    elif sln_cfg in declared[guid] and proj_cfg != sln_cfg:
        errors.append(f'{name}: {sln_cfg}.{kind} maps to {proj_cfg} but the project declares {sln_cfg}')
    elif '.' in proj_cfg and proj_cfg != sln_cfg:
        errors.append(f'{name}: {sln_cfg}.{kind} maps to {proj_cfg} (different crypto backend)')

for error in errors:
    print(error)
if errors:
    sys.exit(1)
print(f'OK: {len(projects)} projects, solution configuration mappings are consistent.')
