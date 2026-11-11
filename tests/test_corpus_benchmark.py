"""Synthetic adversarial fixtures validate the evaluator, not AI image accuracy."""
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

import ezdxf
import fitz

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
import benchmark_corpus as benchmark
import verify_section_reproduction as section
import recheck_corpus
import summarize_reasoning


def reference_document():
    doc = ezdxf.new('R2010', setup=True)
    doc.header['$INSUNITS'] = 4
    model = doc.modelspace()
    outer = [(0, 0), (10000, 0), (10000, 11000), (0, 11000)]
    inner = [(1500, 1500), (8500, 1500), (8500, 9500), (1500, 9500)]
    model.add_lwpolyline(outer, close=True)
    model.add_lwpolyline(inner, close=True)
    for p1, p2, base, angle in [((0,0),(10000,0),(0,-1000),0),
                                ((0,0),(0,11000),(-1000,0),90),
                                ((1500,1500),(8500,1500),(0,1000),0),
                                ((1500,1500),(1500,9500),(1000,0),90)]:
        model.add_linear_dim(base=base,p1=p1,p2=p2,angle=angle,
                             override={'dimlfac':.1}).render()
    model.add_text('3-3',dxfattribs={'insert':(4000,-600)})
    model.add_line((4000,-700),(6000,-700))
    model.add_line((5000,-200),(5000,11200),dxfattribs={'linetype':'CENTER'})
    model.add_line((-200,5500),(10200,5500),dxfattribs={'linetype':'CENTER'})
    for name in ['ANSI31','DOTS']:
        hatch=model.add_hatch()
        hatch.set_pattern_fill(name,scale=10)
        hatch.paths.add_polyline_path(outer,is_closed=True,flags=1)
        hatch.paths.add_polyline_path(inner,is_closed=True,flags=0)
    return doc


class SectionVerifierTests(unittest.TestCase):
    def verify(self, document):
        with tempfile.TemporaryDirectory() as folder:
            path=Path(folder)/'fixture.dxf'
            document.saveas(path)
            return section.verify(path)

    def test_valid_actual_section(self):
        result=self.verify(reference_document())
        self.assertEqual((result['passed'],result['total']),(14,14))
        self.assertFalse(result['source_accuracy_verified'])
        self.assertEqual(result['verifier_version'],4)

    def test_diamond_and_bowtie_cannot_pass_as_rectangle(self):
        for points in [[(5000,0),(10000,5500),(5000,11000),(0,5500)],
                       [(0,0),(10000,11000),(10000,0),(0,11000)]]:
            with self.subTest(points=points):
                doc=reference_document()
                next(iter(doc.modelspace().query('LWPOLYLINE'))).set_points(points)
                result=self.verify(doc)
                self.assertFalse(result['checks'][0]['passed'])

    def test_small_arc_bulge_is_not_a_straight_wall(self):
        doc=reference_document()
        outer=next(iter(doc.modelspace().query('LWPOLYLINE')))
        points=list(outer.get_points('xyb'));points[0]=(0,0,.0001)
        outer.set_points(points,format='xyb')
        self.assertFalse(self.verify(doc)['checks'][0]['passed'])

    def test_misplaced_hatch_hole_fails_despite_two_loops(self):
        doc=reference_document()
        for hatch in doc.modelspace().query('HATCH'):
            hatch.paths[1].vertices=[(2000,1500,0),(9000,1500,0),(9000,9500,0),(2000,9500,0)]
        self.assertFalse(self.verify(doc)['checks'][-1]['passed'])

    def test_ignore_islands_and_solid_fill_fail(self):
        for attr in [('hatch_style',2),('solid_fill',1)]:
            with self.subTest(attr=attr):
                doc=reference_document()
                setattr(next(iter(doc.modelspace().query('HATCH'))).dxf,*attr)
                self.assertFalse(self.verify(doc)['checks'][-1]['passed'])

    def test_dimension_on_unrelated_parallel_line_fails(self):
        doc=reference_document()
        dim=next(iter(doc.modelspace().query('DIMENSION')))
        dim.dxf.defpoint2=(0,-1000,0);dim.dxf.defpoint3=(10000,-1000,0)
        self.assertFalse(self.verify(doc)['checks'][3]['passed'])

    def test_valid_mid_edge_extension_points_are_not_forced_to_corners(self):
        doc=reference_document()
        dim=next(iter(doc.modelspace().query('DIMENSION')))
        dim.dxf.defpoint2=(0,5500,0);dim.dxf.defpoint3=(10000,5500,0)
        self.assertEqual(self.verify(doc)['passed'],14)

    def test_aligned_dimension_uses_actual_endpoint_distance(self):
        doc=reference_document();model=doc.modelspace()
        first=next(iter(model.query('DIMENSION')));model.delete_entity(first)
        model.add_aligned_dim(p1=(0,0),p2=(10000,0),distance=-1000,
                              override={'dimlfac':.1}).render()
        self.assertEqual(self.verify(doc)['passed'],14)

    def test_centerline_stubs_do_not_prove_source_axes(self):
        doc=reference_document()
        for line in doc.modelspace().query('LINE'):
            if line.dxf.linetype=='CENTER':
                line.dxf.start=(5000,5499,0);line.dxf.end=(5000,5501,0)
        self.assertFalse(self.verify(doc)['checks'][10]['passed'])

    def test_straight_polyline_underline_is_not_a_missing_source_line(self):
        doc=reference_document();model=doc.modelspace()
        line=next(e for e in model.query('LINE') if e.dxf.start.y==-700)
        model.delete_entity(line)
        model.add_lwpolyline([(4000,-700),(6000,-700)],dxfattribs={'const_width':5})
        self.assertEqual(self.verify(doc)['passed'],14)
        model.add_line((4000,-750),(6000,-750))
        self.assertFalse(self.verify(doc)['checks'][9]['passed'])


class BenchmarkTests(unittest.TestCase):
    def test_budget_comparison_stops_only_on_an_unapplied_or_rejected_option(self):
        self.assertFalse(benchmark.reasoning_unavailable({'request_success':False,'error':'Timeout waiting for reasoning_effort response'},'low'))
        self.assertTrue(benchmark.reasoning_unavailable({'request_success':False,'error':'Unsupported value: reasoning_effort low'},'low'))
        self.assertTrue(benchmark.reasoning_unavailable({'compatibility_warning':'using service default'},'low'))
        self.assertFalse(benchmark.reasoning_unavailable({'compatibility_warning':'other warning'},'default'))

    def test_reasoning_summary_verifies_raw_usage_and_does_not_invent_missing_counts(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);trial=root/'sample/trial-01';trial.mkdir(parents=True)
            payload={'usage':{'completion_tokens':80,'completion_tokens_details':{'reasoning_tokens':60,'text_tokens':20}}}
            benchmark.save(trial/'model.response-01.json',payload)
            row={'case':'sample','trial':1,'request_success':True,'execution_success':True,'selected_checks_passed':False,
                 'elapsed_seconds':15,'model_seconds':10,'checks_passed':7,'checks_total':14,
                 'request_options_capture_available':True,'request_attempts':[{'reasoning_effort_sent':True,'reasoning_effort':'low'}],
                 'raw_responses':[{'file':'model.response-01.json','sha256':benchmark.digest(trial/'model.response-01.json')}]}
            benchmark.save(root/'report.json',{'identity':{'plugin_sha256':'fixed'},'results':[row]})
            result=summarize_reasoning.summarize(root)
            group=result['groups'][0]
            self.assertEqual(group['observed_request_mode'],'sent:low')
            self.assertEqual(group['selected_check_successes'],0)
            self.assertEqual(group['provider_reported_token_medians']['reasoning_tokens'],60)
            self.assertNotIn('prompt_tokens',group['provider_reported_token_medians'])
            (trial/'model.response-01.json').write_text('{}',encoding='utf-8')
            with self.assertRaisesRegex(ValueError,'changed'):
                summarize_reasoning.summarize(root)
            (trial/'model.response-01.json').write_text('<html>upstream unavailable</html>',encoding='utf-8')
            row['raw_responses'][0]['sha256']=benchmark.digest(trial/'model.response-01.json')
            row['request_success']=False;row['execution_success']=False;row['model_seconds']=240
            benchmark.save(root/'report.json',{'identity':{'plugin_sha256':'fixed'},'results':[row]})
            result=summarize_reasoning.summarize(root)
            self.assertEqual(result['groups'][0]['model_seconds_median'],240)
            self.assertEqual(result['groups'][0]['provider_reported_token_medians'],{})

    def test_recheck_preserves_original_and_rejects_changed_native_files(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);native=root/'sixth-section33/trial-01/native';native.mkdir(parents=True)
            dxf=native/'drawing.dxf';reference_document().saveas(dxf)
            report={'cases':[{'id':'sixth-section33'}],'results':[
                {'case':'sixth-section33','trial':1,'request_success':True,'execution_success':True,
                 'dxf_sha256':benchmark.digest(dxf),'stage':'completed','elapsed_seconds':20},
                {'case':'sixth-section33','trial':2,'request_success':False,'execution_success':False,
                 'stage':'request','elapsed_seconds':240,'error':'timeout'}]}
            benchmark.save(root/'report.json',report)
            before=(root/'report.json').read_bytes()
            checked,_=recheck_corpus.recheck(root)
            self.assertEqual(checked['summary'][0]['selected_check_successes'],1)
            self.assertEqual(checked['summary'][0]['attempts'],2)
            self.assertIsNone(checked['results'][1]['checks_total'])
            self.assertEqual(checked['new_model_requests'],0)
            dxf.write_bytes(b'changed after evaluation')
            checked,_=recheck_corpus.recheck(root)
            self.assertIn('DXF changed',checked['results'][0]['recheck_error'])
            self.assertFalse(checked['results'][0]['selected_checks_passed'])
            self.assertEqual((root/'report.json').read_bytes(),before)

    def test_windows_powershell_child_does_not_inherit_other_shell_modules(self):
        original={'PSMODULEPATH':'wrong/ps7/modules','CADAI_BENCH_MODEL':'auto','PATH':'path'}
        result=benchmark.child_environment(['powershell','-NoProfile'],original)
        self.assertNotIn('PSMODULEPATH',result)
        self.assertEqual(result['CADAI_BENCH_MODEL'],'auto')
        self.assertIn('PSMODULEPATH',original)
        self.assertEqual(benchmark.child_environment(['python','worker.py'],original),original)

    def test_reading_multiset_uses_measured_geometry_not_override_text(self):
        doc=reference_document()
        self.assertCountEqual(benchmark.dimension_readings(doc),[1000,1100,700,800])
        first=next(iter(doc.modelspace().query('DIMENSION')))
        first.dxf.text='1000'
        self.assertCountEqual(benchmark.dimension_readings(doc),[1100,700,800])

    def test_repeated_source_readings_require_repeated_dimensions(self):
        with tempfile.TemporaryDirectory() as folder:
            path=Path(folder)/'drawing.dxf';reference_document().saveas(path)
            result=benchmark.source_checks({'id':'synthetic','dimensions':[1000,1000]},path)
            self.assertFalse(result['checks'][1]['passed'])
            self.assertEqual(result['checks'][1]['actual_count'],1)

    def test_aggregate_counts_failures_and_separates_configuration_changes(self):
        rows=[{'case':'a','trial':1,'elapsed_seconds':120,'stage':'request','error':'timeout'},
              {'case':'a','trial':2,'elapsed_seconds':30,'stage':'completed','model_configuration_sha256':'v2',
               'request_success':True,'execution_success':True,'selected_checks_passed':True},
              {'case':'a','trial':3,'elapsed_seconds':40,'stage':'completed','model_configuration_sha256':'v2',
               'request_success':True,'execution_success':True,'selected_checks_passed':False}]
        groups=benchmark.aggregate(rows)
        self.assertEqual(sum(g['attempts'] for g in groups),3)
        self.assertEqual(len(groups),2)
        self.assertEqual(groups[1]['selected_check_successes'],1)
        self.assertEqual(groups[1]['elapsed_median_seconds'],35)
        self.assertEqual(groups[0]['failures'][0]['error'],'timeout')

    def test_source_escape_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            with self.assertRaises(ValueError):
                benchmark.case_source(Path(folder)/'root',{'source':'../../outside.pdf'})

    def test_prepare_is_fresh_and_never_calls_models(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);(root/'tests').mkdir();(root/'source').mkdir()
            with fitz.open() as pdf:
                pdf.new_page().draw_rect(fitz.Rect(0,0,20,20))
                pdf.save(root/'source/source.pdf')
            case={'id':'fixture','source':'source.pdf','page':1,'sourceUnit':'mm','crop':[0,0,.5,.5]}
            (root/'tests/corpus_cases.json').write_text(json.dumps({'cases':[case]}),encoding='utf-8')
            args=['benchmark','--prepare-only','--case','fixture','--source-root',str(root/'source'),'--output',str(root/'out')]
            with patch.object(benchmark,'ROOT',root),patch.object(sys,'argv',args),patch.object(benchmark,'run_process') as network:
                self.assertEqual(benchmark.main(),0);self.assertEqual(benchmark.main(),0)
                network.assert_not_called()
            runs=list((root/'out').iterdir());self.assertEqual(len(runs),2)
            for run in runs:
                report=benchmark.read(run/'report.json')
                self.assertEqual(report['status'],'prepared');self.assertEqual(report['results'],[])
                self.assertEqual(report['sources']['fixture']['source_sha256'],benchmark.digest(root/'source/source.pdf'))
            scheduled=args+['--reasoning-schedule','default','low','low','default','default','low']
            with patch.object(benchmark,'ROOT',root),patch.object(sys,'argv',scheduled),patch.object(benchmark,'run_process') as network:
                self.assertEqual(benchmark.main(),0);network.assert_not_called()
            schedules=[benchmark.read(r/'report.json') for r in (root/'out').iterdir()]
            scheduled_report=next(r for r in schedules if r['requested_trials']==6)
            self.assertEqual(scheduled_report['reasoning_schedule'],['default','low','low','default','default','low'])

    def test_preparation_failure_still_persists_report(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);(root/'tests').mkdir()
            (root/'tests/corpus_cases.json').write_text(json.dumps({'cases':[{'id':'fixture','source':'missing.pdf','page':1}]}),encoding='utf-8')
            args=['benchmark','--prepare-only','--case','fixture','--source-root',str(root),'--output',str(root/'out')]
            with patch.object(benchmark,'ROOT',root),patch.object(sys,'argv',args):
                self.assertEqual(benchmark.main(),1)
            report=benchmark.read(next((root/'out').iterdir())/'report.json')
            self.assertEqual(report['status'],'failed');self.assertIn('FileNotFoundError',report['error'])


if __name__=='__main__':
    unittest.main()
