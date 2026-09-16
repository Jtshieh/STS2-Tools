"""Fixed ordinary input preparation; executes no game, native library or subprocess.

HQ integration supplies the already reviewed ordinary-copy and path functions.
This module does not install dependencies or mutate source/baseline files.
"""
from pathlib import Path, PurePosixPath
import hashlib
import json
import os
import stat

CONTRACT_SHA256 = None
OVERRIDE = ('config_version=5\n\n[dotnet]\n\n'
    'project/assembly_name="E003LinuxHostControls"\n\n[autoload]\n\n'
    'E004NormalBootObserver="*res://Main.cs"\n').encode()


def sha(path):
    result = hashlib.sha256()
    with path.open('rb') as f:
        while block := f.read(1024 * 1024):
            result.update(block)
    return result.hexdigest()


def prepare(sources, evidence, runtime, game_baseline, private, *, ordinary, checked_private,
            copy_file, write_json):
    """Return fixed file facts for NormalBootConfig; all destination paths are new."""
    for name, value in locals().copy().items():
        if name in ('sources', 'evidence', 'runtime', 'game_baseline', 'private'):
            if not isinstance(value, Path):
                raise TypeError('Input path must be pathlib.Path: ' + name)
    global CONTRACT_SHA256
    CONTRACT_SHA256 = sha(sources / "game-input-contract.json")
    ordinary(game_baseline, True)
    contract_path = sources / 'game-input-contract.json'
    ordinary(contract_path)
    if sha(contract_path) != CONTRACT_SHA256:
        raise ValueError('Fixed game/profile contract changed')
    contract = json.loads(contract_path.read_text())
    rows = contract['files']
    if (contract['schema'] != 'e004b-normal-boot-game-inputs-v1'
            or len(rows) != 32 or len({r['path'] for r in rows}) != 32
            or sum(r['bytes'] for r in rows) != 2031724971):
        raise ValueError('Unexpected fixed candidate input shape')
    expected = {r['path']: r for r in rows}
    actual = set()
    for parent, directories, files in os.walk(game_baseline, followlinks=False):
        for name in directories:
            ordinary(Path(parent) / name, True)
        for name in files:
            source = Path(parent) / name
            info = ordinary(source)
            if stat.S_IMODE(info.st_mode) & 0o222:
                raise ValueError('Candidate source is not an immutable baseline: ' + str(source))
            actual.add(str(source.relative_to(game_baseline)))
    if actual != set(expected) | {'SlayTheSpire2'}:
        raise ValueError('Candidate baseline has missing or extra files')
    target = evidence / 'game-input'
    target.mkdir(mode=0o700)
    records = []
    for name, item in sorted(expected.items()):
        parts = PurePosixPath(name).parts
        if not parts or name.startswith('/') or any(p in ('', '.', '..') for p in parts):
            raise ValueError('Unsafe fixed candidate path')
        source, destination = game_baseline / name, target / name
        destination.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        ordinary(source)
        record = copy_file(source, destination)
        if record['bytes'] != item['bytes'] or record['sha256'] != item['sha256']:
            raise ValueError('Candidate ordinary copy differs from fixed provenance: ' + name)
        destination.chmod(0o400)
        records.append(record)
    # Keep the original project and reference map separate from the untouched PCK.
    extra = []
    for source_name, target_name in (('original-project.binary', 'expected-project.binary'),
                                     ('expected-script-map.json', 'expected-script-map.json')):
        record = copy_file(sources / source_name, target / target_name)
        if source_name == 'original-project.binary':
            if record['sha256'] != contract['projectBinary']['sha256'] or record['bytes'] != 24693:
                raise ValueError('Original project reference changed')
        elif record['sha256'] != 'd7d55364f4b4e442267953eb89d5ce2e9bcacde478058f98dfb99777f15c858c' or record['bytes'] != 83049:
            raise ValueError('Original 709-script metadata reference changed')
        (target / target_name).chmod(0o400)
        extra.append(record)
    override = target / 'override.cfg'
    with override.open('xb') as f:
        f.write(OVERRIDE)
        f.flush()
        os.fsync(f.fileno())
    override.chmod(0o400)
    host = runtime / 'native-host'
    ordinary(host, True)
    native_copies = []
    # Godot's Linux resource-library fallback checks the executable directory.
    for name in sorted(expected):
        if '/' not in name and (name.endswith('.so') or '.so.' in name or name == 'crashpad_handler'):
            record = copy_file(target / name, host / name)
            (host / name).chmod(0o500 if name == 'crashpad_handler' else 0o400)
            native_copies.append(record)
    if len(native_copies) != 10:
        raise ValueError('Unexpected original native fallback file count')
    # Original Linux ReleaseInfo reads beside OS.GetExecutablePath (fixed method 0x060054d2).
    release_info_copy = copy_file(target / 'release_info.json', host / 'release_info.json')
    release_expected = expected['release_info.json']
    if (release_info_copy['bytes'] != release_expected['bytes']
            or release_info_copy['sha256'] != release_expected['sha256']):
        raise ValueError('Original executable-adjacent release metadata copy changed')
    (host / 'release_info.json').chmod(0o400)
    profile_records = []
    account = runtime / 'xdg-data/SlayTheSpire2/default/1'
    account.mkdir(mode=0o700, parents=True)
    for item in contract['profileInputs']:
        name = item['originalRelativePath']
        destination = account / name
        destination.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        record = copy_file(sources / item['name'], destination)
        if record['sha256'] != item['sha256'] or record['bytes'] != item['bytes']:
            raise ValueError('Original profile input changed: ' + name)
        profile_records.append(record)
    if (account / 'settings.save').exists() or len(profile_records) not in (3, 4):
        raise ValueError('Normal settings must start absent with three progression inputs and optional explicit current run')
    for path in sorted(target.rglob('*'), key=lambda p: len(p.parts), reverse=True):
        if path.is_dir():
            path.chmod(0o500)
    target.chmod(0o500)
    summary = dict(schema='e004b-normal-game-input-copy-v1', gameRoot=str(target),
        gameBaseline=str(game_baseline), gameFiles=records, extraReferences=extra,
        override=dict(path=str(override), bytes=len(OVERRIDE), sha256=sha(override)),
        nativeFallbackCopies=native_copies, releaseInfoCopy=release_info_copy, profileInputs=profile_records,
        settingsInitiallyAbsent=True, sourceContractSha256=CONTRACT_SHA256,
        scope='Ordinary copies only; no program execution; original 32 source files unchanged during copy')
    write_json(evidence / 'normal-game-input-copy-manifest.json', summary)
    return target, summary


def verify_immutable_inputs(game_baseline, game_root, contract, *, ordinary, runtime, summary):
    """Check every original and game-working file again after the native phase."""
    checked = []
    for item in contract['files']:
        for root in (game_baseline, game_root):
            path = root / item['path']
            info = ordinary(path)
            if (info.st_size != item['bytes'] or stat.S_IMODE(info.st_mode) & 0o222
                    or sha(path) != item['sha256']):
                raise ValueError('Fixed original/game-working input changed: ' + str(path))
            checked.append(dict(path=str(path), bytes=info.st_size, sha256=item['sha256'], nlink=info.st_nlink))
    for item in summary['extraReferences'] + summary['nativeFallbackCopies'] + [summary['releaseInfoCopy']]:
        path = Path(item['destination'])
        if not path.is_relative_to(game_root) and not path.is_relative_to(runtime / 'native-host'):
            raise ValueError('Unexpected retained-copy verification path')
        info = ordinary(path)
        if info.st_size != item['bytes'] or stat.S_IMODE(info.st_mode) & 0o222 or sha(path) != item['sha256']:
            raise ValueError('Frozen normal-game reference/native fallback changed: ' + str(path))
        checked.append(dict(path=str(path), bytes=info.st_size, sha256=item['sha256'], nlink=info.st_nlink))
    override = game_root / 'override.cfg'
    info = ordinary(override)
    if stat.S_IMODE(info.st_mode) & 0o222 or override.read_bytes() != OVERRIDE:
        raise ValueError('Exact normal-game override changed')
    checked.append(dict(path=str(override), bytes=info.st_size, sha256=sha(override), nlink=info.st_nlink))
    # Working profile files may change through official normal initialization; retain all actual hashes.
    profile_after = []
    account = runtime / 'xdg-data/SlayTheSpire2/default/1'
    for parent, directories, files in os.walk(account, followlinks=False):
        for name in directories:
            ordinary(Path(parent) / name, True)
        for name in files:
            path = Path(parent) / name
            info = ordinary(path)
            profile_after.append(dict(path=str(path.relative_to(account)), bytes=info.st_size,
                sha256=sha(path), mode=oct(stat.S_IMODE(info.st_mode)), nlink=info.st_nlink))
    return dict(passed=True, checkedFiles=checked, workingProfileAfter=profile_after,
        workingProfileComparison='Observed official writes retained; no byte-equality or semantic-acceptance claim here')


def build_config(sources, summary, native_id, native_args):
    """Generate the fixed normal boot contract from independently pinned inputs."""
    contract = json.loads((sources / 'game-input-contract.json').read_text())
    if sha(sources / 'game-input-contract.json') != CONTRACT_SHA256:
        raise ValueError('Normal boot input contract changed before configuration')
    original = {item['path']: item for item in contract['files']}
    def pinned(relative, prefix='/game/'):
        item = original[relative]
        return dict(path=prefix + relative, sha256=item['sha256'], bytes=item['bytes'])
    assemblies = json.loads((sources / 'game-assemblies-pinned.json').read_text())
    expected_managed = {Path(name).stem for name in original if name.endswith('.dll')} - {'GodotSharp'}
    if set(assemblies) != expected_managed or len(assemblies) != 16:
        raise ValueError('Normal boot must pin all sixteen original managed DLLs except shared GodotSharp')
    for name, item in assemblies.items():
        expected = pinned('data_sts2_linuxbsd_x86_64/' + name + '.dll')
        if set(item) != {'path', 'sha256', 'bytes', 'fullName'} or any(item[key] != value for key, value in expected.items()):
            raise ValueError('Managed identity/hash manifest differs from original candidate: ' + name)
        if not isinstance(item['fullName'], str) or not item['fullName'].startswith(name + ', Version='):
            raise ValueError('Missing full managed Assembly identity: ' + name)
    native = json.loads((sources / 'native-libraries-pinned.json').read_text())
    if set(native) != {'nativeLibraryAliases', 'allowedNativeFiles'} or native['nativeLibraryAliases'] != {}:
        raise ValueError('This first normal probe permits no guessed native resolver aliases')
    expected_native = {}
    for name in original:
        if name.endswith('.so') or '.so.' in name or name == 'crashpad_handler':
            item = pinned(name)
            expected_native[item['path']] = item
            if '/' not in name:
                item = pinned(name, '/work/native-host/')
                expected_native[item['path']] = item
    if native['allowedNativeFiles'] != expected_native or len(expected_native) != 21:
        raise ValueError('Normal native allowlist must match all original native files and ten fallback copies')
    expected_argv = ['/work/native-host/SlayTheSpire2', '--headless', '--audio-driver', 'Dummy',
                     '--path', '/work/project', '--main-pack', '/game/SlayTheSpire2.pck',
                     '--force-steam=off', '--', '--e003-linux-host-controls', '--config=/work/control-config.json']
    if native_args != expected_argv:
        raise ValueError('Normal native command differs from reviewed original main-pack route')
    script_map = sources / 'expected-script-map.json'
    if script_map.stat().st_size != 83049 or sha(script_map) != 'd7d55364f4b4e442267953eb89d5ce2e9bcacde478058f98dfb99777f15c858c':
        raise ValueError('Original script map changed')
    return dict(schema='e004b-normal-boot-config-v1', processRunId=native_id,
        expectedResourceRoot='', nativeArgs=native_args, godotArgs=['--force-steam=off'],
        userArgs=['--e003-linux-host-controls', '--config=/work/control-config.json'],
        pck=pinned('SlayTheSpire2.pck'),
        override=dict(path='/game/override.cfg', sha256=hashlib.sha256(OVERRIDE).hexdigest(), bytes=len(OVERRIDE)),
        originalProjectBinary=dict(path='/game/expected-project.binary',
            sha256=contract['projectBinary']['sha256'], bytes=24693),
        originalScriptMap=dict(path='/game/expected-script-map.json', sha256=sha(script_map), bytes=83049),
        gameAssemblies=assemblies, sharedGodotSharp=pinned('data_sts2_linuxbsd_x86_64/GodotSharp.dll'),
        **native, expectedUserDataPath='/work/xdg-data/SlayTheSpire2',
        inputFiles={item['originalRelativePath']: dict(path='/work/xdg-data/SlayTheSpire2/default/1/' + item['originalRelativePath'],
                    sha256=item['sha256'], bytes=item['bytes']) for item in contract['profileInputs']},
        observedStdoutPath='/work/normal-game.stdout.log', observedStderrPath='/work/normal-game.stderr.log',
        observerTimeoutSeconds=1150, profileId=contract['profileId'])
