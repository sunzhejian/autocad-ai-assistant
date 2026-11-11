"""Read actual exported dimension styles/extents after source-size conversion."""
import argparse
import json
from pathlib import Path

import ezdxf
from ezdxf import bbox


def verify(path):
    doc=ezdxf.readfile(path)
    dimensions=list(doc.modelspace().query('DIMENSION'))
    checks=[{'criterion':'one native measured dimension','passed':len(dimensions)==1}]
    if dimensions:
        d=dimensions[0]
        style=d.override()
        expected={'dimscale':100,'dimtxt':3.5,'dimasz':2.5,'dimtsz':2.5,
                  'dimexo':1,'dimexe':1.5,'dimgap':1,'dimlfac':.1}
        for name,value in expected.items():
            actual=float(style.get(name,0))
            checks.append({'criterion':name,'expected':value,'actual':actual,'passed':abs(actual-value)<1e-8})
        bounds=bbox.extents([d])
        checks.append({'criterion':'dimension glyphs remain within a 2500 mm envelope for a 1000 mm measurement',
                       'passed':bounds.size.x<2500 and bounds.size.y<2500,
                       'actual_size_mm':[bounds.size.x,bounds.size.y]})
        checks.append({'criterion':'source reading comes from actual 1000 mm geometry',
                       'passed':abs(float(d.get_measurement())-1000)<1e-8 and d.dxf.get('text','') in ('','<>')})
    return {'checks':checks,'passed':sum(c['passed'] for c in checks),'total':len(checks),
            'scope':'Native source dimension-size conversion only; not image fidelity.'}


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('dxf',type=Path)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    report=verify(args.dxf)
    args.output.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps(report,ensure_ascii=False,indent=2))
    raise SystemExit(0 if report['passed']==report['total'] else 1)
