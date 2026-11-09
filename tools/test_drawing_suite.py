"""Real configured plugin AI -> unchanged plan -> isolated AutoCAD -> DXF semantics.

Needs an existing model configuration, compiled plugin/ModelBenchmark, AutoCAD,
and ezdxf. Requests consume the configured provider's quota. No credentials are
copied into reports. Expected geometry is fixed before asking the model.
"""
import argparse
import concurrent.futures
import hashlib
import json
import math
import os
from pathlib import Path
import shutil
import subprocess
import time

import ezdxf

ROOT = Path(__file__).resolve().parents[1]
CASES = [
    ("basic", "直线、矩形、圆、正六边形、点",
     "分别在指定图层绘制：B_LINE直线(0,0)到(30,40)；B_RECT矩形对角(60,0)、(100,20)；B_CIRCLE圆心(140,10)半径10；B_HEX正六边形中心(200,0)外接圆半径10；B_POINT点(230,15)。"),
    ("curves", "圆弧、椭圆、样条",
     "C_ARC图层画圆心(0,0)、半径20、从0度逆时针到90度的圆弧；C_ELLIPSE图层画中心(80,0)、沿X轴长半轴30、短长轴比0.5的完整椭圆；C_SPLINE图层画通过拟合点(140,0)、(160,20)、(180,0)的样条。"),
    ("dimensions", "线性、对齐、半径、直径标注",
     "D_LINEAR图层添加端点(0,0)、(30,40)的水平线性标注，标注位置(15,-10)，真实读数30；D_ALIGNED图层添加同两端点的对齐标注，位置(-10,20)，读数50；画圆心(100,0)、半径20的圆，在D_RADIUS图层创建真实半径标注，圆周点(120,0)，读数20；在D_DIAMETER图层创建同圆的真实直径标注，圆周点(100,20)，读数40。"),
    ("hatch", "斜线、颗粒填充及内孔",
     "H_DIAGONAL与H_DOTS两图层分别画完全相同外边界(0,0)、(100,0)、(100,80)、(0,80)，内孔(20,20)、(80,20)、(80,60)、(20,60)的原生填充。分别指定ANSI31与DOTS图案，spacing=5、angle=0。内孔留空，不要用白色实心块遮挡。"),
    ("concrete", "混凝土箱涵剖面、材料填充和引线关联",
     "绘制混凝土箱涵剖面测试示例。先standard，再设DIMSCALE=10、DIMTXT=3.5、DIMASZ=2.5。C_OUTER层闭合外轮廓(0,0)-(1200,900)，C_INNER层闭合内孔(150,150)-(1050,750)。C_CONCRETE层使用material=concrete、spacing=30的原生HATCH，外边界和内孔分别对应上述两矩形，空洞不填。C_LEADER层原生leader注明C30混凝土，material=concrete、targetLayer=C_CONCRETE，箭头尖(75,450)位于左侧实墙内，折点(-150,650)、文字位置(-450,650)，字高35、箭头大小20。C_DIM层创建四条真实线性标注：外宽1200放在y=-150；外高900放在x=1400；内宽900放在y=300；内高600放在x=900。保持单色黑线，轮廓粗、材料纹理细，不要黑色实心三角形或矩形。"),
    ("blocks", "块、平移、旋转、缩放",
     "定义块TEST_MARK，内部仅圆心(0,0)半径5的圆；在K_BLOCK图层插入3次：位置(0,0)比例1旋转0度；位置(40,0)比例2旋转90度；位置(80,0)比例0.5旋转180度。"),
    ("annotations", "单行、多行文字、引线、实心区域",
     "T_TEXT图层单行文字TEST高度3.5位置(0,0)；T_MTEXT图层多行文字第一行说明、第二行复核，高度3.5位置(30,0)；T_LEADER图层用原生leader注释引线测试，箭头点(0,40)、折点(20,60)、文字位置(30,60)、字高3.5、箭头大小2；T_TRI图层实心三角形按边界顺序(80,0)、(110,0)、(80,40)；T_QUAD图层实心凸四边形按边界顺序(140,0)、(180,0)、(180,20)、(140,20)。"),
    ("rings", "圆环及实心圆盘",
     "R_RING图层用donut绘制圆心(0,0)内径20外径40的带宽圆环；R_DISK图层用donut绘制圆心(80,0)内径0外径30的实心圆盘。不能用两个空心圆轮廓替代带宽圆环。"),
    ("bulges", "正向、反向圆弧多段线",
     "P_CCW图层绘制原生多段线，从(0,0)到(40,0)为逆时针半圆，使用bulges=[1,0]；P_CW图层从(80,0)到(120,0)为顺时针半圆，使用bulges=[-1,0]。两条均开放，不用短线拼接。"),
    ("angular", "45度与90度原生角度标注",
     "A_45图层创建dimangular，顶点(0,0)，两条射线上的点(100,0)、(100,100)，标注弧位置(60,20)，真实角度45度；A_90图层创建dimangular，顶点(200,0)，射线上点(300,0)、(200,100)，标注弧位置(250,50)，真实角度90度。绘制所需射线，不用文字冒充角度标注。"),
    ("source_cm", "厘米源单位到毫米",
     "本题所有输入坐标和长度单位是厘米，请声明sourceUnit=cm、coordinateUnit=source并按源值输出。U_CIRCLE图层圆心(20,30)、半径5厘米；U_ALIGNED图层创建端点(0,0)、(3,4)的真实对齐尺寸，标注位置(-1,2)，value=5厘米。")
]


def near(a, b, tolerance=1e-4):
    return abs(float(a) - float(b)) <= tolerance


def xy(a, b):
    return near(a[0], b[0]) and near(a[1], b[1])


def area(points):
    return abs(sum(a[0]*b[1]-b[0]*a[1] for a, b in zip(points, points[1:]+points[:1]))) / 2


def verify(case, dxf):
    doc = ezdxf.readfile(dxf)
    model = doc.modelspace()
    checks = []

    def add(name, result):
        checks.append({"criterion": name, "passed": bool(result)})

    def one(layer, kind, predicate):
        items = [e for e in model if e.dxf.layer == layer and e.dxftype() == kind]
        return len(items) == 1 and predicate(items[0])

    def dim(layer, value, kind):
        # ezdxf's aligned-dimension helper projects along the optional angle,
        # which AutoCAD does not write for an aligned dimension. Read endpoints.
        def measurement(d):
            if d.dimtype==1:
                a,b=d.dxf.defpoint2,d.dxf.defpoint3
                return math.hypot(b.x-a.x,b.y-a.y)
            return d.get_measurement()
        return one(layer, "DIMENSION", lambda d: d.dimtype == kind and near(measurement(d), value)
                   and d.dxf.get("text", "") in ("", "<>"))

    add("native model uses millimeters", doc.header.get("$INSUNITS") == 4)
    if case == "basic":
        add("line endpoints and length", one("B_LINE", "LINE", lambda e: xy(e.dxf.start,(0,0)) and xy(e.dxf.end,(30,40))))
        add("rectangle has four correct corners and is closed", one("B_RECT", "LWPOLYLINE", lambda e: e.closed and len(e)==4 and sorted((round(p[0],4),round(p[1],4)) for p in e)==[(60,0),(60,20),(100,0),(100,20)]))
        add("circle center and radius", one("B_CIRCLE", "CIRCLE", lambda e: xy(e.dxf.center,(140,10)) and near(e.dxf.radius,10)))
        add("regular hexagon has six radius-10 vertices", one("B_HEX", "LWPOLYLINE", lambda e: e.closed and len(e)==6 and all(near(math.hypot(p[0]-200,p[1]),10) for p in e)))
        add("point position", one("B_POINT", "POINT", lambda e: xy(e.dxf.location,(230,15))))
    elif case == "curves":
        add("arc center radius and sweep", one("C_ARC", "ARC", lambda e: xy(e.dxf.center,(0,0)) and near(e.dxf.radius,20) and near(e.dxf.start_angle,0) and near(e.dxf.end_angle,90)))
        add("ellipse center axis and ratio", one("C_ELLIPSE", "ELLIPSE", lambda e: xy(e.dxf.center,(80,0)) and xy(e.dxf.major_axis,(30,0)) and near(e.dxf.ratio,.5)))
        add("spline retains all three fit points", one("C_SPLINE", "SPLINE", lambda e: len(e.fit_points)==3 and all(xy(a,b) for a,b in zip(e.fit_points,[(140,0),(160,20),(180,0)]))))
    elif case == "dimensions":
        for layer, value, kind in [("D_LINEAR",30,0),("D_ALIGNED",50,1),("D_RADIUS",20,4),("D_DIAMETER",40,3)]:
            add(layer+" is a native measured dimension without text override", dim(layer,value,kind))
    elif case == "hatch":
        for layer, pattern in [("H_DIAGONAL","ANSI31"),("H_DOTS","DOTS")]:
            def correct(e):
                loops=list(e.paths)
                if len(loops)!=2 or not all(hasattr(p,"vertices") for p in loops):
                    return False
                sizes=sorted(area(list(p.vertices)) for p in loops)
                return e.dxf.pattern_name==pattern and near(sizes[0],2400) and near(sizes[1],8000)
            add(pattern+" preserves actual outer area and inner hole", one(layer,"HATCH",correct))
    elif case == "concrete":
        def rectangle(layer, corners):
            return one(layer,"LWPOLYLINE",lambda e:e.closed and len(e)==4 and sorted((round(p[0],4),round(p[1],4)) for p in e)==sorted(corners))
        add("closed outer section is 1200 x 900 mm",rectangle("C_OUTER",[(0,0),(1200,0),(1200,900),(0,900)]))
        add("open interior is 900 x 600 mm with 150 mm walls",rectangle("C_INNER",[(150,150),(1050,150),(1050,750),(150,750)]))
        def concrete_hatch(e):
            loops=list(e.paths)
            return e.dxf.pattern_name=="AR-CONC" and len(loops)==2 and all(hasattr(p,"vertices") for p in loops) and sorted(round(area(list(p.vertices)),3) for p in loops)==[540000,1080000]
        add("concrete is a native patterned material with an empty hole",one("C_CONCRETE","HATCH",concrete_hatch))
        def material_note(e):
            tips=[line.vertices[0] for cluster in e.context.leaders for line in cluster.lines if line.vertices]
            return e.context.mtext is not None and "C30混凝土" in e.context.mtext.default_content and any(xy(t,(75,450)) for t in tips)
        add("C30 annotation arrow actually lands inside the concrete wall",one("C_LEADER","MULTILEADER",material_note))
        dims=[e for e in model.query("DIMENSION") if e.dxf.layer=="C_DIM"]
        add("four dimensions retain actual source sizes",len(dims)==4 and sorted(round(e.get_measurement(),3) for e in dims)==[600,900,900,1200] and all(e.dxf.get("text","") in ("","<>") for e in dims))
        add("no unrelated black solid placeholders",not list(model.query("SOLID")))
    elif case == "blocks":
        inserts=[e for e in model.query("INSERT") if e.dxf.layer=="K_BLOCK"]
        add("three native block references",len(inserts)==3)
        for x, scale, rotation in [(0,1,0),(40,2,90),(80,.5,180)]:
            found=[e for e in inserts if xy(e.dxf.insert,(x,0))]
            add(f"block at {x}: transform and actual circle radius",len(found)==1 and near(found[0].dxf.xscale,scale)
                and near(found[0].dxf.rotation,rotation) and any(e.dxftype()=="CIRCLE" and xy(e.dxf.center,(x,0)) and near(e.dxf.radius,5*scale) for e in found[0].virtual_entities()))
    elif case == "annotations":
        add("single-line text content height and position",one("T_TEXT","TEXT",lambda e:e.dxf.text=="TEST" and near(e.dxf.height,3.5) and xy(e.dxf.insert,(0,0))))
        add("multi-line text contains both requested lines",one("T_MTEXT","MTEXT",lambda e:e.plain_text().splitlines()==["说明","复核"] and near(e.dxf.char_height,3.5)))
        def leader(e):
            tips=[line.vertices[0] for cluster in e.context.leaders for line in cluster.lines if line.vertices]
            vertices=[v for cluster in e.context.leaders for line in cluster.lines for v in line.vertices]
            return e.context.mtext is not None and "引线测试" in e.context.mtext.default_content and any(xy(t,(0,40)) for t in tips) and any(xy(v,(20,60)) for v in vertices)
        add("native leader retains arrow tip bend and Chinese annotation",one("T_LEADER","MULTILEADER",leader))
        for layer, expected in [("T_TRI",600),("T_QUAD",800)]:
            add(layer+" native solid has correct area",one(layer,"SOLID",lambda e:near(area([tuple(e.dxf.get("vtx"+str(i))) for i in [0,1,3,2]]),expected)))
    elif case == "rings":
        for layer, center, inner, outer in [("R_RING",(0,0),10,20),("R_DISK",(80,0),0,15)]:
            def ring(e):
                if len(e)!=2 or not e.closed:
                    return False
                p=list(e.get_points("xyseb")); r=math.hypot(p[0][0]-p[1][0],p[0][1]-p[1][1])/2
                width=e.dxf.get("const_width",0) or p[0][2]
                return xy(((p[0][0]+p[1][0])/2,(p[0][1]+p[1][1])/2),center) and near(r-width/2,inner) and near(r+width/2,outer) and all(near(v[4],1) for v in p)
            add(layer+" actual inner outer radii and native curved width",one(layer,"LWPOLYLINE",ring))
    elif case == "bulges":
        for layer, start, end, bulge in [("P_CCW",(0,0),(40,0),1),("P_CW",(80,0),(120,0),-1)]:
            def curved(e):
                p=list(e.get_points("xyb"))
                return len(p)==2 and not e.closed and xy(p[0],start) and xy(p[1],end) and near(p[0][2],bulge) and near(p[1][2],0)
            add(layer+" has exact endpoints and native semicircle direction",one(layer,"LWPOLYLINE",curved))
    elif case == "angular":
        add("native 45 degree angle",dim("A_45",45,5))
        add("native 90 degree angle",dim("A_90",90,5))
    elif case == "source_cm":
        add("source cm circle becomes correct mm geometry",one("U_CIRCLE","CIRCLE",lambda e:xy(e.dxf.center,(200,300)) and near(e.dxf.radius,50)))
        add("source cm aligned dimension measures actual 50 mm geometry",dim("U_ALIGNED",50,1))
    return checks


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model",default="auto")
    parser.add_argument("--output",type=Path,required=True)
    parser.add_argument("--case",action="append",dest="selected")
    parser.add_argument("--execute-only",action="store_true",help="Replay already saved AI output; does not make fresh AI requests")
    args=parser.parse_args()
    output=args.output.resolve();output.mkdir(parents=True,exist_ok=True)
    cases=[c for c in CASES if not args.selected or c[0] in args.selected]
    if not cases:
        parser.error("No matching cases")
    plugin=ROOT/"bin/Release/AutoCAD.AI.Assistant.dll"
    benchmark=ROOT/"artifacts/ModelBenchmark.exe"
    compiler=Path(os.environ["WINDIR"])/"Microsoft.NET/Framework64/v4.0.30319/csc.exe"
    subprocess.run([str(compiler),"/nologo","/r:System.Web.Extensions.dll",f"/out:{benchmark}",str(ROOT/"tools/ModelBenchmark.cs")],check=True,cwd=ROOT)
    env=dict(os.environ,CADAI_BENCH_MODEL=args.model)
    started=time.time()

    def request(case):
        key,title,prompt=case
        prompt="测试绘图。除明确指定外全部单位为毫米，不加图框、标题或额外标注。按要求使用原生图元及指定图层，保留真实尺寸，不用固定文字覆盖测量值。"+prompt
        (output/(key+".prompt.txt")).write_text(prompt,encoding="utf-8")
        report=output/(key+".json")
        try:
            if not args.execute_only:
                result=subprocess.run([str(benchmark),str(plugin),"-",str(report),prompt],cwd=ROOT,env=env,capture_output=True,timeout=280)
                (output/(key+".request.log")).write_bytes(result.stdout+result.stderr)
            data=json.loads(report.read_text(encoding="utf-8-sig"))
        except Exception as error:
            data={"success":False,"error":str(error),"seconds":None}
            report.write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding="utf-8")
        print(f"AI {key}: success={data.get('success')} seconds={data.get('seconds')}",flush=True)
        return {"id":key,"title":title,"model_request_success":data.get("success",False),"seconds":data.get("seconds"),
                "request_error":data.get("error"),"prompt_sha256":hashlib.sha256(prompt.encode()).hexdigest(),"checks":[]}

    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        results=list(pool.map(request,cases))
    for result in results:
        key=result["id"]
        try:
            plan=output/(key+".plan.json")
            if not result["model_request_success"] or not plan.exists():
                raise ValueError(result.get("request_error") or "Model did not return a drawing plan")
            name="suite-"+key
            execution=subprocess.run(["powershell","-NoProfile","-ExecutionPolicy","Bypass","-File",str(ROOT/"tools/Reproduce-Cad.ps1"),"-Plan",str(plan),"-Name",name],cwd=ROOT,capture_output=True,timeout=155)
            (output/(key+".execution.log")).write_bytes(execution.stdout+execution.stderr)
            if execution.returncode:
                raise ValueError("Native execution failed; see execution log")
            native=json.loads((ROOT/"artifacts/reproductions"/(name+".native.json")).read_text(encoding="utf-8-sig"))
            if any(r.get("error") or r.get("passed") is False for r in native):
                raise ValueError("Native plan was rejected")
            result["execution_success"]=True
            result["plan_sha256"]=hashlib.sha256(plan.read_bytes()).hexdigest()
            for extension in (".dwg",".dxf",".native.json"):
                shutil.copy2(ROOT/"artifacts/reproductions"/(name+extension),output/(key+extension))
            result["checks"]=verify(key,output/(key+".dxf"))
        except Exception as error:
            result["error"]=str(error)
        result["passed"]=bool(result["checks"]) and all(c["passed"] for c in result["checks"]) and not result.get("error")
        print(f"CAD {key}: passed={result['passed']} checks={sum(c['passed'] for c in result['checks'])}/{len(result['checks'])} error={result.get('error','')}",flush=True)
    report={"model":args.model,"plugin_sha256":hashlib.sha256(plugin.read_bytes()).hexdigest(),"fresh_requests":not args.execute_only,
            "source_accuracy_verified":False,"scope":"Fixed text tasks only; no guarantee of arbitrary image fidelity.","elapsed_seconds":round(time.time()-started,2),"cases":results}
    (output/"report.json").write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding="utf-8")
    lines=["# 插件 AI 实际绘图测试","",f"模型 ID：{args.model}；独立文字任务；原始 AI 计划未经人工改写。","","| 测试 | 请求秒数 | CAD语义检查 | 结果 |","| --- | ---: | ---: | --- |"]
    for r in results:
        lines.append(f"| {r['title']} | {r['seconds']} | {sum(c['passed'] for c in r['checks'])}/{len(r['checks'])} | {'通过' if r['passed'] else '未通过'} |")
    lines.extend(["","通过表示选定的实际图元和数值符合该固定任务，不能推及任意赛题或图片识别。"])
    (output/"report.md").write_text("\n".join(lines)+"\n",encoding="utf-8")
    return 0 if all(r["passed"] for r in results) else 1


if __name__=="__main__":
    raise SystemExit(main())
