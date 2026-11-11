"""Re-evaluate immutable DXFs with current checks, without new AI/CAD calls."""
import argparse
from datetime import datetime, timezone
from pathlib import Path
import re
import uuid

from benchmark_corpus import ROOT, aggregate, digest, read, save, source_checks


def recheck(run):
    run=Path(run).resolve()
    original=read(run/'report.json')
    if not original.get('results'):
        raise ValueError('Run has no completed attempts to recheck')
    cases={c['id']:c for c in original['cases']}
    target=run/'rechecks'/(datetime.now(timezone.utc).strftime('check-%Y%m%dT%H%M%SZ-')+uuid.uuid4().hex[:8])
    target.mkdir(parents=True,exist_ok=False)
    results=[]
    for previous in original['results']:
        row=dict(previous)
        row.update(selected_checks_passed=False,checks_passed=None,checks_total=None,failed_criteria=[])
        if previous.get('execution_success'):
            try:
                case_id=previous['case']
                if not re.fullmatch(r'[A-Za-z0-9_-]+',case_id):
                    raise ValueError('Invalid stored case ID')
                trial=run/case_id/f"trial-{previous['trial']:02}"
                dxf=trial/'native/drawing.dxf'
                if digest(dxf)!=previous['dxf_sha256']:
                    raise ValueError('DXF changed since the original execution')
                if previous.get('plan_sha256') and digest(trial/'model.plan.json')!=previous['plan_sha256']:
                    raise ValueError('Original AI plan changed since execution')
                result=source_checks(cases[case_id],dxf)
                save(target/f"{case_id}-trial-{previous['trial']:02}.json",result)
                row.update(checks_passed=result['passed'],checks_total=result['total'],
                           selected_checks_passed=result['total']>1 and result['passed']==result['total'],
                           check_scope=result['scope'],
                           failed_criteria=[c['criterion'] for c in result['checks'] if not c['passed']])
            except Exception as error:
                row['recheck_error']=f'{type(error).__name__}: {error}'
        results.append(row)
    report={'source_run_report_sha256':digest(run/'report.json'),'new_model_requests':0,
            'new_native_executions':0,'source_accuracy_verified':False,
            'case_definitions':'Pinned definitions from original report, not the current manifest',
            'current_verifiers':{p:digest(ROOT/p) for p in ['tools/benchmark_corpus.py','tools/verify_section_reproduction.py']},
            'results':results,'summary':aggregate(results)}
    save(target/'report.json',report)
    print('RECHECK='+str(target),flush=True)
    return report,target


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('run',type=Path)
    args=parser.parse_args()
    report,_=recheck(args.run)
    for row in report['summary']:
        print(row['case'],f"{row['selected_check_successes']}/{row['attempts']} selected checks; no fresh requests")
