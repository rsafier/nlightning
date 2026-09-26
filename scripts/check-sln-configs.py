#!/usr/bin/env python3
"""Check the solution configuration mappings in NLightning.sln (NL-172) and test project discovery (NL-167).

For every C# project and every solution configuration it maps, the project configuration must:
  * keep the Debug/Release flavour of the solution configuration, and
  * use the same-named configuration when the project declares it in <Configurations>
    (so *.Native / *.Wasm builds never silently fall back to Debug or cross over to another backend).
Every solution configuration a project declares in <Configurations> must have an ActiveCfg mapping
(an unmapped configuration only shows up as the MSB4121 warning the build suppresses).
Every test project (a *.Tests project, or one with <IsTestProject>true</IsTestProject>) must set
IsTestProject and reference xunit.runner.visualstudio, or `dotnet test` silently finds 0 tests.

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

platforms = sln.split('GlobalSection(SolutionConfigurationPlatforms)')[1].split('EndGlobalSection')[0]
sln_cfgs = set(re.findall(r'^\s*([^|\s=]+)\|Any CPU = ', platforms, re.M))

errors = []
declared = {}
for guid, (name, path) in projects.items():
    csproj = open(os.path.join(sln_dir, path), encoding='utf-8-sig').read()
    match = re.search(r'<Configurations>(.*?)</Configurations>', csproj)
    declared[guid] = set(c.strip() for c in match.group(1).split(';') if c.strip()) if match else {'Debug', 'Release'}
    is_test = re.search(r'<IsTestProject>\s*true\s*</IsTestProject>', csproj, re.I) is not None
    if name.endswith('.Tests') or is_test:
        if not is_test:
            errors.append(f'{name}: test project does not set <IsTestProject>true</IsTestProject>')
        if 'Include="xunit.runner.visualstudio"' not in csproj:
            errors.append(f'{name}: test project does not reference xunit.runner.visualstudio (dotnet test finds 0 tests)')

mapped = set(re.findall(r'\{([0-9A-Fa-f-]+)\}\.([^|]+)\|Any CPU\.ActiveCfg = ', sln))
for guid, (name, _) in projects.items():
    for cfg in sorted(declared[guid] & sln_cfgs):
        if (guid, cfg) not in mapped:
            errors.append(f'{name}: declares {cfg} but has no {cfg}|Any CPU.ActiveCfg mapping in the solution')

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
