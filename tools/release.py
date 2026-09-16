"""Audited source and Windows release packaging. Python standard library only."""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import struct
import subprocess
import sys
import zipfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / 'XiaobianPet'
SKIP = {'.git', 'bin', 'obj', '__pycache__', 'release-output', '.vs'}
PRIVATE = {'settings.json', 'auth.json', 'memory.json', 'character-profile.json',
           'dialogue-session.json', 'conversation-context.json', 'conversation-history.dpapi.jsonl'}
# Exact NuGet 2.2.1 assembly bytes retain the vendor's build path. Only that
# rule is waived; token/credential rules still apply. Re-audit on dependency updates.
VENDOR_PROFILE_HASHES = {
    'NAudio.dll': 'c001e49d31497f608b14066b0f10304c0ccb7e3b9df9d5fec5cf3196f92961b8',
    'NAudio.WinMM.dll': '749a01ebbb5edd8b1a03c5263b04de6acadecf52e4cc84d7412bc6e93f180958',
    'NAudio.WinForms.dll': 'b313f530227fd31fc2b6fa74547f4c8a964cde7696c07624dfc19b693fe9c468',
    'NAudio.Wasapi.dll': '618ef0e49d64e7a66dfe64bbf6ae81705b9d9683d8a9f321e5c3024d666bdf82',
    'NAudio.Asio.dll': 'ca03780217139b37f7f5b6921d59defb8d24988315b16b167a77fa88caa7d00f',
    'NAudio.Midi.dll': 'f246e29921797b173b54229685e997a11f9cc388fa1e589c212328abd7a94ebe',
    'NAudio.Core.dll': 'fcf493fc47a2f478a65303886b975fbdbf714cbb1f2d79f7fce97e4bb16b01a8',
}

def vendor_path_exception(name, data, rule):
    return rule == 'user-profile-path' and VENDOR_PROFILE_HASHES.get(name) == hashlib.sha256(data).hexdigest()
RULES = {
    'service-token': re.compile(r'(?<![\w-])(?:s[k]-[\w-]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|github[_]pat_[A-Za-z0-9_]{20,}|AKI[A][A-Z0-9]{16})'),
    'private-key': re.compile(r'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE' + r' KEY-----'),
    'literal-credential': re.compile(r'''(?im)["']?(?:api[_-]?key|access[_-]?token|client[_-]?secret|password)["']?\s*[:=]\s*["']([^"'\r\n]{16,})["']'''),
    'user-profile-path': re.compile(r'(?i)[A-Z]:[\\/]+Users[\\/]+(?!Public\b|Default\b|<|\{)[^\\/\s"\']+'),
    'signed-url': re.compile(r'(?i)[?&](?:X-Amz-Signature|X-Tos-Signature|access_token|api_key)=\S{12,}'),
}

def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()

def files(root, skip_build=False):
    for path in sorted(root.rglob('*')):
        if skip_build and any(x in SKIP for x in path.relative_to(root).parts):
            continue
        if path.is_symlink():
            raise ValueError('Symlinks are not permitted in release input: ' + str(path.relative_to(root)))
        if path.is_file():
            yield path

def source_allowed(name):
    p = Path(name)
    if re.fullmatch(r'docs/media/keduo-\d+\.\d+(?:\.\d+)?-(?:cover-4x3|features-16x9)\.png', name):
        return True
    if name in {'README.md', 'CHANGELOG.md', 'SECURITY.md', 'LICENSE-STATUS.md', 'THIRD-PARTY-NOTICES.md',
                'version.json', 'global.json', 'Directory.Build.props', '.gitignore', '.gitattributes',
                '.github/dependabot.yml', '.github/workflows/build.yml', '.github/workflows/release.yml'}:
        return True
    if p.parent.as_posix() == 'tools' and p.suffix == '.py':
        return True
    if p.parent.as_posix() == 'docs' and p.suffix == '.md':
        return True
    if not name.startswith('XiaobianPet/'):
        return False
    q = p.relative_to('XiaobianPet')
    if len(q.parts) == 1:
        return q.suffix in {'.cs', '.xaml', '.csproj'} or q.name in {'app.manifest', 'packages.lock.json'}
    if q.parts[0] in {'Models', 'Services'}:
        return len(q.parts) == 2 and q.suffix == '.cs'
    if q.parts[0] == 'Tests':
        return len(q.parts) == 3 and (q.suffix in {'.cs', '.csproj'} or q.name == 'packages.lock.json')
    if q.as_posix() in {'Scripts/whisper_worker.py', 'Scripts/setup_local_asr.ps1',
                        'tools/Set-XiaobianTtsCredential.ps1', 'tools/Set-XiaobianArkModelCredential.ps1'}:
        return True
    if q.parts[0] == 'Assets':
        if len(q.parts) == 2:
            return q.name in {'ui-corner-ornament.png', 'ui-knot-divider.png', 'ui-progress-bubble-shell-generated-v4.png'}
        if q.parts[1] == 'animations':
            return (len(q.parts) == 3 and q.name in {'README.md', 'animation-manifest.json', 'workload-pack.json'}) or (
                len(q.parts) == 4 and q.suffix == '.png' and q.stem.isdecimal())
    return False

def findings(data):
    # Decode both ordinary source and managed/native UTF-16 string pools. Never print matched values.
    for encoding in ('utf-8', 'utf-16-le'):
        text = data.decode(encoding, errors='ignore')
        for rule, pattern in RULES.items():
            for match in pattern.finditer(text):
                if rule == 'literal-credential':
                    value = match.group(1)
                    if not re.fullmatch(r'[A-Za-z0-9_+/=.-]{16,}', value):
                        continue
                    if value.lower().startswith(('your_', 'example_', 'placeholder', 'test_', 'fake_')):
                        continue
                    # Names of environment variables are not credentials.
                    if value in {'XIAOBIAN_ARK_API_KEY', 'XIAOBIAN_ARK_MODEL_API_KEY'}:
                        continue
                yield rule, text.count('\n', 0, match.start()) + 1
    for name, value in os.environ.items():
        if re.search(r'(?i)(?:api_?key|access_?token|secret|password)$', name) and len(value) >= 16:
            if value.encode() in data or value.encode('utf-16-le') in data:
                yield 'current-environment-secret', 0

def scan(root, source=False):
    problems = []
    count = 0
    for path in files(root, skip_build=source):
        rel = path.relative_to(root).as_posix()
        count += 1
        if (path.name.lower() in PRIVATE or path.name.startswith('.env') or
            path.suffix.lower() in {'.log', '.pdb', '.pfx', '.pem', '.key', '.jsonl', '.sqlite', '.db'}):
            problems.append((rel, 'private-file', 0))
        if source and not source_allowed(rel):
            problems.append((rel, 'not-in-source-allowlist', 0))
        data = path.read_bytes()
        for rule, line in set(findings(data)):
            if not source and vendor_path_exception(path.name, data, rule):
                continue
            problems.append((rel, rule, line))
    if problems:
        for rel, rule, line in problems:
            print(f'BLOCKED {rel}:{line} [{rule}]')
        raise ValueError(f'Security gate rejected {len(problems)} finding(s). Values were not logged.')
    print(f'Security gate PASS: {count} files', flush=True)
    return count

def check_assets():
    manifest = json.loads((APP / 'Assets/animations/animation-manifest.json').read_text('utf-8'))
    actual = {p.relative_to(APP / 'Assets/animations').as_posix() for p in (APP / 'Assets/animations').rglob('*.png')}
    expected = set()
    for clip in manifest['clips']:
        for frame in clip['frames']:
            rel = clip['directory'] + '/' + frame['file']
            expected.add(rel)
            path = APP / 'Assets/animations' / rel
            if not path.is_file() or sha(path) != frame['sha256']:
                raise ValueError('Missing/changed animation frame; update manifest: ' + rel)
            data = path.read_bytes()
            if data[:8] != b'\x89PNG\r\n\x1a\n' or struct.unpack('>II', data[16:24]) != (384, 416):
                raise ValueError('Invalid/LFS-pointer animation frame: ' + rel)
    if actual != expected:
        raise ValueError('Unmanifested/missing animation frames detected')
    definitions = (APP / 'Models/PetAnimation.cs').read_text('utf-8-sig')
    directories = set(re.findall(r'\[PetMood\.\w+\]\s*=\s*"([\w-]+)"', definitions))
    if directories != {c['directory'] for c in manifest['clips']}:
        raise ValueError('Runtime animation mappings and manifest differ')
    print(f'Assets PASS: {len(expected)} verified frames', flush=True)

def run(command):
    env = {k: v for k, v in os.environ.items() if not k.startswith(('XIAOBIAN_', 'CODEX_', 'OPENAI_', 'ARK_'))}
    subprocess.run(command, cwd=ROOT, env=env, check=True)

def test():
    run([sys.executable, str(ROOT / 'tools/test_release.py')])
    # Pure offline harnesses only. Account integration and optional live-ASR tests are deliberately excluded.
    for name in ('AnimationSequenceHarness', 'ChatOrbPlacementHarness', 'PointerGestureHarness',
                 'RuntimeAnimationPolicyHarness', 'SpriteAtlasPreparedHarness', 'TaskContextFeedbackHarness',
                 'WorkloadReactionHarness', 'RecycleBinFeedbackHarness'):
        run(['dotnet', 'run', '--project', str(APP / 'Tests' / name / (name + '.csproj')), '-c', 'Release'])

def archive(folder, output, paths):
    if output.exists():
        raise FileExistsError('Refusing to replace an existing archive: ' + output.name)
    with zipfile.ZipFile(output, 'x', zipfile.ZIP_DEFLATED, compresslevel=6, allowZip64=True) as z:
        for path in paths:
            z.write(path, path.relative_to(folder).as_posix())
    with zipfile.ZipFile(output) as z:
        if z.testzip():
            raise ValueError('ZIP CRC verification failed')
        for item in z.infolist():
            if item.filename.startswith('/') or '..' in Path(item.filename).parts:
                raise ValueError('Unsafe archive path')

def normalized_version(version):
    if not re.fullmatch(r'\d+\.\d+(?:\.\d+)?(?:-[A-Za-z0-9.-]+)?', version):
        raise ValueError('Use a two- or three-component version without a leading v')
    core, separator, suffix = version.partition('-')
    if core.count('.') == 1:
        core += '.0'
    return core + separator + suffix

def package(version, framework_dependent=False):
    build_version = normalized_version(version)
    release_notes = ROOT / 'docs' / f'RELEASE-{version}.md'
    media = [ROOT / 'docs/media' / f'keduo-{version}-{suffix}.png'
             for suffix in ('cover-4x3', 'features-16x9')]
    for required in media + [release_notes, ROOT / 'docs/QUICKSTART.md']:
        if not required.is_file():
            raise ValueError('Missing versioned release material: ' + required.name)
    scan(ROOT, source=True)
    check_assets()
    out = ROOT / 'release-output' / version
    out.mkdir(parents=True, exist_ok=False)
    kind = 'framework-dependent' if framework_dependent else 'portable'
    published = out / f'Keduo-{version}-win-x64-{kind}'
    run(['dotnet', 'publish', str(APP / 'XiaobianPet.csproj'), '-c', 'Release', '-r', 'win-x64',
         '--self-contained', str(not framework_dependent).lower(), '-o', str(published),
         '-p:Version=' + build_version, '-p:InformationalVersion=' + version,
         '-p:IncludeSourceRevisionInInformationalVersion=false', '-p:DebugType=None', '-p:DebugSymbols=false',
         '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:RestoreLockedMode=true'])
    import shutil
    if not framework_dependent:
        runtime_version = ET.parse(ROOT / 'Directory.Build.props').findtext('.//RuntimeFrameworkVersion')
        assets = json.loads((APP / 'obj/project.assets.json').read_text('utf-8'))
        licenses = published / 'licenses'
        licenses.mkdir()
        for package_id, names in {
            'microsoft.netcore.app.runtime.win-x64': ('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT'),
            'microsoft.windowsdesktop.app.runtime.win-x64': ('LICENSE', 'THIRD-PARTY-NOTICES.TXT'),
        }.items():
            package = next((Path(folder) / package_id / runtime_version
                            for folder in assets['packageFolders']
                            if (Path(folder) / package_id / runtime_version).is_dir()), None)
            if package is None:
                raise ValueError('Runtime license package missing: ' + package_id)
            for name in names:
                src = package / name
                if src.is_file():
                    shutil.copy2(src, licenses / (package_id + '-' + name + '.txt'))
                elif name.startswith('LICENSE'):
                    raise ValueError('Required runtime license missing: ' + package_id)
    for name in ('README.md', 'SECURITY.md', 'LICENSE-STATUS.md', 'THIRD-PARTY-NOTICES.md', 'CHANGELOG.md'):
        shutil.copy2(ROOT / name, published / name)
    shutil.copytree(ROOT / 'docs', published / 'docs')
    shutil.copy2(ROOT / 'docs/QUICKSTART.md', published / '先读我-使用说明.md')
    (published / '先读我-使用说明.txt').write_text(
        (ROOT / 'docs/QUICKSTART.md').read_text('utf-8'), encoding='utf-8-sig')
    for rel in ('Scripts/setup_local_asr.ps1', 'Scripts/whisper_worker.py',
                'tools/Set-XiaobianTtsCredential.ps1', 'tools/Set-XiaobianArkModelCredential.ps1'):
        dest = published / rel
        dest.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(APP / rel, dest)
    (published / 'Start-Keduo.cmd').write_text('@echo off\nstart "" /d "%~dp0" "%~dp0XiaobianPet.exe"\n', encoding='ascii')
    for label, script in (('Configure-Voice', 'tools/Set-XiaobianTtsCredential.ps1'),
                          ('Setup-Local-ASR', 'Scripts/setup_local_asr.ps1')):
        (published / (label + '.cmd')).write_text('@echo off\npowershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0' + script.replace('/', '\\') + '"\npause\n', encoding='ascii')
    scan(published)
    source_paths = [p for p in files(ROOT, skip_build=True) if source_allowed(p.relative_to(ROOT).as_posix())]
    scan(ROOT, source=True)
    source_zip = out / f'Keduo-{version}-source.zip'
    app_zip = out / f'{published.name}.zip'
    media_zip = out / f'Keduo-{version}-covers-and-guide.zip'
    archive(ROOT, source_zip, source_paths)
    archive(published, app_zip, list(files(published)))
    media_paths = media + [ROOT / 'docs/QUICKSTART.md', release_notes]
    archive(ROOT / 'docs', media_zip, media_paths)
    (out / 'SHA256SUMS.txt').write_text(''.join(f'{sha(p)}  {p.name}\n' for p in (source_zip, app_zip, media_zip)), encoding='utf-8')
    inventory = [{'path': p.relative_to(published).as_posix(), 'bytes': p.stat().st_size, 'sha256': sha(p)} for p in files(published)]
    (out / 'release-audit.json').write_text(json.dumps({'version': version, 'build_version': build_version, 'secrets_scan': 'pass',
        'archive_crc': 'pass', 'runtime': kind, 'files': inventory}, ensure_ascii=False, indent=2), encoding='utf-8')
    print('READY ' + str(out), flush=True)

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=['scan', 'test', 'package'])
    parser.add_argument('--version')
    parser.add_argument('--framework-dependent', action='store_true')
    args = parser.parse_args()
    if args.command == 'scan':
        scan(ROOT, source=True)
        check_assets()
    elif args.command == 'test':
        test()
    else:
        version = args.version or json.loads((ROOT / 'version.json').read_text('utf-8'))['version']
        package(version, args.framework_dependent)

if __name__ == '__main__':
    try:
        main()
    except (ValueError, FileExistsError) as exc:
        print(str(exc), file=sys.stderr)
        sys.exit(1)
