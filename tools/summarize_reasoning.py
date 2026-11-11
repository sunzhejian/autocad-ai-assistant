"""Summarize a fixed-DLL budget experiment; never infer backend identity/billing."""
import argparse
from collections import defaultdict
from pathlib import Path
import statistics

from benchmark_corpus import digest, read, save


def usage(folder, result):
    totals={}
    coverage=0
    for captured in result.get('raw_responses',[]):
        path=(folder/captured['file']).resolve()
        if not path.is_relative_to(folder.resolve()) or digest(path)!=captured['sha256']:
            raise ValueError('Raw response changed or leaves trial directory')
        try:payload=read(path)
        except ValueError:continue  # Captured HTML/malformed error bodies have unknown usage, not zero usage.
        data=payload.get('usage') if isinstance(payload,dict) else None
        if not isinstance(data,dict):continue
        coverage+=1
        details=data.get('completion_tokens_details') or {}
        if not isinstance(details,dict):details={}
        values={k:data.get(k) for k in ['prompt_tokens','completion_tokens','total_tokens']}
        values.update(reasoning_tokens=details.get('reasoning_tokens'),visible_text_tokens=details.get('text_tokens'))
        for key,value in values.items():
            if type(value) is int and value>=0:totals[key]=totals.get(key,0)+value
    return totals,coverage


def summarize(run):
    run=Path(run).resolve();report=read(run/'report.json')
    groups=defaultdict(list)
    for row in report['results']:
        options=row.get('request_attempts') or []
        if not row.get('request_options_capture_available') or not options:
            sent='unobserved'
        elif options[-1].get('reasoning_effort_sent'):
            sent='sent:'+str(options[-1].get('reasoning_effort'))
        elif row.get('compatibility_warning'):
            sent='omitted_after_rejection'
        else:sent='omitted_service_default'
        folder=run/row['case']/f"trial-{row['trial']:02}"
        if not folder.resolve().is_relative_to(run):raise ValueError('Invalid stored trial directory')
        tokens,coverage=usage(folder,row)
        entry={'trial':row['trial'],'requested_override':row.get('reasoning_effort_override'),
               'observed_request_mode':sent,'model_seconds':row.get('model_seconds'),
               'elapsed_seconds':row['elapsed_seconds'],'request_success':row['request_success'],
               'execution_success':row['execution_success'],'selected_checks_passed':row['selected_checks_passed'],
               'checks_passed':row.get('checks_passed'),'checks_total':row.get('checks_total'),
               'http_attempts':len(options) if row.get('request_options_capture_available') else None,
               'provider_reported_tokens':tokens,'responses_with_usage':coverage,
               'failed_criteria':row.get('failed_criteria',[])}
        groups[(row['case'],sent,row.get('model_configuration_sha256'))].append(entry)
    rows=[]
    for (case,mode,configuration),attempts in groups.items():
        measured=[r['model_seconds'] for r in attempts if r['model_seconds'] is not None]
        token_keys={k for r in attempts for k in r['provider_reported_tokens']}
        token_medians={k:statistics.median([r['provider_reported_tokens'][k] for r in attempts if k in r['provider_reported_tokens']]) for k in token_keys}
        rows.append({'case':case,'observed_request_mode':mode,'configuration_sha256':configuration,
                     'attempts':len(attempts),'model_timing_count':len(measured),
                     'model_seconds_median':statistics.median(measured) if measured else None,
                     'elapsed_seconds_median':statistics.median([r['elapsed_seconds'] for r in attempts]),
                     'request_successes':sum(r['request_success'] for r in attempts),
                     'execution_successes':sum(r['execution_success'] for r in attempts),
                     'selected_check_successes':sum(r['selected_checks_passed'] for r in attempts),
                     'provider_reported_token_medians':token_medians,'trials':attempts})
    result={'run_report_sha256':digest(run/'report.json'),'plugin_sha256':report['identity']['plugin_sha256'],
            'schedule':report.get('reasoning_schedule'),'source_accuracy_verified':False,'groups':rows,
            'limits':['Request options record what was sent, not proof that a compatible gateway honored it.',
                      'Usage is provider-reported and may be incomplete; it is not account balance or an invoice.',
                      'Small samples and an unidentified auto route do not establish general latency/quality causality.',
                      'Source checks cover only their declared subset, not pixel or full-drawing fidelity.']}
    save(run/'reasoning-summary.json',result)
    return result


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('run',type=Path)
    args=parser.parse_args()
    for group in summarize(args.run)['groups']:
        print(group['observed_request_mode'],'attempts=',group['attempts'],
              'model_median_s=',group['model_seconds_median'],
              'selected_pass=',str(group['selected_check_successes'])+'/'+str(group['attempts']),
              'provider_token_medians=',group['provider_reported_token_medians'])
