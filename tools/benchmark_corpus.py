"""Fresh plugin AI -> pinned DLL -> isolated CAD -> declared source checks.

Each invocation owns a new run directory. Never reuses old plans or claims full
image accuracy. --prepare-only renders sources without making model requests.
"""
import argparse
from collections import Counter
from datetime import datetime, timezone
import hashlib
import json
import math
import os
from pathlib import Path
import shutil
import statistics
import subprocess
import time
import uuid

import ezdxf
import fitz
from verify_section_reproduction import verify as verify_section

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_CASES = ['fifth-channel', 'sixth-section33', 'seventh-detail']


def digest(path):
    with Path(path).open('rb') as source:
        h = hashlib.sha256()
        for chunk in iter(lambda: source.read(1024 * 1024), b''):
            h.update(chunk)
        return h.hexdigest()


def save(path, value):
    path = Path(path)
    temporary = path.with_name(path.name + '.tmp')
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')
    os.replace(temporary, path)


def read(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def case_source(root, case):
    root = Path(root).resolve()
    source = (root / case['source']).resolve()
    if not source.is_relative_to(root):
        raise ValueError('Case source leaves the supplied source root')
    if not source.is_file():
        raise FileNotFoundError(case['source'])
    return source


def render_case(root, case, output):
    source = case_source(root, case)
    source_hash = digest(source)
    with fitz.open(source) as pdf:
        number = case['page']
        if not isinstance(number, int) or not 1 <= number <= len(pdf):
            raise ValueError('Invalid 1-based source page')
        page = pdf[number - 1]
        page.set_rotation(case.get('rotation', 0))
        clip = None
        if 'crop' in case:
            x0, y0, x1, y1 = case['crop']
            if not (0 <= x0 < x1 <= 1 and 0 <= y0 < y1 <= 1):
                raise ValueError('Invalid normalized crop')
            clip = fitz.Rect(x0 * page.rect.width, y0 * page.rect.height,
                             x1 * page.rect.width, y1 * page.rect.height)
        scale = 3 if case['id'] == 'sixth-section33' else 2.5 if clip else 2
        page.get_pixmap(matrix=fitz.Matrix(scale, scale), clip=clip, alpha=False).save(output)
    if digest(source) != source_hash:
        raise ValueError('Source PDF changed during rendering')
    return {'source_sha256': source_hash, 'image_sha256': digest(output),
            'page': number, 'rotation': case.get('rotation', 0), 'crop': case.get('crop')}


def dimension_readings(doc):
    values = []
    for dim in doc.modelspace().query('DIMENSION'):
        # A fixed label cannot provide evidence of actual geometry.
        if dim.dxf.get('text', '') not in ('', '<>') or dim.dimtype not in (0, 1, 3, 4):
            continue
        if dim.dimtype == 1:
            a, b = dim.dxf.defpoint2, dim.dxf.defpoint3
            actual = math.hypot(b.x - a.x, b.y - a.y)
        else:
            actual = float(dim.get_measurement())
        factor = float(dim.override().get('dimlfac', 1))
        value = abs(actual * factor)
        if math.isfinite(value):
            values.append(value)
    return values


def source_checks(case, dxf):
    if case['id'] == 'sixth-section33':
        return verify_section(dxf)
    doc = ezdxf.readfile(dxf)
    checks = [{'criterion': 'CAD model units are millimeters',
               'passed': doc.header.get('$INSUNITS') == 4}]
    available = dimension_readings(doc)
    for expected, multiplicity in sorted(Counter(case.get('dimensions', [])).items()):
        matched = sum(abs(value - expected) <= .01 for value in available)
        checks.append({'criterion': f'selected source reading {expected} occurs at least {multiplicity} times',
                       'passed': matched >= multiplicity, 'expected_count': multiplicity,
                       'actual_count': matched})
    return {'case': case['id'], 'checks': checks,
            'passed': sum(c['passed'] for c in checks), 'total': len(checks),
            'scope': 'Selected native dimension readings only; positions, outlines, textures and unreadable labels are not certified.',
            'source_accuracy_verified': False, 'native_dimension_readings': available,
            'unverified': ['dimension positions', 'full source geometry', 'font shapes and pixel fidelity']}


def child_environment(command, env=None):
    inherited = dict(os.environ if env is None else env)
    if Path(command[0]).name.lower() in ('powershell', 'powershell.exe'):
        # Windows PowerShell must rebuild its own module path. Inheriting the
        # parent's PowerShell 7 modules can hide built-ins such as Get-FileHash.
        inherited = {k: v for k, v in inherited.items() if k.lower() != 'psmodulepath'}
    return inherited


def run_process(command, log, timeout, env=None):
    started = time.monotonic()
    with Path(log).open('wb') as stream:
        process = subprocess.Popen(command, cwd=ROOT, env=child_environment(command, env), stdout=stream,
                                   stderr=subprocess.STDOUT,
                                   creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        try:
            code = process.wait(timeout=timeout)
        except (subprocess.TimeoutExpired, KeyboardInterrupt) as error:
            if os.name == 'nt':
                subprocess.run(['taskkill', '/PID', str(process.pid), '/T', '/F'],
                               capture_output=True, timeout=15)
            else:
                process.kill()
            process.wait(timeout=15)
            if isinstance(error, KeyboardInterrupt):
                raise
            raise TimeoutError(f'Owned subprocess exceeded {timeout}s')
    return code, round(time.monotonic() - started, 3)


def aggregate(results):
    groups = {}
    for result in results:
        key = (result['case'], result.get('model_configuration_sha256'))
        group = groups.setdefault(key, {'case': key[0], 'model_configuration_sha256': key[1],
                                        'attempts': 0, 'request_successes': 0, 'execution_successes': 0,
                                        'selected_check_successes': 0, 'failures': [], 'elapsed_seconds': []})
        group['attempts'] += 1
        for field, source in [('request_successes', 'request_success'), ('execution_successes', 'execution_success'),
                              ('selected_check_successes', 'selected_checks_passed')]:
            group[field] += int(result.get(source, False))
        if result.get('error'):
            group['failures'].append({'trial': result['trial'], 'stage': result['stage'], 'error': result['error']})
        group['elapsed_seconds'].append(result['elapsed_seconds'])
    for group in groups.values():
        values = group['elapsed_seconds']
        group['elapsed_median_seconds'] = round(statistics.median(values), 3)
        group['elapsed_range_seconds'] = [min(values), max(values)]
    return list(groups.values())


def native_preflight(run, plugin, identity, env):
    folder = run / 'runtime' / 'preflight'
    folder.mkdir()
    plan = folder / 'probe.plan.json'
    save(plan, {'commands': [{'type': 'circle', 'center': [0, 0], 'radius': 1}]})
    code, seconds = run_process(['powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                                str(ROOT / 'tools/Reproduce-Cad.ps1'), '-Plan', str(plan), '-Name', 'probe',
                                '-PluginDll', str(plugin), '-OutputDirectory', str(folder)],
                               folder / 'preflight.log', 180, env)
    if code:
        raise ValueError('Native preflight failed before any model request; see runtime/preflight/preflight.log')
    proof = read(folder / 'probe.execution.json')
    if proof.get('plugin_sha256') != identity['plugin_sha256'] or proof.get('plan_sha256') != digest(plan):
        raise ValueError('Preflight plugin/plan identity mismatch')
    doc = ezdxf.readfile(folder / 'probe.dxf')
    circles = list(doc.modelspace().query('CIRCLE'))
    if len(circles) != 1 or abs(circles[0].dxf.radius - 1) > 1e-8 or doc.header.get('$INSUNITS') != 4:
        raise ValueError('Preflight actual geometry did not match its fixed probe')
    return {'passed': True, 'seconds': seconds, **proof}


def trial(case, number, prepared, run, plugin, helper, identity, env):
    folder = run / case['id'] / f'trial-{number:02}'
    folder.mkdir(parents=True, exist_ok=False)
    image = run / 'sources' / (case['id'] + '.png')
    result = {'case': case['id'], 'trial': number, 'fresh_request': True, 'stage': 'request',
              'request_success': False, 'execution_success': False, 'selected_checks_passed': False,
              'source_accuracy_verified': False, **prepared}
    started = time.monotonic()
    prompt = case.get('prompt') or ('复刻当前实际可见的工程视图、轮廓、尺寸和材料，不补画未给出的视图。源图单位为' + case['sourceUnit'] + '，按源图比例和真实尺寸建立几何。')
    (folder / 'prompt.txt').write_text(prompt, encoding='utf-8')
    result['prompt_sha256'] = digest(folder / 'prompt.txt')
    try:
        if digest(plugin) != identity['plugin_sha256'] or digest(image) != prepared['image_sha256']:
            raise ValueError('Pinned plugin or source image changed')
        report = folder / 'model.json'
        code, seconds = run_process([str(helper), str(plugin), str(image), str(report), prompt],
                                    folder / 'request.log', 280, env)
        result['request_wall_seconds'] = seconds
        model = read(report)
        for key in ['model', 'model_configuration_sha256', 'plugin_version']:
            result[key] = model.get(key)
        result['model_seconds'] = model.get('seconds')
        result['response_capture_available'] = model.get('response_capture_available', False)
        result['raw_responses'] = model.get('raw_responses', [])
        for captured in result['raw_responses']:
            raw = (folder / captured['file']).resolve()
            if not raw.is_relative_to(folder.resolve()) or digest(raw) != captured['sha256']:
                raise ValueError('Captured model response identity mismatch')
        if model.get('plugin_sha256') != identity['plugin_sha256']:
            raise ValueError('Model helper used a different plugin')
        if code or not model.get('success'):
            raise ValueError(model.get('error') or 'Model helper returned failure')
        plan = folder / 'model.plan.json'
        if not plan.is_file() or not model.get('plan'):
            raise ValueError('A fresh model drawing plan is missing')
        if json.loads(plan.read_text(encoding='utf-8-sig')) != json.loads(model['plan']):
            raise ValueError('Plan differs from the actual model response')
        result['request_success'] = True
        result['plan_sha256'] = digest(plan)
        result['stage'] = 'execution'
        for relative, expected in identity['native_tools'].items():
            if digest(ROOT / relative) != expected:
                raise ValueError('Native execution tool changed during the run')
        native = folder / 'native'
        code, seconds = run_process(['powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                                    str(ROOT / 'tools/Reproduce-Cad.ps1'), '-Plan', str(plan),
                                    '-Name', 'drawing', '-PluginDll', str(plugin), '-OutputDirectory', str(native)],
                                   folder / 'execution.log', 180, env)
        result['execution_wall_seconds'] = seconds
        if code:
            raise ValueError('Native execution failed; see this trial execution.log')
        proof = read(native / 'drawing.execution.json')
        if proof.get('plugin_sha256') != identity['plugin_sha256'] or proof.get('plan_sha256') != result['plan_sha256']:
            raise ValueError('Native execution identity mismatch')
        if proof.get('helper_sha256') != identity['native_tools']['tests/CadIntegrationTests.cs']:
            raise ValueError('Native helper changed during execution')
        records = read(native / 'drawing.native.json')
        if any(r.get('error') or r.get('passed') is False or r.get('result') is False for r in records):
            raise ValueError('Native checks rejected this plan')
        reproduction = next(r for r in records if r.get('test') == 'reproduction')
        if reproduction.get('entities', 0) <= 0:
            raise ValueError('Native drawing is empty')
        result['execution_success'] = True
        result['stage'] = 'source_checks'
        dxf = native / 'drawing.dxf'
        verification = source_checks(case, dxf)
        save(folder / 'source-checks.json', verification)
        result['dxf_sha256'] = digest(dxf)
        result['checks_passed'] = verification['passed']
        result['checks_total'] = verification['total']
        result['check_scope'] = verification['scope']
        result['failed_criteria'] = [c['criterion'] for c in verification['checks'] if not c['passed']]
        result['selected_checks_passed'] = verification['total'] > 1 and verification['passed'] == verification['total']
        result['stage'] = 'completed'
    except Exception as error:
        result['error'] = f'{type(error).__name__}: {error}'
    finally:
        result['elapsed_seconds'] = round(time.monotonic() - started, 3)
        save(folder / 'trial.json', result)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source-root', type=Path, default=Path(r'D:\AutoCAD 2024\高教杯水利赛道'))
    parser.add_argument('--output', type=Path, default=ROOT / 'artifacts/corpus-runs')
    parser.add_argument('--case', action='append', dest='selected')
    parser.add_argument('--trials', type=int, default=1)
    parser.add_argument('--model', default='auto')
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument('--prepare-only', action='store_true')
    mode.add_argument('--preflight-only', action='store_true')
    parser.add_argument('--detailed-images', action='store_true', help='Use the same whole-image plus detailed tiles path as the plugin UI')
    args = parser.parse_args()
    if not 1 <= args.trials <= 20:
        parser.error('--trials must be 1..20')
    cases = read(ROOT / 'tests/corpus_cases.json')['cases']
    selected = args.selected or DEFAULT_CASES
    if len(set(selected)) != len(selected) or set(selected) - {c['id'] for c in cases}:
        parser.error('Unknown or duplicate case ID')
    cases = [c for c in cases if c['id'] in selected]
    run = args.output.resolve() / (datetime.now(timezone.utc).strftime('run-%Y%m%dT%H%M%SZ-') + uuid.uuid4().hex[:8])
    (run / 'sources').mkdir(parents=True, exist_ok=False)
    report = {'schema': 1, 'mode': 'prepare_only' if args.prepare_only else 'preflight_only' if args.preflight_only else 'fresh_plugin_ai',
              'source_accuracy_verified': False, 'model': args.model, 'requested_trials': args.trials, 'detailed_images': args.detailed_images,
              'started_utc': datetime.now(timezone.utc).isoformat(), 'cases': cases, 'results': [],
              'status': 'preparing', 'scope': 'Declared selected source checks, not full image accuracy.'}
    save(run / 'report.json', report)
    print('RUN=' + str(run), flush=True)
    try:
        prepared = report['sources'] = {}
        for case in cases:
            prepared[case['id']] = render_case(args.source_root, case, run / 'sources' / (case['id'] + '.png'))
            save(run / 'report.json', report)
        if args.prepare_only:
            report['status'] = 'prepared'
            return 0
        runtime = run / 'runtime'
        runtime.mkdir()
        plugin = runtime / 'AutoCAD.AI.Assistant.dll'
        shutil.copy2(ROOT / 'bin/Release/AutoCAD.AI.Assistant.dll', plugin)
        shutil.copy2(ROOT / 'tools/ModelBenchmark.cs', runtime / 'ModelBenchmark.cs')
        identity = {'plugin_sha256': digest(plugin), 'model_helper_sha256': digest(runtime / 'ModelBenchmark.cs'),
                    'native_tools': {p: digest(ROOT / p) for p in ['tools/Reproduce-Cad.ps1', 'tools/run_console.py', 'tests/CadIntegrationTests.cs']},
                    'verifier_sha256': digest(ROOT / 'tools/verify_section_reproduction.py'),
                    'runner_sha256': digest(Path(__file__))}
        report['identity'] = identity
        snapshots = runtime / 'source-snapshot'
        for relative in ['tools/benchmark_corpus.py', 'tools/verify_section_reproduction.py',
                         'tests/corpus_cases.json', *identity['native_tools']]:
            target = snapshots / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(ROOT / relative, target)
        helper = runtime / 'ModelBenchmark.exe'
        compiler = Path(os.environ['WINDIR']) / 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
        code, _ = run_process([str(compiler), '/nologo', '/r:System.Web.Extensions.dll',
                               '/out:' + str(helper), str(runtime / 'ModelBenchmark.cs')], runtime / 'compile.log', 45)
        if code:
            raise ValueError('Model helper compilation failed')
        env = dict(os.environ, CADAI_BENCH_MODEL=args.model, CADAI_BENCH_STREAM='0',
                   CADAI_BENCH_DETAILS='1' if args.detailed_images else '0')
        report['status'] = 'preflight'
        save(run / 'report.json', report)
        report['native_preflight'] = native_preflight(run, plugin, identity, env)
        if args.preflight_only:
            report['status'] = 'preflight_passed'
            return 0
        report['status'] = 'running'
        save(run / 'report.json', report)
        for number in range(1, args.trials + 1):
            for case in cases:
                result = trial(case, number, prepared[case['id']], run, plugin, helper, identity, env)
                report['results'].append(result)
                report['summary'] = aggregate(report['results'])
                save(run / 'report.json', report)
                print(f"{case['id']} trial={number} request={result['request_success']} native={result['execution_success']} source={result.get('checks_passed',0)}/{result.get('checks_total',0)} elapsed={result['elapsed_seconds']} error={result.get('error','')}", flush=True)
        report['status'] = 'completed'
        return 0 if all(r['selected_checks_passed'] for r in report['results']) else 1
    except KeyboardInterrupt:
        report['status'] = 'cancelled'
        return 130
    except Exception as error:
        report['status'] = 'failed'
        report['error'] = f'{type(error).__name__}: {error}'
        print(report['error'], flush=True)
        return 1
    finally:
        report['finished_utc'] = datetime.now(timezone.utc).isoformat()
        save(run / 'report.json', report)


if __name__ == '__main__':
    raise SystemExit(main())
