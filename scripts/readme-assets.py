#!/usr/bin/env python3
"""Generate the README art in docs/assets/: animated hero, pipeline and architecture SVGs (light + dark, shown through
<picture>) and social-preview.svg. Usage: python3 scripts/readme-assets.py [out-dir]

The SVGs animate with SMIL only (no scripts, no external fonts: GitHub renders them through <img>). The query, rows and
runtime facts drawn here are real sandbox output and repo facts; keep them in sync with the README.

social-preview.png (the repo's GitHub social preview, uploaded by hand in Settings) is rendered from the SVG, e.g.
  chrome --headless=new --hide-scrollbars --window-size=1280,640 --screenshot=docs/assets/social-preview.png     file:///<repo>/docs/assets/social-preview.svg
"""
import os
import sys

OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "docs", "assets")
MONO = "ui-monospace,SFMono-Regular,'SF Mono',Menlo,Consolas,'Liberation Mono',monospace"
SANS = "-apple-system,BlinkMacSystemFont,'Segoe UI',Helvetica,Arial,sans-serif"

PALETTES = {
    "dark": dict(bg="#0d1117", card="#161b22", card2="#1c2330", bd="#30363d", tx="#e6edf3", mu="#8b949e",
                 ac="#39d0d8", vi="#a371f7", gr="#3fb950", orr="#f0883e", pk="#ff7b72", ye="#e3b341", g1="#39d0d8", g2="#a371f7"),
    "light": dict(bg="#ffffff", card="#f6f8fa", card2="#eef2f6", bd="#d0d7de", tx="#1f2328", mu="#59636e",
                  ac="#0b7f8a", vi="#8250df", gr="#1a7f37", orr="#bc4c00", pk="#cf222e", ye="#9a6700", g1="#0b7f8a", g2="#8250df"),
}


def style(p):
    return f"""<style>
.bg{{fill:{p['bg']}}}.card{{fill:{p['card']}}}.card2{{fill:{p['card2']}}}.bd{{stroke:{p['bd']}}}
.tx{{fill:{p['tx']}}}.mu{{fill:{p['mu']}}}.ac{{fill:{p['ac']}}}.vi{{fill:{p['vi']}}}.gr{{fill:{p['gr']}}}
.or{{fill:{p['orr']}}}.pk{{fill:{p['pk']}}}.ye{{fill:{p['ye']}}}
.sac{{stroke:{p['ac']}}}.svi{{stroke:{p['vi']}}}.smu{{stroke:{p['mu']}}}.sgr{{stroke:{p['gr']}}}
.mono{{font-family:{MONO}}}.sans{{font-family:{SANS}}}
</style>"""


def svg(w, h, p, body, title):
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w} {h}" width="{w}" height="{h}" role="img" '
            f'aria-label="{title}"><title>{title}</title>{style(p)}'
            f'<defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="{p["g1"]}"/>'
            f'<stop offset="1" stop-color="{p["g2"]}"/></linearGradient></defs>{body}</svg>')


def kt(*xs):
    return ";".join(f"{x:.4f}".rstrip("0").rstrip(".") if x not in (0, 1) else str(int(x)) for x in xs)


# ---------------------------------------------------------------- hero
def hero(p):
    W, H, D = 1200, 320, 10.0
    b = [f'<rect class="bg" width="{W}" height="{H}"/>',
         f'<rect class="card bd" x="1" y="1" width="{W-2}" height="{H-2}" rx="18" stroke-width="1.5"/>']
    # faint background graph
    nodes = [(460, 60), (520, 120), (470, 210), (560, 270), (430, 290), (540, 40), (590, 180)]
    edges = [(0, 1), (1, 2), (2, 3), (2, 4), (0, 5), (1, 6), (6, 3)]
    for a, c in edges:
        (x1, y1), (x2, y2) = nodes[a], nodes[c]
        b.append(f'<line class="smu" x1="{x1}" y1="{y1}" x2="{x2}" y2="{y2}" stroke-width="1.2" opacity=".35"/>')
    for i, (x, y) in enumerate(nodes):
        b.append(f'<circle class="ac" cx="{x}" cy="{y}" r="4" opacity=".5"><animate attributeName="opacity" '
                 f'values=".2;.9;.2" dur="3.2s" begin="{i*0.45:.2f}s" repeatCount="indefinite"/></circle>')
    # wordmark
    b.append(f'<text x="60" y="148" class="sans" font-size="104" font-weight="800" fill="url(#g)" letter-spacing="-2">RQL</text>')
    b.append(f'<text x="64" y="194" class="sans tx" font-size="27" font-weight="600">Reflection Query Language</text>')
    b.append(f'<text x="64" y="230" class="sans mu" font-size="17">Query and safely change live object graphs,</text>')
    b.append(f'<text x="64" y="254" class="sans mu" font-size="17">one precise call per question from an AI agent.</text>')
    x = 64
    for label, cls in ((".NET", "vi"), ("TypeScript", "ac"), ("C++", "or")):
        w = 18 + len(label) * 8.6
        b.append(f'<rect x="{x}" y="270" width="{w:.0f}" height="26" rx="13" class="card2 bd" stroke-width="1"/>'
                 f'<text x="{x + w/2:.0f}" y="288" text-anchor="middle" class="mono {cls}" font-size="13" font-weight="600">{label}</text>')
        x += w + 10
    # terminal
    tx0, ty0, tw, th = 610, 36, 556, 248
    b.append(f'<rect x="{tx0}" y="{ty0}" width="{tw}" height="{th}" rx="12" class="bg bd" stroke-width="1.2"/>')
    for i, c in enumerate(("pk", "ye", "gr")):
        b.append(f'<circle cx="{tx0 + 20 + i*16}" cy="{ty0 + 18}" r="5" class="{c}" opacity=".85"/>')
    b.append(f'<text x="{tx0 + tw - 16}" y="{ty0 + 22}" text-anchor="end" class="mono mu" font-size="11">rql-query</text>')

    def q(parts):
        return "".join(f'<tspan class="{c}">{t}</tspan>' for c, t in parts)

    line1 = [("ac", "› "), ("tx", "objects "), ("ac", "| "), ("vi", "where "), ("tx", "$ is Enemy and Health &lt; "), ("or", "20")]
    line2 = [("ac", "  | "), ("vi", "sort "), ("tx", "Health "), ("ac", "| "), ("vi", "first "), ("or", "3 "),
             ("ac", "| "), ("vi", "select "), ("tx", "$ref Name Health")]
    fs, cw = 14, 8.43  # monospace advance at 14px (0.602em)
    lx, l1y, l2y = tx0 + 20, ty0 + 62, ty0 + 86
    n1, n2 = 44, 51
    # typewriter: clip rects grow; keyTimes on a shared 10 s loop
    t1a, t1b, t2a, t2b = 0.04, 0.20, 0.21, 0.38
    end = 0.94
    b.append(f'<clipPath id="c1"><rect x="{lx-2}" y="{l1y-16}" height="22" width="0"><animate attributeName="width" '
             f'values="0;0;{n1*cw+4:.0f};{n1*cw+4:.0f}" keyTimes="{kt(0,t1a,t1b,1)}" dur="{D}s" repeatCount="indefinite"/></rect></clipPath>')
    b.append(f'<clipPath id="c2"><rect x="{lx-2}" y="{l2y-16}" height="22" width="0"><animate attributeName="width" '
             f'values="0;0;{n2*cw+4:.0f};{n2*cw+4:.0f}" keyTimes="{kt(0,t2a,t2b,1)}" dur="{D}s" repeatCount="indefinite"/></rect></clipPath>')
    fade = f'<animate attributeName="opacity" values="1;1;0" keyTimes="{kt(0,end,1)}" dur="{D}s" repeatCount="indefinite"/>'
    b.append(f'<g>{fade}<text x="{lx}" y="{l1y}" class="mono" font-size="{fs}" textLength="{n1*cw:.0f}" lengthAdjust="spacing" clip-path="url(#c1)" xml:space="preserve">{q(line1)}</text>'
             f'<text x="{lx}" y="{l2y}" class="mono" font-size="{fs}" textLength="{n2*cw:.0f}" lengthAdjust="spacing" clip-path="url(#c2)" xml:space="preserve">{q(line2)}</text></g>')
    # cursor follows the typing
    cx = [lx, lx, lx + n1*cw, lx, lx + n2*cw, lx + n2*cw]
    cy = [l1y-13, l1y-13, l1y-13, l2y-13, l2y-13, l2y-13]
    ckt = kt(0, t1a, t1b, t2a, t2b, 1)
    b.append(f'<rect width="8" height="17" class="ac" x="{lx}" y="{l1y-13}">'
             f'<animate attributeName="x" values="{";".join(f"{v:.0f}" for v in cx)}" keyTimes="{ckt}" dur="{D}s" repeatCount="indefinite"/>'
             f'<animate attributeName="y" values="{";".join(f"{v:.0f}" for v in cy)}" keyTimes="{ckt}" calcMode="discrete" dur="{D}s" repeatCount="indefinite"/>'
             f'<animate attributeName="opacity" values="1;0;1" dur="1s" repeatCount="indefinite"/></rect>')
    # result rows (real sandbox output)
    rows = [("mu", "3 results"), ("mu", "$ref|Name|Health"),
            ("tx", "Enemy#228|Wraith_0227|0"), ("tx", "Enemy#241|Bandit_0240|0"), ("tx", "Enemy#490|Zombie_0489|0")]
    for i, (c, t) in enumerate(rows):
        a = 0.43 + i * 0.045
        y = ty0 + 124 + i * 23
        parts = t.split("|")
        inner = '<tspan class="ac">|</tspan>'.join(
            (f'<tspan class="gr">{s}</tspan>' if (j == 0 and c == "tx") else s) for j, s in enumerate(parts))
        b.append(f'<text x="{lx}" y="{y}" class="mono {c}" font-size="{fs}" opacity="0">{inner}'
                 f'<animate attributeName="opacity" values="0;0;1;1;0" keyTimes="{kt(0,a,a+0.03,end,1)}" dur="{D}s" repeatCount="indefinite"/></text>')
    return svg(W, H, p, "".join(b), "RQL, Reflection Query Language: a query typed into an agent tool and its compact result rows")


# ---------------------------------------------------------------- pipeline
def pipeline(p):
    W, H, D = 1200, 430, 9.0
    b = [f'<rect class="bg" width="{W}" height="{H}"/>',
         f'<rect class="card bd" x="1" y="1" width="{W-2}" height="{H-2}" rx="18" stroke-width="1.5"/>']
    TY = 214  # track y
    # headings
    for x, t in ((24, "LIVE OBJECT GRAPH"), (340, "PIPELINE (LAZY, ON LIVE OBJECTS)"), (996, "COMPACT ROWS")):
        b.append(f'<text x="{x}" y="38" class="sans mu" font-size="12" font-weight="700" letter-spacing="1.5">{t}</text>')

    def pill(x, y, w, h, label, cls="tx", fs=13, extra=""):
        return (f'<rect x="{x}" y="{y - h/2}" width="{w}" height="{h}" rx="{h/2}" class="card2 bd" stroke-width="1.2"{extra}/>'
                f'<text x="{x + w/2}" y="{y + fs*0.36:.1f}" text-anchor="middle" class="mono {cls}" font-size="{fs}">{label}</text>')

    # graph
    root = (24, TY, 64)
    scenes = [(112, 128, 72), (112, 300, 72)]
    leaf_x, leaf_w = 208, 104
    leaves = [("Enemy", "hp 0", "pk"), ("Npc", "", "gr"), ("Enemy", "hp 0", "pk"), ("Chest", "", "ye"),
              ("Enemy", "hp 90", "pk"), ("Light", "", "ye"), ("Enemy", "hp 0", "pk"), ("Enemy", "hp 12", "pk")]
    leaf_y = [70 + i * 41 for i in range(8)]
    for sx, sy, sw in scenes:
        b.append(f'<path d="M{root[0]+root[2]},{TY} C{root[0]+root[2]+14},{TY} {sx-14},{sy} {sx},{sy}" fill="none" class="smu" stroke-width="1.3" opacity=".6"/>')
    for i, ly in enumerate(leaf_y):
        sx, sy, sw = scenes[i // 4]
        b.append(f'<path d="M{sx+sw},{sy} C{sx+sw+12},{sy} {leaf_x-12},{ly} {leaf_x},{ly}" fill="none" class="smu" stroke-width="1.3" opacity=".6"/>')
    b.append(pill(root[0], TY, root[2], 30, "world", "ac", 13))
    for n, (sx, sy, sw) in enumerate(scenes):
        b.append(pill(sx, sy, sw, 28, f"Scene{n+1}", "tx", 12))
    for (t, hp, c), ly in zip(leaves, leaf_y):
        b.append(f'<rect x="{leaf_x}" y="{ly-13}" width="{leaf_w}" height="26" rx="13" class="card2 bd" stroke-width="1.2"/>'
                 f'<circle cx="{leaf_x+13}" cy="{ly}" r="4.5" class="{c}"/>'
                 f'<text x="{leaf_x+24}" y="{ly+4.3}" class="mono tx" font-size="12">{t}<tspan class="mu"> {hp}</tspan></text>')
    # track + stages
    x0, x1 = 340, 980
    b.append(f'<line x1="{x0}" y1="{TY}" x2="{x1}" y2="{TY}" class="smu" stroke-width="2" stroke-dasharray="2 6" stroke-linecap="round" opacity=".7"/>')
    stages = [(398, "objects", ["source: every", "GameObject"]), (528, "where", ["$ is Enemy and", "Health &lt; 20"]),
              (656, "sort", ["Health"]), (782, "first", ["3"]), (910, "select", ["$ref Name", "Health"])]
    for sx, name, args in stages:
        b.append(f'<rect x="{sx-56}" y="{TY-112}" width="112" height="{50 + 16*len(args)}" rx="10" class="card2 bd" stroke-width="1.2"/>')
        b.append(f'<text x="{sx}" y="{TY-88}" text-anchor="middle" class="mono vi" font-size="15" font-weight="700">{name}</text>')
        for k, a in enumerate(args):
            b.append(f'<text x="{sx}" y="{TY-68 + 16*k}" text-anchor="middle" class="mono mu" font-size="11.5">{a}</text>')
        b.append(f'<line x1="{sx}" y1="{TY-112 + 50 + 16*len(args)}" x2="{sx}" y2="{TY-9}" class="smu" stroke-width="1.2" opacity=".5"/>')
        b.append(f'<circle cx="{sx}" cy="{TY}" r="9" class="card2 sac" stroke-width="2"/>')
    # result table
    rx0, ry0 = 996, 120
    b.append(f'<rect x="{rx0}" y="{ry0}" width="182" height="150" rx="10" class="bg bd" stroke-width="1.2"/>')
    b.append(f'<text x="{rx0+12}" y="{ry0+24}" class="mono mu" font-size="11.5">3 results</text>')
    b.append(f'<text x="{rx0+12}" y="{ry0+46}" class="mono mu" font-size="11.5">$ref|Name|Health</text>')
    out_rows = ["Enemy#228|Wraith_0227|0", "Enemy#241|Bandit_0240|0", "Enemy#490|Zombie_0489|0"]
    row_y = [ry0 + 76 + i * 24 for i in range(3)]
    # dots: (leaf index, where it stops: 'where' | 'first' | row k)
    # dots in source order: (leaf index, fate). Fates: "where" / "first" = dropped there, int = result row k.
    plan = [(0, 0), (1, "where"), (2, 1), (3, "where"), (4, "where"), (5, "where"), (6, 2), (7, "first")]
    sx_of = {name: sx for sx, name, _ in stages}
    speed, end = 520.0, 0.94  # px / s; rows fade out at `end`

    def seg_len(segs):  # segs: ("L", (x, y)) or ("C", c1, c2, (x, y)); cubic length by sampling
        total, cur, out = 0.0, segs[0][1], []
        for sg in segs[1:]:
            if sg[0] == "L":
                total += ((sg[1][0]-cur[0])**2 + (sg[1][1]-cur[1])**2) ** .5
                cur = sg[1]
            else:
                c1, c2, e = sg[1], sg[2], sg[3]
                prev = cur
                for k in range(1, 41):
                    t = k / 40
                    pt = tuple((1-t)**3*cur[i] + 3*(1-t)**2*t*c1[i] + 3*(1-t)*t*t*c2[i] + t**3*e[i] for i in range(2))
                    total += ((pt[0]-prev[0])**2 + (pt[1]-prev[1])**2) ** .5
                    prev = pt
                cur = e
            out.append(total)
        return out

    def d_of(segs):
        r = f"M{segs[0][1][0]},{segs[0][1][1]}"
        for sg in segs[1:]:
            r += f" L{sg[1][0]},{sg[1][1]}" if sg[0] == "L" else " C" + " ".join(f"{x:.0f},{y:.0f}" for x, y in sg[1:])
        return r

    def dot(cls, segs, times, stops):
        """Move along segs; `stops` = segment indices whose end is a waypoint, `times` = arrival/departure times."""
        lens = seg_len(segs)
        L = lens[-1]
        pts = [0.0] + [lens[i] / L for i in stops] + [1.0]
        kp, ktimes = [], []
        for p_, (ta, tb) in zip(pts, times):
            kp += [p_, p_]
            ktimes += [ta, tb]
        kp.append(pts[-1]); ktimes.append(1.0)
        return (f'<circle r="6.5" class="{cls}" opacity="0">'
                f'<animateMotion path="{d_of(segs)}" keyPoints="{";".join(f"{v:.4f}" for v in kp)}" keyTimes="{kt(*ktimes)}" calcMode="linear" dur="{D}s" repeatCount="indefinite"/>'
                f'<animate attributeName="opacity" values="0;0;1;1;0;0" keyTimes="{kt(0,times[0][1],times[0][1]+0.01,times[-1][0]-0.015,times[-1][0],1)}" dur="{D}s" repeatCount="indefinite"/></circle>')

    def drop_label(gx, t):
        return (f'<text x="{gx+14}" y="{TY+48}" class="mono pk" font-size="12" opacity="0">dropped'
                f'<animate attributeName="opacity" values="0;0;1;0;0" keyTimes="{kt(0,t,t+0.02,t+0.07,1)}" dur="{D}s" repeatCount="indefinite"/></text>')

    sort_x, queue_gap = sx_of["sort"], 18
    queued, t_s = [], 0.03
    for li, fate in plan:
        ly, lx = leaf_y[li], leaf_x + leaf_w
        lead = [("M", (lx, ly)), ("C", (lx+18, ly), (x0-18, TY), (x0, TY))]
        lead_len = seg_len(lead)[-1]
        cls = leaves[li][2]
        if fate == "where":
            gx = sx_of["where"]
            segs = lead + [("L", (gx, TY)), ("L", (gx, TY+70))]
            lens = seg_len(segs)
            t_gate = t_s + lens[1] / speed / D
            t_end = t_s + lens[2] / speed / D
            b.append(dot(cls, segs, [(0, t_s), (t_end, t_end)], []))
            b.append(drop_label(gx, t_gate))
        else:
            queued.append((li, fate, cls, lead, lead_len, t_s))
        t_s += 0.045
    # sort buffers every survivor, then releases them in Health order (stable): rows 0..2, then hp 12
    q_arrive = []
    for qi, (li, fate, cls, lead, lead_len, ts) in enumerate(queued):
        qx = sort_x - queue_gap * qi
        q_arrive.append(ts + (lead_len + qx - x0) / speed / D)
    t_release = max(q_arrive) + 0.04
    for qi, (li, fate, cls, lead, lead_len, ts) in enumerate(queued):
        qx = sort_x - queue_gap * qi
        segs = lead + [("L", (qx, TY))]
        if fate == "first":
            gx = sx_of["first"]
            segs += [("L", (gx, TY)), ("L", (gx, TY+70))]
        else:
            gx, ry = sx_of["select"], row_y[fate] - 4
            segs += [("L", (gx, TY)), ("C", (gx+40, TY), (rx0-30, ry), (rx0+4, ry))]
        lens = seg_len(segs)
        tr = t_release + qi * 0.035
        t_end = tr + (lens[-1] - lens[1]) / speed / D
        b.append(dot(cls, segs, [(0, ts), (q_arrive[qi], tr), (t_end, t_end)], [1]))
        if fate == "first":
            b.append(drop_label(sx_of["first"], tr + (lens[2] - lens[1]) / speed / D))
        else:
            parts = out_rows[fate].split("|")
            inner = f'<tspan class="gr">{parts[0]}</tspan><tspan class="ac">|</tspan>{parts[1]}<tspan class="ac">|</tspan>{parts[2]}'
            b.append(f'<text x="{rx0+12}" y="{row_y[fate]}" class="mono tx" font-size="11.5" opacity="0">{inner}'
                     f'<animate attributeName="opacity" values="0;0;1;1;0" keyTimes="{kt(0,t_end-0.005,t_end+0.02,end,1)}" dur="{D}s" repeatCount="indefinite"/></text>')
    assert t_end < end - 0.05, t_end
    # "buffering" hint at sort while it waits for the last item
    b.append(f'<text x="{sort_x}" y="{TY+30}" text-anchor="middle" class="mono ac" font-size="11" opacity="0">buffering'
             f'<animate attributeName="opacity" values="0;0;1;1;0;0" keyTimes="{kt(0,min(q_arrive),min(q_arrive)+0.01,t_release,t_release+0.01,1)}" dur="{D}s" repeatCount="indefinite"/></text>')
    # source pulse on the graph root
    b.append(f'<rect x="{root[0]}" y="{TY-15}" width="{root[2]}" height="30" rx="15" fill="none" class="sac" stroke-width="2" opacity="0">'
             f'<animate attributeName="opacity" values="0;1;0;0" keyTimes="{kt(0,0.03,0.12,1)}" dur="{D}s" repeatCount="indefinite"/></rect>')
    b.append(f'<text x="600" y="{H-30}" text-anchor="middle" class="sans mu" font-size="14">'
             f'Stages stream live objects; <tspan class="tx">sort</tspan> buffers, <tspan class="tx">first 3</tspan> keeps the top three, and only those rows are encoded.</text>')
    return svg(W, H, p, "".join(b), "How an RQL query flows: items stream from a live object graph through where, sort, first and select into compact rows")


# ---------------------------------------------------------------- architecture
def architecture(p):
    W, H, D = 1200, 300, 6.0
    b = [f'<rect class="bg" width="{W}" height="{H}"/>',
         f'<rect class="card bd" x="1" y="1" width="{W-2}" height="{H-2}" rx="18" stroke-width="1.5"/>']
    CY = 150

    def box(x, y, w, h, title, sub, tcls="tx"):
        s = f'<rect x="{x}" y="{y-h/2}" width="{w}" height="{h}" rx="12" class="card2 bd" stroke-width="1.3"/>'
        s += f'<text x="{x+w/2}" y="{y-4 if sub else y+5}" text-anchor="middle" class="sans {tcls}" font-size="15" font-weight="700">{title}</text>'
        if sub:
            s += f'<text x="{x+w/2}" y="{y+16}" text-anchor="middle" class="mono mu" font-size="11.5">{sub}</text>'
        return s

    b.append(box(30, CY, 170, 70, "RQL text", "objects | where …", "ac"))
    b.append(box(260, CY, 170, 70, "Parser", ".NET · TypeScript", "tx"))
    b.append(box(490, CY, 200, 86, "QueryPlan JSON", "versioned, canonical", "vi"))
    b.append(f'<text x="590" y="{CY+66}" text-anchor="middle" class="mono mu" font-size="11">spec/query-plan.schema.json</text>')
    ex = [(CY - 82, ".NET executor", "System.Reflection", "vi"), (CY, "TypeScript executor", "descriptors", "ac"),
          (CY + 82, "C++17 executor", "TypeRegistry, no RTTI", "or")]
    for y, t, s, c in ex:
        b.append(box(760, y, 200, 62, t, s, c))
    b.append(box(1020, CY, 150, 86, "QueryResult", "JSON or rows", "gr"))
    b.append(f'<text x="1095" y="{CY+66}" text-anchor="middle" class="mono mu" font-size="11">surviving items only</text>')
    b.append(f'<text x="600" y="{H-18}" text-anchor="middle" class="sans mu" font-size="13.5">'
             f'One plan, three runtimes, one set of golden vectors. C++ executes plans only (no text parser by design).</text>')
    paths = ["M200,150 L260,150", "M430,150 L490,150"]
    for y, *_ in ex:
        paths.append(f"M690,150 C725,150 725,{y} 760,{y}")
        paths.append(f"M960,{y} C990,{y} 990,150 1020,150")
    for d in paths:
        b.append(f'<path d="{d}" fill="none" class="smu" stroke-width="1.6" opacity=".7"/>')
    # packets
    seq = [("M200,150 L260,150", 0.02, 0.12, "ac"), ("M430,150 L490,150", 0.30, 0.40, "vi")]
    for y, _, _, c in ex:
        seq.append((f"M690,150 C725,150 725,{y} 760,{y}", 0.50, 0.62, "vi"))
        seq.append((f"M960,{y} C990,{y} 990,150 1020,150", 0.72, 0.84, c))
    for d, ts, te, c in seq:
        b.append(f'<circle r="6" class="{c}" opacity="0"><animateMotion path="{d}" keyPoints="0;0;1;1" keyTimes="{kt(0,ts,te,1)}" calcMode="linear" dur="{D}s" repeatCount="indefinite"/>'
                 f'<animate attributeName="opacity" values="0;0;1;1;0;0" keyTimes="{kt(0,ts,ts+0.01,te-0.01,te,1)}" dur="{D}s" repeatCount="indefinite"/></circle>')
    # box glows when a packet arrives
    for (x, y, w, h), t in (((260, CY, 170, 70), 0.12), ((490, CY, 200, 86), 0.40), ((1020, CY, 150, 86), 0.84)):
        b.append(f'<rect x="{x}" y="{y-h/2}" width="{w}" height="{h}" rx="12" fill="none" class="sac" stroke-width="2" opacity="0">'
                 f'<animate attributeName="opacity" values="0;0;1;0;0" keyTimes="{kt(0,t-0.01,t+0.02,t+0.16,1)}" dur="{D}s" repeatCount="indefinite"/></rect>')
    for y, *_ in ex:
        b.append(f'<rect x="760" y="{y-31}" width="200" height="62" rx="12" fill="none" class="sac" stroke-width="2" opacity="0">'
                 f'<animate attributeName="opacity" values="0;0;1;0;0" keyTimes="{kt(0,0.61,0.64,0.76,1)}" dur="{D}s" repeatCount="indefinite"/></rect>')
    return svg(W, H, p, "".join(b), "RQL architecture: text is parsed into a canonical QueryPlan that .NET, TypeScript and C++ executors run against live objects")


# ---------------------------------------------------------------- social preview (static, rendered to PNG)
def social():
    p = PALETTES["dark"]
    W, H = 1280, 640
    b = [f'<rect width="{W}" height="{H}" fill="{p["bg"]}"/>']
    # faint graph in the corners
    nodes = [(90, 90), (190, 150), (120, 250), (1120, 400), (1190, 500), (1060, 540), (1180, 90), (1100, 160)]
    for a, c in ((0, 1), (1, 2), (0, 2), (3, 4), (3, 5), (6, 7)):
        (x1, y1), (x2, y2) = nodes[a], nodes[c]
        b.append(f'<line x1="{x1}" y1="{y1}" x2="{x2}" y2="{y2}" stroke="{p["mu"]}" stroke-width="2" opacity=".25"/>')
    for x, y in nodes:
        b.append(f'<circle cx="{x}" cy="{y}" r="7" fill="{p["ac"]}" opacity=".45"/>')
    b.append(f'<text x="640" y="250" text-anchor="middle" class="sans" font-size="150" font-weight="800" fill="url(#g)" letter-spacing="-3">RQL</text>')
    b.append(f'<text x="640" y="318" text-anchor="middle" class="sans tx" font-size="44" font-weight="600">Reflection Query Language</text>')
    b.append(f'<text x="640" y="368" text-anchor="middle" class="sans mu" font-size="26">Query and safely change live object graphs from AI agents</text>')
    # query card
    b.append(f'<rect x="200" y="404" width="880" height="64" rx="14" fill="{p["card"]}" stroke="{p["bd"]}" stroke-width="2"/>')
    b.append(f'<text x="640" y="445" text-anchor="middle" class="mono" font-size="23" xml:space="preserve">'
             f'<tspan class="tx">objects </tspan><tspan class="ac">| </tspan><tspan class="vi">where </tspan><tspan class="tx">$ is Enemy and Health &lt; </tspan><tspan class="or">20 </tspan>'
             f'<tspan class="ac">| </tspan><tspan class="vi">first </tspan><tspan class="or">3</tspan></text>')
    x = 640 - (3 * 170 + 2 * 16) / 2
    for label, cls in ((".NET", "vi"), ("TypeScript", "ac"), ("C++17", "or")):
        b.append(f'<rect x="{x:.0f}" y="500" width="170" height="44" rx="22" fill="{p["card2"]}" stroke="{p["bd"]}" stroke-width="2"/>'
                 f'<text x="{x+85:.0f}" y="530" text-anchor="middle" class="mono {cls}" font-size="21" font-weight="700">{label}</text>')
        x += 186
    b.append(f'<text x="640" y="600" text-anchor="middle" class="sans mu" font-size="20">github.com/IvanMurzak/RQL  ·  ai-game.dev</text>')
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W} {H}" width="{W}" height="{H}">{style(p)}'
            f'<defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="{p["g1"]}"/>'
            f'<stop offset="1" stop-color="{p["g2"]}"/></linearGradient></defs>{"".join(b)}</svg>')


os.makedirs(OUT, exist_ok=True)
for name, fn in (("hero", hero), ("pipeline", pipeline), ("architecture", architecture)):
    for theme, pal in PALETTES.items():
        with open(os.path.join(OUT, f"{name}-{theme}.svg"), "w", encoding="utf-8", newline="\n") as f:
            f.write(fn(pal) + "\n")
with open(os.path.join(OUT, "social-preview.svg"), "w", encoding="utf-8", newline="\n") as f:
    f.write(social() + "\n")
print("ok")
